using System;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Flint.Payments;

/// <summary>
/// The payment link BTCPay reports for a USDC/USDT prompt — the Greenfield invoice payment-methods endpoint's
/// <c>paymentLink</c> — which is the quote on show, if there is one.
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
            return (_handler.ParsePaymentPromptDetails(raw) as StablecoinPromptDetails)?.Quote is { } quote
                   && prompt.Destination == quote.DepositAddress
                ? quote.PaymentRequest
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
