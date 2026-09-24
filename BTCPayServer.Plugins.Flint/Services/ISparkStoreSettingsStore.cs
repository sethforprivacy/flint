using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Sdk;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// What happened when settings were applied to a store.
/// </summary>
/// <param name="WalletRunning">
/// True when the store now has a live SDK instance. False after a removal, and false when the settings were
/// stored but the wallet declined to start.
/// </param>
/// <param name="Reason">
/// Merchant-facing explanation of why the wallet is not running, or null when there is nothing to explain — a
/// removal, or a successful start.
/// </param>
/// <remarks>
/// This type exists because "no exception" is not the same as "running". A store's wallet declines to start for
/// several reasons that are configuration problems rather than faults: a seed this server can no longer
/// decrypt, a seed another store already owns, a chain the SDK does not support, or missing setup. Reporting
/// those as success told the merchant Spark was ready, wrote and enabled their Lightning payment method, and
/// left every checkout failing.
/// </remarks>
public sealed record SparkSettingsApplied(bool WalletRunning, string? Reason)
{
    /// <summary>The wallet is up.</summary>
    public static readonly SparkSettingsApplied Running = new(true, null);

    /// <summary>The configuration was removed, so there is deliberately no wallet.</summary>
    public static readonly SparkSettingsApplied Removed = new(false, null);

    public static SparkSettingsApplied NotRunning(string reason) => new(false, reason);
}

/// <summary>
/// Reads and writes one store's <see cref="SparkSettings"/>, bringing its SDK instance in line with them.
/// </summary>
/// <remarks>
/// <para>
/// A seam over <see cref="SparkService"/>, which is the only implementation. It exists so
/// <see cref="SparkStoreProvisioner"/> — where the setup flow's decisions live — can be unit-tested without
/// a store repository, a database and a 200 MB native library.
/// </para>
/// <para>
/// <see cref="SetAsync"/> is not a plain write. It persists the settings and then reconciles the store's live
/// SDK instance with them: a non-null value starts or replaces the instance, and null tears it down and clears
/// the store's Lightning payment-method configuration if it still points at this plugin. It reports the outcome
/// rather than only throwing, because the failures that matter most are the quiet ones — see
/// <see cref="SparkSettingsApplied"/>. It may also throw, when the SDK rejects the seed outright, and a caller
/// must treat either as "the store may now be half-configured" and roll back.
/// </para>
/// </remarks>
public interface ISparkStoreSettingsStore
{
    /// <summary>Settings for a store, or null when the store has not configured Spark.</summary>
    Task<SparkSettings?> GetAsync(string storeId);

    /// <summary>Persists settings and reconciles the running instance. Null removes the configuration.</summary>
    Task<SparkSettingsApplied> SetAsync(string storeId, SparkSettings? settings);

    /// <summary>
    /// Clears a store's deprecated <see cref="UnilateralExitSettings.ExitStateBackup"/> slot — the
    /// persisted row and the cached copy alike. Nothing else about the settings moves, and the store's
    /// running SDK instance is left alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one write on this seam that is not <see cref="SetAsync"/>, and it exists because of what
    /// <see cref="SetAsync"/> costs: it reconciles the live instance, which tears the wallet down and
    /// reconnects it. The two callers that empty this slot — a legacy backup being adopted on connect, and
    /// an operator clearing the backup from the page — have no business interrupting a wallet for a write
    /// that stores nothing.
    /// </para>
    /// <para>
    /// Idempotent, and silent about the value: the slot is where an earlier version of this plugin kept a
    /// store's multi-megabyte exit-state secret.
    /// </para>
    /// </remarks>
    Task ClearExitStateBackupSlotAsync(string storeId);
}

/// <summary>
/// Runtime facts about a store's Spark wallet, for pages that report on one.
/// </summary>
/// <remarks>
/// Split out from <see cref="ISparkStoreSettingsStore"/> because it answers a different question — what is
/// running right now, rather than what is configured — and because together the two seams are everything
/// <c>SparkController</c> needs from <see cref="SparkService"/>. Depending on the interfaces rather than the
/// concrete singleton is what makes the controller constructible in a test, which is how its store-scoping
/// guard is covered.
/// </remarks>
public interface ISparkStoreRuntime
{
    /// <summary>Absolute path of the store's SDK storage directory.</summary>
    string GetStorageDirectory(string storeId);

    /// <summary>The store's live SDK handle, or null when it has no running instance.</summary>
    Task<ISparkSdkClient?> GetSdkClientAsync(string storeId);
}
