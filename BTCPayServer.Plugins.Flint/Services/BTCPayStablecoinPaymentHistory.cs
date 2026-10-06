using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Flint.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Rates;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// The real <see cref="IStablecoinPaymentHistory"/>, over BTCPay's <see cref="InvoiceRepository"/>.
/// </summary>
/// <remarks>
/// Uses only what BTCPay's own Payments report uses — <c>GetInvoices(InvoiceQuery)</c> and the payment entities it
/// returns — all unchanged from 2.4.1 to 2.4.5. The handler dictionary arrives through a <c>Func&lt;T&gt;</c> for
/// the reason every core payment type does in this plugin (see <c>SparkPlugin.Execute</c>).
/// </remarks>
public sealed class BTCPayStablecoinPaymentHistory : IStablecoinPaymentHistory
{
    private readonly InvoiceRepository _invoiceRepository;
    private readonly Func<PaymentMethodHandlerDictionary> _handlers;
    private readonly CurrencyNameTable _currencies;
    private readonly ILogger<BTCPayStablecoinPaymentHistory> _logger;

    public BTCPayStablecoinPaymentHistory(
        InvoiceRepository invoiceRepository,
        Func<PaymentMethodHandlerDictionary> handlers,
        CurrencyNameTable currencies,
        ILogger<BTCPayStablecoinPaymentHistory> logger)
    {
        _invoiceRepository = invoiceRepository;
        _handlers = handlers;
        _currencies = currencies;
        _logger = logger;
    }

    public async Task<IReadOnlyList<StablecoinPaymentHistoryEntry>> ListAsync(
        string storeId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        var invoices = await _invoiceRepository.GetInvoices(new InvoiceQuery
        {
            StoreId = [storeId],
            StartDate = from,
            EndDate = to,
            OrderByDesc = false
        }, cancellationToken).ConfigureAwait(false);

        var handlers = _handlers();
        var entries = new List<StablecoinPaymentHistoryEntry>();
        foreach (var invoice in invoices)
        {
            foreach (var payment in invoice.GetPayments(true))
            {
                if (StablecoinPayments.For(payment.PaymentMethodId) is null
                    || !handlers.TryGetValue(payment.PaymentMethodId, out var found)
                    || found is not StablecoinPaymentMethodHandler handler)
                {
                    continue;
                }

                StablecoinPaymentDetails? details;
                try
                {
                    details = handler.ParsePaymentDetails(payment.Details) as StablecoinPaymentDetails;
                }
                catch (Exception ex)
                {
                    // One unreadable row must not cost the merchant the rest of the report. It is still in BTCPay's
                    // own Payments report.
                    _logger.LogWarning(ex, "Invoice {InvoiceId}: a {PaymentMethodId} payment's details did not read",
                        invoice.Id, payment.PaymentMethodId);
                    continue;
                }

                if (details is null || string.IsNullOrEmpty(details.Chain))
                    continue;

                decimal? invoiceAmount = invoice.TryGetRate(payment.Currency, out var rate)
                    ? rate * payment.Value
                    : null;
                entries.Add(new StablecoinPaymentHistoryEntry(
                    payment.ReceivedTime,
                    invoice.Id,
                    invoice.Metadata.OrderId,
                    payment.Status is PaymentStatus.Settled,
                    handler.Asset,
                    payment.Value,
                    details,
                    invoice.Currency,
                    invoiceAmount,
                    _currencies.GetCurrencyData(invoice.Currency, true)?.Divisibility ?? 2));
            }
        }

        return entries;
    }
}
