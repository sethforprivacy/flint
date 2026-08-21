using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Tests.Postgres;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The <see cref="IUnilateralExitRecordStore"/> contract, asserted against the production EF store and the
/// in-memory one the service tests run on.
/// </summary>
/// <remarks>
/// <para>
/// The exit service's own tests run entirely against the in-memory store, so they mean nothing if the two
/// implementations disagree — and on this table a disagreement is expensive. A column missing from the model
/// reads back as its default rather than failing, which silently discards the merchant's only copy of a signed
/// transaction set; a compare-and-set that is really a blind write lets an abandon clobber a build; and a store
/// that permits two active exits permits two signed transaction sets over the same statechain nodes.
/// </para>
/// <para>
/// Every test scopes itself to its own store id rather than relying on a truncated table. The shared Postgres
/// fixture does truncate this table between tests, but the isolation here deliberately comes from the store
/// scope instead, which is also what every production read is scoped by.
/// </para>
/// </remarks>
public abstract class UnilateralExitRecordStoreContractTests
{
    private const string Destination = "bcrt1qtxwcjjvf4ny9wsw9emgnpazey2vde3xhnyqpw0";
    private const string Funding = "bcrt1q9wpzfrqx3l9dhwvpvsrjgnd8x9tfkgdhkfxpu6";

    protected abstract Task<IUnilateralExitRecordStore> CreateStoreAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Origin = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A store id no other test shares. See the remarks on the class.</summary>
    private readonly string _storeId = "store-" + Guid.NewGuid().ToString("N");

    private readonly string _otherStoreId = "store-" + Guid.NewGuid().ToString("N");

    private UnilateralExitRecord NewRecord(
        string id,
        string? storeId = null,
        UnilateralExitStatus status = UnilateralExitStatus.AwaitingFunding,
        int minutesOld = 0,
        long fundingKeyIndex = 0) => new()
    {
        Id = id,
        StoreId = storeId ?? _storeId,
        Status = status,
        CreatedUtc = Origin.AddMinutes(-minutesOld),
        UpdatedUtc = Origin.AddMinutes(-minutesOld),
        DestinationAddress = Destination,
        FeeRateSatPerVbyte = 12,
        LeafIdsJson = """["leaf-a","leaf-b"]""",
        RecoverableValueSat = 480_000,
        TotalFeeSat = 31_000,
        SingleUtxoFundingSat = 44_000,
        FundingAddress = Funding,
        FundingKeyIndex = fundingKeyIndex
    };

    [Fact]
    public async Task A_record_round_trips_with_every_field()
    {
        // Not an assertion that a property setter works: the round trip goes through the store, so this is what
        // proves the entity is mapped. An unmapped column reads back as its default, and on this table that means
        // signed transactions nobody can broadcast any more.
        var store = await CreateStoreAsync();
        var record = NewRecord("exit-1", fundingKeyIndex: 7);
        record.FundingUtxosJson = """[{"Txid":"aa","Vout":0,"ValueSat":44000,"PubkeyHex":"02ff"}]""";
        record.TransactionsJson = """[{"Kind":"Fanout","Txid":"bb","TxHex":"0200"}]""";
        record.LastError = "nothing in particular";

        Assert.True(await store.CreateAsync(record, Ct));
        var read = await store.GetAsync(_storeId, "exit-1", Ct);

        Assert.NotNull(read);
        Assert.Equal(_storeId, read.StoreId);
        Assert.Equal(UnilateralExitStatus.AwaitingFunding, read.Status);
        Assert.Equal(Origin, read.CreatedUtc);
        Assert.Equal(Origin, read.UpdatedUtc);
        Assert.Equal(Destination, read.DestinationAddress);
        Assert.Equal(12, read.FeeRateSatPerVbyte);
        Assert.Equal("""["leaf-a","leaf-b"]""", read.LeafIdsJson);
        Assert.Equal(480_000, read.RecoverableValueSat);
        Assert.Equal(31_000, read.TotalFeeSat);
        Assert.Equal(44_000, read.SingleUtxoFundingSat);
        Assert.Equal(Funding, read.FundingAddress);
        Assert.Equal(7, read.FundingKeyIndex);
        Assert.Equal(record.FundingUtxosJson, read.FundingUtxosJson);
        Assert.Equal(record.TransactionsJson, read.TransactionsJson);
        Assert.Equal("nothing in particular", read.LastError);
    }

    [Fact]
    public async Task Reusing_an_id_is_refused()
    {
        // An exception rather than a false: a reused id is a programming error, and reporting it as the ordinary
        // "this store already has an exit" refusal would let the caller believe its row was stored.
        var store = await CreateStoreAsync();
        Assert.True(await store.CreateAsync(
            NewRecord("exit-1", status: UnilateralExitStatus.Completed), Ct));

        await Assert.ThrowsAnyAsync<Exception>(() => store.CreateAsync(
            NewRecord("exit-1", status: UnilateralExitStatus.Completed), Ct));
    }

    [Fact]
    public async Task A_record_is_not_readable_from_another_store()
    {
        var store = await CreateStoreAsync();
        await store.CreateAsync(NewRecord("exit-1"), Ct);

        Assert.Null(await store.GetAsync(_otherStoreId, "exit-1", Ct));
        Assert.Null(await store.GetActiveForStoreAsync(_otherStoreId, Ct));
        Assert.Empty(await store.ListTerminalForStoreAsync(_otherStoreId, 10, Ct));
        Assert.Equal(0, await store.NextFundingKeyIndexAsync(_otherStoreId, Ct));
    }

    [Fact]
    public async Task A_second_active_exit_for_one_store_is_refused()
    {
        // The durable half of the single-flight rule. The service checks for an active exit before quoting, but
        // that check and the insert are two statements — so the store has to be the one that says no, or two
        // exits end up committing the same statechain nodes to two different sets of signed transactions.
        var store = await CreateStoreAsync();
        Assert.True(await store.CreateAsync(NewRecord("exit-1"), Ct));

        Assert.False(await store.CreateAsync(NewRecord("exit-2", fundingKeyIndex: 1), Ct));
        Assert.Null(await store.GetAsync(_storeId, "exit-2", Ct));

        // Another store is unaffected, and a terminal row is outside the index's filter entirely.
        Assert.True(await store.CreateAsync(NewRecord("exit-3", _otherStoreId), Ct));
        Assert.True(await store.CreateAsync(
            NewRecord("exit-4", status: UnilateralExitStatus.Abandoned, fundingKeyIndex: 1), Ct));
    }

    [Fact]
    public async Task Finishing_an_exit_lets_the_store_quote_another()
    {
        var store = await CreateStoreAsync();
        var record = NewRecord("exit-1");
        await store.CreateAsync(record, Ct);

        record.Status = UnilateralExitStatus.Completed;
        Assert.True(await store.UpdateAsync(record, UnilateralExitStatus.AwaitingFunding, Ct));

        Assert.True(await store.CreateAsync(NewRecord("exit-2", fundingKeyIndex: 1), Ct));
    }

    [Fact]
    public async Task An_update_writes_the_build_result_and_leaves_the_exit_s_identity_alone()
    {
        // The identity columns are what the operator approved and funded against, so a caller that hands back a
        // mutated copy must not be able to rewrite the exit into a different one.
        var store = await CreateStoreAsync();
        await store.CreateAsync(NewRecord("exit-1"), Ct);

        var record = NewRecord("exit-1");
        record.Status = UnilateralExitStatus.Built;
        record.UpdatedUtc = Origin.AddMinutes(90);
        record.TotalFeeSat = 33_500;
        record.TransactionsJson = """[{"Kind":"Fanout","Txid":"bb"}]""";
        record.DestinationAddress = "bcrt1qsomewhereelse";
        record.LeafIdsJson = """["leaf-c"]""";
        record.FeeRateSatPerVbyte = 400;
        record.CreatedUtc = Origin.AddYears(1);
        record.FundingKeyIndex = 99;

        Assert.True(await store.UpdateAsync(record, UnilateralExitStatus.AwaitingFunding, Ct));

        var read = await store.GetAsync(_storeId, "exit-1", Ct);
        Assert.NotNull(read);
        Assert.Equal(UnilateralExitStatus.Built, read.Status);
        Assert.Equal(Origin.AddMinutes(90), read.UpdatedUtc);
        Assert.Equal(33_500, read.TotalFeeSat);
        Assert.Equal("""[{"Kind":"Fanout","Txid":"bb"}]""", read.TransactionsJson);
        Assert.Equal(Destination, read.DestinationAddress);
        Assert.Equal("""["leaf-a","leaf-b"]""", read.LeafIdsJson);
        Assert.Equal(12, read.FeeRateSatPerVbyte);
        Assert.Equal(Origin, read.CreatedUtc);
        Assert.Equal(0, read.FundingKeyIndex);
    }

    [Fact]
    public async Task An_update_from_an_unexpected_status_changes_nothing()
    {
        // The compare-and-set, and the case it exists for: an abandon that read the row while it was awaiting
        // funding must not land after a build has filled it with signed transactions.
        var store = await CreateStoreAsync();
        await store.CreateAsync(NewRecord("exit-1"), Ct);

        var built = NewRecord("exit-1");
        built.Status = UnilateralExitStatus.Built;
        built.TransactionsJson = """[{"Kind":"Fanout","Txid":"bb"}]""";
        Assert.True(await store.UpdateAsync(built, UnilateralExitStatus.AwaitingFunding, Ct));

        var stale = NewRecord("exit-1");
        stale.Status = UnilateralExitStatus.Abandoned;

        Assert.False(await store.UpdateAsync(stale, UnilateralExitStatus.AwaitingFunding, Ct));

        var read = await store.GetAsync(_storeId, "exit-1", Ct);
        Assert.Equal(UnilateralExitStatus.Built, read!.Status);
        Assert.Equal("""[{"Kind":"Fanout","Txid":"bb"}]""", read.TransactionsJson);
    }

    [Fact]
    public async Task An_update_that_says_nothing_about_the_blobs_does_not_erase_them()
    {
        // Abandoning, recording a failure, or writing back a history row projected without its blobs all pass a
        // record whose JSON columns are null. Those columns are the exit's only copy of its signed transactions
        // and the outpoint they spend, so null has to mean "nothing new to say".
        var store = await CreateStoreAsync();
        var record = NewRecord("exit-1");
        record.FundingUtxosJson = """[{"Txid":"aa","Vout":0,"ValueSat":44000,"PubkeyHex":"02ff"}]""";
        record.TransactionsJson = """[{"Kind":"Fanout","Txid":"bb","TxHex":"0200"}]""";
        record.Status = UnilateralExitStatus.Built;
        await store.CreateAsync(record, Ct);

        var abandoning = NewRecord("exit-1", status: UnilateralExitStatus.Abandoned);
        Assert.True(await store.UpdateAsync(abandoning, UnilateralExitStatus.Built, Ct));

        var read = await store.GetAsync(_storeId, "exit-1", Ct);
        Assert.Equal(UnilateralExitStatus.Abandoned, read!.Status);
        Assert.Equal(record.FundingUtxosJson, read.FundingUtxosJson);
        Assert.Equal(record.TransactionsJson, read.TransactionsJson);
    }

    [Fact]
    public async Task An_update_clears_a_previous_error()
    {
        // Null is an assignment here rather than "nothing new to say": a build that got further must not leave the
        // failed attempt's complaint on the page next to its own result.
        var store = await CreateStoreAsync();
        var record = NewRecord("exit-1");
        record.LastError = "not enough on the funding address";
        await store.CreateAsync(record, Ct);

        record.LastError = null;
        record.Status = UnilateralExitStatus.Built;
        Assert.True(await store.UpdateAsync(record, UnilateralExitStatus.AwaitingFunding, Ct));

        var read = await store.GetAsync(_storeId, "exit-1", Ct);
        Assert.Null(read!.LastError);
    }

    [Fact]
    public async Task An_update_from_another_store_changes_nothing()
    {
        var store = await CreateStoreAsync();
        await store.CreateAsync(NewRecord("exit-1"), Ct);

        var impostor = NewRecord("exit-1", _otherStoreId);
        impostor.Status = UnilateralExitStatus.Abandoned;

        Assert.False(await store.UpdateAsync(impostor, UnilateralExitStatus.AwaitingFunding, Ct));

        var read = await store.GetAsync(_storeId, "exit-1", Ct);
        Assert.Equal(UnilateralExitStatus.AwaitingFunding, read!.Status);
    }

    [Fact]
    public async Task An_update_to_an_unknown_exit_reports_that_it_did_nothing()
    {
        var store = await CreateStoreAsync();

        Assert.False(await store.UpdateAsync(
            NewRecord("exit-missing"), UnilateralExitStatus.AwaitingFunding, Ct));
    }

    [Fact]
    public async Task The_active_exit_is_the_one_that_has_not_finished()
    {
        // Both non-terminal statuses count, which is the single-flight guard: an exit holding unbroadcast
        // transactions occupies the store just as much as one waiting for its funding.
        var store = await CreateStoreAsync();
        await store.CreateAsync(NewRecord("exit-done", status: UnilateralExitStatus.Completed), Ct);
        await store.CreateAsync(
            NewRecord("exit-gone", status: UnilateralExitStatus.Abandoned, fundingKeyIndex: 1), Ct);
        await store.CreateAsync(NewRecord("exit-built", status: UnilateralExitStatus.Built, fundingKeyIndex: 2), Ct);

        Assert.Equal("exit-built", (await store.GetActiveForStoreAsync(_storeId, Ct))!.Id);

        var built = NewRecord("exit-built", status: UnilateralExitStatus.Completed, fundingKeyIndex: 2);
        await store.UpdateAsync(built, UnilateralExitStatus.Built, Ct);
        await store.CreateAsync(NewRecord("exit-waiting", fundingKeyIndex: 3), Ct);

        Assert.Equal("exit-waiting", (await store.GetActiveForStoreAsync(_storeId, Ct))!.Id);
    }

    [Fact]
    public async Task Abandoning_the_last_active_exit_frees_the_store()
    {
        // The whole reason Abandoned exists: an exit with no way forward would otherwise block every later one.
        var store = await CreateStoreAsync();
        var record = NewRecord("exit-1");
        await store.CreateAsync(record, Ct);

        record.Status = UnilateralExitStatus.Abandoned;
        await store.UpdateAsync(record, UnilateralExitStatus.AwaitingFunding, Ct);

        Assert.Null(await store.GetActiveForStoreAsync(_storeId, Ct));
    }

    [Fact]
    public async Task History_lists_finished_exits_newest_first_and_honours_the_limit()
    {
        var store = await CreateStoreAsync();
        await store.CreateAsync(
            NewRecord("exit-1", status: UnilateralExitStatus.Completed, minutesOld: 30), Ct);
        await store.CreateAsync(
            NewRecord("exit-2", status: UnilateralExitStatus.Abandoned, minutesOld: 20, fundingKeyIndex: 1), Ct);
        await store.CreateAsync(
            NewRecord("exit-3", status: UnilateralExitStatus.Completed, minutesOld: 10, fundingKeyIndex: 2), Ct);
        // Active, so it belongs to the page's own panel and not to the history table.
        await store.CreateAsync(NewRecord("exit-live", fundingKeyIndex: 3), Ct);

        var page = await store.ListTerminalForStoreAsync(_storeId, 2, Ct);

        Assert.Equal(["exit-3", "exit-2"], page.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task History_rows_do_not_carry_the_json_columns()
    {
        // The history table renders scalars. Dragging every signed transaction set in a store's past out of the
        // database to render a date and a status is the cost this projection exists to avoid, so the absence is
        // asserted rather than assumed.
        var store = await CreateStoreAsync();
        var record = NewRecord("exit-1", status: UnilateralExitStatus.Completed);
        record.FundingUtxosJson = """[{"Txid":"aa","Vout":0,"ValueSat":44000,"PubkeyHex":"02ff"}]""";
        record.TransactionsJson = """[{"Kind":"Fanout","Txid":"bb","TxHex":"0200"}]""";
        record.LastError = "something worth reading";
        await store.CreateAsync(record, Ct);

        var row = Assert.Single(await store.ListTerminalForStoreAsync(_storeId, 10, Ct));

        Assert.Null(row.FundingUtxosJson);
        Assert.Null(row.TransactionsJson);
        Assert.Equal(string.Empty, row.LeafIdsJson);
        // Everything the table actually shows is there.
        Assert.Equal(UnilateralExitStatus.Completed, row.Status);
        Assert.Equal(Origin, row.CreatedUtc);
        Assert.Equal(Destination, row.DestinationAddress);
        Assert.Equal(480_000, row.RecoverableValueSat);
        Assert.Equal("something worth reading", row.LastError);
    }

    [Fact]
    public async Task A_non_positive_limit_is_refused()
    {
        var store = await CreateStoreAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.ListTerminalForStoreAsync(_storeId, 0, Ct));
    }

    [Fact]
    public async Task The_next_funding_key_index_is_one_past_every_index_ever_issued()
    {
        // Terminal rows count. Reusing an index re-issues a funding address that may still hold sats from an
        // abandoned exit, and the next build would then select that stale output as if the operator had just sent
        // it.
        var store = await CreateStoreAsync();
        Assert.Equal(0, await store.NextFundingKeyIndexAsync(_storeId, Ct));

        await store.CreateAsync(
            NewRecord("exit-1", status: UnilateralExitStatus.Abandoned, fundingKeyIndex: 0), Ct);
        Assert.Equal(1, await store.NextFundingKeyIndexAsync(_storeId, Ct));

        await store.CreateAsync(
            NewRecord("exit-2", status: UnilateralExitStatus.Completed, fundingKeyIndex: 4), Ct);
        Assert.Equal(5, await store.NextFundingKeyIndexAsync(_storeId, Ct));

        // Per store, not per server: another store's indexes are its own.
        await store.CreateAsync(NewRecord("exit-3", _otherStoreId, fundingKeyIndex: 40), Ct);
        Assert.Equal(5, await store.NextFundingKeyIndexAsync(_storeId, Ct));
        Assert.Equal(41, await store.NextFundingKeyIndexAsync(_otherStoreId, Ct));
    }
}

/// <summary>The contract against the production EF store and a real Postgres database.</summary>
[Trait("Category", "Postgres")]
[Collection(PostgresTestDatabase.CollectionName)]
public class PostgresUnilateralExitRecordStoreTests : UnilateralExitRecordStoreContractTests
{
    private readonly PostgresTestDatabase _database;

    public PostgresUnilateralExitRecordStoreTests(PostgresTestDatabase database) => _database = database;

    protected override async Task<IUnilateralExitRecordStore> CreateStoreAsync() =>
        new EfUnilateralExitRecordStore(await _database.CreateFactoryAsync());
}
