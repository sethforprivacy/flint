using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using BTCPayServer.Plugins.Flint.Tests.Postgres;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The <see cref="ISweepRecordStore"/> contract, asserted against every implementation.
/// </summary>
/// <remarks>
/// Written as a contract for the same reason as the other two stores': the sweep-engine tests run against the
/// in-memory implementation and mean nothing unless the real one agrees. The outgoing-payment store's cross-store
/// key defect was invisible for exactly as long as only one implementation was tested.
/// </remarks>
public abstract class SweepRecordStoreContractTests
{
    private const string StoreId = "store-1";
    private const string OtherStoreId = "store-2";
    private const string Destination = "bcrt1qtxwcjjvf4ny9wsw9emgnpazey2vde3xhnyqpw0";

    protected abstract Task<ISweepRecordStore> CreateStoreAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Origin = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private static SweepRecord NewRecord(
        string key,
        string storeId = StoreId,
        SweepRecordStatus status = SweepRecordStatus.Pending,
        int minutesOld = 0,
        SweepTrigger trigger = SweepTrigger.Automatic) => new()
    {
        IdempotencyKey = key,
        StoreId = storeId,
        DestinationAddress = Destination,
        DestinationMode = SweepDestinationMode.StoreWallet,
        AmountSats = 200_000,
        FeesIncluded = true,
        ConfirmationSpeed = SweepConfirmationSpeed.Medium,
        QuotedFeeSats = 2_190,
        BalanceAtDecisionSats = 200_000,
        Trigger = trigger,
        Status = status,
        CreatedAt = Origin.AddMinutes(-minutesOld)
    };

    private static SweepRecord NewRefusal(
        string key,
        SweepRefusalCode code,
        string storeId = StoreId,
        int minutesOld = 0,
        SweepTrigger trigger = SweepTrigger.Automatic,
        string error = "refused") 
    {
        var record = NewRecord(key, storeId, SweepRecordStatus.Refused, minutesOld, trigger);
        record.RefusalCode = code;
        record.Error = error;
        record.CompletedAt = record.CreatedAt;
        return record;
    }

    /// <remarks>
    /// <para>
    /// The round trip goes through the store, so on the Postgres implementation this is what proves the entity is
    /// mapped (a column missing from the model silently reads back as its default), and on the in-memory one it
    /// proves the hand-written <c>InMemorySweepRecordStore.Copy</c> carries every column. That second omission
    /// already happened: when Wave 7 added the cross-chain columns none of them were copied, so a cross-chain row
    /// came back as a cooperative exit and every engine test exercised the wrong crash-recovery branch.
    /// </para>
    /// <para>
    /// Every property is set through <see cref="EveryProperty"/>, so a dropped column fails, and so do two
    /// swapped ones wherever <see cref="DistinctValues"/> can tell them apart.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_record_round_trips_with_every_field()
    {
        // Computed properties have nothing to store.
        var properties = EveryProperty.Of<SweepRecord>(
            nameof(SweepRecord.RecipientAmountSats),
            nameof(SweepRecord.FeePercent),
            nameof(SweepRecord.IsInFlight),
            nameof(SweepRecord.LastActivityAt),
            nameof(SweepRecord.IsCrossChain));
        var record = EveryProperty.Filled<SweepRecord>(properties);

        var store = await CreateStoreAsync();
        await store.AddAsync(record, Ct);
        var read = await store.GetAsync(record.StoreId, record.IdempotencyKey, Ct);

        Assert.NotNull(read);
        EveryProperty.AssertCarried(record, read, properties, "the round trip");
    }

    [Fact]
    public async Task Reusing_an_idempotency_key_is_refused()
    {
        // The primary key is the guarantee that two sweeps cannot share a key. Papering over a collision would mean
        // one row describing two sends.
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord("key-1"), Ct);

        await Assert.ThrowsAnyAsync<Exception>(() => store.AddAsync(NewRecord("key-1"), Ct));
    }

    [Fact]
    public async Task A_record_is_not_readable_from_another_store()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord("key-1"), Ct);

        Assert.Null(await store.GetAsync(OtherStoreId, "key-1", Ct));
    }

    [Fact]
    public async Task Only_one_caller_may_resolve_a_record()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord("key-1"), Ct);
        var resolution = new SweepResolution(SweepRecordStatus.Sent, 2_190, "txid-1", null, Origin);

        Assert.True(await store.TryResolveAsync(StoreId, "key-1", [SweepRecordStatus.Pending], resolution, Ct));
        Assert.False(await store.TryResolveAsync(StoreId, "key-1", [SweepRecordStatus.Pending], resolution, Ct));

        var read = await store.GetAsync(StoreId, "key-1", Ct);
        Assert.Equal(SweepRecordStatus.Sent, read!.Status);
        Assert.Equal(2_190, read.FeeSats);
        Assert.Equal("txid-1", read.TxId);
    }

    [Fact]
    public async Task Resolving_from_a_disallowed_status_changes_nothing()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord("key-1", status: SweepRecordStatus.Confirmed), Ct);

        Assert.False(await store.TryResolveAsync(
            StoreId, "key-1", [SweepRecordStatus.Pending],
            new SweepResolution(SweepRecordStatus.Failed, null, null, "should not happen", Origin), Ct));

        var read = await store.GetAsync(StoreId, "key-1", Ct);
        Assert.Equal(SweepRecordStatus.Confirmed, read!.Status);
        Assert.Null(read.Error);
    }

    [Fact]
    public async Task Resolving_another_stores_record_changes_nothing()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord("key-1"), Ct);

        Assert.False(await store.TryResolveAsync(
            OtherStoreId, "key-1", [SweepRecordStatus.Pending],
            new SweepResolution(SweepRecordStatus.Failed, null, null, "hijacked", Origin), Ct));

        var read = await store.GetAsync(StoreId, "key-1", Ct);
        Assert.Equal(SweepRecordStatus.Pending, read!.Status);
        Assert.Null(read.Error);
    }

    [Fact]
    public async Task A_later_resolution_without_new_facts_keeps_the_txid_and_the_fee()
    {
        // The Sent -> Confirmed step. The completion event carries no new information, and an overwriting write
        // would erase the only record of which transaction holds the merchant's money.
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord("key-1"), Ct);
        await store.TryResolveAsync(
            StoreId, "key-1", [SweepRecordStatus.Pending],
            new SweepResolution(SweepRecordStatus.Sent, 2_190, "txid-1", null, Origin), Ct);

        Assert.True(await store.TryResolveAsync(
            StoreId, "key-1", [SweepRecordStatus.Pending, SweepRecordStatus.Sent],
            new SweepResolution(SweepRecordStatus.Confirmed, null, null, null, Origin.AddMinutes(1)), Ct));

        var read = await store.GetAsync(StoreId, "key-1", Ct);
        Assert.Equal(SweepRecordStatus.Confirmed, read!.Status);
        Assert.Equal(2_190, read.FeeSats);
        Assert.Equal("txid-1", read.TxId);
    }

    [Fact]
    public async Task Unresolved_lists_pending_and_sent_records_oldest_first()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord("key-new", minutesOld: 1), Ct);
        await store.AddAsync(NewRecord("key-old", minutesOld: 10), Ct);
        await store.AddAsync(NewRecord("key-sent", minutesOld: 5, status: SweepRecordStatus.Sent), Ct);
        await store.AddAsync(NewRecord("key-done", minutesOld: 6, status: SweepRecordStatus.Confirmed), Ct);
        await store.AddAsync(NewRecord("key-failed", minutesOld: 7, status: SweepRecordStatus.Failed), Ct);
        await store.AddAsync(NewRecord("key-refused", minutesOld: 8, status: SweepRecordStatus.Refused), Ct);

        var unresolved = await store.ListUnresolvedAsync(StoreId, Origin.AddMinutes(-60), Ct);

        Assert.Equal(["key-old", "key-sent", "key-new"], unresolved.Select(r => r.IdempotencyKey));
    }

    [Fact]
    public async Task Unresolved_excludes_only_old_Sent_records()
    {
        // The window stops a Sent exit whose completion is never observed being re-read forever.
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord("key-ancient", minutesOld: 200, status: SweepRecordStatus.Sent), Ct);
        await store.AddAsync(NewRecord("key-recent", minutesOld: 5, status: SweepRecordStatus.Sent), Ct);

        var unresolved = await store.ListUnresolvedAsync(StoreId, Origin.AddMinutes(-60), Ct);

        Assert.Equal(["key-recent"], unresolved.Select(r => r.IdempotencyKey));
    }

    [Fact]
    public async Task Unresolved_never_ages_out_a_Sent_cross_chain_record_whose_conversion_is_undecided()
    {
        // A cross-chain conversion's outcome has no event: it is learned only from the crash-recovery poll
        // over this very list. Ageing an undecided one out with the sats rows would strand it silently — the
        // status stuck at Sent forever and a needed refund never requested. Terminal conversions age out
        // normally; there is nothing left to learn about them.
        var store = await CreateStoreAsync();

        SweepRecord CrossChain(string key, SparkConversionStatus? conversion)
        {
            var record = NewRecord(key, minutesOld: 5_000, status: SweepRecordStatus.Sent);
            record.DestinationKind = SweepDestinationKind.EvmAddress;
            record.ConversionStatus = conversion;
            return record;
        }

        await store.AddAsync(CrossChain("key-undecided-null", null), Ct);
        await store.AddAsync(CrossChain("key-undecided-pending", SparkConversionStatus.Pending), Ct);
        await store.AddAsync(CrossChain("key-refund-needed", SparkConversionStatus.RefundNeeded), Ct);
        await store.AddAsync(CrossChain("key-completed", SparkConversionStatus.Completed), Ct);
        await store.AddAsync(CrossChain("key-conversion-failed", SparkConversionStatus.Failed), Ct);
        await store.AddAsync(CrossChain("key-refunded", SparkConversionStatus.Refunded), Ct);
        // And an aged sats Sent row, to pin that the exemption is cross-chain-only.
        await store.AddAsync(NewRecord("key-sats-ancient", minutesOld: 5_000, status: SweepRecordStatus.Sent), Ct);

        var unresolved = await store.ListUnresolvedAsync(StoreId, Origin.AddMinutes(-60), Ct);

        Assert.Equal(
            ["key-refund-needed", "key-undecided-null", "key-undecided-pending"],
            unresolved.Select(r => r.IdempotencyKey).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Unresolved_never_ages_out_a_Pending_record()
    {
        // The bug this pins: an earlier revision aged Pending rows out too, so a store whose wallet stayed down
        // longer than the window left a row that would never be resolved and never block again — a permanent
        // "Unresolved" in the history for a sweep that may well have gone through. A Pending row is the one thing
        // that must be chased until Spark answers.
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord("key-stale-pending", minutesOld: 5_000), Ct);

        var unresolved = await store.ListUnresolvedAsync(StoreId, Origin.AddMinutes(-60), Ct);

        Assert.Equal(["key-stale-pending"], unresolved.Select(r => r.IdempotencyKey));
    }

    [Fact]
    public async Task Unresolved_is_scoped_to_one_store()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord("mine", minutesOld: 1), Ct);
        await store.AddAsync(NewRecord("theirs", OtherStoreId, minutesOld: 1), Ct);

        var unresolved = await store.ListUnresolvedAsync(StoreId, Origin.AddMinutes(-60), Ct);

        Assert.Equal(["mine"], unresolved.Select(r => r.IdempotencyKey));
    }

    [Fact]
    public async Task History_pages_newest_first_without_repeating_or_skipping_a_row()
    {
        // Five rows created in the same instant, which is the case an unbroken tie-break gets wrong: rows are free
        // to swap places between two queries, so one appears on both pages and another on neither.
        var store = await CreateStoreAsync();
        for (var i = 0; i < 5; i++)
            await store.AddAsync(NewRecord($"key-{i}", status: SweepRecordStatus.Confirmed), Ct);

        var first = await store.ListAsync(StoreId, 0, 2, Ct);
        var second = await store.ListAsync(StoreId, 2, 2, Ct);
        var third = await store.ListAsync(StoreId, 4, 2, Ct);

        var seen = first.Concat(second).Concat(third).Select(r => r.IdempotencyKey).ToList();
        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());
        Assert.Equal(5, await store.CountAsync(StoreId, Ct));
    }

    [Fact]
    public async Task History_orders_by_creation_time_descending()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord("oldest", minutesOld: 30, status: SweepRecordStatus.Confirmed), Ct);
        await store.AddAsync(NewRecord("newest", minutesOld: 1, status: SweepRecordStatus.Confirmed), Ct);
        await store.AddAsync(NewRecord("middle", minutesOld: 15, status: SweepRecordStatus.Confirmed), Ct);

        var page = await store.ListAsync(StoreId, 0, 10, Ct);

        Assert.Equal(["newest", "middle", "oldest"], page.Select(r => r.IdempotencyKey));
    }

    [Fact]
    public async Task History_and_the_count_are_scoped_to_one_store()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord("mine"), Ct);
        await store.AddAsync(NewRecord("theirs", OtherStoreId), Ct);

        Assert.Equal(["mine"], (await store.ListAsync(StoreId, 0, 10, Ct)).Select(r => r.IdempotencyKey));
        Assert.Equal(1, await store.CountAsync(StoreId, Ct));
    }

    [Fact]
    public async Task An_open_refusal_is_found_by_its_code_and_not_by_its_message()
    {
        // The W4-M1 fix, at the store layer. The stored message embeds live figures, so it cannot be an identity;
        // the code can. This lookup is what stops a store parked on a refusal writing a row every pass forever.
        var store = await CreateStoreAsync();
        await store.AddAsync(
            NewRefusal("open", SweepRefusalCode.FeeAboveLimit, minutesOld: 5, error: "the fee was 2,190 sat"), Ct);

        var found = await store.FindOpenRefusalAsync(
            StoreId, SweepRefusalCode.FeeAboveLimit, SweepDestinationMode.StoreWallet, Origin.AddHours(-24), Ct);

        Assert.Equal("open", found!.IdempotencyKey);
    }

    [Fact]
    public async Task An_open_refusal_is_found_past_intervening_rows()
    {
        // Deliberately not "is the newest row this refusal?": a manual refusal or one successful sweep in the middle
        // would defeat that, and an ongoing condition does not stop being ongoing because something else happened.
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRefusal("open", SweepRefusalCode.FeeAboveLimit, minutesOld: 30), Ct);
        await store.AddAsync(
            NewRefusal("manual", SweepRefusalCode.FeeAboveLimit, minutesOld: 20, trigger: SweepTrigger.Manual), Ct);
        await store.AddAsync(NewRecord("swept", minutesOld: 10, status: SweepRecordStatus.Confirmed), Ct);

        var found = await store.FindOpenRefusalAsync(
            StoreId, SweepRefusalCode.FeeAboveLimit, SweepDestinationMode.StoreWallet, Origin.AddHours(-24), Ct);

        Assert.Equal("open", found!.IdempotencyKey);
    }

    [Theory]
    [InlineData(SweepRefusalCode.None)]
    [InlineData(SweepRefusalCode.NoDestination)]
    public async Task A_different_refusal_code_is_not_the_same_open_refusal(SweepRefusalCode code)
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRefusal("open", SweepRefusalCode.FeeAboveLimit, minutesOld: 5), Ct);

        Assert.Null(await store.FindOpenRefusalAsync(
            StoreId, code, SweepDestinationMode.StoreWallet, Origin.AddHours(-24), Ct));
    }

    [Fact]
    public async Task A_refusal_about_a_different_destination_mode_is_not_the_same_open_refusal()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRefusal("open", SweepRefusalCode.FeeAboveLimit, minutesOld: 5), Ct);

        Assert.Null(await store.FindOpenRefusalAsync(
            StoreId, SweepRefusalCode.FeeAboveLimit, SweepDestinationMode.StaticAddress, Origin.AddHours(-24), Ct));
    }

    [Fact]
    public async Task A_manual_refusal_is_never_an_open_refusal_to_fold_onto()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(
            NewRefusal("manual", SweepRefusalCode.FeeAboveLimit, minutesOld: 5, trigger: SweepTrigger.Manual), Ct);

        Assert.Null(await store.FindOpenRefusalAsync(
            StoreId, SweepRefusalCode.FeeAboveLimit, SweepDestinationMode.StoreWallet, Origin.AddHours(-24), Ct));
    }

    [Fact]
    public async Task A_refusal_last_seen_before_the_window_is_a_new_episode()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRefusal("stale", SweepRefusalCode.FeeAboveLimit, minutesOld: 5_000), Ct);

        Assert.Null(await store.FindOpenRefusalAsync(
            StoreId, SweepRefusalCode.FeeAboveLimit, SweepDestinationMode.StoreWallet, Origin.AddHours(-24), Ct));
    }

    [Fact]
    public async Task A_recurrence_extends_an_open_refusals_window_rather_than_its_creation_time()
    {
        // Which is what makes a long-running condition keep folding onto one row instead of starting a new episode
        // every 24 hours from when it began.
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRefusal("open", SweepRefusalCode.FeeAboveLimit, minutesOld: 1_000), Ct);

        Assert.True(await store.TryRecordRepeatRefusalAsync(
            StoreId, "open", "the fee is now 33,600 sat", Origin, Ct));

        var found = await store.FindOpenRefusalAsync(
            StoreId, SweepRefusalCode.FeeAboveLimit, SweepDestinationMode.StoreWallet,
            Origin.AddHours(-1), Ct);

        Assert.Equal("open", found!.IdempotencyKey);
        Assert.Equal(2, found.AttemptCount);
        Assert.Equal(Origin, found.LastSeenAt);
        // Refreshed, so the merchant reads current figures rather than the ones from when it started.
        Assert.Equal("the fee is now 33,600 sat", found.Error);
    }

    [Fact]
    public async Task Recurrences_accumulate()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRefusal("open", SweepRefusalCode.FeeAboveLimit, minutesOld: 5), Ct);

        for (var i = 0; i < 4; i++)
            await store.TryRecordRepeatRefusalAsync(StoreId, "open", $"sighting {i}", Origin, Ct);

        var read = await store.GetAsync(StoreId, "open", Ct);
        Assert.Equal(5, read!.AttemptCount);
    }

    [Fact]
    public async Task A_recurrence_cannot_touch_a_row_that_describes_a_real_send()
    {
        // The guard that keeps this from ever rewriting a sweep's own history.
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord("sent", status: SweepRecordStatus.Sent), Ct);

        Assert.False(await store.TryRecordRepeatRefusalAsync(StoreId, "sent", "not a refusal", Origin, Ct));

        var read = await store.GetAsync(StoreId, "sent", Ct);
        Assert.Equal(1, read!.AttemptCount);
        Assert.Null(read.LastSeenAt);
        Assert.Null(read.Error);
    }

    [Fact]
    public async Task A_recurrence_cannot_touch_another_stores_refusal()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRefusal("open", SweepRefusalCode.FeeAboveLimit, minutesOld: 5), Ct);

        Assert.False(await store.TryRecordRepeatRefusalAsync(OtherStoreId, "open", "hijacked", Origin, Ct));
        Assert.Equal(1, (await store.GetAsync(StoreId, "open", Ct))!.AttemptCount);
    }

    [Fact]
    public async Task An_open_refusal_lookup_is_scoped_to_one_store()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(
            NewRefusal("theirs", SweepRefusalCode.FeeAboveLimit, OtherStoreId, minutesOld: 5), Ct);

        Assert.Null(await store.FindOpenRefusalAsync(
            StoreId, SweepRefusalCode.FeeAboveLimit, SweepDestinationMode.StoreWallet, Origin.AddHours(-24), Ct));
    }

    [Fact]
    public async Task A_store_with_no_sweeps_has_no_open_refusal()
    {
        var store = await CreateStoreAsync();

        Assert.Null(await store.FindOpenRefusalAsync(
            StoreId, SweepRefusalCode.FeeAboveLimit, SweepDestinationMode.StoreWallet, Origin.AddHours(-24), Ct));
        Assert.Equal(0, await store.CountAsync(StoreId, Ct));
    }

    [Fact]
    public async Task A_recurrence_of_an_unknown_row_reports_false()
    {
        var store = await CreateStoreAsync();

        Assert.False(await store.TryRecordRepeatRefusalAsync(StoreId, "nothing-here", "x", Origin, Ct));
    }

    [Fact]
    public async Task A_resolution_can_record_a_refusal_code_and_a_later_one_cannot_erase_it()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord("key-1"), Ct);

        Assert.True(await store.TryResolveAsync(
            StoreId, "key-1", [SweepRecordStatus.Pending],
            new SweepResolution(
                SweepRecordStatus.Refused, 50_000, null, "too dear", Origin, SweepRefusalCode.FeeAboveLimit),
            Ct));

        var read = await store.GetAsync(StoreId, "key-1", Ct);
        Assert.Equal(SweepRefusalCode.FeeAboveLimit, read!.RefusalCode);

        // None means "nothing new to say", not "clear it".
        Assert.True(await store.TryResolveAsync(
            StoreId, "key-1", [SweepRecordStatus.Refused],
            new SweepResolution(SweepRecordStatus.Refused, null, null, null, Origin), Ct));
        Assert.Equal(
            SweepRefusalCode.FeeAboveLimit, (await store.GetAsync(StoreId, "key-1", Ct))!.RefusalCode);
    }

    [Theory]
    [InlineData("", "key-1")]
    [InlineData(StoreId, "")]
    public async Task Resolving_with_an_empty_identifier_is_refused_by_every_implementation(
        string storeId,
        string idempotencyKey)
    {
        // A contract test because the two implementations disagreed: the EF store threw and the in-memory one
        // silently returned false, so a caller passing an empty id would fail loudly in production and pass in
        // every test. That is exactly the divergence this contract exists to catch.
        var store = await CreateStoreAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.TryResolveAsync(
            storeId, idempotencyKey, [SweepRecordStatus.Pending],
            new SweepResolution(SweepRecordStatus.Failed, null, null, "x", Origin), Ct));
    }

    [Theory]
    [InlineData("", "store-1")]
    [InlineData("key-1", "")]
    public async Task Adding_a_record_with_an_empty_identifier_is_refused_by_every_implementation(
        string idempotencyKey,
        string storeId)
    {
        var store = await CreateStoreAsync();
        var record = NewRecord("key-1");
        record.IdempotencyKey = idempotencyKey;
        record.StoreId = storeId;

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.AddAsync(record, Ct));
    }

    [Fact]
    public async Task A_recurrence_with_an_empty_identifier_is_refused_by_every_implementation()
    {
        var store = await CreateStoreAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => store.TryRecordRepeatRefusalAsync("", "key-1", "x", Origin, Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => store.TryRecordRepeatRefusalAsync(StoreId, "", "x", Origin, Ct));
    }

    #region Cross-chain rows

    /// <summary>
    /// A cross-chain row survives storage still saying how it has to be recovered.
    /// </summary>
    /// <remarks>
    /// The two properties asserted here are the ones the sweep engine branches on after a crash:
    /// <c>IdempotencyKeyAccepted</c> decides whether the row can be resolved by asking the SDK for a payment
    /// under its key, and <c>ProviderQuoteId</c> is what it is scanned for when it cannot. Losing either turns a
    /// recoverable sweep into one that is written off as never sent — or, worse, sends a caller looking for a
    /// payment id that was never issued.
    /// <para>
    /// In the shared contract rather than against one implementation, because the in-memory store's hand-written
    /// copy dropped exactly these when they were added and the engine tests carried on passing against the wrong
    /// branch.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_cross_chain_row_keeps_the_fields_its_recovery_depends_on()
    {
        var store = await CreateStoreAsync();

        await store.AddAsync(
            new SweepRecord
            {
                IdempotencyKey = "key-cc",
                StoreId = StoreId,
                DestinationAddress = "0x742d35Cc6634C0532925a3b844Bc454e4438f44e",
                DestinationMode = SweepDestinationMode.EvmAddress,
                DestinationKind = SweepDestinationKind.EvmAddress,
                DestinationChain = "arbitrum",
                DestinationAsset = "USDT",
                DestinationAssetDecimals = 6,
                Provider = SparkCrossChainProvider.Orchestra,
                ProviderQuoteId = "q_arbitrum_1",
                // The token path: the SDK rejected a key, so the quote id is the only handle on this send.
                IdempotencyKeyAccepted = false,
                SourceTokenIdentifier = StableBalanceSettings.DefaultTokenIdentifier,
                SourceAmountBaseUnits = "35600000",
                SourceTokenDecimals = 6,
                EstimatedOutBaseUnits = "35480000",
                ConversionStatus = SparkConversionStatus.Pending,
                Status = SweepRecordStatus.Pending,
                CreatedAt = Origin
            },
            Ct);

        var stored = await store.GetAsync(StoreId, "key-cc", Ct);

        Assert.NotNull(stored);
        Assert.True(stored!.IsCrossChain);
        Assert.False(stored.IdempotencyKeyAccepted);
        Assert.Equal("q_arbitrum_1", stored.ProviderQuoteId);
        Assert.Equal(SparkCrossChainProvider.Orchestra, stored.Provider);
        Assert.Equal("arbitrum", stored.DestinationChain);
        Assert.Equal("35600000", stored.SourceAmountBaseUnits);
        Assert.Equal(SparkConversionStatus.Pending, stored.ConversionStatus);

        // And the amount reads in its own unit rather than as a sats figure of zero.
        Assert.Equal("35.6", stored.DescribeAmount());
    }

    /// <summary>
    /// Resolving a cross-chain row records the provider's state, and a later blind poll cannot erase it.
    /// </summary>
    /// <remarks>
    /// The delivered amount arrives late and through no event whatsoever, so the sequence that matters is
    /// "learned it, then polled again and could not read it". A non-coalescing write would unset the only record
    /// of what actually arrived at the destination — the same defect the txid coalescing already guards against
    /// on the exit path, and worth pinning separately because these two columns are written by a different
    /// code path.
    /// </remarks>
    [Fact]
    public async Task A_delivered_amount_once_recorded_is_not_erased_by_a_later_poll()
    {
        var store = await CreateStoreAsync();

        await store.AddAsync(
            new SweepRecord
            {
                IdempotencyKey = "key-cc2",
                StoreId = StoreId,
                DestinationKind = SweepDestinationKind.EvmAddress,
                DestinationMode = SweepDestinationMode.EvmAddress,
                Status = SweepRecordStatus.Pending,
                CreatedAt = Origin
            },
            Ct);

        Assert.True(await store.TryResolveAsync(
            StoreId, "key-cc2", [SweepRecordStatus.Pending],
            new SweepResolution(
                SweepRecordStatus.Confirmed, null, null, null, Origin, SweepRefusalCode.None,
                SparkConversionStatus.Completed, "35480000", "order-7f3a"),
            Ct));

        // A second pass that learned nothing new — which is the normal case once a sweep has settled.
        Assert.True(await store.TryResolveAsync(
            StoreId, "key-cc2", [SweepRecordStatus.Confirmed],
            new SweepResolution(SweepRecordStatus.Confirmed, null, null, null, Origin),
            Ct));

        var stored = await store.GetAsync(StoreId, "key-cc2", Ct);
        Assert.Equal("35480000", stored!.DeliveredAmountBaseUnits);
        Assert.Equal(SparkConversionStatus.Completed, stored.ConversionStatus);
        // The bridge provider's order id is durable — it is the handle an investigation into a stuck delivery
        // quotes at the provider, and it used to live only on the in-memory copy the resolving pass held.
        Assert.Equal("order-7f3a", stored.ProviderOrderId);
    }

    #endregion

    #region Report listing

    [Fact]
    public async Task The_report_lists_this_stores_sends_in_the_range_oldest_first()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord("newest", status: SweepRecordStatus.Confirmed, minutesOld: 10), Ct);
        await store.AddAsync(NewRecord("oldest", status: SweepRecordStatus.Sent, minutesOld: 50), Ct);
        await store.AddAsync(NewRecord("middle", status: SweepRecordStatus.Failed, minutesOld: 30), Ct);
        await store.AddAsync(NewRecord("pending", minutesOld: 20), Ct);
        await store.AddAsync(NewRecord("before-range", status: SweepRecordStatus.Confirmed, minutesOld: 120), Ct);
        await store.AddAsync(NewRecord("other-store", OtherStoreId, SweepRecordStatus.Confirmed, minutesOld: 10), Ct);

        var listed = await store.ListForReportAsync(StoreId, Origin.AddMinutes(-60), Origin, Ct);

        Assert.Equal(["oldest", "middle", "pending", "newest"], listed.Select(r => r.IdempotencyKey));
    }

    [Fact]
    public async Task The_report_leaves_refusals_out()
    {
        // A store below its fee ceiling writes a refusal every pass; a report of what left the wallet is not the
        // place for them.
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRefusal("refused", SweepRefusalCode.FeeAboveLimit, minutesOld: 5), Ct);
        await store.AddAsync(NewRecord("sent", status: SweepRecordStatus.Sent, minutesOld: 5), Ct);

        var listed = await store.ListForReportAsync(StoreId, Origin.AddDays(-1), Origin, Ct);

        Assert.Equal(["sent"], listed.Select(r => r.IdempotencyKey));
    }

    [Fact]
    public async Task The_report_range_includes_both_ends()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord("at-from", status: SweepRecordStatus.Sent, minutesOld: 60), Ct);
        await store.AddAsync(NewRecord("at-to", status: SweepRecordStatus.Sent), Ct);

        var listed = await store.ListForReportAsync(StoreId, Origin.AddMinutes(-60), Origin, Ct);

        Assert.Equal(["at-from", "at-to"], listed.Select(r => r.IdempotencyKey));
    }

    [Fact]
    public async Task The_report_range_may_be_given_in_any_timezone()
    {
        // BTCPay passes the merchant's chosen timezone through; Npgsql refuses a non-UTC DateTimeOffset outright.
        var store = await CreateStoreAsync();
        await store.AddAsync(NewRecord("inside", status: SweepRecordStatus.Sent, minutesOld: 30), Ct);
        await store.AddAsync(NewRecord("outside", status: SweepRecordStatus.Sent, minutesOld: 90), Ct);
        var tokyo = TimeSpan.FromHours(9);

        var listed = await store.ListForReportAsync(
            StoreId, Origin.AddMinutes(-60).ToOffset(tokyo), Origin.ToOffset(tokyo), Ct);

        Assert.Equal(["inside"], listed.Select(r => r.IdempotencyKey));
    }

    #endregion
}

/// <summary>The contract against the in-memory implementation used by the engine tests.</summary>
public class InMemorySweepRecordStoreTests : SweepRecordStoreContractTests
{
    protected override Task<ISweepRecordStore> CreateStoreAsync() =>
        Task.FromResult<ISweepRecordStore>(new InMemorySweepRecordStore());
}

/// <summary>The same contract against the production EF store and a real Postgres database.</summary>
[Trait("Category", "Postgres")]
[Collection(PostgresTestDatabase.CollectionName)]
public class PostgresSweepRecordStoreTests : SweepRecordStoreContractTests
{
    private readonly PostgresTestDatabase _database;

    public PostgresSweepRecordStoreTests(PostgresTestDatabase database) => _database = database;

    protected override async Task<ISweepRecordStore> CreateStoreAsync() =>
        new EfSweepRecordStore(await _database.CreateFactoryAsync());
}
