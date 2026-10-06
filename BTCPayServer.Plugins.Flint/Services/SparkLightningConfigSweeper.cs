using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>Outcome of one sweep of every store's Lightning configuration.</summary>
/// <param name="Cleared">Cross-store configurations cleared — each a store whose Lightning payment method pointed at another store's Spark wallet.</param>
/// <param name="Rotated">Victim payment keys rotated so every previously leaked copy of the victim's connection string stops resolving.</param>
public sealed record SparkLightningConfigSweepResult(int Cleared, int Rotated);

/// <summary>
/// Enforces store binding on Lightning configurations that are already saved — the boundary
/// <see cref="ISparkClientResolver"/> cannot police, because BTCPay's connection-string handler is never
/// told which store a string is being configured for.
/// </summary>
/// <remarks>
/// <para>
/// Save-time (a store's own UI saving a mismatched string, or the Greenfield PUT) is refused by
/// <c>SparkLightningClient.Validate</c>, which runs inside the request with the configured store on the
/// <c>HttpContext</c>. <c>Validate</c> is skipped when the saved string is unchanged, and a configuration
/// could in principle land without ever passing it (a string saved by an older version, or written straight
/// to the database), so this sweep is the second layer: at startup, and every
/// <see cref="Constants.ConfigSweepInterval"/> thereafter (<see cref="SparkLightningConfigSweepTask"/>), it walks
/// <em>every</em> store's Lightning payment-method configuration, and one that embeds another store's id is
/// cleared and its victim's payment key rotated.
/// </para>
/// <para>
/// Clearing removes the hijacking store's Lightning payment method — the configuration itself is the
/// attack, and there is no legitimate reason for store A's Lightning config to embed store B's id. Rotating
/// the victim's payment key is what kills copies of the victim's connection string that were never saved on
/// any store: the string embeds the key, and the resolver only honours the key the settings currently hold,
/// so a fresh key makes every older copy a dead <c>StaleSpark</c> string. Rotation deliberately rewrites
/// only a victim configuration that still points at Spark; a merchant who has since moved their Lightning to
/// their own node keeps that configuration untouched, and their settings simply carry a key nothing uses
/// until they come back.
/// </para>
/// <para>
/// The sweep is idempotent: before it runs, a cross-store configuration both exists and is broken (the
/// status page marks it <see cref="SparkLightningWiringState.OtherStoreSpark"/>); after it runs, nothing
/// does, so a repeat pass finds nothing to do. It runs at startup and — as the slower backstop for
/// configurations written outside HTTP — about every half hour; the save-time layer already blocks new
/// mismatches from every HTTP path, so the periodic pass only has to be there, not be fast.
/// </para>
/// <para>
/// BTCPay 2.4.5 restricts saving a <c>type=flint</c> string through core to server admins (see the remarks
/// on <c>SparkLightningClient.Validate</c>). That narrows who could create a cross-store configuration, but
/// none of this sweep's work goes away. Configurations saved before an upgrade survive it, writes outside
/// HTTP are untouched by it, and the key rotation is still what retires leaked copies of a string.
/// </para>
/// </remarks>
public sealed class SparkLightningConfigSweeper
{
    private readonly ISparkStoreSource _stores;
    private readonly SparkLightningWiring _wiring;
    private readonly ISparkStoreSettingsStore _settingsStore;
    private readonly ILogger<SparkLightningConfigSweeper> _logger;

    public SparkLightningConfigSweeper(
        ISparkStoreSource stores,
        SparkLightningWiring wiring,
        ISparkStoreSettingsStore settingsStore,
        ILogger<SparkLightningConfigSweeper> logger)
    {
        _stores = stores;
        _wiring = wiring;
        _settingsStore = settingsStore;
        _logger = logger;
    }

    /// <summary>
    /// Walks every store, clears every cross-store Lightning configuration found, and rotates the victim
    /// keys those configurations referenced. Never throws; one broken store must not stop the rest of the
    /// walk.
    /// </summary>
    public async Task<SparkLightningConfigSweepResult> SweepAsync(
        CancellationToken cancellationToken = default)
    {
        var stores = await _stores.GetStoresAsync(cancellationToken).ConfigureAwait(false);

        var victims = new List<string>();
        var cleared = 0;

        foreach (var store in stores)
        {
            var storeId = store.Id;
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var report = _wiring.Inspect(store, paymentKey: null);

                if (report.State is not SparkLightningWiringState.OtherStoreSpark)
                    continue;

                if (await _wiring.ClearCrossStoreAsync(storeId, cancellationToken).ConfigureAwait(false)
                    is { } victimStoreId)
                {
                    victims.Add(victimStoreId);
                    // Only a confirmed clear counts: null here means the cross-store configuration was
                    // already gone by the time the clear re-read it (deleted mid-pass), so there was
                    // nothing left to clear.
                    cleared++;
                }
            }
            catch (Exception ex)
            {
                // One broken store must not stop the walk: the next store's configuration may still be a
                // hijack, and skipping it would leave the victim exposed for another full interval.
                _logger.LogWarning(ex,
                    "Store {StoreId}: its Lightning configuration could not be swept; it will be retried "
                    + "on the next sweep pass", storeId);
            }
        }

        var rotated = 0;
        foreach (var victim in victims.Distinct(StringComparer.Ordinal))
        {
            if (await RotateVictimKeyAsync(victim, cancellationToken).ConfigureAwait(false))
                rotated++;
        }

        if (victims.Count > 0)
        {
            _logger.LogWarning(
                "Cross-store Lightning configuration sweep: cleared {Cleared} configuration(s) and rotated "
                + "{Rotated} of {VictimCount} victim payment key(s)",
                cleared, rotated, victims.Distinct(StringComparer.Ordinal).Count());
        }

        return new SparkLightningConfigSweepResult(cleared, rotated);
    }

    /// <summary>
    /// Mints a fresh payment key for the victim store, persists it, restarts its wallet, and rewrites its
    /// Lightning configuration with the new string — invalidating every previously issued copy of the old
    /// string. Restores the old settings verbatim if the wallet declines to restart.
    /// </summary>
    private async Task<bool> RotateVictimKeyAsync(string victimStoreId, CancellationToken cancellationToken)
    {
        SparkSettings? settings;
        try
        {
            settings = await _settingsStore.GetAsync(victimStoreId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Store {StoreId}: could not read its settings to rotate the payment key", victimStoreId);
            return false;
        }

        // No wallet to revoke: clearing the cross-store configuration was the whole remediation.
        if (settings is null)
            return false;

        var rotated = settings.Clone();
        rotated.PaymentKey = SparkConnectionString.GeneratePaymentKey();

        try
        {
            var applied = await _settingsStore.SetAsync(victimStoreId, rotated).ConfigureAwait(false);
            if (!applied.WalletRunning)
            {
                _logger.LogWarning(
                    "Store {StoreId}: its wallet declined to restart with a rotated payment key; restoring "
                    + "the previous key", victimStoreId);
                await _settingsStore.SetAsync(victimStoreId, settings).ConfigureAwait(false);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Store {StoreId}: could not rotate its payment key; restoring the previous key", victimStoreId);
            try
            {
                await _settingsStore.SetAsync(victimStoreId, settings).ConfigureAwait(false);
            }
            catch (Exception rollbackEx)
            {
                _logger.LogError(rollbackEx,
                    "Store {StoreId}: could not restore the previous payment key after a failed rotation",
                    victimStoreId);
            }

            return false;
        }

        // Only a configuration that still points at Spark is rewritten; a merchant who moved their
        // Lightning to their own node keeps that configuration untouched.
        try
        {
            var current = await _wiring.InspectAsync(victimStoreId, paymentKey: null, cancellationToken)
                .ConfigureAwait(false);
            if (current.State is SparkLightningWiringState.Spark or SparkLightningWiringState.StaleSpark)
            {
                var wired = await _wiring.EnableAsync(victimStoreId, rotated.PaymentKey, cancellationToken)
                    .ConfigureAwait(false);
                if (!wired)
                {
                    _logger.LogWarning(
                        "Store {StoreId}: its payment key was rotated, but its Lightning configuration could "
                        + "not be rewritten; the store's own copy of the connection string is now stale",
                        victimStoreId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Store {StoreId}: its payment key was rotated, but its Lightning configuration could not be "
                + "re-inspected or rewritten", victimStoreId);
        }

        _logger.LogInformation(
            "Store {StoreId}: payment key rotated because another store's Lightning configuration pointed at "
            + "this wallet", victimStoreId);
        return true;
    }
}
