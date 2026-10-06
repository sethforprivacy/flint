using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Reports;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using BTCPayServer.Services.Reporting;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

public class FlintSweepsReportProviderTests
{
    private const string StoreId = "store-1";
    private static readonly DateTimeOffset Origin = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IReadOnlyList<string> Columns =
        FlintSweepsReportProvider.CreateViewDefinition().Fields.Select(f => f.Name).ToList();

    private static object? Cell(IList<object?> row, string column) => row[Columns.ToList().IndexOf(column)];

    private static (decimal Value, int Decimals) AmountOf(IList<object?> row, string column)
    {
        var amount = Assert.IsType<JObject>(Cell(row, column));
        return (decimal.Parse(amount["v"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture),
            amount["d"]!.Value<int>());
    }

    private static SweepRecord CooperativeExit(
        SweepRecordStatus status = SweepRecordStatus.Confirmed, bool feesIncluded = true) => new()
    {
        IdempotencyKey = Guid.NewGuid().ToString(),
        StoreId = StoreId,
        DestinationAddress = "bc1qtxwcjjvf4ny9wsw9emgnpazey2vde3xhnyqpw0",
        DestinationMode = SweepDestinationMode.StoreWallet,
        AmountSats = 200_000,
        FeesIncluded = feesIncluded,
        QuotedFeeSats = 2_500,
        FeeSats = 2_190,
        TxId = "f00dfeed",
        Trigger = SweepTrigger.Automatic,
        Status = status,
        CreatedAt = Origin
    };

    private static SweepRecord CrossChain(string? delivered, string? sourceToken = null) => new()
    {
        IdempotencyKey = Guid.NewGuid().ToString(),
        StoreId = StoreId,
        DestinationAddress = "0x1111111111111111111111111111111111111111",
        DestinationMode = SweepDestinationMode.EvmAddress,
        DestinationKind = SweepDestinationKind.EvmAddress,
        DestinationChain = "arbitrum",
        DestinationAsset = "USDT",
        DestinationAssetDecimals = 6,
        Provider = SparkCrossChainProvider.Orchestra,
        AmountSats = sourceToken is null ? 50_000 : 0,
        QuotedFeeSats = sourceToken is null ? 400 : 0,
        SourceTokenIdentifier = sourceToken,
        SourceAmountBaseUnits = sourceToken is null ? null : "36000000",
        SourceTokenDecimals = sourceToken is null ? 0 : 6,
        EstimatedOutBaseUnits = "35500000",
        DeliveredAmountBaseUnits = delivered,
        ConversionStatus = delivered is null ? SparkConversionStatus.Pending : SparkConversionStatus.Completed,
        Trigger = SweepTrigger.Manual,
        Status = SweepRecordStatus.Sent,
        CreatedAt = Origin
    };

    [Fact]
    public void Every_kind_of_row_has_one_cell_per_column()
    {
        // BTCPay pads or truncates mismatched rows on 2.4.5, but not before it: a short row shifts every later cell
        // under the wrong heading.
        SweepRecord[] records =
        [
            CooperativeExit(), CooperativeExit(SweepRecordStatus.Failed), CrossChain(null),
            CrossChain("35480000"), CrossChain(null, StableBalanceSettings.DefaultTokenIdentifier)
        ];

        foreach (var record in records)
            Assert.Equal(Columns.Count, FlintSweepsReportProvider.ToRow(record).Count);
    }

    [Fact]
    public void The_transaction_column_follows_the_currency_bitcoin_explorer_links_read()
    {
        // BTCPay's report page builds a tx_id link from the column immediately before it.
        var fields = FlintSweepsReportProvider.CreateViewDefinition().Fields;
        var tx = fields.Select((f, i) => (f, i)).Single(x => x.f.Type == "tx_id").i;

        Assert.Equal("ReceivedCurrency", fields[tx - 1].Name);
    }

    [Fact]
    public void A_cooperative_exit_with_the_fee_netted_out_reports_what_left_and_what_arrived()
    {
        var row = FlintSweepsReportProvider.ToRow(CooperativeExit());

        Assert.Equal(Origin, Cell(row, "Date"));
        Assert.Equal("Confirmed", Cell(row, "Status"));
        Assert.Equal("Automatic", Cell(row, "Trigger"));
        Assert.Equal("Store wallet", Cell(row, "DestinationType"));
        Assert.Equal("BTC", Cell(row, "SentCurrency"));
        Assert.Equal((0.002m, 8), AmountOf(row, "Sent"));
        Assert.Equal((0.0000219m, 8), AmountOf(row, "Fee"));
        Assert.Equal((0.0019781m, 8), AmountOf(row, "Received"));
        Assert.Equal("BTC", Cell(row, "ReceivedCurrency"));
        Assert.Equal("f00dfeed", Cell(row, "TransactionId"));
        Assert.Null(Cell(row, "Conversion"));
    }

    [Fact]
    public void A_cooperative_exit_with_the_fee_on_top_debits_amount_plus_fee()
    {
        var row = FlintSweepsReportProvider.ToRow(CooperativeExit(feesIncluded: false));

        Assert.Equal((0.0020219m, 8), AmountOf(row, "Sent"));
        Assert.Equal((0.002m, 8), AmountOf(row, "Received"));
    }

    [Fact]
    public void A_failed_sweep_has_no_amounts_and_keeps_its_error()
    {
        var record = CooperativeExit(SweepRecordStatus.Failed);
        record.Error = "the operator refused the exit";

        var row = FlintSweepsReportProvider.ToRow(record);

        Assert.Equal("Failed", Cell(row, "Status"));
        Assert.Null(Cell(row, "Sent"));
        Assert.Null(Cell(row, "Fee"));
        Assert.Null(Cell(row, "Received"));
        Assert.Equal("the operator refused the exit", Cell(row, "Error"));
    }

    [Fact]
    public void A_delivered_cross_chain_sweep_reports_the_delivered_stablecoin()
    {
        var row = FlintSweepsReportProvider.ToRow(CrossChain("35480000"));

        Assert.Equal("By hand", Cell(row, "Trigger"));
        Assert.Equal("USDT on arbitrum via Orchestra", Cell(row, "DestinationType"));
        Assert.Equal((0.000504m, 8), AmountOf(row, "Sent"));
        Assert.Equal((0.000004m, 8), AmountOf(row, "Fee"));
        Assert.Equal((35.48m, 6), AmountOf(row, "Received"));
        Assert.Equal("USDT", Cell(row, "ReceivedCurrency"));
        Assert.Equal("Delivered", Cell(row, "Conversion"));
    }

    [Fact]
    public void An_undelivered_cross_chain_sweep_says_its_amount_is_the_estimate()
    {
        var row = FlintSweepsReportProvider.ToRow(CrossChain(null));

        Assert.Equal((35.5m, 6), AmountOf(row, "Received"));
        Assert.Equal("Pending (amount estimated)", Cell(row, "Conversion"));
    }

    [Fact]
    public void A_token_funded_sweep_reports_the_token_and_no_sats_fee()
    {
        // The engine records no verified sats-side fee for a token-funded send; a zero would read as a free sweep.
        var row = FlintSweepsReportProvider.ToRow(CrossChain("35480000", StableBalanceSettings.DefaultTokenIdentifier));

        Assert.Equal("USDB", Cell(row, "SentCurrency"));
        Assert.Equal((36m, 6), AmountOf(row, "Sent"));
        Assert.Null(Cell(row, "Fee"));
    }

    [Fact]
    public async Task The_report_lists_the_stores_sweeps_in_its_range()
    {
        var store = new InMemorySweepRecordStore();
        await store.AddAsync(CooperativeExit(), Ct);
        var provider = new FlintSweepsReportProvider(store, new CapturingLogger<FlintSweepsReportProvider>());
        var context = new QueryContext(StoreId, Origin.AddDays(-1), Origin);

        await provider.Query(context, Ct);

        Assert.NotNull(context.ViewDefinition);
        Assert.Single(context.Data);
    }

    [Fact]
    public async Task A_store_failure_returns_an_empty_report_instead_of_throwing()
    {
        // An exception out of Query reaches BTCPay's plugin crash guard, which disables the plugin and restarts
        // the server.
        var store = new InMemorySweepRecordStore { FailReportWith = new InvalidOperationException("database down") };
        await store.AddAsync(CooperativeExit(), Ct);
        var logger = new CapturingLogger<FlintSweepsReportProvider>();
        var provider = new FlintSweepsReportProvider(store, logger);
        var context = new QueryContext(StoreId, Origin.AddDays(-1), Origin);

        await provider.Query(context, Ct);

        Assert.NotNull(context.ViewDefinition);
        Assert.Empty(context.Data);
        Assert.Contains("could not be built", logger.AllText);
    }

    [Fact]
    public async Task A_cancelled_request_does_not_throw()
    {
        var store = new InMemorySweepRecordStore();
        await store.AddAsync(CooperativeExit(), Ct);
        var provider = new FlintSweepsReportProvider(store, new CapturingLogger<FlintSweepsReportProvider>());
        var context = new QueryContext(StoreId, Origin.AddDays(-1), Origin);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await provider.Query(context, cancelled.Token);

        Assert.Empty(context.Data);
    }
}
