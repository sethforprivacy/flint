using BTCPayServer.Data;
using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Plugins.Flint.Controllers;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Flint.Payments;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using BTCPayServer.Rating;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The BTCPay-facing surface of the USDC and USDT payment methods: the handler, what checkout is handed, the
/// payment link, and the rates they are priced at.
/// </summary>
public class StablecoinPaymentMethodTests
{
    private static StablecoinPaymentMethodHandler Handler(StablecoinAsset? asset = null, StablecoinHarness? harness = null) =>
        new(asset ?? StablecoinPayments.Usdc, () => (harness ?? new StablecoinHarness(new FakeSparkStoreRuntime())).Service);

    [Fact]
    public void The_payment_method_ids_are_the_coin_and_the_plugin()
    {
        // Pinned, because every store configuration, invoice prompt and payment row written by this plugin is keyed
        // by these strings: a rename strands them all.
        Assert.Equal("USDC-FLINT", StablecoinPayments.Usdc.PaymentMethodId.ToString());
        Assert.Equal("USDT-FLINT", StablecoinPayments.Usdt.PaymentMethodId.ToString());
        Assert.Same(StablecoinPayments.Usdt, StablecoinPayments.For("usdt-flint"));
        Assert.Null(StablecoinPayments.For("BTC-LN"));
    }

    [Fact]
    public async Task A_prompt_is_denominated_in_the_coin_at_six_decimals()
    {
        var handler = Handler(StablecoinPayments.Usdt);
        var context = new PaymentMethodContext(
            new StoreData(), new StoreBlob(), new JObject(), handler,
            new InvoiceEntity { Currency = "USD" }, new BTCPayServer.Logging.InvoiceLogs());
        var prompt = context.Prompt;
        prompt.PaymentMethodFee = 1m;

        await handler.BeforeFetchingRates(context);

        Assert.Equal("USDT", prompt.Currency);
        Assert.Equal(6, prompt.Divisibility);
        Assert.Equal(0m, prompt.PaymentMethodFee);
    }

    [Fact]
    public void Prompt_and_payment_details_round_trip_through_the_handlers_serializer()
    {
        var handler = Handler();
        var details = new StablecoinPromptDetails
        {
            Networks = [new StablecoinNetworkOption { Chain = "base", Name = "Base", ContractAddress = "0xabc" }],
            Quote = new StablecoinActiveQuote
            {
                QuoteId = "q1", Chain = "base", ChainName = "Base", DepositAddress = "0xdep", Amount = "10.08",
                PaymentRequest = "ethereum:0xabc@8453/transfer?address=0xdep&uint256=10080000",
                ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), Due = 10m, Fee = 0.08m
            }
        };

        var parsed = (StablecoinPromptDetails)handler.ParsePaymentPromptDetails(JToken.FromObject(details, handler.Serializer));

        Assert.Equal("base", Assert.Single(parsed.Networks).Chain);
        Assert.Equal("10.08", parsed.Quote!.Amount);
        Assert.Equal(0.08m, parsed.Quote.Fee);

        var payment = new StablecoinPaymentDetails
        {
            QuoteId = "q1", Chain = "tron", Asset = "USDT", DepositAddress = "Tdep", DestinationAsset = "BTC",
            SdkPaymentId = "spark-1", ExternalTxHash = "abc", DeliveredAmount = "9990"
        };
        var parsedPayment = (StablecoinPaymentDetails)handler.ParsePaymentDetails(JToken.FromObject(payment, handler.Serializer));
        Assert.Equal("spark-1", parsedPayment.SdkPaymentId);
        Assert.Equal("9990", parsedPayment.DeliveredAmount);
    }

    [Fact]
    public void A_quote_on_show_reads_back_from_the_invoice_blob_BTCPay_stores()
    {
        // The crash this pins, found on a live server: BTCPay stores a prompt with its invoice serializer, whose
        // NBitcoin converters write every date inside the prompt's details as Unix seconds. A handler serializer
        // without those converters wrote an ISO date, could not read back the integer BTCPay stored, and threw from
        // inside BTCPay's Greenfield invoice endpoint — which BTCPay 2.4 answers by disabling the plugin and
        // restarting the server. The handler's own round trip, above, never saw it.
        var handler = Handler();
        var expires = DateTimeOffset.FromUnixTimeSeconds(1_790_267_071);
        var details = Details();
        details.Quote!.ExpiresAt = expires;
        var invoice = new InvoiceEntity { Id = "invoice-1", Currency = "USD" };
        invoice.SetPaymentPrompt(StablecoinPayments.Usdc.PaymentMethodId, new PaymentPrompt
        {
            Currency = "USDC",
            Divisibility = 6,
            Destination = "0xdep",
            Details = JToken.FromObject(details, handler.Serializer)
        });

        var stored = invoice.GetPaymentPrompt(StablecoinPayments.Usdc.PaymentMethodId)!;
        Assert.Equal(JTokenType.Integer, stored.Details["quote"]!["expiresAt"]!.Type);

        var parsed = (StablecoinPromptDetails)handler.ParsePaymentPromptDetails(stored.Details);
        Assert.Equal(expires, parsed.Quote!.ExpiresAt);
        Assert.Equal(10m, parsed.Quote.Due);
        Assert.Equal(0.08m, parsed.Quote.Fee);
        Assert.Equal(2, parsed.Networks.Count);
    }

    [Fact]
    public void Details_that_do_not_read_keep_their_networks_and_lose_their_quote()
    {
        var handler = Handler();
        var details = JObject.FromObject(Details(), handler.Serializer);
        details["quote"]!["expiresAt"] = "not a date";

        var parsed = (StablecoinPromptDetails)handler.ParsePaymentPromptDetails(details);

        Assert.Null(parsed.Quote);
        Assert.Equal(["ethereum", "base"], parsed.Networks.Select(n => n.Chain).ToArray());
    }

    public static TheoryData<string> Unreadable => new()
    {
        "null", "\"a string\"", "42", "[1, 2]", "{\"networks\": \"not a list\"}", "{\"sdkPaymentId\": {}}"
    };

    [Theory]
    [MemberData(nameof(Unreadable))]
    public void No_parse_BTCPay_calls_ever_throws(string json)
    {
        // Every one of these runs inside a BTCPay request, where an exception from plugin code disables the plugin
        // and restarts the server.
        var handler = Handler();
        var token = JToken.Parse(json);

        var prompt = Assert.IsType<StablecoinPromptDetails>(handler.ParsePaymentPromptDetails(token));
        Assert.Null(prompt.Quote);
        Assert.IsType<StablecoinPaymentMethodConfig>(handler.ParsePaymentMethodConfig(token));
        // Its chain may be empty, which the invoice page's partial takes as "nothing to add" rather than naming it.
        Assert.IsType<StablecoinPaymentDetails>(handler.ParsePaymentDetails(token));
    }

    #region Checkout

    private static (CheckoutModel Model, JObject Data) Checkout(
        StablecoinPromptDetails details,
        string? destination,
        decimal netDue = 10m,
        IUrlHelper? url = null)
    {
        var handler = Handler();
        var invoice = new InvoiceEntity { Id = "invoice-1", Currency = "USD", StoreId = "store-1" };
        invoice.AddRate(new CurrencyPair("USDC", "USD"), 1m);
        // Through BTCPay's own invoice storage, so checkout reads the details back in the shape BTCPay stores them
        // in, which is not the shape the handler wrote them in (see
        // A_quote_on_show_reads_back_from_the_invoice_blob_BTCPay_stores).
        invoice.SetPaymentPrompt(StablecoinPayments.Usdc.PaymentMethodId, new PaymentPrompt
        {
            Currency = "USDC",
            Divisibility = 6,
            Destination = destination,
            Details = JToken.FromObject(details, handler.Serializer)
        });
        var prompt = invoice.GetPaymentPrompt(StablecoinPayments.Usdc.PaymentMethodId)!;
        invoice.NetDue = netDue;
        var model = new CheckoutModel();

        new StablecoinCheckoutModelExtension(StablecoinPayments.Usdc).ModifyCheckoutModel(
            new CheckoutModelContext(model, new StoreData(), new StoreBlob(), invoice, url ?? new RouteEchoUrlHelper(),
                prompt, handler));

        return (model, (JObject)model.AdditionalData[StablecoinCheckoutModelExtension.ModelKey]);
    }

    private static StablecoinPromptDetails Details(decimal quoteDue = 10m) => new()
    {
        Networks =
        [
            new StablecoinNetworkOption { Chain = "ethereum", Name = "Ethereum" },
            new StablecoinNetworkOption { Chain = "base", Name = "Base", ContractAddress = "0xabc" }
        ],
        Quote = new StablecoinActiveQuote
        {
            QuoteId = "q1", Chain = "base", ChainName = "Base", DepositAddress = "0xdep", Amount = "10.08",
            PaymentRequest = "ethereum:0xabc@8453/transfer?address=0xdep&uint256=10080000",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10), Due = quoteDue, Fee = 0.08m
        }
    };

    [Fact]
    public void Checkout_is_handed_the_networks_and_the_quote_on_show()
    {
        var (model, data) = Checkout(Details(), destination: "0xdep");

        Assert.Equal(StablecoinCheckoutModelExtension.CheckoutBodyComponentName, model.CheckoutBodyComponentName);
        Assert.Equal("USDC", (string?)data["asset"]);
        Assert.Equal(["ethereum", "base"], data["networks"]!.Select(n => (string)n["chain"]!).ToArray());
        Assert.Equal("10.08", (string?)data["quote"]!["amount"]);
        Assert.Equal("0xdep", (string?)data["quote"]!["address"]);
        Assert.Equal("ethereum:0xabc@8453/transfer?address=0xdep&uint256=10080000", model.InvoiceBitcoinUrl);
        Assert.Equal("UIStablecoinCheckout/Quote?invoiceId=invoice-1&paymentMethodId=USDC-FLINT", (string?)data["quoteUrl"]);
    }

    /// <summary>An <see cref="IUrlHelper"/> that spells out the action it was asked for, for asserting on.</summary>
    private sealed class RouteEchoUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext { get; } = new();

        public string? Action(UrlActionContext actionContext)
        {
            var values = new Microsoft.AspNetCore.Routing.RouteValueDictionary(actionContext.Values);
            return $"{actionContext.Controller}/{actionContext.Action}?"
                   + string.Join("&", values.Select(v => $"{v.Key}={v.Value}"));
        }

        public string? Content(string? contentPath) => contentPath;
        public bool IsLocalUrl(string? url) => true;
        public string? Link(string? routeName, object? values) => null;
        public string? RouteUrl(UrlRouteContext routeContext) => null;
    }

    [Fact]
    public void A_quote_made_for_a_different_due_is_not_offered_for_payment()
    {
        // The invoice was part-paid since: the quote would ask for the whole original amount.
        var (model, data) = Checkout(Details(quoteDue: 10m), destination: "0xdep", netDue: 4m);

        Assert.Equal(JTokenType.Null, data["quote"]!.Type);
        Assert.Null(model.InvoiceBitcoinUrl);
        Assert.Equal(2, data["networks"]!.Count());
    }

    [Fact]
    public void Before_a_network_is_picked_checkout_shows_the_picker_alone()
    {
        var details = Details();
        details.Quote = null;

        var (model, data) = Checkout(details, destination: null);

        Assert.Equal(JTokenType.Null, data["quote"]!.Type);
        Assert.Null(model.InvoiceBitcoinUrlQR);
    }

    [Fact]
    public void Checkout_offers_only_networks_it_can_show_and_keeps_an_address_for_an_hour_past_the_price()
    {
        var details = Details();
        details.Networks.Add(new StablecoinNetworkOption { Chain = "optimism", Name = "Optimism" });

        var (_, data) = Checkout(details, destination: "0xdep");

        Assert.Equal(["ethereum", "base"], data["networks"]!.Select(n => (string)n["chain"]!).ToArray());
        Assert.Null(data["networks"]![0]!["contract"]);
        Assert.Equal(
            (details.Quote!.ExpiresAt + StablecoinPayments.OfferedPastExpiry).ToUnixTimeSeconds(),
            (long)data["quote"]!["offeredUntil"]!);
    }

    [Fact]
    public void Every_icon_the_checkout_names_is_served_from_the_plugin_and_is_inert()
    {
        // BTCPay serves each plugin's embedded Resources/** from its web root through an EmbeddedFileProvider over
        // the plugin's assembly; this asks the same provider for the same paths the checkout writes.
        var files = new Microsoft.Extensions.FileProviders.EmbeddedFileProvider(typeof(SparkPlugin).Assembly);
        var paths = StablecoinPayments.NetworkIconPaths.Values
            .Concat(StablecoinPayments.Assets.Select(StablecoinPayments.TokenIcon))
            .ToList();

        Assert.Equal(8 + 2, paths.Count);
        foreach (var path in paths)
        {
            var file = files.GetFileInfo(path);
            Assert.True(file.Exists, $"{path} is not embedded");
            using var reader = new StreamReader(file.CreateReadStream());
            var svg = reader.ReadToEnd();
            Assert.StartsWith("<svg", svg);
            Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("href=\"http", svg, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal("Resources/img/networks/bsc.svg", StablecoinPayments.NetworkIcon("BSC"));
        Assert.Null(StablecoinPayments.NetworkIcon("optimism"));
    }

    [Fact]
    public void A_checkout_that_cannot_be_prepared_shows_the_method_as_unavailable_instead_of_throwing()
    {
        // The public checkout is BTCPay's request, and an exception from here would disable the plugin and restart
        // the server for anyone holding an invoice link.
        var (model, data) = Checkout(Details(), destination: "0xdep", url: new ThrowingUrlHelper());

        Assert.Equal(StablecoinCheckoutModelExtension.CheckoutBodyComponentName, model.CheckoutBodyComponentName);
        Assert.Empty(data["networks"]!);
        Assert.Null(data["quote"]);
        Assert.Null(model.InvoiceBitcoinUrl);
    }

    private sealed class ThrowingUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext { get; } = new();
        public string? Action(UrlActionContext actionContext) => throw new InvalidOperationException("no router");
        public string? Content(string? contentPath) => throw new InvalidOperationException("no router");
        public bool IsLocalUrl(string? url) => false;
        public string? Link(string? routeName, object? values) => throw new InvalidOperationException("no router");
        public string? RouteUrl(UrlRouteContext routeContext) => throw new InvalidOperationException("no router");
    }

    [Fact]
    public void The_payment_link_is_the_quote_on_show_and_nothing_before_one()
    {
        var handler = Handler();
        var link = new StablecoinPaymentLinkExtension(handler);
        var details = Details();

        var shown = new PaymentPrompt { Destination = "0xdep", Details = JToken.FromObject(details, handler.Serializer) };
        Assert.Equal(details.Quote!.PaymentRequest, link.GetPaymentLink(shown, null));

        details.Quote = null;
        var unpicked = new PaymentPrompt { Details = JToken.FromObject(details, handler.Serializer) };
        Assert.Null(link.GetPaymentLink(unpicked, null));
    }

    #endregion

    #region The quote endpoint

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_quote_that_fails_unexpectedly_answers_503_rather_than_throwing()
    {
        // Anonymous, and BTCPay turns an exception from plugin code into a disabled plugin and a restarted server.
        var harness = new StablecoinHarness(new FakeSparkStoreRuntime());
        harness.Invoices.FailLookupsWith = new InvalidOperationException("the database is down");
        var controller = new UIStablecoinCheckoutController(
            harness.Service, NullLogger<UIStablecoinCheckoutController>.Instance);

        var result = await controller.Quote("invoice-1", "USDC-FLINT", "base", Ct);

        var error = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, error.StatusCode);
    }

    [Fact]
    public async Task A_payer_who_went_away_is_answered_rather_than_thrown_at()
    {
        var harness = new StablecoinHarness(new FakeSparkStoreRuntime());
        harness.Invoices.FailLookupsWith = new OperationCanceledException();
        var controller = new UIStablecoinCheckoutController(
            harness.Service, NullLogger<UIStablecoinCheckoutController>.Instance);

        var result = await controller.Quote("invoice-1", "USDC-FLINT", "base", new CancellationToken(canceled: true));

        Assert.IsType<EmptyResult>(result);
    }

    #endregion

    #region Rates

    /// <summary>The default rules as the plugin registers them, under a store whose preferred exchange is Kraken.</summary>
    private static RateRules Rules(string symbol) => RateRules.Combine(
    [
        RateRules.Parse("X_X = kraken(X_X);"),
        RateRules.Parse($"{symbol}_USD = 1; {symbol}_X = {symbol}_BTC * BTC_X; {symbol}_BTC = 1 / BTC_USD;")
    ]);

    [Theory]
    [InlineData("USDC")]
    [InlineData("USDT")]
    public void A_dollar_invoice_asks_for_exactly_its_price(string symbol)
    {
        // The exact pair outranks the store's catch-all, so there is no bid/ask spread's worth of extra to pay.
        var rule = Rules(symbol).GetRuleFor(new CurrencyPair(symbol, "USD"));

        Assert.True(rule.Reevaluate());
        Assert.Equal(1m, rule.BidAsk!.Bid);
        Assert.Empty(rule.ExchangeRates);
    }

    [Fact]
    public void Any_other_currency_is_crossed_through_bitcoin_at_dollar_parity()
    {
        var rule = Rules("USDC").GetRuleFor(new CurrencyPair("USDC", "EUR"));
        rule.ExchangeRates.SetRate("kraken", new CurrencyPair("BTC", "USD"), new BidAsk(100_000m));
        rule.ExchangeRates.SetRate("kraken", new CurrencyPair("BTC", "EUR"), new BidAsk(90_000m));

        Assert.True(rule.Reevaluate());
        Assert.Equal(0.9m, rule.BidAsk!.Bid);
    }

    [Fact]
    public void A_bitcoin_denominated_invoice_is_priced_from_the_bitcoin_rate_alone()
    {
        var rule = Rules("USDT").GetRuleFor(new CurrencyPair("USDT", "BTC"));
        rule.ExchangeRates.SetRate("kraken", new CurrencyPair("BTC", "USD"), new BidAsk(100_000m));

        Assert.True(rule.Reevaluate());
        Assert.Equal(0.00001m, rule.BidAsk!.Bid);
    }

    #endregion
}
