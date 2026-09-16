using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// The one path by which a store quotes, funds and builds a unilateral exit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every guard is here.</b> The controller renders, redirects and decides nothing; the feature gate is
/// re-checked in every method because a controller's 404 is a courtesy and not the enforcement, and the
/// disclosure is re-read from storage before each write because a checkbox enforced in a view is enforced
/// nowhere — the same arrangement <see cref="SparkStableBalanceService"/> uses for a comparably irreversible
/// action.
/// </para>
/// <para>
/// <b>Nothing here broadcasts, and that is what makes the failure modes benign.</b> Every refusal and every
/// exception below has moved no coins: the SDK builds and signs and stops. What can be lost is the signed
/// transaction set itself, which exists only in <see cref="UnilateralExitRecord.TransactionsJson"/> — so a
/// failure to persist a successful build is logged as an error with the txids, is reported as a failure even
/// though the SDK call succeeded, and is never skipped because the operator's browser went away.
/// </para>
/// <para>
/// <b>One exit operation at a time per store</b>, held in <see cref="_running"/> exactly as
/// <see cref="SparkSweepEngine"/> holds a sweep pass. Two of these must never overlap for a reason stronger than
/// tidiness: they would race the same funding UTXO, and a second build could spend an output the first has
/// already committed to signed transactions. The gate also
/// covers the two settings writes, because storing settings tears down and reconnects the store's SDK handle —
/// pulling it out from under a build in flight. It is an in-process gate, so the durable half of the same rule
/// lives in the database: see <see cref="IUnilateralExitRecordStore.CreateAsync"/> and the compare-and-set on
/// <see cref="IUnilateralExitRecordStore.UpdateAsync"/>.
/// </para>
/// <para>
/// <b>This service is the only reader and writer of the record's JSON columns.</b> Leaf ids, funding UTXOs and
/// transactions are written with default <see cref="System.Text.Json"/> settings — exact property names, numeric
/// enum values — and read back with the same options, so the write format has exactly one owner. The enum orders
/// in <see cref="SparkExitTxKind"/> and <see cref="SparkExitTxStatus"/> are documented as fixed for this reason.
/// Callers get typed data out of <see cref="ReadAsync"/> and never see the blobs.
/// </para>
/// </remarks>
public sealed class SparkUnilateralExitService : ISparkUnilateralExitService
{
    /// <summary>How many past exits the page lists. Small: this is a last-resort tool, not a ledger.</summary>
    internal const int HistoryLimit = 20;

    internal const long MinFeeRateSatPerVbyte = 1;

    /// <summary>
    /// The highest fee rate a quote may be taken at.
    /// </summary>
    /// <remarks>
    /// A backstop against a typo, not an opinion about the fee market. The rate multiplies across every
    /// transaction in the tree — dozens of them — so a mistyped rate is not one overpriced transaction, it is an
    /// overpriced exit and a funding requirement to match.
    /// </remarks>
    internal const long MaxFeeRateSatPerVbyte = 500;

    /// <summary>
    /// The rate the quote form opens at when no recommendation could be had.
    /// </summary>
    /// <remarks>
    /// Two sat/vB, and it is a floor rather than a guess: one is the lowest rate this plugin will quote at all and
    /// is also the usual mempool minimum, so a quote priced at exactly one sits on the boundary where a small rise
    /// in the minimum makes every transaction in the tree non-relayable — and the exit is a chain of dozens, so a
    /// rate that stops relaying halfway is the failure mode this whole flow is built to avoid. Two clears that
    /// boundary while still costing an operator almost nothing, which is the right way to be wrong when the
    /// alternative is no number at all. It is used off mainnet with no explorer override, and whenever the
    /// explorer cannot be read — the operator sees a usable rate and can always type another one.
    /// </remarks>
    internal const long DefaultFeeRateSatPerVbyte = 2;

    internal const string FeatureDisabled =
        "Unilateral exit is not enabled on this server.";

    internal const string NotConfigured =
        "Flint is not set up for this store.";

    internal const string WalletNotRunning =
        "This store's Spark wallet is not running, so nothing can be quoted or built.";

    internal const string DisclosureRequired =
        "Confirm that you have read what a unilateral exit involves. It is a last resort: the transactions are "
        + "broadcast by hand, the funds are locked behind timelocks measured in days, and the on-chain fees are "
        + "paid up front from a separate funding address.";

    internal const string OperationInFlight =
        "Another unilateral-exit operation for this store is already running. Try again in a moment.";

    internal const string NothingWorthExiting =
        "No leaves are large enough to exit properly at the selected fee-rate. Please reduce the fee-rate and "
        + "try again, or wait for fees to drop so you can do so.";

    internal const string ExitNotFound =
        "This store has no exit with that reference.";

    internal const string ExitAlreadyInProgress =
        "This store already has an exit in progress. Finish or abandon it before quoting another: two exits would "
        + "compete for the same leaves, and only one of the two sets of transactions could ever be broadcast.";

    /// <summary>
    /// A compare-and-set lost its race: the row moved between being read and being written.
    /// </summary>
    /// <remarks>
    /// Reachable from two browser tabs, or from a second server behind the same database. Worth its own message
    /// rather than a generic failure, because nothing is broken and reloading shows the operator what happened.
    /// </remarks>
    internal const string ExitChangedUnderneath =
        "This exit changed while that was being done, so nothing was applied. Reload the page to see its current "
        + "state.";

    /// <summary>
    /// The build's pre-check and its veto share this: the leaf set the operator funded for is gone.
    /// </summary>
    internal const string LeavesGone =
        "The leaves this exit was quoted for are no longer in this wallet, so there is nothing left to force "
        + "on-chain. Abandon this exit and quote a new one.";

    internal const string BuiltButNotSaved =
        "The exit was built, but its signed transactions could not be saved, so they are lost. Nothing was "
        + "broadcast. Try again.";

    /// <summary>
    /// Largest exit-state backup the plugin will store, in characters.
    /// </summary>
    /// <remarks>
    /// A backstop against a wrong paste, not a format check. The SDK documents a real wallet's export as reaching
    /// several megabytes, so sixteen is generous by design: the cap has to be far above any true value, because
    /// refusing a genuine backup costs an operator the one copy of data they cannot get back — and accepting a
    /// paste that is not a backup costs only the storage. It exists at all because a pasted page or a hex dump can
    /// be tens of megabytes, and a settings blob that size breaks every later settings read.
    /// </remarks>
    internal const int MaxExitStateBackupChars = 16 * 1024 * 1024;

    /// <summary>
    /// A funding key index that is not a BIP32 address index. Only reachable from a hand-edited row.
    /// </summary>
    internal const string FundingIndexUnusable =
        "This exit's funding key index is outside the range a key can be derived at, so its funding address "
        + "cannot be reproduced. Abandon it and quote a new one.";

    private static readonly JsonSerializerOptions JsonOptions = new();

    /// <summary>
    /// What the page data looks like when there is no feature, or no Flint on this store.
    /// </summary>
    /// <remarks>
    /// Spelled out once rather than at each return, because a positional record of twelve members is exactly the
    /// shape where two "empty" literals drift apart from one another.
    /// </remarks>
    private static UnilateralExitPageData AbsentFeature =>
        new(false, false, 0, null, null, [], null, null, null, null, null, false, null);

    private readonly ISparkStoreSettingsStore _settingsStore;
    private readonly ISparkStoreRuntime _runtime;
    private readonly IUnilateralExitRecordStore _records;
    private readonly SparkMnemonicProtector _mnemonicProtector;
    private readonly SparkExitFundingExplorer _explorer;
    private readonly Network _network;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SparkUnilateralExitService> _logger;

    /// <summary>
    /// Where a stored exit-state backup actually lives: one owner-only file per store, outside the
    /// settings blob. The settings write below is gone from this path deliberately — a several-megabyte
    /// value in the store's settings is deserialized on every settings read, and storing one there
    /// tore down and reconnected the store's wallet.
    /// </summary>
    private readonly IExitStateBackupStore _backups;

    /// <summary>
    /// Stores with an exit operation in progress. Membership is the lock, and there is deliberately no queueing:
    /// see the class remarks.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _running = new();

    /// <param name="network">
    /// The chain this server runs on, resolved once at registration because it is fixed for the life of the
    /// process. Null coalesces to mainnet rules, matching <see cref="SweepDestinationResolver"/>: on a chain the
    /// SDK does not support no wallet starts at all, so nothing here is reachable, and failing DI would hide the
    /// clearer error.
    /// </param>
    public SparkUnilateralExitService(
        ISparkStoreSettingsStore settingsStore,
        ISparkStoreRuntime runtime,
        IUnilateralExitRecordStore records,
        SparkMnemonicProtector mnemonicProtector,
        SparkExitFundingExplorer explorer,
        IExitStateBackupStore backups,
        Network? network,
        TimeProvider timeProvider,
        ILogger<SparkUnilateralExitService> logger)
    {
        _settingsStore = settingsStore;
        _runtime = runtime;
        _records = records;
        _mnemonicProtector = mnemonicProtector;
        _explorer = explorer;
        _backups = backups;
        _network = network ?? Network.Main;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private bool Mainnet => _network == Network.Main;

    /// <inheritdoc />
    public async Task<UnilateralExitPageData> ReadAsync(
        string storeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        // Feature-off reads as "there is no such feature": no wallet, no history, nothing acknowledged. The page
        // is unreachable anyway, and a read that reported a store's real acknowledgement through a disabled
        // feature would be a surface the gate does not cover.
        if (!Constants.UnilateralExitEnabled)
            return AbsentFeature;

        var settings = await _settingsStore.GetAsync(storeId).ConfigureAwait(false);
        if (settings is null)
            return AbsentFeature;

        var exitSettings = settings.UnilateralExit ?? new UnilateralExitSettings();

        var sdk = await _runtime.GetSdkClientAsync(storeId).ConfigureAwait(false);
        var balance = 0L;
        if (sdk is not null)
        {
            try
            {
                // Cached read: this is a request thread, and the balance is context next to the quote form rather
                // than an input to any decision — the quote itself walks the wallet's own tree.
                var info = await sdk.GetInfoAsync(ensureSynced: false, cancellationToken).ConfigureAwait(false);
                balance = info.BalanceSats;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Store {StoreId}: could not read its Spark balance for the exit page ({Reason})",
                    storeId, SparkErrors.Describe(ex));
            }
        }

        var active = await _records.GetActiveForStoreAsync(storeId, cancellationToken).ConfigureAwait(false);
        var history = await _records
            .ListTerminalForStoreAsync(storeId, HistoryLimit, cancellationToken)
            .ConfigureAwait(false);

        var funding = SparkExitFundingBalance.Unknown("no exit is awaiting funding");
        if (active is { Status: UnilateralExitStatus.AwaitingFunding })
        {
            // Only while funding is what the operator is waiting on. Once the exit is built the UTXO has been
            // committed to signed transactions, and reporting a balance for it would invite a top-up that helps
            // nothing.
            funding = await ReadFundingAsync(active, exitSettings, cancellationToken).ConfigureAwait(false);
        }

        int? leafCount = null;
        string? keyPath = null;
        if (active is not null)
        {
            leafCount = DeserializeLeafIds(active).Count;
            keyPath = DescribeKeyPath(active);
        }

        // Only when the quote form will actually render, which is only when nothing is in flight. A store with an
        // exit already awaiting funding or built has its rate on the record, so asking the explorer for a market
        // rate here would be an external round trip on every page view that decides nothing — and the exit page is
        // reachable from any store view. Null, not the fallback, when there is no recommendation: the fallback is
        // the caller's decision, and a service that returned a market rate it did not have would be claiming one.
        long? recommendedFeeRate = null;
        if (active is null)
        {
            recommendedFeeRate = await _explorer
                .RecommendFeeRateSatPerVbyteAsync(Mainnet, exitSettings, cancellationToken)
                .ConfigureAwait(false);
        }

        // Read back and checked here rather than anywhere above: the page renders these, and a malformed column
        // has to become an explanation on the page instead of an exception in a view.
        var readable = TryReadTransactions(active, out var transactions);

        // Derived here rather than left to the view. A built exit runs to a dozen rows of which at most one or
        // two are actionable at any moment, so "send these now" is the one thing the page has to say about the
        // set — and having it come from the same read that produced the list means the two cannot disagree.
        //
        // Null, not empty, when there is nothing to send: the page distinguishes "no built set yet" from "a set
        // that is entirely waiting on timelocks", and collapsing those two would put a materialised empty
        // instruction block on a record that has never been built. Null when the set was unreadable as well —
        // there is nothing trustworthy to filter.
        var pending = readable && transactions is { Count: > 0 }
            ? transactions.Where(transaction => transaction.Status.CanBroadcast).ToArray()
            : null;

        return new UnilateralExitPageData(
            sdk is not null,
            exitSettings.DisclosureAcknowledged,
            balance,
            recommendedFeeRate,
            active,
            history,
            funding.TotalSat,
            funding.LargestOutputSat,
            leafCount,
            keyPath,
            transactions,
            !readable,
            pending);
    }

    /// <inheritdoc />
    public async Task<UnilateralExitOpResult> AcknowledgeDisclosureAsync(
        string storeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        if (!Constants.UnilateralExitEnabled)
            return Refuse(FeatureDisabled);

        if (!_running.TryAdd(storeId, 0))
            return Refuse(OperationInFlight);

        try
        {
            var settings = await _settingsStore.GetAsync(storeId).ConfigureAwait(false);
            if (settings is null)
                return Refuse(NotConfigured);

            if ((settings.UnilateralExit ?? new UnilateralExitSettings()).DisclosureAcknowledged)
                return new UnilateralExitOpResult(true, null, null);

            return await SaveExitSettingsAsync(
                    storeId,
                    settings,
                    exit => exit.DisclosureAcknowledged = true,
                    "the unilateral-exit disclosure acknowledgement",
                    "The acknowledgement")
                .ConfigureAwait(false);
        }
        finally
        {
            _running.TryRemove(storeId, out _);
        }
    }

    /// <inheritdoc />
    public async Task<UnilateralExitOpResult> SetExplorerUrlAsync(
        string storeId,
        string? esploraApiUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        if (!Constants.UnilateralExitEnabled)
            return Refuse(FeatureDisabled);

        // Blank clears it, which is the only way back to the mainnet default once an override has been set.
        string? normalised = null;
        if (!string.IsNullOrWhiteSpace(esploraApiUrl))
        {
            if (!SparkExitFundingExplorer.TryNormaliseApiUrl(esploraApiUrl, out normalised, out var fragment))
            {
                return Refuse(
                    "That block-explorer address cannot be used: " + fragment
                    + ". Give the base URL of an esplora-compatible API, for example "
                    + SparkExitFundingExplorer.MainnetDefaultApiUrl + ", or leave it empty to use the default.");
            }
        }

        if (!_running.TryAdd(storeId, 0))
            return Refuse(OperationInFlight);

        try
        {
            var settings = await _settingsStore.GetAsync(storeId).ConfigureAwait(false);
            if (settings is null)
                return Refuse(NotConfigured);

            var current = (settings.UnilateralExit ?? new UnilateralExitSettings()).EsploraApiUrl;
            if (string.Equals(current, normalised, StringComparison.Ordinal))
            {
                // No write for a press that changes nothing: storing settings tears down and reconnects the
                // store's wallet, which is not a thing to do to confirm the status quo.
                return new UnilateralExitOpResult(true, null, null);
            }

            return await SaveExitSettingsAsync(
                    storeId,
                    settings,
                    exit => exit.EsploraApiUrl = normalised,
                    "the unilateral-exit block-explorer URL",
                    "The block-explorer address")
                .ConfigureAwait(false);
        }
        finally
        {
            _running.TryRemove(storeId, out _);
        }
    }

    /// <inheritdoc />
    public async Task<UnilateralExitOpResult> CheckAsync(
        string storeId,
        string recordId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        if (!Constants.UnilateralExitEnabled)
            return Refuse(FeatureDisabled);

        if (string.IsNullOrWhiteSpace(recordId))
            return Refuse(ExitNotFound);

        // Held for the same reason the build holds it. Checking does not itself move money, but it writes the
        // refreshed transaction set back over the row, and a compare-and-set landing between a build's two writes
        // would let a check persist statuses for a set the build is in the middle of replacing.
        if (!_running.TryAdd(storeId, 0))
            return Refuse(OperationInFlight);

        try
        {
            var record = await _records.GetAsync(storeId, recordId, cancellationToken).ConfigureAwait(false);
            if (record is null)
                return Refuse(ExitNotFound);

            if (record.Status is not UnilateralExitStatus.Built)
            {
                return new UnilateralExitOpResult(
                    false,
                    record.Status is UnilateralExitStatus.Abandoned
                        ? "This exit was abandoned, so there is nothing to check on chain."
                        : "This exit has not been built yet, so there is nothing to check on chain.",
                    record);
            }

            // The status every compare-and-set below is guarded on, read once: the row must still be Built when
            // the refreshed set lands, because anything else means the operator finished or abandoned it while the
            // chain was being asked.
            var from = record.Status;

            if (!TryReadTransactions(record, out var stored) || stored is not { Count: > 0 })
            {
                // A built row whose set cannot be read has nothing to ask the chain about, and the SDK is handed
                // the set — so this is a refusal rather than a call that would report on an exit that is not the
                // one stored. The page reports the same condition from the same check.
                return await FailAsync(
                        record,
                        from,
                        "This exit's stored transactions could not be read back, so there is nothing to check "
                        + "against the chain. Abandon it and quote a new one.")
                    .ConfigureAwait(false);
            }

            // The SDK checks a whole exit response, and the record only stores the transactions — see
            // UnilateralExitRecord. The two totals and the leaf set it echoes back are not inputs to the check,
            // so the stored figures are passed through as they are rather than re-quoted: quoting is what this
            // read path must not do, because a check has to work when the wallet is gone.
            var stored2 = new SparkExitResult(
                record.RecoverableValueSat,
                record.TotalFeeSat,
                stored,
                DeserializeLeafIds(record).Select(id => new SparkExitLeaf(id, 0)).ToArray());

            // The client can be gone — the wallet failed to start, or was stopped while the exit sat built — and
            // reporting that as a chain failure would send the operator to look at the wrong thing. A built exit
            // is checkable from a store whose wallet is down, which is the case this feature exists for, so a null
            // client is a refusal here rather than a silent no-op.
            var sdk = await _runtime.GetSdkClientAsync(storeId).ConfigureAwait(false);
            if (sdk is null)
                return new UnilateralExitOpResult(false, WalletNotRunning, record);

            SparkExitProgress progress;
            try
            {
                progress = await sdk.CheckUnilateralExitAsync(stored2, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Store {StoreId}: could not check unilateral exit {ExitId} against the chain ({Reason})",
                    storeId, record.Id, SparkErrors.Describe(ex));

                return await FailAsync(
                        record,
                        from,
                        "Spark could not read the chain for this exit: " + SparkErrors.Describe(ex)
                        + ". The stored transactions are unchanged and nothing was broadcast.")
                    .ConfigureAwait(false);
            }

            // The statuses it reports replace the ones stored, which is the whole point of the call: an operator
            // reading the page afterwards sees what the chain says, not what it said when the set was built.
            record.TransactionsJson = JsonSerializer.Serialize(progress.Transactions.ToArray(), JsonOptions);
            record.UpdatedUtc = _timeProvider.GetUtcNow();

            // The verdict is not written to the row — it is derived state the SDK recomputes on every call, and a
            // stored copy would be rendered later as a live claim about a chain nothing has re-read. It travels
            // back on the result instead. The error is cleared on success for the same reason a build clears it.
            record.LastError = null;

            if (!await _records.UpdateAsync(record, from, CancellationToken.None).ConfigureAwait(false))
                return new UnilateralExitOpResult(false, ExitChangedUnderneath, record, progress.Verdict);

            _logger.LogInformation(
                "Store {StoreId}: checked unilateral exit {ExitId} against the chain: {Verdict}, {Ready} of "
                + "{Count} transactions ready to broadcast. Nothing was broadcast by this check",
                storeId, record.Id, progress.Verdict,
                progress.Transactions.Count(transaction => transaction.Status.CanBroadcast),
                progress.Transactions.Count);

            return new UnilateralExitOpResult(true, null, record, progress.Verdict);
        }
        finally
        {
            _running.TryRemove(storeId, out _);
        }
    }

    /// <inheritdoc />
    public async Task<UnilateralExitOpResult> SetExitStateBackupAsync(
        string storeId,
        string? exitState,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        if (!Constants.UnilateralExitEnabled)
            return Refuse(FeatureDisabled);

        // Blank clears it, so the only way back to "no backup configured" is the same form that set one.
        string? normalised = null;
        if (!string.IsNullOrWhiteSpace(exitState))
        {
            if (exitState.Length > MaxExitStateBackupChars)
            {
                // The only check made on the value, and it exists because the alternative is silently storing a
                // paste that will never import. The SDK documents a real wallet's backup as reaching several
                // megabytes, so anything past this cap is a wrong paste — a page's HTML, a hex dump of something
                // else — rather than a backup. The content itself is not checked: see the interface remarks.
                return Refuse(string.Format(
                    CultureInfo.InvariantCulture,
                    "That is {0:N0} characters, which is far larger than an exit-state backup can be, so it is "
                    + "not one. A real backup is a few megabytes at most. Nothing has been stored.",
                    exitState.Length));
            }

            normalised = exitState;
        }

        if (!_running.TryAdd(storeId, 0))
            return Refuse(OperationInFlight);

        try
        {
            var settings = await _settingsStore.GetAsync(storeId).ConfigureAwait(false);
            if (settings is null)
                return Refuse(NotConfigured);

            // Read the stored value only to answer "does this press change anything" — a full read of a
            // multi-megabyte blob, on a press an operator makes rarely at most, and what it buys is that a
            // re-paste cannot move the file's timestamp: a rewrite would report the backup as taken at a
            // moment when nothing was actually learned about the wallet. A failed read is not a refusal —
            // "unchanged" has to be earned from a read that answered, so this press just proceeds to its
            // own write, which is the same failure or success the comparison was guarding.
            string? current = null;
            var compared = false;
            try
            {
                current = await _backups.ReadAsync(storeId, cancellationToken).ConfigureAwait(false);
                compared = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Store {StoreId}: its stored exit-state backup could not be read before a save, so the "
                    + "write proceeded without comparing to what was there",
                    storeId);
            }

            // A clear never takes this shortcut. "Nothing stored" is what the file answers for a store whose
            // deprecated settings slot still holds a blob — upgraded while the feature was off, or not yet
            // reconnected since, or a file write that failed during adoption — and the press that empties
            // one location has to empty the other with it.
            if (compared && normalised is not null &&
                string.Equals(current, normalised, StringComparison.Ordinal))
            {
                // No write for a press that changes nothing, and the comparison is by value rather than
                // by reference so a re-paste of the same blob is also a no-op. This used to be required
                // because a settings write reconnected the wallet; it is kept because a write that moves
                // the TakenAt of a backup that did not change lies about the backup, not just about cost.
                return new UnilateralExitOpResult(true, null, null);
            }

            // The subject and the log's description deliberately say nothing about the value: it discloses
            // the store's balance and history, and this method is one of the two places in the plugin
            // that handles it.
            try
            {
                if (normalised is null)
                {
                    await _backups.DeleteAsync(storeId, cancellationToken).ConfigureAwait(false);

                    // Both locations, or the word "cleared" is a promise the next connect breaks:
                    // RestoreExitStateAsync adopts from the deprecated slot whenever the file is empty, so a
                    // store that still holds a value there takes back the blob this press removed — under a
                    // banner telling the operator a restart will import nothing.
                    await _settingsStore.ClearExitStateBackupSlotAsync(storeId).ConfigureAwait(false);

                    _logger.LogInformation(
                        "Store {StoreId}: the stored exit-state backup was cleared", storeId);
                }
                else
                {
                    await _backups.WriteAsync(storeId, normalised, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Store {StoreId}: stored an exit-state backup from the page ({Length} characters)",
                        storeId, normalised.Length);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Store {StoreId}: could not store {What} ({Reason})",
                    storeId,
                    normalised is null
                        ? "the unilateral-exit state backup being cleared"
                        : "a unilateral-exit state backup",
                    SparkErrors.Describe(ex));

                return Refuse($"The exit-state backup could not be saved: {SparkErrors.Describe(ex)}");
            }

            return new UnilateralExitOpResult(true, null, null);
        }
        finally
        {
            _running.TryRemove(storeId, out _);
        }
    }

    /// <inheritdoc />
    public async Task<UnilateralExitOpResult> QuoteAsync(
        string storeId,
        long feeRateSatPerVbyte,
        string destinationAddress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        if (!Constants.UnilateralExitEnabled)
            return Refuse(FeatureDisabled);

        var settings = await _settingsStore.GetAsync(storeId).ConfigureAwait(false);
        if (settings is null)
            return Refuse(NotConfigured);

        var exitSettings = settings.UnilateralExit ?? new UnilateralExitSettings();

        // The disclosure first, before the input checks: an operator who has not read what this costs them should
        // be told that rather than that their fee rate is out of range.
        if (!exitSettings.DisclosureAcknowledged)
            return Refuse(DisclosureRequired);

        if (feeRateSatPerVbyte is < MinFeeRateSatPerVbyte or > MaxFeeRateSatPerVbyte)
        {
            return Refuse(string.Format(
                CultureInfo.InvariantCulture,
                "The fee rate has to be between {0:N0} and {1:N0} sat/vB. Every transaction in the exit is built "
                + "at this one rate, so it also decides which leaves are worth exiting at all.",
                MinFeeRateSatPerVbyte,
                MaxFeeRateSatPerVbyte));
        }

        if (!TryParseDestination(destinationAddress, out var destination, out var destinationError))
            return Refuse(destinationError);

        if (!_running.TryAdd(storeId, 0))
            return Refuse(OperationInFlight);

        try
        {
            var active = await _records.GetActiveForStoreAsync(storeId, cancellationToken).ConfigureAwait(false);
            if (active is not null)
                return new UnilateralExitOpResult(false, ExitAlreadyInProgress, active);

            var sdk = await _runtime.GetSdkClientAsync(storeId).ConfigureAwait(false);
            if (sdk is null)
                return Refuse(WalletNotRunning);

            // Allocated before the derivation, because the index is what the derivation is for. One address per
            // exit: see UnilateralExitRecord.FundingKeyIndex for why reusing one is a trap rather than a saving.
            var nextIndex = await _records.NextFundingKeyIndexAsync(storeId, cancellationToken)
                .ConfigureAwait(false);

            if (!TryFundingKeyIndex(nextIndex, out var keyIndex))
            {
                _logger.LogError(
                    "Store {StoreId}: its next exit funding key index ({Index}) is outside the BIP32 range",
                    storeId, nextIndex);
                return Refuse(FundingIndexUnusable);
            }

            // Derived before the quote, and disposed immediately. The address is all a quote needs — the private
            // half is a build's business — and deriving first means a store whose seed cannot be decrypted is
            // refused without an SDK round trip.
            string fundingAddress;
            using (var derived = DeriveFundingKey(settings, keyIndex, out var keyError))
            {
                if (derived is null)
                    return Refuse(keyError!);
                fundingAddress = derived.Address;
            }

            SparkExitQuote quote;
            try
            {
                quote = await sdk
                    .PrepareUnilateralExitAsync(
                        (ulong)feeRateSatPerVbyte, destination, leafIds: null, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Store {StoreId}: could not quote a unilateral exit ({Reason})",
                    storeId, SparkErrors.Describe(ex));

                return Refuse(
                    "Spark could not quote a unilateral exit: " + SparkErrors.Describe(ex)
                    + ". Nothing has been recorded, so trying again is safe.");
            }

            // Not an error. Auto selection returns nothing whenever no leaf clears the fee rate, and the honest
            // report is that there is nothing worth doing rather than that something failed.
            if (quote.IsEmpty)
                return Refuse(NothingWorthExiting);

            if (quote.RecoverableValueSat <= quote.TotalFeeSat)
            {
                return Refuse(string.Format(
                    CultureInfo.InvariantCulture,
                    "This exit would cost more than it recovers: {0:N0} sat of fees against {1:N0} sat of value. "
                    + "Nothing has been recorded. A lower fee rate may change the arithmetic.",
                    quote.TotalFeeSat,
                    quote.RecoverableValueSat));
            }

            var now = _timeProvider.GetUtcNow();
            var record = new UnilateralExitRecord
            {
                Id = Guid.NewGuid().ToString(),
                StoreId = storeId,
                Status = UnilateralExitStatus.AwaitingFunding,
                CreatedUtc = now,
                UpdatedUtc = now,
                DestinationAddress = destination,
                FeeRateSatPerVbyte = feeRateSatPerVbyte,
                // Pinned here and never rewritten: the build re-quotes these exact leaves, so the operator cannot
                // end up funding one exit and building another.
                LeafIdsJson = JsonSerializer.Serialize(
                    quote.Leaves.Select(leaf => leaf.LeafId).ToArray(), JsonOptions),
                RecoverableValueSat = quote.RecoverableValueSat,
                TotalFeeSat = quote.TotalFeeSat,
                SingleUtxoFundingSat = quote.SingleUtxoFundingSat,
                FundingAddress = fundingAddress,
                FundingKeyIndex = keyIndex
            };

            bool created;
            try
            {
                created = await _records.CreateAsync(record, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The correct direction to fail in: the operator is never shown a funding address for an exit
                // that was not recorded, because sats on an unrecorded funding address are only recoverable by
                // re-deriving the key by hand.
                _logger.LogError(ex, "Store {StoreId}: could not record a quoted unilateral exit", storeId);
                return Refuse("The quote could not be recorded, so no funding address has been issued.");
            }

            if (!created)
            {
                // The database's own single-flight guard fired: something inserted an active exit between the
                // check above and this insert. Reported as the same refusal, with the row that won.
                var winner = await _records.GetActiveForStoreAsync(storeId, cancellationToken)
                    .ConfigureAwait(false);
                return new UnilateralExitOpResult(false, ExitAlreadyInProgress, winner);
            }

            _logger.LogInformation(
                "Store {StoreId}: quoted a unilateral exit of {Leaves} leaves worth {Recoverable} sat at "
                + "{FeeRate} sat/vB; it needs {Funding} sat on {FundingAddress}",
                storeId, quote.Leaves.Count, quote.RecoverableValueSat, feeRateSatPerVbyte,
                quote.SingleUtxoFundingSat, fundingAddress);

            return new UnilateralExitOpResult(true, null, record);
        }
        finally
        {
            _running.TryRemove(storeId, out _);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>The order of the steps below is the fix for a deadlock, not a preference.</b> A quote's funding
    /// requirement moves with the fee market and with the wallet's tree, and the build's own veto judges the
    /// funding output against a quote taken inside the SDK call. If the output were selected against the figure
    /// the record was created with, an operator who topped up to exactly the amount the veto demanded would find
    /// that top-up ignored — selection would keep picking the smaller output that satisfied the stale figure, and
    /// the veto would keep refusing it, for ever. So this re-quotes first, persists the fresh requirement so that
    /// the number on the page is the number that will be judged, and only then selects.
    /// </remarks>
    public async Task<UnilateralExitOpResult> BuildAsync(
        string storeId,
        string recordId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        if (!Constants.UnilateralExitEnabled)
            return Refuse(FeatureDisabled);

        if (string.IsNullOrWhiteSpace(recordId))
            return Refuse(ExitNotFound);

        var settings = await _settingsStore.GetAsync(storeId).ConfigureAwait(false);
        if (settings is null)
            return Refuse(NotConfigured);

        var exitSettings = settings.UnilateralExit ?? new UnilateralExitSettings();

        // Re-checked here and not only at quote time. This is the call that produces signed transactions
        // spending the store's balance, and a gate only one entry point enforces is a gate with a bypass.
        if (!exitSettings.DisclosureAcknowledged)
            return Refuse(DisclosureRequired);

        if (!_running.TryAdd(storeId, 0))
            return Refuse(OperationInFlight);

        try
        {
            var record = await _records.GetAsync(storeId, recordId, cancellationToken).ConfigureAwait(false);
            if (record is null)
                return Refuse(ExitNotFound);

            if (!record.IsActive)
            {
                return new UnilateralExitOpResult(
                    false,
                    "This exit is finished. Quote a new one rather than building this one again.",
                    record);
            }

            // The status every compare-and-set below is guarded on: whatever this row was when it was read is
            // what all of the following decisions are about.
            var from = record.Status;

            if (record.FeeRateSatPerVbyte is < MinFeeRateSatPerVbyte or > MaxFeeRateSatPerVbyte)
            {
                // Only reachable from a hand-edited row: the quote guard bounds this before it is ever stored. It
                // is checked again because the value is cast to an unsigned rate on the way to the SDK, where a
                // negative would arrive as an astronomical one.
                return await FailAsync(
                        record,
                        from,
                        "This exit's fee rate is out of range, so it cannot be built. Abandon it and quote a new "
                        + "one.")
                    .ConfigureAwait(false);
            }

            var leafIds = DeserializeLeafIds(record);
            if (leafIds.Count == 0)
            {
                return await FailAsync(
                        record,
                        from,
                        "This exit's leaf selection could not be read, so it cannot be rebuilt. Abandon it and "
                        + "quote a new one.")
                    .ConfigureAwait(false);
            }

            if (!TryFundingKeyIndex(record.FundingKeyIndex, out var keyIndex))
                return await FailAsync(record, from, FundingIndexUnusable).ConfigureAwait(false);

            var sdk = await _runtime.GetSdkClientAsync(storeId).ConfigureAwait(false);
            if (sdk is null)
                return new UnilateralExitOpResult(false, WalletNotRunning, record);

            using var funding = DeriveFundingKey(settings, keyIndex, out var keyError);
            if (funding is null)
                return await FailAsync(record, from, keyError!).ConfigureAwait(false);

            // The funding address is stored rather than re-derived for display, so the two can disagree — a
            // replaced seed, a different network. If they do, the plugin no longer holds the key to the output the
            // operator funded, and building against a key that cannot sign it would fail deep inside the SDK.
            if (!string.Equals(funding.Address, record.FundingAddress, StringComparison.Ordinal))
            {
                return await FailAsync(
                        record,
                        from,
                        "This store's seed no longer derives the funding address this exit was quoted against, so "
                        + "the plugin cannot spend what was sent there. Abandon this exit and quote a new one; the "
                        + "old funding is recoverable from the original seed at "
                        + $"{DescribeKeyPath(record)}.")
                    .ConfigureAwait(false);
            }

            if (!SparkExitFundingExplorer.TryResolveBaseUrl(exitSettings, Mainnet, out var baseUrl, out var urlError))
                return await FailAsync(record, from, urlError!).ConfigureAwait(false);

            // Step one: re-quote the record's own leaves. This happens before funding is even looked at, because
            // its answer is what the funding has to satisfy — see the remarks on this method.
            SparkExitQuote fresh;
            try
            {
                fresh = await sdk
                    .PrepareUnilateralExitAsync(
                        (ulong)record.FeeRateSatPerVbyte,
                        record.DestinationAddress,
                        leafIds,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Store {StoreId}: could not re-quote unilateral exit {ExitId} ({Reason})",
                    storeId, record.Id, SparkErrors.Describe(ex));

                return await FailAsync(
                        record,
                        from,
                        "Spark could not re-price this exit: " + SparkErrors.Describe(ex)
                        + ". Nothing was signed, so trying again is safe.")
                    .ConfigureAwait(false);
            }

            if (fresh.IsEmpty)
            {
                return await FailAsync(record, from, LeavesGone).ConfigureAwait(false);
            }

            if (fresh.RecoverableValueSat <= fresh.TotalFeeSat)
            {
                return await FailAsync(record, from, DescribeUneconomic(fresh)).ConfigureAwait(false);
            }

            // Step two: persist the fresh figures before selecting against them, so the requirement the operator
            // reads on the page and the requirement the selection uses are the same number. Written even though
            // the build may still fail — especially then, because a failed attempt's whole value to the operator
            // is telling them what to fund.
            ApplyQuote(record, fresh);
            record.LastError = null;
            record.UpdatedUtc = _timeProvider.GetUtcNow();

            if (!await _records.UpdateAsync(record, from, cancellationToken).ConfigureAwait(false))
                return new UnilateralExitOpResult(false, ExitChangedUnderneath, record);

            var required = fresh.SingleUtxoFundingSat;

            var lookup = await _explorer
                .ListConfirmedAsync(baseUrl!, record.FundingAddress, funding.PubkeyHex, cancellationToken)
                .ConfigureAwait(false);

            if (lookup.Utxos is not { } confirmed)
                return await FailAsync(record, from, lookup.Error!).ConfigureAwait(false);

            var largest = confirmed.Count == 0 ? 0 : confirmed.Max(utxo => utxo.ValueSat);

            // One output, not a sum. CPFP funding spends a single P2WPKH outpoint, so two outputs each half the
            // required size do not fund the exit however encouraging their total looks — which is exactly why the
            // funding instructions say "as one output" and why the check is not against the balance.
            //
            // The smallest output that suffices, so an operator who over-funded (or funded twice) keeps the larger
            // one intact for a later attempt rather than having it committed to this one.
            var chosen = confirmed
                .Where(utxo => utxo.ValueSat >= required)
                .OrderBy(utxo => utxo.ValueSat)
                .FirstOrDefault();

            if (chosen is null)
            {
                return await FailAsync(
                        record,
                        from,
                        DescribeShortfall(required, record.FundingAddress, confirmed))
                    .ConfigureAwait(false);
            }

            SparkExitResult result;
            SparkExitQuote? committed = null;
            try
            {
                result = await sdk
                    .UnilateralExitAsync(
                        (ulong)record.FeeRateSatPerVbyte,
                        record.DestinationAddress,
                        leafIds,
                        [chosen],
                        funding.Secret,
                        second =>
                        {
                            // The veto, against the quote the SDK took inside this call. A unilateral-exit quote
                            // does not expire — it goes stale silently as the wallet's tree moves — so this is
                            // the last point at which the arithmetic is authoritative. It can differ again from
                            // the quote taken moments ago, which is why its own requirement is re-checked and
                            // then persisted by the catch below.
                            committed = second;

                            if (second.IsEmpty)
                                return LeavesGone;

                            if (second.RecoverableValueSat <= second.TotalFeeSat)
                                return DescribeUneconomic(second);

                            if (second.SingleUtxoFundingSat > chosen.ValueSat)
                            {
                                return string.Format(
                                    CultureInfo.InvariantCulture,
                                    "This exit now needs {0:N0} sat as a single confirmed output, and the largest "
                                    + "one on the funding address holds {1:N0} sat. Send at least the full "
                                    + "required amount as a single new output and try again once it confirms.",
                                    second.SingleUtxoFundingSat,
                                    largest);
                            }

                            return null;
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SparkExitRefusedException refused)
            {
                // The veto above. Already written for a merchant, so it is passed through verbatim — and the
                // quote it judged by is persisted, so the next attempt selects against the same requirement the
                // operator was just asked to fund.
                ApplyQuote(record, committed);
                return await FailAsync(record, from, refused.Reason).ConfigureAwait(false);
            }
            catch (SparkExitFundingShortfallException shortfall)
            {
                ApplyQuote(record, committed);
                return await FailAsync(record, from, shortfall.Message).ConfigureAwait(false);
            }
            // No catch for a funding conflict, and its absence is the 0.25 change: the SDK removed
            // FundingUtxoConflict entirely, because a spent funding output is no longer an error — it follows
            // each outpoint to whatever it became and accepts fresh funding alongside it. An empty catch here
            // would swallow a real failure into a message about a condition the SDK can no longer report.
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Store {StoreId}: could not build unilateral exit {ExitId} ({Reason})",
                    storeId, record.Id, SparkErrors.Describe(ex));

                ApplyQuote(record, committed);
                return await FailAsync(
                        record,
                        from,
                        "Spark could not build this exit: " + SparkErrors.Describe(ex)
                        + ". Nothing was signed or broadcast, so trying again is safe.")
                    .ConfigureAwait(false);
            }

            // Past this point the request's cancellation token is deliberately never used again. The SDK has
            // returned signed transactions that exist in this process and nowhere else, and it will not hand them
            // back without a fresh build and a fresh funding output — so a browser that went away must not be
            // able to skip the write that saves them.
            record.Status = UnilateralExitStatus.Built;
            record.UpdatedUtc = _timeProvider.GetUtcNow();
            record.RecoverableValueSat = result.RecoverableValueSat;
            record.TotalFeeSat = result.TotalFeeSat;
            record.SingleUtxoFundingSat = committed?.SingleUtxoFundingSat ?? record.SingleUtxoFundingSat;
            record.FundingUtxosJson = JsonSerializer.Serialize(new[] { chosen }, JsonOptions);
            // Replaced with exactly what the SDK returned, however short that is. Since 0.25 the build continues
            // from confirmed chain state, so a partial set is the normal result of a resumed attempt and an empty
            // one means everything it planned has already been seen on-chain. Merging the previous set back in
            // would be actively wrong: a transaction missing from the new set is one the SDK has already observed
            // confirm, or one its own re-plan dropped, and keeping a stale copy of it next to the new set invites
            // an operator to broadcast a transaction the SDK has already superseded.
            record.TransactionsJson = JsonSerializer.Serialize(result.Transactions.ToArray(), JsonOptions);
            // Cleared, not left in place: a build that got further must not show the failed attempt's complaint
            // next to its own transactions.
            record.LastError = null;

            bool persisted;
            try
            {
                persisted = await _records
                    .UpdateAsync(record, from, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Wrapped rather than allowed to propagate, so the txids reach the log on the one failure where
                // the log is the last copy of them.
                LogUnsavedBuild(storeId, record, result, ex);
                return new UnilateralExitOpResult(false, BuiltButNotSaved, record);
            }

            if (!persisted)
            {
                LogUnsavedBuild(storeId, record, result, null);
                return new UnilateralExitOpResult(false, BuiltButNotSaved, record);
            }

            // Wording that reads correctly when the set is empty, which is now a normal outcome rather than a fault:
            // "built ... : 0 signed transactions (everything it planned is already on chain)". A count that only
            // made sense above zero would have an operator reading a successful resumed build as a broken one.
            _logger.LogInformation(
                "Store {StoreId}: built unilateral exit {ExitId}: {Count} signed transactions recovering "
                + "{Recoverable} sat for {Fee} sat in fees{Note}. Nothing has been broadcast",
                storeId, record.Id, result.Transactions.Count, result.RecoverableValueSat, result.TotalFeeSat,
                result.Transactions.Count == 0
                    ? " (the set is empty, so everything this build planned is already confirmed on chain)"
                    : string.Empty);

            return new UnilateralExitOpResult(true, null, record);
        }
        finally
        {
            _running.TryRemove(storeId, out _);
        }
    }

    /// <inheritdoc />
    public async Task<UnilateralExitOpResult> MarkCompletedAsync(
        string storeId,
        string recordId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        if (!Constants.UnilateralExitEnabled)
            return Refuse(FeatureDisabled);

        if (string.IsNullOrWhiteSpace(recordId))
            return Refuse(ExitNotFound);

        // Held for the same reason abandoning is: this frees the store for a new quote, and doing that under a
        // build in flight would let the next quote start while the first exit is still committing to its output.
        if (!_running.TryAdd(storeId, 0))
            return Refuse(OperationInFlight);

        try
        {
            var record = await _records.GetAsync(storeId, recordId, cancellationToken).ConfigureAwait(false);
            if (record is null)
                return Refuse(ExitNotFound);

            if (record.Status is UnilateralExitStatus.Completed)
                return new UnilateralExitOpResult(true, null, record);

            if (record.Status is not UnilateralExitStatus.Built)
            {
                return new UnilateralExitOpResult(
                    false,
                    record.Status is UnilateralExitStatus.Abandoned
                        ? "This exit was abandoned, so there is nothing to mark as finished."
                        : "This exit has not been built yet, so there is nothing to mark as finished.",
                    record);
            }

            record.Status = UnilateralExitStatus.Completed;
            record.UpdatedUtc = _timeProvider.GetUtcNow();

            if (!await _records
                    .UpdateAsync(record, UnilateralExitStatus.Built, cancellationToken)
                    .ConfigureAwait(false))
            {
                return new UnilateralExitOpResult(false, ExitChangedUnderneath, record);
            }

            _logger.LogInformation(
                "Store {StoreId}: unilateral exit {ExitId} marked completed by the operator. The plugin watches "
                + "no chain, so this is their statement rather than an observation",
                storeId, record.Id);

            return new UnilateralExitOpResult(true, null, record);
        }
        finally
        {
            _running.TryRemove(storeId, out _);
        }
    }

    /// <inheritdoc />
    public async Task<UnilateralExitOpResult> AbandonAsync(
        string storeId,
        string recordId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        if (!Constants.UnilateralExitEnabled)
            return Refuse(FeatureDisabled);

        if (string.IsNullOrWhiteSpace(recordId))
            return Refuse(ExitNotFound);

        // Held even though abandoning moves nothing: a row marked abandoned under a build in flight would let the
        // next quote start while the build is still committing to its funding output.
        if (!_running.TryAdd(storeId, 0))
            return Refuse(OperationInFlight);

        try
        {
            var record = await _records.GetAsync(storeId, recordId, cancellationToken).ConfigureAwait(false);
            if (record is null)
                return Refuse(ExitNotFound);

            if (record.Status is UnilateralExitStatus.Abandoned)
                return new UnilateralExitOpResult(true, null, record);

            if (record.Status is UnilateralExitStatus.Completed)
            {
                return new UnilateralExitOpResult(
                    false,
                    "This exit is already recorded as finished, so there is nothing to abandon.",
                    record);
            }

            var from = record.Status;
            record.Status = UnilateralExitStatus.Abandoned;
            record.UpdatedUtc = _timeProvider.GetUtcNow();

            if (!await _records.UpdateAsync(record, from, cancellationToken).ConfigureAwait(false))
                return new UnilateralExitOpResult(false, ExitChangedUnderneath, record);

            _logger.LogInformation(
                "Store {StoreId}: abandoned unilateral exit {ExitId}. Any transactions already broadcast remain "
                + "valid",
                storeId, record.Id);

            return new UnilateralExitOpResult(true, null, record);
        }
        finally
        {
            _running.TryRemove(storeId, out _);
        }
    }

    private static string DescribeUneconomic(SparkExitQuote quote) => string.Format(
        CultureInfo.InvariantCulture,
        "This exit now costs more than it recovers: {0:N0} sat of fees against {1:N0} sat of value. Nothing was "
        + "built.",
        quote.TotalFeeSat,
        quote.RecoverableValueSat);

    /// <summary>
    /// Confirmed satoshi on a record's funding address, in total and in its largest output.
    /// </summary>
    /// <remarks>
    /// <b>No key is derived here.</b> Measuring an address takes no key at all, so the read path unprotects
    /// nothing — only a build does. Both figures come back null when the explorer could not be read or none is
    /// configured for this network, and the page renders that as unknown rather than as zero: collapsing the two
    /// is the failure this whole distinction exists to prevent.
    /// </remarks>
    private async Task<SparkExitFundingBalance> ReadFundingAsync(
        UnilateralExitRecord record,
        UnilateralExitSettings settings,
        CancellationToken cancellationToken)
    {
        if (!SparkExitFundingExplorer.TryResolveBaseUrl(settings, Mainnet, out var baseUrl, out var error))
            return SparkExitFundingBalance.Unknown(error!);

        return await _explorer
            .MeasureConfirmedAsync(baseUrl!, record.FundingAddress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The store's exit funding key at one address index, or null with a merchant-facing reason.
    /// </summary>
    /// <remarks>
    /// The mnemonic is unprotected here and handed straight to the derivation; nothing keeps a reference to it,
    /// and the caller is expected to dispose the returned key as soon as it has what it needs — see
    /// <see cref="SparkExitFundingKey"/>. Only <see cref="QuoteAsync"/> and <see cref="BuildAsync"/> call this.
    /// </remarks>
    private SparkExitFundingKey? DeriveFundingKey(SparkSettings settings, uint index, out string? error)
    {
        var mnemonic = _mnemonicProtector.TryUnprotect(settings.ProtectedMnemonic);
        return SparkExitFundingKey.TryDerive(mnemonic, _network, index, out var key, out error) ? key : null;
    }

    /// <summary>
    /// The BIP32 path of a record's funding key, as an operator would type it into a recovery wallet.
    /// </summary>
    /// <remarks>
    /// This is what makes sats stranded on an abandoned exit's funding address recoverable without this plugin,
    /// so it is shown on the page and repeated in the refusal for a seed that no longer derives the address.
    /// Null only for a row whose index is not a usable one, which no quote can produce.
    /// </remarks>
    private string? DescribeKeyPath(UnilateralExitRecord record) =>
        TryFundingKeyIndex(record.FundingKeyIndex, out var index)
            ? "m/" + SparkExitFundingKey.KeyPathFor(_network, index)
            : null;

    /// <summary>
    /// Narrows a stored funding key index to a BIP32 address index.
    /// </summary>
    /// <remarks>
    /// The column is a <c>long</c> because Postgres has no unsigned types, and BIP32 reserves the top bit of a
    /// child number for hardening — so the usable range is 0 to <see cref="int.MaxValue"/>. A row outside it is
    /// refused rather than wrapped, because a wrapped index derives a real key for the wrong address.
    /// </remarks>
    private static bool TryFundingKeyIndex(long stored, out uint index)
    {
        if (stored is < 0 or > int.MaxValue)
        {
            index = 0;
            return false;
        }

        index = (uint)stored;
        return true;
    }

    /// <summary>Copies a quote's three figures onto a record. A null quote leaves them as they were.</summary>
    private static void ApplyQuote(UnilateralExitRecord record, SparkExitQuote? quote)
    {
        if (quote is null)
            return;

        record.RecoverableValueSat = quote.RecoverableValueSat;
        record.TotalFeeSat = quote.TotalFeeSat;
        record.SingleUtxoFundingSat = quote.SingleUtxoFundingSat;
    }

    /// <summary>
    /// Applies a change to a store's exit settings and reports whether the store came back up.
    /// </summary>
    /// <remarks>
    /// Applied to a copy of the whole blob rather than to the instance that was read, for the reason
    /// <c>SparkStableBalanceService.SaveAsync</c> documents: a write that throws on the way to the database must
    /// not leave the caller holding settings that were never persisted. The protected mnemonic in the same blob is
    /// carried across untouched. Storing settings also reconciles the store's running SDK instance with them,
    /// which tears the wallet down and reconnects it — which is why every caller holds the single-flight gate.
    /// </remarks>
    /// <param name="what">Lower-case description for the operator log, e.g. "the disclosure acknowledgement".</param>
    /// <param name="subject">Capitalised subject for the merchant-facing sentences, e.g. "The acknowledgement".</param>
    private async Task<UnilateralExitOpResult> SaveExitSettingsAsync(
        string storeId,
        SparkSettings settings,
        Action<UnilateralExitSettings> change,
        string what,
        string subject)
    {
        var updated = settings.Clone();
        updated.UnilateralExit = (settings.UnilateralExit ?? new UnilateralExitSettings()).Clone();
        change(updated.UnilateralExit);

        SparkSettingsApplied applied;
        try
        {
            applied = await _settingsStore.SetAsync(storeId, updated).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Store {StoreId}: could not store {What} ({Reason})",
                storeId, what, SparkErrors.Describe(ex));

            return Refuse($"{subject} could not be saved: {SparkErrors.Describe(ex)}");
        }

        if (!applied.WalletRunning)
        {
            // Reported as a failure even though the change is stored, because the operator cannot do the next
            // thing: quoting and building both need a running wallet, and saying "saved" would send them to a
            // form that refuses.
            return Refuse(
                $"{subject} was saved, but this store's Spark wallet did not come back up: "
                + (applied.Reason ?? "check the server logs."));
        }

        return new UnilateralExitOpResult(true, null, null);
    }

    /// <summary>
    /// Records why an attempt on a live exit failed and reports it, leaving the row's status alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The status does not move, deliberately: an exit that failed to build is still awaiting funding (or still
    /// holds the previous build's transactions), and the explanation belongs beside it rather than in a log
    /// nobody reads. A row that cannot be updated still reports the original refusal — the operator's problem is
    /// the refusal, not the bookkeeping.
    /// </para>
    /// <para>
    /// Never cancellable. Recording why something failed is the cheapest write in this service and the one an
    /// operator most needs to see, so it does not take the request's token: a browser that went away is not a
    /// reason to leave a row with no explanation on it.
    /// </para>
    /// </remarks>
    /// <param name="expectedStatus">The status the caller read, guarding the update — see the store's contract.</param>
    private async Task<UnilateralExitOpResult> FailAsync(
        UnilateralExitRecord record,
        UnilateralExitStatus expectedStatus,
        string error)
    {
        record.LastError = error;
        record.UpdatedUtc = _timeProvider.GetUtcNow();

        try
        {
            await _records
                .UpdateAsync(record, expectedStatus, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Store {StoreId}: could not record why unilateral exit {ExitId} failed",
                record.StoreId, record.Id);
        }

        return new UnilateralExitOpResult(false, error, record);
    }

    /// <summary>
    /// The one log line in this service that is the last copy of something valuable.
    /// </summary>
    /// <remarks>
    /// An error rather than a warning, and it names every txid: the SDK will not hand these transactions back
    /// without a fresh build against a fresh funding output, so an operator recovering from this reads the hex out
    /// of nothing. The hex itself is deliberately not logged — it is large, and the txids are enough to establish
    /// what was signed and whether any of it reached the chain.
    /// </remarks>
    private void LogUnsavedBuild(
        string storeId,
        UnilateralExitRecord record,
        SparkExitResult result,
        Exception? exception)
    {
        var txids = string.Join(", ", result.Transactions.Select(transaction => transaction.Txid));

        _logger.LogError(
            exception,
            "Store {StoreId}: built unilateral exit {ExitId} but could not persist its {Count} signed "
            + "transactions. Nothing was broadcast. The transactions were: {Txids}",
            storeId, record.Id, result.Transactions.Count, txids);
    }

    private static UnilateralExitOpResult Refuse(string error) => new(false, error, null);

    /// <summary>
    /// Why the funding on the address does not fund this exit, in terms an operator can act on.
    /// </summary>
    /// <remarks>
    /// <b>Every branch says "a single new output", and that is the whole point of the message.</b> The natural
    /// reading of "the address holds 3,000 sat and needs 4,200" is "send 1,200 more", which produces a second
    /// output and funds nothing — CPFP spends one outpoint. So the instruction is always to send the full amount
    /// again, as one output, and the arithmetic is there to explain why rather than to be added up.
    /// </remarks>
    private static string DescribeShortfall(
        long required,
        string fundingAddress,
        IReadOnlyList<SparkExitFundingUtxo> confirmed)
    {
        var total = confirmed.Sum(utxo => utxo.ValueSat);
        var largest = confirmed.Count == 0 ? 0 : confirmed.Max(utxo => utxo.ValueSat);

        return confirmed.Count switch
        {
            0 => string.Format(
                CultureInfo.InvariantCulture,
                "The funding address holds no confirmed output yet. Send at least {0:N0} sat to {1} as a single "
                + "transaction and try again once it has one confirmation.",
                required,
                fundingAddress),
            1 => string.Format(
                CultureInfo.InvariantCulture,
                "The funding address holds one confirmed output of {0:N0} sat and this exit needs {1:N0} sat. The "
                + "fees are paid from one output, so topping up does not help: send at least the full required "
                + "amount as a single new output and try again once it confirms.",
                largest,
                required),
            _ => string.Format(
                CultureInfo.InvariantCulture,
                "The funding address holds {0:N0} sat across {1} confirmed outputs and this exit needs {2:N0} sat, "
                + "but the fees are paid from one single output and the largest holds {3:N0} sat. Send at least "
                + "the full required amount as a single new output and try again once it confirms.",
                total,
                confirmed.Count,
                required,
                largest)
        };
    }

    /// <summary>
    /// The leaf ids this exit was pinned to, or an empty list when the column cannot be read.
    /// </summary>
    /// <remarks>
    /// A malformed column is a refusal rather than a fallback to automatic selection, which is why an empty list
    /// is returned instead of null: <c>Auto</c> at build time would price and sign a different set of leaves than
    /// the one the operator funded for, which is the whole hazard the column exists to prevent.
    /// </remarks>
    private IReadOnlyList<string> DeserializeLeafIds(UnilateralExitRecord record)
    {
        if (string.IsNullOrEmpty(record.LeafIdsJson))
            return [];

        try
        {
            var ids = JsonSerializer.Deserialize<string[]>(record.LeafIdsJson, JsonOptions);
            return ids is null
                ? []
                : ids.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex,
                "Store {StoreId}: unilateral exit {ExitId} has an unreadable leaf selection",
                record.StoreId, record.Id);
            return [];
        }
    }

    /// <summary>
    /// Reads a built record's transaction set back, refusing anything that is not a well-formed set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sanity-checked and not merely deserialised, because <see cref="System.Text.Json"/> will happily produce a
    /// <see cref="SparkExitTransaction"/> with a null <c>Txid</c> and a null <c>DependsOn</c> from
    /// <c>[{}]</c> — records get no null checks on their positional parameters. The page renders these as
    /// broadcast instructions, so a structurally broken entry must become an explanation here rather than a
    /// <see cref="NullReferenceException"/> in a view. An out-of-range <c>Kind</c> or <c>Status</c> is the same
    /// story from the other direction: the enums are persisted numerically, so an unknown number would render as
    /// a bare integer next to copy-pasteable transaction hex.
    /// </para>
    /// <para>
    /// The order is left exactly as stored. It is the SDK's own topological broadcast order — see
    /// <see cref="SparkExitResult"/> — and re-deriving it here from <c>DependsOn</c> would be inventing an
    /// ordering the SDK already gave.
    /// </para>
    /// </remarks>
    /// <returns>
    /// False when a built record's column could not be read as a well-formed set. True — with a null
    /// <paramref name="transactions"/> — when there is simply nothing built yet.
    /// </returns>
    private bool TryReadTransactions(
        UnilateralExitRecord? record,
        out IReadOnlyList<SparkExitTransaction>? transactions)
    {
        transactions = null;

        if (record?.TransactionsJson is not { } json || string.IsNullOrWhiteSpace(json))
            return true;

        SparkExitTransaction[]? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<SparkExitTransaction[]>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex,
                "Store {StoreId}: unilateral exit {ExitId} has an unreadable transaction set",
                record.StoreId, record.Id);
            return false;
        }

        if (parsed is null || parsed.Length == 0 || parsed.Any(IsMalformed))
        {
            _logger.LogError(
                "Store {StoreId}: unilateral exit {ExitId} has a transaction set that parsed but is not usable",
                record.StoreId, record.Id);
            return false;
        }

        transactions = parsed;
        return true;

        static bool IsMalformed(SparkExitTransaction? transaction) =>
            transaction is null
            || string.IsNullOrWhiteSpace(transaction.Txid)
            || string.IsNullOrWhiteSpace(transaction.TxHex)
            || transaction.DependsOn is null
            || !Enum.IsDefined(transaction.Kind)
            || !Enum.IsDefined(transaction.Status.Readiness);
    }

    /// <summary>
    /// Validates the destination for this server's network, wrapping the sweep resolver's own parser.
    /// </summary>
    /// <remarks>
    /// The same parser, deliberately, and not a second <see cref="BitcoinAddress.Create(string, Network)"/> call:
    /// it also rejects a <c>bitcoin:</c> payment link, which parses as nothing and would otherwise reach the SDK
    /// as a destination. Its messages are sentence fragments by design, so they are wrapped here — the fragment
    /// names the fault and the wrapper says why it matters at all.
    /// </remarks>
    private bool TryParseDestination(string? candidate, out string destination, out string error)
    {
        destination = string.Empty;

        if (!SweepDestinationResolver.TryParse(candidate, _network, out var fragment))
        {
            error = $"That destination cannot be used: {fragment}. The recovered coins are swept there by a "
                    + $"transaction signed during the build, so it has to be a plain address that is valid on "
                    + $"{_network.ChainName}.";
            return false;
        }

        destination = candidate!.Trim();
        error = string.Empty;
        return true;
    }
}
