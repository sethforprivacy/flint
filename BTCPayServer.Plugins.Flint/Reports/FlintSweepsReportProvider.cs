using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Services.Reporting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Reports;

/// <summary>
/// BTCPay's <b>Reporting</b> view of a store's sweeps: what left the Spark wallet, what it cost, and what arrived.
/// </summary>
/// <remarks>
/// <para>
/// The sweep page's history table shows the same rows a page at a time; this is the version an accountant can
/// filter, chart and export as CSV, and on BTCPay 2.4.5 and later it is also readable over Greenfield
/// (<c>POST /api/v1/stores/{storeId}/reports</c> with <c>view:Flint Sweeps</c>).
/// </para>
/// <para>
/// <b>Never throws.</b> BTCPay runs <see cref="Query"/> inside a request, and an exception attributed to a plugin
/// there disables the plugin and restarts the server. A failure is logged and the report comes back empty, with
/// its columns, rather than taking the merchant's checkout down with it.
/// </para>
/// </remarks>
public sealed class FlintSweepsReportProvider : ReportProvider
{
    /// <summary>The report's name in BTCPay's report picker and in Greenfield's <c>view:</c> filter.</summary>
    /// <remarks>
    /// Prefixed so it can never collide with a core report: BTCPay keys reports by name with <c>TryAdd</c>, so on a
    /// collision whichever registered first wins and the other silently disappears from the picker.
    /// </remarks>
    public const string ReportName = "Flint Sweeps";

    private const string Bitcoin = "BTC";
    private const int BitcoinDecimals = 8;

    private readonly ISweepRecordStore _records;
    private readonly ILogger<FlintSweepsReportProvider> _logger;

    public FlintSweepsReportProvider(ISweepRecordStore records, ILogger<FlintSweepsReportProvider> logger)
    {
        _records = records;
        _logger = logger;
    }

    public override string Name => ReportName;

    public override async Task Query(QueryContext queryContext, CancellationToken cancellation)
    {
        queryContext.ViewDefinition = CreateViewDefinition();
        try
        {
            var records = await _records
                .ListForReportAsync(queryContext.StoreId, queryContext.From, queryContext.To, cancellation)
                .ConfigureAwait(false);
            foreach (var record in records)
                queryContext.Data.Add(ToRow(record));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Nobody is waiting for the answer. Swallowed rather than rethrown: see the remarks on this class.
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
            new StoreReportResponse.Field("Status", "string"),
            new StoreReportResponse.Field("Trigger", "string"),
            new StoreReportResponse.Field("DestinationType", "string"),
            new StoreReportResponse.Field("Destination", "string"),
            new StoreReportResponse.Field("SentCurrency", "string"),
            new StoreReportResponse.Field("Sent", "amount"),
            new StoreReportResponse.Field("Fee", "amount"),
            new StoreReportResponse.Field("Received", "amount"),
            // BTCPay links a tx_id column to the block explorer of the currency in the column just before it, so
            // these two stay adjacent: a cooperative exit's txid links, a cross-chain one (no BTC explorer) does not.
            new StoreReportResponse.Field("ReceivedCurrency", "string"),
            new StoreReportResponse.Field("TransactionId", "tx_id"),
            new StoreReportResponse.Field("Conversion", "string"),
            new StoreReportResponse.Field("Error", "string")
        },
        Charts =
        {
            new ChartDefinition
            {
                Name = "Received, by currency and status",
                Groups = { "ReceivedCurrency", "Status" },
                Totals = { "ReceivedCurrency" },
                HasGrandTotal = false,
                Aggregates = { "Received" }
            },
            new ChartDefinition
            {
                Name = "Sent and fees, by currency",
                Groups = { "SentCurrency", "Status" },
                Totals = { "SentCurrency" },
                HasGrandTotal = false,
                Aggregates = { "Sent", "Fee" }
            }
        }
    };

    /// <summary>One sweep as a report row, in <see cref="CreateViewDefinition"/>'s column order.</summary>
    /// <remarks>
    /// Mirrors the sweep page's history table: the same status words, and amounts only for a sweep that moved
    /// money or may have. Every amount carries its own decimals, because one report holds satoshi, Stable Balance
    /// tokens and other chains' stablecoins side by side.
    /// </remarks>
    internal static IList<object?> ToRow(SweepRecord record)
    {
        var moved = record.Status is SweepRecordStatus.Sent or SweepRecordStatus.Confirmed
            or SweepRecordStatus.Pending;
        var tokenFunded = record.SourceTokenIdentifier is not null && record.SourceAmountBaseUnits is not null;
        var feeSats = record.FeeSats ?? record.QuotedFeeSats;

        string sentCurrency;
        object? sent = null;
        object? fee = null;
        if (tokenFunded)
        {
            sentCurrency = TokenLabel(record.SourceTokenIdentifier!);
            if (moved)
            {
                // There is no verified sats-side fee on a token-funded send (SparkSweepEngine records zero), so the
                // column is left empty rather than reporting a free sweep.
                sent = Amount(SweepRecord.ParseBaseUnits(record.SourceAmountBaseUnits), record.SourceTokenDecimals);
            }
        }
        else
        {
            sentCurrency = Bitcoin;
            if (moved)
            {
                // What left the wallet: the fee is inside AmountSats when it was netted out, on top of it otherwise
                // (and on a sats-funded cross-chain sweep the "fee" is the provider's overpay, never included).
                var debitedSats = record.FeesIncluded ? record.AmountSats : record.AmountSats + feeSats;
                sent = Amount(debitedSats, BitcoinDecimals);
                fee = Amount(feeSats, BitcoinDecimals);
            }
        }

        string receivedCurrency;
        object? received = null;
        if (record.IsCrossChain)
        {
            receivedCurrency = record.DestinationAsset ?? "";
            var units = record.DeliveredAmountBaseUnits ?? record.EstimatedOutBaseUnits;
            if (moved && units is not null)
                received = Amount(SweepRecord.ParseBaseUnits(units), record.DestinationAssetDecimals);
        }
        else
        {
            receivedCurrency = Bitcoin;
            if (moved)
                received = Amount(record.RecipientAmountSats, BitcoinDecimals);
        }

        return new List<object?>
        {
            record.CreatedAt,
            StatusText(record.Status),
            record.Trigger == SweepTrigger.Manual ? "By hand" : "Automatic",
            DestinationType(record),
            string.IsNullOrEmpty(record.DestinationAddress) ? null : record.DestinationAddress,
            sentCurrency,
            sent,
            fee,
            received,
            receivedCurrency,
            record.TxId,
            ConversionText(record),
            record.Error
        };
    }

    /// <summary>The words the sweep page's history table uses.</summary>
    internal static string StatusText(SweepRecordStatus status) => status switch
    {
        SweepRecordStatus.Confirmed => "Confirmed",
        SweepRecordStatus.Sent => "Sent",
        SweepRecordStatus.Pending => "Unresolved",
        SweepRecordStatus.Refused => "Refused",
        _ => "Failed"
    };

    private static string DestinationType(SweepRecord record) => record.DestinationMode switch
    {
        SweepDestinationMode.StaticAddress => "Fixed address",
        SweepDestinationMode.EvmAddress =>
            $"{record.DestinationAsset} on {record.DestinationChain}"
            + (record.Provider is { } provider ? $" via {provider}" : ""),
        _ => "Store wallet"
    };

    /// <summary>
    /// A cross-chain sweep's delivery state, which also says whether <c>Received</c> is still the quote's estimate.
    /// </summary>
    private static string? ConversionText(SweepRecord record)
    {
        if (!record.IsCrossChain)
            return null;
        if (record.DeliveredAmountBaseUnits is not null)
            return "Delivered";
        return record.ConversionStatus is { } status ? $"{status} (amount estimated)" : "Amount estimated";
    }

    /// <summary>The Stable Balance token's label when it is the plugin's default token, else its identifier.</summary>
    private static string TokenLabel(string identifier) =>
        identifier == StableBalanceSettings.DefaultTokenIdentifier ? StableBalanceSettings.DefaultLabel : identifier;

    private static object? Amount(long baseUnits, int decimals) => Amount(new BigInteger(baseUnits), decimals);

    /// <summary>
    /// A base-units integer as BTCPay's report amount: the value and how many decimals to show it with.
    /// </summary>
    /// <remarks>Null for a figure <see cref="decimal"/> cannot hold, rather than an exception out of the report.</remarks>
    internal static object? Amount(BigInteger baseUnits, int decimals)
    {
        if (decimals is < 0 or > 28)
            return null;
        try
        {
            var value = (decimal)baseUnits;
            for (var i = 0; i < decimals; i++)
                value /= 10m;
            return new FormattedAmount(value, decimals).ToJObject();
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
