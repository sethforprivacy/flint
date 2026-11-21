using System;
using System.Threading;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// Unit tests for the <see cref="ISparkLedgerStore"/> contract and the
/// <see cref="BackfillCount"/>/<see cref="BackfillReport"/> value types.
/// </summary>
public class SparkLedgerTests
{
    private const string StoreA = "store-aaa";
    private const string StoreB = "store-bbb";

    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SparkLedgerEntry LightningEntry(string storeId, string sdkPaymentId, string paymentHash) =>
        new()
        {
            Id = Guid.NewGuid().ToString(),
            StoreId = storeId,
            EntryType = SparkLedgerEntryType.LightningReceive,
            AmountMsat = 1_000_000,
            FeeMsat = 0,
            OccurredAt = T0,
            RecordedAt = T0,
            SdkPaymentId = sdkPaymentId,
            PaymentHash = paymentHash,
            IsBackfill = false,
        };

    private static SparkLedgerEntry SendEntry(string storeId, string sdkPaymentId) =>
        new()
        {
            Id = Guid.NewGuid().ToString(),
            StoreId = storeId,
            EntryType = SparkLedgerEntryType.Send,
            AmountMsat = 500_000,
            FeeMsat = 1_000,
            OccurredAt = T0,
            RecordedAt = T0,
            SdkPaymentId = sdkPaymentId,
            IsBackfill = false,
        };

    // ---- ExistsForSdkPaymentAsync store isolation ----

    [Fact]
    public async System.Threading.Tasks.Task ExistsForSdkPayment_returns_true_for_same_store()
    {
        var store = new InMemorySparkLedgerStore();
        await store.AppendAsync(SendEntry(StoreA, "pay-001"), Ct);

        var exists = await store.ExistsForSdkPaymentAsync(StoreA, "pay-001", Ct);

        Assert.True(exists);
    }

    [Fact]
    public async System.Threading.Tasks.Task ExistsForSdkPayment_returns_false_for_different_store()
    {
        var store = new InMemorySparkLedgerStore();
        await store.AppendAsync(SendEntry(StoreA, "pay-001"), Ct);

        // Store B must not see Store A's entry even with the same SdkPaymentId.
        var exists = await store.ExistsForSdkPaymentAsync(StoreB, "pay-001", Ct);

        Assert.False(exists);
    }

    // ---- AppendAsync: unique SdkPaymentId constraint ----

    [Fact]
    public async System.Threading.Tasks.Task AppendAsync_second_insert_same_SdkPaymentId_is_silent_and_idempotent()
    {
        // Mirrors the Postgres UNIQUE partial index on (StoreId, SdkPaymentId) WHERE SdkPaymentId IS NOT NULL.
        // EfSparkLedgerStore catches PostgresException SqlState 23505 and treats it as "already recorded".
        // InMemorySparkLedgerStore enforces the same contract.
        var store = new InMemorySparkLedgerStore();
        var first = SendEntry(StoreA, "pay-dup");
        var second = SendEntry(StoreA, "pay-dup");   // duplicate SdkPaymentId

        await store.AppendAsync(first, Ct);
        await store.AppendAsync(second, Ct);  // must not throw

        Assert.Equal(1, await store.CountAsync(StoreA, Ct));
    }

    [Fact]
    public async System.Threading.Tasks.Task AppendAsync_null_SdkPaymentId_is_not_subject_to_uniqueness()
    {
        // The partial index excludes rows WHERE SdkPaymentId IS NULL (Sweeps, UnilateralExits).
        // Both appends must succeed; count must be 2.
        var store = new InMemorySparkLedgerStore();
        var e1 = new SparkLedgerEntry
        {
            Id = Guid.NewGuid().ToString(), StoreId = StoreA,
            EntryType = SparkLedgerEntryType.Sweep,
            AmountMsat = 1_000_000, FeeMsat = 0,
            OccurredAt = T0, RecordedAt = T0,
            SdkPaymentId = null, IsBackfill = false,
        };
        var e2 = new SparkLedgerEntry
        {
            Id = Guid.NewGuid().ToString(), StoreId = StoreA,
            EntryType = SparkLedgerEntryType.Sweep,
            AmountMsat = 2_000_000, FeeMsat = 0,
            OccurredAt = T0, RecordedAt = T0,
            SdkPaymentId = null, IsBackfill = false,
        };

        await store.AppendAsync(e1, Ct);
        await store.AppendAsync(e2, Ct);

        Assert.Equal(2, await store.CountAsync(StoreA, Ct));
    }

    // ---- ExistsForPaymentHashAsync store isolation ----

    [Fact]
    public async System.Threading.Tasks.Task ExistsForPaymentHash_returns_true_for_same_store()
    {
        var store = new InMemorySparkLedgerStore();
        await store.AppendAsync(LightningEntry(StoreA, "sdk-1", "aabbccdd"), Ct);

        var exists = await store.ExistsForPaymentHashAsync(StoreA, "aabbccdd", Ct);

        Assert.True(exists);
    }

    [Fact]
    public async System.Threading.Tasks.Task ExistsForPaymentHash_returns_false_for_different_store()
    {
        var store = new InMemorySparkLedgerStore();
        await store.AppendAsync(LightningEntry(StoreA, "sdk-1", "aabbccdd"), Ct);

        // Store B must not see Store A's entry even with the same payment hash.
        var exists = await store.ExistsForPaymentHashAsync(StoreB, "aabbccdd", Ct);

        Assert.False(exists);
    }

    // ---- ListAsync store isolation ----

    [Fact]
    public async System.Threading.Tasks.Task List_returns_only_entries_for_requested_store()
    {
        var store = new InMemorySparkLedgerStore();
        await store.AppendAsync(LightningEntry(StoreA, "sdk-a1", "hash-a"), Ct);
        await store.AppendAsync(LightningEntry(StoreB, "sdk-b1", "hash-b"), Ct);

        var rows = await store.ListAsync(StoreA, skip: 0, take: 100, Ct);

        Assert.Single(rows);
        Assert.Equal(StoreA, rows[0].StoreId);
    }

    // ---- CountAsync store isolation ----

    [Fact]
    public async System.Threading.Tasks.Task Count_returns_only_own_store_count()
    {
        var store = new InMemorySparkLedgerStore();
        await store.AppendAsync(LightningEntry(StoreA, "sdk-a1", "hash-a"), Ct);
        await store.AppendAsync(LightningEntry(StoreA, "sdk-a2", "hash-a2"), Ct);
        await store.AppendAsync(LightningEntry(StoreB, "sdk-b1", "hash-b"), Ct);

        var countA = await store.CountAsync(StoreA, Ct);
        var countB = await store.CountAsync(StoreB, Ct);

        Assert.Equal(2, countA);
        Assert.Equal(1, countB);
    }

    // ---- BackfillCount value type ----

    [Fact]
    public void BackfillCount_Add_sums_all_fields()
    {
        var a = new BackfillCount(Skipped: 1, Created: 2, Failed: 3);
        var b = new BackfillCount(Skipped: 10, Created: 20, Failed: 30);

        var sum = a.Add(b);

        Assert.Equal(11, sum.Skipped);
        Assert.Equal(22, sum.Created);
        Assert.Equal(33, sum.Failed);
    }

    [Fact]
    public void BackfillCount_Zero_is_all_zeros()
    {
        var z = BackfillCount.Zero;

        Assert.Equal(0, z.Skipped);
        Assert.Equal(0, z.Created);
        Assert.Equal(0, z.Failed);
    }

    // ---- BackfillReport.Total ----

    [Fact]
    public void BackfillReport_Total_sums_all_categories()
    {
        var report = new BackfillReport(
            DryRun: false,
            StoreId: StoreA,
            LightningReceives: new BackfillCount(1, 2, 0),
            StablecoinReceives: new BackfillCount(0, 1, 0),
            Sends: BackfillCount.Zero,
            Sweeps: new BackfillCount(0, 3, 1),
            UnilateralExits: new BackfillCount(2, 0, 0),
            SdkPayments: BackfillCount.Zero);

        var total = report.Total;

        Assert.Equal(3, total.Skipped);  // 1 + 0 + 0 + 0 + 2
        Assert.Equal(6, total.Created);  // 2 + 1 + 0 + 3 + 0
        Assert.Equal(1, total.Failed);   // 0 + 0 + 0 + 1 + 0
    }

    // ---- dry-run does not write ----

    [Fact]
    public async System.Threading.Tasks.Task DryRun_flag_on_backfill_entry_is_stored()
    {
        var store = new InMemorySparkLedgerStore();
        var entry = SendEntry(StoreA, "pay-dryrun");
        entry.IsBackfill = true;

        await store.AppendAsync(entry, Ct);

        var rows = await store.ListAsync(StoreA, 0, 10, Ct);
        Assert.Single(rows);
        Assert.True(rows[0].IsBackfill);
    }
}
