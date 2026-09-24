using System.Numerics;
using Breez.Sdk.Spark;
using BTCPayServer.Plugins.Flint.Payments;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// Accepting USDC and USDT: which networks an invoice offers, the quote a payer is shown, and the crediting of
/// what arrives.
/// </summary>
/// <remarks>
/// Driven through the real service over the fake SDK, whose receive quotes are sized the way the SDK sizes them
/// (<c>FeesExcluded</c>: the deposit is the due plus the provider's fee) and whose arrivals carry the provider's
/// conversion details exactly as the SDK freezes them from the quote.
/// </remarks>
public class StablecoinPaymentServiceTests
{
    private const string StoreId = "store-1";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(StablecoinHarness Harness, FakeSparkSdkClient Sdk)
    {
        public StablecoinPaymentService Service => Harness.Service;
        public FakeStablecoinInvoiceGateway Invoices => Harness.Invoices;
        public InMemoryStablecoinQuoteStore Quotes => Harness.Quotes;
    }

    private static Setup Create(bool available = true, bool walletRunning = true)
    {
        var sdk = new FakeSparkSdkClient();
        var runtime = new FakeSparkStoreRuntime();
        if (walletRunning)
            runtime.Clients[StoreId] = sdk;
        return new Setup(new StablecoinHarness(runtime, available), sdk);
    }

    /// <summary>An invoice with both prompts, offering every network the fake's route table carries for each.</summary>
    private static FakeStablecoinInvoiceGateway.Invoice Invoice(Setup setup, string id = "invoice-1", decimal due = 10m)
    {
        var usdc = StablecoinPaymentService
            .SelectNetworks(setup.Sdk.CrossChainReceiveRoutes, StablecoinPayments.Usdc, due)
            .Select(n => n.Chain).ToArray();
        var usdt = StablecoinPaymentService
            .SelectNetworks(setup.Sdk.CrossChainReceiveRoutes, StablecoinPayments.Usdt, due)
            .Select(n => n.Chain).ToArray();
        var invoice = setup.Invoices.Add(id, StoreId, due, (StablecoinPayments.Usdc, usdc), (StablecoinPayments.Usdt, usdt));
        setup.Invoices.SetContracts(id, StablecoinPayments.Usdc, setup.Sdk.CrossChainReceiveRoutes);
        setup.Invoices.SetContracts(id, StablecoinPayments.Usdt, setup.Sdk.CrossChainReceiveRoutes);
        return invoice;
    }

    private static async Task<StablecoinActiveQuote> QuoteOk(
        Setup setup, string chain, string invoiceId = "invoice-1", StablecoinAsset? asset = null)
    {
        var result = await setup.Service.QuoteAsync(
            invoiceId, (asset ?? StablecoinPayments.Usdc).PaymentMethodId, chain, Ct);
        Assert.True(result.Quote is not null, result.Error);
        return result.Quote!;
    }

    #region Networks

    [Fact]
    public void An_invoice_offers_exactly_this_coin_on_the_routes_that_can_land_it_as_bitcoin()
    {
        var routes = new FakeSparkSdkClient().CrossChainReceiveRoutes;

        var usdc = StablecoinPaymentService.SelectNetworks(routes, StablecoinPayments.Usdc, 10m);
        var usdt = StablecoinPaymentService.SelectNetworks(routes, StablecoinPayments.Usdt, 10m);

        // arc lands only as a token; USDT0 is not USDT; Boltz serves no receive. Ordered the way payers hold them.
        Assert.Equal(["ethereum", "solana", "base", "bsc"], usdc.Select(n => n.Chain).ToArray());
        Assert.Equal(["tron", "ethereum", "bsc"], usdt.Select(n => n.Chain).ToArray());
        Assert.Equal("BNB Chain", usdc.Single(n => n.Chain == "bsc").Name);
        Assert.Equal("0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913", usdc.Single(n => n.Chain == "base").ContractAddress);
    }

    [Fact]
    public void A_published_band_that_excludes_the_due_hides_that_network()
    {
        var routes = new List<SparkCrossChainReceiveRoute>
        {
            FakeSparkSdkClient.ReceiveRoute("ethereum", "1", "USDC", "0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48", 6,
                bitcoinLimits: new SparkCrossChainLimits(null, null, MinUsdCents: 2_000, MaxUsdCents: null)),
            FakeSparkSdkClient.ReceiveRoute("base", "8453", "USDC", "0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913", 6)
        };

        Assert.Equal(["base"], StablecoinPaymentService.SelectNetworks(routes, StablecoinPayments.Usdc, 10m).Select(n => n.Chain).ToArray());
        Assert.Equal(["ethereum", "base"], StablecoinPaymentService.SelectNetworks(routes, StablecoinPayments.Usdc, 25m).Select(n => n.Chain).ToArray());
    }

    [Fact]
    public async Task No_networks_are_offered_off_mainnet_or_without_a_running_wallet()
    {
        var offMainnet = Create(available: false);
        var (networks, why) = await offMainnet.Service.GetNetworksAsync(StoreId, StablecoinPayments.Usdc, 10m, Ct);
        Assert.Empty(networks);
        Assert.Contains("mainnet", why);

        var noWallet = Create(walletRunning: false);
        (networks, why) = await noWallet.Service.GetNetworksAsync(StoreId, StablecoinPayments.Usdc, 10m, Ct);
        Assert.Empty(networks);
        Assert.Contains("not running", why);
    }

    [Fact]
    public async Task A_provider_that_cannot_list_routes_costs_the_invoice_its_stablecoin_option_not_the_invoice()
    {
        var setup = Create();
        setup.Sdk.FailCrossChainReceiveRoutesWith = new SdkException.NetworkException("@v1=provider unreachable");

        var (networks, why) = await setup.Service.GetNetworksAsync(StoreId, StablecoinPayments.Usdc, 10m, Ct);

        Assert.Empty(networks);
        Assert.NotNull(why);
    }

    #endregion

    #region Quoting

    [Fact]
    public async Task A_quote_asks_for_the_due_plus_the_routes_cost_and_shows_it_as_the_network_fee()
    {
        var setup = Create();
        var invoice = Invoice(setup);

        var quote = await QuoteOk(setup, "base");

        // Asked the SDK for exactly the due, in the route's units…
        var call = Assert.Single(setup.Sdk.CrossChainReceiveCalls);
        Assert.Equal(new BigInteger(10_000_000), call.Amount);
        Assert.Equal(StablecoinPayments.MaxSlippageBps, call.MaxSlippageBps);
        // …and the payer for the SDK's deposit: 10 + 0.05 fixed + 0.3%.
        Assert.Equal("10.08", quote.Amount);
        Assert.Equal(0.08m, quote.Fee);
        Assert.Equal(10m, quote.Due);
        Assert.Equal(
            $"ethereum:0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913@8453/transfer?address={quote.DepositAddress}&uint256=10080000",
            quote.PaymentRequest);

        // Shown on the prompt with that cost, and recorded before anybody could have been shown the address.
        Assert.Equal((quote.DepositAddress, 0.08m), invoice.Shown[StablecoinPayments.Usdc.PaymentMethodId]);
        var record = Assert.Single(setup.Quotes.Quotes);
        Assert.Equal(quote.QuoteId, record.Id);
        Assert.Equal("10080000", record.AskedBaseUnits);
        Assert.Equal("BTC", record.DestinationAsset);
    }

    [Fact]
    public async Task An_eighteen_decimal_route_is_quoted_in_its_own_units()
    {
        var setup = Create();
        Invoice(setup);

        var quote = await QuoteOk(setup, "bsc");

        var call = Assert.Single(setup.Sdk.CrossChainReceiveCalls);
        Assert.Equal(BigInteger.Parse("10000000000000000000"), call.Amount);
        Assert.Equal("10.08", quote.Amount);
        Assert.EndsWith("&uint256=10080000000000000000", quote.PaymentRequest);
    }

    [Fact]
    public async Task A_quote_on_tron_or_solana_hands_the_wallet_the_bare_address()
    {
        var setup = Create();
        Invoice(setup);

        var tron = await QuoteOk(setup, "tron", asset: StablecoinPayments.Usdt);
        var solana = await QuoteOk(setup, "solana");

        Assert.Equal(tron.DepositAddress, tron.PaymentRequest);
        Assert.Equal(solana.DepositAddress, solana.PaymentRequest);
    }

    [Fact]
    public async Task Two_equal_invoices_on_one_route_are_asked_for_different_amounts()
    {
        // The fake quotes deterministically, so without the nudge both payers would be asked for 10.08 and an exact
        // payment could not say whose it was.
        var setup = Create();
        Invoice(setup, "invoice-1");
        Invoice(setup, "invoice-2");

        var first = await QuoteOk(setup, "base", "invoice-1");
        var second = await QuoteOk(setup, "base", "invoice-2");

        Assert.Equal("10.08", first.Amount);
        Assert.Equal("10.080001", second.Amount);
        Assert.Equal(0.080001m, second.Fee);
    }

    [Fact]
    public async Task A_live_quote_for_the_same_network_and_due_is_reused_rather_than_minted_again()
    {
        var setup = Create();
        Invoice(setup);

        var first = await QuoteOk(setup, "base");
        var again = await QuoteOk(setup, "base");

        Assert.Equal(first.QuoteId, again.QuoteId);
        Assert.Single(setup.Sdk.CrossChainReceiveCalls);
        Assert.Single(setup.Quotes.Quotes);
    }

    [Fact]
    public async Task A_changed_due_is_a_fresh_quote()
    {
        var setup = Create();
        var invoice = Invoice(setup);
        var first = await QuoteOk(setup, "base");

        invoice.Due = 4m;
        var second = await QuoteOk(setup, "base");

        Assert.NotEqual(first.QuoteId, second.QuoteId);
        Assert.Equal(new BigInteger(4_000_000), setup.Sdk.CrossChainReceiveCalls[1].Amount);
    }

    [Fact]
    public async Task One_invoice_may_ask_for_only_so_many_quotes()
    {
        var setup = Create();
        var invoice = Invoice(setup);
        for (var i = 0; i < StablecoinPayments.MaxQuotesPerInvoice; i++)
        {
            invoice.Due = 10m + i;
            await QuoteOk(setup, "base");
        }

        invoice.Due = 99m;
        var refused = await setup.Service.QuoteAsync("invoice-1", StablecoinPayments.Usdc.PaymentMethodId, "base", Ct);

        Assert.Null(refused.Quote);
        Assert.Contains("as many quotes", refused.Error);
        Assert.Equal(StablecoinPayments.MaxQuotesPerInvoice, setup.Sdk.CrossChainReceiveCalls.Count);
    }

    [Theory]
    [InlineData("polygon")]
    [InlineData("arc")]
    [InlineData("")]
    public async Task A_network_the_invoice_does_not_offer_is_refused_without_asking_the_provider(string chain)
    {
        var setup = Create();
        Invoice(setup);

        var result = await setup.Service.QuoteAsync("invoice-1", StablecoinPayments.Usdc.PaymentMethodId, chain, Ct);

        Assert.Null(result.Quote);
        Assert.NotNull(result.Error);
        Assert.Empty(setup.Sdk.CrossChainReceiveCalls);
    }

    [Fact]
    public async Task An_invoice_that_can_no_longer_be_paid_is_not_quoted()
    {
        var setup = Create();
        var invoice = Invoice(setup);
        invoice.Payable = false;

        var result = await setup.Service.QuoteAsync("invoice-1", StablecoinPayments.Usdc.PaymentMethodId, "base", Ct);

        Assert.Equal("This invoice can no longer be paid.", result.Error);
        Assert.Empty(setup.Sdk.CrossChainReceiveCalls);
    }

    [Fact]
    public async Task An_unknown_invoice_or_payment_method_is_not_found()
    {
        var setup = Create();
        Invoice(setup);

        Assert.True((await setup.Service.QuoteAsync("nope", StablecoinPayments.Usdc.PaymentMethodId, "base", Ct)).NotFound);
        Assert.True((await setup.Service.QuoteAsync(
            "invoice-1", new BTCPayServer.Payments.PaymentMethodId("BTC-LN"), "base", Ct)).NotFound);
    }

    [Fact]
    public async Task The_providers_refusal_reaches_the_payer_in_its_own_words()
    {
        var setup = Create();
        Invoice(setup);
        setup.Sdk.FailCrossChainReceiveWith = new SdkException.CrossChainAmountOutOfRange(
            "Amount below the route minimum of $20", true, null, 2_000);

        var result = await setup.Service.QuoteAsync("invoice-1", StablecoinPayments.Usdc.PaymentMethodId, "ethereum", Ct);

        Assert.Null(result.Quote);
        Assert.Contains("Amount below the route minimum of $20", result.Error);
        Assert.Empty(setup.Quotes.Quotes);
    }

    [Fact]
    public async Task A_route_that_costs_more_than_half_the_due_is_refused()
    {
        var setup = Create();
        Invoice(setup, due: 1m);
        setup.Sdk.ReceiveFixedFeeMicroUsd = 3_000_000;

        var result = await setup.Service.QuoteAsync("invoice-1", StablecoinPayments.Usdc.PaymentMethodId, "ethereum", Ct);

        Assert.Null(result.Quote);
        Assert.Contains("cheaper network", result.Error);
        Assert.Empty(setup.Quotes.Quotes);
    }

    [Fact]
    public async Task Nothing_is_quoted_off_mainnet()
    {
        var setup = Create(available: false);
        Invoice(setup);

        var result = await setup.Service.QuoteAsync("invoice-1", StablecoinPayments.Usdc.PaymentMethodId, "base", Ct);

        Assert.Null(result.Quote);
        Assert.Empty(setup.Sdk.CrossChainReceiveCalls);
    }

    #endregion

    #region Crediting

    private static SparkCrossChainReceiveQuote SdkQuoteFor(Setup setup, StablecoinActiveQuote shown)
    {
        var record = setup.Quotes.Quotes.Single(q => q.Id == shown.QuoteId);
        var route = setup.Sdk.CrossChainReceiveRoutes.First(r => r.Chain == record.Chain && r.Asset == record.Asset);
        return new SparkCrossChainReceiveQuote(
            route,
            record.DepositAddress,
            BigInteger.Parse(record.DepositBaseUnits),
            record.ExpectedReceived,
            record.DestinationAsset,
            record.DestinationAsset == "BTC" ? null : FakeSparkSdkClient.Usdb.Value,
            record.ServiceFee,
            record.ServiceFeeAsset,
            record.ExpiresAt,
            record.PaymentRequest);
    }

    [Fact]
    public async Task An_exact_payment_settles_exactly_the_due_once_however_often_it_is_reported()
    {
        var setup = Create();
        var invoice = Invoice(setup);
        var shown = await QuoteOk(setup, "base");
        var arrival = FakeSparkSdkClient.CrossChainReceivePayment(
            SdkQuoteFor(setup, shown), "spark-pay-1", paid: 10_080_000);

        Assert.Equal(StablecoinReceiveOutcome.Credited, await setup.Service.TryCreditAsync(StoreId, arrival, Ct));
        Assert.Equal(StablecoinReceiveOutcome.AlreadyCredited, await setup.Service.TryCreditAsync(StoreId, arrival, Ct));

        var payment = Assert.Single(invoice.Payments);
        Assert.Equal(10.08m, payment.Value);
        Assert.Equal(0.08m, payment.Fee);
        Assert.Equal(10m, payment.Value - payment.Fee);
        Assert.Equal("spark-pay-1", payment.Details.SdkPaymentId);
        Assert.Equal("base", payment.Details.Chain);
        Assert.Equal("0xpayerspark-pay-1", payment.Details.ExternalTxHash);
        Assert.Equal(shown.DepositAddress, payment.Details.DepositAddress);
        Assert.NotNull(setup.Quotes.Quotes.Single().CreditedAt);
    }

    [Fact]
    public async Task A_short_payment_is_credited_with_what_arrived()
    {
        var setup = Create();
        var invoice = Invoice(setup);
        var shown = await QuoteOk(setup, "base");
        var arrival = FakeSparkSdkClient.CrossChainReceivePayment(
            SdkQuoteFor(setup, shown), "spark-pay-1", paid: 6_000_000);

        Assert.Equal(StablecoinReceiveOutcome.Credited, await setup.Service.TryCreditAsync(StoreId, arrival, Ct));

        var payment = Assert.Single(invoice.Payments);
        Assert.Equal(6m, payment.Value);
        Assert.Equal(0.08m, payment.Fee);
    }

    [Fact]
    public async Task A_payment_to_an_earlier_networks_quote_records_that_networks_cost()
    {
        // The payer opened Ethereum, switched to Base, and then paid the Ethereum address after all. The prompt shows
        // Base's cost by now; the payment must carry Ethereum's, or the invoice reads as short by the difference.
        var setup = Create();
        var invoice = Invoice(setup);
        setup.Sdk.ReceiveFixedFeeMicroUsd = 1_500_000;
        var ethereum = await QuoteOk(setup, "ethereum");
        setup.Sdk.ReceiveFixedFeeMicroUsd = 50_000;
        await QuoteOk(setup, "base");

        var paid = StablecoinAmounts.ToBaseUnits(decimal.Parse(ethereum.Amount, System.Globalization.CultureInfo.InvariantCulture), 6);
        var arrival = FakeSparkSdkClient.CrossChainReceivePayment(SdkQuoteFor(setup, ethereum), "spark-pay-1", paid: paid);
        await setup.Service.TryCreditAsync(StoreId, arrival, Ct);

        var payment = Assert.Single(invoice.Payments);
        Assert.Equal(1.53m, payment.Fee);
        Assert.Equal(10m, payment.Value - payment.Fee);
    }

    [Fact]
    public async Task Equal_invoices_landing_as_usdb_are_attributed_by_the_exact_amount_paid()
    {
        // Landing as USDB, equal dues freeze an identical estimate and fee: only the nudged ask tells them apart.
        var setup = Create();
        setup.Sdk.ReceiveLandsAsToken = true;
        var first = Invoice(setup, "invoice-1");
        var second = Invoice(setup, "invoice-2");
        await QuoteOk(setup, "base", "invoice-1");
        var secondQuote = await QuoteOk(setup, "base", "invoice-2");

        var arrival = FakeSparkSdkClient.CrossChainReceivePayment(
            SdkQuoteFor(setup, secondQuote), "spark-pay-2", paid: 10_080_001);
        Assert.Equal(StablecoinReceiveOutcome.Credited, await setup.Service.TryCreditAsync(StoreId, arrival, Ct));

        Assert.Empty(first.Payments);
        Assert.Single(second.Payments);
        Assert.Equal("USDB", second.Payments[0].Details.DestinationAsset);
    }

    [Fact]
    public async Task Equal_invoices_and_an_inexact_payment_are_left_for_a_human_and_reported_once()
    {
        var setup = Create();
        setup.Sdk.ReceiveLandsAsToken = true;
        var first = Invoice(setup, "invoice-1");
        var second = Invoice(setup, "invoice-2");
        var quote = await QuoteOk(setup, "base", "invoice-1");
        await QuoteOk(setup, "base", "invoice-2");

        var arrival = FakeSparkSdkClient.CrossChainReceivePayment(SdkQuoteFor(setup, quote), "spark-pay-1", paid: 9_999_999);
        Assert.Equal(StablecoinReceiveOutcome.Unattributed, await setup.Service.TryCreditAsync(StoreId, arrival, Ct));
        Assert.Equal(StablecoinReceiveOutcome.Unattributed, await setup.Service.TryCreditAsync(StoreId, arrival, Ct));

        Assert.Empty(first.Payments);
        Assert.Empty(second.Payments);
        Assert.Single(setup.Harness.Log.Lines, line => line.Contains("spark-pay-1") && line.StartsWith("Warning"));
    }

    [Fact]
    public async Task A_receive_this_plugin_did_not_quote_is_not_credited_to_anything()
    {
        var setup = Create();
        var invoice = Invoice(setup);
        var shown = await QuoteOk(setup, "base");
        var foreign = SdkQuoteFor(setup, shown) with { ExpectedReceivedAmount = 12_345 };

        var arrival = FakeSparkSdkClient.CrossChainReceivePayment(foreign, "spark-pay-1", paid: 10_080_000);

        Assert.Equal(StablecoinReceiveOutcome.Unattributed, await setup.Service.TryCreditAsync(StoreId, arrival, Ct));
        Assert.Empty(invoice.Payments);
    }

    [Fact]
    public async Task A_receive_without_its_conversion_details_is_not_the_stablecoin_paths_yet()
    {
        var setup = Create();
        Invoice(setup);
        var shown = await QuoteOk(setup, "base");

        var bare = FakeSparkSdkClient.CrossChainReceivePayment(SdkQuoteFor(setup, shown), "spark-pay-1", withConversion: false);
        var claiming = FakeSparkSdkClient.CrossChainReceivePayment(
            SdkQuoteFor(setup, shown), "spark-pay-1", status: SparkPaymentStatus.Pending);

        Assert.Equal(StablecoinReceiveOutcome.NotStablecoin, await setup.Service.TryCreditAsync(StoreId, bare, Ct));
        Assert.Equal(StablecoinReceiveOutcome.Pending, await setup.Service.TryCreditAsync(StoreId, claiming, Ct));
        Assert.True(await setup.Service.HasOpenQuotesAsync(StoreId, Ct));
        Assert.Null(setup.Quotes.Quotes.Single().SdkPaymentId);
    }

    [Fact]
    public async Task A_credit_that_did_not_land_is_retried_by_the_reconciliation_pass()
    {
        var setup = Create();
        var invoice = Invoice(setup);
        var shown = await QuoteOk(setup, "base");
        var arrival = FakeSparkSdkClient.CrossChainReceivePayment(SdkQuoteFor(setup, shown), "spark-pay-1", paid: 10_080_000);

        setup.Invoices.FailPaymentsWith = new InvalidOperationException("database unavailable");
        Assert.Equal(StablecoinReceiveOutcome.CreditFailed, await setup.Service.TryCreditAsync(StoreId, arrival, Ct));
        Assert.Empty(invoice.Payments);
        Assert.NotNull(setup.Quotes.Quotes.Single().SdkPaymentId);

        setup.Invoices.FailPaymentsWith = null;
        Assert.Equal(1, await setup.Service.ReconcileAsync(Ct));

        Assert.Single(invoice.Payments);
        Assert.NotNull(setup.Quotes.Quotes.Single().CreditedAt);
        Assert.Equal(0, await setup.Service.ReconcileAsync(Ct));
    }

    [Fact]
    public async Task The_reconciliation_pass_credits_an_arrival_the_event_stream_dropped()
    {
        var setup = Create();
        var invoice = Invoice(setup);
        var shown = await QuoteOk(setup, "base");
        setup.Sdk.Seed(FakeSparkSdkClient.CrossChainReceivePayment(SdkQuoteFor(setup, shown), "spark-pay-1", paid: 10_080_000));
        // An ordinary Lightning receive in the same window is not the stablecoin path's to touch.
        setup.Sdk.Seed(new SparkPayment(
            "lightning-pay-1", SparkPaymentDirection.Receive, SparkPaymentStatus.Completed, SparkPaymentMethod.Lightning,
            1_000, 0, DateTimeOffset.UtcNow, PaymentFixture.PaymentHash, "lnbc1", null, null));

        Assert.Equal(1, await setup.Service.ReconcileAsync(Ct));

        Assert.Single(invoice.Payments);
        Assert.False(await setup.Service.HasOpenQuotesAsync(StoreId, Ct));
    }

    [Fact]
    public async Task A_store_with_no_open_quote_costs_the_pass_nothing_on_its_wallet()
    {
        var setup = Create();
        Invoice(setup);

        Assert.Equal(0, await setup.Service.ReconcileAsync(Ct));

        Assert.Empty(setup.Sdk.ListQueries);
    }

    #endregion

    #region The store's switch

    [Fact]
    public async Task The_switch_turns_on_only_where_it_can_work_and_always_turns_off()
    {
        var mainnet = Create();
        Assert.True(await mainnet.Service.SetEnabledAsync(StoreId, true, Ct));
        Assert.True(await mainnet.Service.IsEnabledAsync(StoreId, Ct));
        Assert.True(await mainnet.Service.SetEnabledAsync(StoreId, false, Ct));
        Assert.False(await mainnet.Service.IsEnabledAsync(StoreId, Ct));

        var regtest = Create(available: false);
        Assert.False(await regtest.Service.SetEnabledAsync(StoreId, true, Ct));
        Assert.False(await regtest.Service.IsEnabledAsync(StoreId, Ct));
        Assert.True(await regtest.Service.SetEnabledAsync(StoreId, false, Ct));
    }

    #endregion
}
