using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

public class SparkLightningClientTests
{
    private const string StoreId = "store-1";
    private const string PaymentKey = "deadbeef";
    private const string Bolt11 = "lnbcrt-one";
    private static readonly string Hash = PaymentFixture.PaymentHash;

    private sealed record Harness(
        SparkLightningClient Client,
        FakeSparkSdkClient Sdk,
        InMemoryInvoiceRecordStore Store,
        SparkSettlementBroadcaster Broadcaster,
        InMemoryOutgoingPaymentStore Outgoing);

    private static Harness Create(Bolt11Info? bolt11Info = null, IHttpContextAccessor? httpContextAccessor = null)
    {
        var sdk = new FakeSparkSdkClient { NextPaymentRequest = Bolt11 };
        var store = new InMemoryInvoiceRecordStore();
        var outgoing = new InMemoryOutgoingPaymentStore();
        var broadcaster = new SparkSettlementBroadcaster(NullLogger<SparkSettlementBroadcaster>.Instance);
        var parser = new StubBolt11Parser();
        parser.Register(Bolt11, bolt11Info ?? new Bolt11Info(Hash, DateTimeOffset.UtcNow.AddHours(1), 100_000));
        // An empty credit gateway: BTCPay knows of none of these payment hashes, which is the honest shape for
        // a test of the client. Crediting is covered where it belongs, in SparkInvoiceCreditorTests and
        // SparkSupersededInvoiceCreditTests.
        var reconciler = new SparkSettlementReconciler(
            store,
            broadcaster,
            new SparkInvoiceCreditor(
                new FakeInvoiceCreditGateway(), store, NullLogger<SparkInvoiceCreditor>.Instance),
            NullLogger<SparkSettlementReconciler>.Instance);

        var client = new SparkLightningClient(
            StoreId, PaymentKey, sdk, store, outgoing, reconciler, broadcaster, parser, NullLogger.Instance,
            httpContextAccessor);
        return new Harness(client, sdk, store, broadcaster, outgoing);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #region CreateInvoice

    [Fact]
    public async Task CreateInvoice_returns_the_payment_hash_as_the_id_and_persists_a_record()
    {
        var (client, _, store, _, _) = Create();

        var invoice = await client.CreateInvoice(
            new CreateInvoiceParams(LightMoney.Satoshis(100), "order 42", TimeSpan.FromMinutes(15)), Ct);

        // Id must be the payment hash: BTCPay joins CreateInvoice, GetInvoice and WaitInvoice on it.
        Assert.Equal(Hash, invoice.Id);
        Assert.Equal(Hash, invoice.PaymentHash);
        Assert.Equal(Bolt11, invoice.BOLT11);
        Assert.Equal(LightningInvoiceStatus.Unpaid, invoice.Status);
        Assert.Null(invoice.AmountReceived);

        var record = Assert.Single(store.Records).Value;
        Assert.Equal(StoreId, record.StoreId);
        Assert.Equal(InvoiceRecordStatus.Unpaid, record.Status);
        Assert.Equal("order 42", record.Description);
    }

    [Fact]
    public async Task CreateInvoice_takes_the_amount_from_the_minted_invoice_not_the_request()
    {
        // The SSP rounds to whole satoshi, so the invoice is the authority on what the payer will be asked
        // for. Reporting the requested amount instead would make an overpayment look like an exact payment.
        var (client, _, _, _, _) = Create(new Bolt11Info(Hash, DateTimeOffset.UtcNow.AddHours(1), 99_000));

        var invoice = await client.CreateInvoice(
            new CreateInvoiceParams(LightMoney.MilliSatoshis(98_001), "x", TimeSpan.FromMinutes(15)), Ct);

        Assert.Equal(99_000, invoice.Amount.MilliSatoshi);
    }

    [Fact]
    public async Task CreateInvoice_rounds_a_sub_satoshi_amount_up()
    {
        // BTCPay derives Lightning amounts from a fiat price, so sub-satoshi remainders are routine. Rounding
        // down would undercharge by up to a satoshi on every invoice.
        var (client, sdk, _, _, _) = Create();

        await client.CreateInvoice(
            new CreateInvoiceParams(LightMoney.MilliSatoshis(1001), "x", TimeSpan.FromMinutes(15)), Ct);

        Assert.Equal(2, Assert.Single(sdk.ReceiveCalls).AmountSats);
    }

    [Fact]
    public async Task CreateInvoice_requests_an_amountless_invoice_for_a_zero_amount()
    {
        var (client, sdk, _, _, _) = Create(new Bolt11Info(Hash, DateTimeOffset.UtcNow.AddHours(1), null));

        var invoice = await client.CreateInvoice(
            new CreateInvoiceParams(LightMoney.Zero, "tip", TimeSpan.FromMinutes(15)), Ct);

        Assert.Null(Assert.Single(sdk.ReceiveCalls).AmountSats);
        Assert.Equal(LightMoney.Zero, invoice.Amount);
    }

    [Theory]
    [InlineData(0, 86_400)]
    [InlineData(-5, 86_400)]
    [InlineData(900, 900)]
    public async Task CreateInvoice_never_passes_an_expiry_the_SDK_would_reinterpret(int requested, uint expected)
    {
        // The SDK turns 0 into 24 h and null into 30 days, either of which would contradict what BTCPay told
        // the payer.
        var (client, sdk, _, _, _) = Create();

        await client.CreateInvoice(
            new CreateInvoiceParams(LightMoney.Satoshis(1), "x", TimeSpan.FromSeconds(requested)), Ct);

        Assert.Equal(expected, Assert.Single(sdk.ReceiveCalls).ExpirySecs);
    }

    [Fact]
    public async Task CreateInvoice_puts_the_plain_description_in_the_d_tag_even_for_description_hash_only()
    {
        // Documented LUD-06 deviation: the SDK's receive request has no descriptionHash field, so BTCPay's
        // LNURL flow (which always sets DescriptionHashOnly) would otherwise be unusable.
        var (client, sdk, _, _, _) = Create();
        var request = new CreateInvoiceParams(LightMoney.Satoshis(100), "{\"metadata\":1}", TimeSpan.FromMinutes(15))
        {
            DescriptionHashOnly = true
        };

        var invoice = await client.CreateInvoice(request, Ct);

        Assert.Equal("{\"metadata\":1}", Assert.Single(sdk.ReceiveCalls).Description);
        // The critical part is that it did not fail.
        Assert.Equal(Hash, invoice.Id);
    }

    [Fact]
    public async Task CreateInvoice_uses_an_empty_description_when_only_a_hash_was_supplied()
    {
        // Reachable only through BTCPay's obsolete description-hash constructor. Since the SDK cannot set the
        // h tag either way, putting 64 characters of hash hex in the d tag would show the payer noise without
        // becoming any more LUD-06 conformant.
        var harness = Create();
#pragma warning disable CS0618 // The obsolete constructor is the only way to reach this path, which is why it
        // needs covering: BTCPay itself still exposes it.
        var request = new CreateInvoiceParams(
            LightMoney.Satoshis(100), uint256.Parse(Hash), TimeSpan.FromMinutes(15));
#pragma warning restore CS0618

        await harness.Client.CreateInvoice(request, Ct);

        Assert.Equal(string.Empty, Assert.Single(harness.Sdk.ReceiveCalls).Description);
    }

    [Fact]
    public async Task CreateInvoice_truncates_an_over_long_description_locally()
    {
        // The SSP rejects anything over 639 bytes, and only after a round trip.
        var (client, sdk, _, _, _) = Create();

        await client.CreateInvoice(
            new CreateInvoiceParams(LightMoney.Satoshis(1), new string('x', 900), TimeSpan.FromMinutes(15)), Ct);

        Assert.Equal(639, Assert.Single(sdk.ReceiveCalls).Description.Length);
    }

    [Fact]
    public async Task CreateInvoice_fails_when_the_record_cannot_be_persisted()
    {
        // An invoice handed out but not recorded is an invoice that can be paid and never credited.
        var (client, _, store, _, _) = Create();
        store.FailAddWith = new InvalidOperationException("database down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateInvoice(
            new CreateInvoiceParams(LightMoney.Satoshis(100), "x", TimeSpan.FromMinutes(15)), Ct));
    }

    [Fact]
    public async Task CreateInvoice_fails_when_the_minted_invoice_cannot_be_parsed()
    {
        // The store under test is the one the failing client actually writes to — the earlier version of this
        // test asserted Empty on a second store the client had never been given, which would have passed even
        // if a record had been written.
        var harness = Create();
        harness.Sdk.NextPaymentRequest = "garbage-that-cannot-be-parsed";

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Client.CreateInvoice(
            new CreateInvoiceParams(LightMoney.Satoshis(100), "x", TimeSpan.FromMinutes(15)), Ct));

        // An invoice we cannot key must not be recorded, and must not be handed to BTCPay either.
        Assert.Empty(harness.Store.Records);
    }

    #endregion

    #region GetInvoice

    [Fact]
    public async Task GetInvoice_returns_null_for_an_unknown_hash()
    {
        var (client, _, _, _, _) = Create();

        Assert.Null(await client.GetInvoice(new string('b', 64), Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("zzzz")]
    public async Task GetInvoice_rejects_a_malformed_id_without_touching_the_SDK(string id)
    {
        var (client, sdk, _, _, _) = Create();

        Assert.Null(await client.GetInvoice(id, Ct));
        Assert.Empty(sdk.ListQueries);
    }

    [Fact]
    public async Task GetInvoice_reports_an_unpaid_invoice_as_unpaid()
    {
        var (client, _, store, _, _) = Create();
        Seed(store);

        var invoice = await client.GetInvoice(Hash, Ct);

        Assert.Equal(LightningInvoiceStatus.Unpaid, invoice.Status);
        Assert.Null(invoice.AmountReceived);
    }

    [Fact]
    public async Task GetInvoice_reports_a_past_expiry_unpaid_invoice_as_expired()
    {
        var (client, _, store, _, _) = Create();
        Seed(store, expiresAt: DateTimeOffset.UtcNow.AddHours(-1));

        var invoice = await client.GetInvoice(Hash, Ct);

        Assert.Equal(LightningInvoiceStatus.Expired, invoice.Status);
    }

    [Fact]
    public async Task GetInvoice_settles_from_a_reconciliation_scan_and_reports_the_received_amount()
    {
        var (client, sdk, store, _, _) = Create();
        Seed(store);
        sdk.Payments.Add(Receive(amountSats: 250));

        var invoice = await client.GetInvoice(Hash, Ct);

        Assert.Equal(LightningInvoiceStatus.Paid, invoice.Status);
        Assert.Equal(250_000, invoice.AmountReceived!.MilliSatoshi);
        // Invoiced amount is reported separately and unchanged.
        Assert.Equal(100_000, invoice.Amount.MilliSatoshi);
        Assert.Equal("sdk-1", store.Records[Hash].SdkPaymentId);
    }

    [Fact]
    public async Task GetInvoice_anchors_the_reconciliation_scan_to_the_invoice_creation_time()
    {
        // An unbounded scan would become O(all history) once a wallet has been busy for a while, and this
        // runs per pending invoice on every reconciliation pass.
        var (client, sdk, store, _, _) = Create();
        var createdAt = DateTimeOffset.UtcNow.AddHours(-2);
        Seed(store, createdAt: createdAt);

        await client.GetInvoice(Hash, Ct);

        var query = Assert.Single(sdk.ListQueries);
        Assert.Equal(SparkPaymentDirection.Receive, query.Direction);
        Assert.True(query.CompletedOnly);
        Assert.NotNull(query.From);
        Assert.True(query.From < createdAt, "the scan window must start before the invoice was created");
        Assert.True(query.From > createdAt.AddHours(-1), "the scan window must stay narrow");
        Assert.Equal(50, query.Limit);
    }

    [Fact]
    public async Task GetInvoice_uses_a_point_lookup_once_the_SDK_payment_id_is_known()
    {
        var (client, sdk, store, _, _) = Create();
        Seed(store, sdkPaymentId: "sdk-1");
        sdk.PaymentsById["sdk-1"] = Receive(amountSats: 100);

        var invoice = await client.GetInvoice(Hash, Ct);

        Assert.Equal(LightningInvoiceStatus.Paid, invoice.Status);
        Assert.Equal("sdk-1", Assert.Single(sdk.GetPaymentCalls));
        Assert.Empty(sdk.ListQueries);
    }

    [Fact]
    public async Task GetInvoice_ignores_the_send_leg_of_a_self_payment()
    {
        // One payment hash produces two Payment rows when a wallet pays its own invoice, and the send leg's
        // amount is net of a fee the receive leg never paid. Crediting the wrong leg would credit the wrong
        // amount. The point-lookup branch cannot filter by direction in the query, so it must check here.
        var (client, sdk, store, _, _) = Create();
        Seed(store, sdkPaymentId: "send-leg");
        sdk.PaymentsById["send-leg"] = new SparkPayment(
            "send-leg", SparkPaymentDirection.Send, SparkPaymentStatus.Completed, SparkPaymentMethod.Lightning,
            97, 3, DateTimeOffset.UtcNow, Hash, Bolt11, PaymentFixture.Preimage, null);

        var invoice = await client.GetInvoice(Hash, Ct);

        Assert.Equal(LightningInvoiceStatus.Unpaid, invoice.Status);
        Assert.Null(invoice.AmountReceived);
    }

    [Fact]
    public async Task GetInvoice_settles_from_a_recorded_pending_payment_whose_event_never_arrived()
    {
        // The case that makes polling load-bearing: a completed receive was observed emitting only
        // PaymentPending, with the completion visible from storage afterwards and no PaymentSucceeded ever.
        // The pending event recorded the SDK id; this poll is what actually settles it.
        var (client, sdk, store, _, _) = Create();
        Seed(store, sdkPaymentId: "sdk-1");
        sdk.PaymentsById["sdk-1"] = Receive(amountSats: 100);

        var invoice = await client.GetInvoice(Hash, Ct);

        Assert.Equal(LightningInvoiceStatus.Paid, invoice.Status);
        Assert.Equal(100_000, invoice.AmountReceived!.MilliSatoshi);
    }

    [Fact]
    public async Task GetInvoice_is_idempotent_across_repeated_polls()
    {
        // The reconciliation pass, a BTCPay GetInvoice lookup and a duplicated event all reach the same
        // settlement, so it gets applied more than once by design. It must converge, not accumulate.
        var (client, sdk, store, _, _) = Create();
        Seed(store);
        sdk.Payments.Add(Receive(amountSats: 250));

        var first = await client.GetInvoice(Hash, Ct);
        var second = await client.GetInvoice(Hash, Ct);

        Assert.Equal(LightningInvoiceStatus.Paid, first.Status);
        Assert.Equal(LightningInvoiceStatus.Paid, second.Status);
        Assert.Equal(first.AmountReceived!.MilliSatoshi, second.AmountReceived!.MilliSatoshi);
        Assert.Equal(250_000, store.Records[Hash].AmountReceivedMsat);
    }

    [Fact]
    public async Task GetInvoice_does_not_query_the_SDK_for_an_already_paid_invoice()
    {
        var (client, sdk, store, _, _) = Create();
        Seed(store);
        await store.SettleAsync(StoreId, Hash, "sdk-1", 100_000, PaymentFixture.Preimage, DateTimeOffset.UtcNow, Ct);

        var invoice = await client.GetInvoice(Hash, Ct);

        Assert.Equal(LightningInvoiceStatus.Paid, invoice.Status);
        Assert.Equal(PaymentFixture.Preimage, invoice.Preimage);
        Assert.Empty(sdk.ListQueries);
        Assert.Empty(sdk.GetPaymentCalls);
    }

    [Fact]
    public async Task GetInvoice_ignores_a_pending_receive()
    {
        var (client, sdk, store, _, _) = Create();
        Seed(store);
        sdk.Payments.Add(Receive() with { Status = SparkPaymentStatus.Pending });

        var invoice = await client.GetInvoice(Hash, Ct);

        // CompletedOnly filters it out in the fake exactly as it does in the SDK, so this asserts the query
        // was built correctly as much as the mapping.
        Assert.Equal(LightningInvoiceStatus.Unpaid, invoice.Status);
    }

    [Fact]
    public async Task GetInvoice_reports_unpaid_rather_than_throwing_when_the_SDK_is_unreachable()
    {
        // Throwing here would be logged as a listener failure and can drop the invoice from BTCPay's poll
        // set. Reporting "still unpaid" lets it retry in a minute.
        var (client, sdk, store, _, _) = Create();
        Seed(store);
        sdk.FailWith = new InvalidOperationException("SSP unreachable");

        var invoice = await client.GetInvoice(Hash, Ct);

        Assert.Equal(LightningInvoiceStatus.Unpaid, invoice.Status);
    }

    [Fact]
    public async Task GetInvoice_accepts_an_upper_case_hash()
    {
        var (client, _, store, _, _) = Create();
        Seed(store);

        Assert.NotNull(await client.GetInvoice(Hash.ToUpperInvariant(), Ct));
        Assert.NotNull(await client.GetInvoice(uint256.Parse(Hash), Ct));
    }

    #endregion

    #region CancelInvoice

    [Fact]
    public async Task CancelInvoice_keeps_a_late_payment_settleable_and_creditable()
    {
        var (client, sdk, store, _, _) = Create();
        Seed(store);

        await client.CancelInvoice(Hash, Ct);
        Assert.Equal(InvoiceRecordStatus.Expired, store.Records[Hash].Status);

        // A payment can still arrive: Spark cannot withdraw the invoice from the SSP. Refusing to settle it
        // would leave the money unattributed in the wallet and the BTCPay invoice unpaid, so it must be
        // reported paid with the received amount.
        sdk.Payments.Add(Receive());
        var invoice = await client.GetInvoice(Hash, Ct);

        Assert.Equal(LightningInvoiceStatus.Paid, invoice.Status);
        Assert.Equal(100_000, invoice.AmountReceived!.MilliSatoshi);
        Assert.Equal("sdk-1", store.Records[Hash].SdkPaymentId);
        Assert.NotNull(store.Records[Hash].SettledAt);
    }

    [Fact]
    public async Task GetInvoice_reports_a_cancelled_but_unpaid_invoice_as_unpaid()
    {
        // BTCPay's Lightning listener drops an invoice whose status reads expired — the very listener that
        // would deliver a late payment's credit. A cancelled Spark invoice is still payable, so it must keep
        // reporting unpaid until it settles.
        var (client, _, store, _, _) = Create();
        Seed(store);
        await client.CancelInvoice(Hash, Ct);

        var invoice = await client.GetInvoice(Hash, Ct);

        Assert.Equal(LightningInvoiceStatus.Unpaid, invoice.Status);
        Assert.Null(invoice.AmountReceived);
    }

    [Fact]
    public async Task CancelInvoice_does_not_throw_for_an_unknown_or_settled_invoice()
    {
        var (client, _, store, _, _) = Create();
        Seed(store);
        await store.SettleAsync(StoreId, Hash, "sdk-1", 100_000, null, DateTimeOffset.UtcNow, Ct);

        // BTCPay calls this speculatively on expiry; throwing would log an error for every paid invoice.
        await client.CancelInvoice(Hash, Ct);
        await client.CancelInvoice(new string('c', 64), Ct);
        await client.CancelInvoice("garbage", Ct);

        Assert.Equal(InvoiceRecordStatus.Paid, store.Records[Hash].Status);
    }

    #endregion

    #region Listen

    [Fact]
    public async Task Listen_delivers_a_broadcast_settlement_as_a_paid_invoice()
    {
        var (client, _, _, broadcaster, _) = Create();
        using var listener = await client.Listen(Ct);

        broadcaster.Publish(new SparkSettlement(
            StoreId, Hash, Bolt11, 100_000, 250_000,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), PaymentFixture.Preimage));
        var invoice = await listener.WaitInvoice(Ct);

        Assert.Equal(Hash, invoice.Id);
        Assert.Equal(LightningInvoiceStatus.Paid, invoice.Status);
        Assert.Equal(250_000, invoice.AmountReceived!.MilliSatoshi);
        Assert.Equal(PaymentFixture.Preimage, invoice.Preimage);
    }

    [Fact]
    public async Task Two_concurrent_listeners_both_receive_the_same_settlement()
    {
        var (client, _, _, broadcaster, _) = Create();
        using var first = await client.Listen(Ct);
        using var second = await client.Listen(Ct);

        broadcaster.Publish(new SparkSettlement(
            StoreId, Hash, Bolt11, 100_000, 100_000,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), null));

        Assert.Equal(Hash, (await first.WaitInvoice(Ct)).Id);
        Assert.Equal(Hash, (await second.WaitInvoice(Ct)).Id);
    }

    [Fact]
    public async Task WaitInvoice_waits_rather_than_polling()
    {
        var (client, _, _, broadcaster, _) = Create();
        using var listener = await client.Listen(Ct);

        var waiting = listener.WaitInvoice(Ct);
        Assert.False(waiting.IsCompleted);

        broadcaster.Publish(new SparkSettlement(
            StoreId, Hash, Bolt11, null, 1000,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), null));

        Assert.Equal(Hash, (await waiting).Id);
    }

    [Fact]
    public async Task WaitInvoice_honours_cancellation()
    {
        var (client, _, _, _, _) = Create();
        using var listener = await client.Listen(Ct);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.WaitInvoice(cts.Token));
    }

    #endregion

    #region Balance, info and validation

    [Fact]
    public async Task GetBalance_reports_the_Spark_balance_as_the_local_offchain_balance()
    {
        var (client, sdk, _, _, _) = Create();
        sdk.BalanceSats = 54_321;

        var balance = await client.GetBalance(Ct);

        Assert.Equal(LightMoney.Satoshis(54_321), balance.OffchainBalance.Local);
        // No fictional inbound liquidity, and no on-chain wallet.
        Assert.Null(balance.OffchainBalance.Remote);
        Assert.Null(balance.OnchainBalance);
    }

    [Fact]
    public async Task GetInfo_throws_NotSupported_because_there_is_no_node()
    {
        var (client, _, _, _, _) = Create();

        await Assert.ThrowsAsync<NotSupportedException>(() => client.GetInfo(Ct));
    }

    [Fact]
    public async Task Node_shaped_members_throw_NotSupported()
    {
        var (client, _, _, _, _) = Create();

        await Assert.ThrowsAsync<NotSupportedException>(() => client.ListChannels(Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.GetDepositAddress(Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.OpenChannel(new OpenChannelRequest(), Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            client.ConnectTo(NodeInfo.Parse("0244a2e3e0e3f8e26f5d0e3a8f4bbdba0f3b03bd12b1d1b0a9e5b0f6e9f7a2b1c3@1.2.3.4:9735"), Ct));
    }

    [Fact]
    public async Task Validate_passes_when_the_instance_is_alive()
    {
        var (client, _, _, _, _) = Create();

        Assert.Null(await client.Validate());
    }

    [Fact]
    public async Task Validate_reports_a_readable_message_when_the_instance_is_gone()
    {
        var (client, sdk, _, _, _) = Create();
        sdk.FailWith = new ObjectDisposedException("BreezSdk");

        var result = await client.Validate();

        Assert.NotNull(result);
        Assert.NotNull(result.ErrorMessage);
        // Never the raw "@v1=..." UniFFI message.
        Assert.DoesNotContain("@v1=", result.ErrorMessage);
    }

    [Fact]
    public async Task Validate_refuses_a_connection_string_saved_on_another_store()
    {
        // The save-time layer of cross-store enforcement. This client is bound to store-1 (the store id
        // embedded in its connection string); the request is authorised for store-2. Accepting the save
        // would let store-2 receive into and spend from store-1's wallet, which is the cross-store hijack.
        var (client, _, _, _, _) = Create(httpContextAccessor: ContextAuthorisedFor("store-2"));

        var result = await client.Validate();

        Assert.NotNull(result);
        Assert.Contains("another store", result.ErrorMessage);
    }

    [Fact]
    public async Task Validate_accepts_a_connection_string_saved_on_its_own_store()
    {
        // The legitimate save: store-1's own string, saved on store-1, passes the store check and reaches
        // the live wallet check.
        var (client, _, _, _, _) = Create(httpContextAccessor: ContextAuthorisedFor(StoreId));

        Assert.Null(await client.Validate());
    }

    [Fact]
    public async Task Validate_with_no_request_context_still_runs_the_live_check()
    {
        // A background resolution has no request and therefore no store to compare; the store check must pass
        // silently (not fail closed on missing context) while the live wallet check still runs.
        var (client, sdk, _, _, _) = Create();
        sdk.FailWith = new ObjectDisposedException("BreezSdk");

        var result = await client.Validate();

        Assert.NotNull(result);
        Assert.NotNull(result.ErrorMessage);
    }

    private static IHttpContextAccessor ContextAuthorisedFor(string storeId)
    {
        // Reproduces an authorised store route: core's authorisation middleware places the configured store
        // in HttpContext.Items under "BTCPAY.STOREDATA" (the GetStoreData family of extensions).
        var context = new DefaultHttpContext();
        context.SetStoreData(new StoreData { Id = storeId });
        return new FakeHttpContextAccessor { HttpContext = context };
    }

    [Fact]
    public void ToString_round_trips_as_a_connection_string()
    {
        // BTCPay persists client.ToString() as the store's Lightning connection string.
        var (client, _, _, _, _) = Create();

        var result = SparkConnectionString.Parse(client.ToString(), out var storeId, out var key, out _);

        Assert.Equal(SparkConnectionStringParseResult.Ok, result);
        Assert.Equal(StoreId, storeId);
        Assert.Equal(PaymentKey, key);
    }

    [Fact]
    public void Dispose_does_not_shut_the_wallet_down()
    {
        // The client is shared by every connection-string resolution for the store; SparkService owns the
        // instance. Disposing the SDK here would take the wallet offline mid-checkout.
        var (client, sdk, _, _, _) = Create();

        client.Dispose();

        Assert.False(sdk.Disposed);
        Assert.False(sdk.Disconnected);
    }

    #endregion

    #region Send path

    [Fact]
    public async Task Pay_sends_and_reports_the_fee()
    {
        var (client, sdk, _, _, _) = Create();
        sdk.NextQuote = new SparkSendQuote(1000, 4, Hash);

        var response = await client.Pay(Bolt11, new PayInvoiceParams(), Ct);

        Assert.Equal(PayResult.Ok, response.Result);
        Assert.Equal(LightMoney.Satoshis(4), response.Details!.FeeAmount);
        Assert.Equal(LightMoney.Satoshis(1004), response.Details.TotalAmount);
        Assert.Equal(LightningPaymentStatus.Complete, response.Details.Status);
    }

    [Fact]
    public async Task Pay_uses_an_idempotency_key_derived_from_the_payment_hash()
    {
        // Must be a UUID (the SDK rejects anything else) and must be stable, so a retry after a crash cannot
        // double-spend.
        var (client, sdk, _, _, _) = Create();

        await client.Pay(Bolt11, new PayInvoiceParams(), Ct);
        await client.Pay(Bolt11, new PayInvoiceParams(), Ct);

        var keys = sdk.SendCalls.Select(c => c.IdempotencyKey).Distinct().ToList();
        var key = Assert.Single(keys);
        Assert.True(Guid.TryParse(key, out _), $"'{key}' is not a UUID");
        Assert.Equal(SparkLightningClient.DeriveIdempotencyKey(Hash), key);
    }

    [Fact]
    public void The_derived_idempotency_key_is_a_well_formed_version_4_UUID()
    {
        // The SDK rejects a non-UUID with a misleading "Invalid TransferId format", and SdkException.InvalidUuid
        // exists, so raw hash bytes are not good enough — the version and variant nibbles must be stamped.
        foreach (var hash in new[] { Hash, PaymentFixture.OtherPaymentHash, new string('f', 64) })
        {
            var key = SparkLightningClient.DeriveIdempotencyKey(hash);
            Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", key);
            Assert.Equal(key, SparkLightningClient.DeriveIdempotencyKey(hash));
        }

        Assert.NotEqual(
            SparkLightningClient.DeriveIdempotencyKey(Hash),
            SparkLightningClient.DeriveIdempotencyKey(PaymentFixture.OtherPaymentHash));
    }

    [Fact]
    public async Task Pay_reports_an_earlier_completed_send_instead_of_sending_again()
    {
        // The SDK adopts the idempotency key as Payment.id, so this is how a retry after a crash mid-send
        // learns that the money already left, instead of sending twice.
        var harness = Create();
        var key = SparkLightningClient.DeriveIdempotencyKey(Hash);
        harness.Sdk.PaymentsById[key] = CompletedSend(key);

        var response = await harness.Client.Pay(Bolt11, new PayInvoiceParams(), Ct);

        Assert.Equal(PayResult.Ok, response.Result);
        Assert.Equal(LightMoney.Satoshis(4), response.Details!.FeeAmount);
        Assert.Empty(harness.Sdk.SendCalls);
    }

    [Fact]
    public async Task Pay_refuses_a_second_claim_on_an_invoice_it_has_already_reported_paid()
    {
        // BTCPay's duplicate-destination guard only blocks a claim while an earlier payout for the same payment
        // hash is still pending — it explicitly allows a fresh claim once that payout is Completed or
        // Cancelled. And a crash between Pay returning and BTCPay persisting the proof leaves the payout
        // InProgress with no proof, which LightningPendingPayoutListener turns into Cancelled without asking
        // the node, freeing the invoice for exactly such a re-claim. A BOLT11 can only be paid once, so
        // reporting Ok again would mark two payouts Completed for one payment.
        var harness = Create();

        var first = await harness.Client.Pay(Bolt11, new PayInvoiceParams(), Ct);
        Assert.Equal(PayResult.Ok, first.Result);
        Assert.Single(harness.Sdk.SendCalls);

        var second = await harness.Client.Pay(Bolt11, new PayInvoiceParams(), Ct);

        // Error, not Ok: BTCPay returns the payout to AwaitingPayment where a human can see it, rather than
        // marking it Completed with no money moved.
        Assert.Equal(PayResult.Error, second.Result);
        Assert.Contains("already been paid", second.ErrorDetail);
        Assert.Single(harness.Sdk.SendCalls);
    }

    [Fact]
    public async Task Two_concurrent_pays_of_one_invoice_send_once_and_only_one_reports_success()
    {
        // Audit finding PaymentFlow F1. The sequential case above was always safe, because the second Pay's
        // probe saw the first payment. Concurrently it did not: both calls passed the probe before either send
        // reached SDK storage, both sent with the same idempotency key, and both returned Ok — two payouts
        // marked Completed against one payment. Reachable from the automated payout processor ticking while
        // someone confirms by hand, or two Greenfield pay calls.
        var harness = Create();
        harness.Sdk.HoldSendUntil = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = Task.Run(() => harness.Client.Pay(Bolt11, new PayInvoiceParams(), Ct));

        // Wait for a send to actually be in flight rather than sleeping and hoping.
        await harness.Sdk.SendEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var second = Task.Run(() => harness.Client.Pay(Bolt11, new PayInvoiceParams(), Ct));

        // Give the second caller room to reach the probe and send. Unserialised it gets there in microseconds
        // against an in-memory fake, so this window is generous; serialised it is parked on the lock and no
        // amount of waiting produces a second send.
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        Assert.Single(harness.Sdk.SendCalls);

        harness.Sdk.HoldSendUntil.SetResult();
        var results = await Task.WhenAll(first, second);

        // The money moved once...
        Assert.Single(harness.Sdk.SendCalls);

        // ...and exactly one payout is told so. The loser gets Error, which returns it to AwaitingPayment for a
        // human rather than marking a second obligation discharged.
        Assert.Equal(1, results.Count(r => r.Result == PayResult.Ok));
        var loser = Assert.Single(results, r => r.Result != PayResult.Ok);
        Assert.Equal(PayResult.Error, loser.Result);
        Assert.Contains("already been paid", loser.ErrorDetail);
    }

    [Fact]
    public async Task Pay_retries_after_a_failed_send_rather_than_bricking_the_payout()
    {
        // A Failed send moved no money, so it must not short-circuit. BTCPay maps Error back to
        // AwaitingPayment and retries up to ten times before parking the payout; returning the cached failure
        // forever would burn all ten attempts without ever contacting the service provider again.
        var harness = Create();
        var key = SparkLightningClient.DeriveIdempotencyKey(Hash);
        harness.Sdk.PaymentsById[key] = CompletedSend(key) with { Status = SparkPaymentStatus.Failed };

        var response = await harness.Client.Pay(Bolt11, new PayInvoiceParams(), Ct);

        Assert.Single(harness.Sdk.SendCalls);
        Assert.Equal(PayResult.Ok, response.Result);
    }

    [Fact]
    public async Task Pay_reports_an_in_flight_send_without_sending_again()
    {
        var harness = Create();
        var key = SparkLightningClient.DeriveIdempotencyKey(Hash);
        harness.Sdk.PaymentsById[key] = CompletedSend(key) with { Status = SparkPaymentStatus.Pending };

        var response = await harness.Client.Pay(Bolt11, new PayInvoiceParams(), Ct);

        Assert.Equal(PayResult.Unknown, response.Result);
        Assert.Empty(harness.Sdk.SendCalls);
    }

    [Fact]
    public async Task A_vetoed_or_refused_attempt_does_not_consume_the_right_to_report()
    {
        // The duplicate guard must not brick a payout that never sent anything. A fee veto and a locally
        // rejected input both leave the invoice claimable by the next attempt.
        var harness = Create();
        harness.Sdk.NextQuote = new SparkSendQuote(1000, 5000, Hash);

        var vetoed = await harness.Client.Pay(Bolt11, new PayInvoiceParams(), Ct);
        Assert.Equal(PayResult.Error, vetoed.Result);
        Assert.Null(harness.Outgoing.Records[(StoreId, Hash)].ReportedAt);

        harness.Sdk.NextQuote = new SparkSendQuote(1000, 4, Hash);
        var retried = await harness.Client.Pay(Bolt11, new PayInvoiceParams(), Ct);

        Assert.Equal(PayResult.Ok, retried.Result);
        Assert.NotNull(harness.Outgoing.Records[(StoreId, Hash)].ReportedAt);
    }

    [Fact]
    public async Task An_indeterminate_failure_consumes_the_right_to_report()
    {
        // A timeout may have landed at the service provider. BTCPay leaves such a payout InProgress and
        // resolves it through GetPayment, so a later claim on the same invoice must still be refused.
        var harness = Create();
        harness.Sdk.FailSendWith = new TimeoutException("network");

        var response = await harness.Client.Pay(Bolt11, new PayInvoiceParams(), Ct);

        Assert.Equal(PayResult.Unknown, response.Result);
        Assert.NotNull(harness.Outgoing.Records[(StoreId, Hash)].ReportedAt);
    }

    [Fact]
    public async Task Pay_counts_attempts_and_keeps_the_first_attempt_time()
    {
        var harness = Create();
        harness.Sdk.NextQuote = new SparkSendQuote(1000, 5000, Hash);

        await harness.Client.Pay(Bolt11, new PayInvoiceParams(), Ct);
        await harness.Client.Pay(Bolt11, new PayInvoiceParams(), Ct);

        var record = harness.Outgoing.Records[(StoreId, Hash)];
        Assert.Equal(2, record.AttemptCount);
        Assert.Equal(Bolt11, record.Bolt11);
        Assert.Equal(SparkLightningClient.DeriveIdempotencyKey(Hash), record.IdempotencyKey);
    }

    [Fact]
    public async Task Pay_refuses_to_send_when_the_attempt_cannot_be_recorded()
    {
        // Without the record the duplicate guard cannot work, so not sending is the safe answer: Error returns
        // the payout to AwaitingPayment and no money moves.
        var harness = Create();
        harness.Outgoing.FailRegisterWith = new InvalidOperationException("database down");

        var response = await harness.Client.Pay(Bolt11, new PayInvoiceParams(), Ct);

        Assert.Equal(PayResult.Error, response.Result);
        Assert.Empty(harness.Sdk.SendCalls);
    }

    private static SparkPayment CompletedSend(string id) => new(
        id, SparkPaymentDirection.Send, SparkPaymentStatus.Completed, SparkPaymentMethod.Lightning,
        1000, 4, DateTimeOffset.UtcNow, Hash, Bolt11, PaymentFixture.Preimage, null);

    [Fact]
    public async Task Pay_still_sends_when_the_idempotency_key_names_only_a_receive()
    {
        // Defensive: a Receive under that id would mean the key collided with an inbound payment, not that
        // this send already happened.
        var (client, sdk, _, _, _) = Create();
        sdk.PaymentsById[SparkLightningClient.DeriveIdempotencyKey(Hash)] = Receive();

        await client.Pay(Bolt11, new PayInvoiceParams(), Ct);

        Assert.Single(sdk.SendCalls);
    }

    [Fact]
    public async Task Pay_reports_a_locally_rejected_send_as_a_definite_failure()
    {
        // An InvalidInput is a client-side validation (a sub-dust amount, a malformed destination): nothing
        // was sent, so the payout may safely be retried on different terms. Anything less certain must be
        // Unknown instead.
        var (client, sdk, _, _, _) = Create();
        sdk.FailWith = new Breez.Sdk.Spark.SdkException.InvalidInput(
            "@v1=Amount is below the minimum of 294 sats required for this address");

        var response = await client.Pay(Bolt11, new PayInvoiceParams(), Ct);

        Assert.Equal(PayResult.Error, response.Result);
        Assert.DoesNotContain("@v1=", response.ErrorDetail);
    }

    [Fact]
    public async Task Pay_reports_an_insufficient_balance_as_a_definite_failure()
    {
        var (client, sdk, _, _, _) = Create();
        sdk.FailWith = new Breez.Sdk.Spark.SdkException.SparkException(
            "@v1=Tree service error: insufficient funds");

        var response = await client.Pay(Bolt11, new PayInvoiceParams(), Ct);

        Assert.Equal(PayResult.Error, response.Result);
    }

    [Fact]
    public async Task Pay_aborts_before_sending_when_the_quoted_fee_exceeds_a_flat_limit()
    {
        var (client, sdk, _, _, _) = Create();
        sdk.NextQuote = new SparkSendQuote(1000, 50, Hash);

        var response = await client.Pay(Bolt11, new PayInvoiceParams { MaxFeeFlat = Money.Satoshis(10) }, Ct);

        Assert.Equal(PayResult.Error, response.Result);
        Assert.Contains("50 sat", response.ErrorDetail);
        Assert.Null(response.Details);
    }

    [Fact]
    public async Task Pay_aborts_when_the_quoted_fee_exceeds_a_percentage_limit()
    {
        var (client, sdk, _, _, _) = Create();
        sdk.NextQuote = new SparkSendQuote(1000, 50, Hash);

        // 1% of 1000 sat is 10 sat.
        var response = await client.Pay(Bolt11, new PayInvoiceParams { MaxFeePercent = 1 }, Ct);

        Assert.Equal(PayResult.Error, response.Result);
    }

    [Fact]
    public void Pay_applies_the_stricter_of_the_two_fee_limits()
    {
        var quote = new SparkSendQuote(1000, 15, Hash);

        // 1% of 1000 = 10, flat limit 100: the percentage wins.
        Assert.NotNull(SparkLightningClient.ApproveFee(
            quote, new PayInvoiceParams { MaxFeePercent = 1, MaxFeeFlat = Money.Satoshis(100) }, 1000));
        // 5% of 1000 = 50, flat limit 10: the flat limit wins.
        Assert.NotNull(SparkLightningClient.ApproveFee(
            quote, new PayInvoiceParams { MaxFeePercent = 5, MaxFeeFlat = Money.Satoshis(10) }, 1000));
        // Both permissive.
        Assert.Null(SparkLightningClient.ApproveFee(
            quote, new PayInvoiceParams { MaxFeePercent = 5, MaxFeeFlat = Money.Satoshis(100) }, 1000));
        // No caller limit still applies the plugin's default, which is generous enough to allow a 15 sat fee on
        // 1 000 sat but not an absurd one.
        Assert.Null(SparkLightningClient.ApproveFee(quote, new PayInvoiceParams(), 1000));
        Assert.Null(SparkLightningClient.ApproveFee(quote, null, 1000));
        Assert.NotNull(SparkLightningClient.ApproveFee(new SparkSendQuote(1000, 500, Hash), null, 1000));
    }

    [Fact]
    public void Pay_refuses_a_negative_fee_quote_under_every_limit()
    {
        // A provider u64 fee cast raw to long wraps negative past long.MaxValue, and every limit here is a
        // `<=` — so a wrapped fee would pass the caller's limit, the default limit, all of them. The
        // conversions saturate now, and this refusal is the backstop for any path that missed one.
        var negative = new SparkSendQuote(1000, -1, Hash);

        Assert.NotNull(SparkLightningClient.ApproveFee(negative, null, 1000));
        Assert.NotNull(SparkLightningClient.ApproveFee(
            negative, new PayInvoiceParams { MaxFeeFlat = Money.Satoshis(1_000_000) }, 1000));
        Assert.NotNull(SparkLightningClient.ApproveFee(
            new SparkSendQuote(1000, long.MinValue, Hash), new PayInvoiceParams(), 1000));
    }

    [Fact]
    public void Provider_fee_conversions_saturate_instead_of_wrapping()
    {
        Assert.Equal(5, SparkSdkClient.ToSatsSaturating(5));
        Assert.Equal(long.MaxValue, SparkSdkClient.ToSatsSaturating((ulong)long.MaxValue));
        Assert.Equal(long.MaxValue, SparkSdkClient.ToSatsSaturating((ulong)long.MaxValue + 1));
        Assert.Equal(long.MaxValue, SparkSdkClient.ToSatsSaturating(ulong.MaxValue));

        Assert.Equal(7, SparkSdkClient.SaturatingAdd(3, 4));
        Assert.Equal(long.MaxValue, SparkSdkClient.SaturatingAdd(long.MaxValue, 1));
        Assert.Equal(long.MaxValue, SparkSdkClient.SaturatingAdd(long.MaxValue - 3, 10));
        Assert.Equal(long.MaxValue, SparkSdkClient.SaturatingAdd(long.MaxValue, long.MaxValue));
    }

    [Fact]
    public void An_extreme_requested_amount_does_not_wrap_into_an_amountless_invoice()
    {
        // (msat + 999) / 1000 wraps negative near long.MaxValue, and a non-positive result downstream means
        // "amountless invoice" — an absurd requested amount must stay absurd, never become anything-goes.
        var sats = SparkLightningClient.ToAmountSats(LightMoney.MilliSatoshis(long.MaxValue));

        Assert.NotNull(sats);
        Assert.Equal(long.MaxValue / 1000 + 1, sats);
    }

    [Fact]
    public async Task Pay_requires_an_amount_for_an_amountless_invoice()
    {
        var (client, sdk, _, _, _) = Create(new Bolt11Info(Hash, DateTimeOffset.UtcNow.AddHours(1), null));

        var response = await client.Pay(Bolt11, new PayInvoiceParams(), Ct);

        Assert.Equal(PayResult.Error, response.Result);
        Assert.Empty(sdk.SendCalls);
    }

    [Fact]
    public async Task Pay_passes_an_explicit_amount_only_for_an_amountless_invoice()
    {
        var (amountless, amountlessSdk, _, _, _) = Create(new Bolt11Info(Hash, DateTimeOffset.UtcNow.AddHours(1), null));
        await amountless.Pay(Bolt11, new PayInvoiceParams { Amount = LightMoney.Satoshis(500) }, Ct);
        Assert.Equal(500, Assert.Single(amountlessSdk.SendCalls).AmountSats);

        var (fixedAmount, fixedSdk, _, _, _) = Create();
        await fixedAmount.Pay(Bolt11, new PayInvoiceParams { Amount = LightMoney.Satoshis(500) }, Ct);
        // The invoice already carries an amount; overriding it would be rejected or, worse, honoured.
        Assert.Null(Assert.Single(fixedSdk.SendCalls).AmountSats);
    }

    [Fact]
    public async Task Pay_rejects_a_spontaneous_payment()
    {
        var (client, sdk, _, _, _) = Create();

        var response = await client.Pay(new PayInvoiceParams { Destination = new Key().PubKey }, Ct);

        Assert.Equal(PayResult.Error, response.Result);
        Assert.Contains("keysend", response.ErrorDetail);
        Assert.Empty(sdk.SendCalls);
    }

    [Fact]
    public async Task Pay_rejects_an_unparseable_invoice()
    {
        var (client, sdk, _, _, _) = Create();

        var response = await client.Pay("garbage", new PayInvoiceParams(), Ct);

        Assert.Equal(PayResult.Error, response.Result);
        Assert.Empty(sdk.SendCalls);
    }

    [Fact]
    public async Task Pay_returns_Unknown_for_an_interrupted_send()
    {
        // An interrupted send may still be in flight on the SSP, so it must not be reported as a failure the
        // payout processor would retry with a different amount.
        //
        // FailSendWith, not FailWith: the latter throws from *every* fake method including the idempotency
        // probe, so this test used to fail before a send was ever attempted and still assert Unknown — which is
        // precisely the misclassification audit finding PaymentFlow F2 is about. Failing only the send is what
        // the test always claimed to do.
        var (client, sdk, _, _, _) = Create();
        sdk.FailSendWith = new TimeoutException("network");

        var response = await client.Pay(Bolt11, new PayInvoiceParams(), Ct);

        Assert.Equal(PayResult.Unknown, response.Result);
    }

    [Fact]
    public async Task Pay_reports_a_failed_idempotency_probe_as_a_definite_error_not_Unknown()
    {
        // Audit finding PaymentFlow F2. The probe runs before anything is sent — it is what decides whether to
        // send at all — so a blip there spent nothing. Reporting Unknown made BTCPay mark the payout InProgress,
        // and LightningPendingPayoutListener then resolved it through GetPayment, found nothing, and cancelled
        // it: ten minutes later the claimant has to re-claim, over a failure that never touched the wallet.
        var (client, sdk, _, _, _) = Create();
        sdk.FailWith = new TimeoutException("network");

        var response = await client.Pay(Bolt11, new PayInvoiceParams(), Ct);

        // Error returns the payout to AwaitingPayment for an immediate, safe retry.
        Assert.Equal(PayResult.Error, response.Result);
        Assert.Empty(sdk.SendCalls);
    }

    [Fact]
    public async Task Pay_reports_a_pending_send_as_unknown()
    {
        var (client, sdk, _, _, _) = Create();
        sdk.NextSendResult = new SparkPayment(
            "send-1", SparkPaymentDirection.Send, SparkPaymentStatus.Pending, SparkPaymentMethod.Lightning,
            1000, 4, DateTimeOffset.UnixEpoch, Hash, Bolt11, null, null);

        var response = await client.Pay(Bolt11, new PayInvoiceParams(), Ct);

        Assert.Equal(PayResult.Unknown, response.Result);
        Assert.Equal(LightningPaymentStatus.Pending, response.Details!.Status);
    }

    [Fact]
    public async Task GetPayment_finds_a_send_by_payment_hash()
    {
        var (client, sdk, _, _, _) = Create();
        sdk.Payments.Add(new SparkPayment(
            "send-1", SparkPaymentDirection.Send, SparkPaymentStatus.Completed, SparkPaymentMethod.Lightning,
            1000, 4, DateTimeOffset.UnixEpoch, Hash, Bolt11, PaymentFixture.Preimage, null));

        var payment = await client.GetPayment(Hash, Ct);

        Assert.Equal(Hash, payment.PaymentHash);
        Assert.Equal(LightMoney.Satoshis(1000), payment.Amount);
        Assert.Equal(LightMoney.Satoshis(1004), payment.AmountSent);
        Assert.Equal(LightMoney.Satoshis(4), payment.Fee);
        Assert.Equal(SparkPaymentDirection.Send, Assert.Single(sdk.ListQueries).Direction);
    }

    [Fact]
    public async Task GetPayment_finds_a_send_pushed_off_the_first_page_of_history()
    {
        // Up to BTCPay 2.4.5, returning null here cancels a payout: LightningPendingPayoutListener maps a null
        // payment through `_ => PayoutState.Cancelled`, so failing to find a send that really happened marks the
        // payout cancelled while the recipient keeps the sats (2.4.6 leaves it InProgress instead). A single
        // newest-first page would do exactly that.
        var harness = Create();
        var target = new SparkPayment(
            "target", SparkPaymentDirection.Send, SparkPaymentStatus.Completed, SparkPaymentMethod.Lightning,
            1000, 4, DateTimeOffset.UtcNow.AddHours(-2), Hash, Bolt11, PaymentFixture.Preimage, null);
        harness.Sdk.Payments.Add(target);
        for (var i = 0; i < 120; i++)
        {
            harness.Sdk.Payments.Add(new SparkPayment(
                $"later-{i}", SparkPaymentDirection.Send, SparkPaymentStatus.Completed,
                SparkPaymentMethod.Lightning, 10, 1, DateTimeOffset.UtcNow, i.ToString("x64"), "lnbcrt-other",
                null, null));
        }

        var payment = await harness.Client.GetPayment(Hash, Ct);

        Assert.NotNull(payment);
        Assert.Equal(Hash, payment.PaymentHash);
        Assert.True(harness.Sdk.ListQueries.Count > 1, "the scan must page rather than read one page");
    }

    [Fact]
    public async Task GetPayment_prefers_the_point_lookup_on_the_derived_idempotency_key()
    {
        // One query instead of up to ten, and it works however long the history is.
        var harness = Create();
        var key = SparkLightningClient.DeriveIdempotencyKey(Hash);
        harness.Sdk.PaymentsById[key] = CompletedSend(key);

        var payment = await harness.Client.GetPayment(Hash, Ct);

        Assert.NotNull(payment);
        Assert.Equal(Hash, payment.PaymentHash);
        Assert.Empty(harness.Sdk.ListQueries);
    }

    [Fact]
    public async Task GetPayment_ignores_the_receive_leg_of_a_self_payment()
    {
        var harness = Create();
        harness.Sdk.Seed(Receive());

        Assert.Null(await harness.Client.GetPayment(Hash, Ct));
    }

    [Fact]
    public async Task GetPayment_returns_null_for_an_unknown_or_malformed_hash()
    {
        var (client, _, _, _, _) = Create();

        Assert.Null(await client.GetPayment(new string('d', 64), Ct));
        Assert.Null(await client.GetPayment("garbage", Ct));
    }

    #endregion

    #region Listing

    [Fact]
    public async Task ListInvoices_reads_the_plugin_table_not_the_SDK()
    {
        // The SDK has no record of unpaid invoices at all, so this can only come from our own table.
        var (client, sdk, store, _, _) = Create();
        Seed(store);
        store.Seed(new InvoiceRecord
        {
            PaymentHash = new string('b', 64),
            StoreId = StoreId,
            Bolt11 = "lnbcrt-two",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        });

        var invoices = await client.ListInvoices(Ct);

        Assert.Equal(2, invoices.Length);
        Assert.Empty(sdk.ListQueries);
    }

    [Fact]
    public async Task ListInvoices_can_filter_to_still_payable_invoices()
    {
        var (client, _, store, _, _) = Create();
        Seed(store);
        store.Seed(new InvoiceRecord
        {
            PaymentHash = new string('b', 64),
            StoreId = StoreId,
            Bolt11 = "lnbcrt-two",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1)
        });

        var invoices = await client.ListInvoices(new ListInvoicesParams { PendingOnly = true }, Ct);

        Assert.Equal(Hash, Assert.Single(invoices).Id);
    }

    [Fact]
    public async Task ListInvoices_does_not_leak_another_stores_invoices()
    {
        var (client, _, store, _, _) = Create();
        Seed(store);
        store.Seed(new InvoiceRecord
        {
            PaymentHash = new string('b', 64),
            StoreId = "another-store",
            Bolt11 = "lnbcrt-two",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        });

        Assert.Equal(Hash, Assert.Single(await client.ListInvoices(Ct)).Id);
    }

    [Fact]
    public async Task ListPayments_reads_sends_from_the_SDK()
    {
        var (client, sdk, _, _, _) = Create();
        sdk.Payments.Add(new SparkPayment(
            "send-1", SparkPaymentDirection.Send, SparkPaymentStatus.Completed, SparkPaymentMethod.Lightning,
            1000, 4, DateTimeOffset.UnixEpoch, Hash, Bolt11, null, null));
        sdk.Payments.Add(Receive());

        var payments = await client.ListPayments(Ct);

        Assert.Equal("send-1", Assert.Single(payments).Id);
    }

    #endregion

    #region Pure helpers

    [Theory]
    [InlineData(null, null)]
    [InlineData(0L, null)]
    [InlineData(-1L, null)]
    [InlineData(1L, 1L)]
    [InlineData(1000L, 1L)]
    [InlineData(1001L, 2L)]
    [InlineData(1_999L, 2L)]
    public void ToAmountSats_rounds_up_and_treats_zero_as_amountless(long? msat, long? expected)
    {
        var amount = msat is null ? null : LightMoney.MilliSatoshis(msat.Value);

        Assert.Equal(expected, SparkLightningClient.ToAmountSats(amount));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0L, null)]
    [InlineData(-1L, null)]
    [InlineData(1L, null)]
    [InlineData(999L, null)]
    [InlineData(1000L, 1L)]
    [InlineData(1001L, 1L)]
    [InlineData(1_999L, 1L)]
    public void ToSendAmountSats_rounds_down(long? msat, long? expected)
    {
        // The mirror of the receive side, rounding the other way. Both round in the merchant's favour: up when
        // charging, down when paying. Rounding up here would overpay a recipient by up to a satoshi per payout.
        var amount = msat is null ? null : LightMoney.MilliSatoshis(msat.Value);

        Assert.Equal(expected, SparkLightningClient.ToSendAmountSats(amount));
    }

    [Fact]
    public async Task Pay_floors_a_sub_satoshi_amount_on_an_amountless_invoice()
    {
        var harness = Create(new Bolt11Info(Hash, DateTimeOffset.UtcNow.AddHours(1), null));

        await harness.Client.Pay(
            Bolt11, new PayInvoiceParams { Amount = LightMoney.MilliSatoshis(1999) }, Ct);

        Assert.Equal(1, Assert.Single(harness.Sdk.SendCalls).AmountSats);
    }

    [Fact]
    public void TruncateUtf8_does_not_split_a_multi_byte_character()
    {
        // The 639-byte limit is in bytes, so a description of emoji hits it after 159 characters, not 639.
        var value = string.Concat(Enumerable.Repeat("☕", 300));

        var truncated = SparkLightningClient.TruncateUtf8(value, 639);

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(truncated) <= 639);
        Assert.Equal(213, truncated.Length);
        Assert.DoesNotContain('�', truncated);
    }

    [Fact]
    public void TruncateUtf8_leaves_short_values_alone()
    {
        Assert.Equal("order 42", SparkLightningClient.TruncateUtf8("order 42", 639));
        Assert.Equal(string.Empty, SparkLightningClient.TruncateUtf8(string.Empty, 639));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("abc", null)]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", null)]
    public void NormaliseHash_rejects_anything_that_is_not_32_bytes_of_hex(string? input, string? expected)
    {
        Assert.Equal(expected, SparkLightningClient.NormaliseHash(input));
    }

    [Fact]
    public void NormaliseHash_lower_cases_and_trims()
    {
        var upper = new string('A', 64);

        Assert.Equal(new string('a', 64), SparkLightningClient.NormaliseHash($"  {upper} "));
    }

    #endregion

    private static InvoiceRecord Seed(
        InMemoryInvoiceRecordStore store,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? expiresAt = null,
        string? sdkPaymentId = null)
    {
        var record = new InvoiceRecord
        {
            PaymentHash = Hash,
            StoreId = StoreId,
            Bolt11 = Bolt11,
            AmountMsat = 100_000,
            Description = "order 42",
            SdkPaymentId = sdkPaymentId,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddHours(1),
            Status = InvoiceRecordStatus.Unpaid
        };
        store.Seed(record);
        return record;
    }

    private static SparkPayment Receive(long amountSats = 100) => new(
        "sdk-1",
        SparkPaymentDirection.Receive,
        SparkPaymentStatus.Completed,
        SparkPaymentMethod.Lightning,
        amountSats,
        0,
        DateTimeOffset.UtcNow,
        Hash,
        Bolt11,
        // The preimage that really hashes to Hash: BTCPay recomputes sha256(preimage) and silently discards a
        // mismatch, so an arbitrary value here would pass these tests while breaking in production.
        PaymentFixture.Preimage,
        "order 42");
}
