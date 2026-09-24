using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Flint.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
/// <para>
/// <b>Nothing BTCPay calls here may throw.</b> BTCPay 2.4 answers an exception from a plugin's code during a
/// request by disabling the plugin and restarting the server (<c>PluginExceptionHandler</c>), and it calls the
/// parse methods below from inside its own requests — the Greenfield invoice endpoints, the invoice page, the
/// public checkout. One unreadable details blob was once enough to take a server down that way, so each parse
/// degrades instead: to the network list, or to an empty object, with a warning in the log.
/// </para>
/// </remarks>
public sealed class StablecoinPaymentMethodHandler : IPaymentMethodHandler
{
    private readonly Func<StablecoinPaymentService> _service;
    private readonly ILogger _logger;

    public StablecoinPaymentMethodHandler(
        StablecoinAsset asset,
        Func<StablecoinPaymentService> service,
        ILogger? logger = null)
    {
        Asset = asset ?? throw new ArgumentNullException(nameof(asset));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _logger = logger ?? NullLogger.Instance;
        // The invoice blob's own serializer, NBitcoin's converters included. BTCPay writes a prompt's details with
        // it, and that turns every date inside them into Unix seconds; a serializer without those converters writes
        // an ISO date and then cannot read back what BTCPay stored. That mismatch is the crash described above.
        Serializer = BlobSerializer.CreateSerializer(null as NBitcoin.Network).Serializer;
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

    /// <summary>
    /// The prompt's networks and quote. A blob that does not read keeps its network list and loses its quote, so
    /// the checkout offers a fresh one; the quote itself lives on in the plugin's own table, which is what settles it.
    /// </summary>
    public object ParsePaymentPromptDetails(JToken details)
    {
        try
        {
            return details.ToObject<StablecoinPromptDetails>(Serializer) ?? new StablecoinPromptDetails();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A {Asset} prompt's details could not be read; offering its networks without a quote",
                Asset.Symbol);
        }

        try
        {
            return new StablecoinPromptDetails
            {
                Networks = details is JObject o && o["networks"] is JArray networks
                    ? networks.ToObject<List<StablecoinNetworkOption>>(Serializer) ?? []
                    : []
            };
        }
        catch (Exception)
        {
            return new StablecoinPromptDetails();
        }
    }

    /// <summary>Empty on purpose (see <see cref="StablecoinPaymentMethodConfig"/>), so any object reads as one.</summary>
    public object ParsePaymentMethodConfig(JToken config)
    {
        try
        {
            return config.ToObject<StablecoinPaymentMethodConfig>(Serializer) ?? new StablecoinPaymentMethodConfig();
        }
        catch (Exception)
        {
            return new StablecoinPaymentMethodConfig();
        }
    }

    /// <summary>
    /// A credited payment's details. One that does not read comes back with its fields empty rather than throwing:
    /// the payment is already on the invoice, and what this adds is display.
    /// </summary>
    public object ParsePaymentDetails(JToken details)
    {
        try
        {
            if (details.ToObject<StablecoinPaymentDetails>(Serializer) is { } parsed)
                return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A {Asset} payment's details could not be read", Asset.Symbol);
        }

        return new StablecoinPaymentDetails
        {
            QuoteId = "",
            Chain = "",
            Asset = Asset.Symbol,
            DepositAddress = "",
            DestinationAsset = "",
            SdkPaymentId = ""
        };
    }
}
