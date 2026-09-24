using BTCPayServer.Plugins.Flint.Services;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// In-memory <see cref="ISparkStoreSettingsStore"/> that reproduces the real one's failure shapes.
/// </summary>
/// <remarks>
/// <para>
/// Three behaviours are modelled because the logic above this seam is wrong without them:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="FailNextSetWith"/> — the real implementation connects the SDK inside
/// <see cref="SetAsync"/> and therefore <em>throws</em> on a seed the SDK rejects, after having already
/// persisted the settings.</description></item>
/// <item><description><see cref="NextSetDeclinesWith"/> — the quiet failure. The real implementation stores
/// the settings and reports that the wallet declined to start, without any exception: a seed another store
/// already owns, an unsupported chain, a seed this server cannot decrypt.</description></item>
/// <item><description>A removal clears the store's Lightning configuration, when constructed with a
/// <see cref="SparkLightningWiring"/>. <c>SparkService.Set(null)</c> does that, and it is the coupling
/// <c>SparkStoreProvisioner.RemoveAsync</c> relies on rather than doing itself.</description></item>
/// <item><description><b>A write replaces the store's SDK handle, and disposes the old one</b>, when
/// constructed with a <see cref="FakeSparkStoreRuntime"/>. <c>SparkService.Set</c> reconciles the running
/// instance with the new settings by tearing the old one down and connecting a fresh one, so <em>any handle a
/// caller resolved before the write is disposed after it</em>. Without this modelled, a caller that captured
/// the handle first and used it afterwards passes every test and then fails on every single request in
/// production — which is exactly what the Stable Balance save did.</description></item>
/// </list>
/// </remarks>
public sealed class FakeSparkStoreSettingsStore : ISparkStoreSettingsStore
{
    private readonly SparkLightningWiring? _lightningWiring;
    private readonly WriteLog? _writeLog;
    private readonly FakeSparkStoreRuntime? _runtime;
    private readonly Func<FakeSparkSdkClient>? _reconnect;

    /// <param name="runtime">
    /// When supplied, a write reconciles this store's live handle the way <c>SparkService.Set</c> does: the old
    /// handle is disposed and replaced. Callers that resolved a handle before the write will then find it
    /// disposed, which is the production behaviour.
    /// </param>
    /// <param name="reconnect">
    /// Produces the replacement handle. Defaults to carrying the previous one's observable state across, so a
    /// test asserting on balances or recorded calls does not lose them to a reconnect it did not ask about.
    /// </param>
    public FakeSparkStoreSettingsStore(
        SparkLightningWiring? lightningWiring = null,
        WriteLog? writeLog = null,
        FakeSparkStoreRuntime? runtime = null,
        Func<FakeSparkSdkClient>? reconnect = null)
    {
        _lightningWiring = lightningWiring;
        _writeLog = writeLog;
        _runtime = runtime;
        _reconnect = reconnect;
    }

    public Dictionary<string, SparkSettings?> Settings { get; } = [];

    /// <summary>Every value passed to <see cref="SetAsync"/>, in order. Null entries are removals.</summary>
    public List<(string StoreId, SparkSettings? Settings)> Writes { get; } = [];

    /// <summary>
    /// Thrown by the next non-null <see cref="SetAsync"/>, after it has stored the settings — the way the
    /// real one fails when the SDK refuses the seed outright.
    /// </summary>
    public Exception? FailNextSetWith { get; set; }

    /// <summary>
    /// Thrown by the next <see cref="SetAsync"/> <b>before</b> it stores anything — the way the real one fails
    /// when the database write itself fails.
    /// </summary>
    /// <remarks>
    /// A different shape from <see cref="FailNextSetWith"/> and worth both. That one is "persisted, then the
    /// wallet refused the seed"; this one is "nothing was persisted at all", which is the only case in which
    /// the difference between what a caller edited and what is stored is observable. A caller that applied its
    /// edit to the object it read from <see cref="GetAsync"/> — which this fake, like the interface, hands out
    /// by reference — leaves that edit behind here even though no write happened.
    /// </remarks>
    public Exception? FailNextSetBeforeStoringWith { get; set; }

    /// <summary>
    /// Reported by the next non-null <see cref="SetAsync"/> as "stored, but the wallet is not running".
    /// </summary>
    public string? NextSetDeclinesWith { get; set; }

    /// <summary>
    /// Set for the whole lifetime rather than one call, for the case that actually happens in production: two
    /// stores given the same seed, where every attempt after the first is refused by the wallet-owner guard.
    /// </summary>
    public string? AlwaysDeclineWith { get; set; }

    public Task<SparkSettings?> GetAsync(string storeId) =>
        Task.FromResult(Settings.TryGetValue(storeId, out var settings) ? settings : null);

    public async Task<SparkSettingsApplied> SetAsync(string storeId, SparkSettings? settings)
    {
        if (FailNextSetBeforeStoringWith is { } beforeStoring)
        {
            FailNextSetBeforeStoringWith = null;
            throw beforeStoring;
        }

        Writes.Add((storeId, settings));
        _writeLog?.Record($"settings:{storeId}:{(settings is null ? "removed" : "stored")}");
        Settings[storeId] = settings;

        if (settings is null)
        {
            // What SparkService.Set does on removal, and the reason RemoveAsync does not clear the config
            // itself. Without this here, that coupling is untested.
            if (_lightningWiring is not null)
                await _lightningWiring.ClearIfOursAsync(storeId).ConfigureAwait(false);
            TeardownWallet(storeId);
            return SparkSettingsApplied.Removed;
        }

        if (FailNextSetWith is { } failure)
        {
            FailNextSetWith = null;
            throw failure;
        }

        if (NextSetDeclinesWith is { } once)
        {
            NextSetDeclinesWith = null;
            return SparkSettingsApplied.NotRunning(once);
        }

        if (AlwaysDeclineWith is { } always)
            return SparkSettingsApplied.NotRunning(always);

        ReconnectWallet(storeId);
        return SparkSettingsApplied.Running;
    }

    /// <summary>
    /// Clears the deprecated exit-state backup slot, as <c>SparkService</c> does. Deliberately not recorded
    /// in <see cref="Writes"/>: the real one empties the slot through the store repository rather than
    /// through <see cref="SetAsync"/>, precisely so a press that stores no settings blob does not reconcile
    /// the wallet — and a caller that reached for <see cref="SetAsync"/> instead is the thing a test needs
    /// to see.
    /// </summary>
    public Task ClearExitStateBackupSlotAsync(string storeId)
    {
        if (Settings.TryGetValue(storeId, out var settings) && settings?.UnilateralExit is { } exit)
            exit.ExitStateBackup = null;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Replaces the store's live handle, disposing the old one, as <c>SparkService.Set</c> does.
    /// </summary>
    private void ReconnectWallet(string storeId)
    {
        if (_runtime is null || !_runtime.Clients.TryGetValue(storeId, out var existing))
            return;

        var previous = existing as FakeSparkSdkClient;
        var replacement = _reconnect?.Invoke() ?? previous?.Reconnected() ?? new FakeSparkSdkClient();

        // Whether the reconnected wallet has a stable-balance config, decided by the *production* rule over the
        // settings that were just written. This is the whole point of modelling the reconnect: the config the
        // wallet comes back with is the config that reconnect used, so anything the next call needs has to be
        // in it — it cannot be applied afterwards. Calling SparkService's own rule means narrowing that rule
        // breaks these tests rather than quietly passing them.
        if (Settings.TryGetValue(storeId, out var written) && written is not null)
            replacement.StableBalanceConfigured = SparkService.BuildStableBalance(written) is not null;

        // Removed from the map before it is disposed, exactly as the real teardown does, so nothing can resolve
        // a handle that is already gone.
        _runtime.Clients[storeId] = replacement;
        existing.Dispose();
    }

    private void TeardownWallet(string storeId)
    {
        if (_runtime is null || !_runtime.Clients.Remove(storeId, out var existing))
            return;
        existing.Dispose();
    }
}
