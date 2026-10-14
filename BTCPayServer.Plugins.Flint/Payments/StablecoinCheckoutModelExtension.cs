using System;
using System.Linq;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Services.Invoices;
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

        var quote = details.Quote;
        var current = IsCurrent(
            context.InvoiceEntity, context.Prompt, quote, handler.RunningWalletTag(context.InvoiceEntity?.StoreId));

        if (current && quote is not null)
        {
            // The link and the QR code are both the payment request (EIP-681 on an EVM chain), so a scanning wallet
            // gets the amount; only the clipboard is the bare address. See the component's note on its QR code.
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
            quote = current && quote is not null ? Controllers.UIStablecoinCheckoutController.ToResponse(quote) : null,
            // The quote the prompt last showed, once it is no longer offered. The payer's page may still hold it from
            // its own quote request, and must stop showing it and send the payer for a fresh one — see IsCurrent.
            withdrawn = !current && quote is not null ? new { id = quote.QuoteId, chain = quote.Chain } : null
        });
    }

    /// <summary>
    /// Whether the quote a prompt shows is still one to pay: its address is the prompt's destination, it pays the
    /// wallet the store runs now, and it was made for the invoice's due as it stands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A quote made for a different due is withdrawn.</b> The invoice was part-paid since — most often by this
    /// quote itself, a payer who sent too little — and the quote would ask for the whole original amount. Worse, a
    /// second deposit to a quote's address is never linked to anything: the SDK closes a receive's provider row
    /// once its first order completes, so a top-up sent there reaches the wallet with nothing to attribute it by.
    /// The payer must get a fresh quote for the remainder, and the checkout says so.
    /// </para>
    /// <para>
    /// <b>A quote from another wallet is withdrawn.</b> Its address pays the wallet that minted it, and after a store's
    /// recovery phrase is replaced that is the old one — perhaps replaced because someone else holds it. The quote
    /// path already refuses to reuse such a quote; without this check the checkout, its status poll and the Greenfield
    /// payment link went on handing it out for the hour it is offered. A quote with no tag predates the check and is
    /// withdrawn too, as is every quote while the store runs no wallet: neither can be shown to pay the store.
    /// </para>
    /// <para>
    /// Never throws: a due that cannot be computed (no rate) is a quote that cannot be confirmed current.
    /// </para>
    /// </remarks>
    internal static bool IsCurrent(
        InvoiceEntity? invoice, PaymentPrompt prompt, StablecoinActiveQuote? quote, string? runningWalletTag) =>
        quote is not null
        && quote.WalletTag is not null
        && string.Equals(quote.WalletTag, runningWalletTag, StringComparison.Ordinal)
        && prompt.Destination == quote.DepositAddress
        && invoice is not null
        && quote.Due == CurrentDue(invoice, prompt);

    /// <summary>The invoice's net due in the prompt's currency, as the quote path computes it.</summary>
    private static decimal? CurrentDue(InvoiceEntity invoice, PaymentPrompt prompt)
    {
        try
        {
            var netDue = invoice.NetDue;
            return netDue <= 0m
                ? 0m
                : BTCPayServer.Extensions.RoundUp(netDue / prompt.Rate, StablecoinPayments.Divisibility);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
