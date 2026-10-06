using System.Globalization;
using BTCPayServer.Plugins.Flint.Payments;
using BTCPayServer.Plugins.Flint.Reports;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using BTCPayServer.Services.Reporting;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

public class FlintStablecoinPaymentsReportProviderTests
{
    private const string StoreId = "store-1";
    private static readonly DateTimeOffset Origin = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly List<string> Columns =
        FlintStablecoinPaymentsReportProvider.CreateViewDefinition().Fields.Select(f => f.Name).ToList();

    private static object? Cell(IList<object?> row, string column) => row[Columns.IndexOf(column)];

    private static (decimal Value, int Decimals) AmountOf(IList<object?> row, string column)
    {
        var amount = Assert.IsType<JObject>(Cell(row, column));
        return (decimal.Parse(amount["v"]!.ToString(), CultureInfo.InvariantCulture), amount["d"]!.Value<int>());
    }

    private static StablecoinPaymentHistoryEntry Payment(
        string destinationAsset = "BTC",
        string? delivered = "1170",
        string? externalTxHash = "0xabc123",
        decimal? invoiceAmount = 1m) => new(
        Origin,
        "inv-1",
        "order-9",
        true,
        StablecoinPayments.Usdc,
        1.004512m,
        new StablecoinPaymentDetails
        {
            QuoteId = "quote-1",
            Chain = "base",
            Asset = "USDC",
            DepositAddress = "0x2222222222222222222222222222222222222222",
            ExternalTxHash = externalTxHash,
            DestinationAsset = destinationAsset,
            DeliveredAmount = delivered,
            SdkPaymentId = "spark-payment-1"
        },
        "USD",
        invoiceAmount,
        2);

    private sealed class FakeHistory(params StablecoinPaymentHistoryEntry[] entries) : IStablecoinPaymentHistory
    {
        public Exception? FailWith { get; init; }

        public Task<IReadOnlyList<StablecoinPaymentHistoryEntry>> ListAsync(
            string storeId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailWith is not null)
                throw FailWith;
            return Task.FromResult<IReadOnlyList<StablecoinPaymentHistoryEntry>>(entries);
        }
    }

    private static FlintStablecoinPaymentsReportProvider Provider(
        IStablecoinPaymentHistory history,
        Func<bool>? available = null,
        CapturingLogger<FlintStablecoinPaymentsReportProvider>? logger = null) =>
        new(history, available ?? (() => true), logger ?? new CapturingLogger<FlintStablecoinPaymentsReportProvider>());

    [Fact]
    public void Every_row_has_one_cell_per_column()
    {
        StablecoinPaymentHistoryEntry[] payments =
            [Payment(), Payment("USDB", "1004000"), Payment(delivered: null, externalTxHash: null, invoiceAmount: null)];

        foreach (var payment in payments)
            Assert.Equal(Columns.Count, FlintStablecoinPaymentsReportProvider.ToRow(payment).Count);
    }

    [Fact]
    public void A_payment_landed_as_bitcoin_reports_the_coin_the_network_and_the_sats_received()
    {
        var row = FlintStablecoinPaymentsReportProvider.ToRow(Payment());

        Assert.Equal(Origin, Cell(row, "Date"));
        Assert.Equal("inv-1", Cell(row, "InvoiceId"));
        Assert.Equal("order-9", Cell(row, "OrderId"));
        Assert.Equal(true, Cell(row, "Confirmed"));
        Assert.Equal(StablecoinPayments.ChainName("base"), Cell(row, "Network"));
        Assert.Equal("0xabc123", Cell(row, "PayerTransaction"));
        Assert.Equal("USDC", Cell(row, "PaidCurrency"));
        Assert.Equal((1.004512m, 6), AmountOf(row, "Paid"));
        Assert.Equal("BTC", Cell(row, "ReceivedCurrency"));
        Assert.Equal((0.0000117m, 8), AmountOf(row, "Received"));
        Assert.Equal("USD", Cell(row, "InvoiceCurrency"));
        Assert.Equal((1m, 2), AmountOf(row, "InvoiceCurrencyAmount"));
    }

    [Fact]
    public void A_payment_landed_as_a_stable_balance_token_is_six_decimal()
    {
        var row = FlintStablecoinPaymentsReportProvider.ToRow(Payment("USDB", "1004000"));

        Assert.Equal("USDB", Cell(row, "ReceivedCurrency"));
        Assert.Equal((1.004m, 6), AmountOf(row, "Received"));
    }

    [Fact]
    public void Missing_figures_are_left_empty_rather_than_zero()
    {
        var row = FlintStablecoinPaymentsReportProvider.ToRow(
            Payment(delivered: null, externalTxHash: null, invoiceAmount: null));

        Assert.Null(Cell(row, "Received"));
        Assert.Null(Cell(row, "PayerTransaction"));
        Assert.Null(Cell(row, "InvoiceCurrencyAmount"));
    }

    [Fact]
    public void The_payers_transaction_is_not_a_bitcoin_explorer_link()
    {
        // BTCPay would build an explorer link from the preceding column; it has no explorer for the payer's chain.
        Assert.DoesNotContain(FlintStablecoinPaymentsReportProvider.CreateViewDefinition().Fields, f => f.Type == "tx_id");
    }

    [Fact]
    public async Task The_report_lists_the_payments()
    {
        var context = new QueryContext(StoreId, Origin.AddDays(-1), Origin);

        await Provider(new FakeHistory(Payment(), Payment("USDB", "1004000"))).Query(context, Ct);

        Assert.NotNull(context.ViewDefinition);
        Assert.Equal(2, context.Data.Count);
    }

    [Fact]
    public async Task A_failure_returns_an_empty_report_instead_of_throwing()
    {
        var logger = new CapturingLogger<FlintStablecoinPaymentsReportProvider>();
        var context = new QueryContext(StoreId, Origin.AddDays(-1), Origin);

        await Provider(new FakeHistory(Payment()) { FailWith = new InvalidOperationException("db down") },
            logger: logger).Query(context, Ct);

        Assert.NotNull(context.ViewDefinition);
        Assert.Empty(context.Data);
        Assert.Contains("could not be built", logger.AllText);
    }

    [Fact]
    public async Task A_cancelled_request_does_not_throw()
    {
        var context = new QueryContext(StoreId, Origin.AddDays(-1), Origin);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Provider(new FakeHistory(Payment())).Query(context, cancelled.Token);

        Assert.Empty(context.Data);
    }

    [Fact]
    public void The_report_is_hidden_where_stablecoins_are_unavailable_and_when_that_cannot_be_read()
    {
        Assert.True(Provider(new FakeHistory(), () => true).IsAvailable());
        Assert.False(Provider(new FakeHistory(), () => false).IsAvailable());
        Assert.False(Provider(new FakeHistory(), () => throw new InvalidOperationException()).IsAvailable());
    }
}
