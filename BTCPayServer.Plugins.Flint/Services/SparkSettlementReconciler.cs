using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>One store to reconcile, and the live SDK handle to reconcile it against.</summary>
public sealed record SparkReconciliationTarget(string StoreId, ISparkSdkClient Sdk);

/// <summary>
/// The one place a Spark receive is turned into a settled BTCPay invoice.
/// </summary>
/// <remarks>
/// <para>
/// Three callers converge here, and they must agree: the SDK event consumer, BTCPay's
/// <c>GetInvoice</c> lookup, and the plugin's own reconciliation task. Sharing the code is what makes
/// "exactly one caller notifies BTCPay" true rather than aspirational — the guarantee lives in
/// <see cref="IInvoiceRecordStore.SettleAsync"/>'s compare-and-set, and every path has to go through it.
/// </para>
/// <para>
/// <b>Why a reconciliation task exists at all.</b> Settlement cannot rely on the SDK's events: a completed
/// receive has been observed emitting only <c>PaymentPending</c> and never <c>PaymentSucceeded</c>, with the
/// completion visible solely from a later storage read. Nor can it rely on BTCPay re-polling. BTCPay calls
/// <c>GetInvoice</c> once per invoice when the invoice is created or activated, and once per invoice when a
/// listening session starts (<c>LightningInstanceListener.PollAllListenedInvoices</c>); its one-minute
/// <c>_ListenPoller</c> timer only calls <c>CheckConnections()</c>, which expires stale entries and restarts
/// a <em>dead</em> session — it polls no invoices. Since <c>WaitInvoice</c> here awaits a channel and never
/// faults, the session never dies and never re-polls. Without this task, a dropped completion event means an
/// invoice that expires unpaid while the sats sit in the merchant's wallet.
/// </para>
/// <para>
/// <b>A quiet store would pay for its own scan, so a pass shares one across an invoice page.</b> Each
/// invoice with no recorded SDK payment id costs up to ten pages of payment history per pass, every
/// minute, and a store with five waiting invoices and no incoming traffic pays that five times over to
/// find nothing. So the pass runs one scan for the whole page — anchored to its oldest unpaid invoice and
/// indexed by payment hash — and settles records straight out of the index. What the index does not name
/// counts as unpaid <em>only</em> when the scan saw a short page: ten full pages prove only that more
/// history was there to read, not that the payment is absent, so on a capped or failed sweep every miss
/// falls back to the per-invoice scan that ran before the batching existed. Coverage is unchanged; only
/// the cost is shared.
/// </para>
/// <para>
/// <b>Settling is not the same as crediting, and both happen here.</b> Notifying BTCPay's listener only works
/// while that listener is watching the BOLT11 in question, and it watches only each invoice's <em>current</em>
/// payment prompt — so a superseded BOLT11 paid after a restart settles here and reaches no BTCPay invoice.
/// Every settlement is therefore also routed to the invoice its BOLT11 was minted for, through
/// <see cref="SparkInvoiceCreditor"/>, and retried by <see cref="CreditStoreAsync"/> until it lands. The two
/// paths cannot double-credit: they collide on BTCPay's own payments primary key.
/// </para>
/// </remarks>
public class SparkSettlementReconciler
{
    /// <summary>
    /// How far before an invoice's creation time the payment scan starts. Absorbs clock skew between this
    /// server and the service provider without widening the scan much.
    /// </summary>
    private static readonly TimeSpan ReconciliationSlack = TimeSpan.FromMinutes(10);

    /// <summary>Payments fetched per page when scanning for a settled receive.</summary>
    private const int PaymentPageSize = 50;

    /// <summary>
    /// Most pages the scan will walk before giving up on one invoice, bounding it at 500 payments.
    /// </summary>
    /// <remarks>
    /// A single page is not enough: the scan is newest-first, so on a busy store the target receive is
    /// pushed off page one by later payments and would never be found. It still has to be bounded, because
    /// this runs per pending invoice on a timer.
    /// </remarks>
    private const int MaxPaymentPages = 10;

    /// <summary>Invoices fetched per page while walking a store's set.</summary>
    private const int InvoicePageSize = 100;

    /// <summary>Invoices examined per store per reconciliation pass, across all pages.</summary>
    private const int MaxInvoicesPerPass = 1000;

    /// <summary>
    /// How long after expiry an invoice is still worth re-checking.
    /// </summary>
    /// <remarks>
    /// The service provider accepts a payment after expiry and Spark cannot stop it, so a just-expired invoice
    /// can still take real money. Recording it keeps this plugin's own ledger truthful even when BTCPay has
    /// already stopped listening for that invoice — the alternative is sats in the wallet that nothing accounts
    /// for. Bounded because the window is otherwise unbounded work on every pass, forever.
    /// </remarks>
    private static readonly TimeSpan ExpiredReconciliationGrace = TimeSpan.FromHours(1);

    /// <summary>
    /// Settled-but-uncredited invoices whose credit is retried per store per pass.
    /// </summary>
    /// <remarks>
    /// Far smaller than <see cref="MaxInvoicesPerPass"/> because the set is normally empty: the credit is
    /// attempted the moment a settlement is recorded, and only lands here when BTCPay's own row was not
    /// written yet, when the process died in between, or when the settlement predates the column. A backlog
    /// larger than this on one store is drained over consecutive passes, oldest first, which is the right
    /// order — the oldest is the closest to its retry horizon.
    /// </remarks>
    private const int MaxCreditsPerPass = 200;

    /// <summary>
    /// Stores the credit walk will pick up per pass from the record store rather than from a live wallet.
    /// </summary>
    /// <remarks>
    /// Reaching this cap is pathological: it would mean hundreds of stores each holding money that has not
    /// reached a BTCPay invoice. It exists so the query cannot become unbounded, and the pass says so in the log
    /// rather than pretending it walked them all.
    /// </remarks>
    private const int MaxCreditStoresPerPass = 500;

    private readonly IInvoiceRecordStore _invoiceStore;
    private readonly SparkSettlementBroadcaster _broadcaster;
    private readonly SparkInvoiceCreditor _creditor;
    private readonly ILogger<SparkSettlementReconciler> _logger;

    /// <summary>
    /// Where the last capped reconciliation pass stopped, per store, so the next pass resumes rather than
    /// re-examining the same head of the walk — see the note at the cursor's initialisation.
    /// </summary>
    private readonly ConcurrentDictionary<string, InvoiceSettlementCursor> _resumeCursors = new();

    public SparkSettlementReconciler(
        IInvoiceRecordStore invoiceStore,
        SparkSettlementBroadcaster broadcaster,
        SparkInvoiceCreditor creditor,
        ILogger<SparkSettlementReconciler> logger)
    {
        _invoiceStore = invoiceStore;
        _broadcaster = broadcaster;
        _creditor = creditor;
        _logger = logger;
    }

    /// <summary>
    /// Applies a completed inbound payment to its invoice, notifying listeners if this call is the one that
    /// settled it.
    /// </summary>
    public async Task<InvoiceSettlementResult> ApplyAsync(
        string storeId,
        SparkPayment payment,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        ArgumentNullException.ThrowIfNull(payment);

        if (payment.PaymentHash is not { } paymentHash)
            throw new ArgumentException("A settled receive must have a payment hash.", nameof(payment));
        if (payment.Direction is not SparkPaymentDirection.Receive)
            throw new ArgumentException("Only an inbound payment can settle an invoice.", nameof(payment));

        var result = await _invoiceStore.SettleAsync(
                storeId,
                paymentHash,
                payment.SdkPaymentId,
                payment.AmountMsat,
                payment.Preimage,
                payment.Timestamp,
                cancellationToken)
            .ConfigureAwait(false);

        switch (result.Outcome)
        {
            case InvoiceSettlementOutcome.Settled when result.Record is not null:
                // Audit finding PaymentFlow F3: nothing here compares what arrived to what was invoiced, and the
                // record settles once and never revises upward. That is safe because the Spark operators enforce
                // the floor, not this plugin. For a fixed-amount BOLT11 they take the amount from the invoice the
                // receiver stored with its preimage shares — not from the service provider's claim — and refuse
                // to complete the swap when the leaves transferred are worth less (spark
                // so/handler/lightning_handler.go, "invalid amount, expected: %d or more"). Every invoice this
                // plugin mints carries an amount. So a receive below the invoiced amount means that invariant
                // broke, and the operator has to hear about it. It is still a warning and not a refusal, because
                // refusing would stop legitimate invoices settling if the SDK ever reported an amount a hair low.
                // Core accounts the received millisatoshis, so the BTCPay invoice reads PaidPartial, not Paid.
                if (result.Record.AmountMsat is { } invoiced && payment.AmountMsat < invoiced)
                {
                    _logger.LogWarning(
                        "Store {StoreId}: Lightning invoice {PaymentHash} settled with {ReceivedSats} sat but "
                        + "asked for {InvoicedSats} sat. Spark should have refused a short payment; the BTCPay "
                        + "invoice will show it as partially paid. Check the Spark balance against this invoice "
                        + "before fulfilling it",
                        storeId, paymentHash, payment.AmountSats, invoiced / 1000);
                }
                else
                {
                    _logger.LogInformation(
                        "Store {StoreId}: Lightning invoice {PaymentHash} settled with {AmountSats} sat",
                        storeId, paymentHash, payment.AmountSats);
                }

                // Published only on the transition, so a duplicate event or a poll racing the event does not
                // wake every listening session twice.
                _broadcaster.Publish(ToSettlement(result.Record));

                // And routed to the BTCPay invoice the BOLT11 was minted for, which the publish above cannot
                // be relied on to achieve: BTCPay's listener only watches each invoice's current prompt, so a
                // superseded BOLT11 paid after a restart has nothing listening for it. Independent of the
                // publish rather than an alternative to it — the two collide on BTCPay's payments primary key
                // and exactly one of them records the money. Ordered after, and unable to throw, so a BTCPay
                // database that is briefly unreachable cannot unwind a settlement that is already committed.
                await CreditAsync(result.Record, cancellationToken).ConfigureAwait(false);
                break;

            case InvoiceSettlementOutcome.AlreadySettled:
                // Ordinarily a replay of the receive that settled it. A *different* completed receive for the
                // same hash is the money this path would otherwise swallow: it cannot be credited, because the
                // BTCPay payment row is keyed on the payment hash and the first receive holds that key. Spark's
                // operators allow one completed receive per (payment hash, receiver) — a unique index on their
                // preimage requests — so this is a tripwire for that invariant, not a flow: loud, so the sats
                // that reached the wallet are found and placed by hand.
                if (result.Record is { SdkPaymentId: { } settledBy }
                    && !string.Equals(settledBy, payment.SdkPaymentId, StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "Store {StoreId}: Lightning invoice {PaymentHash} received a second completed payment "
                        + "{SdkPaymentId} of {AmountSats} sat after {SettledBy} settled it with {SettledSats} sat. "
                        + "Spark should not complete two receives for one invoice. That payment is in the Spark "
                        + "wallet and on no BTCPay invoice; place it by hand",
                        storeId, paymentHash, payment.SdkPaymentId, payment.AmountSats, settledBy,
                        (result.Record.AmountReceivedMsat ?? 0) / 1000);
                }
                else
                {
                    _logger.LogDebug(
                        "Store {StoreId}: Lightning invoice {PaymentHash} was already settled",
                        storeId, paymentHash);
                }

                // A duplicate event is the cheapest retry there is: if the credit for this settlement has not
                // landed yet, attempt it again here rather than waiting for the next pass.
                if (result.Record is { CreditedAt: null, CreditAbandonedAt: null })
                    await CreditAsync(result.Record, cancellationToken).ConfigureAwait(false);
                break;

            default:
                _logger.LogWarning(
                    "Store {StoreId}: received {AmountSats} sat for payment hash {PaymentHash}, which this "
                    + "plugin has no record of creating. The funds are in the Spark balance",
                    storeId, payment.AmountSats, paymentHash);
                break;
        }

        return result;
    }

    /// <summary>
    /// Finds the completed inbound payment for an invoice, or null if there is not one yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A point lookup when the SDK's payment id is known — which the pending-event path records — and a bounded,
    /// paged, newest-first scan otherwise, because <c>GetPayment</c> is not addressable by payment hash. A point
    /// lookup that comes back empty <b>falls through to the scan</b> rather than concluding anything: the
    /// recorded id came from a <c>PaymentPending</c> event, and if the SDK has since replaced or re-keyed that
    /// row, treating the miss as "not paid" would make the invoice permanently unresolvable.
    /// </para>
    /// <para>
    /// Never throws. An unreachable service provider means "not settled yet", which the next pass retries;
    /// an exception here would abort a whole reconciliation pass or fail a BTCPay lookup. Every SDK call is
    /// bounded by a deadline for the same reason.
    /// </para>
    /// </remarks>
    public async Task<SparkPayment?> FindReceiveAsync(
        ISparkSdkClient sdk,
        InvoiceRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sdk);
        ArgumentNullException.ThrowIfNull(record);

        try
        {
            if (record.SdkPaymentId is { } sdkPaymentId)
            {
                var byId = await SparkDeadline.OrNullAsync(
                        sdk.GetPaymentAsync(sdkPaymentId, cancellationToken),
                        Constants.SdkCallDeadline,
                        () => _logger.LogWarning(
                            "Store {StoreId}: looking up Spark payment {SdkPaymentId} exceeded {Seconds}s",
                            record.StoreId, sdkPaymentId, Constants.SdkCallDeadline.TotalSeconds),
                        cancellationToken)
                    .ConfigureAwait(false);

                // Direction is re-checked because a point lookup cannot filter on it, and a self-payment
                // produces a Receive leg and a Send leg sharing one payment hash whose amounts differ by the
                // routing fee.
                if (byId is { Direction: SparkPaymentDirection.Receive })
                    return byId;

                _logger.LogDebug(
                    "Store {StoreId}: the recorded Spark payment id for invoice {PaymentHash} resolved to "
                    + "nothing usable; falling back to a history scan",
                    record.StoreId, record.PaymentHash);
            }

            for (var page = 0; page < MaxPaymentPages; page++)
            {
                var payments = await SparkDeadline.OrNullAsync(
                        sdk.ListPaymentsAsync(
                            new SparkListPaymentsQuery(
                                SparkPaymentDirection.Receive,
                                CompletedOnly: true,
                                // Anchored to the invoice's own creation time so this stays a small scan however
                                // long the wallet's history gets.
                                From: record.CreatedAt - ReconciliationSlack,
                                Offset: page * PaymentPageSize,
                                Limit: PaymentPageSize),
                            cancellationToken),
                        Constants.SdkCallDeadline,
                        () => _logger.LogWarning(
                            "Store {StoreId}: scanning Spark payments for invoice {PaymentHash} exceeded "
                            + "{Seconds}s",
                            record.StoreId, record.PaymentHash, Constants.SdkCallDeadline.TotalSeconds),
                        cancellationToken)
                    .ConfigureAwait(false);

                // Null means the deadline passed, not that there is nothing there. Give up on this invoice for
                // this pass rather than paging on against a service that is not answering.
                if (payments is null)
                    return null;

                var match = payments.FirstOrDefault(p =>
                    p.PaymentHash == record.PaymentHash && p.Direction is SparkPaymentDirection.Receive);
                if (match is not null)
                    return match;

                // A short page is the end of the window.
                if (payments.Count < PaymentPageSize)
                    return null;
            }

            _logger.LogWarning(
                "Store {StoreId}: gave up looking for a payment matching invoice {PaymentHash} after {Count} "
                + "payments. If this repeats, the wallet's history has outgrown the scan window",
                record.StoreId, record.PaymentHash, MaxPaymentPages * PaymentPageSize);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Store {StoreId}: could not check the Spark service for invoice {PaymentHash} ({Reason})",
                record.StoreId, record.PaymentHash, SparkErrors.Describe(ex));
            return null;
        }
    }

    /// <summary>
    /// Resolves one invoice against the SDK and settles it if it has been paid. Returns the current record.
    /// </summary>
    public Task<InvoiceRecord> ResolveAsync(
        ISparkSdkClient sdk,
        InvoiceRecord record,
        CancellationToken cancellationToken = default)
        => ResolveAsync(sdk, record, sweep: null, cancellationToken);

    /// <summary>
    /// The batched form of <see cref="ResolveAsync(ISparkSdkClient,InvoiceRecord,CancellationToken)"/>: one
    /// invoice resolved against a payment sweep already run for its page.
    /// </summary>
    /// <remarks>
    /// <paramref name="sweep"/> is the only difference, and it only ever spends less: a record the index
    /// names settles exactly as one a scan would have found, and a record it does not name is passed over
    /// only when the sweep drained. A null sweep — every caller outside the store walk, and every batch the
    /// sweep declined or failed to run — is the pre-batching path, unchanged.
    /// </remarks>
    internal async Task<InvoiceRecord> ResolveAsync(
        ISparkSdkClient sdk,
        InvoiceRecord record,
        ReceiveSweep? sweep,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        // Paid is terminal. A cancelled invoice is still scanned: on Spark it remains payable, so a late
        // payment must be found and credited here (see InvoiceRecord.EffectiveStatus).
        if (record.Status is InvoiceRecordStatus.Paid)
            return record;

        SparkPayment? payment;
        if (record.SdkPaymentId is null && sweep is { } shared)
        {
            if (shared.Index.TryGetValue(record.PaymentHash, out var swept))
            {
                payment = swept;
            }
            else if (shared.Drained)
            {
                // The shared scan walked its window to the end without naming this hash — the same evidence
                // a per-invoice scan stopping at a short page used to produce, bought with one query for the
                // whole page instead of one per invoice. Absent Drained this would be the only way batching
                // can lose a settlement, which is why Drained exists.
                return record;
            }
            else
            {
                payment = await FindReceiveAsync(sdk, record, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            // A recorded id still needs its point lookup — the shared scan neither covers GetPayment nor is
            // run for a page of such records — and a record without a sweep gets exactly the scan it always
            // got.
            payment = await FindReceiveAsync(sdk, record, cancellationToken).ConfigureAwait(false);
        }

        if (payment is not { Status: SparkPaymentStatus.Completed, PaymentHash: not null })
            return record;

        var result = await ApplyAsync(record.StoreId, payment, cancellationToken).ConfigureAwait(false);
        return result.Record ?? record;
    }

    /// <summary>
    /// The result of one shared history scan for a page of invoices: every completed receive it saw, keyed
    /// by payment hash, and whether it reached the end of the window.
    /// </summary>
    /// <remarks>
    /// <c>Drained</c> carries the whole epistemic weight. "Not in the index" means "not paid" only if the
    /// scan drained its window; a run of full pages stopped by the cap proves only that more history was
    /// there. Settling from <see cref="Index"/> is safe either way — the hash matched — but treating a miss
    /// as absence is not, and that miss is the only thing a non-drained sweep could otherwise be used for.
    /// </remarks>
    internal sealed record ReceiveSweep(Dictionary<string, SparkPayment> Index, bool Drained);

    /// <summary>
    /// Scans the payment history once for a whole page of invoices, or returns null when there is nothing
    /// for the scan to serve or the scan itself failed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Anchored to the oldest invoice on the page still needing a scan, so the shared window contains every
    /// per-invoice window it replaces; the paging, the deadline and the cap are the ones
    /// <see cref="FindReceiveAsync"/> would have paid per record, paid once instead.
    /// </para>
    /// <para>
    /// A failure is a null, not a throw: the sweep is a cost optimisation layered over the old path, and
    /// losing it must cost the pass its batching and not its invoices. A null sweep sends every record down
    /// exactly the route it took before the sweep existed.
    /// </para>
    /// </remarks>
    private async Task<ReceiveSweep?> BuildReceiveSweepAsync(
        string storeId,
        ISparkSdkClient sdk,
        IReadOnlyList<InvoiceRecord> page,
        CancellationToken cancellationToken)
    {
        try
        {
            // Only a record with no id to look up by needs the scan; running one for a page where every
            // record has an id would spend the shared query to serve nobody.
            DateTimeOffset? anchor = null;
            var awaiting = 0;
            foreach (var record in page)
            {
                if (record.SdkPaymentId is not null)
                    continue;
                awaiting++;
                if (anchor is null || record.CreatedAt < anchor)
                    anchor = record.CreatedAt;
            }

            if (anchor is null)
                return null;

            var index = new Dictionary<string, SparkPayment>(StringComparer.Ordinal);
            for (var pageIndex = 0; pageIndex < MaxPaymentPages; pageIndex++)
            {
                var payments = await SparkDeadline.OrNullAsync(
                        sdk.ListPaymentsAsync(
                            new SparkListPaymentsQuery(
                                SparkPaymentDirection.Receive,
                                CompletedOnly: true,
                                // Anchored to the oldest invoice still waiting, so the shared window holds
                                // every window the per-invoice scans would each have walked.
                                From: anchor.Value - ReconciliationSlack,
                                Offset: pageIndex * PaymentPageSize,
                                Limit: PaymentPageSize),
                            cancellationToken),
                        Constants.SdkCallDeadline,
                        () => _logger.LogWarning(
                            "Store {StoreId}: the shared Spark payment scan for {Invoices} invoice(s) "
                            + "exceeded {Seconds}s",
                            storeId, awaiting, Constants.SdkCallDeadline.TotalSeconds),
                        cancellationToken)
                    .ConfigureAwait(false);

                // Null means the deadline passed. Whatever this index holds, what it omits now proves
                // nothing, so no invoice may be concluded unpaid from it.
                if (payments is null)
                    return new ReceiveSweep(index, Drained: false);

                foreach (var payment in payments)
                {
                    // The same shape the per-invoice scan matched on: a hashless row or a send leg settles
                    // nothing. First writer under a hash wins, because paging walks newest-first and that is
                    // the row a per-invoice scan's FirstOrDefault would have returned.
                    if (payment.PaymentHash is not { } hash
                        || payment.Direction is not SparkPaymentDirection.Receive)
                    {
                        continue;
                    }

                    index.TryAdd(hash, payment);
                }

                // A short page is the end of the window, and the only thing that lets a miss in this index
                // stand for "not paid".
                if (payments.Count < PaymentPageSize)
                    return new ReceiveSweep(index, Drained: true);
            }

            _logger.LogWarning(
                "Store {StoreId}: the shared scan of Spark payments for {Invoices} invoice(s) hit the "
                + "{Count}-payment cap without draining the window; anything it did not name falls back to "
                + "its own scan. If this repeats, the wallet's history has outgrown the scan window",
                storeId, awaiting, MaxPaymentPages * PaymentPageSize);
            return new ReceiveSweep(index, Drained: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Store {StoreId}: the shared Spark payment scan failed ({Reason}); this pass reconciles "
                + "invoice by invoice, as it did before the scan was shared",
                storeId, SparkErrors.Describe(ex));
            return null;
        }
    }

    /// <summary>
    /// Reconciles every supplied store, isolating each one's failures from the others.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lives here rather than in <c>SparkService</c> so the isolation guarantee is testable: the service's
    /// dependencies (a store repository, an event aggregator, a network provider) need a BTCPay host to
    /// construct, and "one store's failure must not skip the others" is exactly the sort of property that
    /// quietly stops holding.
    /// </para>
    /// <para>
    /// The walk itself belongs to <paramref name="pass"/>, which supplies the pass budget, the per-store
    /// deadline and the rotation. It is a parameter rather than something built here because the rotation
    /// position has to outlive a pass, and because the reconciliation task and the startup catch-up are the same
    /// walk over the same stores and should share one position rather than each starting from the top.
    /// </para>
    /// <para>
    /// <b>The set of stores is wider than the set of live wallets, and has to be.</b> Settling needs an SDK
    /// handle; crediting does not — it touches only BTCPay's tables and this plugin's rows. A store whose Spark
    /// connection is broken (a rotated key, a service-provider outage, a wallet reconfigured away) has no live
    /// handle and so appears in no target, and while the credit walk was reachable only through a target's visit
    /// such a store never retried the settlements it had already received: real money, already in the merchant's
    /// wallet, that no BTCPay invoice recorded. So the pass walks the union of the supplied targets and the
    /// stores the record store says are still awaiting a credit, and a store in the second set but not the first
    /// gets the credit walk alone.
    /// </para>
    /// </remarks>
    public async Task<int> ReconcileStoresAsync(
        IEnumerable<SparkReconciliationTarget> targets,
        SparkStorePassScheduler pass,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(pass);

        // Indexed by store id because the scheduler walks ids, not targets — it owns the rotation and has to be
        // able to name where it got to. A repeated store id would collapse here rather than being reconciled
        // twice; that cannot arise from the only caller, which projects SparkService._instances, and reconciling
        // one store twice in a pass would be wasted work rather than a correctness problem in any case.
        var byStore = new Dictionary<string, ISparkSdkClient>(StringComparer.Ordinal);
        foreach (var target in targets)
            byStore[target.StoreId] = target.Sdk;

        var allStores = new HashSet<string>(byStore.Keys, StringComparer.Ordinal);
        allStores.UnionWith(await ListStoresAwaitingCreditAsync(cancellationToken).ConfigureAwait(false));

        // Interlocked rather than a plain increment: a store whose visit outlived its deadline is still running
        // when this method returns, and it must not race the read below or corrupt a later pass's count. Its
        // settlements are simply not counted here, which is honest — nothing waited to find out.
        var settled = 0;

        await pass.RunAsync(
                allStores,
                async (storeId, token) =>
                {
                    if (!byStore.TryGetValue(storeId, out var sdk))
                    {
                        // No live wallet, so nothing to settle against — but the credit walk needs none.
                        await CreditStoreSafelyAsync(storeId, token).ConfigureAwait(false);
                        return;
                    }

                    var count = await ReconcileStoreAsync(storeId, sdk, token).ConfigureAwait(false);
                    Interlocked.Add(ref settled, count);
                },
                (storeId, ex) =>
                {
                    if (ex is ObjectDisposedException)
                    {
                        // The store was reconfigured or removed mid-pass; the next pass picks up its replacement.
                        _logger.LogDebug(
                            "Store {StoreId}: Spark wallet went away during reconciliation", storeId);
                        return;
                    }

                    _logger.LogError(ex, "Store {StoreId}: Spark reconciliation failed", storeId);
                },
                cancellationToken)
            .ConfigureAwait(false);

        return Volatile.Read(ref settled);
    }

    /// <summary>
    /// Walks a store's still-settleable invoices, soonest-expiring first, and settles any that have been paid.
    /// Returns the number settled by this pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pages by keyset to the end of the set rather than taking one page, so no invoice starves behind a
    /// sustained backlog. Bounded overall by <see cref="MaxInvoicesPerPass"/>, and the log says so honestly when
    /// the bound is what stopped the walk.
    /// </para>
    /// <para>
    /// <b>The credit walk runs first, and independently.</b> It used to run last and inside the settlement
    /// walk's control flow, which gave it two ways to be skipped entirely: a failure loading the settlement page
    /// returned before reaching it, and a store with a sustained settlement backlog could burn the whole
    /// per-store deadline before reaching it — every pass, forever, on exactly the store most likely to have
    /// uncredited money. It is now ordered first (it is bounded, cheap, and makes no SDK call, so it cannot
    /// starve the settlement walk in return) and wrapped in its own error boundary, so neither walk's failure
    /// can cost the other its turn. Ordering it first loses nothing: a settlement recorded later in this same
    /// pass has its credit attempted inline on the settlement path.
    /// </para>
    /// </remarks>
    public async Task<int> ReconcileStoreAsync(
        string storeId,
        ISparkSdkClient sdk,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        ArgumentNullException.ThrowIfNull(sdk);

        // Settling is only half of it: a settlement whose credit never reached the merchant's BTCPay invoice has
        // to be retried, and this is the pass that does it. First, so a backlogged settlement walk cannot starve
        // it out of the store's deadline slice.
        await CreditStoreSafelyAsync(storeId, cancellationToken).ConfigureAwait(false);

        // Recently expired invoices are included on purpose. The service provider accepts a late payment and
        // Spark has no way to withdraw an invoice from it, so an invoice that expired moments ago can still
        // receive real money. Excluding it would leave that money unattributed in this plugin's own records.
        var settleableFrom = DateTimeOffset.UtcNow - ExpiredReconciliationGrace;

        var settled = 0;
        var examined = 0;
        // Resumed from where the previous pass stopped, not from the top. The per-pass cap exists to bound a
        // pass's work, but restarting at the soonest-expiring invoice every pass would make it a starvation
        // line: with more settleable invoices than the cap, the same soonest set is re-examined forever
        // and everything behind it is never reached. The cursor carries across passes and resets only
        // when a pass drains the set, so every invoice is reached within a bounded number of passes.
        InvoiceSettlementCursor? cursor = _resumeCursors.TryGetValue(storeId, out var resume) ? resume : null;

        while (examined < MaxInvoicesPerPass)
        {
            var pageSize = Math.Min(InvoicePageSize, MaxInvoicesPerPass - examined);

            IReadOnlyList<InvoiceRecord> page;
            try
            {
                page = await _invoiceStore
                    .ListForReconciliationAsync(storeId, settleableFrom, cursor, pageSize, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Store {StoreId}: could not load Spark invoices to reconcile", storeId);
                return settled;
            }

            if (page.Count == 0)
                break;

            // One history scan for the whole page rather than one per unpaid invoice: this walk used to
            // spend up to ten pages of SDK paging per record lacking an id, every minute, mostly to prove
            // nobody had paid. A null sweep — nothing to share, or the sweep itself failed — leaves every
            // record on the pre-batching path.
            var sweep = await BuildReceiveSweepAsync(storeId, sdk, page, cancellationToken)
                .ConfigureAwait(false);

            foreach (var record in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                examined++;
                // Advanced before the attempt, so a record that keeps failing cannot stall the walk on itself.
                // Carries the record's expiry, not its creation time: the walk pages by expiry, and the keyset
                // only resumes correctly on the columns the ordering uses.
                cursor = new InvoiceSettlementCursor(record.ExpiresAt, record.PaymentHash);

                try
                {
                    var resolved = await ResolveAsync(sdk, record, sweep, cancellationToken).ConfigureAwait(false);
                    if (resolved.Status is InvoiceRecordStatus.Paid)
                        settled++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One bad invoice must not abandon the rest of the store's set.
                    _logger.LogWarning(ex,
                        "Store {StoreId}: could not reconcile invoice {PaymentHash}", storeId, record.PaymentHash);
                }
            }

            // A short page is the end of the set.
            if (page.Count < pageSize)
                break;
        }

        if (examined >= MaxInvoicesPerPass && cursor is { } stopped)
        {
            // The next pass resumes here rather than restarting at the soonest-expiring invoice, which is what
            // makes the sentence below true rather than a starvation line.
            _resumeCursors[storeId] = stopped;
            _logger.LogInformation(
                "Store {StoreId}: reconciliation examined its per-pass limit of {Count} Spark invoices; the "
                + "next pass resumes where this one stopped",
                storeId, MaxInvoicesPerPass);
        }
        else
        {
            // The set drained; the next pass starts from the top again. One fairness note on the shape the
            // resume changed: under expiry order a newly minted short-TTL invoice can sort behind a cursor
            // parked on a long-dated one and wait until the set drains to be reached — bounded by the same
            // per-pass cap, and backstop-only, since a settlement normally arrives on its own event.
            _resumeCursors.TryRemove(storeId, out _);
        }

        if (settled > 0)
        {
            _logger.LogInformation(
                "Store {StoreId}: reconciliation settled {Settled} Lightning invoice(s) whose settlement event "
                + "had been missed",
                storeId, settled);
        }

        return settled;
    }

    /// <summary>
    /// Runs a store's credit walk without letting its failure reach the settlement walk that follows.
    /// </summary>
    /// <remarks>
    /// An error boundary of its own rather than a shared <c>try</c>, because the two walks answer different
    /// questions and neither is a precondition of the other: a store whose invoice listing is failing must still
    /// have the money it already received routed onto BTCPay's invoices, and a store whose BTCPay database is
    /// briefly unreachable must still have its unpaid invoices re-checked against the service provider.
    /// </remarks>
    private async Task CreditStoreSafelyAsync(string storeId, CancellationToken cancellationToken)
    {
        try
        {
            await CreditStoreAsync(storeId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Store {StoreId}: the Spark credit pass failed; the settlements it covers stay recorded and "
                + "uncredited, and the next pass retries them",
                storeId);
        }
    }

    /// <summary>
    /// The stores the record store says are still awaiting a credit, or none if it cannot be asked.
    /// </summary>
    /// <remarks>
    /// A failure here must not abort the pass: the targets are still walkable, and losing the credit-only stores
    /// for one pass costs a retry rather than a settlement.
    /// </remarks>
    private async Task<IReadOnlyList<string>> ListStoresAwaitingCreditAsync(CancellationToken cancellationToken)
    {
        try
        {
            var storeIds = await _invoiceStore
                .ListStoreIdsAwaitingCreditAsync(
                    SparkInvoiceCreditor.ListableFrom(DateTimeOffset.UtcNow),
                    MaxCreditStoresPerPass,
                    cancellationToken)
                .ConfigureAwait(false);

            if (storeIds.Count >= MaxCreditStoresPerPass)
            {
                _logger.LogWarning(
                    "{Count} store(s) hold settled Lightning payments awaiting a BTCPay credit, which is this "
                    + "pass's limit; the rest are not walked until these are resolved. Something is wrong with "
                    + "this server's invoices — this set is normally empty",
                    storeIds.Count);
            }

            return storeIds;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Could not list the stores awaiting a BTCPay credit; this pass covers only the stores with a "
                + "running Spark wallet");
            return [];
        }
    }

    /// <summary>
    /// Retries the BTCPay credit for every settlement of a store that has not been credited yet. Returns the
    /// number credited by this pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes the routing survive a restart. Four situations put a row here, and one walk covers
    /// all of them: a payment that arrived while the process was down and was settled from the SDK's replay
    /// at reconnect; a listening session that was saturated past its retry allowance; a crash between the
    /// settlement's compare-and-set and BTCPay's own insert; and rows settled before this column existed,
    /// which resolve on the first pass as already recorded and are then never looked at again.
    /// </para>
    /// <para>
    /// Paged by keyset, oldest first, and bounded by <see cref="MaxCreditsPerPass"/>. No resume cursor, unlike
    /// the settlement walk: a record leaves this set permanently once it is resolved — credited, or given up on
    /// and reported — so restarting at the oldest each pass drains a backlog rather than starving behind it. The
    /// exception is a record that keeps failing, which stays at the front, and that is the right thing to keep
    /// retrying, because it is the one closest to its horizon.
    /// </para>
    /// <para>
    /// Every record examined here either leaves the set or is left deliberately: nothing ages out of it
    /// unresolved and unreported, which was the defect this walk shipped with. The bound the listing is given is
    /// <see cref="SparkInvoiceCreditor.ListableFrom"/> and not the retry horizon precisely so that a record past
    /// the horizon is still handed to the creditor once — to be reported and stamped — rather than vanishing.
    /// </para>
    /// </remarks>
    public async Task<int> CreditStoreAsync(string storeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        // The *listable* bound, not the creditable one. They used to be the same value, and that is precisely
        // what made a settlement past its retry horizon invisible: it left this listing at the instant it became
        // eligible to be reported as abandoned, so nothing ever reported it and its row stayed indistinguishable
        // from one still in flight. See SparkInvoiceCreditor.AbandonedReportingGrace.
        var settledFrom = SparkInvoiceCreditor.ListableFrom(DateTimeOffset.UtcNow);
        var credited = 0;
        var examined = 0;
        InvoiceReconciliationCursor? cursor = null;

        while (examined < MaxCreditsPerPass)
        {
            var pageSize = Math.Min(InvoicePageSize, MaxCreditsPerPass - examined);

            IReadOnlyList<InvoiceRecord> page;
            try
            {
                page = await _invoiceStore
                    .ListUncreditedAsync(storeId, settledFrom, cursor, pageSize, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Store {StoreId}: could not load settled Spark invoices awaiting a BTCPay credit", storeId);
                return credited;
            }

            if (page.Count == 0)
                break;

            foreach (var record in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                examined++;
                // Advanced before the attempt, so a record that keeps failing cannot stall this page's walk on
                // itself; the next pass starts from the top and reaches it again.
                cursor = new InvoiceReconciliationCursor(record.CreatedAt, record.PaymentHash);

                if (await CreditAsync(record, cancellationToken).ConfigureAwait(false)
                    is SparkInvoiceCreditResult.Credited)
                {
                    credited++;
                }
            }

            if (page.Count < pageSize)
                break;
        }

        if (credited > 0)
        {
            _logger.LogInformation(
                "Store {StoreId}: reconciliation credited {Credited} settled Lightning payment(s) to BTCPay "
                + "invoices that were no longer being watched for them",
                storeId, credited);
        }

        return credited;
    }

    /// <summary>
    /// Attempts one credit, absorbing anything it throws.
    /// </summary>
    /// <remarks>
    /// The creditor already promises not to throw; this is the belt to that braces, and it exists because of
    /// where the call sits. On the settlement path the settlement is already committed and the notification
    /// already published by the time it runs, so an exception escaping here would turn a completed settlement
    /// into a logged failure — and, on the SDK's event loop, into a store-wide error line for a payment that
    /// was in fact recorded correctly.
    /// </remarks>
    private async Task<SparkInvoiceCreditResult> CreditAsync(
        InvoiceRecord record,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _creditor.CreditAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Store {StoreId}: crediting the settled Lightning payment {PaymentHash} to its BTCPay invoice "
                + "failed unexpectedly; the settlement is recorded and the credit will be retried",
                record.StoreId, record.PaymentHash);
            return SparkInvoiceCreditResult.Failed;
        }
    }

    private static SparkSettlement ToSettlement(InvoiceRecord record) => new(
        record.StoreId,
        record.PaymentHash,
        record.Bolt11,
        record.AmountMsat,
        record.AmountReceivedMsat ?? 0,
        record.SettledAt ?? DateTimeOffset.UtcNow,
        record.ExpiresAt,
        record.Preimage);
}
