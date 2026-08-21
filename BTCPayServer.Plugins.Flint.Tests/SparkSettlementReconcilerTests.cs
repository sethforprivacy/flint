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
        var store = new InMemoryInvoiceRecordStore();
        var broadcaster = new SparkSettlementBroadcaster(NullLogger<SparkSettlementBroadcaster>.Instance);
        var log = new CapturingLogger<SparkSettlementReconciler>();
        return (new SparkSettlementReconciler(store, broadcaster, log), store, broadcaster, log);
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
    public async Task Applying_a_settlement_to_a_cancelled_invoice_notifies_nobody()
    {
        var (reconciler, store, broadcaster) = Create();
        Seed(store);
        await store.CancelAsync(StoreId, Hash, Ct);
        using var listener = broadcaster.Subscribe(StoreId);

        var result = await reconciler.ApplyAsync(StoreId, Receive(), Ct);

        Assert.Equal(InvoiceSettlementOutcome.RefusedCancelled, result.Outcome);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await listener.ReadAsync(cts.Token));
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
    public async Task Reconciling_a_store_skips_paid_cancelled_and_expired_invoices()
    {
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
        // Nothing was even asked about: the pending query excluded all three.
        Assert.Empty(sdk.ListQueries);
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

        public Task<SparkExitQuote> PrepareUnilateralExitAsync(
            ulong feeRateSatPerVbyte,
            string destinationAddress,
            IReadOnlyList<string>? leafIds,
            CancellationToken cancellationToken = default) =>
            _inner.PrepareUnilateralExitAsync(
                feeRateSatPerVbyte, destinationAddress, leafIds, cancellationToken);

        public Task<SparkExitResult> UnilateralExitAsync(
            ulong feeRateSatPerVbyte,
            string destinationAddress,
            IReadOnlyList<string>? leafIds,
            IReadOnlyList<SparkExitFundingUtxo> fundingUtxos,
            byte[] fundingSecretKey,
            Func<SparkExitQuote, string?> approveQuote,
            CancellationToken cancellationToken = default) =>
            _inner.UnilateralExitAsync(
                feeRateSatPerVbyte, destinationAddress, leafIds, fundingUtxos, fundingSecretKey, approveQuote,
                cancellationToken);

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
        var store = new InMemoryInvoiceRecordStore();
        return (
            new SparkSettlementReconciler(
                store,
                new SparkSettlementBroadcaster(NullLogger<SparkSettlementBroadcaster>.Instance),
                NullLogger<SparkSettlementReconciler>.Instance),
            store);
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
}
