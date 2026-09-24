using System;
using System.Globalization;
using System.Linq;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Flint.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Flint.Payments;

/// <summary>
/// Hands the checkout page what its USDC/USDT component renders: the networks, the quote on show, and where to
/// ask for another.
/// </summary>
/// <remarks>
/// <para>
/// Everything comes out of the prompt's own details, so the status poll that refreshes a checkout every few seconds
/// is a read of the invoice blob and never a provider call. The component is
/// <c>Views/Shared/Spark/StablecoinCheckout.cshtml</c>, registered at <c>checkout-end</c>.
/// </para>
/// <para>
/// <b>It never throws</b>, for the reason given on <see cref="StablecoinPaymentMethodHandler"/>: this runs inside
/// BTCPay's public checkout request, where an exception from plugin code disables the plugin and restarts the
/// server. Anyone holding an invoice link can make that request, so a failure here shows the payer an unavailable
/// payment method instead.
/// </para>
/// </remarks>
public sealed class StablecoinCheckoutModelExtension : ICheckoutModelExtension
{
    public const string CheckoutBodyComponentName = "FlintStablecoinCheckout";

    /// <summary>The key the component reads its data from on the checkout model.</summary>
    public const string ModelKey = "flintStablecoin";

    private readonly ILogger _logger;

    public StablecoinCheckoutModelExtension(StablecoinAsset asset, ILogger? logger = null)
    {
        Asset = asset ?? throw new ArgumentNullException(nameof(asset));
        _logger = logger ?? NullLogger.Instance;
    }

    public StablecoinAsset Asset { get; }

    public PaymentMethodId PaymentMethodId => Asset.PaymentMethodId;

    /// <summary>
    /// None here: the icon a payer needs is the network's, not the coin's, and the component draws it over its own
    /// QR code once a network is picked.
    /// </summary>
    public string Image => "";

    public string Badge => "";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context is not { Handler: StablecoinPaymentMethodHandler handler })
            return;

        context.Model.CheckoutBodyComponentName = CheckoutBodyComponentName;
        try
        {
            Populate(context, handler);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The {Asset} checkout for invoice {InvoiceId} could not be prepared; showing it as unavailable",
                Asset.Symbol, context.InvoiceEntity?.Id);
            context.Model.InvoiceBitcoinUrl = null;
            context.Model.InvoiceBitcoinUrlQR = null;
            context.Model.AdditionalData[ModelKey] = JObject.FromObject(new
            {
                asset = Asset.Symbol,
                assetName = Asset.Name,
                networks = Array.Empty<object>()
            });
        }
    }

    private void Populate(CheckoutModelContext context, StablecoinPaymentMethodHandler handler)
    {
        var details = context.Prompt.Details is { Type: not JTokenType.Null } raw
            ? handler.ParsePaymentPromptDetails(raw) as StablecoinPromptDetails
            : null;
        details ??= new StablecoinPromptDetails();

        // A quote made for a different due — the invoice was part-paid since — is not offered for payment: it would
        // ask for the wrong amount. The component shows the network picker instead, and picking re-quotes.
        var quote = details.Quote;
        var current = quote is not null
                      && context.Prompt.Destination == quote.DepositAddress
                      && quote.Due == CurrentDue(context);

        if (current && quote is not null)
        {
            context.Model.InvoiceBitcoinUrl = quote.PaymentRequest;
            context.Model.InvoiceBitcoinUrlQR = quote.PaymentRequest;
        }
        else
        {
            context.Model.InvoiceBitcoinUrl = null;
            context.Model.InvoiceBitcoinUrlQR = null;
        }

        context.Model.AdditionalData[ModelKey] = JObject.FromObject(new
        {
            asset = Asset.Symbol,
            assetName = Asset.Name,
            quoteUrl = context.UrlHelper.Action(
                nameof(Controllers.UIStablecoinCheckoutController.Quote),
                "UIStablecoinCheckout",
                new { invoiceId = context.InvoiceEntity.Id, paymentMethodId = PaymentMethodId.ToString() }),
            // Filtered here as well as at invoice creation, so an invoice made before a network lost its place
            // (or before networks needed an icon) offers only what the checkout can show.
            networks = details.Networks
                .Where(n => StablecoinPayments.NetworkIcon(n.Chain) is not null)
                .Select(n => new { chain = n.Chain, name = n.Name }),
            quote = current && quote is not null
                ? new
                {
                    id = quote.QuoteId,
                    chain = quote.Chain,
                    chainName = quote.ChainName,
                    address = quote.DepositAddress,
                    amount = quote.Amount,
                    paymentRequest = quote.PaymentRequest,
                    contract = quote.ContractAddress,
                    fee = quote.Fee.ToString(CultureInfo.InvariantCulture),
                    offeredUntil = StablecoinPayments.OfferedUntil(quote.ExpiresAt).ToUnixTimeSeconds()
                }
                : null
        });
    }

    /// <summary>The invoice's net due in the prompt's currency, as the quote path computes it.</summary>
    private static decimal? CurrentDue(CheckoutModelContext context)
    {
        try
        {
            var netDue = context.InvoiceEntity.NetDue;
            return netDue <= 0m
                ? 0m
                : BTCPayServer.Extensions.RoundUp(netDue / context.Prompt.Rate, StablecoinPayments.Divisibility);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
