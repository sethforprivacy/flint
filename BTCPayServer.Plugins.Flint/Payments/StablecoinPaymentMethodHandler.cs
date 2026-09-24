using System;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Flint.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Flint.Payments;

/// <summary>
/// BTCPay's payment method for one stablecoin — USDC or USDT — paid from any network the provider serves and
/// received into the store's Spark wallet.
/// </summary>
/// <remarks>
/// <para>
/// <b>A prompt is created without an address.</b> Which network the payer holds the coin on is theirs to say, and
/// every network needs its own quote, so creating an invoice lists the networks and mints nothing — a quote per
/// network per invoice would be two dozen provider rows the SDK then polls for a day, almost all of them for
/// nothing. The checkout asks for a quote when the payer picks a network (<see cref="StablecoinPaymentService.QuoteAsync"/>).
/// </para>
/// <para>
/// <b>Denominated in the coin itself</b>, six decimals, rated through the default rules this plugin registers
/// (<c>USDC_USD = 1</c> and a cross through bitcoin for every other currency; see <c>SparkPlugin</c>). That keeps the
/// invoice's accounting in what the payer actually sent, and lets BTCPay's own payment-method criteria, tolerance
/// and partial-payment handling work on it unchanged.
/// </para>
/// <para>
/// The service arrives as a factory: this handler is built while BTCPay builds its handler dictionary, and the
/// service reaches the store runtime, which must not be constructed from inside that graph (see
/// <c>SparkPlugin</c>'s note on deferred resolution).
/// </para>
/// </remarks>
public sealed class StablecoinPaymentMethodHandler : IPaymentMethodHandler
{
    private readonly Func<StablecoinPaymentService> _service;

    public StablecoinPaymentMethodHandler(StablecoinAsset asset, Func<StablecoinPaymentService> service)
    {
        Asset = asset ?? throw new ArgumentNullException(nameof(asset));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        Serializer = BlobSerializer.CreateSerializer().Serializer;
    }

    public StablecoinAsset Asset { get; }

    public PaymentMethodId PaymentMethodId => Asset.PaymentMethodId;

    public JsonSerializer Serializer { get; }

    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = Asset.Symbol;
        context.Prompt.Divisibility = StablecoinPayments.Divisibility;
        context.Prompt.PaymentMethodFee = 0m;
        return Task.CompletedTask;
    }

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        if (context.InvoiceEntity.Type == InvoiceType.TopUp || context.InvoiceEntity.IsUnsetTopUp())
        {
            // A cross-chain quote needs an amount to size the deposit from; there is no "send what you like" quote.
            throw new PaymentMethodUnavailableException($"{Asset.Symbol} payments need an invoice amount.");
        }

        var due = context.Prompt.Calculate().Due;
        var (networks, unavailable) = await _service()
            .GetNetworksAsync(context.InvoiceEntity.StoreId, Asset, due)
            .ConfigureAwait(false);
        if (unavailable is not null)
            throw new PaymentMethodUnavailableException(unavailable);

        context.Prompt.Destination = null;
        context.Prompt.PaymentMethodFee = 0m;
        context.Prompt.Details = JObject.FromObject(new StablecoinPromptDetails { Networks = [.. networks] }, Serializer);
    }

    public object ParsePaymentPromptDetails(JToken details) =>
        details.ToObject<StablecoinPromptDetails>(Serializer) ?? new StablecoinPromptDetails();

    public object ParsePaymentMethodConfig(JToken config) =>
        config.ToObject<StablecoinPaymentMethodConfig>(Serializer) ?? new StablecoinPaymentMethodConfig();

    public object ParsePaymentDetails(JToken details) =>
        details.ToObject<StablecoinPaymentDetails>(Serializer)
        ?? throw new FormatException($"Invalid {nameof(StablecoinPaymentDetails)}");
}
