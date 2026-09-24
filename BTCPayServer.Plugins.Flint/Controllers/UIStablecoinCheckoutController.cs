using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Flint.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NicolasDorier.RateLimits;

namespace BTCPayServer.Plugins.Flint.Controllers;

/// <summary>
/// The checkout's one call into the plugin: a payer picked a network, so quote it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Anonymous, like the checkout page it serves.</b> The invoice id is the capability, exactly as it is for
/// BTCPay's own checkout endpoints: whoever holds the link may pay it, and asking for a quote is part of paying.
/// Nothing here moves money or reveals anything a payer cannot already see.
/// </para>
/// <para>
/// <b>Bounded three ways</b>, because every quote is a provider row the SDK polls for a day: BTCPay's own public
/// rate limit per remote address, a cap on quotes per invoice, and a cap on open quotes per store. A live quote
/// for the same network and due is reused rather than re-minted, so a payer clicking back and forth costs nothing.
/// </para>
/// <para>
/// Antiforgery is not checked, as on core's checkout status endpoints: there is no session to forge a request
/// from, and the worst a forged request achieves is a quote nobody pays.
/// </para>
/// <para>
/// <b>Nothing escapes it.</b> BTCPay 2.4 answers an unhandled exception from a plugin's code during a request by
/// disabling the plugin and restarting the server, and this endpoint is anonymous: an exception here would be a
/// server restart anybody holding an invoice link could cause. Every failure — a database error, an SDK that
/// throws something unforeseen — answers <c>503</c> with a sentence for the payer, and is logged.
/// </para>
/// </remarks>
[AllowAnonymous]
[Route("plugins/flint/checkout")]
public class UIStablecoinCheckoutController : Controller
{
    private readonly StablecoinPaymentService _service;
    private readonly ILogger<UIStablecoinCheckoutController> _logger;

    public UIStablecoinCheckoutController(
        StablecoinPaymentService service,
        ILogger<UIStablecoinCheckoutController> logger)
    {
        _service = service;
        _logger = logger;
    }

    [HttpPost("{invoiceId}/{paymentMethodId}/quote")]
    [IgnoreAntiforgeryToken]
    [RateLimitsFilter(ZoneLimits.PublicInvoices, Scope = RateLimitsScope.RemoteAddress)]
    public async Task<IActionResult> Quote(
        string invoiceId,
        string paymentMethodId,
        [FromForm] string? chain,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(invoiceId)
            || !PaymentMethodId.TryParse(paymentMethodId, out var parsed)
            || StablecoinPayments.For(parsed) is null)
        {
            return NotFound();
        }

        StablecoinQuoteResult result;
        try
        {
            result = await _service.QuoteAsync(invoiceId, parsed, chain, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The payer went away and nobody reads this. Answered rather than rethrown, so not even an aborted
            // request reaches the exception handler.
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Quoting {PaymentMethodId} on {Chain} for invoice {InvoiceId} failed",
                parsed, chain, invoiceId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "This payment method is unavailable right now. Please try again shortly, or pay another way."
            });
        }

        if (result.NotFound)
            return NotFound();
        if (result.Quote is not { } quote)
            return BadRequest(new { error = result.Error });

        return Ok(new
        {
            quote = new
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
        });
    }
}
