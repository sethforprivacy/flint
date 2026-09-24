using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Flint.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
/// </remarks>
[AllowAnonymous]
[Route("plugins/flint/checkout")]
public class UIStablecoinCheckoutController : Controller
{
    private readonly StablecoinPaymentService _service;

    public UIStablecoinCheckoutController(StablecoinPaymentService service)
    {
        _service = service;
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

        var result = await _service.QuoteAsync(invoiceId, parsed, chain, cancellationToken);
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
