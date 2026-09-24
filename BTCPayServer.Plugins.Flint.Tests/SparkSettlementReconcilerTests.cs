using System.Numerics;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The reconciliation path — the plugin's actual settlement guarantee.
/// </summary>
/// <remarks>
/// It exists because neither alternative is reliable. The SDK drops completion events (a completed receive was
/// observed emitting only <c>PaymentPending</c>), and BTCPay does not re-poll: it calls <c>GetInvoice</c> once
/// per invoice at creation or activation and once per invoice when a listening session starts, while its
/// one-minute timer only calls <c>CheckConnections()</c>. These tests pin the behaviour that covers that gap.
/// </remarks>
public class SparkSettlementReconcilerTests
{
    private const string StoreId = "store-1";
    private const string Bolt11 = "lnbcrt-one";
    private static readonly string Hash = PaymentFixture.PaymentHash;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (SparkSettlementReconciler Reconciler, InMemoryInvoiceRecordStore Store,
        SparkSettlementBroadcaster Broadcaster) Create()
    {
        var (reconciler, store, broadcaster, _) = CreateWithLog();
        return (reconciler, store, broadcaster);
    }

    private static (SparkSettlementReconciler Reconciler, InMemoryInvoiceRecordStore Store,
        SparkSettlementBroadcaster Broadcaster, CapturingLogger<SparkSettlementReconciler> Log) CreateWithLog()
    {
        var (reconciler, store, broadcaster, log, _) = CreateWithCredits();
        return (reconciler, store, broadcaster, log);
    }

    /// <summary>
    /// The same reconciler with BTCPay's side of the settlement visible, for the tests about crediting.
    /// </summary>
    private static (SparkSettlementReconciler Reconciler, InMemoryInvoiceRecordStore Store,
        SparkSettlementBroadcaster Broadcaster, CapturingLogger<SparkSettlementReconciler> Log,
        FakeInvoiceCreditGateway Credits) CreateWithCredits()
    {
        var store = new InMemoryInvoiceRecordStore();
        var broadcaster = new SparkSettlementBroadcaster(NullLogger<SparkSettlementBroadcaster>.Instance);
        var log = new CapturingLogger<SparkSettlementReconciler>();
        var credits = new FakeInvoiceCreditGateway();
        var reconciler = new SparkSettlementReconciler(
            store,
            broadcaster,
            new SparkInvoiceCreditor(credits, store, NullLogger<SparkInvoiceCreditor>.Instance),
            log);
        return (reconciler, store, broadcaster, log, credits);
    }

    /// <summary>
    /// The same reconciler again, with the <em>creditor's</em> log visible rather than the reconciler's.
    /// </summary>
    /// <remarks>
    /// A separate factory because the operator report about a settlement that can never be credited is emitted
    /// by <see cref="SparkInvoiceCreditor"/>, and the property worth pinning — that it fires once per record and
    /// not once per pass — is only observable by counting lines on that logger while driving the walk this class
    /// owns.
    /// </remarks>
    private static (SparkSettlementReconciler Reconciler, InMemoryInvoiceRecordStore Store,
        FakeInvoiceCreditGateway Credits, CapturingLogger<SparkInvoiceCreditor> CreditLog) CreateWithCreditLog()
    {
        var store = new InMemoryInvoiceRecordStore();
        var credits = new FakeInvoiceCreditGateway();
        var creditLog = new CapturingLogger<SparkInvoiceCreditor>();
        var reconciler = new SparkSettlementReconciler(
            store,
            new SparkSettlementBroadcaster(NullLogger<SparkSettlementBroadcaster>.Instance),
            new SparkInvoiceCreditor(credits, store, creditLog),
            NullLogger<SparkSettlementReconciler>.Instance);
        return (reconciler, store, credits, creditLog);
    }

    [Fact]
    public async Task A_settlement_for_less_than_the_invoiced_amount_is_logged_loudly()
    {
        // Audit finding PaymentFlow F3. Nothing compares what arrived to what was invoiced, and the record
        // settles once and never revises upward — so a sub-amount arrival marks the invoice paid for less than
        // it asked, and the completing payment that follows is swallowed as AlreadySettled.
        //
        // Deliberately still a settlement, not a refusal: on the Lightning rail this cannot happen (the preimage
        // is only released for a full payment), and refusing on an amount the SDK reported a hair low would stop
        // legitimate invoices settling — a worse failure than the unproven Spark-rail case it would defend
        // against. So the invariant pinned here is that the operator is told, not that the money is rejected.
        var (reconciler, store, _, log) = CreateWithLog();
        Seed(store); // invoiced 100_000 msat = 100 sat

        var result = await reconciler.ApplyAsync(StoreId, Receive(amountSats: 40), Ct);

        Assert.Equal(InvoiceSettlementOutcome.Settled, result.Outcome);
        Assert.Contains("asked for", log.AllText);
        Assert.Contains("40", log.AllText);
    }

    [Fact]
    public async Task A_settlement_for_the_full_amount_is_not_flagged()
    {
        // The other half of the mutation: a warning that fires on every settlement tells an operator nothing.
        var (reconciler, store, _, log) = CreateWithLog();
        Seed(store);

        await reconciler.ApplyAsync(StoreId, Receive(amountSats: 100), Ct);

        Assert.DoesNotContain("asked for", log.AllText);
    }

    private static InvoiceRecord Seed(
        InMemoryInvoiceRecordStore store,
        string? hash = null,
        string? sdkPaymentId = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? expiresAt = null)
    {
        var record = new InvoiceRecord
        {
            PaymentHash = hash ?? Hash,
            StoreId = StoreId,
            Bolt11 = Bolt11,
            AmountMsat = 100_000,
            SdkPaymentId = sdkPaymentId,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow.AddMinutes(-5),
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddHours(1),
            Status = InvoiceRecordStatus.Unpaid
        };
        store.Seed(record);
        return record;
    }

    private static SparkPayment Receive(
        string? hash = null,
        string sdkPaymentId = "sdk-1",
        long amountSats = 100,
        SparkPaymentStatus status = SparkPaymentStatus.Completed,
        DateTimeOffset? timestamp = null) => new(
        sdkPaymentId,
        SparkPaymentDirection.Receive,
        status,
        SparkPaymentMethod.Lightning,
        amountSats,
        0,
        timestamp ?? DateTimeOffset.UtcNow,
        hash ?? Hash,
        Bolt11,
        PaymentFixture.Preimage,
        "order 42");

    [Fact]
    public async Task Applying_a_settlement_notifies_listeners_exactly_once()
    {
        var (reconciler, store, broadcaster) = Create();
        Seed(store);
        using var listener = broadcaster.Subscribe(StoreId);

        var first = await reconciler.ApplyAsync(StoreId, Receive(amountSats: 250), Ct);
        var second = await reconciler.ApplyAsync(StoreId, Receive(amountSats: 250), Ct);

        Assert.Equal(InvoiceSettlementOutcome.Settled, first.Outcome);
        Assert.Equal(InvoiceSettlementOutcome.AlreadySettled, second.Outcome);

        var notification = await listener.ReadAsync(Ct);
        Assert.Equal(Hash, notification.PaymentHash);
        Assert.Equal(250_000, notification.AmountReceivedMsat);

        // The duplicate must not wake the listener again — BTCPay would otherwise process the same settlement
        // twice for the same invoice.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await listener.ReadAsync(cts.Token));
    }

    [Fact]
    public async Task Applying_a_settlement_to_a_cancelled_invoice_credits_it_and_notifies_once()
    {
        var (reconciler, store, broadcaster) = Create();
        Seed(store);
        await store.CancelAsync(StoreId, Hash, Ct);
        using var listener = broadcaster.Subscribe(StoreId);

        var result = await reconciler.ApplyAsync(StoreId, Receive(), Ct);

        Assert.Equal(InvoiceSettlementOutcome.Settled, result.Outcome);
        Assert.NotNull(result.Record);
        Assert.Equal(InvoiceRecordStatus.Paid, result.Record.Status);
        Assert.Equal(100_000, result.Record.AmountReceivedMsat);

        // The notification is what lets BTCPay reach the invoice this bolt11 was minted for; it must fire
        // exactly once, exactly for this payment.
        var notification = await listener.ReadAsync(Ct);
        Assert.Equal(Hash, notification.PaymentHash);
        Assert.Equal(100_000, notification.AmountReceivedMsat);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await listener.ReadAsync(cts.Token));
    }

    [Fact]
    public async Task A_late_payment_of_a_superseded_invoice_credits_the_invoice_it_was_minted_for()
    {
        // The production shape of the finding: an X -> Y invoice replacement (sequential requests to a
        // public TopUp LNURL callback cancel the older bolt11), then a late completed payment of X. Spark
        // cannot withdraw X, so the payment settles anyway. It must credit X — the invoice whose payment
        // hash it carries — not the replacement, and Y must stay untouched.
        var (reconciler, store, broadcaster) = Create();
        var xHash = new string('a', 64);
        var yHash = new string('b', 64);
        Seed(store, hash: xHash);
        Seed(store, hash: yHash);

        // BTCPay replaces X with Y: the old bolt11 is cancelled locally, the new one offered.
        Assert.True(await store.CancelAsync(StoreId, xHash, Ct));
        Assert.Equal(InvoiceRecordStatus.Expired, store.Records[xHash].Status);
        Assert.Equal(InvoiceRecordStatus.Unpaid, store.Records[yHash].Status);

        using var listener = broadcaster.Subscribe(StoreId);
        var result = await reconciler.ApplyAsync(
            StoreId, Receive(hash: xHash, sdkPaymentId: "sdk-late", amountSats: 100), Ct);

        // X is credited with the received amount and the settlement is published for BTCPay.
        Assert.Equal(InvoiceSettlementOutcome.Settled, result.Outcome);
        Assert.Equal(xHash, result.Record!.PaymentHash);
        Assert.Equal(InvoiceRecordStatus.Paid, result.Record.Status);
        Assert.Equal(100_000, result.Record.AmountReceivedMsat);

        var notification = await listener.ReadAsync(Ct);
        Assert.Equal(xHash, notification.PaymentHash);
        Assert.Equal(100_000, notification.AmountReceivedMsat);

        // The replacement is untouched: nothing about X's late payment credits Y.
        Assert.Equal(InvoiceRecordStatus.Unpaid, store.Records[yHash].Status);
        Assert.Null(store.Records[yHash].AmountReceivedMsat);
    }

    [Fact]
    public async Task Applying_a_settlement_for_an_unknown_invoice_notifies_nobody()
    {
        var (reconciler, _, broadcaster) = Create();
        using var listener = broadcaster.Subscribe(StoreId);

        var result = await reconciler.ApplyAsync(StoreId, Receive(), Ct);

        Assert.Equal(InvoiceSettlementOutcome.NotFound, result.Outcome);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await listener.ReadAsync(cts.Token));
    }

    [Fact]
    public async Task Applying_rejects_a_send_or_a_payment_with_no_hash()
    {
        // Guard rails on the shared entry point: a send leg shares its hash with the receive leg but carries a
        // different amount, and a payment with no hash cannot belong to an invoice at all.
        var (reconciler, store, _) = Create();
        Seed(store);

        await Assert.ThrowsAsync<ArgumentException>(() => reconciler.ApplyAsync(
            StoreId, Receive() with { Direction = SparkPaymentDirection.Send }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => reconciler.ApplyAsync(
            StoreId, Receive() with { PaymentHash = null }, Ct));
    }

    [Fact]
    public async Task Finding_a_receive_uses_a_point_lookup_when_the_SDK_payment_id_is_known()
    {
        var (reconciler, store, _) = Create();
        var record = Seed(store, sdkPaymentId: "sdk-1");
        var sdk = new FakeSparkSdkClient().Seed(Receive());

        var found = await reconciler.FindReceiveAsync(sdk, record, Ct);

        Assert.NotNull(found);
        Assert.Equal("sdk-1", Assert.Single(sdk.GetPaymentCalls));
        Assert.Empty(sdk.ListQueries);
    }

    [Fact]
    public async Task Finding_a_receive_never_returns_a_send_leg()
    {
        // A self-payment produces a Receive leg and a Send leg sharing one payment hash, and the send leg's
        // amount is net of a fee the receive leg never paid. Crediting it would credit the wrong amount.
        var (reconciler, store, _) = Create();
        var record = Seed(store, sdkPaymentId: "send-leg");
        var sdk = new FakeSparkSdkClient().Seed(
            Receive(sdkPaymentId: "send-leg") with { Direction = SparkPaymentDirection.Send, AmountSats = 97 });

        // Falls through to the scan, which also filters by direction, and finds nothing creditable.
        Assert.Null(await reconciler.FindReceiveAsync(sdk, record, Ct));
    }

    [Fact]
    public async Task Finding_a_receive_pages_past_the_first_page_of_history()
    {
        // The regression this pins: a single newest-first page means a busy store's target payment is pushed
        // out of view and the invoice never settles. The fake honours Offset, so an unpaged scan fails here.
        var (reconciler, store, _) = Create();
        var record = Seed(store, createdAt: DateTimeOffset.UtcNow.AddHours(-1));
        var sdk = new FakeSparkSdkClient();
        sdk.Seed(Receive(timestamp: DateTimeOffset.UtcNow.AddMinutes(-50)));
        // 120 later receives, so the target sits on the third page of 50.
        for (var i = 0; i < 120; i++)
        {
            sdk.Seed(Receive(
                hash: i.ToString("x64"),
                sdkPaymentId: $"other-{i}",
                timestamp: DateTimeOffset.UtcNow.AddMinutes(-40 + i / 10.0)));
        }

        var found = await reconciler.FindReceiveAsync(sdk, record, Ct);

        Assert.NotNull(found);
        Assert.Equal(Hash, found.PaymentHash);
        Assert.True(sdk.ListQueries.Count > 1, "the scan must page rather than read one page");
        Assert.Equal([0, 50, 100], sdk.ListQueries.Select(q => q.Offset).ToArray());
    }

    [Fact]
    public async Task Finding_a_receive_stops_at_a_short_page()
    {
        var (reconciler, store, _) = Create();
        var record = Seed(store);
        var sdk = new FakeSparkSdkClient();

        Assert.Null(await reconciler.FindReceiveAsync(sdk, record, Ct));
        Assert.Single(sdk.ListQueries);
    }

    [Fact]
    public async Task Finding_a_receive_anchors_the_scan_to_the_invoice_creation_time()
    {
        // Anchoring is what keeps this bounded: the scan runs per pending invoice on every pass, so a window
        // that started at the beginning of history would grow into an O(all payments) walk.
        var (reconciler, store, _) = Create();
        var createdAt = DateTimeOffset.UtcNow.AddHours(-3);
        var record = Seed(store, createdAt: createdAt);
        var sdk = new FakeSparkSdkClient();

        await reconciler.FindReceiveAsync(sdk, record, Ct);

        var query = Assert.Single(sdk.ListQueries);
        Assert.Equal(SparkPaymentDirection.Receive, query.Direction);
        Assert.True(query.CompletedOnly);
        Assert.NotNull(query.From);
        Assert.True(query.From < createdAt, "the window must start before the invoice was created");
        Assert.True(query.From > createdAt.AddHours(-1), "the window must stay narrow");
        Assert.Equal(50, query.Limit);
    }

    [Fact]
    public async Task Finding_a_receive_reports_nothing_rather_than_throwing_when_the_SDK_fails()
    {
        // Throwing would abort a whole reconciliation pass, skipping every later invoice in the store.
        var (reconciler, store, _) = Create();
        var record = Seed(store);
        var sdk = new FakeSparkSdkClient { FailWith = new InvalidOperationException("SSP unreachable") };

        Assert.Null(await reconciler.FindReceiveAsync(sdk, record, Ct));
    }

    [Fact]
    public async Task Reconciling_a_store_settles_an_invoice_whose_completion_event_never_arrived()
    {
        var (reconciler, store, broadcaster) = Create();
        Seed(store, sdkPaymentId: "sdk-1");
        var sdk = new FakeSparkSdkClient().Seed(Receive(amountSats: 100));
        using var listener = broadcaster.Subscribe(StoreId);

        var settled = await reconciler.ReconcileStoreAsync(StoreId, sdk, Ct);

        Assert.Equal(1, settled);
        Assert.Equal(InvoiceRecordStatus.Paid, store.Records[Hash].Status);
        // And BTCPay is told, which is the whole point: the settlement is useless if nobody hears about it.
        Assert.Equal(Hash, (await listener.ReadAsync(Ct)).PaymentHash);
    }

    [Fact]
    public async Task Reconciling_a_store_leaves_an_unpaid_invoice_alone()
    {
        var (reconciler, store, _) = Create();
        Seed(store);

        Assert.Equal(0, await reconciler.ReconcileStoreAsync(StoreId, new FakeSparkSdkClient(), Ct));
        Assert.Equal(InvoiceRecordStatus.Unpaid, store.Records[Hash].Status);
    }

    [Fact]
    public async Task Reconciling_a_store_skips_paid_and_long_expired_but_rechecks_cancelled_invoices()
    {
        // Paid is terminal and a long-expired invoice is past the grace window (a deliberate bound). A
        // cancelled invoice, by contrast, is still payable on the service provider, so the walk re-examines
        // it within the same window as anything else — a late payment of a cancelled invoice is exactly what
        // the walk exists to catch when the SDK's completion event is dropped.
        var (reconciler, store, _) = Create();
        var expiredHash = new string('b', 64);
        var cancelledHash = new string('c', 64);
        Seed(store, expiredHash,
            createdAt: DateTimeOffset.UtcNow.AddDays(-2), expiresAt: DateTimeOffset.UtcNow.AddDays(-1));
        Seed(store, cancelledHash);
        await store.CancelAsync(StoreId, cancelledHash, Ct);
        var paidHash = new string('d', 64);
        Seed(store, paidHash);
        await store.SettleAsync(StoreId, paidHash, "sdk-x", 100_000, null, DateTimeOffset.UtcNow, Ct);

        var sdk = new FakeSparkSdkClient();
        var settled = await reconciler.ReconcileStoreAsync(StoreId, sdk, Ct);

        Assert.Equal(0, settled);
        // Only the cancelled invoice was asked about; paid and long-expired did not even produce a query.
        var query = Assert.Single(sdk.ListQueries);
        Assert.True(query.From < DateTimeOffset.UtcNow, "the single query should be the cancelled invoice's scan");
        Assert.Empty(sdk.GetPaymentCalls);
    }

    [Fact]
    public async Task Reconciling_a_store_continues_past_one_invoice_that_throws()
    {
        // Injected at the store, not at the SDK: FindReceiveAsync catches everything the SDK throws by design,
        // so an SDK-level failure never reaches the per-invoice catch this test is about.
        var (reconciler, store, _) = Create();
        var brokenHash = Hash;
        var goodHash = PaymentFixture.OtherPaymentHash;
        Seed(store, brokenHash, sdkPaymentId: "sdk-broken",
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-30));
        Seed(store, goodHash, sdkPaymentId: "sdk-good",
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        store.FailSettleFor.Add(brokenHash);

        var sdk = new FakeSparkSdkClient();
        sdk.Seed(Receive(hash: brokenHash, sdkPaymentId: "sdk-broken"));
        sdk.Seed(Receive(hash: goodHash, sdkPaymentId: "sdk-good"));

        var settled = await reconciler.ReconcileStoreAsync(StoreId, sdk, Ct);

        // The broken invoice is oldest, so it is examined first — and the walk still reaches the second.
        Assert.Equal(1, settled);
        Assert.Equal(InvoiceRecordStatus.Paid, store.Records[goodHash].Status);
        Assert.Equal(InvoiceRecordStatus.Unpaid, store.Records[brokenHash].Status);
    }

    [Fact]
    public async Task Reconciling_a_store_walks_past_the_first_page()
    {
        // With a page size of 100 and a per-pass cap of 1000, a store holding more than one page must still have
        // every invoice examined — the earlier single-page implementation silently ignored the rest.
        var (reconciler, store, _) = Create();
        var sdk = new FakeSparkSdkClient();
        for (var i = 0; i < 130; i++)
        {
            var hash = i.ToString("x64");
            Seed(store, hash, sdkPaymentId: $"sdk-{i}",
                createdAt: DateTimeOffset.UtcNow.AddMinutes(-200 + i));
            sdk.Seed(Receive(hash: hash, sdkPaymentId: $"sdk-{i}"));
        }

        var settled = await reconciler.ReconcileStoreAsync(StoreId, sdk, Ct);

        Assert.Equal(130, settled);
    }

    /// <summary>
    /// Seeds a store with one record more than the per-pass cap (1000), every record a permanent miss —
    /// looked up, no payment behind the id — so only the cap itself can stop a pass over it — and runs one
    /// capped pass. Expiries ascend with the record index and creation times descend with it, so the two
    /// candidate orderings visit these records in opposite orders: a walk paging by expiry and a walk paging
    /// by creation cannot be confused for each other, and neither can their cursors.
    /// </summary>
    private static async Task<(SparkSettlementReconciler Reconciler, InMemoryInvoiceRecordStore Store,
        FakeSparkSdkClient Sdk, CapturingLogger<SparkSettlementReconciler> Log, InvoiceRecord StoppedAt)>
        ACappedPassOverAManufacturedBacklog()
    {
        var (reconciler, store, _, log) = CreateWithLog();
        var sdk = new FakeSparkSdkClient();
        var expiryBase = DateTimeOffset.UtcNow.AddHours(1);
        var createdBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        for (var i = 0; i < 1001; i++)
        {
            Seed(store, i.ToString("x64"), sdkPaymentId: $"sdk-{i}",
                createdAt: createdBase.AddMinutes(-i),
                expiresAt: expiryBase.AddMinutes(i));
        }

        // Page size 100 × cap 1000: the pass examines records 0..999 in expiry order and stops with one
        // record still unexamined.
        Assert.Equal(0, await reconciler.ReconcileStoreAsync(StoreId, sdk, Ct));
        Assert.Contains("per-pass limit", log.AllText);
        return (reconciler, store, sdk, log, store.Records[999.ToString("x64")]);
    }

    [Fact]
    public async Task The_resume_cursor_handed_across_passes_carries_expiry_and_hash()
    {
        // The resume position has to name the walk's own ordering columns — expiry, then hash. A creation-time
        // cursor would point the next pass at the wrong row in exactly the backlog case the resume exists for.
        var (reconciler, store, sdk, _, stoppedAt) = await ACappedPassOverAManufacturedBacklog();

        Assert.Equal(0, await reconciler.ReconcileStoreAsync(StoreId, sdk, Ct));

        // Pass 1 made ten list requests (cursors 0..9, the first null); pass 2's first request is the
        // eleventh, and it carries the record the capped pass stopped on — by expiry, not by creation.
        var resumed = Assert.IsType<InvoiceSettlementCursor>(store.ReconciliationCursors[10]);
        Assert.Equal(new InvoiceSettlementCursor(stoppedAt.ExpiresAt, stoppedAt.PaymentHash), resumed);
        Assert.NotEqual(stoppedAt.CreatedAt, resumed.ExpiresAt);
    }

    [Fact]
    public async Task The_next_pass_resumes_past_the_capped_prefix_instead_of_restarting_it()
    {
        // The starvation property, end to end: a store whose settleable set outgrows the cap must have every
        // record reached within a bounded number of passes. The first thousand permanent misses are the
        // abandoned prefix; the next pass must walk past them straight to the one record it never saw,
        // not re-examine the prefix.
        var (reconciler, store, sdk, _, _) = await ACappedPassOverAManufacturedBacklog();

        Assert.Equal(0, await reconciler.ReconcileStoreAsync(StoreId, sdk, Ct));

        // Exactly one more record was looked up across the two passes — the last-expiring one — and pass 2
        // asked for exactly one page to find it.
        Assert.Equal(1001, sdk.GetPaymentCalls.Count);
        Assert.Equal("sdk-1000", sdk.GetPaymentCalls[^1]);
        Assert.Equal(11, store.ReconciliationCursors.Count);
    }

    [Fact]
    public async Task Reconciling_a_store_includes_a_recently_expired_invoice()
    {
        // The capability the computed-expiry design exists for: the service provider accepts a late payment and
        // Spark cannot stop it, so an invoice that expired minutes ago can still take real money.
        var (reconciler, store, _) = Create();
        Seed(store, sdkPaymentId: "sdk-1",
            createdAt: DateTimeOffset.UtcNow.AddHours(-1),
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        var sdk = new FakeSparkSdkClient().Seed(Receive());

        Assert.Equal(1, await reconciler.ReconcileStoreAsync(StoreId, sdk, Ct));
        Assert.Equal(InvoiceRecordStatus.Paid, store.Records[Hash].Status);
    }

    [Fact]
    public async Task Reconciling_a_store_gives_up_on_a_long_expired_invoice()
    {
        // The bound on the above. Beyond the grace window the invoice is no longer re-checked, which is a
        // deliberate trade against rescanning every invoice ever created on every pass.
        var (reconciler, store, _) = Create();
        Seed(store, sdkPaymentId: "sdk-1",
            createdAt: DateTimeOffset.UtcNow.AddDays(-2),
            expiresAt: DateTimeOffset.UtcNow.AddDays(-1));
        var sdk = new FakeSparkSdkClient().Seed(Receive());

        Assert.Equal(0, await reconciler.ReconcileStoreAsync(StoreId, sdk, Ct));
        Assert.Empty(sdk.GetPaymentCalls);
    }

    [Fact]
    public async Task Reconciling_a_store_scans_shared_history_once_for_many_unpaid_invoices()
    {
        // The cost the shared sweep exists to remove: five unpaid invoices with no recorded id used to mean
        // five full history scans per pass, every minute, on a store with no incoming traffic whatsoever.
        // One drained shared scan now answers all five with a single query.
        var (reconciler, store, _) = Create();
        for (var i = 0; i < 5; i++)
            Seed(store, hash: i.ToString("x64"));

        var sdk = new FakeSparkSdkClient();
        var settled = await reconciler.ReconcileStoreAsync(StoreId, sdk, Ct);

        Assert.Equal(0, settled);
        Assert.Single(sdk.ListQueries);
    }

    [Fact]
    public async Task Reconciling_a_store_settles_from_the_shared_history_page_without_another_scan()
    {
        // The payment arrived inside the one page the shared sweep fetched. Neither the hit nor the two
        // drained misses may spend a further query — the whole batching win, pinned.
        var (reconciler, store, _) = Create();
        var first = new string('a', 64);
        var second = new string('b', 64);
        var third = new string('c', 64);
        Seed(store, first);
        Seed(store, second);
        Seed(store, third);
        var sdk = new FakeSparkSdkClient().Seed(Receive(hash: second));

        var settled = await reconciler.ReconcileStoreAsync(StoreId, sdk, Ct);

        Assert.Equal(1, settled);
        Assert.Equal(InvoiceRecordStatus.Paid, store.Records[second].Status);
        Assert.Equal(InvoiceRecordStatus.Unpaid, store.Records[first].Status);
        Assert.Equal(InvoiceRecordStatus.Unpaid, store.Records[third].Status);
        Assert.Single(sdk.ListQueries);
        // The shared query must keep the exact filter shape the per-record scan used, or the sweep could
        // quietly start settling on rows the old path would have ignored.
        Assert.True(sdk.ListQueries.All(q =>
            q.CompletedOnly && q.Direction == SparkPaymentDirection.Receive));
    }

    [Fact]
    public async Task Reconciling_a_store_falls_back_to_a_per_invoice_scan_when_the_shared_scan_caps_out()
    {
        // A capped shared scan is not evidence of absence. The page holds an old invoice (which anchors the
        // shared window) and a newer one whose payment the shared window buries: the fake orders history by
        // insertion, so seeding the target FIRST under 500 receives that live inside the shared window but
        // before the target invoice's own, plus 60 inside both windows, puts the target at position 560 of
        // the newest-first shared scan — past the 500-payment cap, so the sweep returns undrained and the
        // target must not be judged by its miss. The target's own scan, anchored to its own creation time,
        // cuts those 500 out of its window entirely and finds the payment on its second page.
        var (reconciler, store, _) = Create();
        var olderHash = new string('a', 64);
        var targetHash = new string('b', 64);
        Seed(store, olderHash, createdAt: DateTimeOffset.UtcNow.AddHours(-3));
        Seed(store, targetHash, createdAt: DateTimeOffset.UtcNow.AddMinutes(-30));

        var sdk = new FakeSparkSdkClient();
        sdk.Seed(Receive(hash: targetHash, sdkPaymentId: "target",
            timestamp: DateTimeOffset.UtcNow.AddMinutes(-25)));
        for (var i = 0; i < 500; i++)
        {
            sdk.Seed(Receive(
                hash: i.ToString("x64"),
                sdkPaymentId: $"filler-old-{i}",
                timestamp: DateTimeOffset.UtcNow.AddMinutes(-180)));
        }
        for (var i = 0; i < 60; i++)
        {
            sdk.Seed(Receive(
                hash: (1000 + i).ToString("x64"),
                sdkPaymentId: $"filler-new-{i}",
                timestamp: DateTimeOffset.UtcNow.AddMinutes(-35 - i / 10.0)));
        }

        var settled = await reconciler.ReconcileStoreAsync(StoreId, sdk, Ct);

        Assert.Equal(1, settled);
        Assert.Equal(InvoiceRecordStatus.Paid, store.Records[targetHash].Status);
        Assert.Equal(InvoiceRecordStatus.Unpaid, store.Records[olderHash].Status);
        // The sweep's ten capped pages alone already fill MaxPaymentPages; anything more is proof the
        // undrained miss was not taken as absence and the per-invoice fallback ran.
        Assert.True(sdk.ListQueries.Count > 10,
            $"the capped sweep must not silence the per-invoice fallback (got {sdk.ListQueries.Count})");
    }

    [Fact]
    public async Task Reconciling_a_store_skips_the_shared_scan_when_every_invoice_has_an_SDK_payment_id()
    {
        // The shared scan exists only for records the point lookup cannot serve. Running it anyway would be
        // the batching spending money it saves nobody.
        var (reconciler, store, _) = Create();
        var first = new string('a', 64);
        var second = new string('b', 64);
        Seed(store, first, sdkPaymentId: "sdk-a");
        Seed(store, second, sdkPaymentId: "sdk-b");
        var sdk = new FakeSparkSdkClient();
        sdk.Seed(Receive(hash: first, sdkPaymentId: "sdk-a"));
        sdk.Seed(Receive(hash: second, sdkPaymentId: "sdk-b"));

        var settled = await reconciler.ReconcileStoreAsync(StoreId, sdk, Ct);

        Assert.Equal(2, settled);
        Assert.Empty(sdk.ListQueries);
        Assert.Equal(2, sdk.GetPaymentCalls.Count);
    }

    [Fact]
    public async Task Finding_a_receive_falls_back_to_a_scan_when_the_recorded_id_resolves_to_nothing()
    {
        // The recorded id came from a PaymentPending event. If the SDK has since replaced or re-keyed that row,
        // treating the miss as "not paid" would make the invoice permanently unresolvable — a dead end for an
        // invoice that may hold real money.
        var (reconciler, store, _) = Create();
        var record = Seed(store, sdkPaymentId: "stale-id");
        var sdk = new FakeSparkSdkClient().Seed(Receive(sdkPaymentId: "real-id"));

        var found = await reconciler.FindReceiveAsync(sdk, record, Ct);

        Assert.NotNull(found);
        Assert.Equal("real-id", found.SdkPaymentId);
        Assert.NotEmpty(sdk.ListQueries);
    }

    [Fact]
    public async Task Finding_a_receive_falls_back_to_a_scan_when_the_recorded_id_names_a_send()
    {
        var (reconciler, store, _) = Create();
        var record = Seed(store, sdkPaymentId: "send-leg");
        var sdk = new FakeSparkSdkClient();
        sdk.Seed(Receive(sdkPaymentId: "send-leg") with { Direction = SparkPaymentDirection.Send });
        sdk.Seed(Receive(sdkPaymentId: "receive-leg"));

        var found = await reconciler.FindReceiveAsync(sdk, record, Ct);

        Assert.NotNull(found);
        Assert.Equal("receive-leg", found.SdkPaymentId);
    }

    [Fact]
    public async Task Reconciling_a_store_with_no_pending_invoices_does_nothing()
    {
        var (reconciler, _, _) = Create();
        var sdk = new FakeSparkSdkClient();

        Assert.Equal(0, await reconciler.ReconcileStoreAsync(StoreId, sdk, Ct));
        Assert.Empty(sdk.ListQueries);
    }

    [Fact]
    public async Task Resolving_an_already_paid_record_makes_no_SDK_call()
    {
        var (reconciler, store, _) = Create();
        var record = Seed(store);
        await store.SettleAsync(StoreId, Hash, "sdk-1", 100_000, null, DateTimeOffset.UtcNow, Ct);
        var sdk = new FakeSparkSdkClient();

        var resolved = await reconciler.ResolveAsync(sdk, record, Ct);

        Assert.Equal(InvoiceRecordStatus.Paid, resolved.Status);
        Assert.Empty(sdk.ListQueries);
        Assert.Empty(sdk.GetPaymentCalls);
    }

    // -------------------------------------------------------------------------------------------------------
    // Crediting: settling records that money arrived, which is not the same as BTCPay knowing about it
    // -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_settlement_is_routed_to_its_BTCPay_invoice_as_well_as_announced()
    {
        // Both, not either. The announcement only reaches a listener that is still watching this BOLT11, and
        // for a superseded one after a restart there is none — so the credit is not an alternative path, it is
        // the one that does not depend on who is listening.
        var (reconciler, store, broadcaster, _, credits) = CreateWithCredits();
        Seed(store);
        credits.Mint(Hash, "btcpay-invoice-1", StoreId);
        using var subscription = broadcaster.Subscribe(StoreId);

        await reconciler.ApplyAsync(StoreId, Receive(), Ct);

        var announced = await subscription.ReadAsync(Ct);
        Assert.Equal(Hash, announced.PaymentHash);
        var credit = Assert.Single(credits.Credits);
        Assert.Equal("btcpay-invoice-1", credit.InvoiceId);
        Assert.NotNull(store.Records[Hash].CreditedAt);
    }

    [Fact]
    public async Task A_credit_that_fails_leaves_the_settlement_intact_and_is_retried_by_the_next_pass()
    {
        // The ordering guarantee. By the time the credit is attempted the settlement is committed and the
        // notification published, so a BTCPay database that is briefly unreachable must cost a pass and not a
        // settlement — and the pass afterwards has to pick it up.
        var (reconciler, store, _, _, credits) = CreateWithCredits();
        Seed(store);
        credits.Mint(Hash, "btcpay-invoice-1", StoreId);
        credits.FailWith = new InvalidOperationException("BTCPay's database is unreachable");

        var result = await reconciler.ApplyAsync(StoreId, Receive(), Ct);

        Assert.Equal(InvoiceSettlementOutcome.Settled, result.Outcome);
        Assert.Equal(InvoiceRecordStatus.Paid, store.Records[Hash].Status);
        Assert.Null(store.Records[Hash].CreditedAt);

        credits.FailWith = null;
        Assert.Equal(1, await reconciler.CreditStoreAsync(StoreId, Ct));

        Assert.Equal(Hash, Assert.Single(credits.Credits).PaymentHash);
        Assert.NotNull(store.Records[Hash].CreditedAt);
    }

    [Fact]
    public async Task A_duplicate_settlement_event_retries_a_credit_that_had_not_landed()
    {
        // The SDK does duplicate its events, and a duplicate is the cheapest retry available — no waiting for
        // the next pass. It must not, however, credit an already-credited settlement a second time.
        var (reconciler, store, _, _, credits) = CreateWithCredits();
        Seed(store);
        credits.Mint(Hash, "btcpay-invoice-1", StoreId);
        credits.FailWith = new InvalidOperationException("BTCPay's database is unreachable");
        await reconciler.ApplyAsync(StoreId, Receive(), Ct);
        credits.FailWith = null;

        var second = await reconciler.ApplyAsync(StoreId, Receive(), Ct);
        Assert.Equal(InvoiceSettlementOutcome.AlreadySettled, second.Outcome);
        Assert.Single(credits.Credits);

        // A third event finds the credit done and does not even ask.
        var lookups = credits.Lookups;
        await reconciler.ApplyAsync(StoreId, Receive(), Ct);
        Assert.Equal(lookups, credits.Lookups);
        Assert.Single(credits.Credits);
    }

    [Fact]
    public async Task A_reconciliation_pass_credits_a_settlement_BTCPay_never_heard_about()
    {
        // The restart case, at the reconciler's own altitude: a paid row whose credit never landed, and whose
        // invoice has since expired — so the settlement walk skips it and only the credit walk reaches it.
        var (reconciler, store, _, _, credits) = CreateWithCredits();
        Seed(store, createdAt: DateTimeOffset.UtcNow.AddHours(-5),
            expiresAt: DateTimeOffset.UtcNow.AddHours(-4));
        await store.SettleAsync(StoreId, Hash, "sdk-1", 100_000, null, DateTimeOffset.UtcNow, Ct);
        credits.Mint(Hash, "btcpay-invoice-1", StoreId);

        Assert.Equal(0, await reconciler.ReconcileStoreAsync(StoreId, new FakeSparkSdkClient(), Ct));

        Assert.Equal(Hash, Assert.Single(credits.Credits).PaymentHash);
        Assert.NotNull(store.Records[Hash].CreditedAt);
    }

    [Fact]
    public async Task A_reconciliation_pass_credits_each_settlement_at_most_once()
    {
        // Idempotence across passes, which is what a timer makes unavoidable: the pass runs every minute for
        // the life of the server.
        var (reconciler, store, _, _, credits) = CreateWithCredits();
        Seed(store);
        await store.SettleAsync(StoreId, Hash, "sdk-1", 100_000, null, DateTimeOffset.UtcNow, Ct);
        credits.Mint(Hash, "btcpay-invoice-1", StoreId);

        Assert.Equal(1, await reconciler.CreditStoreAsync(StoreId, Ct));
        Assert.Equal(0, await reconciler.CreditStoreAsync(StoreId, Ct));
        Assert.Equal(0, await reconciler.CreditStoreAsync(StoreId, Ct));

        Assert.Single(credits.Credits);
    }

    [Fact]
    public async Task A_credit_pass_does_not_touch_another_stores_settlements()
    {
        var (reconciler, store, _, _, credits) = CreateWithCredits();
        Seed(store);
        await store.SettleAsync(StoreId, Hash, "sdk-1", 100_000, null, DateTimeOffset.UtcNow, Ct);
        credits.Mint(Hash, "btcpay-invoice-1", StoreId);

        Assert.Equal(0, await reconciler.CreditStoreAsync("some-other-store", Ct));
        Assert.Empty(credits.Credits);
        Assert.Null(store.Records[Hash].CreditedAt);
    }

    [Fact]
    public async Task A_settlement_that_can_never_be_credited_is_reported_by_the_walk_exactly_once()
    {
        // The defect this pins is a silence, which is the hardest kind to notice. The walk's listing bound and
        // the creditor's retry horizon were the same value, so a settlement crossed out of the listing at the
        // exact instant it became eligible to be reported: the operator warning was unreachable from the pass,
        // the money stayed unaccounted for, and the row was indistinguishable from one still in flight.
        //
        // Driven through the walk and not by calling the creditor directly — deliberately. The creditor's own
        // test proves the classification; only the walk can prove the record is *reachable* to be classified.
        var (reconciler, store, credits, creditLog) = CreateWithCreditLog();
        var settledAt = DateTimeOffset.UtcNow - SparkInvoiceCreditor.CreditRetryHorizon - TimeSpan.FromDays(1);
        Seed(store, createdAt: settledAt.AddMinutes(-5), expiresAt: settledAt.AddHours(1));
        await store.SettleAsync(StoreId, Hash, "sdk-1", 100_000, PaymentFixture.Preimage, settledAt, Ct);
        // No Mint: BTCPay has no invoice for this hash and never will, which is what a BOLT11 minted through
        // this plugin's own Greenfield endpoints looks like from here.

        Assert.Equal(0, await reconciler.CreditStoreAsync(StoreId, Ct));

        // Reported, at operator level, with the amount and the hash a human needs to trace the money.
        var reports = creditLog.Lines.Where(l => l.Contains("no longer be retried")).ToList();
        Assert.Single(reports);
        Assert.StartsWith("Warning:", reports[0]);
        Assert.Contains(Hash, reports[0]);
        Assert.Contains("100 sat", reports[0]);

        // Recorded as abandoned rather than credited: the row must not claim the merchant was paid.
        Assert.Null(store.Records[Hash].CreditedAt);
        Assert.NotNull(store.Records[Hash].CreditAbandonedAt);

        // And that is the end of it — no further attempt, and above all no second report, however many passes
        // run afterwards.
        var lookups = credits.Lookups;
        Assert.Equal(0, await reconciler.CreditStoreAsync(StoreId, Ct));
        Assert.Equal(0, await reconciler.CreditStoreAsync(StoreId, Ct));

        Assert.Equal(lookups, credits.Lookups);
        Assert.Single(creditLog.Lines.Where(l => l.Contains("no longer be retried")));
    }

    [Fact]
    public async Task A_settlement_still_inside_the_retry_horizon_is_retried_rather_than_reported()
    {
        // The other side of the boundary, and the reason the report cannot simply be moved earlier: BTCPay
        // writes its payment-hash row just after CreateInvoice returns, so a payment landing in that window
        // finds nothing. An operator-level line there would fire on ordinary checkouts.
        var (reconciler, store, _, creditLog) = CreateWithCreditLog();
        Seed(store);
        await store.SettleAsync(StoreId, Hash, "sdk-1", 100_000, null, DateTimeOffset.UtcNow, Ct);

        Assert.Equal(0, await reconciler.CreditStoreAsync(StoreId, Ct));

        Assert.DoesNotContain("no longer be retried", creditLog.AllText);
        Assert.Null(store.Records[Hash].CreditAbandonedAt);
        // Still in the queue, so the pass after BTCPay catches up credits it.
        Assert.Null(store.Records[Hash].CreditedAt);
    }

    [Fact]
    public async Task A_failed_settlement_listing_does_not_skip_the_credit_walk()
    {
        // The two walks read different queries of the same table and neither is a precondition of the other.
        // The credit walk used to run last and inside the settlement walk's control flow, so a failure loading
        // the settlement page returned before reaching it — on exactly the store whose invoices are in trouble
        // and therefore most likely to be holding uncredited money.
        var (reconciler, store, credits, _) = CreateWithCreditLog();
        Seed(store);
        await store.SettleAsync(StoreId, Hash, "sdk-1", 100_000, null, DateTimeOffset.UtcNow, Ct);
        credits.Mint(Hash, "btcpay-invoice-1", StoreId);
        store.FailReconciliationListWith = new InvalidOperationException("the invoice listing is unavailable");

        Assert.Equal(0, await reconciler.ReconcileStoreAsync(StoreId, new FakeSparkSdkClient(), Ct));

        Assert.Equal(Hash, Assert.Single(credits.Credits).PaymentHash);
        Assert.NotNull(store.Records[Hash].CreditedAt);
    }

    /// <summary>
    /// Decorator that fails one specific point lookup and delegates everything else.
    /// </summary>
    /// <remarks>
    /// A decorator rather than a subclass: the reconciler holds the interface, so a <c>new</c>-hiding override
    /// would never be dispatched to and the test would silently prove nothing.
    /// </remarks>
    private sealed class FailingLookupSdkClient : ISparkSdkClient
    {
        private readonly ISparkSdkClient _inner;
        private readonly string _failingId;

        public FailingLookupSdkClient(ISparkSdkClient inner, string failingId)
        {
            _inner = inner;
            _failingId = failingId;
        }

        public Task<SparkPayment?> GetPaymentAsync(
            string sdkPaymentId,
            CancellationToken cancellationToken = default) =>
            sdkPaymentId == _failingId
                ? throw new InvalidOperationException("boom")
                : _inner.GetPaymentAsync(sdkPaymentId, cancellationToken);

        public Task<SparkNodeInfo> GetInfoAsync(bool ensureSynced, CancellationToken cancellationToken = default) =>
            _inner.GetInfoAsync(ensureSynced, cancellationToken);

        public Task SyncWalletAsync(CancellationToken cancellationToken = default) =>
            _inner.SyncWalletAsync(cancellationToken);

        public Task<SparkReceiveResult> ReceiveBolt11Async(
            string description,
            long? amountSats,
            uint expirySecs,
            CancellationToken cancellationToken = default) =>
            _inner.ReceiveBolt11Async(description, amountSats, expirySecs, cancellationToken);

        public Task<IReadOnlyList<SparkPayment>> ListPaymentsAsync(
            SparkListPaymentsQuery query,
            CancellationToken cancellationToken = default) =>
            _inner.ListPaymentsAsync(query, cancellationToken);

        public Task<SparkSendResult> SendBolt11Async(
            string bolt11,
            long? amountSats,
            string idempotencyKey,
            Func<SparkSendQuote, string?> approveQuote,
            TimeSpan? completionTimeout,
            CancellationToken cancellationToken = default) =>
            _inner.SendBolt11Async(
                bolt11, amountSats, idempotencyKey, approveQuote, completionTimeout, cancellationToken);

        public Task<SparkOnchainFeeQuote> QuoteOnchainSendAsync(
            string address,
            long amountSats,
            bool feesIncluded,
            CancellationToken cancellationToken = default) =>
            _inner.QuoteOnchainSendAsync(address, amountSats, feesIncluded, cancellationToken);

        public Task<SparkOnchainSendResult> SendToBitcoinAddressAsync(
            string address,
            long amountSats,
            SparkOnchainSpeed speed,
            bool feesIncluded,
            string idempotencyKey,
            Func<SparkOnchainQuote, string?> approveQuote,
            CancellationToken cancellationToken = default) =>
            _inner.SendToBitcoinAddressAsync(
                address, amountSats, speed, feesIncluded, idempotencyKey, approveQuote, cancellationToken);

        // The post-MVP surface is delegated wholesale. This decorator exists to fail one payment lookup; every
        // other method must behave exactly as the fake does, or the test would be measuring the decorator.
        public Task<string> GetBitcoinDepositAddressAsync(CancellationToken cancellationToken = default) =>
            _inner.GetBitcoinDepositAddressAsync(cancellationToken);

        public Task<IReadOnlyList<SparkDepositInfo>> ListUnclaimedDepositsAsync(
            CancellationToken cancellationToken = default) =>
            _inner.ListUnclaimedDepositsAsync(cancellationToken);

        public Task<SparkClaimDepositResult> ClaimDepositAsync(
            string txId,
            uint vout,
            SparkMaxFee maxFee,
            CancellationToken cancellationToken = default) =>
            _inner.ClaimDepositAsync(txId, vout, maxFee, cancellationToken);

        public Task<SparkRecommendedFees> GetRecommendedFeesAsync(CancellationToken cancellationToken = default) =>
            _inner.GetRecommendedFeesAsync(cancellationToken);

        public Task<SparkUserSettings> GetUserSettingsAsync(CancellationToken cancellationToken = default) =>
            _inner.GetUserSettingsAsync(cancellationToken);

        public Task SetStableBalanceActiveAsync(
            bool activate,
            string? label,
            CancellationToken cancellationToken = default) =>
            _inner.SetStableBalanceActiveAsync(activate, label, cancellationToken);

        public Task<SparkConversionLimits> FetchConversionLimitsAsync(
            SparkConversionDirection direction,
            SparkTokenIdentifier token,
            CancellationToken cancellationToken = default) =>
            _inner.FetchConversionLimitsAsync(direction, token, cancellationToken);

        public Task RefundPendingConversionsAsync(CancellationToken cancellationToken = default) =>
            _inner.RefundPendingConversionsAsync(cancellationToken);

        public Task<IReadOnlyList<SparkCrossChainRoute>> GetCrossChainRoutesAsync(
            string address,
            CancellationToken cancellationToken = default) =>
            _inner.GetCrossChainRoutesAsync(address, cancellationToken);

        public Task<SparkCrossChainQuote> QuoteCrossChainAsync(
            SparkCrossChainRoute route,
            string recipientAddress,
            SparkSendAmount amount,
            uint? maxSlippageBps,
            CancellationToken cancellationToken = default) =>
            _inner.QuoteCrossChainAsync(route, recipientAddress, amount, maxSlippageBps, cancellationToken);

        public Task<SparkCrossChainSendResult> SendCrossChainAsync(
            SparkCrossChainRoute route,
            string recipientAddress,
            SparkSendAmount amount,
            uint? maxSlippageBps,
            string? idempotencyKey,
            Func<SparkCrossChainQuote, Task<string?>> approveQuote,
            CancellationToken cancellationToken = default) =>
            _inner.SendCrossChainAsync(
                route, recipientAddress, amount, maxSlippageBps, idempotencyKey, approveQuote, cancellationToken);

        public Task<IReadOnlyList<SparkCrossChainReceiveRoute>> GetCrossChainReceiveRoutesAsync(
            CancellationToken cancellationToken = default) =>
            _inner.GetCrossChainReceiveRoutesAsync(cancellationToken);

        public Task<SparkCrossChainReceiveQuote> ReceiveCrossChainAsync(
            SparkCrossChainReceiveRoute route,
            BigInteger amount,
            uint maxSlippageBps,
            CancellationToken cancellationToken = default) =>
            _inner.ReceiveCrossChainAsync(route, amount, maxSlippageBps, cancellationToken);

        public Task DisconnectAsync() => _inner.DisconnectAsync();

        public void Dispose() => _inner.Dispose();
    }
}

/// <summary>
/// Cross-store isolation for a reconciliation pass.
/// </summary>
/// <remarks>
/// Split from the per-store tests because it is the property most likely to rot silently: one store's broken
/// wallet must never stop the others from settling, and a server with several stores is the normal case.
/// </remarks>
public class SparkReconcileStoresTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (SparkSettlementReconciler Reconciler, InMemoryInvoiceRecordStore Store) Create()
    {
        var (reconciler, store, _) = CreateWithCredits();
        return (reconciler, store);
    }

    private static (SparkSettlementReconciler Reconciler, InMemoryInvoiceRecordStore Store,
        FakeInvoiceCreditGateway Credits) CreateWithCredits()
    {
        var store = new InMemoryInvoiceRecordStore();
        var credits = new FakeInvoiceCreditGateway();
        return (
            new SparkSettlementReconciler(
                store,
                new SparkSettlementBroadcaster(NullLogger<SparkSettlementBroadcaster>.Instance),
                new SparkInvoiceCreditor(credits, store, NullLogger<SparkInvoiceCreditor>.Instance),
                NullLogger<SparkSettlementReconciler>.Instance),
            store,
            credits);
    }

    private static FakeSparkSdkClient SeedStore(
        InMemoryInvoiceRecordStore store,
        string storeId,
        string paymentHash)
    {
        store.Seed(new InvoiceRecord
        {
            PaymentHash = paymentHash,
            StoreId = storeId,
            Bolt11 = "lnbcrt-one",
            AmountMsat = 100_000,
            SdkPaymentId = $"sdk-{storeId}",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Status = InvoiceRecordStatus.Unpaid
        });

        return new FakeSparkSdkClient().Seed(new SparkPayment(
            $"sdk-{storeId}",
            SparkPaymentDirection.Receive,
            SparkPaymentStatus.Completed,
            SparkPaymentMethod.Lightning,
            100,
            0,
            DateTimeOffset.UtcNow,
            paymentHash,
            "lnbcrt-one",
            PaymentFixture.Preimage,
            null));
    }

    [Fact]
    public async Task Every_store_is_reconciled_and_the_totals_add_up()
    {
        var (reconciler, store) = Create();
        var first = SeedStore(store, "store-1", PaymentFixture.PaymentHash);
        var second = SeedStore(store, "store-2", PaymentFixture.OtherPaymentHash);

        var settled = await reconciler.ReconcileStoresAsync(
            [new SparkReconciliationTarget("store-1", first), new SparkReconciliationTarget("store-2", second)],
            TestPasses.Reconciliation(), Ct);

        Assert.Equal(2, settled);
        Assert.Equal(InvoiceRecordStatus.Paid, store.Records[PaymentFixture.PaymentHash].Status);
        Assert.Equal(InvoiceRecordStatus.Paid, store.Records[PaymentFixture.OtherPaymentHash].Status);
    }

    [Fact]
    public async Task One_stores_failure_does_not_skip_the_others()
    {
        var (reconciler, store) = Create();
        var broken = SeedStore(store, "store-1", PaymentFixture.PaymentHash);
        broken.FailWith = new InvalidOperationException("this wallet is broken");
        var healthy = SeedStore(store, "store-2", PaymentFixture.OtherPaymentHash);
        store.FailSettleFor.Add(PaymentFixture.PaymentHash);

        var settled = await reconciler.ReconcileStoresAsync(
            [new SparkReconciliationTarget("store-1", broken), new SparkReconciliationTarget("store-2", healthy)],
            TestPasses.Reconciliation(), Ct);

        Assert.Equal(1, settled);
        Assert.Equal(InvoiceRecordStatus.Unpaid, store.Records[PaymentFixture.PaymentHash].Status);
        Assert.Equal(InvoiceRecordStatus.Paid, store.Records[PaymentFixture.OtherPaymentHash].Status);
    }

    [Fact]
    public async Task A_disposed_wallet_is_tolerated_and_the_others_still_settle()
    {
        // What a store being reconfigured mid-pass looks like from here.
        var (reconciler, store) = Create();
        var goneAway = SeedStore(store, "store-1", PaymentFixture.PaymentHash);
        goneAway.FailWith = new ObjectDisposedException("BreezSdk");
        store.FailSettleFor.Add(PaymentFixture.PaymentHash);
        var healthy = SeedStore(store, "store-2", PaymentFixture.OtherPaymentHash);

        var settled = await reconciler.ReconcileStoresAsync(
            [new SparkReconciliationTarget("store-1", goneAway), new SparkReconciliationTarget("store-2", healthy)],
            TestPasses.Reconciliation(), Ct);

        Assert.Equal(1, settled);
    }

    [Fact]
    public async Task Cancellation_stops_the_pass_rather_than_being_swallowed_per_store()
    {
        var (reconciler, store) = Create();
        var sdk = SeedStore(store, "store-1", PaymentFixture.PaymentHash);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconciler.ReconcileStoresAsync(
            [new SparkReconciliationTarget("store-1", sdk)], TestPasses.Reconciliation(), cts.Token));
    }

    [Fact]
    public async Task No_stores_is_not_an_error()
    {
        var (reconciler, _) = Create();

        Assert.Equal(0, await reconciler.ReconcileStoresAsync([], TestPasses.Reconciliation(), Ct));
    }

    [Fact]
    public async Task A_store_with_no_running_wallet_still_gets_its_settlements_credited()
    {
        // The gap this closes. The pass's stores used to come only from the live SDK instances, so a store whose
        // Spark connection was broken — a rotated key, a service-provider outage, a wallet reconfigured away —
        // got no credit pass either. Its money had already arrived and been recorded; only the routing onto
        // BTCPay's invoice was outstanding, and that needs no wallet at all. So the pass takes its stores from
        // the record store as well, and this store is in no target list whatsoever.
        var (reconciler, store, credits) = CreateWithCredits();
        var offline = "store-with-no-wallet";
        store.Seed(new InvoiceRecord
        {
            PaymentHash = PaymentFixture.PaymentHash,
            StoreId = offline,
            Bolt11 = "lnbcrt-one",
            AmountMsat = 100_000,
            AmountReceivedMsat = 100_000,
            SdkPaymentId = "sdk-1",
            Preimage = PaymentFixture.Preimage,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-5),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-4),
            SettledAt = DateTimeOffset.UtcNow.AddHours(-4),
            Status = InvoiceRecordStatus.Paid
        });
        credits.Mint(PaymentFixture.PaymentHash, "btcpay-invoice-1", offline);

        // No targets: as far as the live wallets are concerned this store does not exist.
        Assert.Equal(0, await reconciler.ReconcileStoresAsync([], TestPasses.Reconciliation(), Ct));

        Assert.Equal(PaymentFixture.PaymentHash, Assert.Single(credits.Credits).PaymentHash);
        Assert.NotNull(store.Records[PaymentFixture.PaymentHash].CreditedAt);
    }

    [Fact]
    public async Task A_running_store_that_also_awaits_a_credit_does_both_and_is_counted_once()
    {
        // A store reached by both sources at once. The union is a set, so it is visited a single time, and that
        // one visit has to do both jobs: settle the invoice the SDK has been paid for, and route the settlement
        // from an earlier pass that never reached BTCPay. A settled count of two would mean the store had been
        // walked twice.
        var (reconciler, store, credits) = CreateWithCredits();
        var sdk = SeedStore(store, "store-1", PaymentFixture.PaymentHash);
        credits.Mint(PaymentFixture.PaymentHash, "btcpay-invoice-1", "store-1");

        // Already settled on an earlier pass, and never credited — which is what puts this store in the record
        // store's awaiting-credit list as well as in the target list.
        store.Seed(new InvoiceRecord
        {
            PaymentHash = PaymentFixture.OtherPaymentHash,
            StoreId = "store-1",
            Bolt11 = "lnbcrt-two",
            AmountMsat = 100_000,
            AmountReceivedMsat = 100_000,
            SdkPaymentId = "sdk-earlier",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-5),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-4),
            SettledAt = DateTimeOffset.UtcNow.AddHours(-4),
            Status = InvoiceRecordStatus.Paid
        });
        credits.Mint(PaymentFixture.OtherPaymentHash, "btcpay-invoice-2", "store-1");

        var settled = await reconciler.ReconcileStoresAsync(
            [new SparkReconciliationTarget("store-1", sdk)], TestPasses.Reconciliation(), Ct);

        Assert.Equal(1, settled);
        var credited = credits.Credits.Select(c => c.PaymentHash).ToList();
        Assert.Equal(2, credited.Count);
        Assert.Contains(PaymentFixture.PaymentHash, credited);
        Assert.Contains(PaymentFixture.OtherPaymentHash, credited);
    }
}
