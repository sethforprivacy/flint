using System.Threading;
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

    /// <summary>
    /// A publishable name for the wallet the store is running, or null when it runs none — or, given
    /// <paramref name="sdk"/>, when that handle is no longer the one it runs. Never starts anything: it is read on
    /// the checkout's synchronous path.
    /// </summary>
    /// <remarks>
    /// The same recovery phrase gives the same tag across restarts, and a different phrase a different one, so a
    /// tag recorded beside a deposit address says whether that address still pays the store's wallet.
    /// </remarks>
    string? GetWalletTag(string storeId, ISparkSdkClient? sdk = null);

    /// <summary>
    /// Imports every backup queued for this store (see <see cref="IExitStateBackupStore.AddPendingAsync"/>)
    /// into its running wallet now, and reports what happened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What a paste calls, so a pasted backup is in the wallet when the page says so.</b> Deferring the
    /// import to the next restart was justified as avoiding a race with the running SDK — but the connect
    /// already imports into a running SDK, whose import is built to merge into a live wallet, and a backup
    /// that waits for a restart is a backup whose fate the operator cannot see.
    /// </para>
    /// <para>
    /// A queued backup is removed only once an import of it has returned. One that fails stays queued and is
    /// retried at every connect; the automatic pass never touches the queue. Never throws: every failure is
    /// in the report.
    /// </para>
    /// </remarks>
    Task<ExitStateImportReport> ImportPendingExitStateAsync(string storeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports the running wallet's exit state now, and stores it as the automatic backup when that is safe.
    /// </summary>
    /// <remarks>
    /// The page's Export button. It does not store while the wallet's startup import is still in flight —
    /// that import may be reading the very file this would replace — and says so in the result rather than
    /// waiting. Never throws.
    /// </remarks>
    Task<ExitStateExportResult> ExportExitStateAsync(string storeId, CancellationToken cancellationToken = default);
}

/// <summary>How an attempt to import a store's queued exit-state backups ended.</summary>
public enum ExitStateImportOutcome
{
    /// <summary>Nothing was queued.</summary>
    NothingPending,

    /// <summary>Something is queued, but the store has no running wallet; it is imported at the next connect.</summary>
    WalletNotRunning,

    /// <summary>Another import for this store was still running; what is queued stays queued.</summary>
    Busy,

    /// <summary>Every queued backup was imported (which may still have restored nothing — see the counts).</summary>
    Imported,

    /// <summary>At least one queued backup could not be imported; it stays queued for the next connect.</summary>
    Failed
}

/// <summary>
/// What <see cref="ISparkStoreRuntime.ImportPendingExitStateAsync"/> did, in counts — never content.
/// </summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Imported">How many queued backups were imported and left the queue.</param>
/// <param name="Failed">How many could not be, and stay queued.</param>
/// <param name="RestoredLeaves">Leaves restored, summed over every import that returned.</param>
/// <param name="ForeignLeaves">Leaves skipped as another wallet's.</param>
/// <param name="ConflictingLeaves">Leaves refused because they disagree with data the wallet holds.</param>
/// <param name="SkippedChains">Leaves the wallet already had usable data for.</param>
/// <param name="Reason">For a failure, a merchant-facing sentence that carries nothing of the blob.</param>
public sealed record ExitStateImportReport(
    ExitStateImportOutcome Outcome,
    int Imported = 0,
    int Failed = 0,
    uint RestoredLeaves = 0,
    uint ForeignLeaves = 0,
    uint ConflictingLeaves = 0,
    uint SkippedChains = 0,
    string? Reason = null)
{
    public static readonly ExitStateImportReport NothingPending = new(ExitStateImportOutcome.NothingPending);
}

/// <summary>What <see cref="ISparkStoreRuntime.ExportExitStateAsync"/> produced.</summary>
/// <param name="ExitState">The exported blob, for one render only; null when the export failed.</param>
/// <param name="Stored">Whether it was also stored as the automatic backup.</param>
/// <param name="Error">Why there is no blob — merchant-facing, and carrying nothing of one.</param>
/// <param name="NotStoredReason">When there is a blob but it was not stored, why.</param>
public sealed record ExitStateExportResult(
    string? ExitState,
    bool Stored,
    string? Error = null,
    string? NotStoredReason = null);
