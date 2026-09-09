using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Models;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>One reason a sweep configuration was refused, and which field it belongs to.</summary>
/// <param name="Field">
/// The <see cref="SweepSettingsInput"/> property name. Each surface prefixes it as its own conventions require —
/// the form as <c>Settings.&lt;field&gt;</c> in <c>ModelState</c>, the API as the JSON member name.
/// </param>
public sealed record SparkSweepSettingsError(string Field, string Error);

/// <summary>What happened to a sweep-configuration write.</summary>
public enum SparkSweepSettingsSaveStatus
{
    /// <summary>Stored. <see cref="SparkSweepSettingsSaveResult.WalletRunning"/> says whether the wallet is up.</summary>
    Saved,

    /// <summary>The store has not set Spark up, so it has no sweep configuration to change.</summary>
    NotConfigured,

    /// <summary>Refused, and nothing was written. <see cref="SparkSweepSettingsSaveResult.Errors"/> says why.</summary>
    Invalid
}

/// <param name="WalletRunning">
/// False when the settings were stored but the store's wallet is not running. <b>Not a failure of the write</b> —
/// the settings are persisted before the instance is reconciled — so callers report it rather than rolling back.
/// </param>
/// <param name="Settings">The configuration now in force. Null unless the write succeeded.</param>
public sealed record SparkSweepSettingsSaveResult(
    SparkSweepSettingsSaveStatus Status,
    IReadOnlyList<SparkSweepSettingsError> Errors,
    bool WalletRunning,
    string? WalletReason,
    SweepSettings? Settings)
{
    private static readonly SparkSweepSettingsError[] NoErrors = [];

    public static SparkSweepSettingsSaveResult NotConfigured() =>
        new(SparkSweepSettingsSaveStatus.NotConfigured, NoErrors, false, null, null);

    public static SparkSweepSettingsSaveResult Invalid(IReadOnlyList<SparkSweepSettingsError> errors) =>
        new(SparkSweepSettingsSaveStatus.Invalid, errors, false, null, null);

    public static SparkSweepSettingsSaveResult Saved(
        SweepSettings settings, bool walletRunning, string? walletReason) =>
        new(SparkSweepSettingsSaveStatus.Saved, NoErrors, walletRunning, walletReason, settings);
}

/// <summary>
/// A store's sweep configuration and the surrounding facts a caller needs to make sense of it.
/// </summary>
/// <param name="Configured">False when the store has not set Spark up. Everything else is then meaningless.</param>
/// <param name="BalanceSats">Indicative, read without forcing a sync. Null when it could not be read.</param>
public sealed record SparkSweepSettingsView(
    bool Configured,
    SweepSettingsInput Settings,
    bool WalletRunning,
    long? BalanceSats,
    SweepAddressStatus StoreWalletStatus,
    string? StoreWalletReason);

/// <summary>One page of a store's sweep history.</summary>
/// <param name="Skip">The offset actually used, after clamping.</param>
/// <param name="Count">The page size actually used, after clamping.</param>
public sealed record SparkSweepHistoryPage(
    IReadOnlyList<SweepRecord> Records,
    int Total,
    int Skip,
    int Count);

/// <summary>
/// The single path by which a store's sweep configuration is read, validated and written.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both surfaces go through here, and that is the point.</b> The settings page and the Greenfield
/// <c>.../spark/sweep</c> endpoints would otherwise each carry their own copy of things worth exactly one
/// implementation: <see cref="SweepSettingsInput.Validate"/> against this server's chain, the refusal for a store
/// in <see cref="SweepDestinationMode.StoreWallet"/> mode that has no on-chain wallet to sweep into, the
/// read-modify-write that must leave the protected mnemonic in the settings blob untouched, and the paging clamp.
/// A second, subtly laxer copy on the API is how a fee guard gets switched off through the surface nobody was
/// watching.
/// </para>
/// <para>
/// <b>None of this is the enforcement.</b> <see cref="SparkSweepEngine"/> re-derives every economic and safety
/// decision from the stored settings and a live fee quote at the point money would move, and applies a hard fee
/// backstop no configuration can lift. What happens here is refusing to <em>store</em> a configuration that could
/// only ever produce refusals — which is a courtesy to the merchant, and a hard requirement of parity between the
/// two surfaces.
/// </para>
/// </remarks>
public sealed class SparkSweepSettingsService
{
    /// <summary>Largest page of sweep history either surface will return.</summary>
    public const int MaxHistoryPageSize = 100;

    private readonly ISparkStoreSettingsStore _settingsStore;
    private readonly ISparkStoreRuntime _runtime;
    private readonly SweepDestinationResolver _destinations;
    private readonly ISweepAddressSource _addressSource;
    private readonly ISweepRecordStore _records;
    private readonly ILogger<SparkSweepSettingsService> _logger;

    public SparkSweepSettingsService(
        ISparkStoreSettingsStore settingsStore,
        ISparkStoreRuntime runtime,
        SweepDestinationResolver destinations,
        ISweepAddressSource addressSource,
        ISweepRecordStore records,
        ILogger<SparkSweepSettingsService> logger)
    {
        _settingsStore = settingsStore;
        _runtime = runtime;
        _destinations = destinations;
        _addressSource = addressSource;
        _records = records;
        _logger = logger;
    }

    /// <summary>The chain a static destination address is validated against.</summary>
    public Network Network => _destinations.Network;

    /// <summary>
    /// Reads a store's sweep configuration and the state around it.
    /// </summary>
    public async Task<SparkSweepSettingsView> ReadAsync(
        string storeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        var settings = await _settingsStore.GetAsync(storeId).ConfigureAwait(false);
        if (settings is null)
        {
            return new SparkSweepSettingsView(
                Configured: false, new SweepSettingsInput(), false, null, SweepAddressStatus.Unavailable, null);
        }

        // Coalesced: an explicit `"Sweep": null` in a stored blob defeats the property initialiser.
        var input = SweepSettingsInput.From(settings.Sweep ?? new SweepSettings());

        long? balance = null;
        var sdk = await _runtime.GetSdkClientAsync(storeId).ConfigureAwait(false);
        if (sdk is not null)
        {
            try
            {
                // Cached read: this is a request thread and the number is indicative wherever it is reported.
                var info = await sdk.GetInfoAsync(ensureSynced: false, cancellationToken).ConfigureAwait(false);
                balance = info.BalanceSats;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Store {StoreId}: could not read its Spark balance while reporting sweep settings", storeId);
            }
        }

        // reserve: false — reporting a configuration must not consume an address from the merchant's wallet.
        var address = await _addressSource
            .GetAddressAsync(storeId, reserve: false, cancellationToken)
            .ConfigureAwait(false);

        return new SparkSweepSettingsView(
            Configured: true, input, sdk is not null, balance, address.Status, address.Reason);
    }

    /// <summary>
    /// Mempool fee rates, for showing what each confirmation-speed tier roughly pays. Null when the wallet is
    /// not running or the rates cannot be read — the page renders without them.
    /// </summary>
    public async Task<Sdk.SparkRecommendedFees?> ReadRecommendedFeesAsync(
        string storeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        var sdk = await _runtime.GetSdkClientAsync(storeId).ConfigureAwait(false);
        if (sdk is null)
            return null;

        try
        {
            return await sdk.GetRecommendedFeesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Decoration only: the tiers still render, just without a market rate beside them.
            _logger.LogDebug(ex, "Store {StoreId}: could not read recommended fees", storeId);
            return null;
        }
    }

    /// <summary>
    /// One page of a store's sweep history, newest first.
    /// </summary>
    /// <remarks>
    /// The bounds are clamped rather than validated, and shared for the same reason the rest of this class is: a
    /// nonsensical page size should produce a sensible page identically on both surfaces, and a caller who asks for
    /// ten thousand rows must not be able to make the server read them.
    /// </remarks>
    public async Task<SparkSweepHistoryPage> ReadHistoryAsync(
        string storeId,
        int skip,
        int count,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        skip = Math.Max(0, skip);
        count = Math.Clamp(count, 1, MaxHistoryPageSize);

        var total = await _records.CountAsync(storeId, cancellationToken).ConfigureAwait(false);
        var records = await _records.ListAsync(storeId, skip, count, cancellationToken).ConfigureAwait(false);
        return new SparkSweepHistoryPage(records, total, skip, count);
    }

    /// <summary>One sweep record for this store, or null when no row matches the key.</summary>
    public async Task<SweepRecord?> ReadRecordAsync(
        string storeId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);
        return await _records.GetAsync(storeId, idempotencyKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates <paramref name="input"/> and, if it passes, stores it as the store's sweep configuration.
    /// </summary>
    public async Task<SparkSweepSettingsSaveResult> SaveAsync(
        string storeId,
        SweepSettingsInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        ArgumentNullException.ThrowIfNull(input);

        var settings = await _settingsStore.GetAsync(storeId).ConfigureAwait(false);
        if (settings is null)
            return SparkSweepSettingsSaveResult.NotConfigured();

        var errors = new List<SparkSweepSettingsError>();
        foreach (var (field, error) in input.Validate(_destinations.Network))
            errors.Add(new SparkSweepSettingsError(field, error));

        // Checked here rather than trusted from whatever the caller rendered or read: the option can be disabled
        // in the view and still posted, an API caller has no view at all, and sweeping into a wallet that does not
        // exist would be refused later anyway — better to say so while the configuration is being written.
        if (input.DestinationMode is SweepDestinationMode.StoreWallet)
        {
            // reserve: false — validating a configuration must not consume an address from the merchant's wallet.
            var address = await _addressSource
                .GetAddressAsync(storeId, reserve: false, cancellationToken)
                .ConfigureAwait(false);
            if (address.Status is SweepAddressStatus.NoOnchainWallet)
            {
                errors.Add(new SparkSweepSettingsError(
                    nameof(SweepSettingsInput.DestinationMode),
                    address.Reason ?? "This store has no Bitcoin wallet to sweep into."));
            }
        }

        if (errors.Count > 0)
            return SparkSweepSettingsSaveResult.Invalid(errors);

        // Applied onto a copy of the whole settings object, not onto the one that was read. Cloning only the
        // sweep block was not enough: assigning it back still mutated the object the store handed over, so a
        // write that threw on the way to the database left the caller's copy — and, before the service started
        // cloning too, the service's cache — holding a configuration that was never persisted. The engine reads
        // through the same cache, so it would have swept on settings that do not exist anywhere durable.
        //
        // Only Sweep is touched: the protected mnemonic in the same blob is carried across untouched, because
        // nothing on this path may see or rewrite it.
        var updated = settings.Clone();
        var sweep = (updated.Sweep ?? new SweepSettings()).Clone();
        input.ApplyTo(sweep);
        updated.Sweep = sweep;

        var applied = await _settingsStore.SetAsync(storeId, updated).ConfigureAwait(false);
        return SparkSweepSettingsSaveResult.Saved(sweep, applied.WalletRunning, applied.Reason);
    }
}
