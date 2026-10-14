using System;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Flint.Payments;

/// <summary>
/// The payment link BTCPay reports for a USDC/USDT prompt — the Greenfield invoice payment-methods endpoint's
/// <c>paymentLink</c> — which is the quote on show, if there is one and it is still current.
/// </summary>
/// <remarks>
/// Null until a payer has picked a network: before that there is no address to link to, and a link that quoted on
/// the caller's behalf would mint provider rows for every API read.
/// </remarks>
public sealed class StablecoinPaymentLinkExtension : IPaymentLinkExtension
{
    private readonly StablecoinPaymentMethodHandler _handler;

    public StablecoinPaymentLinkExtension(StablecoinPaymentMethodHandler handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public PaymentMethodId PaymentMethodId => _handler.PaymentMethodId;

    public string? GetPaymentLink(PaymentPrompt prompt, IUrlHelper? urlHelper)
    {
        if (prompt.Details is not { Type: not JTokenType.Null } raw)
            return null;
        try
        {
            // The checkout's own rule: a quote withdrawn — the invoice part-paid since, or the store's wallet
            // replaced — is not a link to pay.
            return (_handler.ParsePaymentPromptDetails(raw) as StablecoinPromptDetails)?.Quote is { } quote
                   && StablecoinCheckoutModelExtension.IsCurrent(
                       prompt.ParentEntity, prompt, quote, _handler.RunningWalletTag(prompt.ParentEntity?.StoreId))
                ? quote.PaymentRequest
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
