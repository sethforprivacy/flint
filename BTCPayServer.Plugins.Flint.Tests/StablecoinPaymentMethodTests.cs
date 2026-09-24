using BTCPayServer.Data;
using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Flint.Payments;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using BTCPayServer.Rating;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
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

    #region Checkout

    private static (CheckoutModel Model, JObject Data) Checkout(
        StablecoinPromptDetails details,
        string? destination,
        decimal netDue = 10m)
    {
        var handler = Handler();
        var invoice = new InvoiceEntity { Id = "invoice-1", Currency = "USD", StoreId = "store-1" };
        invoice.AddRate(new CurrencyPair("USDC", "USD"), 1m);
        var prompt = new PaymentPrompt
        {
            ParentEntity = invoice,
            PaymentMethodId = StablecoinPayments.Usdc.PaymentMethodId,
            Currency = "USDC",
            Divisibility = 6,
            Destination = destination,
            Details = JToken.FromObject(details, handler.Serializer)
        };
        invoice.NetDue = netDue;
        var model = new CheckoutModel();
        var url = new RouteEchoUrlHelper();

        new StablecoinCheckoutModelExtension(StablecoinPayments.Usdc).ModifyCheckoutModel(
            new CheckoutModelContext(model, new StoreData(), new StoreBlob(), invoice, url, prompt, handler));

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
