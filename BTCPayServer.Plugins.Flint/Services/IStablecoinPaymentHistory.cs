using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Payments;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>One USDC/USDT payment BTCPay recorded on an invoice, with what Flint stored about it.</summary>
/// <param name="ReceivedAt">When BTCPay recorded the payment.</param>
/// <param name="Settled">Whether BTCPay counts the payment as settled.</param>
/// <param name="Paid">What the payer sent, in <paramref name="Asset"/>.</param>
/// <param name="InvoiceAmount">
/// <paramref name="Paid"/> in the invoice's currency at the invoice's rate; null when the invoice has no rate for the
/// coin.
/// </param>
/// <param name="InvoiceDivisibility">How many decimals <paramref name="InvoiceCurrency"/> is shown with.</param>
public sealed record StablecoinPaymentHistoryEntry(
    DateTimeOffset ReceivedAt,
    string InvoiceId,
    string? OrderId,
    bool Settled,
    StablecoinAsset Asset,
    decimal Paid,
    StablecoinPaymentDetails Details,
    string InvoiceCurrency,
    decimal? InvoiceAmount,
    int InvoiceDivisibility);

/// <summary>
/// Reads a store's USDC/USDT payments back out of BTCPay's invoices, which are their durable record.
/// </summary>
/// <remarks>
/// The plugin's own quote table is not: finished quotes are deleted after
/// <see cref="StablecoinPaymentService.QuoteRetention"/>, so anything that reports on past payments reads the
/// payment rows BTCPay keeps for good.
/// </remarks>
public interface IStablecoinPaymentHistory
{
    /// <summary>
    /// The stablecoin payments on the store's invoices created in [<paramref name="from"/>, <paramref name="to"/>],
    /// oldest invoice first.
    /// </summary>
    /// <remarks>
    /// Ranged by invoice creation, as BTCPay's own Payments report is, so the two reports agree on which payments a
    /// date range holds.
    /// </remarks>
    Task<IReadOnlyList<StablecoinPaymentHistoryEntry>> ListAsync(
        string storeId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default);
}
