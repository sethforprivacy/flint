using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.HostedServices;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// Periodically credits USDC/USDT receives the event stream did not deliver, and retries credits that did not land.
/// </summary>
/// <remarks>
/// <para>
/// The same guarantee the Lightning reconciliation gives, for the same reasons and one more. The SDK's events drop
/// completions; and on a cross-chain receive the conversion details that attribute the payment to a quote arrive
/// only with <c>PaymentMetadataUpdated</c>, which can come after the completion — so a payment can be reported
/// before there is anything in it to credit by.
/// </para>
/// <para>
/// Separate from <see cref="SparkReconciliationTask"/> rather than folded into it, because it walks a different
/// thing — the store's open quotes, not its unpaid Lightning invoices — and costs nothing on a store that has
/// none: the pass starts from the quote table and never touches a wallet without an open quote.
/// </para>
/// </remarks>
public class StablecoinReconciliationTask : IPeriodicTask
{
    private readonly StablecoinPaymentService _service;
    private readonly ILogger<StablecoinReconciliationTask> _logger;

    public StablecoinReconciliationTask(StablecoinPaymentService service, ILogger<StablecoinReconciliationTask> logger)
    {
        _service = service;
        _logger = logger;
    }

    public async Task Do(CancellationToken cancellationToken)
    {
        var credited = await _service.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        if (credited > 0)
            _logger.LogInformation("USDC/USDT reconciliation recorded {Credited} payment(s) on their invoices", credited);
    }
}
