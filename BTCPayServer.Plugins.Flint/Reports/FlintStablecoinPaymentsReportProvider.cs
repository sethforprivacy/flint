using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Services.Reporting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Reports;

/// <summary>
/// BTCPay's <b>Reporting</b> view of a store's USDC and USDT payments: what the payer sent, on which network and in
/// which transaction, and what reached the Spark wallet.
/// </summary>
/// <remarks>
/// <para>
/// BTCPay's own Payments report already lists these payments, but only as the coin amount at a deposit address. The
/// conversion is the part a merchant reconciles — the payer's transaction on the other chain and the bitcoin (or
/// Stable Balance token) that actually arrived — and that lives only in the payment details this plugin writes. It is
/// read from BTCPay's invoices rather than the plugin's quote table, which forgets finished quotes after a month.
/// </para>
/// <para>
/// <b>Never throws</b>, for the reason <see cref="FlintSweepsReportProvider"/> gives.
/// </para>
/// </remarks>
public sealed class FlintStablecoinPaymentsReportProvider : ReportProvider
{
    /// <summary>The report's name in BTCPay's report picker and in Greenfield's <c>view:</c> filter.</summary>
    public const string ReportName = "Flint Stablecoin Payments";

    private const string Bitcoin = "BTC";
    private const int BitcoinDecimals = 8;

    /// <summary>Every Spark token the SDK lands a receive as is six-decimal (it asserts this), USDB included.</summary>
    private const int SparkTokenDecimals = 6;

    private readonly IStablecoinPaymentHistory _history;
    private readonly Func<bool> _available;
    private readonly ILogger<FlintStablecoinPaymentsReportProvider> _logger;

    /// <param name="available">
    /// Whether stablecoin payments exist on this server at all (mainnet only), so the report stays out of the picker
    /// on a server where it could only ever be empty.
    /// </param>
    public FlintStablecoinPaymentsReportProvider(
        IStablecoinPaymentHistory history,
        Func<bool> available,
        ILogger<FlintStablecoinPaymentsReportProvider> logger)
    {
        _history = history;
        _available = available;
        _logger = logger;
    }

    public override string Name => ReportName;

    public override bool IsAvailable()
    {
        try
        {
            return _available();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not tell whether the {Report} report applies; hiding it", ReportName);
            return false;
        }
    }

    public override async Task Query(QueryContext queryContext, CancellationToken cancellation)
    {
        queryContext.ViewDefinition = CreateViewDefinition();
        try
        {
            var payments = await _history
                .ListAsync(queryContext.StoreId, queryContext.From, queryContext.To, cancellation)
                .ConfigureAwait(false);
            foreach (var payment in payments)
                queryContext.Data.Add(ToRow(payment));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            queryContext.Data.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Store {StoreId}: the {Report} report could not be built", queryContext.StoreId,
                ReportName);
            queryContext.Data.Clear();
        }
    }

    internal static ViewDefinition CreateViewDefinition() => new()
    {
        Fields =
        {
            new StoreReportResponse.Field("Date", "datetime"),
            new StoreReportResponse.Field("InvoiceId", "invoice_id"),
            new StoreReportResponse.Field("OrderId", "string"),
            new StoreReportResponse.Field("Confirmed", "boolean"),
            new StoreReportResponse.Field("Network", "string"),
            // Not tx_id: BTCPay only knows block explorers for its own chains, and this is a hash on another one.
            new StoreReportResponse.Field("PayerTransaction", "string"),
            new StoreReportResponse.Field("PaidCurrency", "string"),
            new StoreReportResponse.Field("Paid", "amount"),
            new StoreReportResponse.Field("ReceivedCurrency", "string"),
            new StoreReportResponse.Field("Received", "amount"),
            new StoreReportResponse.Field("InvoiceCurrency", "string"),
            new StoreReportResponse.Field("InvoiceCurrencyAmount", "amount")
        },
        Charts =
        {
            new ChartDefinition
            {
                Name = "Paid, by coin and network",
                Groups = { "PaidCurrency", "Network" },
                Totals = { "PaidCurrency" },
                HasGrandTotal = false,
                Aggregates = { "Paid" }
            },
            new ChartDefinition
            {
                Name = "Received in the wallet, by currency",
                Groups = { "ReceivedCurrency", "PaidCurrency" },
                Totals = { "ReceivedCurrency" },
                HasGrandTotal = false,
                Aggregates = { "Received" }
            },
            new ChartDefinition
            {
                Name = "Invoice value, by invoice currency",
                Groups = { "InvoiceCurrency", "PaidCurrency" },
                Totals = { "InvoiceCurrency" },
                HasGrandTotal = false,
                Aggregates = { "InvoiceCurrencyAmount" }
            }
        }
    };

    /// <summary>One payment as a report row, in <see cref="CreateViewDefinition"/>'s column order.</summary>
    internal static IList<object?> ToRow(StablecoinPaymentHistoryEntry payment)
    {
        var details = payment.Details;
        var receivedCurrency = string.IsNullOrEmpty(details.DestinationAsset) ? null : details.DestinationAsset;
        object? received = null;
        if (receivedCurrency is not null
            && BigInteger.TryParse(details.DeliveredAmount, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var delivered))
        {
            // Sats for a bitcoin landing, as the invoice page's own table reads it.
            var decimals = string.Equals(receivedCurrency, Bitcoin, StringComparison.OrdinalIgnoreCase)
                ? BitcoinDecimals
                : SparkTokenDecimals;
            received = ReportAmounts.FromBaseUnits(delivered, decimals);
        }

        return new List<object?>
        {
            payment.ReceivedAt,
            payment.InvoiceId,
            payment.OrderId,
            payment.Settled,
            StablecoinPayments.ChainName(details.Chain),
            string.IsNullOrEmpty(details.ExternalTxHash) ? null : details.ExternalTxHash,
            payment.Asset.Symbol,
            ReportAmounts.FromDecimal(payment.Paid, StablecoinPayments.Divisibility),
            receivedCurrency,
            received,
            payment.InvoiceCurrency,
            payment.InvoiceAmount is { } amount
                ? ReportAmounts.FromDecimal(amount, payment.InvoiceDivisibility)
                : null
        };
    }
}
