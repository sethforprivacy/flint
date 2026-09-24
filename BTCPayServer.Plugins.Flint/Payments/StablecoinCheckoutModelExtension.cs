using System;
using System.Globalization;
using System.Linq;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Flint.Services;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Flint.Payments;

/// <summary>
/// Hands the checkout page what its USDC/USDT component renders: the networks, the quote on show, and where to
/// ask for another.
/// </summary>
/// <remarks>
/// Everything comes out of the prompt's own details, so the status poll that refreshes a checkout every few seconds
/// is a read of the invoice blob and never a provider call. The component is
/// <c>Views/Shared/Spark/StablecoinCheckout.cshtml</c>, registered at <c>checkout-end</c>.
/// </remarks>
public sealed class StablecoinCheckoutModelExtension : ICheckoutModelExtension
{
    public const string CheckoutBodyComponentName = "FlintStablecoinCheckout";

    /// <summary>The key the component reads its data from on the checkout model.</summary>
    public const string ModelKey = "flintStablecoin";

    public StablecoinCheckoutModelExtension(StablecoinAsset asset)
    {
        Asset = asset ?? throw new ArgumentNullException(nameof(asset));
    }

    public StablecoinAsset Asset { get; }

    public PaymentMethodId PaymentMethodId => Asset.PaymentMethodId;

    /// <summary>No image: the component names the coin and the network in words, and draws no icon over its QR code.</summary>
    public string Image => "";

    public string Badge => "";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context is not { Handler: StablecoinPaymentMethodHandler handler })
            return;

        context.Model.CheckoutBodyComponentName = CheckoutBodyComponentName;

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
            networks = details.Networks.Select(n => new { chain = n.Chain, name = n.Name, contract = n.ContractAddress }),
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
                    expiresAt = quote.ExpiresAt.ToUnixTimeSeconds()
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
