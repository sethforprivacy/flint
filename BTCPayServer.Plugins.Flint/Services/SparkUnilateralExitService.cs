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
/// <b>Every guard is here.</b> The controller renders, redirects and decides nothing; the disclosure is re-read
/// from storage before each quote and build because a checkbox enforced in a view is enforced
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

    internal const string NotConfigured =
        "Flint is not set up for this store.";

    internal const string WalletNotRunning =
        "This store's Spark wallet is not running, so nothing can be quoted or built.";

    internal const string DisclosureRequired =
        "Confirm that you have read what a unilateral exit involves. It is a last resort: the transactions are "
        + "broadcast by hand, the funds are locked behind timelocks measured in days, and the on-chain fees are "
        + "paid up front from a separate funding address.";

    internal static readonly string FeeRateOutOfRange = string.Format(
        CultureInfo.InvariantCulture,
        "The fee rate has to be between {0:N0} and {1:N0} sat/vB. Every transaction in the exit is built at this one "
        + "rate, so it also decides which leaves are worth exiting at all.",
        MinFeeRateSatPerVbyte,
        MaxFeeRateSatPerVbyte);

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

    /// <summary>The backstop's refusal: see <see cref="GuardAsync"/>.</summary>
    internal const string UnexpectedFailure =
        "Something unexpected went wrong with that, so it was not completed. Nothing was broadcast. The server log "
        + "has the detail; reload the page to see this exit's current state.";

    /// <summary>The page read's backstop: the exit records could not be read at all.</summary>
    internal const string ExitsUnreadable =
        "This store's unilateral-exit records could not be read, so this page cannot show whether an exit is in "
        + "progress. Nothing has been changed. The server log has the detail; try again shortly.";

    /// <summary>A check of a built exit whose last build returned no transactions at all.</summary>
    internal const string EmptySetNothingToCheck =
        "This exit's last build returned no transactions — Spark found nothing left to sign for its leaves — so "
        + "there is nothing here to check against the chain. If its sweep has already confirmed, the exit is "
        + "finished and you can mark it completed. Otherwise build it again, which re-reads the chain.";

    /// <summary>A check needs the wallet: see <see cref="CheckBuiltLockedAsync"/>.</summary>
    internal const string CheckNeedsWallet =
        "This store's Spark wallet is not running, and checking an exit against the chain needs it: the check is "
        + "made through the wallet's own connection to the chain. The signed transactions below do not need it — "
        + "you can keep broadcasting what you know is ready — but start the wallet to see fresh statuses.";

    /// <summary>
    /// Why an exit was not marked completed, when the operator asked the chain to confirm it.
    /// </summary>
    private static string DescribeNotDone(UnilateralExitOpResult check)
    {
        const string Override =
            " If you have confirmed yourself that the sweep is in a block, tick the confirmation beside the button "
            + "and mark it completed again.";

        if (!check.Success)
        {
            return "This exit was not marked completed, because Spark could not confirm it is finished: "
                   + (check.Error ?? "the check did not answer.") + Override;
        }

        return check.Verdict is SparkExitVerdict.Redo
            ? "This exit was not marked completed: Spark's check says this set of transactions can no longer "
              + "finish, so its money is not at the destination yet. Build it again rather than setting it aside."
              + Override
            : "This exit was not marked completed: Spark's check says it is still on track but not finished — "
              + "something is still to be broadcast or confirmed, and completing it now would take its signed "
              + "transactions off this page. Keep broadcasting what is ready." + Override;
    }

    /// <summary>A re-quote found every pinned leaf's exit already over on-chain.</summary>
    internal const string AllLeavesFinished =
        "Every leaf this exit was pinned to has already finished on-chain — its refund was swept, or its branch can "
        + "go no further — so there is nothing left to build. Check progress to confirm the sweep, then mark this "
        + "exit completed.";

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
    /// What the page data looks like when there is no Flint on this store, or nothing could be read.
    /// </summary>
    /// <remarks>
    /// Spelled out once rather than at each return, because a positional record of twelve members is exactly the
    /// shape where two "empty" literals drift apart from one another.
    /// </remarks>
    private static UnilateralExitPageData NothingToShow =>
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
    /// Where exit-state backups actually live: the automatic backup and the import queue, as owner-only files per
    /// store, outside the
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
    /// <remarks>
    /// Guarded for the same reason every operation is (see <see cref="GuardAsync"/>), and this is the call that
    /// most needs it: it runs on every GET of a page any store viewer can reload. A failure reads as a page that
    /// says it could not read the exits — never as the quote form, which would invite a second exit beside one
    /// the page merely failed to load.
    /// </remarks>
    public async Task<UnilateralExitPageData> ReadAsync(
        string storeId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await ReadCoreAsync(storeId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Store {StoreId}: could not read the unilateral-exit page data ({Reason})",
                storeId, SparkErrors.Describe(ex));
            return NothingToShow with { LoadError = ExitsUnreadable };
        }
    }

    private async Task<UnilateralExitPageData> ReadCoreAsync(
        string storeId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        var settings = await _settingsStore.GetAsync(storeId).ConfigureAwait(false);
        if (settings is null)
            return NothingToShow;

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

        // Only when the quote form will actually render: the disclosure has been accepted and nothing is in
        // flight. A store with an exit already awaiting funding or built has its rate on the record, and a store
        // that has not acknowledged sees only the disclosure — so asking the explorer for a market rate in either
        // case would be an external round trip that decides nothing. The second one matters more than it looks:
        // the exit page is linked from every store's Advanced page, and an operator who opens it only to read
        // what an exit involves must not have this server announce itself to mempool.space for it. Null, not
        // the fallback, when there is no recommendation: the fallback is the caller's decision, and a service
        // that returned a market rate it did not have would be claiming one.
        long? recommendedFeeRate = null;
        if (active is null && exitSettings.DisclosureAcknowledged)
        {
            recommendedFeeRate = await _explorer
                .RecommendFeeRateSatPerVbyteAsync(Mainnet, exitSettings, cancellationToken)
                .ConfigureAwait(false);
        }

        // Read back and checked here rather than anywhere above: the page renders these, and a malformed column
        // has to become an explanation on the page instead of an exception in a view.
        var readable = TryReadTransactions(
            active, out var transactions, out var statusesReadUtc, out var failedLeaves);

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
            pending,
            StatusesReadUtc: readable ? statusesReadUtc : null,
            FailedLeaves: readable && failedLeaves is { Count: > 0 } ? failedLeaves : null);
    }

    /// <inheritdoc />
    public Task<UnilateralExitOpResult> AcknowledgeDisclosureAsync(
        string storeId,
        CancellationToken cancellationToken = default) =>
        GuardAsync(storeId, "acknowledging the disclosure for",
            () => AcknowledgeDisclosureCoreAsync(storeId, cancellationToken));

    private async Task<UnilateralExitOpResult> AcknowledgeDisclosureCoreAsync(
        string storeId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

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
    public Task<UnilateralExitOpResult> SetExplorerUrlAsync(
        string storeId,
        string? esploraApiUrl,
        CancellationToken cancellationToken = default) =>
        GuardAsync(storeId, "setting the explorer for",
            () => SetExplorerUrlCoreAsync(storeId, esploraApiUrl, cancellationToken));

    private async Task<UnilateralExitOpResult> SetExplorerUrlCoreAsync(
        string storeId,
        string? esploraApiUrl,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

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
    public Task<UnilateralExitOpResult> CheckAsync(
        string storeId,
        string recordId,
        CancellationToken cancellationToken = default) =>
        GuardAsync(storeId, "checking", () => CheckCoreAsync(storeId, recordId, cancellationToken));

    private async Task<UnilateralExitOpResult> CheckCoreAsync(
        string storeId,
        string recordId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

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

            return await CheckBuiltLockedAsync(storeId, record).ConfigureAwait(false);
        }
        finally
        {
            _running.TryRemove(storeId, out _);
        }
    }

    /// <summary>
    /// Asks the chain about a built record and stores the refreshed statuses. The caller holds the store's gate.
    /// </summary>
    /// <remarks>
    /// Shared by <see cref="CheckAsync"/> and <see cref="MarkCompletedAsync"/>, which needs a fresh verdict to
    /// complete on and must not release the gate between asking and completing. Never takes the request's
    /// token: what it writes is the chain's answer, and a browser that went away is not a reason to drop it.
    /// </remarks>
    private async Task<UnilateralExitOpResult> CheckBuiltLockedAsync(string storeId, UnilateralExitRecord record)
    {
        {
            // The status every compare-and-set below is guarded on, read once: the row must still be Built when
            // the refreshed set lands, because anything else means the operator finished or abandoned it while the
            // chain was being asked.
            var from = record.Status;

            if (!TryReadTransactions(record, out var stored, out var refusedLeaves) || stored is null)
            {
                // A built row whose set cannot be read has nothing to ask the chain about, and the SDK is handed
                // the set — so this is a refusal rather than a call that would report on an exit that is not the
                // one stored. The page reports the same condition from the same check, and offers a rebuild: the
                // advice is not to abandon, because the funding may already be committed to what was signed.
                return await FailAsync(
                        record,
                        from,
                        "This exit's stored transactions could not be read back, so there is nothing to check "
                        + "against the chain. Build it again, which re-reads the chain and stores a fresh set.")
                    .ConfigureAwait(false);
            }

            if (stored.Count == 0)
            {
                // Not a failure, and not written to the row as one: the last build legitimately returned nothing.
                // The SDK's check over an empty set can only ever answer "valid", which would read as "on track"
                // for an exit with nothing in it, so it is not asked.
                return new UnilateralExitOpResult(false, EmptySetNothingToCheck, record);
            }

            // The SDK checks a whole exit response, and the record only stores the transactions — see
            // UnilateralExitRecord. The two totals and the leaf set it echoes back are not inputs to the check,
            // so the stored figures are passed through as they are rather than re-quoted: the check reads the
            // chain and nothing else, and re-quoting would make it depend on the wallet's leaves as well.
            var stored2 = new SparkExitResult(
                record.RecoverableValueSat,
                record.TotalFeeSat,
                (ulong)record.FeeRateSatPerVbyte,
                record.DestinationAddress,
                stored,
                DeserializeLeafIds(record).Select(id => new SparkExitLeaf(id, 0)).ToArray(),
                // Echoed back so the reconstruction is the document the build returned. A check reads the chain
                // and does not revisit these, so carrying them is what keeps a reloaded page honest about the
                // leaves the operators refused.
                refusedLeaves);

            // The SDK's check reads only the chain, but it is a method on a connected SDK instance, and this plugin
            // has one only while the store's wallet runs — there is no chain-only handle to ask instead. So a
            // stopped wallet cannot check, and says so in its own words rather than as a chain failure, which
            // would send the operator to look at the wrong thing. The broadcasting itself needs no wallet: the
            // signed set on the page is complete without one.
            var sdk = await _runtime.GetSdkClientAsync(storeId).ConfigureAwait(false);
            if (sdk is null)
                return new UnilateralExitOpResult(false, CheckNeedsWallet, record);

            SparkExitProgress progress;
            try
            {
                progress = await WithinDeadlineAsync(
                        sdk.CheckUnilateralExitAsync(stored2, CancellationToken.None), CheckDeadline, "checking")
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Store {StoreId}: could not check unilateral exit {ExitId} against the chain ({Reason})",
                    storeId, record.Id, DescribeSdkFailure(ex));

                return await FailAsync(
                        record,
                        from,
                        "Spark could not read the chain for this exit: " + DescribeSdkFailure(ex)
                        + ". The stored transactions are unchanged and nothing was broadcast.")
                    .ConfigureAwait(false);
            }

            // The statuses it reports replace the ones stored, which is the whole point of the call: an operator
            // reading the page afterwards sees what the chain says, not what it said when the set was built.
            // The refused leaves are carried across rather than cleared: a check refreshes statuses, not the
            // set's membership, and writes that dropped them would quietly turn a partial recovery into a
            // complete-looking one on the next page load.
            record.TransactionsJson = SerializeTransactions(
                progress.Transactions, _timeProvider.GetUtcNow(), refusedLeaves);
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
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>A pasted backup is queued for import, never written over the automatic backup.</b> The automatic
    /// slot is the wallet's own latest export and every due pass replaces it — within two minutes of any
    /// receive, and at once after a restart — so a paste written there was routinely gone before anything had
    /// imported it. The queue is a slot no automatic pass writes: a queued backup leaves it only once an import
    /// of it has returned. The caller imports it straight away when the wallet is running
    /// (<see cref="ISparkStoreRuntime.ImportPendingExitStateAsync"/>); otherwise the next connect does.
    /// </para>
    /// <para>
    /// <b>Blank clears everything the page reports</b>: the queue, the automatic backup, and the deprecated
    /// settings slot — the last because the connect adopts from it whenever the file is empty, so a store that
    /// still held a value there would take the blob back on its next connect under a page that says none is
    /// stored. The automatic backup is then taken afresh by the next due pass.
    /// </para>
    /// </remarks>
    public Task<UnilateralExitOpResult> SetExitStateBackupAsync(
        string storeId,
        string? exitState,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        // Behind the same backstop as every other operation here: a settings or file-store fault on the paste
        // path would otherwise escape the controller, and BTCPay answers that by disabling the plugin.
        return GuardAsync(storeId, "storing the exit-state backup for",
            () => SetExitStateBackupCoreAsync(storeId, exitState, cancellationToken));
    }

    private async Task<UnilateralExitOpResult> SetExitStateBackupCoreAsync(
        string storeId,
        string? exitState,
        CancellationToken cancellationToken)
    {
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

            // The subject and the log's description deliberately say nothing about the value: it discloses
            // the store's balance and history, and this method is one of the places in the plugin that
            // handles it.
            try
            {
                if (normalised is null)
                {
                    // The queue first: it is the one slot holding data the wallet may not have, so a clear that
                    // stopped part way is better stopped with the automatic copy still in place.
                    foreach (var queued in await _backups.ListPendingAsync(storeId, cancellationToken)
                                 .ConfigureAwait(false))
                    {
                        await _backups.DeletePendingAsync(storeId, queued.Id, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    await _backups.DeleteAsync(storeId, cancellationToken).ConfigureAwait(false);

                    // Both locations, or the word "cleared" is a promise the next connect breaks.
                    await _settingsStore.ClearExitStateBackupSlotAsync(storeId).ConfigureAwait(false);

                    _logger.LogInformation(
                        "Store {StoreId}: the stored and queued exit-state backups were cleared", storeId);
                }
                else
                {
                    // Deduplicated by the store: a save pressed twice, or a page reloaded with the value still
                    // in the textarea, queues one copy, not two multi-megabyte files.
                    await _backups.AddPendingAsync(storeId, normalised, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Store {StoreId}: queued an exit-state backup from the page for import ({Length} characters)",
                        storeId, normalised.Length);
                }
            }
            catch (Exception ex)
            {
                // The exception is the store's own — filesystem or settings — and never carries the value.
                _logger.LogError(ex,
                    "Store {StoreId}: could not {What}",
                    storeId,
                    normalised is null
                        ? "clear the unilateral-exit state backups"
                        : "queue a unilateral-exit state backup");

                return Refuse(normalised is null
                    ? "The exit-state backups could not all be cleared. Check the server log for the reason."
                    : "The exit-state backup could not be stored, so nothing will be imported. Check the server "
                      + "log for the reason.");
            }

            return new UnilateralExitOpResult(true, null, null);
        }
        finally
        {
            _running.TryRemove(storeId, out _);
        }
    }

    /// <inheritdoc />
    public Task<UnilateralExitOpResult> QuoteAsync(
        string storeId,
        long feeRateSatPerVbyte,
        string destinationAddress,
        CancellationToken cancellationToken = default) =>
        GuardAsync(storeId, "quoting",
            () => QuoteCoreAsync(storeId, feeRateSatPerVbyte, destinationAddress, cancellationToken));

    private async Task<UnilateralExitOpResult> QuoteCoreAsync(
        string storeId,
        long feeRateSatPerVbyte,
        string destinationAddress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        var settings = await _settingsStore.GetAsync(storeId).ConfigureAwait(false);
        if (settings is null)
            return Refuse(NotConfigured);

        var exitSettings = settings.UnilateralExit ?? new UnilateralExitSettings();

        // The disclosure first, before the input checks: an operator who has not read what this costs them should
        // be told that rather than that their fee rate is out of range.
        if (!exitSettings.DisclosureAcknowledged)
            return Refuse(DisclosureRequired);

        if (feeRateSatPerVbyte is < MinFeeRateSatPerVbyte or > MaxFeeRateSatPerVbyte)
            return Refuse(FeeRateOutOfRange);

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
                quote = await WithinDeadlineAsync(
                        sdk.PrepareUnilateralExitAsync(
                            (ulong)feeRateSatPerVbyte, destination, leafIds: null, cancellationToken),
                        QuoteDeadline,
                        "quoting")
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Store {StoreId}: could not quote a unilateral exit ({Reason})",
                    storeId, DescribeSdkFailure(ex));

                return Refuse(
                    "Spark could not quote a unilateral exit: " + DescribeSdkFailure(ex)
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
                + "{FeeRate} sat/vB; {Funding}{Skipped}",
                storeId, quote.Leaves.Count, quote.RecoverableValueSat, feeRateSatPerVbyte,
                quote.RequiresFunding
                    ? $"it needs {quote.SingleUtxoFundingSat} sat on {fundingAddress}"
                    : "every selected leaf is recovered cooperatively, so it needs no funding",
                quote.Skipped is { Count: > 0 } skipped
                    ? $"; Spark left out {skipped.Count} leaf(s): {DescribeSkipped(skipped)}"
                    : string.Empty);

            // Said once, here, because a quote's skipped set describes that quote and not the exit: the record
            // pins the leaf ids it did select, and re-quoting later can skip a different set. So it belongs in
            // the answer to the press that produced it — an operator who expected more leaves is told why
            // straight away — and not in a column that would go stale silently.
            return new UnilateralExitOpResult(
                true,
                quote.Skipped is { Count: > 0 } ? DescribeSkipped(quote.Skipped) : null,
                record);
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
    /// <para>
    /// <b>A rebuild funds differently from a first build, and that difference is the SDK's contract.</b> Once a
    /// build has succeeded, the outputs it was given are recorded (<see cref="UnilateralExitRecord.FundingUtxosJson"/>)
    /// and every later build passes them back, with anything else confirmed on the funding address alongside —
    /// the SDK's documented "funding a second attempt". It follows each outpoint to whatever an earlier attempt
    /// turned it into, so a Redo, a rebuild after an unverified status, or a fee bump spends the money already
    /// committed instead of demanding a second full funding and stranding the first on per-branch outputs.
    /// </para>
    /// <para>
    /// <b>A new fee rate is how a stalled exit is sped up, and it is the SDK's only way to do it.</b> Its guidance
    /// for an exit that stopped confirming is to quote again at the higher rate, naming the same leaves, and build
    /// again: what has confirmed stays, and what has not is rebuilt at the new rate and replaces the earlier
    /// version on the network. So <paramref name="feeRateSatPerVbyte"/> re-prices this build and, when it
    /// succeeds, becomes the record's rate. For a built exit it may not go <em>down</em>: a replacement has to pay
    /// a higher rate than what it replaces to relay, so a lower one would sign children that can never displace
    /// the ones already in mempools. The same rate is allowed, because that is the plain rebuild a Redo or an
    /// unverified status asks for.
    /// </para>
    /// </remarks>
    public Task<UnilateralExitOpResult> BuildAsync(
        string storeId,
        string recordId,
        long? feeRateSatPerVbyte,
        CancellationToken cancellationToken = default) =>
        GuardAsync(storeId, "building",
            () => BuildCoreAsync(storeId, recordId, feeRateSatPerVbyte, cancellationToken));

    /// <inheritdoc />
    public Task<UnilateralExitOpResult> BuildAsync(
        string storeId,
        string recordId,
        CancellationToken cancellationToken = default) =>
        BuildAsync(storeId, recordId, null, cancellationToken);

    private async Task<UnilateralExitOpResult> BuildCoreAsync(
        string storeId,
        string recordId,
        long? requestedFeeRate,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

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

        if (requestedFeeRate is < MinFeeRateSatPerVbyte or > MaxFeeRateSatPerVbyte)
            return Refuse(FeeRateOutOfRange);

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

            if (record.Status is UnilateralExitStatus.Built && requestedFeeRate < record.FeeRateSatPerVbyte)
            {
                // Not written to the row: nothing about the exit is wrong, the request was. See the remarks on
                // this method for why a built exit's rate may only stay or rise.
                return new UnilateralExitOpResult(
                    false,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "This exit was built at {0:N0} sat/vB, and a rebuild cannot go lower: a replacement has to "
                        + "pay a higher rate than the transactions it replaces, so a cheaper set could never displace "
                        + "anything already broadcast. Build again at {0:N0} sat/vB or higher.",
                        record.FeeRateSatPerVbyte),
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

            // The rate this attempt prices and signs at. It only becomes the record's rate when it is written with
            // the figures that belong to it: in step two for a first build, and with the signed set otherwise.
            var feeRate = requestedFeeRate ?? record.FeeRateSatPerVbyte;

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

            // What this exit has already committed to signed transactions. Empty for a first build; set by every
            // successful one — see UnilateralExitRecord.FundingUtxosJson and the remarks on this method.
            if (!TryReadCommittedFunding(record, funding.PubkeyHex, out var committedFunding))
                return await FailAsync(record, from, DescribeCommittedFundingUnreadable(record)).ConfigureAwait(false);

            var following = committedFunding.Count > 0;

            // Only a first build writes a fresh quote's figures over the row before it succeeds. A built row's
            // figures describe the transaction set stored beside them, and a failed rebuild must not leave the page
            // showing a fee and a total that belong to transactions nobody signed.
            var awaiting = from is UnilateralExitStatus.AwaitingFunding;

            // The explorer is how a first build finds the output the operator sent, so without one it cannot
            // start. A rebuild already holds the outpoints it committed, and the SDK follows those through its own
            // chain service — so for a rebuild the explorer only adds any fresh top-up, and its absence is not a
            // reason to refuse.
            var haveExplorer = SparkExitFundingExplorer.TryResolveBaseUrl(
                exitSettings, Mainnet, out var baseUrl, out var urlError);

            // Step one: re-quote the record's own leaves. This happens before funding is even looked at, because
            // its answer is what the funding has to satisfy — see the remarks on this method.
            SparkExitQuote fresh;
            try
            {
                fresh = await WithinDeadlineAsync(
                        sdk.PrepareUnilateralExitAsync(
                            (ulong)feeRate,
                            record.DestinationAddress,
                            leafIds,
                            cancellationToken),
                        QuoteDeadline,
                        "re-pricing")
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Store {StoreId}: could not re-quote unilateral exit {ExitId} ({Reason})",
                    storeId, record.Id, DescribeSdkFailure(ex));

                return await FailAsync(
                        record,
                        from,
                        "Spark could not re-price this exit: " + DescribeSdkFailure(ex)
                        + ". Nothing was signed, so trying again is safe.")
                    .ConfigureAwait(false);
            }

            // The pinned leaves have to still be the exit. The SDK skips a named leaf that is missing from its
            // local storage without saying so — a payment, a sweep, a Stable Balance conversion or its own leaf
            // optimisation can have spent or reshaped it since the quote — so a quote that covers fewer leaves
            // than were pinned is a smaller exit than the one the operator funded, and building it would commit
            // that funding to recovering less. Refused here, before any funding is looked at, so nothing is
            // committed. A leaf the chain shows as finished is the one legitimate absence.
            if (DescribeCoverage(record, fresh, leafIds) is { } coverage)
                return await FailAsync(record, from, coverage).ConfigureAwait(false);

            if (fresh.RecoverableValueSat <= fresh.TotalFeeSat)
            {
                return await FailAsync(record, from, DescribeUneconomic(fresh)).ConfigureAwait(false);
            }

            if (awaiting)
            {
                // Step two, for a first build: persist the fresh figures before selecting against them, so the
                // requirement the operator reads on the page and the requirement the selection uses are the same
                // number. Written even though the build may still fail — especially then, because a failed
                // attempt's whole value to the operator is telling them what to fund.
                ApplyQuote(record, fresh);
                record.FeeRateSatPerVbyte = feeRate;
                record.LastError = null;
                record.UpdatedUtc = _timeProvider.GetUtcNow();

                if (!await _records.UpdateAsync(record, from, cancellationToken).ConfigureAwait(false))
                    return new UnilateralExitOpResult(false, ExitChangedUnderneath, record);
            }

            IReadOnlyList<SparkExitFundingUtxo> inputs;
            SparkExitFundingUtxo? chosen = null;
            long largest = 0;
            var explorerMissed = false;

            if (!fresh.RequiresFunding)
            {
                // Nothing to fund: every selected leaf is recovered cooperatively, so its fee comes out of its
                // own value and no Bitcoin of the operator's is spent. The SDK's contract for that case is to be
                // given no funding inputs — and, below, no signer — so the explorer is not consulted at all. Not
                // consulting it is the point: a quote like this must be buildable on a store whose explorer is
                // unreachable, or unconfigured, because nothing about it needs the chain the explorer serves.
                //
                // Any funding this exit committed earlier is dropped rather than passed back, because the SDK
                // does not want it for this quote; the outputs stay on the funding address, recoverable from
                // the record's key path, exactly as an unused top-up always has.
                inputs = [];
            }
            else if (following)
            {
                // Step three, for a rebuild: hand back what the last build committed, and let the SDK follow it.
                // This is the SDK's own documented second attempt ("pass back the funding inputs the stored
                // response carries"): an outpoint an earlier attempt spent is not an error, the SDK walks it to
                // whatever it became — fan-out outputs, CPFP change, several steps of both — and keeps an outpoint
                // spent only by an unconfirmed transaction, because that is this exit's own in-flight child and a
                // rebuild replaces it. Listing the address instead cannot see any of that: an output spent by an
                // in-flight child is not unspent, and a fan-out's per-branch outputs are each below a whole-exit
                // requirement, so every Redo would have demanded a second full funding and stranded the first.
                //
                // Anything else confirmed on the address is added alongside, which is how a top-up reaches the
                // exit — the funding address belongs to this exit alone, so everything on it was sent for it. The
                // SDK de-duplicates by outpoint, so an output that is both listed and reached by following (a
                // confirmed fan-out output, say) is counted once; the list is de-duplicated here as well so the
                // record stores each outpoint once.
                var added = new List<SparkExitFundingUtxo>();
                if (haveExplorer)
                {
                    var lookup = await _explorer
                        .ListConfirmedAsync(baseUrl!, record.FundingAddress, funding.PubkeyHex, cancellationToken)
                        .ConfigureAwait(false);

                    if (lookup.Utxos is { } listed)
                    {
                        added.AddRange(listed.Where(utxo =>
                            committedFunding.All(existing => existing.OutPoint != utxo.OutPoint)));
                    }
                    else
                    {
                        explorerMissed = true;
                        _logger.LogWarning(
                            "Store {StoreId}: rebuilding unilateral exit {ExitId} from its committed funding alone, "
                            + "because the funding address could not be listed ({Reason})",
                            storeId, record.Id, lookup.Error);
                    }
                }
                else
                {
                    explorerMissed = true;
                }

                inputs = committedFunding.Concat(added).ToArray();
            }
            else
            {
                // Moved out of the shared prologue above: the only thing that needs an explorer is finding the
                // output a first build spends, so the requirement is asserted where it is true rather than
                // before the quote has said whether anything needs funding at all.
                if (!haveExplorer)
                    return await FailAsync(record, from, urlError!).ConfigureAwait(false);

                var required = fresh.SingleUtxoFundingSat;

                var lookup = await _explorer
                    .ListConfirmedAsync(baseUrl!, record.FundingAddress, funding.PubkeyHex, cancellationToken)
                    .ConfigureAwait(false);

                if (lookup.Utxos is not { } confirmed)
                    return await FailAsync(record, from, lookup.Error!).ConfigureAwait(false);

                largest = confirmed.Count == 0 ? 0 : confirmed.Max(utxo => utxo.ValueSat);

                // One output, not a sum, for a first build. The quote's requirement is sized for exactly one
                // funding input, so two outputs each half of it are short by the second input's weight — and
                // the funding instructions say "as one output" so the rule the page states is the rule applied.
                //
                // The smallest output that suffices, so an operator who over-funded (or funded twice) keeps the
                // larger one out of this build. It is not lost to a later one either way: once this build has
                // committed an output, a rebuild follows it and adds whatever else is on the address.
                chosen = confirmed
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

                inputs = [chosen];
            }

            SparkExitResult result;
            SparkExitQuote? committed = null;
            try
            {
                result = await WithinDeadlineAsync(sdk
                    .UnilateralExitAsync(
                        (ulong)feeRate,
                        record.DestinationAddress,
                        leafIds,
                        inputs,
                        // A recovery with no funding step has no CPFP child and nothing for a signer to sign,
                        // and the SDK rejects a signer given alongside no funding. The key is only materialised
                        // into this call when the quote says there is something to sign.
                        fresh.RequiresFunding ? funding.Secret : null,
                        second =>
                        {
                            // The veto, against the quote the SDK took inside this call. A unilateral-exit quote
                            // does not expire — it goes stale silently as the wallet's tree moves — so this is
                            // the last point at which the arithmetic is authoritative. It can differ again from
                            // the quote taken moments ago, which is why its own requirement is re-checked and
                            // then persisted by the catch below.
                            committed = second;

                            // Again against the build's own quote: the tree can move between the two.
                            if (DescribeCoverage(record, second, leafIds) is { } coverage)
                                return coverage;

                            if (second.RecoverableValueSat <= second.TotalFeeSat)
                                return DescribeUneconomic(second);

                            // A first build is judged here against its one output. A rebuild is not: what its
                            // funding is worth is only known once the SDK has followed it, and the SDK's own
                            // shortfall is the answer to that — a plugin-side guess would refuse funding that is
                            // in fact sufficient.
                            if (chosen is not null && second.SingleUtxoFundingSat > chosen.ValueSat)
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
                        cancellationToken),
                        BuildDeadline,
                        "building")
                    .ConfigureAwait(false);
            }
            catch (SparkExitRefusedException refused)
            {
                // The veto above. Already written for a merchant, so it is passed through verbatim — and, for a
                // first build, the quote it judged by is persisted, so the next attempt selects against the same
                // requirement the operator was just asked to fund.
                if (awaiting)
                    ApplyQuote(record, committed);
                return await FailAsync(record, from, refused.Reason).ConfigureAwait(false);
            }
            catch (SparkExitFundingShortfallException shortfall)
            {
                if (awaiting)
                    ApplyQuote(record, committed);
                return await FailAsync(
                        record,
                        from,
                        following
                            ? DescribeFollowedShortfall(shortfall.RequiredSat, record.FundingAddress, explorerMissed)
                            : shortfall.Message)
                    .ConfigureAwait(false);
            }
            // No catch for a funding conflict, and its absence is the 0.25 change: the SDK removed
            // FundingUtxoConflict entirely, because a spent funding output is no longer an error — it follows
            // each outpoint to whatever it became and accepts fresh funding alongside it. An empty catch here
            // would swallow a real failure into a message about a condition the SDK can no longer report.
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Store {StoreId}: could not build unilateral exit {ExitId} ({Reason})",
                    storeId, record.Id, DescribeSdkFailure(ex));

                if (awaiting)
                    ApplyQuote(record, committed);
                return await FailAsync(
                        record,
                        from,
                        ex is SparkExitDeadlineException
                            // Worded apart from the others because "nothing was signed" is not something a
                            // timeout can promise: the SDK may still be signing. What it can promise is the part
                            // that matters — nothing is broadcast by the SDK, ever, so whatever it signs is inert.
                            ? "Spark could not build this exit: " + DescribeSdkFailure(ex)
                              + ". Nothing was broadcast and nothing was stored; building again is safe."
                            : "Spark could not build this exit: " + DescribeSdkFailure(ex)
                              + ". Nothing was signed or broadcast, so trying again is safe.")
                    .ConfigureAwait(false);
            }

            // Past this point the request's cancellation token is deliberately never used again. The SDK has
            // returned signed transactions that exist in this process and nowhere else, and it will not hand them
            // back without a fresh build and a fresh funding output — so a browser that went away must not be
            // able to skip the write that saves them.
            record.Status = UnilateralExitStatus.Built;
            record.UpdatedUtc = _timeProvider.GetUtcNow();
            // The rate the stored set was signed at, which after a fee bump is the new one.
            record.FeeRateSatPerVbyte = feeRate;
            record.RecoverableValueSat = result.RecoverableValueSat;
            record.TotalFeeSat = result.TotalFeeSat;
            record.SingleUtxoFundingSat = committed?.SingleUtxoFundingSat ?? record.SingleUtxoFundingSat;
            // Everything this build was handed, which is exactly what the SDK echoes back as the response's
            // funding inputs (breez-sdk core unilateral_exit.rs: `funding_inputs: supplied_funding`) and what its
            // documented second attempt passes back. A superset of what was stored before, never a replacement, so
            // no outpoint the signed set may spend is ever forgotten.
            record.FundingUtxosJson = JsonSerializer.Serialize(inputs.ToArray(), JsonOptions);
            // Replaced with exactly what the SDK returned, however short that is. Since 0.25 the build continues
            // from confirmed chain state, so a partial set is the normal result of a resumed attempt and an empty
            // one means everything it planned has already been seen on-chain. Merging the previous set back in
            // would be actively wrong: a transaction missing from the new set is one the SDK has already observed
            // confirm, or one its own re-plan dropped, and keeping a stale copy of it next to the new set invites
            // an operator to broadcast a transaction the SDK has already superseded.
            // Stamped with the build's own time, because a build reads every status from the chain too.
            record.TransactionsJson = SerializeTransactions(
                result.Transactions, _timeProvider.GetUtcNow(), result.Failed);
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
    /// <remarks>
    /// <b>Completing on the chain's word, not only the operator's.</b> Completing an exit takes it off the page,
    /// and the page is the only place its signed transactions are shown — so completing one early is how an
    /// operator loses sight of the steps still to broadcast. The SDK can tell a finished exit from an unfinished
    /// one, so this asks it first and completes only on <see cref="SparkExitVerdict.Done"/>. The operator can still
    /// complete on their own word, with <paramref name="confirmedWithoutVerdict"/>, for the cases a check cannot
    /// answer — a wallet that will not start, a chain service that will not respond, an empty set — and the log
    /// records which of the two it was.
    /// </remarks>
    public Task<UnilateralExitOpResult> MarkCompletedAsync(
        string storeId,
        string recordId,
        bool confirmedWithoutVerdict,
        CancellationToken cancellationToken = default) =>
        GuardAsync(storeId, "completing",
            () => MarkCompletedCoreAsync(storeId, recordId, confirmedWithoutVerdict, cancellationToken));

    /// <inheritdoc />
    public Task<UnilateralExitOpResult> MarkCompletedAsync(
        string storeId,
        string recordId,
        CancellationToken cancellationToken = default) =>
        MarkCompletedAsync(storeId, recordId, false, cancellationToken);

    private async Task<UnilateralExitOpResult> MarkCompletedCoreAsync(
        string storeId,
        string recordId,
        bool confirmedWithoutVerdict,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

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

            SparkExitVerdict? verdict = null;
            if (!confirmedWithoutVerdict)
            {
                var check = await CheckBuiltLockedAsync(storeId, record).ConfigureAwait(false);
                if (!check.Success || check.Verdict is not SparkExitVerdict.Done)
                {
                    return new UnilateralExitOpResult(
                        false, DescribeNotDone(check), check.Record ?? record, check.Verdict);
                }

                verdict = check.Verdict;
                record = check.Record ?? record;
            }

            record.Status = UnilateralExitStatus.Completed;
            record.UpdatedUtc = _timeProvider.GetUtcNow();

            if (!await _records
                    .UpdateAsync(record, UnilateralExitStatus.Built, CancellationToken.None)
                    .ConfigureAwait(false))
            {
                return new UnilateralExitOpResult(false, ExitChangedUnderneath, record);
            }

            if (verdict is SparkExitVerdict.Done)
            {
                _logger.LogInformation(
                    "Store {StoreId}: unilateral exit {ExitId} marked completed; Spark's check confirmed every "
                    + "transaction, the sweep included, is in a block",
                    storeId, record.Id);
            }
            else
            {
                _logger.LogWarning(
                    "Store {StoreId}: unilateral exit {ExitId} marked completed on the operator's word, without a "
                    + "check confirming it finished",
                    storeId, record.Id);
            }

            return new UnilateralExitOpResult(true, null, record, verdict);
        }
        finally
        {
            _running.TryRemove(storeId, out _);
        }
    }

    /// <inheritdoc />
    public Task<UnilateralExitOpResult> AbandonAsync(
        string storeId,
        string recordId,
        CancellationToken cancellationToken = default) =>
        GuardAsync(storeId, "abandoning", () => AbandonCoreAsync(storeId, recordId, cancellationToken));

    private async Task<UnilateralExitOpResult> AbandonCoreAsync(
        string storeId,
        string recordId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

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

    /// <summary>
    /// Why a fresh quote is not the exit that was pinned, or null when it is.
    /// </summary>
    /// <remarks>
    /// Three answers. Every pinned leaf is gone: <see cref="LeavesGone"/>. Some are: the exit has shrunk under the
    /// operator, and building it would spend their funding on less than they were quoted for. None are missing but
    /// nothing is left to build either: every leaf has finished on-chain, which is success, not a fault — the
    /// words send the operator to confirm it rather than to abandon it.
    /// </remarks>
    private string? DescribeCoverage(
        UnilateralExitRecord record,
        SparkExitQuote quote,
        IReadOnlyList<string> pinned)
    {
        var uncovered = quote.UncoveredOf(pinned);

        if (uncovered.Count == 0)
            return quote.IsEmpty ? AllLeavesFinished : null;

        if (quote.IsEmpty && uncovered.Count == pinned.Count)
            return LeavesGone;

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:N0} of the {1:N0} leaves this exit was pinned to are no longer in this wallet's exit data — a "
            + "payment, a sweep, a Stable Balance conversion or Spark's own leaf optimisation has spent or reshaped "
            + "them since the quote — so building now would recover less than the exit you funded. Nothing was "
            + "signed. Abandon this exit and quote a new one for what the wallet holds now; anything on the funding "
            + "address is recoverable from this store's recovery phrase at {2}.",
            uncovered.Count,
            pinned.Count,
            DescribeKeyPath(record) ?? "the exit's funding key path");
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
    /// <summary>
    /// The skipped leaves as one sentence, for the answer to the press that quoted them.
    /// </summary>
    /// <remarks>
    /// The SDK's own reason is carried through untranslated beyond the mapping in
    /// <see cref="SparkSdkClient.DescribeSkippedReason"/>, because several of them are actionable and a summary
    /// would lose the part the operator acts on — "cheaper fees would include it" is a different instruction
    /// from "the operators could not be reached".
    /// </remarks>
    private static string DescribeSkipped(IReadOnlyList<SparkExitSkippedLeaf> skipped) =>
        "Spark left " + skipped.Count.ToString(CultureInfo.InvariantCulture)
        + (skipped.Count == 1 ? " leaf out of this quote: " : " leaves out of this quote: ")
        + string.Join("; ", skipped.Select(leaf =>
            string.Format(
                CultureInfo.InvariantCulture,
                "{0} ({1:N0} sat) — {2}",
                leaf.LeafId, leaf.ValueSat, leaf.Reason)))
        + ". Re-quoting later may include them.";

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

    /// <summary>How long a quote (prepare) may take before the request stops waiting for it.</summary>
    /// <remarks>
    /// <para>
    /// <b>Why any of these exist: the store's gate is held for the whole call, and the SDK's exit calls take no
    /// cancellation.</b> Preparing an exit starts with a best-effort refresh from the Spark operators
    /// (<c>refresh_before_exit</c>) — the one step that needs them — and an operator that accepts a connection
    /// and never answers holds that call, the request thread and this store's exit gate with it. The binding has
    /// no token to cancel it by, so the request stops <em>waiting</em> instead: the gate is released and the
    /// operator gets an answer, while the native call finishes or fails on its own and is observed and dropped.
    /// </para>
    /// <para>
    /// Safe because nothing here broadcasts. A dropped quote recorded nothing; a dropped build may still sign,
    /// but what it signs is inert and never stored, and a later build starts from the chain as usual. The
    /// figures are generous — an exit is dozens of chain lookups and signatures — because the failure they guard
    /// against is a hang, not slowness. Settable for tests only.
    /// </para>
    /// </remarks>
    internal TimeSpan QuoteDeadline { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long a build may take before the request stops waiting. See <see cref="QuoteDeadline"/>.</summary>
    internal TimeSpan BuildDeadline { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a chain check may take before the request stops waiting. See <see cref="QuoteDeadline"/>.</summary>
    internal TimeSpan CheckDeadline { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Waits for one SDK exit call for at most <paramref name="deadline"/>, then gives up waiting.
    /// </summary>
    /// <remarks>
    /// The abandoned call is not cancelled — it cannot be — so its eventual fault is observed here rather than
    /// left to surface as an unobserved task exception long after the request that started it.
    /// </remarks>
    private static async Task<T> WithinDeadlineAsync<T>(Task<T> call, TimeSpan deadline, string what)
    {
        try
        {
            return await call.WaitAsync(deadline).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _ = call.ContinueWith(
                abandoned => _ = abandoned.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw new SparkExitDeadlineException(what, deadline);
        }
    }

    /// <summary>A failure from an SDK exit call, in merchant-facing words, including a missed deadline.</summary>
    private static string DescribeSdkFailure(Exception exception) =>
        exception is SparkExitDeadlineException deadline ? deadline.Message : SparkErrors.Describe(exception);

    /// <summary>
    /// Runs one operation, turning anything it failed to anticipate into a refusal instead of an exception.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every public operation goes through this, and it is not tidiness.</b> These methods are called straight
    /// from request handlers, and BTCPay 2.4's plugin exception handler answers an unhandled exception from a
    /// plugin's request by <em>disabling the plugin and restarting the server</em> — so a database blip, a row
    /// someone edited by hand, or a shape the SDK has never returned before would take every store's Lightning
    /// down over one exit page. The anticipated failures are all handled where they happen, with wording for
    /// the case; this is the backstop for the ones nobody anticipated, and it words them generically on purpose.
    /// </para>
    /// <para>
    /// Safe because of the class's first rule: nothing here broadcasts, so an exception anywhere in an operation
    /// has moved no coins. The one place an exception could cost something — after a successful build, before
    /// its transactions are stored — is handled inside the build itself, where the txids are logged.
    /// </para>
    /// </remarks>
    /// <param name="operation">A gerund for the log line, e.g. "building".</param>
    private async Task<UnilateralExitOpResult> GuardAsync(
        string storeId,
        string operation,
        Func<Task<UnilateralExitOpResult>> body)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Store {StoreId}: unexpected failure while {Operation} a unilateral exit ({Reason})",
                storeId, operation, SparkErrors.Describe(ex));
            return Refuse(UnexpectedFailure);
        }
    }

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
    /// Why a rebuild came up short once the SDK had followed the funding, in terms an operator can act on.
    /// </summary>
    /// <remarks>
    /// The opposite instruction to <see cref="DescribeShortfall"/>, deliberately. A first build funds from one
    /// output, so topping up does not help there; a rebuild hands the SDK everything this exit committed plus
    /// everything else on the address, and the SDK sums what that has become — so here more funding of any shape
    /// does help, and saying "the full amount again as one output" would overstate what is needed while still
    /// being safe. Both are said: the minimum, and the amount that always suffices.
    /// </remarks>
    private static string DescribeFollowedShortfall(long required, string fundingAddress, bool explorerMissed) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "Spark followed the funding this exit already committed to whatever it has become, and together with "
            + "any other output on the funding address it does not cover what is left to build: Spark needs at "
            + "least {0:N0} sat available in total. Send more to {1} — a new output of {0:N0} sat always "
            + "suffices, and whatever is not spent on fees is swept to your destination with the recovered coins — "
            + "and build again once it confirms. The earlier funding is not wasted: the next build follows it "
            + "again.{2}",
            required,
            fundingAddress,
            explorerMissed
                ? " The block explorer could not be read this time, so any new output already on the address was "
                  + "not included; that may be the whole shortfall."
                : string.Empty);

    private string DescribeCommittedFundingUnreadable(UnilateralExitRecord record) =>
        "This exit's record of the funding it has already committed could not be read, so a rebuild cannot hand "
        + "it back to Spark to follow — and building without it would ask you to fund the exit a second time. "
        + "Nothing was signed. Anything left on the funding address is recoverable from this store's recovery "
        + $"phrase at {DescribeKeyPath(record) ?? "the exit's funding key path"}; abandon this exit and quote a "
        + "new one, which picks the leaves up from wherever they are on-chain.";

    /// <summary>
    /// The funding outputs a previous build committed, re-tagged with the funding key's public key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty — and true — for a record no build has succeeded on yet. False only for a column that is present
    /// but unusable: that is a row the service did not write, and guessing at its contents would hand the SDK
    /// outpoints this exit may never have committed.
    /// </para>
    /// <para>
    /// The public key is re-attached from the derivation rather than trusted from the column, for the reason
    /// <see cref="SparkExitFundingExplorer.ListConfirmedAsync"/> gives: an output tagged with the wrong key fails
    /// deep inside the SDK's witness construction. The caller has already established that the derivation
    /// produces this record's funding address, so the key is the right one for every output that pays it.
    /// </para>
    /// </remarks>
    private bool TryReadCommittedFunding(
        UnilateralExitRecord record,
        string pubkeyHex,
        out IReadOnlyList<SparkExitFundingUtxo> committed)
    {
        committed = [];

        if (string.IsNullOrWhiteSpace(record.FundingUtxosJson))
            return true;

        SparkExitFundingUtxo?[]? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<SparkExitFundingUtxo?[]>(record.FundingUtxosJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex,
                "Store {StoreId}: unilateral exit {ExitId} has an unreadable committed-funding column",
                record.StoreId, record.Id);
            return false;
        }

        if (parsed is null ||
            parsed.Any(utxo => utxo is null || !SparkExitTransaction.IsTxid(utxo.Txid) || utxo.ValueSat <= 0))
        {
            _logger.LogError(
                "Store {StoreId}: unilateral exit {ExitId} has a committed-funding column that parsed but is not "
                + "usable",
                record.StoreId, record.Id);
            return false;
        }

        committed = parsed
            .Select(utxo => utxo! with { PubkeyHex = pubkeyHex })
            .DistinctBy(utxo => utxo.OutPoint)
            .ToArray();
        return true;
    }

    /// <summary>
    /// What <see cref="UnilateralExitRecord.TransactionsJson"/> holds: the signed set, and when its statuses were
    /// last read from the chain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The time travels with the statuses because nothing else refreshes them.</b> They are read from the chain
    /// by a build and by a check, and by nothing between — no background task watches an exit — so a page rendered
    /// a day later shows yesterday's "Ready" as if it were current, and a step's watchtower version becomes valid
    /// about fifty blocks (roughly eight hours) after the step itself. Written in the same value as the set, so
    /// the time can never describe a different write than the statuses beside it.
    /// </para>
    /// <para>
    /// A set stored before this envelope existed is a bare array, and still reads — with no time, which the page
    /// renders as "not recorded" rather than inventing one.
    /// </para>
    /// </remarks>
    /// <param name="Failed">
    /// The leaves the operators refused to co-sign for this build, with the SDK's own reason. Stored in the same
    /// envelope as the transactions because it describes the same build: the set is a complete recovery of
    /// everything except these leaves, each of which needs an attempt of its own, and an operator who reloads
    /// the page days later has to still be told that. A set written before this member existed reads back with
    /// null, which means "nothing was reported refused", not "the column is broken".
    /// </param>
    private sealed record StoredTransactionSet(
        DateTimeOffset? StatusesReadUtc,
        SparkExitTransaction[]? Transactions,
        SparkExitFailedLeaf[]? Failed = null);

    private static string SerializeTransactions(
        IEnumerable<SparkExitTransaction> transactions,
        DateTimeOffset readAt,
        IReadOnlyList<SparkExitFailedLeaf>? failed) =>
        JsonSerializer.Serialize(
            new StoredTransactionSet(readAt, transactions.ToArray(), failed?.ToArray() ?? []),
            JsonOptions);

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
    /// <paramref name="transactions"/> — when there is simply nothing built yet, and true with an empty one when
    /// the last build returned no transactions at all.
    /// </returns>
    private bool TryReadTransactions(
        UnilateralExitRecord? record,
        out IReadOnlyList<SparkExitTransaction>? transactions) =>
        TryReadTransactions(record, out transactions, out _);

    private bool TryReadTransactions(
        UnilateralExitRecord? record,
        out IReadOnlyList<SparkExitTransaction>? transactions,
        out IReadOnlyList<SparkExitFailedLeaf>? failed) =>
        TryReadTransactions(record, out transactions, out _, out failed);

    /// <param name="statusesReadUtc">
    /// When the stored statuses were last read from the chain, or null for a set written before that was
    /// recorded. See <see cref="StoredTransactionSet"/>.
    /// </param>
    private bool TryReadTransactions(
        UnilateralExitRecord? record,
        out IReadOnlyList<SparkExitTransaction>? transactions,
        out DateTimeOffset? statusesReadUtc,
        out IReadOnlyList<SparkExitFailedLeaf>? failed)
    {
        transactions = null;
        statusesReadUtc = null;
        failed = null;

        if (record?.TransactionsJson is not { } json || string.IsNullOrWhiteSpace(json))
            return true;

        SparkExitTransaction[]? parsed;
        try
        {
            // Two shapes: the bare array every set was stored as before the read time was recorded, and the
            // envelope written since. Told apart by the first character rather than by trying one and catching,
            // so a malformed envelope is reported as malformed instead of as "not an array".
            if (json.TrimStart().StartsWith('{'))
            {
                var envelope = JsonSerializer.Deserialize<StoredTransactionSet>(json, JsonOptions);
                parsed = envelope?.Transactions;
                statusesReadUtc = envelope?.StatusesReadUtc;
                failed = envelope?.Failed;
            }
            else
            {
                parsed = JsonSerializer.Deserialize<SparkExitTransaction[]>(json, JsonOptions);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex,
                "Store {StoreId}: unilateral exit {ExitId} has an unreadable transaction set",
                record.StoreId, record.Id);
            return false;
        }

        if (parsed is null || parsed.Any(IsMalformed))
        {
            _logger.LogError(
                "Store {StoreId}: unilateral exit {ExitId} has a transaction set that parsed but is not usable",
                record.StoreId, record.Id);
            return false;
        }

        // An empty array is a readable answer, not a broken one. The build writes exactly what the SDK returned
        // however short it is (see BuildAsync), and an empty set is one of those answers — Spark found nothing
        // left to sign for these leaves. Calling it unreadable told the operator their only copy of the exit was
        // damaged, and the page's own branch for an empty set could never render.
        transactions = parsed;
        return true;

        // Status is checked for null before it is dereferenced, and it is the member most likely to arrive null:
        // SparkExitTxStatus is a reference record, and System.Text.Json fills a missing or explicit-null member of
        // a positional record with null whatever the declared nullability says. An exception here is not a page
        // error — it escapes a request, and BTCPay disables the plugin and restarts the server over it.
        //
        // The hex and the txids are checked for what they are, not only for being present: the page pastes the hex
        // into a shell command for the operator to run (see SparkExitTransaction.IsTxid), so a row whose "hex"
        // could carry a quote must never reach it.
        static bool IsMalformed(SparkExitTransaction? transaction) =>
            transaction is null
            || !SparkExitTransaction.IsTxid(transaction.Txid)
            || !SparkExitTransaction.IsTransactionHex(transaction.TxHex)
            || (transaction.CpfpTxHex is not null && !SparkExitTransaction.IsTransactionHex(transaction.CpfpTxHex))
            || transaction.DependsOn is null
            || transaction.DependsOn.Any(parent => !SparkExitTransaction.IsTxid(parent))
            || transaction.Status is null
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

/// <summary>
/// An SDK exit call that did not answer inside its deadline, so the request stopped waiting for it.
/// </summary>
/// <remarks>
/// A <see cref="TimeoutException"/> so a caller that only knows the base type still reads it as one. Its message
/// is written for a merchant, and names the one step of an exit that needs the Spark operators, because that is
/// the usual reason: see <see cref="SparkUnilateralExitService.QuoteDeadline"/>.
/// </remarks>
internal sealed class SparkExitDeadlineException : TimeoutException
{
    public SparkExitDeadlineException(string what, TimeSpan deadline)
        : base(string.Format(
            CultureInfo.InvariantCulture,
            "Spark did not finish {0} this exit within {1:N0} seconds. Preparing an exit first tries to refresh the "
            + "wallet from the Spark operators, and that can hang while they are unreachable; the wait was abandoned "
            + "so this store is not held up behind it",
            what,
            deadline.TotalSeconds))
    {
    }
}
