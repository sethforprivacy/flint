using System.Numerics;
using Breez.Sdk.Spark;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Xunit;
using SdkPaymentStatus = Breez.Sdk.Spark.PaymentStatus;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The automatic pass: <see cref="SparkService.TakeDueExitStateBackupsAsync"/> over the real service,
/// the real file store and a fake SDK, with a clock the test moves.
/// </summary>
/// <remarks>
/// <para>
/// The cadence decisions themselves are unit-tested in <c>ExitStateBackupSchedulerTests</c>; what is
/// under test here is the collaboration — that the events actually ask for a refresh, that a due store
/// actually gets its file, that a failure actually leaves the previous file alone, and that a failed
/// store actually stops the pass from reaching the stores after it (it does not — that is the point).
/// The scheduler is real and the time is a stub, so the debounce is observed as "the pass immediately
/// after a deposit exports nothing; the pass two minutes and a margin after it does", which is the
/// property rather than a constant.
/// </para>
/// <para>
/// <b>The margin is deliberate.</b> <see cref="ExitStateBackupScheduler.RequestRefresh"/> stamps with
/// the real wall clock — it runs on the event path, where reading an injected clock is exactly what
/// that path must not do — while the pass reads the stub. The advance past the two-minute debounce
/// therefore carries half a minute of slack against the milliseconds of real time a test spends, so
/// wall-clock drift between the two clocks cannot decide a test that is about minutes.
/// </para>
/// <para>
/// Serialized with every other test that toggles the feature-gate environment variable, as the exit
/// tests are.
/// </para>
/// </remarks>
[Collection(UnilateralExitTestCollection.Name)]
public class SparkExitStateAutoBackupTests
{
    private const string StoreId = "store-auto-backup";

    /// <summary>
    /// The stub clock's start — at the real one, so the wall-clock stamps <c>RequestRefresh</c>
    /// records on the event path land in the same era the tests advance through.
    /// </summary>
    private static readonly DateTimeOffset Base = DateTimeOffset.UtcNow;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 60_000)]
    public async Task The_first_due_pass_exports_a_running_store_and_stores_it()
    {
        using var gate = FeatureGate();
        using var h = await StartedAsync(new StubTimeProvider(Base));

        await h.Service.TakeDueExitStateBackupsAsync(Ct);

        Assert.Equal("exit-state-blob", await h.ExitStateBackups.ReadAsync(StoreId, Ct));
        Assert.Single(h.Sdk.Clients[StoreId].ExitExportCalls);

        // The success line carries a length, never the content — this is the log an implementation
        // would be tempted to make "more helpful", so it is pinned to stay unhelpful.
        Assert.DoesNotContain("exit-state-blob", h.Log.AllText);
    }

    [Fact(Timeout = 60_000)]
    public async Task With_the_feature_off_the_whole_pass_is_inert()
    {
        // Gate deliberately absent: with the experiment off there is no exit feature for a backup to
        // serve, and touching a wallet's export path anyway is acting on a secret for nobody.
        using var h = await StartedAsync(new StubTimeProvider(Base));

        await h.Service.TakeDueExitStateBackupsAsync(Ct);

        Assert.Empty(h.Sdk.Clients[StoreId].ExitExportCalls);
        Assert.Null(await h.ExitStateBackups.ReadAsync(StoreId, Ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task An_unchanged_state_is_not_written_again()
    {
        var clock = new StubTimeProvider(Base);
        using var gate = FeatureGate();
        using var h = await StartedAsync(clock);

        await h.Service.TakeDueExitStateBackupsAsync(Ct);
        var firstTakenAt = await h.ExitStateBackups.TakenAtAsync(StoreId, Ct);
        Assert.NotNull(firstTakenAt);

        // A full safety-net interval: the second pass is genuinely due, and this is the state an
        // idle wallet produces forever — due, unchanged, and not worth a multi-megabyte rewrite.
        clock.Advance(TimeSpan.FromHours(1));
        await h.Service.TakeDueExitStateBackupsAsync(Ct);

        // The export did happen — that is how the pass learns the state is unchanged — but the file
        // did not move, and a TakenAt that moved would report a backup re-taken at a moment nothing
        // was learned about the wallet.
        Assert.Equal(2, h.Sdk.Clients[StoreId].ExitExportCalls.Count);
        Assert.Equal(firstTakenAt, await h.ExitStateBackups.TakenAtAsync(StoreId, Ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task A_changed_state_replaces_the_stored_backup()
    {
        var clock = new StubTimeProvider(Base);
        using var gate = FeatureGate();
        using var h = await StartedAsync(clock);

        await h.Service.TakeDueExitStateBackupsAsync(Ct);
        Assert.Equal("exit-state-blob", await h.ExitStateBackups.ReadAsync(StoreId, Ct));

        h.Sdk.Clients[StoreId].ExitStateToExport = "exit-state-blob-next";
        clock.Advance(TimeSpan.FromHours(1));
        await h.Service.TakeDueExitStateBackupsAsync(Ct);

        Assert.Equal("exit-state-blob-next", await h.ExitStateBackups.ReadAsync(StoreId, Ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task An_export_failure_leaves_the_previous_backup_intact_and_the_pass_does_not_throw()
    {
        var clock = new StubTimeProvider(Base);
        using var gate = FeatureGate();
        using var h = await StartedAsync(clock);

        await h.Service.TakeDueExitStateBackupsAsync(Ct);
        Assert.Equal("exit-state-blob", await h.ExitStateBackups.ReadAsync(StoreId, Ct));

        h.Sdk.Clients[StoreId].FailExportWith = new InvalidOperationException("export refused");
        clock.Advance(TimeSpan.FromHours(1));

        // This call is a scheduled task's whole body; BTCPay's launcher logs what escapes it, and the
        // plugin's rule is that nothing does.
        await h.Service.TakeDueExitStateBackupsAsync(Ct);

        Assert.Equal("exit-state-blob", await h.ExitStateBackups.ReadAsync(StoreId, Ct));
        Assert.Contains("retried", h.Log.AllText);
        Assert.DoesNotContain("exit-state-blob", h.Log.AllText);
    }

    [Fact(Timeout = 60_000)]
    public async Task One_failing_store_does_not_cost_the_stores_after_it_their_backup()
    {
        const string brokenStore = "store-broken";
        const string healthyStore = "store-healthy";

        var clock = new StubTimeProvider(Base);
        using var gate = FeatureGate();
        using var h = SparkServiceHarness.Create(timeProvider: clock);
        h.SeedStore(brokenStore, SparkServiceHarness.MnemonicFor(1));
        h.SeedStore(healthyStore, SparkServiceHarness.MnemonicFor(2));
        await h.Service.StartAsync(Ct);

        h.Sdk.Clients[brokenStore].FailExportWith = new InvalidOperationException("export refused");

        await h.Service.TakeDueExitStateBackupsAsync(Ct);

        // The pass walks every running store per call: a throw out of the first would have ended the
        // loop, and the second store's missing file would be the only evidence.
        Assert.Null(await h.ExitStateBackups.ReadAsync(brokenStore, Ct));
        Assert.Equal("exit-state-blob", await h.ExitStateBackups.ReadAsync(healthyStore, Ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task A_claimed_deposit_event_debounces_before_the_next_backup_is_taken()
    {
        var clock = new StubTimeProvider(Base);
        using var gate = FeatureGate();
        using var h = await StartedAsync(clock);

        // A first pass, so the pass that answers "not yet" below answers about the event's request
        // and not about a scheduler with no history at all.
        await h.Service.TakeDueExitStateBackupsAsync(Ct);
        Assert.Single(h.Sdk.Clients[StoreId].ExitExportCalls);

        Emit(h, StoreId, SparkEventKind.ClaimedDeposits, payment: null);
        await WaitFor(() => h.Log.AllText.Contains("Spark claimed an on-chain deposit"),
            "the claimed-deposit event was never consumed");

        // Requested, not yet due: the export the event earned lands after the debounce, not on the
        // event's heels.
        await h.Service.TakeDueExitStateBackupsAsync(Ct);
        Assert.Single(h.Sdk.Clients[StoreId].ExitExportCalls);

        clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(30));
        await h.Service.TakeDueExitStateBackupsAsync(Ct);
        Assert.Equal(2, h.Sdk.Clients[StoreId].ExitExportCalls.Count);
    }

    [Fact(Timeout = 60_000)]
    public async Task An_inbound_payment_event_requests_a_refresh_through_the_same_debounce()
    {
        var clock = new StubTimeProvider(Base);
        using var gate = FeatureGate();
        using var h = await StartedAsync(clock);

        await h.Service.TakeDueExitStateBackupsAsync(Ct);
        Assert.Single(h.Sdk.Clients[StoreId].ExitExportCalls);

        // An auto-claimed deposit as a payment event — the branch of the receive path that logs and
        // returns early, before any invoice wiring. The refresh is requested once, past the direction
        // filter, so this early-returning branch is covered by the same call site.
        Emit(h, StoreId, SparkEventKind.PaymentSucceeded, Deposit("dep-auto-1"));
        await WaitFor(() => h.Log.AllText.Contains("on-chain deposit"),
            "the deposit payment was never consumed");

        await h.Service.TakeDueExitStateBackupsAsync(Ct);
        Assert.Single(h.Sdk.Clients[StoreId].ExitExportCalls);

        clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(30));
        await h.Service.TakeDueExitStateBackupsAsync(Ct);
        Assert.Equal(2, h.Sdk.Clients[StoreId].ExitExportCalls.Count);
    }

    [Fact(Timeout = 60_000)]
    public async Task The_safety_net_takes_a_fresh_backup_although_no_event_ever_arrived()
    {
        var clock = new StubTimeProvider(Base);
        using var gate = FeatureGate();
        using var h = await StartedAsync(clock);

        await h.Service.TakeDueExitStateBackupsAsync(Ct);
        Assert.Single(h.Sdk.Clients[StoreId].ExitExportCalls);

        // Nothing was emitted between these two lines — the exact silence the net exists for, given
        // an event channel this codebase documents as unreliable in both directions.
        clock.Advance(TimeSpan.FromHours(1));
        await h.Service.TakeDueExitStateBackupsAsync(Ct);

        Assert.Equal(2, h.Sdk.Clients[StoreId].ExitExportCalls.Count);
    }

    [Fact(Timeout = 60_000)]
    public async Task An_empty_export_stores_nothing()
    {
        using var gate = FeatureGate();
        using var h = await StartedAsync(new StubTimeProvider(Base));
        h.Sdk.Clients[StoreId].ExitStateToExport = "   ";

        await h.Service.TakeDueExitStateBackupsAsync(Ct);

        // Not an empty file either: an empty export means the SDK had nothing to say, and a file full
        // of nothing imports as a corrupt one — which is worse than the honest "no backup".
        Assert.Null(await h.ExitStateBackups.ReadAsync(StoreId, Ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task After_a_restart_the_first_pass_learns_from_the_file_instead_of_rewriting_it()
    {
        var clock = new StubTimeProvider(Base);
        using var gate = FeatureGate();
        var first = await StartedAsync(clock);
        SparkServiceHarness? h = null;
        try
        {
            await first.Service.TakeDueExitStateBackupsAsync(Ct);
            var firstTakenAt = await first.ExitStateBackups.TakenAtAsync(StoreId, Ct);
            Assert.NotNull(firstTakenAt);

            h = first.Restart();
            await h.Service.StartAsync(Ct);

            // A restarted scheduler knows nothing about what is stored. The pass must seed from the
            // file — a first pass after every restart that rewrote every store's identical backup
            // would spend a multi-megabyte write per store to say "unchanged", on the one path
            // (boot) where the process is least able to spare it.
            await h.Service.TakeDueExitStateBackupsAsync(Ct);

            Assert.Single(h.Sdk.Clients[StoreId].ExitExportCalls);
            Assert.Equal(firstTakenAt, await h.ExitStateBackups.TakenAtAsync(StoreId, Ct));
            Assert.Equal("exit-state-blob", await h.ExitStateBackups.ReadAsync(StoreId, Ct));
        }
        finally
        {
            h?.Dispose();
            first.Dispose();
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Wiring
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// One configured store, started, on the harness's real file store over a temp data dir. The
    /// feature gate is the caller's, held for the whole test — the connect path's warm-up runs
    /// <c>RestoreExitStateAsync</c>, which is gated too, so a helper-scoped gate would be off again
    /// before the assertions ran.
    /// </summary>
    private static async Task<SparkServiceHarness> StartedAsync(TimeProvider clock)
    {
        var h = SparkServiceHarness.Create(timeProvider: clock);
        try
        {
            h.SeedStore(StoreId, SparkServiceHarness.MnemonicFor(1));
            await h.Service.StartAsync(CancellationToken.None);
            return h;
        }
        catch
        {
            h.Dispose();
            throw;
        }
    }

    private static void Emit(SparkServiceHarness h, string storeId, SparkEventKind kind, Payment? payment) =>
        Assert.True(
            h.Sdk.EventWriters[storeId].TryWrite(new SparkEventEnvelope(storeId, kind, payment)),
            "the event channel refused the envelope");

    /// <summary>An auto-claimed on-chain deposit: a Receive with no payment hash and a claim fee netted out.</summary>
    private static Payment Deposit(string id, long amount = 99_901, long fees = 99) =>
        new(
            id: id,
            paymentType: PaymentType.Receive,
            status: SdkPaymentStatus.Completed,
            amount: new BigInteger(amount),
            fees: new BigInteger(fees),
            timestamp: 1_785_847_217,
            method: PaymentMethod.Deposit,
            details: new PaymentDetails.Deposit("e2e11469", 1),
            conversionDetails: null!);

    private static async Task WaitFor(Func<bool> condition, string because)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
                Assert.Fail($"Timed out waiting for {because}");
            await Task.Delay(20, CancellationToken.None);
        }
    }

    private static IDisposable FeatureGate() => new EnvironmentSwitch("FLINT_EXPERIMENTAL_UNILATERAL_EXIT");

    private sealed class EnvironmentSwitch : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentSwitch(string name)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, "1");
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
