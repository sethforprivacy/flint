using System.Numerics;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using BTCPayServer.Plugins.Flint.Tests.Postgres;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The <see cref="IStablecoinQuoteStore"/> contract, asserted against every implementation.
/// </summary>
/// <remarks>
/// The service tests run against the in-memory store, so they mean something only if the Postgres one agrees —
/// above all on the two guards the whole attribution story rests on: a quote settles once, and a payment settles
/// one quote.
/// </remarks>
public abstract class StablecoinQuoteStoreContractTests
{
    private const string StoreId = "store-1";
    private const string OtherStoreId = "store-2";

    protected abstract Task<IStablecoinQuoteStore> CreateStoreAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StablecoinQuote Quote(
        string? id = null,
        string storeId = StoreId,
        string invoiceId = "invoice-1",
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? createdAt = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString("N"),
        StoreId = storeId,
        InvoiceId = invoiceId,
        PaymentMethodId = "USDC-FLINT",
        Chain = "bsc",
        ChainId = "56",
        Asset = "USDC",
        ContractAddress = "0x8AC76a51cc950d9822D68b83fE1Ad97B32Cd580d",
        Decimals = 18,
        DepositAddress = "0x00000000000000000000000000000000000000aa",
        // Eighteen decimals on purpose: these overflow a long at a few dollars, which is why they are strings.
        DepositBaseUnits = "10087341234567890123",
        AskedBaseUnits = "10087342000000000000",
        PaymentRequest = "ethereum:0x8AC76a51cc950d9822D68b83fE1Ad97B32Cd580d@56/transfer?address=0x00000000000000000000000000000000000000aa&uint256=10087342000000000000",
        DueAmount = 10.000000m,
        FeeAmount = 0.087342m,
        ExpectedReceivedBaseUnits = "10000",
        DestinationAsset = "BTC",
        ServiceFeeBaseUnits = "80000",
        ServiceFeeAsset = "USDC",
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(15)
    };

    private static StablecoinSettlement Settlement(string paymentId) => new(
        paymentId,
        BigInteger.Parse("10087342000000000000"),
        new BigInteger(10_001),
        "0xpayer",
        "order-1",
        "quote-1",
        DateTimeOffset.UtcNow);

    [Fact]
    public async Task A_quote_round_trips_with_its_eighteen_decimal_amounts()
    {
        var store = await CreateStoreAsync();
        var quote = Quote();

        await store.AddAsync(quote, Ct);
        var read = Assert.Single(await store.ListForInvoiceAsync("invoice-1", Ct));

        Assert.Equal(quote.Id, read.Id);
        Assert.Equal(BigInteger.Parse("10087342000000000000"), read.Asked);
        Assert.Equal(10.000000m, read.DueAmount);
        Assert.Equal(0.087342m, read.FeeAmount);
        Assert.Equal(18, read.Decimals);
        Assert.Null(read.SdkPaymentId);
    }

    [Fact]
    public async Task Open_quotes_are_the_unsettled_ones_inside_the_window_for_one_store()
    {
        var store = await CreateStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var open = Quote(expiresAt: now.AddMinutes(10));
        var aged = Quote(expiresAt: now.AddHours(-49));
        var other = Quote(storeId: OtherStoreId, expiresAt: now.AddMinutes(10));
        var settled = Quote(expiresAt: now.AddMinutes(10));
        foreach (var quote in new[] { open, aged, other, settled })
            await store.AddAsync(quote, Ct);
        Assert.True(await store.TrySettleAsync(settled.Id, Settlement("pay-1"), Ct));

        var listed = await store.ListOpenAsync(StoreId, now.AddHours(-48), Ct);

        Assert.Equal([open.Id], listed.Select(q => q.Id).ToArray());
        var stores = await store.ListStoresWithOpenQuotesAsync(now.AddHours(-48), Ct);
        Assert.Equal([StoreId, OtherStoreId], stores.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task A_quote_settles_once()
    {
        var store = await CreateStoreAsync();
        var quote = Quote();
        await store.AddAsync(quote, Ct);

        Assert.True(await store.TrySettleAsync(quote.Id, Settlement("pay-1"), Ct));
        Assert.False(await store.TrySettleAsync(quote.Id, Settlement("pay-2"), Ct));

        var read = await store.FindBySdkPaymentIdAsync(StoreId, "pay-1", Ct);
        Assert.NotNull(read);
        Assert.Equal("10087342000000000000", read!.PaidBaseUnits);
        Assert.Equal("10001", read.DeliveredBaseUnits);
        Assert.Equal("0xpayer", read.ExternalTxHash);
        Assert.Null(await store.FindBySdkPaymentIdAsync(StoreId, "pay-2", Ct));
    }

    [Fact]
    public async Task One_payment_settles_one_quote()
    {
        // The guard that stops a single arrival being credited to two invoices, whatever the matcher concluded.
        var store = await CreateStoreAsync();
        var first = Quote();
        var second = Quote(invoiceId: "invoice-2");
        await store.AddAsync(first, Ct);
        await store.AddAsync(second, Ct);

        Assert.True(await store.TrySettleAsync(first.Id, Settlement("pay-1"), Ct));
        Assert.False(await store.TrySettleAsync(second.Id, Settlement("pay-1"), Ct));

        Assert.Single(await store.ListOpenAsync(StoreId, DateTimeOffset.UtcNow.AddHours(-48), Ct));
    }

    [Fact]
    public async Task A_payment_is_found_only_by_its_own_store()
    {
        var store = await CreateStoreAsync();
        var quote = Quote();
        await store.AddAsync(quote, Ct);
        await store.TrySettleAsync(quote.Id, Settlement("pay-1"), Ct);

        Assert.Null(await store.FindBySdkPaymentIdAsync(OtherStoreId, "pay-1", Ct));
    }

    [Fact]
    public async Task A_settled_quote_awaits_credit_until_marked_and_is_marked_once()
    {
        var store = await CreateStoreAsync();
        var quote = Quote();
        await store.AddAsync(quote, Ct);

        // Not settled yet: nothing to credit, and nothing to mark.
        Assert.Empty(await store.ListUncreditedAsync(StoreId, 10, Ct));
        Assert.False(await store.TryMarkCreditedAsync(quote.Id, DateTimeOffset.UtcNow, Ct));

        await store.TrySettleAsync(quote.Id, Settlement("pay-1"), Ct);
        Assert.Equal([quote.Id], (await store.ListUncreditedAsync(StoreId, 10, Ct)).Select(q => q.Id).ToArray());
        Assert.Equal([StoreId], (await store.ListStoresAwaitingCreditAsync(Ct)).ToArray());

        Assert.True(await store.TryMarkCreditedAsync(quote.Id, DateTimeOffset.UtcNow, Ct));
        Assert.False(await store.TryMarkCreditedAsync(quote.Id, DateTimeOffset.UtcNow, Ct));
        Assert.Empty(await store.ListUncreditedAsync(StoreId, 10, Ct));
        Assert.Empty(await store.ListStoresAwaitingCreditAsync(Ct));
    }

    [Fact]
    public async Task Retention_drops_finished_quotes_and_keeps_an_uncredited_settlement()
    {
        var store = await CreateStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var credited = Quote(expiresAt: now.AddDays(-40));
        var unpaid = Quote(expiresAt: now.AddDays(-40));
        var stranded = Quote(expiresAt: now.AddDays(-40));
        var live = Quote(expiresAt: now.AddMinutes(10));
        foreach (var quote in new[] { credited, unpaid, stranded, live })
            await store.AddAsync(quote, Ct);
        await store.TrySettleAsync(credited.Id, Settlement("pay-1"), Ct);
        await store.TryMarkCreditedAsync(credited.Id, now.AddDays(-35), Ct);
        await store.TrySettleAsync(stranded.Id, Settlement("pay-2"), Ct);

        Assert.Equal(2, await store.DeleteFinishedAsync(now.AddDays(-30), Ct));

        var left = await store.ListForInvoiceAsync("invoice-1", Ct);
        Assert.Equal(new[] { live.Id, stranded.Id }.Order(), left.Select(q => q.Id).Order());
    }

    [Fact]
    public async Task An_invoice_lists_its_quotes_newest_first()
    {
        var store = await CreateStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var older = Quote(createdAt: now.AddMinutes(-5));
        var newer = Quote(createdAt: now);
        await store.AddAsync(older, Ct);
        await store.AddAsync(newer, Ct);
        await store.AddAsync(Quote(invoiceId: "invoice-2"), Ct);

        var listed = await store.ListForInvoiceAsync("invoice-1", Ct);

        Assert.Equal([newer.Id, older.Id], listed.Select(q => q.Id).ToArray());
    }
}

/// <summary>The contract against the in-memory implementation the service tests use.</summary>
public class InMemoryStablecoinQuoteStoreTests : StablecoinQuoteStoreContractTests
{
    protected override Task<IStablecoinQuoteStore> CreateStoreAsync() =>
        Task.FromResult<IStablecoinQuoteStore>(new InMemoryStablecoinQuoteStore());
}

/// <summary>The same contract against the production EF store and a real Postgres database.</summary>
[Trait("Category", "Postgres")]
[Collection(PostgresTestDatabase.CollectionName)]
public class PostgresStablecoinQuoteStoreTests : StablecoinQuoteStoreContractTests
{
    private readonly PostgresTestDatabase _database;

    public PostgresStablecoinQuoteStoreTests(PostgresTestDatabase database) => _database = database;

    protected override async Task<IStablecoinQuoteStore> CreateStoreAsync() =>
        new EfStablecoinQuoteStore(await _database.CreateFactoryAsync());
}
