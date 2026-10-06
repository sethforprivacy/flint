using System;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.Flint;

/// <summary>
/// Adapts one store's Breez Spark SDK instance to BTCPay's Lightning client interface.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately a <em>receive-focused</em> client. BTCPay
/// tolerates <see cref="NotSupportedException"/> from the members a nodeless wallet cannot meaningfully
/// implement, so anything node-shaped (channels, peers, deposit addresses, node info) throws that
/// permanently, while the receive path plus BOLT11 send are implemented.
/// </para>
/// <para>
/// One member is a special case: <see cref="GetInfo"/>. BTCPay's <c>LightningLikePaymentHandler</c> calls
/// it while preparing a checkout in order to render node connection info, but it catches the failure and
/// continues. Throwing <see cref="NotSupportedException"/> there is the established nodeless-plugin trick
/// (Kukks' Breez plugin does the same) and is what lets checkout work without a node identity.
/// </para>
/// <para>
/// This object does <b>not</b> own the SDK instance — <c>SparkService</c> does, because the same client is
/// handed out for every connection-string resolution of a store. See <see cref="Dispose"/>.
/// </para>
/// </remarks>
public class SparkLightningClient : IExtendedLightningClient, IDisposable
{
    /// <summary>
    /// Expiry used when BTCPay asks for a zero or negative one. Never passed through to the SDK, which
    /// silently turns 0 into 24 h and null into 30 days.
    /// </summary>
    private static readonly TimeSpan FallbackExpiry = TimeSpan.FromHours(24);

    /// <summary>
    /// Serialises <see cref="Pay"/>'s probe-and-send for one invoice, process-wide.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Audit finding PaymentFlow F1. <see cref="Pay"/> is check-then-act: it asks the SDK whether an earlier
    /// attempt already sent this invoice, and sends if not. Nothing serialised the two, so two callers — the
    /// automated payout processor ticking while someone confirms by hand, two browser tabs, two Greenfield
    /// <c>…/lightning/BTC/invoices/pay</c> calls — could both pass the probe before either send landed in SDK
    /// storage. Both then sent, both got the deduplicated payment back, and both returned <c>Ok</c>: two payouts
    /// marked <c>Completed</c> against one payment. Whether the wallet also *pays* twice depends on the service
    /// provider's own idempotency racing, which is not ours to rely on.
    /// </para>
    /// <para>
    /// Holding a lock across probe-and-send fixes it at the source: the second caller enters only once the first
    /// has finished, so its probe now sees the payment and it takes the existing short-circuit branch, which
    /// already refuses correctly. Note what is deliberately *not* done — converting the post-send report CAS from
    /// false into an error. That CAS also returns false for a legitimate re-claim after an earlier attempt
    /// reported <c>Unknown</c>, and turning that into an error would tell BTCPay a payment failed when the money
    /// had just moved. Serialising is the narrow fix; the CAS is not a concurrency signal.
    /// </para>
    /// <para>
    /// Striped rather than one lock per invoice, because a per-key dictionary needs refcounted eviction to avoid
    /// growing without bound, and getting that wrong is a worse bug than the one being fixed. A hash collision
    /// only makes two unrelated payments take turns for the seconds a send lasts. <b>Process-wide only</b> — two
    /// BTCPay processes on one database would still race, which is the already-documented single-instance
    /// requirement, not a new limitation.
    /// </para>
    /// </remarks>
    private static readonly SemaphoreSlim[] PayLocks =
        Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private static SemaphoreSlim PayLockFor(string storeId, string paymentHash)
    {
        // Ordinal, culture-independent and stable across runs — unlike string.GetHashCode, which is randomised
        // per process. Stability is not required for correctness here, but a deterministic stripe makes a
        // concurrency failure reproducible instead of a coin flip.
        var hash = 17u;
        foreach (var c in storeId)
            hash = hash * 31 + c;
        foreach (var c in paymentHash)
            hash = hash * 31 + c;
        return PayLocks[hash % (uint)PayLocks.Length];
    }

    /// <summary>Page size for the payment scan.</summary>
    private const int PaymentScanLimit = 50;

    /// <summary>
    /// Most pages the send scan will walk, bounding it at 500 payments.
    /// </summary>
    private const int MaxPaymentPages = 10;

    /// <summary>Page size for the invoice listing.</summary>
    private const int InvoiceListLimit = 100;

    private readonly string _storeId;
    private readonly string _paymentKey;
    private readonly ISparkSdkClient _sdk;
    private readonly IInvoiceRecordStore _invoiceStore;
    private readonly IOutgoingPaymentStore _outgoingStore;
    private readonly SparkSettlementReconciler _reconciler;
    private readonly ISparkSettlementSubscriber _settlements;
    private readonly IBolt11Parser _bolt11Parser;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger _logger;

    public SparkLightningClient(
        string storeId,
        string paymentKey,
        ISparkSdkClient sdk,
        IInvoiceRecordStore invoiceStore,
        IOutgoingPaymentStore outgoingStore,
        SparkSettlementReconciler reconciler,
        ISparkSettlementSubscriber settlements,
        IBolt11Parser bolt11Parser,
        ILogger logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _storeId = storeId ?? throw new ArgumentNullException(nameof(storeId));
        _paymentKey = paymentKey ?? throw new ArgumentNullException(nameof(paymentKey));
        _sdk = sdk ?? throw new ArgumentNullException(nameof(sdk));
        _invoiceStore = invoiceStore ?? throw new ArgumentNullException(nameof(invoiceStore));
        _outgoingStore = outgoingStore ?? throw new ArgumentNullException(nameof(outgoingStore));
        _reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));
        _settlements = settlements ?? throw new ArgumentNullException(nameof(settlements));
        _bolt11Parser = bolt11Parser ?? throw new ArgumentNullException(nameof(bolt11Parser));
        // A bare HttpContextAccessor reads the process-wide AsyncLocal, so a fresh instance per client is
        // every bit as correct as a shared one; the optional parameter is what keeps the test fixtures that
        // build clients outside a request from having to supply one.
        _httpContextAccessor = httpContextAccessor ?? new HttpContextAccessor();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Store this client is bound to. A client is never shared between stores.</summary>
    public string StoreId => _storeId;

    /// <summary>
    /// BTCPay persists <c>client.ToString()</c> as the store's Lightning connection string
    /// (<c>LightningPaymentMethodConfig</c>) and parses it back through the registered handlers, so this must
    /// round-trip through <see cref="SparkConnectionStringHandler"/>.
    /// </summary>
    public override string ToString() => SparkConnectionString.Format(_storeId, _paymentKey);

    #region IExtendedLightningClient

    /// <summary>
    /// Called by BTCPay when the merchant saves the Lightning payment method, to tell them what is wrong
    /// before the configuration is accepted. Refuses to accept another store's connection string, then
    /// proves the wallet this string names is alive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The store-binding check is the plugin's cross-store defence.</b> A connection string embeds the
    /// store it belongs to (<c>store-id=…</c>) and the resolver returns that store's wallet, so saving store
    /// B's complete string on store A would make A receive into and spend from B's wallet. The handler
    /// cannot detect this — BTCPay's <c>ILightningConnectionStringHandler.Create</c> is never told which
    /// store is being configured — but <c>Validate</c> runs inside the save request itself, where core's
    /// authorisation has placed the store being configured on the <see cref="HttpContext"/>
    /// (<c>GetStoreDataOrNull</c>). If that store differs from the one this client is bound to, the string
    /// is being saved on the wrong store: refuse with a generic error that names neither store. A missing
    /// context (a background call, a route core has not authorised) means there is nothing to compare, and
    /// the check passes — the configuration sweep (<c>SparkLightningConfigSweeper</c>), which runs at
    /// startup and about every half hour thereafter, is the backstop for configurations that predate this
    /// check or that were written without one.
    /// </para>
    /// <para>
    /// The live check uses the cached, non-syncing <c>GetInfo</c>: it returns in ~0 ms and proves the native
    /// handle is alive and this store's instance has not been torn down. It deliberately does not force a
    /// sync — a ~2.2 s SSP round trip on a settings save is not worth the extra certainty, and a wallet that
    /// cannot reach the SSP is reported by the startup warm-up instead. The store check runs first, so a
    /// mismatched string is refused without contacting the wallet it names.
    /// </para>
    /// </remarks>
    public async Task<ValidationResult?> Validate()
    {
        var configuredStoreId = _httpContextAccessor.HttpContext?.GetStoreDataOrNull()?.Id;
        if (configuredStoreId is not null &&
            !string.Equals(configuredStoreId, _storeId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Refusing to save a connection string for store {EmbeddedStoreId} on store {TargetStoreId}",
                _storeId, configuredStoreId);
            return new ValidationResult("This connection string belongs to another store on this server.");
        }

        try
        {
            await _sdk.GetInfoAsync(ensureSynced: false).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Store {StoreId}: Spark validation failed", _storeId);
            return new ValidationResult($"The Spark wallet for this store is not usable: {SparkErrors.Describe(ex)}");
        }
    }

    /// <summary>Label BTCPay shows for this Lightning connection in store settings.</summary>
    public string? DisplayName => "Flint";

    /// <summary>
    /// Server URI BTCPay logs and displays. There is no server to point at — the SDK runs in-process — so
    /// null is correct and BTCPay falls back to the connection string.
    /// </summary>
    public Uri? ServerUri => null;

    #endregion

    #region Receive path

    /// <summary>
    /// Called by BTCPay for every Lightning checkout (<c>LightningLikePaymentHandler</c>) and by the
    /// LNURL/Lightning-address endpoints (<c>UILNURLController</c>).
    /// </summary>
    public Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry,
        CancellationToken cancellation = default) =>
        CreateInvoice(new CreateInvoiceParams(amount, description, expiry), cancellation);

    /// <summary>
    /// The parameterised overload BTCPay actually prefers.
    /// </summary>
    /// <remarks>
    /// Persists the <c>InvoiceRecord</c> before returning, and fails the call if that write fails. The SDK
    /// stores nothing for an unpaid invoice, so an invoice handed out but not recorded is an invoice that
    /// can be paid and never credited.
    /// </remarks>
    public async Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(createInvoiceRequest);

        var amountSats = ToAmountSats(createInvoiceRequest.Amount);
        var description = BuildDescription(createInvoiceRequest);
        var expirySecs = ToExpirySecs(createInvoiceRequest.Expiry);

        var received = await _sdk
            .ReceiveBolt11Async(description, amountSats, expirySecs, cancellation)
            .ConfigureAwait(false);

        var parsed = _bolt11Parser.Parse(received.PaymentRequest);
        if (parsed is null)
        {
            // A live invoice we cannot key. Refuse to hand it out rather than return one BTCPay could never
            // reconcile; logged with the invoice itself so it stays recoverable by hand.
            _logger.LogError(
                "Store {StoreId}: the Spark service returned a BOLT11 invoice this server cannot parse: {Bolt11}",
                _storeId, received.PaymentRequest);
            throw new InvalidOperationException("The Spark service returned an unusable Lightning invoice.");
        }

        var now = DateTimeOffset.UtcNow;
        var record = new InvoiceRecord
        {
            PaymentHash = parsed.PaymentHash,
            StoreId = _storeId,
            Bolt11 = received.PaymentRequest,
            // Taken from the invoice rather than from the request: the SSP is the authority on what was
            // actually minted, and it rounds to whole satoshi.
            AmountMsat = parsed.AmountMsat,
            Description = description,
            CreatedAt = now,
            ExpiresAt = parsed.ExpiresAt,
            Status = InvoiceRecordStatus.Unpaid
        };

        try
        {
            // CancellationToken.None, deliberately. The invoice now exists on the service provider and can be
            // paid; BTCPay's checkout path passes a short-lived token, and letting it cancel this write would
            // leave a live invoice with no local record — a payment that lands in the wallet and settles
            // nothing. Better to finish the write than to honour the cancellation.
            await _invoiceStore.AddAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Store {StoreId}: could not persist invoice {PaymentHash}. It has already been created on the " +
                "Spark service, so a payment to {Bolt11} would land in the wallet balance without settling a " +
                "BTCPay invoice",
                _storeId, record.PaymentHash, record.Bolt11);
            throw;
        }

        return ToLightningInvoice(record, now);
    }

    /// <summary>
    /// Called by BTCPay's <c>LightningListener</c> when an invoice is created or activated, and once per
    /// invoice when a listening session starts. Keyed by the id returned from
    /// <see cref="CreateInvoice(CreateInvoiceParams, CancellationToken)"/>, which is the payment hash.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolution order is the plugin's own record first (the only place an unpaid invoice exists), then the
    /// SDK, through the same <see cref="SparkSettlementReconciler"/> the event consumer and the reconciliation
    /// task use — so all three agree on what settlement means and only one of them can notify BTCPay.
    /// </para>
    /// <para>
    /// <b>BTCPay calls this far less often than it might appear.</b> It runs once per invoice when the invoice
    /// is created or activated, and once per invoice when a listening session starts; its one-minute timer only
    /// calls <c>CheckConnections()</c> and polls nothing. This is therefore not a recovery mechanism —
    /// <see cref="SparkReconciliationTask"/> is.
    /// </para>
    /// <para>
    /// Returns null for a hash this store did not create, which is what BTCPay's listener expects: it stops
    /// tracking an invoice the client does not know about.
    /// </para>
    /// </remarks>
    public async Task<LightningInvoice> GetInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        var paymentHash = NormaliseHash(invoiceId);
        if (paymentHash is null)
            return null!;

        var record = await _invoiceStore.GetAsync(_storeId, paymentHash, cancellation).ConfigureAwait(false);
        if (record is null)
            return null!;

        record = await _reconciler.ResolveAsync(_sdk, record, cancellation).ConfigureAwait(false);
        return ToLightningInvoice(record, DateTimeOffset.UtcNow);
    }

    /// <summary>Same as the string overload; BTCPay's Greenfield API uses this shape.</summary>
    public Task<LightningInvoice> GetInvoice(uint256 paymentHash, CancellationToken cancellation = default) =>
        GetInvoice(paymentHash?.ToString()!, cancellation);

    /// <summary>
    /// Called by BTCPay's <c>LightningListener</c> once per listening session; the returned listener is then
    /// awaited with <c>WaitInvoice</c> for the lifetime of that session. This is the primary settlement path
    /// — event-driven, not polled.
    /// </summary>
    /// <remarks>
    /// Each session gets an independent queue, because BTCPay may hold several sessions against one
    /// connection string and each filters notifications against its own set of listened invoices. A shared
    /// queue would let one session swallow another's notification.
    /// </remarks>
    public Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = default) =>
        Task.FromResult<ILightningInvoiceListener>(new SparkInvoiceListener(_settlements.Subscribe(_storeId)));

    /// <summary>
    /// Called by BTCPay's store dashboard (<c>StoreLightningBalance</c>) and the Greenfield API.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reports the Spark balance as the local offchain balance, from the cached, non-syncing <c>GetInfo</c>.
    /// Inbound liquidity is deliberately left unset rather than reported as some large number: Spark has no
    /// channels, and claiming inbound capacity would be a fiction the dashboard would then display. There is
    /// no on-chain balance either — this plugin's only on-chain movement is outward, via auto-sweep.
    /// </para>
    /// <para>
    /// <b>This number is for display and nothing else.</b> It lags settlement by roughly 20 seconds and
    /// drifts by a few sats around the SDK's background leaf optimisation, so it is deliberately not used
    /// to detect payments, to validate a send, or in any accounting. No sync is forced here: paying ~800 ms
    /// on a dashboard render to make a figure marginally less stale is not worth it, and a caller that
    /// genuinely needs a current balance has to sync explicitly.
    /// </para>
    /// </remarks>
    public async Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = default)
    {
        var info = await _sdk.GetInfoAsync(ensureSynced: false, cancellation).ConfigureAwait(false);
        return new LightningNodeBalance
        {
            OffchainBalance = new OffchainBalance
            {
                Local = LightMoney.Satoshis(info.BalanceSats)
            }
        };
    }

    /// <summary>
    /// Called by BTCPay's <c>LightningListener</c> and <c>UILNURLController</c> when an invoice expires or is
    /// superseded.
    /// </summary>
    /// <remarks>
    /// The Spark SDK has no invoice-cancellation primitive, so this is implemented locally: the record is
    /// marked cancelled and no longer offered to payers, but the invoice stays payable on the service
    /// provider and a late payment still settles it and credits the BTCPay invoice it was minted for. The
    /// money is real and the alternative is unattributed sats in the wallet; cancellation buys bookkeeping,
    /// not withdrawal.
    /// <para>
    /// Deliberately does not throw when there is nothing to cancel: BTCPay calls this speculatively, and a
    /// throw would be logged as an error for every invoice that had already been paid.
    /// </para>
    /// </remarks>
    public async Task CancelInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        var paymentHash = NormaliseHash(invoiceId);
        if (paymentHash is null)
            return;

        if (await _invoiceStore.CancelAsync(_storeId, paymentHash, cancellation).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "Store {StoreId}: invoice {PaymentHash} cancelled locally. Spark cannot withdraw it from the " +
                "service provider, so a late payment still settles and credits the BTCPay invoice it belongs to",
                _storeId, paymentHash);
            return;
        }

        _logger.LogDebug(
            "Store {StoreId}: nothing to cancel for {PaymentHash} (already settled, or not an invoice of ours)",
            _storeId, paymentHash);
    }

    #endregion

    #region Send path — Greenfield and payouts

    /// <summary>
    /// Called by BTCPay's Greenfield Lightning API and by <c>LightningAutomatedPayoutProcessor</c> when a
    /// store pays out over Lightning.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The idempotency key is derived deterministically from the payment hash rather than being random, so a
    /// retry after a crash reuses the same key and the service provider treats it as the same request. A fresh
    /// random key per attempt would make a double-spend possible on retry.
    /// </para>
    /// <para>
    /// The fee is quoted before anything is sent and checked against a limit, so an unexpectedly expensive
    /// route aborts rather than silently eating the merchant's margin.
    /// </para>
    /// <para>
    /// <b>Result semantics, which BTCPay acts on directly</b> (<c>LightningAutomatedPayoutProcessor</c>):
    /// <c>Ok</c> marks the payout <c>Completed</c> and is terminal; <c>Error</c> and
    /// <c>CouldNotFindRoute</c> return it to <c>AwaitingPayment</c> to be retried on the next tick (up to ten
    /// failures, after which that payout is skipped); <c>Unknown</c> leaves it <c>InProgress</c> for
    /// <c>LightningPendingPayoutListener</c> to resolve via <see cref="GetPayment"/>. So <c>Error</c> must mean
    /// "nothing was sent" and <c>Unknown</c> must mean "it may have been sent" — getting that backwards either
    /// double-pays or abandons a real payment.
    /// </para>
    /// </remarks>
    public async Task<PayResponse> Pay(string? bolt11, PayInvoiceParams? payParams,
        CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(bolt11))
        {
            // Reached when BTCPay asks for a spontaneous (keysend) payment to a bare destination pubkey,
            // which Spark does not offer.
            return new PayResponse(PayResult.Error,
                "Spark can only pay BOLT11 invoices; spontaneous (keysend) payments are not supported.");
        }

        var parsed = _bolt11Parser.Parse(bolt11);
        if (parsed is null)
            return new PayResponse(PayResult.Error, "That is not a valid Lightning invoice for this network.");

        long? explicitAmountSats = null;
        if (parsed.AmountMsat is null)
        {
            if (payParams?.Amount is null)
            {
                return new PayResponse(PayResult.Error,
                    "This invoice has no amount, so an explicit amount is required.");
            }

            explicitAmountSats = ToSendAmountSats(payParams.Amount);
            if (explicitAmountSats is null)
                return new PayResponse(PayResult.Error, "The amount to pay must be greater than zero.");
        }

        // Floor, not ceiling: this is money leaving the wallet. Rounding a fractional-satoshi request up would
        // overpay the recipient by up to a satoshi per payout, which is the opposite of the receive side's
        // requirement.
        var totalAmountSats = explicitAmountSats ?? parsed.AmountMsat!.Value / 1000;
        var idempotencyKey = DeriveIdempotencyKey(parsed.PaymentHash);
        var now = DateTimeOffset.UtcNow;

        // Everything from here to the end of the method is one invoice's turn. See PayLocks: the probe and the
        // send have to be indivisible or two callers both pass the probe and both send. Registration is inside
        // the lock too, so `attempt` is read after any concurrent attempt has finished rather than before it.
        var payLock = PayLockFor(_storeId, parsed.PaymentHash);
        await payLock.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            OutgoingPaymentRecord attempt;
            try
            {
                attempt = await _outgoingStore
                    .RegisterAttemptAsync(_storeId, parsed.PaymentHash, idempotencyKey, bolt11, now, cancellation)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Without this record the duplicate-claim guard below cannot work, so refusing is the safe answer:
                // Error returns the payout to AwaitingPayment and BTCPay retries it, no money moves.
                _logger.LogError(ex,
                    "Store {StoreId}: could not record a payment attempt for {PaymentHash}; refusing to send",
                    _storeId, parsed.PaymentHash);
                return new PayResponse(PayResult.Error,
                    "This server could not record the payment attempt, so it was not sent. It will be retried.");
            }

            try
            {
                // The SDK adopts the idempotency key as the resulting Payment.id, so this asks "did an earlier
                // attempt already send this?" before spending anything. Verified for cooperative exits; for
                // Lightning sends a miss simply falls through to the send, where the service provider's own
                // idempotency still applies.
                var existing = await ProbeForEarlierSendAsync(idempotencyKey, cancellation).ConfigureAwait(false);

                // Only a live or completed send short-circuits. A Failed one must not: BTCPay would otherwise get
                // Error for every subsequent retry, the payout would exhaust its ten attempts and be parked, and
                // the service provider would never be contacted again after one transient failure.
                if (existing is { Direction: SparkPaymentDirection.Send, Status: SparkPaymentStatus.Completed or SparkPaymentStatus.Pending })
                {
                    if (attempt.ReportedAt is null &&
                        await _outgoingStore.TryMarkReportedAsync(_storeId, parsed.PaymentHash, now, cancellation)
                            .ConfigureAwait(false))
                    {
                        _logger.LogInformation(
                            "Store {StoreId}: invoice {PaymentHash} had already been sent by an earlier attempt "
                            + "({Status}); reporting that payment rather than sending again",
                            _storeId, parsed.PaymentHash, existing.Status);
                        return ToPayResponse(existing, parsed.PaymentHash);
                    }

                    // This wallet has already told BTCPay about this payment once. A BOLT11 invoice can only be
                    // paid once, so this is a second obligation naming an invoice that is already settled and it
                    // cannot be discharged. Reporting success would mark both payouts Completed with only one
                    // payment made.
                    _logger.LogError(
                        "Store {StoreId}: refusing a repeat payment of invoice {PaymentHash}. It was already paid "
                        + "by this wallet (first asked at {FirstAttemptAt}, {AttemptCount} attempts) and a Lightning "
                        + "invoice cannot be paid twice. If this is a genuine second payout, it needs a new invoice",
                        _storeId, parsed.PaymentHash, attempt.FirstAttemptAt, attempt.AttemptCount);
                    return new PayResponse(PayResult.Error,
                        "This invoice has already been paid by this wallet. A Lightning invoice cannot be paid "
                        + "twice; a second payout needs a new invoice.");
                }

                if (existing is { Status: SparkPaymentStatus.Failed })
                {
                    _logger.LogWarning(
                        "Store {StoreId}: a previous attempt to pay {PaymentHash} failed; retrying with the same "
                        + "idempotency key. If the service provider replays the failure rather than re-attempting, "
                        + "this payout will need a fresh invoice",
                        _storeId, parsed.PaymentHash);
                }

                var result = await _sdk.SendBolt11Async(
                        bolt11,
                        explicitAmountSats,
                        idempotencyKey,
                        quote => ApproveFee(quote, payParams, totalAmountSats),
                        payParams?.SendTimeout,
                        cancellation)
                    .ConfigureAwait(false);

                if (result.RejectedReason is not null)
                {
                    // The fee policy vetoed the quote, so nothing was sent and ReportedAt stays null: the next
                    // attempt starts clean.
                    return new PayResponse(PayResult.Error, result.RejectedReason);
                }

                var response = ToPayResponse(result.Payment!, parsed.PaymentHash);
                await MarkReportedIfSentAsync(parsed.PaymentHash, response.Result, now, cancellation)
                    .ConfigureAwait(false);
                return response;
            }
            catch (Exception ex)
            {
                // Deliberately not rethrown. BTCPay's payout processor treats an exception as an infrastructure
                // failure, whereas a PayResponse carrying an error is recorded against the payout where the
                // merchant can see it.
                //
                // Only a refusal the SDK made before spending counts as Error — an insufficient balance, or an
                // input it rejected locally such as an amount below the dust floor. Anything else, notably a
                // timeout, may still be in flight at the service provider. Note the balance is deliberately not
                // pre-checked: it lags settlement by ~20 seconds, so a pre-check would reject payments the wallet
                // can actually afford.
                // SparkPreSendException is the third arm, and it is why the probe and the prepare are wrapped:
                // both run before SendPayment, so a blip in either provably spent nothing. Without it they fell
                // through to Unknown, BTCPay marked the payout InProgress, and ten minutes later the pending
                // listener resolved it to Cancelled — churn from a failure that never touched the wallet.
                var definitelyNotSent = SparkErrors.IsInsufficientFunds(ex)
                                        || SparkErrors.IsInvalidInput(ex)
                                        || ex is SparkPreSendException;
                var result = definitelyNotSent ? PayResult.Error : PayResult.Unknown;
                _logger.LogWarning(ex,
                    "Store {StoreId}: Lightning payment failed ({Reason}); reporting {Result}",
                    _storeId, SparkErrors.Describe(ex), result);
                await MarkReportedIfSentAsync(parsed.PaymentHash, result, now, cancellation).ConfigureAwait(false);
                return new PayResponse(result, SparkErrors.Describe(ex));
            }
        }
        finally
        {
            payLock.Release();
        }
    }

    /// <summary>
    /// Claims the right to report a payment, for any outcome that is not a definite "nothing was sent".
    /// </summary>
    /// <remarks>
    /// <c>Unknown</c> counts as reported: the send may be in flight, BTCPay will leave the payout
    /// <c>InProgress</c> and resolve it through <see cref="GetPayment"/>, and if it did land then a later claim
    /// naming the same invoice must be refused. <c>Error</c> deliberately does not, so a retry after a definite
    /// refusal is unimpeded.
    /// </remarks>
    /// <summary>
    /// The "did an earlier attempt already send this?" probe, with its failures marked as pre-send.
    /// </summary>
    /// <remarks>
    /// This call is definitionally before the send — it is what decides whether to send at all — so a network or
    /// SSP failure here spent nothing. It shares <see cref="SparkPreSendException"/> with the prepare round trip
    /// so <see cref="Pay"/> needs one classification rule rather than two.
    /// </remarks>
    private async Task<SparkPayment?> ProbeForEarlierSendAsync(
        string idempotencyKey,
        CancellationToken cancellation)
    {
        try
        {
            return await _sdk.GetPaymentAsync(idempotencyKey, cancellation).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new SparkPreSendException(ex);
        }
    }

    private async Task MarkReportedIfSentAsync(
        string paymentHash,
        PayResult result,
        DateTimeOffset now,
        CancellationToken cancellation)
    {
        if (result is not (PayResult.Ok or PayResult.Unknown))
            return;

        try
        {
            await _outgoingStore.TryMarkReportedAsync(_storeId, paymentHash, now, cancellation)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The money has already moved, or may have. Failing the call now would tell BTCPay the payment did
            // not happen, which is worse than losing the duplicate-claim guard for this invoice.
            _logger.LogError(ex,
                "Store {StoreId}: paid {PaymentHash} but could not record that it was reported. A second payout "
                + "naming this invoice would not be refused",
                _storeId, paymentHash);
        }
    }

    private static PayResponse ToPayResponse(SparkPayment payment, string invoicePaymentHash) =>
        new(ToPayResult(payment.Status))
        {
            Details = new PayDetails
            {
                Status = ToPaymentStatus(payment.Status),
                // The SDK reports amount and fee separately for a send, and amount is what left the wallet
                // before the fee. BTCPay wants the total.
                TotalAmount = LightMoney.Satoshis(payment.AmountSats + payment.FeeSats),
                FeeAmount = LightMoney.Satoshis(payment.FeeSats),
                PaymentHash = TryParseHash(payment.PaymentHash ?? invoicePaymentHash),
                Preimage = TryParseHash(payment.Preimage)
            }
        };

    public Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = default) =>
        Pay(bolt11, null, cancellation);

    public Task<PayResponse> Pay(PayInvoiceParams payParams, CancellationToken cancellation = default) =>
        Pay(null, payParams, cancellation);

    /// <summary>
    /// Called by BTCPay to resolve an outgoing payment, always by <b>payment hash</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two callers, both passing a payment hash: <c>LightningAutomatedPayoutProcessor</c> whenever a
    /// <c>PayResponse</c> arrived without a preimage or an amount, and <c>LightningPendingPayoutListener</c>
    /// every ten minutes for each payout left <c>InProgress</c>.
    /// </para>
    /// <para>
    /// <b>Returning null here cancels a payout.</b> The pending listener maps a null payment through
    /// <c>_ =&gt; PayoutState.Cancelled</c>, so failing to find a payment that really was made marks the payout
    /// cancelled while the recipient keeps the sats. That is why the scan is paged rather than a single
    /// newest-first page: the payout being resolved may be ten minutes and any number of payments old.
    /// </para>
    /// <para>
    /// A hash lookup rather than an id lookup because the SDK's id for an outgoing payment is opaque
    /// <em>and</em> can mutate from provisional to final, so it cannot serve as a key held across calls.
    /// </para>
    /// </remarks>
    public async Task<LightningPayment> GetPayment(string paymentHash, CancellationToken cancellation = default)
    {
        var normalised = NormaliseHash(paymentHash);
        if (normalised is null)
            return null!;

        // The idempotency key is derived from the hash and the SDK adopts it as the payment id, so try the
        // point lookup first: it is one query instead of up to ten, and it works however long the history is.
        try
        {
            var byId = await _sdk
                .GetPaymentAsync(DeriveIdempotencyKey(normalised), cancellation)
                .ConfigureAwait(false);
            if (byId is { Direction: SparkPaymentDirection.Send })
                return ToLightningPayment(byId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Store {StoreId}: point lookup for send {PaymentHash} failed; falling back to a scan",
                _storeId, normalised);
        }

        for (var page = 0; page < MaxPaymentPages; page++)
        {
            var payments = await _sdk.ListPaymentsAsync(
                    new SparkListPaymentsQuery(
                        SparkPaymentDirection.Send,
                        Offset: page * PaymentScanLimit,
                        Limit: PaymentScanLimit),
                    cancellation)
                .ConfigureAwait(false);

            var match = payments.FirstOrDefault(p =>
                p.PaymentHash == normalised && p.Direction is SparkPaymentDirection.Send);
            if (match is not null)
                return ToLightningPayment(match);

            if (payments.Count < PaymentScanLimit)
                return null!;
        }

        _logger.LogWarning(
            "Store {StoreId}: could not find a send matching {PaymentHash} in the most recent {Count} payments. "
            + "If a payout is waiting on it, it may be cancelled incorrectly",
            _storeId, normalised, MaxPaymentPages * PaymentScanLimit);
        return null!;
    }

    #endregion

    #region Listing — Greenfield API

    public Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = default) =>
        ListInvoices(new ListInvoicesParams(), cancellation);

    /// <summary>
    /// Backed by the plugin's own invoice table rather than the SDK, which has no record of unpaid invoices
    /// at all.
    /// </summary>
    public async Task<LightningInvoice[]> ListInvoices(ListInvoicesParams? request,
        CancellationToken cancellation = default)
    {
        var offset = (int)Math.Clamp(request?.OffsetIndex ?? 0, 0, int.MaxValue);
        var records = await _invoiceStore
            .ListAsync(_storeId, request?.PendingOnly is true, offset, InvoiceListLimit, cancellation)
            .ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        return records.Select(record => ToLightningInvoice(record, now)).ToArray();
    }

    public Task<LightningPayment[]> ListPayments(CancellationToken cancellation = default) =>
        ListPayments(new ListPaymentsParams(), cancellation);

    public async Task<LightningPayment[]> ListPayments(ListPaymentsParams? request,
        CancellationToken cancellation = default)
    {
        var offset = (int)Math.Clamp(request?.OffsetIndex ?? 0, 0, int.MaxValue);
        var payments = await _sdk.ListPaymentsAsync(
                new SparkListPaymentsQuery(
                    SparkPaymentDirection.Send,
                    CompletedOnly: request?.IncludePending is not true,
                    Offset: offset,
                    Limit: PaymentScanLimit),
                cancellation)
            .ConfigureAwait(false);

        return payments.Select(ToLightningPayment).ToArray();
    }

    #endregion

    #region Node-shaped members — permanently unsupported

    /// <summary>
    /// Called by core's "Test connection" button, by the Greenfield API, and — up to BTCPay 2.4.4 only — by
    /// <c>LightningLikePaymentHandler</c> on every invoice it created (to show node connection details; 2.4.5
    /// dropped that lookup). BTCPay catches the failure and continues, so throwing
    /// <see cref="NotSupportedException"/> is the correct answer for a nodeless wallet: there is no node id,
    /// alias, block height or peer address to report, and inventing one would put a meaningless "connect to
    /// this node" string in front of the merchant.
    /// </summary>
    public Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = default) =>
        throw new NotSupportedException("Spark is nodeless: there is no Lightning node to describe.");

    /// <summary>Called by the Greenfield API only. There are no channels in a statechain wallet.</summary>
    public Task<LightningChannel[]> ListChannels(CancellationToken cancellation = default) =>
        throw new NotSupportedException("Spark is nodeless: there are no channels.");

    /// <summary>Called by the Greenfield API only. There are no channels to open.</summary>
    public Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest,
        CancellationToken cancellation = default) =>
        throw new NotSupportedException("Spark is nodeless: channels cannot be opened.");

    /// <summary>Called by the Greenfield API only. There are no peers to connect to.</summary>
    public Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo, CancellationToken cancellation = default) =>
        throw new NotSupportedException("Spark is nodeless: there are no peers to connect to.");

    /// <summary>
    /// Called by the Greenfield API only, to fund a node's on-chain wallet.
    /// </summary>
    /// <remarks>
    /// Deliberately unsupported rather than mapped to a Spark deposit address: BTCPay presents this as "fund
    /// your Lightning node", but a Spark static deposit needs an explicit claim (with its own fee ceiling)
    /// before the funds are spendable, so an address handed out here would look like a working deposit
    /// address and behave like a way to lose money.
    /// </remarks>
    public Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = default) =>
        throw new NotSupportedException("Spark is nodeless: it has no on-chain deposit wallet to fund.");

    #endregion

    #region Mapping and validation helpers

    /// <summary>
    /// Converts BTCPay's millisatoshi amount to the whole satoshi the SDK accepts, rounding <b>up</b>.
    /// </summary>
    /// <remarks>
    /// Rounding up matters: BTCPay derives Lightning amounts from a fiat price, so a sub-satoshi remainder is
    /// normal, and rounding down would systematically undercharge by up to one satoshi per invoice. Zero and
    /// null both mean "amountless" — which is also how the SDK treats <c>amountSats = 0</c>.
    /// </remarks>
    internal static long? ToAmountSats(LightMoney? amount)
    {
        if (amount is null || amount.MilliSatoshi <= 0)
            return null;
        // Ceiling without the +999 idiom: for a millisatoshi value near long.MaxValue the addition wraps
        // negative, and a negative result here maps to "amountless invoice" downstream — an absurd requested
        // amount must stay absurd (and fail loudly), never turn into an invoice for anything-goes.
        var msat = amount.MilliSatoshi;
        return msat / 1000 + (msat % 1000 > 0 ? 1 : 0);
    }

    /// <summary>
    /// Converts an amount to pay to whole satoshi, rounding <b>down</b>.
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="ToAmountSats"/> and deliberately not the same rounding. On the receive side a
    /// fractional satoshi must be rounded up or the merchant is undercharged; on the send side rounding up
    /// would pay the recipient up to a satoshi more than the obligation. Both directions round in the
    /// merchant's favour. Returns null when the result is not a positive amount.
    /// </remarks>
    internal static long? ToSendAmountSats(LightMoney? amount)
    {
        if (amount is null || amount.MilliSatoshi <= 0)
            return null;
        var sats = amount.MilliSatoshi / 1000;
        return sats > 0 ? sats : null;
    }

    /// <summary>
    /// Converts BTCPay's expiry to the SDK's seconds, never passing a value the SDK would reinterpret.
    /// </summary>
    /// <remarks>
    /// The SDK coerces <c>0</c> to 24 hours and <c>null</c> to 30 days, either of which would silently
    /// contradict what BTCPay told the payer.
    /// </remarks>
    internal static uint ToExpirySecs(TimeSpan expiry)
    {
        var seconds = expiry.TotalSeconds;
        if (seconds < 1)
            seconds = FallbackExpiry.TotalSeconds;
        return (uint)Math.Min(seconds, uint.MaxValue);
    }

    /// <summary>
    /// Chooses the text that goes into the BOLT11 <c>d</c> tag.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Known LUD-06 deviation.</b> <c>ReceivePaymentMethod.Bolt11Invoice</c> has no <c>descriptionHash</c>
    /// field: the SSP's GraphQL input type does not expose one, and passing a hash as the description simply
    /// puts the hex in the <c>d</c> tag while leaving <c>h</c> unset. So when BTCPay sets
    /// <c>DescriptionHashOnly</c> — which its LNURL and Lightning-address endpoints always do — the plain
    /// description goes in <c>d</c> and the invoice is returned anyway.
    /// </para>
    /// <para>
    /// That is not strictly LUD-06 conformant (the spec requires <c>h == sha256(metadata)</c>) and a strict
    /// wallet may reject the invoice; most tolerate it. The alternative — failing invoice creation — would
    /// break LNURL and Lightning addresses outright, which is worse. Revisit when Breez adds
    /// <c>description_hash</c> to the receive request; that is an upstream feature request to raise alongside
    /// the API-key conversation (spike notes §5.3).
    /// </para>
    /// <para>
    /// The result is truncated to <see cref="Constants.MaxBolt11DescriptionBytes"/> UTF-8 bytes because the
    /// SSP rejects anything longer, and only after a round trip.
    /// </para>
    /// </remarks>
    internal static string BuildDescription(CreateInvoiceParams request)
    {
        // An empty d tag rather than the description hash's hex. The only way to reach here with no description
        // is BTCPay's obsolete description-hash constructor, and since this SDK cannot set the h tag either
        // way, putting a hash's hex in the d tag would show the payer 64 characters of noise while still not
        // conforming to LUD-06. An empty description is at least honest about carrying no information.
        var description = request.Description ?? string.Empty;
        return TruncateUtf8(description, Constants.MaxBolt11DescriptionBytes);
    }

    /// <summary>Truncates to a byte budget without splitting a UTF-8 sequence.</summary>
    internal static string TruncateUtf8(string value, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
            return value;

        var bytes = Encoding.UTF8.GetBytes(value);
        var length = maxBytes;
        // Walk back off any continuation byte so the result stays valid UTF-8.
        while (length > 0 && (bytes[length] & 0b1100_0000) == 0b1000_0000)
            length--;
        return Encoding.UTF8.GetString(bytes, 0, length);
    }

    /// <summary>
    /// Derives the SDK idempotency key for paying an invoice.
    /// </summary>
    /// <remarks>
    /// Must be a UUID string — the SDK rejects anything else with a misleading "Invalid TransferId format", and
    /// <c>SdkException.InvalidUuid</c> exists, so the version and variant nibbles are stamped to make this a
    /// well-formed RFC 4122 version-4 UUID rather than 16 raw hash bytes.
    /// <para>
    /// Derived from the payment hash so it is stable across retries and process restarts without needing a
    /// lookup; a payment hash is unique per invoice, which is exactly the granularity at which "the same
    /// payment" should be deduplicated. It is also what makes <c>GetPayment(key)</c> answer "did this already
    /// send?", because the SDK adopts the key as the payment id.
    /// </para>
    /// </remarks>
    internal static string DeriveIdempotencyKey(string paymentHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(paymentHash);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"spark-pay:{paymentHash}"));
        var bytes = digest.AsSpan(0, 16).ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40); // version 4
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        // Guid's own byte order is little-endian for the first three groups; formatting via the byte array
        // keeps this stable across platforms because the input is a fixed digest either way.
        return new Guid(bytes).ToString();
    }

    /// <summary>
    /// Applies a fee limit to the SDK's quote. Returns null to proceed, or a reason to abort.
    /// </summary>
    /// <remarks>
    /// A limit always applies, even when the caller supplies none. BTCPay's automated payout path passes a
    /// <c>PayInvoiceParams</c> carrying only an amount, so without a default the merchant would have no
    /// protection at all against a pathologically expensive route on an automated payout. The default is
    /// generous — observed Lightning send fees were well under 1% — so it only bites on genuinely abnormal
    /// quotes, and the flat floor keeps small payments from being blocked by percentage rounding.
    /// </remarks>
    internal static string? ApproveFee(SparkSendQuote quote, PayInvoiceParams? payParams, long amountSats)
    {
        // Every limit below is a <=, so a negative fee — the shape a wrapped provider u64 takes — would pass
        // them all. The SDK client's conversions saturate rather than wrap; this is the backstop.
        if (quote.FeeSats < 0)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "The Spark fee for this payment is negative ({0} sat), which is not a number a fee limit can "
                + "bound. The payment was refused.",
                quote.FeeSats);
        }

        long? maxFeeSats = null;

        if (payParams?.MaxFeeFlat is { } flat)
            maxFeeSats = flat.Satoshi;

        if (payParams?.MaxFeePercent is { } percent and > 0)
        {
            var percentLimit = (long)Math.Floor(amountSats * percent / 100d);
            maxFeeSats = maxFeeSats is null ? percentLimit : Math.Min(maxFeeSats.Value, percentLimit);
        }

        var isCallerLimit = maxFeeSats is not null;
        maxFeeSats ??= Math.Max(
            (long)Math.Floor(amountSats * Constants.DefaultMaxFeePercent / 100d),
            Constants.DefaultMaxFeeFloorSats);

        if (quote.FeeSats <= maxFeeSats)
            return null;

        return string.Format(
            CultureInfo.InvariantCulture,
            isCallerLimit
                ? "The Spark fee for this payment is {0} sat, above the {1} sat limit set for this payout."
                : "The Spark fee for this payment is {0} sat, above this plugin's default limit of {1} sat. "
                  + "Set an explicit maximum fee on the payout to allow it.",
            quote.FeeSats, maxFeeSats.Value);
    }

    private static LightningInvoice ToLightningInvoice(InvoiceRecord record, DateTimeOffset now) => new()
    {
        // Id is the payment hash and must stay stable across CreateInvoice, GetInvoice and WaitInvoice:
        // BTCPay's listener keys its pending-invoice dictionary on it.
        Id = record.PaymentHash,
        PaymentHash = record.PaymentHash,
        Preimage = record.Preimage,
        BOLT11 = record.Bolt11,
        Status = record.EffectiveStatus(now) switch
        {
            InvoiceRecordStatus.Paid => LightningInvoiceStatus.Paid,
            InvoiceRecordStatus.Expired => LightningInvoiceStatus.Expired,
            _ => LightningInvoiceStatus.Unpaid
        },
        ExpiresAt = record.ExpiresAt,
        PaidAt = record.SettledAt,
        Amount = record.AmountMsat is { } amount ? LightMoney.MilliSatoshis(amount) : LightMoney.Zero,
        // BTCPay credits AmountReceived ?? Amount, so this must be what actually arrived, and must stay null
        // while nothing has.
        AmountReceived = record.AmountReceivedMsat is { } receivedMsat
            ? LightMoney.MilliSatoshis(receivedMsat)
            : null
    };

    private static LightningInvoice ToLightningInvoice(SparkSettlement settlement) => new()
    {
        Id = settlement.PaymentHash,
        PaymentHash = settlement.PaymentHash,
        Preimage = settlement.Preimage,
        BOLT11 = settlement.Bolt11,
        Status = LightningInvoiceStatus.Paid,
        ExpiresAt = settlement.ExpiresAt,
        PaidAt = settlement.SettledAt,
        Amount = settlement.AmountMsat is { } amount ? LightMoney.MilliSatoshis(amount) : LightMoney.Zero,
        AmountReceived = LightMoney.MilliSatoshis(settlement.AmountReceivedMsat)
    };

    private static LightningPayment ToLightningPayment(SparkPayment payment) => new()
    {
        Id = payment.SdkPaymentId,
        PaymentHash = payment.PaymentHash,
        Preimage = payment.Preimage,
        BOLT11 = payment.Bolt11,
        Status = ToPaymentStatus(payment.Status),
        CreatedAt = payment.Timestamp,
        Amount = LightMoney.Satoshis(payment.AmountSats),
        AmountSent = LightMoney.Satoshis(payment.AmountSats + payment.FeeSats),
        Fee = LightMoney.Satoshis(payment.FeeSats)
    };

    private static LightningPaymentStatus ToPaymentStatus(SparkPaymentStatus status) => status switch
    {
        SparkPaymentStatus.Completed => LightningPaymentStatus.Complete,
        SparkPaymentStatus.Failed => LightningPaymentStatus.Failed,
        _ => LightningPaymentStatus.Pending
    };

    private static PayResult ToPayResult(SparkPaymentStatus status) => status switch
    {
        SparkPaymentStatus.Completed => PayResult.Ok,
        SparkPaymentStatus.Failed => PayResult.Error,
        // Pending is genuinely unknown: the SSP has accepted it but the HTLC has not resolved.
        _ => PayResult.Unknown
    };

    /// <summary>
    /// Normalises a payment hash, rejecting anything that is not 32 bytes of hex.
    /// </summary>
    /// <remarks>
    /// The value arrives from BTCPay's Greenfield API and from stored payment-method details, so it is not
    /// necessarily well formed. Validating here keeps a malformed id from becoming an SDK round trip.
    /// </remarks>
    internal static string? NormaliseHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        if (trimmed.Length != 64)
            return null;
        foreach (var c in trimmed)
        {
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!isHex)
                return null;
        }

        return trimmed.ToLowerInvariant();
    }

    private static uint256? TryParseHash(string? hex)
    {
        var normalised = NormaliseHash(hex);
        return normalised is null ? null : uint256.Parse(normalised);
    }

    #endregion

    /// <summary>
    /// Deliberately does not touch the SDK instance.
    /// </summary>
    /// <remarks>
    /// One client is shared by every connection-string resolution for a store, and BTCPay never disposes an
    /// <c>ILightningClient</c> — but if a future version did, disposing the SDK handle here would take the
    /// store's wallet offline mid-checkout. <c>SparkService</c> owns the instance and is the only thing that
    /// shuts it down.
    /// </remarks>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    /// <summary>Bridges one store's settlement stream to BTCPay's listener contract.</summary>
    private sealed class SparkInvoiceListener : ILightningInvoiceListener
    {
        private readonly ISparkSettlementSubscription _subscription;

        public SparkInvoiceListener(ISparkSettlementSubscription subscription)
        {
            _subscription = subscription;
        }

        public async Task<LightningInvoice> WaitInvoice(CancellationToken cancellation)
        {
            // A genuine wait on a channel, not a polling loop: settlements arrive from the SDK's event
            // stream. Cancellation propagates as OperationCanceledException, which is what BTCPay's listener
            // expects when it shuts a session down.
            var settlement = await _subscription.ReadAsync(cancellation).ConfigureAwait(false);
            return ToLightningInvoice(settlement);
        }

        public void Dispose() => _subscription.Dispose();
    }
}
