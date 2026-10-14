using System.Numerics;
using Breez.Sdk.Spark;
using BTCPayServer.Plugins.Flint.Data;
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

    private static Setup Create(bool available = true, bool walletRunning = true, TimeProvider? time = null)
    {
        var sdk = new FakeSparkSdkClient();
        var runtime = new FakeSparkStoreRuntime();
        if (walletRunning)
            runtime.Clients[StoreId] = sdk;
        return new Setup(new StablecoinHarness(runtime, available, time), sdk);
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
    public void A_network_the_plugin_has_no_icon_for_is_not_offered()
    {
        // The icon is the payer's check that they are sending on the right network.
        var routes = new List<SparkCrossChainReceiveRoute>
        {
            FakeSparkSdkClient.ReceiveRoute("optimism", "10", "USDC", "0x0b2C639c533813f4Aa9D7837CAf62653d097Ff85", 6),
            FakeSparkSdkClient.ReceiveRoute("monad", "143", "USDC", "0x754704bc059f8c67012fed69bc8a327a5aafb603", 6),
            FakeSparkSdkClient.ReceiveRoute("base", "8453", "USDC", "0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913", 6)
        };

        Assert.Equal(["base"], StablecoinPaymentService.SelectNetworks(routes, StablecoinPayments.Usdc, 10m).Select(n => n.Chain).ToArray());
    }

    [Fact]
    public void A_published_floor_in_the_payers_token_above_the_due_hides_that_network()
    {
        var routes = new List<SparkCrossChainReceiveRoute>
        {
            FakeSparkSdkClient.ReceiveRoute("tron", "728126428", "USDT", "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t", 6,
                bitcoinLimits: new SparkCrossChainLimits(MinAmount: 5_000_000, MaxAmount: null, null, null)),
            FakeSparkSdkClient.ReceiveRoute("ethereum", "1", "USDT", "0xdAC17F958D2ee523a2206206994597C13D831ec7", 6)
        };

        Assert.Equal(["ethereum"], StablecoinPaymentService.SelectNetworks(routes, StablecoinPayments.Usdt, 3m).Select(n => n.Chain).ToArray());
        Assert.Equal(["tron", "ethereum"], StablecoinPaymentService.SelectNetworks(routes, StablecoinPayments.Usdt, 5m).Select(n => n.Chain).ToArray());
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
        // Tagged with the wallet that minted it: the checkout withdraws a quote whose tag is not the running wallet's.
        Assert.Equal($"wallet-of-{StoreId}", quote.WalletTag);
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

    private static (string Expected, string ServiceFee) Fingerprint(Setup setup, StablecoinActiveQuote shown)
    {
        var record = setup.Quotes.Quotes.Single(q => q.Id == shown.QuoteId);
        return (record.ExpectedReceivedBaseUnits, record.ServiceFeeBaseUnits);
    }

    [Fact]
    public async Task Equal_invoices_landing_as_usdb_get_quotes_with_different_fingerprints()
    {
        // Landing as USDB the SDK's sizing is deterministic and the estimate is floored to the cent, so equal dues
        // quote identical fingerprints — which is all their payments will be told apart by. The second request starts
        // past the open twin: a target a millionth higher, which moves the sub-cent remainder in the provider's fee.
        var setup = Create();
        setup.Sdk.ReceiveLandsAsToken = true;
        Invoice(setup, "invoice-1");
        Invoice(setup, "invoice-2");
        Invoice(setup, "invoice-3");

        var first = await QuoteOk(setup, "base", "invoice-1");
        var second = await QuoteOk(setup, "base", "invoice-2");
        var third = await QuoteOk(setup, "base", "invoice-3");

        Assert.Equal(
            [new BigInteger(10_000_000), new BigInteger(10_000_001), new BigInteger(10_000_002)],
            setup.Sdk.CrossChainReceiveCalls.Select(c => c.Amount).ToArray());
        Assert.Equal(3, new[] { first, second, third }.Select(q => Fingerprint(setup, q)).Distinct().Count());
        // Each costs its payer the millionths it was nudged by, and nothing more.
        Assert.Equal("10.08", first.Amount);
        Assert.Equal("10.080002", third.Amount);
    }

    [Fact]
    public async Task Equal_invoices_landing_as_sats_are_nudged_by_a_sats_worth()
    {
        // A millionth of a dollar is a hundredth of a sat at this price, so it would move neither half of the
        // fingerprint. The open twin says what a sat is worth, so the one provider call steps by that.
        var setup = Create();
        Invoice(setup, "invoice-1");
        Invoice(setup, "invoice-2");

        var first = await QuoteOk(setup, "base", "invoice-1");
        var second = await QuoteOk(setup, "base", "invoice-2");

        Assert.NotEqual(Fingerprint(setup, first), Fingerprint(setup, second));
        Assert.Equal(new BigInteger(10_001_000), setup.Sdk.CrossChainReceiveCalls[1].Amount);
        Assert.Equal(2, setup.Quotes.Quotes.Count);
        // Well under a cent more for the payer.
        Assert.True(second.Fee - first.Fee < 0.01m, $"{second.Fee} against {first.Fee}");
    }

    [Fact]
    public async Task A_provider_that_quotes_one_fingerprint_whatever_is_asked_is_refused_rather_than_shown()
    {
        var setup = Create();
        setup.Sdk.ReceiveFingerprint = (12_345, 67_890);
        Invoice(setup, "invoice-1");
        Invoice(setup, "invoice-2");
        await QuoteOk(setup, "base", "invoice-1");

        var result = await setup.Service.QuoteAsync("invoice-2", StablecoinPayments.Usdc.PaymentMethodId, "base", Ct);

        Assert.Null(result.Quote);
        Assert.Contains("busy", result.Error);
        Assert.Equal(1 + StablecoinPaymentService.MaxFingerprintAttempts, setup.Sdk.CrossChainReceiveCalls.Count);
        Assert.Single(setup.Quotes.Quotes);
        Assert.Null(setup.Invoices.Invoices["invoice-2"].Prompts[StablecoinPayments.Usdc.PaymentMethodId].Quote);
    }

    [Theory]
    [InlineData(true, 10_000_001)]
    // Landing as sats the retry steps by the sat's worth the colliding quote implies.
    [InlineData(false, 10_001_000)]
    public async Task The_store_is_not_held_while_the_provider_quotes_and_racing_twins_still_come_out_unique(
        bool landsAsToken, long retried)
    {
        var setup = Create();
        setup.Sdk.ReceiveLandsAsToken = landsAsToken;
        Invoice(setup, "invoice-1");
        Invoice(setup, "invoice-2");
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        setup.Sdk.HoldCrossChainReceiveUntil = hold;

        var one = setup.Service.QuoteAsync("invoice-1", StablecoinPayments.Usdc.PaymentMethodId, "base", Ct);
        var two = setup.Service.QuoteAsync("invoice-2", StablecoinPayments.Usdc.PaymentMethodId, "base", Ct);

        // Both are at the provider at once: neither waits on the other's round trip.
        await Eventually.True(() => setup.Sdk.CrossChainReceiveCalls.Count >= 2,
            "the second quote waited on the first one's round trip");
        Assert.Equal(2, setup.Sdk.CrossChainReceiveCalls.Count);

        setup.Sdk.HoldCrossChainReceiveUntil = null;
        hold.SetResult();
        var results = await Task.WhenAll(one, two);

        Assert.All(results, r => Assert.NotNull(r.Quote));
        // Both asked for the same target, so one of them took the fingerprint and the other asked again.
        Assert.Equal(3, setup.Sdk.CrossChainReceiveCalls.Count);
        Assert.Equal(new BigInteger(retried), setup.Sdk.CrossChainReceiveCalls[2].Amount);
        Assert.NotEqual(Fingerprint(setup, results[0].Quote!), Fingerprint(setup, results[1].Quote!));
    }

    private static StablecoinQuote OpenQuote(string id, DateTimeOffset createdAt, DateTimeOffset expiresAt) => new()
    {
        Id = id,
        StoreId = StoreId,
        InvoiceId = $"elsewhere-{id}",
        PaymentMethodId = StablecoinPayments.Usdc.PaymentMethodId.ToString(),
        Chain = "ethereum",
        Asset = "USDC",
        ContractAddress = "0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48",
        Decimals = 6,
        DepositAddress = $"0xother{id}",
        DepositBaseUnits = "5000000",
        AskedBaseUnits = "5000000",
        PaymentRequest = $"0xother{id}",
        DueAmount = 5m,
        FeeAmount = 0m,
        ExpectedReceivedBaseUnits = id,
        DestinationAsset = "BTC",
        ServiceFeeBaseUnits = "1",
        CreatedAt = createdAt,
        ExpiresAt = expiresAt
    };

    [Fact]
    public async Task Quotes_no_longer_on_offer_do_not_hold_the_stores_capacity()
    {
        // An anonymous caller used to fill a store's cap with quotes that nobody would pay and switch its USDC and
        // USDT off for the two days of the match window. Past the hour an address is offered for, a quote is no
        // longer shown and the SDK polls it only every ten minutes, so it no longer counts.
        var time = new StubTimeProvider(DateTimeOffset.UtcNow);
        var setup = Create(time: time);
        Invoice(setup);
        var now = time.GetUtcNow();
        for (var i = 0; i < StablecoinPayments.MaxOpenQuotesPerStore; i++)
            await setup.Quotes.AddAsync(OpenQuote($"{i}", now.AddHours(-3), now.AddHours(-2)), Ct);

        await QuoteOk(setup, "base");
    }

    [Fact]
    public async Task Quotes_still_on_offer_do_hold_the_stores_capacity()
    {
        var time = new StubTimeProvider(DateTimeOffset.UtcNow);
        var setup = Create(time: time);
        Invoice(setup);
        var now = time.GetUtcNow();
        for (var i = 0; i < StablecoinPayments.MaxOpenQuotesPerStore; i++)
            await setup.Quotes.AddAsync(OpenQuote($"{i}", now.AddMinutes(-5), now.AddMinutes(-3)), Ct);

        var result = await setup.Service.QuoteAsync("invoice-1", StablecoinPayments.Usdc.PaymentMethodId, "base", Ct);

        Assert.Contains("busy", result.Error);
        Assert.Empty(setup.Sdk.CrossChainReceiveCalls);
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
    public async Task A_quote_from_the_wallet_a_store_replaced_is_not_handed_out_again()
    {
        // After the recovery phrase is replaced, the old quote's address still pays the old wallet. Reused, checkout
        // kept offering it for the hour it is offered.
        var sdk = new FakeSparkSdkClient();
        var runtime = new FakeSparkStoreRuntime();
        runtime.Clients[StoreId] = sdk;
        var setup = new Setup(new StablecoinHarness(runtime), sdk);
        Invoice(setup);
        var old = await QuoteOk(setup, "base");

        var replacement = new FakeSparkSdkClient();
        runtime.Clients[StoreId] = replacement;
        var fresh = await QuoteOk(setup, "base");

        Assert.NotEqual(old.QuoteId, fresh.QuoteId);
        Assert.Single(replacement.CrossChainReceiveCalls);
        // …and the replacement's own quote is reused as usual.
        Assert.Equal(fresh.QuoteId, (await QuoteOk(setup, "base")).QuoteId);
        Assert.Single(replacement.CrossChainReceiveCalls);
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

    [Theory]
    [InlineData(true, null, 2_000UL, "The smallest USDC payment Ethereum takes is $20.00. Choose another network.")]
    [InlineData(true, "15000000", null, "The smallest USDC payment Ethereum takes is 15 USDC. Choose another network.")]
    [InlineData(true, null, null, "This payment is too small for USDC on Ethereum. Choose another network.")]
    // What Tron did on mainnet: refused a due above the floor it publishes. That floor is not the reason, so it
    // is not named.
    [InlineData(true, null, 80UL, "This payment is too small for USDC on Ethereum. Choose another network.")]
    [InlineData(false, null, 500UL,
        "The largest USDC payment Ethereum takes is $5.00. Choose another network, or pay another way.")]
    [InlineData(false, null, 100_000_000UL,
        "This payment is too large for USDC on Ethereum. Choose another network, or pay another way.")]
    public async Task An_amount_a_network_will_not_take_is_explained_to_the_payer_with_its_bound(
        bool tooSmall, string? boundAmount, ulong? boundUsdCents, string expected)
    {
        // The provider's own words are for the integrator ("Increase the input amount", seen on mainnet for USDT on
        // Tron), and a payer cannot change the price. They go to the log; the payer is told the bound and the fix.
        var setup = Create();
        Invoice(setup);
        setup.Sdk.FailCrossChainReceiveWith = new SdkException.CrossChainAmountOutOfRange(
            "Amount is below the minimum for this route. Increase the input amount.", tooSmall,
            boundAmount is null ? null : BigInteger.Parse(boundAmount), boundUsdCents);

        var result = await setup.Service.QuoteAsync("invoice-1", StablecoinPayments.Usdc.PaymentMethodId, "ethereum", Ct);

        Assert.Null(result.Quote);
        Assert.Equal(expected, result.Error);
        Assert.Empty(setup.Quotes.Quotes);
    }

    [Theory]
    [InlineData(true, "USDC on Ethereum is unavailable right now. Try again shortly, or choose another network.")]
    // What Tron answered on mainnet for a three-dollar invoice, a few minutes after it had quoted ten.
    [InlineData(false, "USDC on Ethereum can't take this payment. Choose another network.")]
    public async Task A_route_the_provider_will_not_serve_sends_the_payer_to_another_network(bool temporary, string expected)
    {
        var setup = Create();
        Invoice(setup);
        setup.Sdk.FailCrossChainReceiveWith = new SdkException.CrossChainRouteUnavailable("This route is not supported", temporary);

        var result = await setup.Service.QuoteAsync("invoice-1", StablecoinPayments.Usdc.PaymentMethodId, "ethereum", Ct);

        Assert.Null(result.Quote);
        Assert.Equal(expected, result.Error);
    }

    [Fact]
    public async Task Any_other_refusal_reaches_the_payer_in_the_plugins_words_and_the_log_in_the_providers()
    {
        // The checkout is anonymous; the provider's and the SDK's error text is for whoever runs the server.
        var setup = Create();
        Invoice(setup);
        setup.Sdk.FailCrossChainReceiveWith = new SdkException.NetworkException("@v1=provider timed out at 10.0.0.7");

        var result = await setup.Service.QuoteAsync("invoice-1", StablecoinPayments.Usdc.PaymentMethodId, "ethereum", Ct);

        Assert.Null(result.Quote);
        Assert.Equal("USDC on Ethereum cannot take this payment right now. Try again shortly, or choose another network.", result.Error);
        Assert.Contains(setup.Harness.Log.Lines, line => line.Contains("provider timed out"));
    }

    [Fact]
    public async Task The_stores_switch_answers_rather_than_throws_when_its_configuration_cannot_be_read()
    {
        // The Flint pages call these inside BTCPay's request, where an escaped exception restarts the server.
        var setup = Create();
        setup.Harness.StoreConfig.FailWith = new InvalidOperationException("database unavailable");

        Assert.False(await setup.Service.IsEnabledAsync(StoreId, Ct));
        Assert.Null(await setup.Service.TryReadEnabledAsync(StoreId, Ct));
        Assert.False(await setup.Service.SetEnabledAsync(StoreId, true, Ct));
        Assert.False(await setup.Service.SetEnabledAsync(StoreId, false, Ct));
    }

    [Fact]
    public async Task A_quote_past_the_providers_expiry_is_still_offered_and_reused_for_an_hour()
    {
        // The provider's expiry is the life of its price, two minutes on mainnet, not of its address: it reprices a
        // late deposit, and the SDK watches an unpaid quote for a day. Re-quoting every two minutes would move the
        // address under a payer who had already copied it.
        var time = new StubTimeProvider(DateTimeOffset.UtcNow);
        var setup = Create(time: time);
        Invoice(setup);
        var first = await QuoteOk(setup, "base");

        time.Advance(TimeSpan.FromMinutes(30));
        var again = await QuoteOk(setup, "base");
        Assert.Equal(first.QuoteId, again.QuoteId);
        Assert.Single(setup.Sdk.CrossChainReceiveCalls);

        time.Advance(StablecoinPayments.OfferedPastExpiry);
        var fresh = await QuoteOk(setup, "base");
        Assert.NotEqual(first.QuoteId, fresh.QuoteId);
        Assert.Equal(2, setup.Sdk.CrossChainReceiveCalls.Count);
    }

    [Fact]
    public async Task A_network_listed_before_networks_needed_an_icon_is_refused_without_asking_the_provider()
    {
        // The listed option matches a live route exactly, so only the icon rule stands between it and a quote.
        const string contract = "0x0b2C639c533813f4Aa9D7837CAf62653d097Ff85";
        var setup = Create();
        var invoice = Invoice(setup);
        invoice.Prompts[StablecoinPayments.Usdc.PaymentMethodId].Networks.Add(new StablecoinNetworkOption
        {
            Chain = "optimism", Name = "Optimism", ChainId = "10", ContractAddress = contract
        });
        setup.Sdk.CrossChainReceiveRoutes.Add(FakeSparkSdkClient.ReceiveRoute("optimism", "10", "USDC", contract, 6));

        var result = await setup.Service.QuoteAsync("invoice-1", StablecoinPayments.Usdc.PaymentMethodId, "optimism", Ct);

        Assert.Null(result.Quote);
        Assert.Equal("This invoice does not take USDC on that network.", result.Error);
        Assert.Empty(setup.Sdk.CrossChainReceiveCalls);
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
    public async Task An_eighteen_decimal_payment_the_provider_did_not_measure_settles_exactly_the_due()
    {
        // The SDK sizes a BSC deposit to the base unit and the payer is asked for it rounded up to six decimals. When
        // the provider's order has no amountIn, the SDK reports that quote-time deposit as "what was paid"; crediting
        // it recorded a millionth less than the ask, and a correctly paid invoice read as partly paid.
        var setup = Create();
        var invoice = Invoice(setup);
        setup.Sdk.ReceiveDepositDust = 123_456_789;
        var shown = await QuoteOk(setup, "bsc");
        var sdkQuote = SdkQuoteFor(setup, shown);
        Assert.Equal("10.080001", shown.Amount);

        var arrival = FakeSparkSdkClient.CrossChainReceivePayment(sdkQuote, "spark-pay-1", paid: sdkQuote.DepositAmount);
        Assert.Equal(StablecoinReceiveOutcome.Credited, await setup.Service.TryCreditAsync(StoreId, arrival, Ct));

        var payment = Assert.Single(invoice.Payments);
        Assert.Equal(10.080001m, payment.Value);
        Assert.Equal(10m, payment.Value - payment.Fee);
    }

    [Fact]
    public async Task An_unmeasured_payment_whose_delivery_fell_well_short_is_credited_by_what_was_delivered()
    {
        // Nothing says what the payer sent, and a USDB delivery is at par: a fifth short of the estimate is a payer
        // who sent a fifth less, and crediting the ask would record money that never came.
        var setup = Create();
        setup.Sdk.ReceiveLandsAsToken = true;
        var invoice = Invoice(setup);
        var shown = await QuoteOk(setup, "base");
        var sdkQuote = SdkQuoteFor(setup, shown);

        var arrival = FakeSparkSdkClient.CrossChainReceivePayment(
            sdkQuote, "spark-pay-1", paid: sdkQuote.DepositAmount, delivered: sdkQuote.ExpectedReceivedAmount * 4 / 5);
        await setup.Service.TryCreditAsync(StoreId, arrival, Ct);

        Assert.Equal(8.064m, Assert.Single(invoice.Payments).Value);
    }

    /// <summary>A quote shown and an arrival for it delivering 85% of its sat estimate, with no measured deposit.</summary>
    private static async Task<(FakeStablecoinInvoiceGateway.Invoice Invoice, StablecoinActiveQuote Shown, SparkPayment Arrival, decimal QuoteBtcUsd)>
        ShortSatsArrival(Setup setup)
    {
        var invoice = Invoice(setup);
        var shown = await QuoteOk(setup, "base");
        var sdkQuote = SdkQuoteFor(setup, shown);
        var arrival = FakeSparkSdkClient.CrossChainReceivePayment(
            sdkQuote, "spark-pay-1", paid: sdkQuote.DepositAmount, delivered: sdkQuote.ExpectedReceivedAmount * 85 / 100,
            at: setup.Harness.Time.GetUtcNow());
        return (invoice, shown, arrival, shown.Due / (decimal)sdkQuote.ExpectedReceivedAmount * 100_000_000m);
    }

    [Fact]
    public async Task An_unmeasured_sats_delivery_a_price_rise_explains_is_credited_in_full_at_the_live_price()
    {
        // A late deposit the provider repriced after bitcoin rose 18%: 15% fewer sat than the quote's estimate, worth
        // the whole due today. Judged by the sat estimate alone it reads as a payer who sent 15% less.
        var setup = Create();
        var (invoice, shown, arrival, quoteBtcUsd) = await ShortSatsArrival(setup);
        setup.Harness.Oracle.BtcUsd = quoteBtcUsd * 1.18m;

        Assert.Equal(StablecoinReceiveOutcome.Credited, await setup.Service.TryCreditAsync(StoreId, arrival, Ct));

        Assert.Equal(decimal.Parse(shown.Amount, System.Globalization.CultureInfo.InvariantCulture),
            Assert.Single(invoice.Payments).Value);
        Assert.Equal((StoreId, "USD"), Assert.Single(setup.Harness.Oracle.Calls));
    }

    [Fact]
    public async Task A_delivery_that_needs_the_live_price_waits_for_one_rather_than_being_recorded_short()
    {
        // A recorded payment is never revalued, so recording this without a price would leave a payer the price moved
        // against reading as part-paid for good — over a rate source that was down for one pass.
        var time = new StubTimeProvider(DateTimeOffset.UtcNow);
        var setup = Create(time: time);
        var (invoice, shown, arrival, quoteBtcUsd) = await ShortSatsArrival(setup);
        setup.Harness.Oracle.Unavailable = true;

        Assert.Equal(StablecoinReceiveOutcome.CreditFailed, await setup.Service.TryCreditAsync(StoreId, arrival, Ct));
        Assert.Empty(invoice.Payments);

        setup.Harness.Oracle.Unavailable = false;
        setup.Harness.Oracle.BtcUsd = quoteBtcUsd * 1.18m;
        Assert.Equal(1, await setup.Service.ReconcileAsync(Ct));

        Assert.Equal(decimal.Parse(shown.Amount, System.Globalization.CultureInfo.InvariantCulture),
            Assert.Single(invoice.Payments).Value);
    }

    [Fact]
    public async Task A_delivery_still_without_a_live_price_after_a_day_is_credited_by_the_quotes_own()
    {
        var time = new StubTimeProvider(DateTimeOffset.UtcNow);
        var setup = Create(time: time);
        var (invoice, shown, arrival, _) = await ShortSatsArrival(setup);
        setup.Harness.Oracle.Unavailable = true;
        await setup.Service.TryCreditAsync(StoreId, arrival, Ct);

        time.Advance(StablecoinPaymentService.LivePriceWait + TimeSpan.FromMinutes(1));
        Assert.Equal(1, await setup.Service.ReconcileAsync(Ct));

        // 85% of the sat estimate, so 85% of the ask, rounded down.
        var asked = StablecoinAmounts.ToBaseUnits(decimal.Parse(shown.Amount, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal(StablecoinAmounts.FromBaseUnits(asked * 85 / 100, 6, 6), Assert.Single(invoice.Payments).Value);
    }

    [Theory]
    // The quote: 10.080001 asked for a due of 10, estimated at 10,000 sat — $100,000 a bitcoin. A live price of 0 is
    // none to be had.
    // Reported, and not the quote-time deposit: the provider measured it, and it is credited as measured.
    [InlineData("9000000", "10000", "BTC", 0, 9_000_000)]
    // The quote-time deposit, or nothing: read as the quote paid, credited as the ask…
    [InlineData("10080000", "10000", "BTC", 0, 10_080_001)]
    [InlineData(null, "10000", "BTC", 0, 10_080_001)]
    [InlineData(null, null, "BTC", 0, 10_080_001)]
    // …which a sats delivery within the quote's slippage, or over its estimate, does not contradict…
    [InlineData("10080000", "9950", "BTC", 0, 10_080_001)]
    [InlineData("10080000", "12000", "BTC", 0, 10_080_001)]
    // …nor one a price rise explains: 9,100 sat at $110,000 is worth the due.
    [InlineData("10080000", "9100", "BTC", 110_000, 10_080_001)]
    // But 9,100 sat at an unchanged price is a payer who sent 9% less, credited by its worth — not the ask the flat
    // 10% band used to give it…
    [InlineData("10080000", "9100", "BTC", 100_000, 9_172_800)]
    // …and with no live price to tell, by the quote's own.
    [InlineData("10080000", "9100", "BTC", 0, 9_172_800)]
    [InlineData("10080000", "5000", "BTC", 0, 5_040_000)]
    // A token at par has no price to move: past the slippage budget, the delivery is the evidence, live price or not.
    [InlineData("10080000", "9800000", "USDB", 0, 9_878_400)]
    [InlineData("10080000", "9800000", "USDB", 110_000, 9_878_400)]
    [InlineData("10080000", "9950000", "USDB", 0, 10_080_001)]
    public void What_a_payer_is_credited_with(string? paid, string? delivered, string destination, long btcUsd, long credited)
    {
        var quote = new StablecoinQuote
        {
            DepositBaseUnits = "10080000",
            AskedBaseUnits = "10080001",
            DueAmount = 10m,
            ExpectedReceivedBaseUnits = destination == "BTC" ? "10000" : "10000000",
            DestinationAsset = destination,
            PaidBaseUnits = paid,
            DeliveredBaseUnits = delivered
        };

        decimal? usdPerSat = btcUsd > 0 ? btcUsd / 100_000_000m : null;
        Assert.Equal(new BigInteger(credited), StablecoinPaymentService.CreditedBaseUnits(quote, usdPerSat));
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

    [Theory]
    [InlineData(10_080_001)]
    // The review's case: the second payer's wallet shows no amount (a Tron or Solana QR code carries none), and
    // they type the first quote's ask. Attributed by the amount paid, that credited the first invoice.
    [InlineData(10_080_000)]
    [InlineData(9_000_000)]
    public async Task Equal_invoices_landing_as_usdb_are_each_credited_by_their_own_quote_however_the_payer_rounds(long paid)
    {
        var setup = Create();
        setup.Sdk.ReceiveLandsAsToken = true;
        var first = Invoice(setup, "invoice-1");
        var second = Invoice(setup, "invoice-2");
        await QuoteOk(setup, "base", "invoice-1");
        var secondQuote = await QuoteOk(setup, "base", "invoice-2");

        var arrival = FakeSparkSdkClient.CrossChainReceivePayment(SdkQuoteFor(setup, secondQuote), "spark-pay-2", paid: paid);
        Assert.Equal(StablecoinReceiveOutcome.Credited, await setup.Service.TryCreditAsync(StoreId, arrival, Ct));

        Assert.Empty(first.Payments);
        Assert.Single(second.Payments);
        Assert.Equal("USDB", second.Payments[0].Details.DestinationAsset);
    }

    [Fact]
    public async Task Twin_quotes_recorded_before_fingerprints_were_unique_are_left_for_a_human_and_reported_once()
    {
        // Rows from before the fix can still share a fingerprint for the two days they stay open. Nothing an arrival
        // carries tells them apart — not the amount paid, even when it is one of their asks exactly.
        var setup = Create();
        setup.Sdk.ReceiveLandsAsToken = true;
        var first = Invoice(setup, "invoice-1");
        var second = Invoice(setup, "invoice-2");
        var quote = await QuoteOk(setup, "base", "invoice-1");
        var twin = await QuoteOk(setup, "base", "invoice-2");
        var original = setup.Quotes.Quotes.Single(q => q.Id == quote.QuoteId);
        var legacy = setup.Quotes.Quotes.Single(q => q.Id == twin.QuoteId);
        legacy.ExpectedReceivedBaseUnits = original.ExpectedReceivedBaseUnits;
        legacy.ServiceFeeBaseUnits = original.ServiceFeeBaseUnits;

        var arrival = FakeSparkSdkClient.CrossChainReceivePayment(SdkQuoteFor(setup, quote), "spark-pay-1", paid: legacy.Asked);
        Assert.Equal(StablecoinReceiveOutcome.Unattributed, await setup.Service.TryCreditAsync(StoreId, arrival, Ct));
        Assert.Equal(StablecoinReceiveOutcome.Unattributed, await setup.Service.TryCreditAsync(StoreId, arrival, Ct));

        Assert.Empty(first.Payments);
        Assert.Empty(second.Payments);
        Assert.Single(setup.Harness.Log.Lines, line => line.Contains("spark-pay-1") && line.StartsWith("Warning"));
    }

    [Fact]
    public async Task Money_on_no_invoice_is_there_for_the_store_to_see_not_only_in_the_server_log()
    {
        var time = new StubTimeProvider(DateTimeOffset.UtcNow);
        var setup = Create(time: time);
        Invoice(setup);
        var shown = await QuoteOk(setup, "base");
        var foreign = FakeSparkSdkClient.CrossChainReceivePayment(
            SdkQuoteFor(setup, shown) with { ExpectedReceivedAmount = 12_345 }, "spark-pay-1", paid: 10_080_000);
        await setup.Service.TryCreditAsync(StoreId, foreign, Ct);
        await setup.Service.TryCreditAsync(StoreId, foreign, Ct);
        var stuck = await SettledElsewhere(setup, "stuck", time.GetUtcNow());
        await SettledElsewhere(setup, "just-now", time.GetUtcNow().AddMinutes(10));
        time.Advance(StablecoinPaymentService.UncreditedNoticeAfter + TimeSpan.FromMinutes(1));

        var attention = await setup.Service.GetAttentionAsync(StoreId, Ct);

        Assert.True(attention.Any);
        var arrival = Assert.Single(attention.Unattributed);
        Assert.Equal("spark-pay-1", arrival.SdkPaymentId);
        Assert.Equal("10.08", arrival.Amount);
        Assert.Equal("Base", arrival.ChainName);
        Assert.Equal("0xpayerspark-pay-1", arrival.ExternalTxHash);
        // The one that settled a quarter of an hour ago and still has no credit; the retry is still on the other.
        var uncredited = Assert.Single(attention.Uncredited);
        Assert.Equal(stuck.InvoiceId, uncredited.InvoiceId);
        Assert.Equal(1, attention.UncreditedCount);
        Assert.False((await Create().Service.GetAttentionAsync(StoreId, Ct)).Any);
    }

    [Fact]
    public async Task What_the_store_should_know_is_answered_even_when_the_database_is_not()
    {
        // Read by the status page's partial, inside the page's own request.
        var setup = Create();
        setup.Quotes.FailUncreditedReadsWith = new InvalidOperationException("database unavailable");

        Assert.False((await setup.Service.GetAttentionAsync(StoreId, Ct)).Any);
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

    private static async Task<StablecoinQuote> SettledElsewhere(Setup setup, string id, DateTimeOffset settledAt)
    {
        var quote = OpenQuote(id, settledAt.AddHours(-1), settledAt.AddMinutes(-58));
        quote.InvoiceId = $"gone-{id}";
        await setup.Quotes.AddAsync(quote, Ct);
        Assert.True(await setup.Quotes.TrySettleAsync(
            id, new StablecoinSettlement($"spark-{id}", 5_000_000, null, null, null, null, settledAt), Ct));
        return quote;
    }

    [Fact]
    public async Task Credits_past_the_retry_horizon_do_not_crowd_out_newer_ones()
    {
        // The retry read the oldest page of uncredited quotes and dropped the ones past a week afterwards, so a
        // page's worth of stale ones — each a credit that can never land — kept every newer credit from being tried.
        var time = new StubTimeProvider(DateTimeOffset.UtcNow);
        var setup = Create(time: time);
        var invoice = Invoice(setup);
        var now = time.GetUtcNow();
        for (var i = 0; i < 150; i++)
            await SettledElsewhere(setup, $"stale-{i}", now.AddDays(-8));

        var shown = await QuoteOk(setup, "base");
        var arrival = FakeSparkSdkClient.CrossChainReceivePayment(SdkQuoteFor(setup, shown), "spark-pay-1", paid: 10_080_000);
        setup.Invoices.FailPaymentsWith = new InvalidOperationException("database unavailable");
        Assert.Equal(StablecoinReceiveOutcome.CreditFailed, await setup.Service.TryCreditAsync(StoreId, arrival, Ct));
        setup.Invoices.FailPaymentsWith = null;

        Assert.Equal(1, await setup.Service.ReconcileAsync(Ct));
        Assert.Single(invoice.Payments);
    }

    [Fact]
    public async Task A_credit_that_keeps_failing_is_warned_about_once_not_every_pass()
    {
        var time = new StubTimeProvider(DateTimeOffset.UtcNow);
        var setup = Create(time: time);
        await SettledElsewhere(setup, "orphan", time.GetUtcNow());

        for (var pass = 0; pass < 5; pass++)
        {
            await setup.Service.ReconcileAsync(Ct);
            time.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Single(setup.Harness.Log.Lines, line => line.StartsWith("Warning") && line.Contains("spark-orphan"));

        time.Advance(StablecoinPaymentService.CreditFailureWarningInterval);
        await setup.Service.ReconcileAsync(Ct);
        Assert.Equal(2, setup.Harness.Log.Lines.Count(line => line.StartsWith("Warning") && line.Contains("spark-orphan")));
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
    public async Task A_busy_stores_lightning_receives_do_not_hide_a_dropped_arrival_from_the_pass()
    {
        // The window reaches back to the oldest open quote, and a store's Lightning receives in two days can run to
        // thousands. Read unfiltered, they pushed the one cross-chain receive past the pass's page limit.
        var setup = Create();
        var invoice = Invoice(setup);
        var shown = await QuoteOk(setup, "base");
        var start = DateTimeOffset.UtcNow;
        for (var i = 0; i < 1_000; i++)
        {
            setup.Sdk.Seed(new SparkPayment(
                $"lightning-pay-{i}", SparkPaymentDirection.Receive, SparkPaymentStatus.Completed,
                SparkPaymentMethod.Lightning, 1_000, 0, start.AddSeconds(i), PaymentFixture.PaymentHash, "lnbc1",
                null, null));
        }
        setup.Sdk.Seed(FakeSparkSdkClient.CrossChainReceivePayment(
            SdkQuoteFor(setup, shown), "spark-pay-1", paid: 10_080_000, at: start.AddSeconds(1_000)));

        Assert.Equal(1, await setup.Service.ReconcileAsync(Ct));

        Assert.Single(invoice.Payments);
        Assert.All(setup.Sdk.ListQueries, q => Assert.Contains(q.Method, new SparkPaymentMethod?[]
        {
            SparkPaymentMethod.Spark, SparkPaymentMethod.Token
        }));
    }

    [Fact]
    public async Task A_window_longer_than_one_pass_is_swept_across_passes_rather_than_restarted()
    {
        // More cross-chain receives in the window than one pass reads, none of them this quote's but one in the
        // middle — past the newest page and past the first pass's sweep. The second pass carries on from where the
        // first stopped instead of re-reading the same oldest pages.
        var setup = Create();
        var invoice = Invoice(setup);
        var shown = await QuoteOk(setup, "base");
        var foreign = SdkQuoteFor(setup, shown) with { ExpectedReceivedAmount = 1 };
        var start = DateTimeOffset.UtcNow;
        var perPass = StablecoinPaymentService.MaxScanPages * StablecoinPaymentService.ScanPageSize;
        var at = 0;
        for (; at < perPass + 20; at++)
            setup.Sdk.Seed(FakeSparkSdkClient.CrossChainReceivePayment(foreign, $"foreign-{at}", at: start.AddSeconds(at)));
        setup.Sdk.Seed(FakeSparkSdkClient.CrossChainReceivePayment(
            SdkQuoteFor(setup, shown), "spark-pay-1", paid: 10_080_000, at: start.AddSeconds(at++)));
        for (var i = 0; i < StablecoinPaymentService.ScanPageSize * 2; i++, at++)
            setup.Sdk.Seed(FakeSparkSdkClient.CrossChainReceivePayment(foreign, $"foreign-{at}", at: start.AddSeconds(at)));

        Assert.Equal(0, await setup.Service.ReconcileAsync(Ct));
        Assert.Equal(1, await setup.Service.ReconcileAsync(Ct));

        Assert.Single(invoice.Payments);
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
