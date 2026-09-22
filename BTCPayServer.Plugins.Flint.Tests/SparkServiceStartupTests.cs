using System.Diagnostics;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// <c>SparkService.StartAsync</c> must always finish, whatever a store's wallet does.
/// </summary>
/// <remarks>
/// <para>
/// The host gives it no help. <c>Program.Main</c> reaches this through
/// <c>IHost.StartAsync</c> → <c>IEnumerable&lt;IHostedService&gt;</c>, and
/// <c>HostOptions.StartupTimeout</c> is infinite by default — so anything <c>StartAsync</c> awaits without a
/// deadline of its own can hang BTCPay's startup permanently, with no exception, no log line, and therefore no
/// auto-disable of the plugin. That is not hypothetical here: PR #6 shipped exactly that failure through a
/// different route, and the SDK connect was for a long time the remaining un-guarded instance of the same
/// class — bounded only by <c>Connect</c> happening to do no network I/O, which is an SDK property and not a
/// guarantee this plugin holds.
/// </para>
/// <para>
/// So these tests run <c>StartAsync</c> on its own background thread and <c>Join</c> a timeout, the same shape
/// <c>SparkPluginStartupTests</c> uses and for the same reason: a regression must fail the test rather than
/// hang the test run forever. The hanging connect is modelled as a task that never completes and ignores
/// cancellation, because no SDK call can be cancelled — see <see cref="FakeSparkSdkClientFactory"/>.
/// </para>
/// </remarks>
[Collection(UnilateralExitTestCollection.Name)]
public class SparkServiceStartupTests
{
    private const string Gate = "FLINT_EXPERIMENTAL_UNILATERAL_EXIT";

    /// <summary>
    /// How long startup may take before it is treated as hung.
    /// </summary>
    /// <remarks>
    /// Two orders of magnitude above the harness's 250 ms connect deadline, so a loaded machine cannot make
    /// this flaky. The failure it guards against never completes at any timeout.
    /// </remarks>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private const string HangingStore = "store-that-hangs";
    private const string HealthyStore = "store-that-works";
    private const string ThrowingStore = "store-that-throws";
    private const string NeverAdoptedStore = "store-never-adopted";

    [Fact]
    public void A_store_whose_SDK_connect_never_returns_does_not_hold_up_the_host()
    {
        using var h = SparkServiceHarness.Create();
        h.SeedStore(HangingStore, SparkServiceHarness.MnemonicFor(1));
        h.Sdk.HangFor.Add(HangingStore);

        var elapsed = StartWithinTimeout(h);

        // The whole point. Without the deadline this never returns and BTCPay never starts.
        Assert.True(
            elapsed < StartTimeout,
            $"StartAsync took {elapsed.TotalSeconds:0.0}s against a 250 ms connect deadline");

        // And the operator is told which store, and what it costs them — the silent version of this failure is
        // what made PR #6 so expensive to diagnose.
        Assert.Contains(HangingStore, h.Log.AllText);
        Assert.Contains("exceeded", h.Log.AllText);
    }

    [Fact]
    public async Task A_hanging_store_leaves_its_own_wallet_not_running_and_says_so()
    {
        using var h = SparkServiceHarness.Create();
        h.SeedStore(HangingStore, SparkServiceHarness.MnemonicFor(1));
        h.Sdk.HangFor.Add(HangingStore);

        StartWithinTimeout(h);

        // Not running, so the connection-string handler reports a transient failure rather than handing a
        // checkout a client that does not exist.
        Assert.Null(await h.Service.GetClient(HangingStore));
        Assert.Empty(await h.Service.GetRunningStoreIds());

        // The settings are still cached, though: the store is configured, it just has no wallet up.
        Assert.NotNull(await h.Service.Get(HangingStore));
    }

    [Fact]
    public async Task One_stores_hanging_connect_does_not_stop_another_stores_wallet_from_starting()
    {
        using var h = SparkServiceHarness.Create();
        h.SeedStore(HangingStore, SparkServiceHarness.MnemonicFor(1));
        h.SeedStore(HealthyStore, SparkServiceHarness.MnemonicFor(2));
        h.Sdk.HangFor.Add(HangingStore);

        StartWithinTimeout(h);

        Assert.NotNull(await h.Service.GetClient(HealthyStore));
        Assert.Null(await h.Service.GetClient(HangingStore));
        Assert.Equal([HealthyStore], await h.Service.GetRunningStoreIds());
    }

    [Fact]
    public async Task A_store_whose_connect_throws_does_not_stop_another_stores_wallet_from_starting()
    {
        using var h = SparkServiceHarness.Create();
        h.SeedStore(ThrowingStore, SparkServiceHarness.MnemonicFor(3));
        h.SeedStore(HealthyStore, SparkServiceHarness.MnemonicFor(2));
        h.Sdk.FailFor[ThrowingStore] = new InvalidOperationException("the SDK refused this seed");

        StartWithinTimeout(h);

        Assert.NotNull(await h.Service.GetClient(HealthyStore));
        Assert.Null(await h.Service.GetClient(ThrowingStore));
    }

    /// <summary>
    /// A connect that throws must not lock its store out of its own wallet either.
    /// </summary>
    /// <remarks>
    /// The timeout path already released the storage claim it had taken (see
    /// <see cref="A_permanently_hung_connect_releases_the_storage_lock_so_the_store_can_be_reconfigured"/>),
    /// but a connect that fails immediately is rethrown by <c>SparkDeadline</c> past both ownership handoffs,
    /// and the leaked <c>FileShare.None</c> handle then refused the store's own next attempt with the
    /// two-BTCPay-instances message — accusing another process of a hold this process was doing to itself,
    /// which reconfiguring could never clear.
    /// </remarks>
    [Fact]
    public async Task A_store_whose_connect_throws_leaves_the_storage_lock_claimable()
    {
        using var h = SparkServiceHarness.Create();
        h.SeedStore(ThrowingStore, SparkServiceHarness.MnemonicFor(3));
        h.Sdk.FailFor[ThrowingStore] = new InvalidOperationException("the SDK refused this seed");

        StartWithinTimeout(h);

        // The refused attempt named the failure it actually hit — the connection error — never the lock
        // message. Startup swallows the throw per-store, so it surfaces in the operator's log.
        Assert.Contains("the SDK refused this seed", h.Log.AllText);
        Assert.DoesNotContain("Another process", h.Log.AllText);

        // The decisive half: the claim the throwing attempt took is back on the shelf. Before the fix a
        // FileStream nothing could any longer reach still held the directory, and this returned null.
        var reclaimed = Sdk.SparkStorageLock.TryAcquire(h.StorageDirFor(ThrowingStore), out _);
        Assert.NotNull(reclaimed);
        reclaimed!.Dispose();

        // And reconfiguring the store starts its wallet, rather than being told another process holds it.
        h.Sdk.FailFor.Remove(ThrowingStore);
        var settings = (await h.Service.Get(ThrowingStore))!;
        var applied = await h.Service.Set(ThrowingStore, settings);

        // Before the fix this came back not-running with the "Another process" refusal.
        Assert.True(applied.WalletRunning, $"the store should be running, got: {applied.Reason}");
        Assert.NotNull(await h.Service.GetClient(ThrowingStore));
    }

    /// <summary>
    /// A connect that arrives after its deadline must be shut down, not left running unreferenced.
    /// </summary>
    /// <remarks>
    /// The deadline abandons the wait, never the call, so the SDK will eventually hand back a live wallet on
    /// the store's own SQLite file — one that still serves the network and still mints invoices. Dropping the
    /// reference would leave the store with an unreachable live wallet <em>and</em> let a later configure put a
    /// second instance on the same one, which is the corruption the whole single-instance guard exists to
    /// prevent.
    /// </remarks>
    [Fact]
    public async Task A_connect_that_finishes_after_its_deadline_is_disconnected_and_disposed()
    {
        using var h = SparkServiceHarness.Create();
        h.SeedStore(HangingStore, SparkServiceHarness.MnemonicFor(1));
        h.Sdk.HangFor.Add(HangingStore);

        StartWithinTimeout(h);

        var late = h.Sdk.Release(HangingStore);

        await WaitUntil(() => late.Disposed, "the late wallet to be shut down");
        Assert.True(late.Disconnected, "Disconnect must precede Dispose: Dispose alone leaves it minting invoices");

        // And it is emphatically not adopted as the store's instance.
        Assert.Null(await h.Service.GetClient(HangingStore));
    }

    /// <summary>
    /// A connect that hangs forever must not lock its store out of its own wallet until the process restarts.
    /// </summary>
    /// <remarks>
    /// Audit finding InfraAndLogging F2. The abandoned connect kept the store's storage lock while it awaited a
    /// task that no SDK call can cancel, and that lock is a <c>FileShare.None</c> handle enforced between
    /// descriptors in the same process. So the store's own next attempt failed with "Another process is already
    /// using this store's Spark wallet storage" — accusing a second BTCPay of a hold this process was doing to
    /// itself — and reconfiguring could never clear it. Only a restart could.
    /// </remarks>
    [Fact]
    public async Task A_permanently_hung_connect_releases_the_storage_lock_so_the_store_can_be_reconfigured()
    {
        using var h = SparkServiceHarness.Create(
            abandonedConnectGrace: TimeSpan.FromMilliseconds(250));
        h.SeedStore(HangingStore, SparkServiceHarness.MnemonicFor(1));
        h.Sdk.HangFor.Add(HangingStore);

        StartWithinTimeout(h);

        // The connect is never released, modelling a stall that outlives any useful wait.
        await WaitUntil(
            () => h.Log.AllText.Contains("Releasing the storage lock"),
            "the abandoned connect's grace period to expire");

        // The decisive half: the store can now be started again rather than being told another process holds it.
        h.Sdk.HangFor.Remove(HangingStore);
        var settings = (await h.Service.Get(HangingStore))!;
        var applied = await h.Service.Set(HangingStore, settings);

        // Before the fix this came back not-running, with a reason blaming another process for a lock this
        // process was holding against itself.
        Assert.True(applied.WalletRunning, $"the store should be running again, got: {applied.Reason}");
        Assert.NotNull(await h.Service.GetClient(HangingStore));
    }

    /// <summary>
    /// Nothing is left buffering events for a wallet no consumer was ever started for.
    /// </summary>
    [Fact]
    public void An_abandoned_connect_has_its_event_channel_completed()
    {
        using var h = SparkServiceHarness.Create();
        h.SeedStore(HangingStore, SparkServiceHarness.MnemonicFor(1));
        h.Sdk.HangFor.Add(HangingStore);

        StartWithinTimeout(h);

        var writer = h.Sdk.EventWriters[HangingStore];
        Assert.False(
            writer.TryWrite(new Sdk.SparkEventEnvelope(HangingStore, Sdk.SparkEventKind.Synced, null)),
            "a completed writer refuses, which is what makes the listener report the loss rather than hide it");
    }

    /// <summary>
    /// A wallet that connected but that no client ever adopted must die with the attempt, not leak.
    /// </summary>
    /// <remarks>
    /// The <c>finally</c> guards a failed adoption on three halves — the storage claim, the SDK handle, and
    /// the event channel — and the throwing-connect tests above cover only the claim, because that throw
    /// lands before a handle exists. This reaches the other two: the harness builds the service without the
    /// BOLT11 parser, so <c>SparkLightningClient</c>'s own argument check throws precisely where a real bad
    /// dependency would, after a successful connect. No production seam was added for the test; the throw is
    /// shipped code refusing a shipped-null argument.
    /// </remarks>
    [Fact]
    public async Task A_wallet_that_connected_but_was_never_adopted_is_disposed_and_its_events_refused()
    {
        using var h = SparkServiceHarness.Create(failWalletAdoption: true);
        h.SeedStore(NeverAdoptedStore, SparkServiceHarness.MnemonicFor(4));

        // Startup swallows the adoption throw per-store, exactly as it swallows a connect throw: one broken
        // store must not stop BTCPay starting.
        StartWithinTimeout(h);
        Assert.Contains("could not start its Spark wallet", h.Log.AllText);

        var client = h.Sdk.Clients[NeverAdoptedStore];
        Assert.True(
            client.Disposed,
            "a connected SDK handle that no instance ever adopted must die in the finally next to the claim "
            + "it was meant to guard");

        // And nothing is left buffering events for a consumer that will never start — the same proof the
        // abandonment test above makes, now on the finally's own path.
        Assert.False(
            h.Sdk.EventWriters[NeverAdoptedStore].TryWrite(
                new Sdk.SparkEventEnvelope(NeverAdoptedStore, Sdk.SparkEventKind.Synced, null)),
            "the failed attempt's event writer must be completed, not left open and unreferenced");

        Assert.Null(await h.Service.GetClient(NeverAdoptedStore));
    }

    /// <summary>
    /// Runs <c>StartAsync</c> the way the host does, on its own thread, and fails rather than hanging.
    /// </summary>
    /// <remarks>
    /// A dedicated thread rather than <c>Task.Run</c> plus <c>Wait(timeout)</c>, matching
    /// <c>SparkPluginStartupTests</c>: the failure being guarded against parks a thread indefinitely, and
    /// parking a pool thread would degrade the rest of the run instead of failing this test. The thread is a
    /// background thread so a genuinely hung startup cannot keep the process alive.
    /// </remarks>
    private static TimeSpan StartWithinTimeout(SparkServiceHarness h)
    {
        Exception? failure = null;
        var stopwatch = Stopwatch.StartNew();

        var thread = new Thread(() =>
        {
            try
            {
                h.Service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "spark-service-start"
        };

        thread.Start();

        if (!thread.Join(StartTimeout))
        {
            Assert.Fail(
                $"SparkService.StartAsync did not complete within {StartTimeout.TotalSeconds:0}s. "
                + "HostOptions.StartupTimeout is infinite by default, so on a real server BTCPay would never "
                + "finish starting — with no exception, and therefore no log line and no auto-disable of the "
                + "plugin. Every SDK call this method awaits needs a deadline of its own.");
        }

        stopwatch.Stop();

        if (failure is not null)
            throw new InvalidOperationException("SparkService.StartAsync threw; startup must not fail.", failure);

        return stopwatch.Elapsed;
    }

    // ------------------------------------------------------------------------------------------------
    // The exit-state backup is imported on connect, or the page lies about it.
    // ------------------------------------------------------------------------------------------------

    private const string BackupStore = "store-with-an-exit-state-backup";

    /// <summary>
    /// A stored exit-state backup is put into the wallet when it starts.
    /// </summary>
    /// <remarks>
    /// <b>This is the only thing that makes the backup worth taking.</b> The Advanced page tells an operator
    /// that a stored backup is imported automatically, and the backup exists for the case where the wallet's
    /// own storage is gone while the Spark operators are unreachable — the one situation in which a leaf's exit
    /// data cannot be re-fetched from anywhere. If nothing imports it, the operator is shown a "Stored" badge
    /// for data the plugin never reads, and they find out only when they need it, which is the worst possible
    /// moment. So this asserts the import reaches the SDK with the stored value, not merely that a code path
    /// exists.
    /// </remarks>
    [Fact]
    public async Task A_stored_exit_state_backup_is_imported_when_the_wallet_starts()
    {
        using var gate = FeatureGate();
        using var h = SparkServiceHarness.Create();
        h.SeedStore(BackupStore, SparkServiceHarness.MnemonicFor(1));
        WithExitStateBackup(h, BackupStore, "the-stored-backup-blob");

        StartWithinTimeout(h);

        // The import happens on the warm-up path, which is deliberately not awaited by the connect, so the
        // assertion has to wait for it rather than assume it has already run.
        await WaitUntil(
            () => h.Sdk.Clients.TryGetValue(BackupStore, out var backupClient) && backupClient.ExitImportCalls.Count > 0,
            "the exit-state backup to be imported");

        Assert.Equal(["the-stored-backup-blob"], h.Sdk.Clients[BackupStore].ExitImportCalls);
    }

    /// <summary>
    /// A store with no backup has nothing imported, so the feature costs nothing until it is used.
    /// </summary>
    /// <remarks>
    /// The other half of the guard above, and the one that catches an import wired to the wrong place: an
    /// implementation that imported on every connect regardless of whether a backup existed would pass the
    /// first test while quietly sending an empty or absent value to the SDK for every store on the server.
    /// <b>The gate is on for this test deliberately.</b> With it off, nothing imports and the assertion would
    /// pass against an implementation that imported for every store, which is precisely the bug it exists to
    /// catch.
    /// </remarks>
    [Fact]
    public async Task A_store_with_no_backup_imports_nothing_on_start()
    {
        using var gate = FeatureGate();
        using var h = SparkServiceHarness.Create();
        h.SeedStore(HealthyStore, SparkServiceHarness.MnemonicFor(1));

        StartWithinTimeout(h);

        // Give a would-be import the same window the positive test gives the real one, so this cannot pass
        // merely by observing the store before an incorrect import had a chance to run.
        await Task.Delay(250);

        Assert.Empty(h.Sdk.Clients[HealthyStore].ExitImportCalls);
    }

    /// <summary>
    /// The backup value itself never reaches the log, on either path.
    /// </summary>
    /// <remarks>
    /// The blob carries every leaf of the wallet and the transactions that spend them, so it discloses the
    /// balance, how it is split, and the payment history. It is the one secret on this surface, and the import
    /// is the only place the plugin handles it — which makes it the place a well-meaning debug line would leak
    /// it. Asserted on the failure path too, because that is where an implementation is most tempted to print
    /// what it could not read.
    /// </remarks>
    [Fact]
    public async Task The_exit_state_backup_value_never_reaches_the_log()
    {
        const string secret = "blob-that-must-not-be-logged-9f3a";

        using var gate = FeatureGate();
        using var h = SparkServiceHarness.Create();
        h.SeedStore(BackupStore, SparkServiceHarness.MnemonicFor(1));
        WithExitStateBackup(h, BackupStore, secret);

        StartWithinTimeout(h);
        await WaitUntil(
            () => h.Sdk.Clients.TryGetValue(BackupStore, out var backupClient) && backupClient.ExitImportCalls.Count > 0,
            "the import to run");

        Assert.DoesNotContain(secret, h.Log.AllText);
    }

    /// <summary>
    /// Turns the experimental-exit gate on for the duration of a test.
    /// </summary>
    /// <remarks>
    /// The import is behind <see cref="Constants.UnilateralExitEnabled"/>, so a test that did not set this
    /// would be asserting against a feature that was off and would pass for the wrong reason. The variable is
    /// process-wide, which is why this class joins <see cref="UnilateralExitTestCollection"/>: xUnit's
    /// per-class parallelism would otherwise let two classes read it while another is mid-swap.
    /// </remarks>
    private static IDisposable FeatureGate() => new EnvironmentSwitch(Gate);

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

    /// <summary>
    /// Gives a seeded store an exit-state backup in its persisted settings.
    /// </summary>
    /// <remarks>
    /// Written through the store repository rather than the in-memory cache, because the connect path reads
    /// what a <em>previous run</em> persisted — a test that set only the cache would be testing the harness.
    /// </remarks>
    private static void WithExitStateBackup(SparkServiceHarness h, string storeId, string backup)
    {
        var settings = h.Stores.Stored<SparkSettings>(storeId, Constants.StoreSettingsKey)!;
        settings.UnilateralExit = new UnilateralExitSettings { ExitStateBackup = backup };
        h.Stores.Seed(storeId, Constants.StoreSettingsKey, settings);
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }
}
