using Breez.Sdk.Spark;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Models;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using Network = NBitcoin.Network;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The sweep engine: what it sends, what it refuses, and what it does after a crash.
/// </summary>
/// <remarks>
/// The fake SDK models the hazards rather than an idealised SDK — see <see cref="FakeSparkSdkClient"/> — so the
/// insufficient-funds, dust and expired-quote cases below arise from its behaviour rather than being stipulated by
/// the test.
/// </remarks>
public class SparkSweepEngineTests
{
    private const string StoreId = "store-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Origin = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Everything one engine and its collaborators. Public because a <c>TheoryData</c> row hands an arrangement
    /// callback over it to xunit.
    /// </summary>
    public sealed record Harness(
        SparkSweepEngine Engine,
        FakeSparkSdkClient Sdk,
        InMemorySweepRecordStore Records,
        FakeSweepAddressSource Addresses,
        FakeSparkStoreSettingsStore Settings,
        FakeSparkStoreRuntime Runtime,
        StubTimeProvider Time,
        WriteLog Log,
        CapturingLogger<SparkSweepEngine> Logger,
        FakeSweepTransactionLabeler Labeler);

    /// <param name="sweep">
    /// The store's sweep configuration. Defaults to enabled with the shipped defaults, since almost every test is
    /// about what happens once a merchant has opted in.
    /// </param>
    /// <param name="balanceSats">
    /// Defaults comfortably above the default threshold so the interesting paths are reachable without every test
    /// restating the arithmetic.
    /// </param>
    private static Harness CreateHarness(
        SweepSettings? sweep = null,
        long balanceSats = 500_000,
        bool walletRunning = true,
        Network? network = null,
        ISweepAddressSource? addressSource = null)
    {
        var log = new WriteLog();
        var sdk = new FakeSparkSdkClient(log) { BalanceSats = balanceSats };
        var records = new InMemorySweepRecordStore(log);
        var addresses = addressSource as FakeSweepAddressSource ?? new FakeSweepAddressSource();
        var settings = new FakeSparkStoreSettingsStore();
        var time = new StubTimeProvider(Origin);
        var logger = new CapturingLogger<SparkSweepEngine>();
        var labeler = new FakeSweepTransactionLabeler();

        settings.Settings[StoreId] = new SparkSettings
        {
            ProtectedMnemonic = "protected",
            PaymentKey = "key",
            Sweep = sweep ?? new SweepSettings { Enabled = true }
        };

        var runtime = new FakeSparkStoreRuntime();
        if (walletRunning)
            runtime.Clients[StoreId] = sdk;

        var resolver = new SweepDestinationResolver(
            addressSource ?? addresses,
            network ?? Network.RegTest,
            NullLogger<SweepDestinationResolver>.Instance);

        var engine = new SparkSweepEngine(
            settings, runtime, records, resolver, Routes(), Oracle(), labeler, time, logger);
        return new Harness(engine, sdk, records, addresses, settings, runtime, time, log, logger, labeler);
    }

    /// <summary>
    /// The route resolver, which every engine needs and which almost no test in this file exercises.
    /// </summary>
    /// <remarks>
    /// Real rather than faked, deliberately. It holds the provider-availability rules — filter to Orchestra,
    /// match the asset exactly — and a stub here would let the engine's cross-chain tests pass against rules
    /// nobody applied. Its own behaviour is covered in <c>CrossChainRouteResolverTests</c>.
    /// </remarks>
    private static CrossChainRouteResolver Routes() =>
        new(NullLogger<CrossChainRouteResolver>.Instance);

    /// <summary>
    /// The value oracle, which no cooperative-exit test exercises but every engine needs.
    /// </summary>
    /// <remarks>
    /// A price that is present and sane, so the cross-chain value guard is never what a test in this file
    /// fails on. Its own behaviour — including refusing when no rate is available — is covered in
    /// <c>SparkCrossChainSweepTests</c>.
    /// </remarks>
    private static ICrossChainValueOracle Oracle() => new FakeCrossChainValueOracle();

    #region The happy path

    [Fact]
    public async Task A_sweep_sends_the_balance_above_the_reserve_and_records_it()
    {
        var h = CreateHarness(new SweepSettings { Enabled = true, ReserveSats = 50_000 }, balanceSats: 500_000);

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Swept, result.Kind);

        var send = Assert.Single(h.Sdk.OnchainSendCalls);
        // balance - reserve, with the fee netted out of it by FeesIncluded.
        Assert.Equal(450_000, send.AmountSats);
        Assert.True(send.FeesIncluded);
        Assert.Equal(SparkOnchainSpeed.Medium, send.Speed);
        Assert.Equal(FakeSweepAddressSource.RegtestAddresses[0], send.Address);

        var record = h.Records.Single();
        Assert.NotNull(record);
        Assert.Equal(send.IdempotencyKey, record.IdempotencyKey);
        Assert.Equal(SweepRecordStatus.Sent, record.Status);
        Assert.Equal(450_000, record.AmountSats);
        Assert.Equal(2_190, record.QuotedFeeSats);
        Assert.Equal(2_190, record.FeeSats);
        Assert.Equal(447_810, record.RecipientAmountSats);
        Assert.Equal(500_000, record.BalanceAtDecisionSats);
        Assert.Equal(h.Sdk.NextOnchainTxId, record.TxId);
        Assert.Equal(SweepTrigger.Automatic, record.Trigger);
        Assert.Equal(Origin, record.CreatedAt);
        Assert.Null(record.Error);

        // FeesIncluded genuinely drains: the balance lands on exactly the reserve.
        Assert.Equal(50_000, h.Sdk.BalanceSats);
    }

    [Fact]
    public async Task A_sweep_labels_its_transaction_in_the_stores_wallet()
    {
        // The label is what tells a merchant reading their Bitcoin wallet where the incoming transaction came
        // from; without it a sweep is money that arrived with no explanation.
        var h = CreateHarness();

        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        var labeled = Assert.Single(h.Labeler.Labeled);
        Assert.Equal(StoreId, labeled.StoreId);
        Assert.Equal(h.Sdk.NextOnchainTxId, labeled.TxId);
        Assert.Equal(SweepDestinationKind.BitcoinAddress, labeled.Kind);
    }

    [Fact]
    public async Task A_pass_that_sweeps_nothing_labels_nothing()
    {
        var h = CreateHarness(balanceSats: 10_000);

        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Empty(h.Labeler.Labeled);
    }

    [Fact]
    public async Task A_crash_recovered_sweep_labels_its_transaction()
    {
        // The reconciliation path learns the txid from the SDK's payment rather than from the row, so the label
        // must land there too — a sweep whose send raced a crash is exactly the transaction a merchant will go
        // looking for.
        var h = CreateHarness(balanceSats: 10_000);
        const string key = "5d0a1cf1-2b3e-4b17-9c8a-7f0c2a0f9e21";
        await h.Records.AddAsync(NewPending(key), Ct);
        h.Sdk.Seed(CompletedExit(key, 450_000, 2_190));

        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        var labeled = Assert.Single(h.Labeler.Labeled);
        Assert.Equal("txid-recovered", labeled.TxId);
    }

    [Fact]
    public async Task The_record_is_persisted_with_its_key_before_the_send()
    {
        // The crash-safety primitive, asserted on a shared monotonic write log rather than on two independent
        // counters — which would pass just as happily with the two writes reversed.
        var h = CreateHarness();

        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        var key = Assert.Single(h.Sdk.OnchainSendCalls).IdempotencyKey;
        Assert.Equal(
            [
                // The balance is synced and read first, because a threshold decision on a stale balance is the wrong
                // sweep…
                "sdk:sync",
                "sdk:getinfo:synced",
                // …then the pre-flight quote…
                "sdk:quote",
                // …then the record exists, with its key, *before* anything can have been sent…
                $"sweep:add:{key}",
                // …and only then the send and its outcome.
                $"sdk:send:{key}",
                $"sweep:resolve:{key}:Sent"
            ],
            h.Log.Entries);
    }

    [Fact]
    public async Task The_idempotency_key_is_a_uuid()
    {
        // The SDK rejects anything else with a misleading "Invalid TransferId format", and it is what
        // GetPayment(key) is later asked for.
        var h = CreateHarness();

        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.True(Guid.TryParse(Assert.Single(h.Sdk.OnchainSendCalls).IdempotencyKey, out _));
    }

    [Fact]
    public async Task The_wallet_is_synced_before_the_balance_is_read()
    {
        // The balance lagged settlement by ~20 s in the funded run and stayed stale even through
        // GetInfo(ensureSynced: true); only an explicit sync moved it. So the ordering is the invariant, and a call
        // count cannot express it — swapping the two calls would leave SyncCount == 1 and the balance stale.
        var h = CreateHarness();

        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        var sync = h.Log.Entries.IndexOf("sdk:sync");
        var read = h.Log.Entries.IndexOf("sdk:getinfo:synced");
        Assert.True(sync >= 0 && read >= 0, $"expected both a sync and a synced read; got [{string.Join(", ", h.Log.Entries)}]");
        Assert.True(sync < read, "the wallet must be synced before its balance is read");
    }

    [Fact]
    public async Task A_completed_send_is_recorded_as_confirmed()
    {
        var h = CreateHarness();
        h.Sdk.NextOnchainSendStatus = SparkPaymentStatus.Completed;

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Swept, result.Kind);
        Assert.Equal(SweepRecordStatus.Confirmed, h.Records.Single()!.Status);
    }

    [Fact]
    public async Task Consecutive_sweeps_rotate_the_destination_address()
    {
        var h = CreateHarness();

        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);
        // The first sweep left the wallet on its reserve of zero, so top it back up and resolve the first record so
        // it stops blocking.
        h.Sdk.BalanceSats = 500_000;
        h.Sdk.NextOnchainSendStatus = SparkPaymentStatus.Completed;
        h.Time.Advance(TimeSpan.FromMinutes(1));
        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(2, h.Sdk.OnchainSendCalls.Count);
        Assert.Equal(FakeSweepAddressSource.RegtestAddresses[0], h.Sdk.OnchainSendCalls[0].Address);
        Assert.Equal(FakeSweepAddressSource.RegtestAddresses[1], h.Sdk.OnchainSendCalls[1].Address);
        Assert.NotEqual(h.Sdk.OnchainSendCalls[0].IdempotencyKey, h.Sdk.OnchainSendCalls[1].IdempotencyKey);
    }

    /// <summary>
    /// The amount in the sweep message is what the destination receives, under either fee policy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A mainnet sweep shipped understating itself by exactly one fee. The message netted the fee out of
    /// <c>Payment.amount</c> whenever <c>FeesIncluded</c> was set — but the SDK has already done that netting by the
    /// time it returns a <c>Payment</c>; only the <em>quote</em> echoes back the un-netted request. So the fee
    /// came off twice: tx
    /// <c>e9946fb8351db1e27bba015f3f3e099ad3de46e91678482222f5a238cb654bca</c> debited 62,000 sat at a 1,710 sat fee
    /// and paid the store's wallet 60,290, while the merchant was told 58,580.
    /// </para>
    /// <para>
    /// Nothing else was wrong — the record, the history table and the Greenfield response all derive the recipient
    /// amount from the quoted request and were right — which is exactly why it survived: no assertion anywhere
    /// looked at the sentence. This is that assertion, and it is stated as an identity against
    /// <c>RecipientAmountSats</c> rather than a literal, so it holds whatever the arithmetic upstream.
    /// </para>
    /// </remarks>
    [Theory]
    // FeesIncluded: the fee is netted out of the 450,000 asked for, so the destination gets 447,810. The bug
    // reported 445,620.
    [InlineData(true, 50_000L, 447_810L)]
    // FeesExcluded: the fee rides on top of the 490,000 asked for, so the destination gets all of it.
    [InlineData(false, 10_000L, 490_000L)]
    public async Task The_sweep_message_states_what_the_destination_receives(
        bool drain, long reserveSats, long expectedRecipientSats)
    {
        var h = CreateHarness(new SweepSettings
        {
            Enabled = true,
            DrainWhenSweeping = drain,
            ReserveSats = reserveSats
        });

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Swept, result.Kind);
        var record = h.Records.Single()!;
        Assert.Equal(expectedRecipientSats, record.RecipientAmountSats);
        Assert.Equal(
            $"Sweep of {expectedRecipientSats.ToString("N0", CultureInfo.InvariantCulture)} sat accepted for a 2,190 sat fee. It confirms on-chain shortly.",
            result.Reason);

        // The sentence and the row must never disagree: they are the same fact shown in two places, and the
        // merchant has no way to tell which one is lying.
        Assert.Contains($"{record.RecipientAmountSats.ToString("N0", CultureInfo.InvariantCulture)} sat", result.Reason);
    }

    /// <summary>
    /// The same, for a cooperative exit that came back already completed.
    /// </summary>
    /// <remarks>
    /// A separate branch with its own copy of the amount expression, and the funded run saw this status arrive on
    /// the send itself ~16–32 s later, so it is reachable.
    /// </remarks>
    [Fact]
    public async Task A_sweep_that_completes_immediately_also_states_what_the_destination_receives()
    {
        var h = CreateHarness(new SweepSettings { Enabled = true, ReserveSats = 50_000 });
        h.Sdk.NextOnchainSendStatus = SparkPaymentStatus.Completed;

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Swept, result.Kind);
        Assert.Equal(SweepRecordStatus.Confirmed, h.Records.Single()!.Status);
        Assert.Equal("Swept 447,810 sat on-chain for a 2,190 sat fee.", result.Reason);
    }

    [Fact]
    public async Task Fees_charged_on_top_come_out_of_the_reserve()
    {
        // The FeesExcluded policy. PrepareSendPayment does not check amount + fee <= balance, so the engine's own
        // arithmetic is what keeps the send from failing late.
        var h = CreateHarness(new SweepSettings
        {
            Enabled = true,
            DrainWhenSweeping = false,
            ReserveSats = 10_000
        });

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Swept, result.Kind);
        var send = Assert.Single(h.Sdk.OnchainSendCalls);
        Assert.False(send.FeesIncluded);
        Assert.Equal(490_000, send.AmountSats);
        // The destination receives the full amount; the fee came out of the reserve.
        Assert.Equal(490_000, h.Records.Single()!.RecipientAmountSats);
        Assert.Equal(10_000 - 2_190, h.Sdk.BalanceSats);
    }

    [Fact]
    public async Task The_configured_confirmation_speed_is_the_one_paid_for()
    {
        var h = CreateHarness(new SweepSettings
        {
            Enabled = true,
            ConfirmationSpeed = SweepConfirmationSpeed.Slow
        });

        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SparkOnchainSpeed.Slow, Assert.Single(h.Sdk.OnchainSendCalls).Speed);
        // 1,950 rather than the medium tier's 2,190. A numeric cast between the two enums would have bought Fast.
        Assert.Equal(1_950, h.Records.Single()!.FeeSats);
    }

    #endregion

    #region Skips and refusals

    [Fact]
    public async Task An_automatic_pass_does_nothing_when_sweeping_is_switched_off()
    {
        var h = CreateHarness(new SweepSettings { Enabled = false });

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Skipped, result.Kind);
        Assert.Empty(h.Sdk.OnchainSendCalls);
        Assert.Empty(h.Records.Records);
        // Not even a sync: a disabled store must cost nothing per pass beyond the in-flight walk.
        Assert.Equal(0, h.Sdk.SyncCount);
    }

    [Fact]
    public async Task An_automatic_pass_does_nothing_below_the_threshold()
    {
        var h = CreateHarness(
            new SweepSettings { Enabled = true, BalanceThresholdSats = 200_000 }, balanceSats: 150_000);

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Skipped, result.Kind);
        Assert.Contains("150,000 sat has not passed the 200,000 sat sweep threshold", result.Reason);
        Assert.Empty(h.Sdk.OnchainSendCalls);
        // A skip is normal and must not accumulate history rows.
        Assert.Empty(h.Records.Records);
    }

    [Fact]
    public async Task A_manual_sweep_ignores_the_switch_and_the_threshold()
    {
        var h = CreateHarness(
            new SweepSettings { Enabled = false, BalanceThresholdSats = 10_000_000 }, balanceSats: 500_000);

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Manual, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Swept, result.Kind);
        Assert.Equal(SweepTrigger.Manual, h.Records.Single()!.Trigger);
    }

    [Fact]
    public async Task A_manual_sweep_still_respects_the_economic_floor()
    {
        // The guards a merchant's button press does not answer. Pressing "sweep now" says "yes, look at it" — not
        // "yes, pay 40% in fees".
        var h = CreateHarness(
            new SweepSettings { Enabled = true, MinimumSweepSats = 100_000 }, balanceSats: 20_000);

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Manual, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Contains("below this store's 100,000 sat minimum", result.Reason);
        Assert.Empty(h.Sdk.OnchainSendCalls);
        Assert.Equal(SweepRecordStatus.Refused, h.Records.Single()!.Status);
    }

    [Fact]
    public async Task A_refusal_below_the_floor_does_not_reserve_an_address()
    {
        // Economics are checked before a destination is resolved, so a store that is not worth sweeping does not
        // burn an address from its wallet on every pass.
        var h = CreateHarness(
            new SweepSettings { Enabled = true, BalanceThresholdSats = 1, MinimumSweepSats = 100_000 },
            balanceSats: 20_000);

        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(0, h.Addresses.ReservedCount);
        Assert.Empty(h.Sdk.OnchainQuoteCalls);
    }

    [Fact]
    public async Task A_fee_above_the_percentage_guard_is_refused_before_anything_is_sent()
    {
        // 2,190 sats on 100,000 delivered is 2.19%, above a 1% ceiling.
        var h = CreateHarness(
            new SweepSettings
            {
                Enabled = true,
                BalanceThresholdSats = 1,
                MinimumSweepSats = Constants.MinimumOnchainSendSats,
                MaxFeePercent = 1.0
            },
            balanceSats: 100_000);

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Contains("2,190 sat exit fee is 2.24%", result.Reason);
        Assert.Empty(h.Sdk.OnchainSendCalls);

        var record = h.Records.Single();
        Assert.Equal(SweepRecordStatus.Refused, record!.Status);
        Assert.Equal(2_190, record.QuotedFeeSats);
        Assert.NotNull(record.Error);
    }

    [Fact]
    public async Task The_stricter_of_the_two_fee_guards_wins()
    {
        // 3% of 450,000 is 13,500, so the percentage alone would allow the 2,190 fee; the flat limit must bite.
        var h = CreateHarness(new SweepSettings
        {
            Enabled = true,
            MaxFeePercent = 3.0,
            MaxFeeFlatSats = 1_000
        });

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Contains("above the 1,000 sat limit", result.Reason);
        Assert.Empty(h.Sdk.OnchainSendCalls);
    }

    [Fact]
    public async Task A_fee_that_rises_between_the_quote_and_the_send_is_vetoed_at_the_send()
    {
        // The enforcement point. A coop-exit quote lives about a minute and the fee it names is not a promise, so the
        // guard that decides whether money moves has to run against the quote the send is committing to — not
        // against the earlier one a page or a pre-flight check saw. Verified by mutation: neutering only the
        // approval callback's guard fails this test and nothing else.
        var h = CreateHarness(new SweepSettings { Enabled = true, MaxFeePercent = 1.0 });
        h.Sdk.OnchainTiersAtSend = new SparkOnchainFeeQuote(
            "SparkCoopExitFeeQuote:later", Origin, 40_000, 50_000, 60_000);

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        // 2,190 on the pre-flight quote cleared the 1% ceiling on 447,810; 50,000 does not.
        Assert.Contains("50,000 sat exit fee", result.Reason);

        var record = h.Records.Single();
        Assert.Equal(SweepRecordStatus.Refused, record!.Status);
        // The record was written before the send, so the refusal is on it — and the quoted fee it recorded is the
        // pre-flight one, while the fee that was refused is the committed one.
        Assert.Equal(2_190, record.QuotedFeeSats);
        Assert.Equal(50_000, record.FeeSats);
        // Nothing moved.
        Assert.Equal(500_000, h.Sdk.BalanceSats);
        Assert.Null(record.TxId);
    }

    [Fact]
    public async Task A_send_vetoed_at_the_last_moment_does_not_block_the_next_pass()
    {
        // A refusal means nothing was sent, so the record is terminal rather than in flight — otherwise one
        // last-moment fee spike would stop the store sweeping until a human intervened.
        var h = CreateHarness(new SweepSettings { Enabled = true, MaxFeePercent = 1.0 });
        h.Sdk.OnchainTiersAtSend = new SparkOnchainFeeQuote("q", Origin, 40_000, 50_000, 60_000);
        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        h.Sdk.OnchainTiersAtSend = null;
        h.Time.Advance(TimeSpan.FromMinutes(2));
        var second = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Swept, second.Kind);
    }

    [Fact]
    public void Clearing_both_fee_guards_does_not_disable_the_guard()
    {
        // An unbounded fee on an automated money-moving path must not be reachable by emptying a field. With both
        // guards cleared the engine falls back to the default percentage, which is a real limit — so this fee is
        // refused rather than waved through.
        var settings = new SweepSettings { Enabled = true, MaxFeePercent = 0, MaxFeeFlatSats = null };
        var quote = new SparkOnchainQuote(
            10_000, 9_000, true, new SparkOnchainFeeQuote("q", Origin, 9_000, 9_000, 9_000));

        var refusal = SparkSweepEngine.ApproveQuote(settings, quote);

        Assert.NotNull(refusal);
        Assert.Equal(SweepRefusalCode.FeeAboveLimit, refusal.Code);
        // 3% of the 1,000 sats that would actually arrive is 30, so a 9,000 sat fee is refused.
        Assert.Contains("above the 30 sat limit", refusal.Message);
    }

    [Fact]
    public async Task Fees_charged_on_top_are_refused_when_the_reserve_cannot_cover_them()
    {
        // Refused rather than allowed to overdraw. PrepareSendPayment would happily quote it and the send would then
        // fail with a late "insufficient funds", after a record had been written.
        var h = CreateHarness(new SweepSettings
        {
            Enabled = true,
            DrainWhenSweeping = false,
            ReserveSats = 500
        });

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Contains("reserve is only 500 sat", result.Reason);
        Assert.Empty(h.Sdk.OnchainSendCalls);
    }

    [Fact]
    public async Task A_store_with_no_onchain_wallet_is_refused_rather_than_sent_anywhere()
    {
        var h = CreateHarness(addressSource: new FakeSweepAddressSource
        {
            Result = SweepAddressResult.NoWallet("This store has no Bitcoin wallet.")
        });

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Equal("This store has no Bitcoin wallet.", result.Reason);
        Assert.Empty(h.Sdk.OnchainQuoteCalls);
        Assert.Empty(h.Sdk.OnchainSendCalls);
        Assert.Equal(string.Empty, h.Records.Single()!.DestinationAddress);
    }

    [Fact]
    public async Task A_wallet_that_is_not_running_is_refused()
    {
        var h = CreateHarness(walletRunning: false);

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Manual, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Contains("Spark wallet is not running", result.Reason);
    }

    [Fact]
    public async Task A_settings_blob_whose_sweep_section_is_null_does_not_crash_a_pass()
    {
        // A real defect, not a hypothetical: the `= new()` initialiser on SparkSettings.Sweep covers a stored blob
        // with no "Sweep" key, but an explicit `"Sweep": null` — a hand edit, a restored backup, an older
        // serializer — deserialises to null, and dereferencing it threw a NullReferenceException out of a scheduler
        // pass. PreviewAsync always coalesced; the run path did not.
        var h = CreateHarness();
        h.Settings.Settings[StoreId]!.Sweep = null!;

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        // Sweeping defaults to off, so the pass declines rather than sweeping on defaults nobody chose.
        Assert.Equal(SweepOutcomeKind.Skipped, result.Kind);
        Assert.Empty(h.Sdk.OnchainSendCalls);
    }

    [Fact]
    public async Task A_preview_of_a_store_whose_sweep_section_is_null_does_not_crash()
    {
        var h = CreateHarness();
        h.Settings.Settings[StoreId]!.Sweep = null!;

        var preview = await h.Engine.PreviewAsync(StoreId, Ct);

        Assert.NotNull(preview.Settings);
    }

    [Fact]
    public async Task A_settings_blob_with_a_pre_Wave4_threshold_of_zero_uses_the_default()
    {
        // W4-m6. Wave 3 shipped BalanceThresholdSats with no initialiser, so every blob written before this wave
        // carries an explicit zero — and an explicit zero wins over a property initialiser on deserialize. Left
        // alone, such a store reads as "sweep at any balance".
        var h = CreateHarness(
            new SweepSettings { Enabled = true, BalanceThresholdSats = 0 }, balanceSats: 150_000);

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Skipped, result.Kind);
        Assert.Contains("200,000 sat sweep threshold", result.Reason);
        Assert.Empty(h.Sdk.OnchainSendCalls);
    }

    [Fact]
    public async Task An_unconfigured_store_is_skipped()
    {
        var h = CreateHarness();
        h.Settings.Settings.Remove(StoreId);

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Skipped, result.Kind);
        Assert.Empty(h.Records.Records);
    }

    [Fact]
    public async Task A_repeated_automatic_refusal_does_not_accumulate_history_rows()
    {
        // W4-M1, and the test is deliberately built on the *hardest* refusal to de-duplicate rather than the easiest.
        //
        // The fee-guard reason interpolates the balance, the sweepable amount, the fee and the percentage; and the
        // Spark balance drifts by a few sats around the SDK's background leaf optimisation, which is simulated here.
        // An earlier version of the de-duplication compared the rendered sentence, so consecutive refusals never
        // matched and a store parked on a refusal wrote ~720 rows a day forever with no cleanup path. It passed its
        // test only because that test used a refusal whose message happens to carry no varying numbers.
        //
        // This also matters more than it sounds: with mainnet broadcast fees an order of magnitude above the regtest
        // levels the defaults were calibrated against, a default-configured store sits permanently on this refusal.
        var h = CreateHarness(new SweepSettings { Enabled = true, MaxFeePercent = 0.1 });
        var messages = new List<string>();

        for (var pass = 0; pass < 30; pass++)
        {
            h.Time.Advance(TimeSpan.FromMinutes(2));
            // Leaf-optimisation drift: a few sats either way, every pass. This is what defeated a de-duplication
            // keyed on the rendered sentence.
            h.Sdk.BalanceSats = 500_000 + (pass % 7) - 3;
            messages.Add((await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct)).Reason);
        }

        var record = Assert.Single(h.Records.Records).Value;
        Assert.Equal(SweepRecordStatus.Refused, record.Status);
        Assert.Equal(SweepRefusalCode.FeeAboveLimit, record.RefusalCode);
        // The one row says how often and how recently, so it reads as ongoing rather than as one stale event.
        Assert.Equal(30, record.AttemptCount);
        Assert.Equal(h.Time.GetUtcNow(), record.LastSeenAt);
        Assert.Equal(h.Time.GetUtcNow(), record.LastActivityAt);

        // The messages really did vary — otherwise this test would not be exercising the hazard at all — and the row
        // carries the latest one rather than the first.
        Assert.True(messages.Distinct().Count() > 1, "the drifting balance should have varied the message");
        Assert.Equal(messages[^1], record.Error);
        Assert.NotEqual(messages[0], record.Error);
    }

    [Fact]
    public async Task A_refusal_whose_message_never_varies_is_also_de_duplicated()
    {
        // The easy case, kept as well: de-duplication must not have become dependent on the message varying either.
        var h = CreateHarness(addressSource: new FakeSweepAddressSource
        {
            Result = SweepAddressResult.NoWallet("This store has no Bitcoin wallet.")
        });

        for (var pass = 0; pass < 5; pass++)
        {
            h.Time.Advance(TimeSpan.FromMinutes(2));
            await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);
        }

        Assert.Equal(5, Assert.Single(h.Records.Records).Value.AttemptCount);
    }

    [Fact]
    public async Task An_intervening_row_does_not_restart_a_refusals_tally()
    {
        // Keyed on the reason rather than on "is the newest row this refusal?". A manual refusal or one successful
        // sweep in the middle would defeat the latter, and an ongoing condition does not stop being ongoing because
        // something else happened once.
        var h = CreateHarness(new SweepSettings { Enabled = true, MaxFeePercent = 0.1 });

        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);
        h.Time.Advance(TimeSpan.FromMinutes(2));
        // A manual attempt, which always files its own row.
        await h.Engine.RunAsync(StoreId, SweepTrigger.Manual, cancellationToken: Ct);
        h.Time.Advance(TimeSpan.FromMinutes(2));
        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(2, h.Records.Records.Count);
        var automatic = h.Records.Records.Values.Single(r => r.Trigger is SweepTrigger.Automatic);
        Assert.Equal(2, automatic.AttemptCount);
    }

    [Fact]
    public async Task A_refusal_that_stops_and_comes_back_much_later_is_a_new_episode()
    {
        // The bound on coalescing. One row per day per reason rather than one forever, so a condition that resolved
        // itself and recurred a week later is visible as two episodes instead of one endless tally.
        var h = CreateHarness(new SweepSettings { Enabled = true, MaxFeePercent = 0.1 });

        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);
        h.Time.Advance(SparkSweepEngine.RefusalCoalescingWindow + TimeSpan.FromMinutes(1));
        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(2, h.Records.Records.Count);
        Assert.All(h.Records.Records.Values, r => Assert.Equal(1, r.AttemptCount));
    }

    [Fact]
    public async Task A_refusal_for_a_different_reason_gets_its_own_row()
    {
        var h = CreateHarness(new SweepSettings { Enabled = true, MaxFeePercent = 0.1 });

        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);
        h.Time.Advance(TimeSpan.FromMinutes(2));
        h.Addresses.Result = SweepAddressResult.NoWallet("no wallet here");
        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(2, h.Records.Records.Count);
        Assert.Equal(
            [SweepRefusalCode.FeeAboveLimit, SweepRefusalCode.NoDestination],
            h.Records.Records.Values.OrderBy(r => r.CreatedAt).Select(r => r.RefusalCode));
    }

    [Fact]
    public async Task A_manual_refusal_is_always_recorded()
    {
        // A merchant who pressed the button wants to find the reason afterwards, even if the automatic pass has
        // already filed the same one.
        var h = CreateHarness(addressSource: new FakeSweepAddressSource
        {
            Result = SweepAddressResult.NoWallet("This store has no Bitcoin wallet.")
        });

        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        await h.Engine.RunAsync(StoreId, SweepTrigger.Manual, cancellationToken: Ct);

        Assert.Equal(2, h.Records.Records.Count);
        Assert.Contains(h.Records.Records.Values, r => r.Trigger is SweepTrigger.Manual);
    }

    #endregion

    #region SDK hazards

    [Fact]
    public async Task An_expired_quote_is_re_quoted_once_with_the_same_key()
    {
        // A normal condition, not a failure: a bitcoin-address prepare lives about a minute. The retry must reuse
        // the key — deduplication is keyed on it alone — so a send that did go through cannot be duplicated.
        var h = CreateHarness();
        h.Sdk.ExpireNextQuotes = 1;

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Swept, result.Kind);
        Assert.Equal(2, h.Sdk.OnchainSendCalls.Count);
        Assert.Single(h.Sdk.OnchainSendCalls.Select(c => c.IdempotencyKey).Distinct());
        // One record, not two: the retry is the same sweep.
        Assert.Single(h.Records.Records);
        Assert.Equal(SweepRecordStatus.Sent, h.Records.Single()!.Status);
    }

    [Fact]
    public async Task A_quote_that_keeps_expiring_is_not_retried_forever()
    {
        var h = CreateHarness();
        h.Sdk.ExpireNextQuotes = 5;

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        // Two attempts, then the outcome is left unknown for the next pass to resolve rather than hammered.
        Assert.Equal(SweepOutcomeKind.Unresolved, result.Kind);
        Assert.Equal(2, h.Sdk.OnchainSendCalls.Count);
        Assert.Equal(SweepRecordStatus.Pending, h.Records.Single()!.Status);
    }

    [Fact]
    public async Task Insufficient_funds_at_send_after_a_clean_quote_fails_cleanly()
    {
        // Arises from the fake rather than being stipulated: its quote does not check the balance, exactly as
        // PrepareSendPayment does not, so a balance that shrinks between the quote and the send produces the real
        // "insufficient funds" the service provider would. That is the shape of a receive that has not settled, or
        // of a concurrent spend.
        var h = CreateHarness(new SweepSettings { Enabled = true, DrainWhenSweeping = false, ReserveSats = 5_000 });
        h.Sdk.BalanceOnSend = 1_000;

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Failed, result.Kind);
        var record = h.Records.Single();
        Assert.Equal(SweepRecordStatus.Failed, record!.Status);
        Assert.Contains("insufficient funds", record.Error);
        // Failed, not left Pending: insufficient funds is decided before anything leaves the wallet, so the store is
        // free to try again next pass.
        Assert.NotNull(record.CompletedAt);
    }

    [Fact]
    public async Task A_dust_sized_sweep_is_refused_before_any_record_exists()
    {
        // 294 sats sweepable, with a 2,190 fee netted out of it, would deliver nothing at all.
        var h = CreateHarness(
            new SweepSettings
            {
                Enabled = true,
                BalanceThresholdSats = 1,
                MinimumSweepSats = Constants.MinimumOnchainSendSats,
                MaxFeePercent = 100
            },
            balanceSats: 294);

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Contains("below the 294 sat on-chain minimum", result.Reason);
        Assert.Empty(h.Sdk.OnchainSendCalls);
    }

    [Fact]
    public async Task The_SDKs_own_dust_rejection_is_unreachable_through_the_engine()
    {
        // The engine's floor is max(configured minimum, on-chain dust), so a below-dust amount is refused before the
        // SDK ever sees it — and a merchant gets a sentence about their balance rather than the SDK's
        // "Amount is below the minimum of 294 sats required for this address".
        var h = CreateHarness(
            new SweepSettings
            {
                Enabled = true,
                BalanceThresholdSats = 1,
                // Genuinely below the on-chain floor. Setting it equal made the assertion pass even with the
                // Math.Max against Constants.MinimumOnchainSendSats deleted, which proved nothing about the floor
                // being the plugin's rather than the merchant's.
                MinimumSweepSats = 1
            },
            balanceSats: 293);

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Empty(h.Sdk.OnchainQuoteCalls);
        Assert.Empty(h.Sdk.OnchainSendCalls);

        // And the fake would indeed have thrown, so the guard above is load-bearing rather than decorative.
        await Assert.ThrowsAsync<SdkException.InvalidInput>(() => h.Sdk.QuoteOnchainSendAsync(
            FakeSweepAddressSource.RegtestAddresses[0], 293, feesIncluded: true, Ct));
    }

    [Theory]
    [MemberData(nameof(FailuresThatProvablySentNothing))]
    public async Task A_failure_before_the_send_is_recorded_as_failed_and_does_not_block(
        string _,
        Exception failure)
    {
        // W4-m2. The client asserts before it sends — a destination the SDK resolved as something other than a
        // Bitcoin address, a fee policy it echoed back changed — and its argument guards throw on the way to the
        // send too. All of those provably sent nothing, so they must be a clean failure. Falling into the generic
        // unknown-outcome branch would leave the row Pending and block every sweep for this store for the whole
        // five-minute grace period, over a configuration problem.
        var h = CreateHarness();
        h.Sdk.FailOnchainSendWith = failure;

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Failed, result.Kind);
        var record = h.Records.Single();
        Assert.Equal(SweepRecordStatus.Failed, record!.Status);
        Assert.NotNull(record.CompletedAt);

        // And the store is free immediately: the next pass sweeps rather than waiting out the grace period.
        h.Sdk.FailOnchainSendWith = null;
        h.Sdk.BalanceSats = 500_000;
        h.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(
            SweepOutcomeKind.Swept,
            (await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct)).Kind);
    }

    public static TheoryData<string, Exception> FailuresThatProvablySentNothing() => new()
    {
        {
            "the SDK resolved the destination as something other than a Bitcoin address",
            new InvalidOperationException(
                "Spark resolved the sweep destination as SendPaymentMethod.SparkAddress rather than a Bitcoin "
                + "address; refusing to send.")
        },
        {
            "the SDK echoed back a different fee policy",
            new InvalidOperationException(
                "Spark quoted the sweep with fee policy FeesExcluded rather than the requested FeesIncluded; "
                + "refusing to send.")
        },
        {
            "an argument guard",
            new ArgumentException("idempotencyKey", "idempotencyKey")
        },
        {
            "the SDK's own local validation",
            new SdkException.InvalidInput("@v1=invalid input")
        }
    };

    [Fact]
    public async Task A_handle_disposed_mid_send_is_unknown_not_a_clean_failure()
    {
        // Audit finding SweepEngine F1. ObjectDisposedException derives from InvalidOperationException, so it
        // silently matched IsProvablyNotSent's list and a sweep whose SDK handle was disposed *mid-send* resolved
        // Failed — which unblocks the store to sweep the same balance again next pass, while the first send may
        // already have left the wallet. Disposal races a send on every reconfigure and shutdown.
        //
        // The bug was invisible because nothing in IsProvablyNotSent mentions disposal; only the base type does.
        // SendAsync's generic catch names "a disposed handle" as genuinely unknown, which is what this pins.
        var h = CreateHarness();
        h.Sdk.FailOnchainSendWith = new ObjectDisposedException(
            "SparkSdkClient", "The Spark wallet for store S has been shut down.");

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Unresolved, result.Kind);
        var record = h.Records.Single();
        Assert.Equal(SweepRecordStatus.Pending, record!.Status);
        Assert.Null(record.CompletedAt);

        // And the store stays blocked rather than re-sending: the money-safety half of the fix. Without it the
        // next pass happily sweeps the same balance a second time.
        h.Sdk.FailOnchainSendWith = null;
        h.Sdk.BalanceSats = 500_000;
        h.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.NotEqual(
            SweepOutcomeKind.Swept,
            (await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct)).Kind);
    }

    [Fact]
    public async Task An_unknown_failure_leaves_the_record_pending_for_the_next_pass()
    {
        // The whole reason the record is written first. A network failure means the send may or may not have
        // reached the service provider, and guessing either way is worse than asking.
        var h = CreateHarness();
        h.Sdk.FailOnchainSendWith = new SdkException.NetworkException("@v1=connection reset");

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Unresolved, result.Kind);
        var record = h.Records.Single();
        Assert.Equal(SweepRecordStatus.Pending, record!.Status);
        Assert.Null(record.CompletedAt);
    }

    #endregion

    #region Crash recovery

    [Fact]
    public async Task An_unresolved_sweep_that_did_happen_is_resolved_from_the_SDK_and_not_resent()
    {
        // The crash-recovery path. GetPayment(idempotencyKey) is definitive because the SDK adopts the key as its
        // own payment id. The balance is left below the threshold so this test observes the recovery alone.
        var h = CreateHarness(balanceSats: 10_000);
        const string key = "5d0a1cf1-2b3e-4b17-9c8a-7f0c2a0f9e11";
        await h.Records.AddAsync(NewPending(key), Ct);
        h.Sdk.Seed(CompletedExit(key, 450_000, 2_190));

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        // Nothing is re-sent: the record was resolved by asking, not by retrying.
        Assert.Empty(h.Sdk.OnchainSendCalls);
        Assert.Equal(SweepOutcomeKind.Skipped, result.Kind);
        Assert.Equal([key], h.Sdk.GetPaymentCalls);

        var record = await h.Records.GetAsync(StoreId, key, Ct);
        Assert.Equal(SweepRecordStatus.Confirmed, record!.Status);
        Assert.Equal(2_190, record.FeeSats);
        Assert.Equal("txid-recovered", record.TxId);
    }

    [Fact]
    public async Task An_unresolved_sweep_still_pending_at_the_provider_is_recorded_as_sent_with_its_txid()
    {
        var h = CreateHarness(balanceSats: 10_000);
        const string key = "5d0a1cf1-2b3e-4b17-9c8a-7f0c2a0f9e12";
        await h.Records.AddAsync(NewPending(key), Ct);
        h.Sdk.Seed(CompletedExit(key, 450_000, 2_190) with { Status = SparkPaymentStatus.Pending });

        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        var record = await h.Records.GetAsync(StoreId, key, Ct);
        Assert.Equal(SweepRecordStatus.Sent, record!.Status);
        Assert.Equal("txid-recovered", record.TxId);
        Assert.Empty(h.Sdk.OnchainSendCalls);
    }

    [Fact]
    public async Task A_sent_sweep_whose_confirmation_is_never_observed_stays_sent_and_is_never_resent()
    {
        // The fake never promotes a pending exit, which is the real hazard: nothing must interpret "we never saw it
        // complete" as "it did not happen". The later passes are given a fresh balance so they genuinely do sweep —
        // otherwise the "never under the original key" half of this test would be checking an empty sequence.
        var h = CreateHarness();
        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);
        var key = Assert.Single(h.Sdk.OnchainSendCalls).IdempotencyKey;

        for (var pass = 0; pass < 3; pass++)
        {
            h.Time.Advance(TimeSpan.FromMinutes(10));
            h.Sdk.BalanceSats = 500_000;
            await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);
        }

        var record = await h.Records.GetAsync(StoreId, key, Ct);
        Assert.Equal(SweepRecordStatus.Sent, record!.Status);
        Assert.NotNull(record.TxId);

        // A Sent exit no longer blocks — its funds have left the balance — so those three passes really did sweep.
        var later = h.Sdk.OnchainSendCalls.Skip(1).Select(c => c.IdempotencyKey).ToList();
        Assert.Equal(3, later.Count);
        Assert.DoesNotContain(key, later);
        // Four distinct keys for four distinct sweeps: nothing was replayed.
        Assert.Equal(4, h.Sdk.OnchainSendCalls.Select(c => c.IdempotencyKey).Distinct().Count());
    }

    [Fact]
    public async Task A_sent_sweep_does_not_block_the_next_pass()
    {
        // Stated directly rather than left as a side effect of another test: a Sent exit's funds have already left
        // the balance, so holding new sweeps behind it would strand every later receive.
        var h = CreateHarness();
        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);
        Assert.Equal(SweepRecordStatus.Sent, h.Records.Single()!.Status);

        h.Sdk.BalanceSats = 500_000;
        h.Time.Advance(TimeSpan.FromMinutes(2));
        var second = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Swept, second.Kind);
    }

    [Theory]
    [MemberData(nameof(PaymentsThatAreNotCooperativeExits))]
    public async Task A_payment_under_a_sweep_key_that_is_not_an_exit_is_never_recorded_against_it(
        string _,
        SparkPaymentDirection direction,
        SparkPaymentMethod method)
    {
        // W4-m4. The key is a UUID this plugin minted, so this should be impossible — but writing a receive's fee and
        // txid onto a sweep row would be a lie about where a merchant's money went, and the settlement reconciler
        // makes exactly this check in the mirror direction before crediting an invoice. The row must keep blocking
        // rather than adopt figures from the wrong payment.
        var h = CreateHarness(balanceSats: 10_000);
        const string key = "5d0a1cf1-2b3e-4b17-9c8a-7f0c2a0f9e30";
        await h.Records.AddAsync(NewPending(key), Ct);
        h.Sdk.Seed(CompletedExit(key, 1_234, 7) with { Direction = direction, Method = method });

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.InFlight, result.Kind);
        var record = await h.Records.GetAsync(StoreId, key, Ct);
        Assert.Equal(SweepRecordStatus.Pending, record!.Status);
        Assert.Null(record.FeeSats);
        Assert.Null(record.TxId);
        Assert.Contains("not what this sweep would have produced", h.Logger.AllText);
    }

    /// <summary>
    /// A Token payment is rejected against a cooperative-exit row and accepted against a cross-chain one.
    /// </summary>
    /// <remarks>
    /// The pair matters, and the theory above only has half of it. Wave 7 widened the shapes a sweep row may
    /// match — a cross-chain send appears as <c>Spark</c> or <c>Token</c>, because the SDK has no cross-chain
    /// payment method — and the obvious way to implement that is to widen the check for every row. That would
    /// silently reopen the case the theory pins: an outgoing token transfer adopted onto a cooperative-exit
    /// row, writing somebody else's amount and fee onto a merchant's sweep. So the widening has to be
    /// conditional on the row, and this is what says so in both directions.
    /// </remarks>
    [Fact]
    public async Task The_payment_shapes_a_row_accepts_depend_on_which_rail_it_used()
    {
        var h = CreateHarness(balanceSats: 10_000);

        const string exitKey = "5d0a1cf1-2b3e-4b17-9c8a-7f0c2a0f9e31";
        await h.Records.AddAsync(NewPending(exitKey), Ct);
        h.Sdk.Seed(CompletedExit(exitKey, 1_234, 7) with { Method = SparkPaymentMethod.Token });

        var blocked = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        // Rejected: a Token payment is not what a cooperative exit produces, so the row keeps blocking.
        Assert.Equal(SweepOutcomeKind.InFlight, blocked.Kind);
        Assert.Equal(SweepRecordStatus.Pending, (await h.Records.GetAsync(StoreId, exitKey, Ct))!.Status);

        // The same payment shape against a cross-chain row, which is exactly what one produces.
        var crossChainHarness = CreateHarness(balanceSats: 10_000);
        const string crossChainKey = "5d0a1cf1-2b3e-4b17-9c8a-7f0c2a0f9e32";
        var row = NewPending(crossChainKey);
        row.DestinationKind = SweepDestinationKind.EvmAddress;
        row.DestinationMode = SweepDestinationMode.EvmAddress;
        await crossChainHarness.Records.AddAsync(row, Ct);
        crossChainHarness.Sdk.Seed(CompletedExit(crossChainKey, 1_234, 7) with
        {
            Method = SparkPaymentMethod.Token,
            Conversion = new SparkConversionState(
                SparkCrossChainProvider.Orchestra, SparkConversionStatus.Completed, "q_1", "order-1", 35_600_000)
        });

        await crossChainHarness.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        var resolved = await crossChainHarness.Records.GetAsync(StoreId, crossChainKey, Ct);
        Assert.Equal(SweepRecordStatus.Confirmed, resolved!.Status);
        Assert.Equal(SparkConversionStatus.Completed, resolved.ConversionStatus);
        Assert.Equal("35600000", resolved.DeliveredAmountBaseUnits);
    }

    public static TheoryData<string, SparkPaymentDirection, SparkPaymentMethod>
        PaymentsThatAreNotCooperativeExits() => new()
    {
        { "an inbound Lightning receive", SparkPaymentDirection.Receive, SparkPaymentMethod.Lightning },
        { "an inbound on-chain deposit", SparkPaymentDirection.Receive, SparkPaymentMethod.Deposit },
        { "an outgoing Lightning payment", SparkPaymentDirection.Send, SparkPaymentMethod.Lightning },
        { "an outgoing token transfer", SparkPaymentDirection.Send, SparkPaymentMethod.Token }
    };

    [Fact]
    public async Task An_unresolved_sweep_blocks_new_sweeps_inside_the_grace_period()
    {
        var h = CreateHarness();
        const string key = "5d0a1cf1-2b3e-4b17-9c8a-7f0c2a0f9e13";
        await h.Records.AddAsync(NewPending(key), Ct);
        // The SDK knows nothing about it — the send may be in flight on an uncancellable call right now.

        h.Time.Advance(SparkSweepEngine.UnresolvedGrace - TimeSpan.FromSeconds(1));
        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.InFlight, result.Kind);
        Assert.Empty(h.Sdk.OnchainSendCalls);
        Assert.Equal(SweepRecordStatus.Pending, (await h.Records.GetAsync(StoreId, key, Ct))!.Status);
    }

    [Fact]
    public async Task An_unresolved_sweep_the_SDK_never_heard_of_is_written_off_after_the_grace_period()
    {
        var h = CreateHarness();
        const string key = "5d0a1cf1-2b3e-4b17-9c8a-7f0c2a0f9e14";
        await h.Records.AddAsync(NewPending(key), Ct);

        h.Time.Advance(SparkSweepEngine.UnresolvedGrace + TimeSpan.FromSeconds(1));
        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        var record = await h.Records.GetAsync(StoreId, key, Ct);
        Assert.Equal(SweepRecordStatus.Failed, record!.Status);
        // Worded as what is known rather than as a conclusion: that the SDK replays a service-provider-accepted
        // transfer into its own storage after a crash and reconnect is an assumption the spike lists as unverified.
        Assert.Contains("Spark has no record of this sweep", record.Error);
        Assert.DoesNotContain("was never sent", record.Error);
        // And it no longer blocks, so the same pass — or the next — is free to sweep with a fresh key.
        Assert.DoesNotContain(key, h.Sdk.OnchainSendCalls.Select(c => c.IdempotencyKey));
    }

    [Fact]
    public async Task A_replay_of_a_key_the_provider_already_honoured_returns_the_original_and_spends_nothing()
    {
        // What makes retrying with the same key safe, and therefore what makes the crash-recovery design work.
        var h = CreateHarness();
        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);
        var send = Assert.Single(h.Sdk.OnchainSendCalls);
        var balanceAfterFirst = h.Sdk.BalanceSats;

        var replay = await h.Sdk.SendToBitcoinAddressAsync(
            send.Address, send.AmountSats, send.Speed, send.FeesIncluded, send.IdempotencyKey, _ => null, Ct);

        Assert.Equal(send.IdempotencyKey, replay.Payment!.SdkPaymentId);
        Assert.Equal(balanceAfterFirst, h.Sdk.BalanceSats);
    }

    [Fact]
    public async Task A_write_off_is_refused_when_the_synced_balance_no_longer_holds_the_sweeps_amount()
    {
        // The row says 450k sat should still be in the wallet if nothing was sent; the synced balance says
        // 400k. "No payment under the key" plus a shortfall is the accepted-but-unrecorded shape — the one
        // case where closing the row and re-planning would send real money twice — so the store stays blocked.
        var h = CreateHarness(balanceSats: 400_000);
        const string key = "5d0a1cf1-2b3e-4b17-9c8a-7f0c2a0f9e16";
        await h.Records.AddAsync(NewPending(key), Ct);

        h.Time.Advance(SparkSweepEngine.UnresolvedGrace + TimeSpan.FromSeconds(1));
        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.InFlight, result.Kind);
        Assert.Equal(SweepRecordStatus.Pending, (await h.Records.GetAsync(StoreId, key, Ct))!.Status);
        Assert.Empty(h.Sdk.OnchainSendCalls);
    }

    [Fact]
    public async Task A_shortfall_blocked_write_off_escalates_after_an_hour_rather_than_wedging_the_store()
    {
        // The gate's premise — funds missing means the exit happened — is also produced by a payout or a
        // Stable Balance conversion landing near a sweep that genuinely never went out. Unbounded, that
        // coincidence would block sweeping forever with no operator escape; bounded, the row is written off
        // after an hour of synced re-checks, with a reason that says exactly what was observed.
        var h = CreateHarness(new SweepSettings
        {
            Enabled = true,
            BalanceThresholdSats = long.MaxValue / 4,
            MinimumSweepSats = long.MaxValue / 4
        }, balanceSats: 400_000);
        const string key = "5d0a1cf1-2b3e-4b17-9c8a-7f0c2a0f9e19";
        await h.Records.AddAsync(NewPending(key), Ct);

        // Inside the escalation window the shortfall blocks...
        h.Time.Advance(SparkSweepEngine.UnresolvedGrace + TimeSpan.FromSeconds(1));
        Assert.Equal(
            SweepOutcomeKind.InFlight, (await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct)).Kind);
        Assert.Equal(SweepRecordStatus.Pending, (await h.Records.GetAsync(StoreId, key, Ct))!.Status);

        // ...and past it, the row closes and sweeping is free again.
        h.Time.Advance(SparkSweepEngine.ShortfallWriteOffAge);
        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        var record = await h.Records.GetAsync(StoreId, key, Ct);
        Assert.Equal(SweepRecordStatus.Failed, record!.Status);
        Assert.Contains("held less than the sweep would have sent", record.Error);
        Assert.Contains("verify the wallet's payment history", record.Error);
        Assert.Empty(h.Sdk.OnchainSendCalls);
    }

    [Fact]
    public async Task A_payment_that_surfaces_only_after_an_explicit_sync_resolves_instead_of_being_written_off()
    {
        // The exact window the write-off used to misread: the SSP accepted the exit, the SDK's local storage
        // has not replayed it yet, and the pass's first lookup returns null. The forced sync surfaces it, and
        // the repeated lookup must resolve the row rather than declare it never sent. The threshold is
        // unreachable so the pass can only resolve — a send here could only be a recovery re-send.
        var h = CreateHarness(new SweepSettings
        {
            Enabled = true,
            BalanceThresholdSats = long.MaxValue / 4,
            MinimumSweepSats = long.MaxValue / 4
        });
        const string key = "5d0a1cf1-2b3e-4b17-9c8a-7f0c2a0f9e17";
        await h.Records.AddAsync(NewPending(key), Ct);
        h.Sdk.OnSync = sdk => sdk.PaymentsById[key] = CompletedExit(key, 450_000, 2_190);

        h.Time.Advance(SparkSweepEngine.UnresolvedGrace + TimeSpan.FromSeconds(1));
        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        var record = await h.Records.GetAsync(StoreId, key, Ct);
        Assert.NotEqual(SweepRecordStatus.Failed, record!.Status);
        Assert.Null(record.Error);
        // And crucially, nothing was re-sent under this row's mandate.
        Assert.Empty(h.Sdk.OnchainSendCalls);
    }

    [Fact]
    public async Task A_sync_that_fails_keeps_a_write_off_candidate_blocking()
    {
        // The write-off is the one decision that can unblock a re-sweep, so it may not run on storage the
        // pass could not freshen. Refusing to sweep is always the safe direction.
        var h = CreateHarness();
        const string key = "5d0a1cf1-2b3e-4b17-9c8a-7f0c2a0f9e18";
        await h.Records.AddAsync(NewPending(key), Ct);
        h.Sdk.OnSync = _ => throw new SdkException.NetworkException("@v1=offline");

        h.Time.Advance(SparkSweepEngine.UnresolvedGrace + TimeSpan.FromSeconds(1));
        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.InFlight, result.Kind);
        Assert.Equal(SweepRecordStatus.Pending, (await h.Records.GetAsync(StoreId, key, Ct))!.Status);
        Assert.Empty(h.Sdk.OnchainSendCalls);
    }

    [Fact]
    public async Task An_SDK_that_cannot_be_read_keeps_an_unresolved_sweep_blocking()
    {
        // Refusing to sweep is always the safe direction.
        var h = CreateHarness();
        const string key = "5d0a1cf1-2b3e-4b17-9c8a-7f0c2a0f9e15";
        await h.Records.AddAsync(NewPending(key), Ct);
        h.Sdk.FailWith = new SdkException.NetworkException("@v1=offline");
        h.Time.Advance(SparkSweepEngine.UnresolvedGrace + TimeSpan.FromMinutes(1));

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.InFlight, result.Kind);
        Assert.Equal(SweepRecordStatus.Pending, (await h.Records.GetAsync(StoreId, key, Ct))!.Status);
    }

    #endregion

    #region Re-entrancy

    [Fact]
    public async Task Overlapping_passes_for_one_store_produce_one_sweep()
    {
        // The scheduler makes no promise that a pass finishes before the next tick, and a merchant can press "sweep
        // now" during one. Two passes each reading the same balance would each send it.
        var h = CreateHarness();
        var gate = new TaskCompletionSource();
        var reached = new TaskCompletionSource();

        // A slow address source, so the second call arrives while the first is still inside the engine.
        var slow = new BlockingAddressSource(gate.Task, reached);
        var resolver = new SweepDestinationResolver(
            slow, Network.RegTest, NullLogger<SweepDestinationResolver>.Instance);
        var runtime = new FakeSparkStoreRuntime();
        runtime.Clients[StoreId] = h.Sdk;
        var engine = new SparkSweepEngine(
            h.Settings, runtime, h.Records, resolver, Routes(), Oracle(),
            new FakeSweepTransactionLabeler(), h.Time,
            NullLogger<SparkSweepEngine>.Instance);

        var first = engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);
        await reached.Task;

        // Bounded, so that a missing guard fails this test instead of hanging the whole run: without it the second
        // pass would queue behind the first on an address source that cannot return until the second pass has
        // already returned, and the suite would simply stop.
        var second = await engine
            .RunAsync(StoreId, SweepTrigger.Manual, cancellationToken: Ct)
            .WaitAsync(TimeSpan.FromSeconds(10), Ct);

        gate.SetResult();
        var firstResult = await first;

        Assert.Equal(SweepOutcomeKind.Swept, firstResult.Kind);
        Assert.Equal(SweepOutcomeKind.Skipped, second.Kind);
        Assert.Contains("already running", second.Reason);
        Assert.Single(h.Sdk.OnchainSendCalls);
    }

    [Fact]
    public async Task A_failed_pass_releases_the_store_for_the_next_one()
    {
        var h = CreateHarness();
        h.Records.FailAddWith = new InvalidOperationException("database down");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct));

        h.Records.FailAddWith = null;
        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Swept, result.Kind);
    }

    #endregion

    #region Previews

    [Fact]
    public async Task A_preview_quotes_without_sending_or_reserving()
    {
        var h = CreateHarness(new SweepSettings { Enabled = true, ReserveSats = 50_000 });

        var preview = await h.Engine.PreviewAsync(StoreId, Ct);

        Assert.True(preview.CanSweep);
        Assert.Null(preview.RefusalReason);
        Assert.Equal(500_000, preview.BalanceSats);
        Assert.Equal(450_000, preview.SweepableSats);
        Assert.Equal(450_000, preview.Quote!.AmountSats);
        Assert.Equal(2_190, preview.Quote.FeeSats);
        Assert.Equal(447_810, preview.Quote.RecipientAmountSats);
        Assert.Equal(1_950, preview.Quote.Tiers.SlowFeeSats);
        Assert.Equal(2_430, preview.Quote.Tiers.FastFeeSats);
        Assert.True(preview.Destination!.Rotates);

        Assert.Empty(h.Sdk.OnchainSendCalls);
        Assert.Equal(0, h.Addresses.ReservedCount);
        Assert.Empty(h.Records.Records);
    }

    [Fact]
    public async Task A_preview_reports_the_same_refusal_the_engine_would_give()
    {
        // So a merchant is never invited to press a button that is going to be declined.
        var h = CreateHarness(
            new SweepSettings { Enabled = true, MinimumSweepSats = 100_000 }, balanceSats: 20_000);

        var preview = await h.Engine.PreviewAsync(StoreId, Ct);
        var run = await h.Engine.RunAsync(StoreId, SweepTrigger.Manual, cancellationToken: Ct);

        Assert.False(preview.CanSweep);
        Assert.Equal(run.Reason, preview.RefusalReason);
    }

    [Fact]
    public async Task A_preview_syncs_before_reading_the_balance()
    {
        // Ordering again, for the same reason: a preview that showed a stale balance would offer a sweep of the
        // wrong size and the merchant would have no way to know.
        var h = CreateHarness();

        await h.Engine.PreviewAsync(StoreId, Ct);

        var sync = h.Log.Entries.IndexOf("sdk:sync");
        var read = h.Log.Entries.IndexOf("sdk:getinfo:synced");
        Assert.True(sync >= 0 && read >= 0);
        Assert.True(sync < read, "a preview must sync before it reads the balance");
    }

    #endregion

    #region Logging

    [Fact]
    public async Task Nothing_the_engine_logs_contains_seed_material()
    {
        // The engine never touches the mnemonic, and this is what keeps that true: with NullLogger everywhere the
        // claim would be unfalsifiable, and a line that printed the merchant's seed would pass every other test.
        const string mnemonic =
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
        var h = CreateHarness();
        // Both the raw phrase and a distinctive protected blob, so each assertion below has something real to find.
        // The previous version asserted the absence of the word "protected" after overwriting the only field that
        // contained it, which made that assertion unfalsifiable.
        h.Settings.Settings[StoreId]!.ProtectedMnemonic = $"CA-protected-blob::{mnemonic}";

        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);
        await h.Engine.PreviewAsync(StoreId, Ct);
        h.Settings.Settings[StoreId]!.Sweep.Enabled = false;
        await h.Engine.RunAsync(StoreId, SweepTrigger.Manual, cancellationToken: Ct);

        Assert.NotEmpty(h.Logger.Lines);
        Assert.DoesNotContain("abandon", h.Logger.AllText);
        Assert.DoesNotContain(mnemonic, h.Logger.AllText);
        // The protected blob is not seed material a reader can use, but it is still key-derived and has no business
        // in an operator's log.
        Assert.DoesNotContain("CA-protected-blob", h.Logger.AllText);
    }

    [Fact]
    public async Task A_refusal_logs_its_reason_and_its_code()
    {
        var h = CreateHarness(addressSource: new FakeSweepAddressSource
        {
            Result = SweepAddressResult.NoWallet("This store has no Bitcoin wallet.")
        });

        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Contains("refusing to sweep (NoDestination) — This store has no Bitcoin wallet.", h.Logger.AllText);
    }

    [Theory]
    [MemberData(nameof(EveryRefusalShape))]
    public async Task Every_refusal_shape_logs_a_reason_and_records_its_code(
        string _,
        SweepRefusalCode expected,
        Action<Harness> arrange)
    {
        // Broadened from a single refusal kind, because "every refusal path logs a reason and surfaces it" is a claim
        // about all of them — and each of these reaches a different branch of the engine.
        var h = CreateHarness(new SweepSettings { Enabled = true, BalanceThresholdSats = 1 });
        arrange(h);

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        Assert.Contains($"refusing to sweep ({expected})", h.Logger.AllText);

        var record = Assert.Single(h.Records.Records).Value;
        Assert.Equal(expected, record.RefusalCode);
        Assert.Equal(result.Reason, record.Error);
        Assert.Empty(h.Sdk.OnchainSendCalls);
    }

    public static TheoryData<string, SweepRefusalCode, Action<Harness>> EveryRefusalShape() => new()
    {
        {
            "wallet not running", SweepRefusalCode.WalletNotRunning,
            h => h.Runtime.Clients.Clear()
        },
        {
            "balance unreadable", SweepRefusalCode.BalanceUnreadable,
            h => h.Sdk.FailWith = new SdkException.NetworkException("@v1=offline")
        },
        {
            "nothing above the reserve", SweepRefusalCode.NothingAboveReserve,
            h => h.Settings.Settings[StoreId]!.Sweep.ReserveSats = 10_000_000
        },
        {
            "below the minimum sweep", SweepRefusalCode.BelowMinimumSweep,
            h => h.Sdk.BalanceSats = 1_000
        },
        {
            "no destination", SweepRefusalCode.NoDestination,
            h => h.Addresses.Result = SweepAddressResult.NoWallet("no wallet here")
        },
        {
            "insufficient funds at quote", SweepRefusalCode.InsufficientFunds,
            h => h.Sdk.FailQuoteWith =
                new SdkException.SparkException("@v1=Tree service error: insufficient funds")
        },
        {
            "quote failed", SweepRefusalCode.QuoteFailed,
            h => h.Sdk.FailQuoteWith = new SdkException.SparkException("@v1=service provider is unhappy")
        },
        {
            "below the dust floor", SweepRefusalCode.BelowDustFloor,
            h =>
            {
                h.Settings.Settings[StoreId]!.Sweep.MinimumSweepSats = 1;
                h.Settings.Settings[StoreId]!.Sweep.MaxFeePercent = 100;
                h.Sdk.BalanceSats = 500;
            }
        },
        {
            "the reserve cannot cover a fee charged on top", SweepRefusalCode.ReserveBelowFee,
            h =>
            {
                h.Settings.Settings[StoreId]!.Sweep.DrainWhenSweeping = false;
                h.Settings.Settings[StoreId]!.Sweep.ReserveSats = 500;
            }
        },
        {
            "fee above the ceiling", SweepRefusalCode.FeeAboveLimit,
            h => h.Settings.Settings[StoreId]!.Sweep.MaxFeePercent = 0.1
        }
    };

    [Fact]
    public async Task The_quote_failure_message_distinguishes_insufficient_funds_from_a_broken_provider()
    {
        // The split matters to a merchant: one means "your balance moved", the other means "Spark is unhappy".
        var funds = CreateHarness();
        funds.Sdk.FailQuoteWith = new SdkException.SparkException("@v1=Tree service error: insufficient funds");
        var fundsResult = await funds.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        var broken = CreateHarness();
        broken.Sdk.FailQuoteWith = new SdkException.SparkException("@v1=service provider is unhappy");
        var brokenResult = await broken.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Contains("insufficient funds", fundsResult.Reason);
        Assert.Contains("may have been spent", fundsResult.Reason);
        Assert.Contains("could not quote this sweep: service provider is unhappy", brokenResult.Reason);
        // Neither leaks the SDK's UniFFI prefix.
        Assert.DoesNotContain("@v1=", fundsResult.Reason);
        Assert.DoesNotContain("@v1=", brokenResult.Reason);
    }

    [Fact]
    public async Task A_preview_reports_a_quote_failure_the_same_way_the_run_does()
    {
        var h = CreateHarness();
        h.Sdk.FailQuoteWith = new SdkException.SparkException("@v1=service provider is unhappy");

        var preview = await h.Engine.PreviewAsync(StoreId, Ct);
        var run = await h.Engine.RunAsync(StoreId, SweepTrigger.Manual, cancellationToken: Ct);

        Assert.False(preview.CanSweep);
        Assert.Equal(run.Reason, preview.RefusalReason);
    }

    [Fact]
    public async Task A_balance_that_cannot_be_read_is_refused_rather_than_treated_as_zero()
    {
        var h = CreateHarness();
        h.Sdk.FailWith = new SdkException.NetworkException("@v1=offline");

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Manual, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Contains("balance could not be read", result.Reason);
        Assert.DoesNotContain("@v1=", result.Reason);
        Assert.Equal(SweepRefusalCode.BalanceUnreadable, h.Records.Single()!.RefusalCode);
        Assert.Empty(h.Sdk.OnchainSendCalls);
    }

    [Fact]
    public async Task A_preview_reports_a_balance_that_cannot_be_read()
    {
        var h = CreateHarness();
        h.Sdk.FailWith = new SdkException.NetworkException("@v1=offline");

        var preview = await h.Engine.PreviewAsync(StoreId, Ct);

        Assert.False(preview.CanSweep);
        Assert.Contains("balance could not be read", preview.RefusalReason);
        Assert.DoesNotContain("@v1=", preview.RefusalReason);
    }

    [Fact]
    public async Task A_manual_sweep_still_respects_the_fee_ceiling()
    {
        // The relaxation for a manual trigger covers Enabled and the threshold only. The fee ceiling is not a
        // question the button press answers, and this is the button a merchant actually clicks.
        var h = CreateHarness(new SweepSettings { Enabled = false, MaxFeePercent = 0.1 });

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Manual, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Equal(SweepRefusalCode.FeeAboveLimit, h.Records.Single()!.RefusalCode);
        Assert.Empty(h.Sdk.OnchainSendCalls);
    }

    [Fact]
    public async Task A_manual_sweep_still_respects_the_dust_floor()
    {
        var h = CreateHarness(
            new SweepSettings
            {
                Enabled = false,
                MinimumSweepSats = 1,
                MaxFeePercent = 100
            },
            balanceSats: 500);

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Manual, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Equal(SweepRefusalCode.BelowDustFloor, h.Records.Single()!.RefusalCode);
        Assert.Empty(h.Sdk.OnchainSendCalls);
    }

    #endregion

    #region Plan and guard units

    [Theory]
    // balance, reserve, minimum, expected amount (0 means refused)
    [InlineData(500_000, 0, 100_000, 500_000)]
    [InlineData(500_000, 50_000, 100_000, 450_000)]
    [InlineData(100_000, 0, 100_000, 100_000)]
    [InlineData(99_999, 0, 100_000, 0)]
    [InlineData(100_000, 50_000, 100_000, 0)]
    [InlineData(0, 0, 100_000, 0)]
    [InlineData(1_000, 2_000, 294, 0)]
    // A minimum below the on-chain dust floor is raised to it rather than honoured.
    [InlineData(200, 0, 1, 0)]
    public void PlanSweep_computes_the_sweepable_amount(
        long balance,
        long reserve,
        long minimum,
        long expectedAmount)
    {
        var plan = SparkSweepEngine.PlanSweep(
            new SweepSettings { ReserveSats = reserve, MinimumSweepSats = minimum }, balance);

        Assert.Equal(expectedAmount, plan.AmountSats);
        Assert.Equal(expectedAmount == 0, plan.Refusal is not null);
    }

    [Fact]
    public void PlanSweep_treats_a_negative_reserve_as_zero()
    {
        var plan = SparkSweepEngine.PlanSweep(
            new SweepSettings { ReserveSats = -1_000, MinimumSweepSats = 294 }, 10_000);

        Assert.Equal(10_000, plan.AmountSats);
    }

    [Fact]
    public void No_configuration_can_authorise_paying_more_than_the_hard_ceiling()
    {
        // W4-m5. Clearing the percentage and entering a very large flat ceiling used to leave the engine with
        // nothing to take a minimum against — a fee guard switched off through a form the plugin accepted. The
        // backstop now refuses regardless of what it was told.
        var settings = new SweepSettings { MaxFeePercent = 0, MaxFeeFlatSats = 10_000_000 };
        var quote = new SparkOnchainQuote(
            100_000, 60_000, true, new SparkOnchainFeeQuote("q", Origin, 60_000, 60_000, 60_000));

        var refusal = SparkSweepEngine.ApproveQuote(settings, quote);

        Assert.NotNull(refusal);
        // 50% of the 40,000 sats that would arrive is 20,000, so a 60,000 sat fee cannot be authorised.
        Assert.Contains("above the 20,000 sat limit", refusal.Message);
    }

    [Fact]
    public void The_form_refuses_a_flat_ceiling_above_the_smallest_sweep_allowed()
    {
        // The other half of W4-m5: the backstop is a last resort, and a configuration that only the backstop stands
        // between and an absurd fee should not be saveable in the first place.
        var input = new SweepSettingsInput
        {
            Enabled = true,
            MaxFeePercent = 0,
            MaxFeeFlatSats = 10_000_000,
            MinimumSweepSats = 100_000,
            BalanceThresholdSats = 200_000
        };

        var errors = input.Validate(Network.RegTest);

        Assert.Contains(errors, e => e.Field == nameof(SweepSettingsInput.MaxFeeFlatSats));
    }

    [Fact]
    public void ApproveQuote_refuses_a_negative_fee_outright()
    {
        // The shape a wrapped provider u64 takes. Every ceiling in this guard is a <=, so a negative fee would
        // pass all of them — including the 50% hard backstop no configuration can lift.
        var quote = new SparkOnchainQuote(
            100_000, -1, true, new SparkOnchainFeeQuote("q", Origin, -1, -1, -1));

        var refusal = SparkSweepEngine.ApproveQuote(new SweepSettings(), quote);

        Assert.NotNull(refusal);
        Assert.Contains("negative", refusal.Message);
    }

    [Fact]
    public void ApproveQuote_allows_a_fee_exactly_on_the_limit()
    {
        // 2% of 100,000 delivered is exactly 2,000.
        var quote = new SparkOnchainQuote(
            102_000, 2_000, true, new SparkOnchainFeeQuote("q", Origin, 2_000, 2_000, 2_000));

        Assert.Null(SparkSweepEngine.ApproveQuote(new SweepSettings { MaxFeePercent = 2.0 }, quote));
    }

    [Fact]
    public void A_fee_charged_on_top_exactly_equal_to_the_reserve_is_allowed()
    {
        // The guard is `fee > reserve`, so the boundary belongs in a test: a reserve that exactly covers the fee is
        // sufficient, and off-by-one here would refuse a perfectly good sweep or overdraw by one sat.
        var quote = new SparkOnchainQuote(
            100_000, 2_190, false, new SparkOnchainFeeQuote("q", Origin, 2_190, 2_190, 2_190));

        Assert.Null(SparkSweepEngine.ApproveQuote(
            new SweepSettings { ReserveSats = 2_190, MaxFeePercent = 5 }, quote));
        Assert.Equal(
            SweepRefusalCode.ReserveBelowFee,
            SparkSweepEngine.ApproveQuote(
                new SweepSettings { ReserveSats = 2_189, MaxFeePercent = 5 }, quote)!.Code);
    }

    [Fact]
    public void The_percentage_guard_applies_to_the_fee_on_top_policy_too()
    {
        // Every other quote in this suite is built with feesIncluded: true, so the non-drain branch of
        // RecipientAmountSats — which returns the gross amount rather than netting the fee — was never pinned
        // against the ceiling.
        var quote = new SparkOnchainQuote(
            100_000, 2_190, false, new SparkOnchainFeeQuote("q", Origin, 2_190, 2_190, 2_190));

        // 2,190 on the 100,000 delivered is 2.19%, so a 3% ceiling allows it and a 2% ceiling does not — and the
        // recipient amount used is the gross 100,000, not 97,810.
        Assert.Null(SparkSweepEngine.ApproveQuote(
            new SweepSettings { ReserveSats = 5_000, MaxFeePercent = 3.0 }, quote));

        var refused = SparkSweepEngine.ApproveQuote(
            new SweepSettings { ReserveSats = 5_000, MaxFeePercent = 2.0 }, quote);
        Assert.Equal(SweepRefusalCode.FeeAboveLimit, refused!.Code);
        Assert.Contains("100,000 sat this sweep would deliver", refused.Message);
    }

    [Fact]
    public void ApproveQuote_measures_the_fee_against_what_the_destination_receives()
    {
        // The number a merchant would compute themselves, and with the fee netted out it is the more conservative
        // of the two readings: 2,000 on a gross 100,000 is 2%, but on the 98,000 that actually arrives it is 2.04%.
        var quote = new SparkOnchainQuote(
            100_000, 2_000, true, new SparkOnchainFeeQuote("q", Origin, 2_000, 2_000, 2_000));

        var refusal = SparkSweepEngine.ApproveQuote(new SweepSettings { MaxFeePercent = 2.0 }, quote);

        Assert.NotNull(refusal);
        Assert.Contains("98,000 sat this sweep would deliver", refusal.Message);
    }

    [Fact]
    public void ToSdkSpeed_does_not_reorder_the_tiers()
    {
        // The two enums disagree numerically — the SDK's is Fast = 0, this plugin's is Slow = 0 — so a cast would
        // buy the most expensive tier for a merchant who asked for the cheapest.
        Assert.Equal(SparkOnchainSpeed.Slow, SparkSweepEngine.ToSdkSpeed(SweepConfirmationSpeed.Slow));
        Assert.Equal(SparkOnchainSpeed.Medium, SparkSweepEngine.ToSdkSpeed(SweepConfirmationSpeed.Medium));
        Assert.Equal(SparkOnchainSpeed.Fast, SparkSweepEngine.ToSdkSpeed(SweepConfirmationSpeed.Fast));
    }

    #endregion

    private static SweepRecord NewPending(string key) => new()
    {
        IdempotencyKey = key,
        StoreId = StoreId,
        DestinationAddress = FakeSweepAddressSource.RegtestAddresses[0],
        DestinationMode = SweepDestinationMode.StoreWallet,
        AmountSats = 450_000,
        FeesIncluded = true,
        ConfirmationSpeed = SweepConfirmationSpeed.Medium,
        QuotedFeeSats = 2_190,
        BalanceAtDecisionSats = 500_000,
        Trigger = SweepTrigger.Automatic,
        Status = SweepRecordStatus.Pending,
        CreatedAt = Origin
    };

    private static SparkPayment CompletedExit(string key, long amountSats, long feeSats) => new(
        key,
        SparkPaymentDirection.Send,
        SparkPaymentStatus.Completed,
        SparkPaymentMethod.Withdraw,
        amountSats,
        feeSats,
        Origin,
        PaymentHash: null,
        Bolt11: null,
        Preimage: null,
        Description: "on-chain withdrawal txid-recovered",
        TxId: "txid-recovered");

    /// <summary>
    /// An address source that blocks until released, so two passes can genuinely overlap.
    /// </summary>
    private sealed class BlockingAddressSource : ISweepAddressSource
    {
        private readonly Task _gate;
        private readonly TaskCompletionSource _reached;

        public BlockingAddressSource(Task gate, TaskCompletionSource reached)
        {
            _gate = gate;
            _reached = reached;
        }

        public async Task<SweepAddressResult> GetAddressAsync(
            string storeId,
            bool reserve,
            CancellationToken cancellationToken = default)
        {
            _reached.TrySetResult();
            await _gate;
            return SweepAddressResult.Available(FakeSweepAddressSource.RegtestAddresses[0]);
        }
    }
}
