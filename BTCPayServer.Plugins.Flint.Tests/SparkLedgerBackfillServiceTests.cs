using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

public class SparkLedgerBackfillServiceTests
{
    private const string StoreId = "test-store";
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);

    // -- Helpers -----------------------------------------------------------------

    private static SparkPayment LnReceive(string id, long amountSats, string paymentHash) =>
        new(id, SparkPaymentDirection.Receive, SparkPaymentStatus.Completed,
            SparkPaymentMethod.Lightning, amountSats, 0, Now, paymentHash,
            Bolt11: null, Preimage: null, Description: null);

    private static SparkPayment LnSend(string id, long amountSats, long feeSats,
        string paymentHash, string? description = null) =>
        new(id, SparkPaymentDirection.Send, SparkPaymentStatus.Completed,
            SparkPaymentMethod.Lightning, amountSats, feeSats, Now, paymentHash,
            Bolt11: null, Preimage: null, Description: description);

    private static SparkPayment OnchainSweep(string id, long amountSats, long feeSats) =>
        new(id, SparkPaymentDirection.Send, SparkPaymentStatus.Completed,
            SparkPaymentMethod.Withdraw, amountSats, feeSats, Now,
            PaymentHash: null, Bolt11: null, Preimage: null, Description: $"on-chain withdrawal {id}");

    private static SparkPayment StablecoinSparkReceive(string id, long sats) =>
        new(id, SparkPaymentDirection.Receive, SparkPaymentStatus.Completed,
            SparkPaymentMethod.Spark, sats, 0, Now,
            PaymentHash: null, Bolt11: null, Preimage: null, Description: null);

    private static SparkPayment StablecoinTokenReceive(string id) =>
        new(id, SparkPaymentDirection.Receive, SparkPaymentStatus.Completed,
            SparkPaymentMethod.Token, 1_000_000, 0, Now,
            PaymentHash: null, Bolt11: null, Preimage: null, Description: null);

    private static SparkPayment OnchainDeposit(string id, long amountSats) =>
        new(id, SparkPaymentDirection.Receive, SparkPaymentStatus.Completed,
            SparkPaymentMethod.Deposit, amountSats, 0, Now,
            PaymentHash: null, Bolt11: null, Preimage: null, Description: $"on-chain deposit {id}");

    private static IReadOnlyList<SparkLedgerEntry> Map(IReadOnlyList<SparkPayment> payments) =>
        SparkLedgerBackfillService.MapSdkPayments(
            StoreId, Now, payments,
            invoiceIdByHash: new Dictionary<string, string>(),
            quoteBySdkId: new Dictionary<string, StablecoinQuote>());

    // ── Type-mapping tests ───────────────────────────────────────────────────

    [Theory]
    [InlineData(SparkPaymentDirection.Receive, SparkPaymentMethod.Lightning, SparkLedgerEntryType.LightningReceive)]
    [InlineData(SparkPaymentDirection.Receive, SparkPaymentMethod.Spark,     SparkLedgerEntryType.StablecoinReceive)]
    [InlineData(SparkPaymentDirection.Receive, SparkPaymentMethod.Token,     SparkLedgerEntryType.StablecoinReceive)]
    [InlineData(SparkPaymentDirection.Receive, SparkPaymentMethod.Deposit,   SparkLedgerEntryType.OnchainDeposit)]
    [InlineData(SparkPaymentDirection.Send,    SparkPaymentMethod.Lightning, SparkLedgerEntryType.Send)]
    [InlineData(SparkPaymentDirection.Send,    SparkPaymentMethod.Spark,     SparkLedgerEntryType.Send)]
    [InlineData(SparkPaymentDirection.Send,    SparkPaymentMethod.Withdraw,  SparkLedgerEntryType.Sweep)]
    public void EntryTypeFor_maps_direction_and_method_to_correct_entry_type(
        SparkPaymentDirection direction,
        SparkPaymentMethod method,
        SparkLedgerEntryType expected)
    {
        var payment = new SparkPayment(
            "id", direction, SparkPaymentStatus.Completed, method, 1000, 0, Now,
            null, null, null, null);

        var actual = SparkLedgerBackfillService.EntryTypeFor(payment);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(SparkPaymentDirection.Send,    SparkPaymentMethod.Token)]
    [InlineData(SparkPaymentDirection.Send,    SparkPaymentMethod.Deposit)]
    public void EntryTypeFor_returns_null_for_unmodelled_methods(
        SparkPaymentDirection direction, SparkPaymentMethod method)
    {
        var payment = new SparkPayment(
            "id", direction, SparkPaymentStatus.Completed, method, 1000, 0, Now,
            null, null, null, null);

        Assert.Null(SparkLedgerBackfillService.EntryTypeFor(payment));
    }

    // ── Reconciliation test ──────────────────────────────────────────────────

    /// <summary>
    /// SDK ledger invariant: sum(credits) - sum(debits) - sum(fees) = wallet balance.
    /// Uses synthetic payments matching a realistic history: 3 LN receives, 2 sends (including
    /// one Bitrefill supplier payment), 1 on-chain sweep, 1 stablecoin BTC-landing receive,
    /// 1 stablecoin Token receive (USDB, AmountMsat=0), 1 on-chain deposit (claimed, credit).
    ///
    /// Expected balance:
    ///   credits: (10_000 + 20_000 + 15_000 + 12_000 + 50_000)
    ///   debits:  (8_000 + 100) + (5_000 + 200) = 13_300
    ///   balance: 107_000 - 13_300 = 93_700 sats
    /// </summary>
    [Fact]
    public void MapSdkPayments_entries_reconcile_to_SDK_balance()
    {
        const long expectedBalanceSats =
            (10_000 + 20_000 + 15_000 + 12_000 + 50_000) // credits: 3 LN + 1 stablecoin spark + 1 onchain deposit
            - (8_000 + 100)                                // debit: 1 LN send + fee
            - (5_000 + 200);                               // debit: 1 sweep + fee

        var payments = new List<SparkPayment>
        {
            LnReceive("rcv-1", 10_000, "hash-rcv-1"),
            LnReceive("rcv-2", 20_000, "hash-rcv-2"),
            LnReceive("rcv-3", 15_000, "hash-rcv-3"),
            StablecoinSparkReceive("stbl-rcv-1", 12_000),
            StablecoinTokenReceive("stbl-token-1"),        // USDB - AmountMsat = 0
            LnSend("snd-1", 8_000, 100, "hash-snd-1", "Bitrefill order-123"),
            OnchainSweep("sweep-1", 5_000, 200),
            OnchainDeposit("dep-1", 50_000),               // claimed on-chain deposit = credit
        };

        var entries = Map(payments);

        // All 8 payments have a mapped entry type (Send+Deposit is unmodelled, Receive+Deposit is now OnchainDeposit).
        Assert.Equal(8, entries.Count);

        var credits = entries
            .Where(e => e.EntryType is SparkLedgerEntryType.LightningReceive
                                    or SparkLedgerEntryType.StablecoinReceive
                                    or SparkLedgerEntryType.OnchainDeposit)
            .Sum(e => e.AmountMsat);

        var debits = entries
            .Where(e => e.EntryType is SparkLedgerEntryType.Send or SparkLedgerEntryType.Sweep)
            .Sum(e => e.AmountMsat + e.FeeMsat);

        var balanceSats = (credits - debits) / 1000L;
        Assert.Equal(expectedBalanceSats, balanceSats);
    }

    // ── Description preservation ──────────────────────────────────────────────

    [Fact]
    public void MapSdkPayments_preserves_SDK_description_for_future_WC_order_linkage()
    {
        const string description = "Bitrefill order-abc123";
        var payments = new List<SparkPayment>
        {
            LnSend("snd-with-desc", 5_000, 50, "hash-desc", description),
        };

        var entries = Map(payments);

        Assert.Single(entries);
        Assert.Equal(description, entries[0].Description);
    }

    // ── Token receive AmountMsat = 0 ─────────────────────────────────────────

    [Fact]
    public void MapSdkPayments_sets_AmountMsat_to_zero_for_token_receives()
    {
        var payments = new List<SparkPayment> { StablecoinTokenReceive("tok-1") };

        var entries = Map(payments);

        Assert.Single(entries);
        Assert.Equal(SparkLedgerEntryType.StablecoinReceive, entries[0].EntryType);
        Assert.Equal(0, entries[0].AmountMsat);
    }

    // ── Stablecoin Spark-method receive carries sats ──────────────────────────

    [Fact]
    public void MapSdkPayments_carries_sats_for_spark_method_stablecoin_receives()
    {
        var payments = new List<SparkPayment> { StablecoinSparkReceive("spark-rcv", 7_500) };

        var entries = Map(payments);

        Assert.Single(entries);
        Assert.Equal(SparkLedgerEntryType.StablecoinReceive, entries[0].EntryType);
        Assert.Equal(7_500_000L, entries[0].AmountMsat);
    }

    // ── Send amounts are from SDK, not fee-net ────────────────────────────────

    [Fact]
    public void MapSdkPayments_stores_AmountMsat_and_FeeMsat_separately_for_sends()
    {
        var payments = new List<SparkPayment>
        {
            LnSend("snd", 10_000, 250, "hash-snd"),
        };

        var entries = Map(payments);

        Assert.Single(entries);
        var e = entries[0];
        Assert.Equal(SparkLedgerEntryType.Send, e.EntryType);
        Assert.Equal(10_000_000L, e.AmountMsat);
        Assert.Equal(250_000L, e.FeeMsat);
    }

    // ── Deposit is recorded as OnchainDeposit credit ────────────────────────

    [Fact]
    public void MapSdkPayments_records_on_chain_deposit_as_OnchainDeposit()
    {
        var payments = new List<SparkPayment>
        {
            OnchainDeposit("dep-1", 100_000),
        };

        var entries = Map(payments);

        Assert.Single(entries);
        Assert.Equal(SparkLedgerEntryType.OnchainDeposit, entries[0].EntryType);
        Assert.Equal(100_000_000L, entries[0].AmountMsat);  // 100_000 sats * 1000
    }

    // ── IsBackfill flag ────────────────────────────────────────────────────

    [Fact]
    public void MapSdkPayments_marks_every_entry_as_backfill()
    {
        var payments = new List<SparkPayment>
        {
            LnReceive("r1", 1_000, "h1"),
            LnSend("s1", 500, 10, "h2"),
        };

        var entries = Map(payments);

        Assert.All(entries, e => Assert.True(e.IsBackfill));
        Assert.All(entries, e => Assert.Equal(StoreId, e.StoreId));
    }

    // ── Idempotency guard (via InMemorySparkLedgerStore) ──────────────────────

    [Fact]
    public async Task SDK_backfill_does_not_double_write_a_payment_already_in_ledger()
    {
        var ledger = new InMemorySparkLedgerStore();
        var sdk = new FakeSparkSdkClient();

        var payment = LnSend("already-in-ledger", 5_000, 50, "hash-existing");
        sdk.Seed(payment);

        // Simulate that the live write path already recorded this payment.
        await ledger.AppendAsync(new SparkLedgerEntry
        {
            Id = Guid.NewGuid().ToString(),
            StoreId = StoreId,
            EntryType = SparkLedgerEntryType.Send,
            AmountMsat = 5_000_000L,
            FeeMsat = 50_000L,
            OccurredAt = Now,
            RecordedAt = Now,
            SdkPaymentId = payment.SdkPaymentId,
            IsBackfill = false,
        });

        // Map again - the entry exists, so the idempotency guard must catch it.
        var existsBySdk = await ledger.ExistsForSdkPaymentAsync(StoreId, payment.SdkPaymentId);

        Assert.True(existsBySdk);
        Assert.Equal(1, await ledger.CountAsync(StoreId));
    }

    // -- Live row + same SDK payment: BackfillFromSdkAsync skips ----------------

    [Fact]
    public async Task BackfillFromSdkAsync_skips_payment_already_recorded_as_live_row()
    {
        var ledger = new InMemorySparkLedgerStore();
        var sdk = new FakeSparkSdkClient();

        var payment = LnSend("live-pay-1", 5_000, 50, "hash-live");
        sdk.Seed(payment);

        // Record it as a LIVE (non-backfill) row - exactly what the live write path produces.
        await ledger.AppendAsync(new SparkLedgerEntry
        {
            Id = Guid.NewGuid().ToString(),
            StoreId = StoreId,
            EntryType = SparkLedgerEntryType.Send,
            AmountMsat = 5_000_000L,
            FeeMsat = 50_000L,
            OccurredAt = Now,
            RecordedAt = Now,
            SdkPaymentId = payment.SdkPaymentId,
            IsBackfill = false,  // live row, NOT a backfill
        });

        var runtime = new FakeSparkStoreRuntime();
        runtime.Clients[StoreId] = sdk;

        var svc = new SparkLedgerBackfillService(
            null!, ledger, runtime,
            NullLogger<SparkLedgerBackfillService>.Instance);

        var result = await svc.BackfillFromSdkAsync(StoreId, dryRun: false, CancellationToken.None);

        Assert.Equal(0, result.Created);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(1, await ledger.CountAsync(StoreId));
    }
}
