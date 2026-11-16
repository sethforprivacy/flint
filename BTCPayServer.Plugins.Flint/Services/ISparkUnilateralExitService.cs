using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// The unilateral-exit flow: quote which leaves are worth forcing on-chain, collect operator-supplied
/// funding, and build the signed transaction set the operator broadcasts by hand.
/// </summary>
/// <remarks>
/// <para>
/// This service holds every guard; the controller renders and redirects and decides nothing. The one that
/// matters most — the store's stored disclosure acknowledgement — is re-read here before every quote and
/// build, because a check the controller made is a courtesy, not the enforcement.
/// </para>
/// <para>
/// <b>Nothing here broadcasts.</b> Phase 0 ends at a signed, ordered transaction set persisted on the
/// <see cref="UnilateralExitRecord"/>; the operator broadcasts each package themselves (fan-out first
/// and alone, then tree-node packages in <c>depends_on</c> order waiting for confirmation between,
/// refunds after their CSV timelocks, sweep last and alone). Since SDK 0.25 quoting and building read each
/// leaf's pre-signed chain from local storage, so neither needs the operators reachable — only a leaf whose
/// chain was collected while they still were (or restored from an exit-state backup) can be exited, and
/// preparing still makes a best-effort refresh from them first, which is why its calls carry a deadline.
/// </para>
/// <para>
/// One exit at a time per store: a store with an active record (awaiting funding or built) refuses a
/// new quote, because two exits would compete for the same leaves and the same funding UTXOs.
/// </para>
/// </remarks>
public interface ISparkUnilateralExitService
{
    /// <summary>
    /// Everything the exit page shows: settings state, the active record, history, and — while a
    /// record is awaiting funding — what the funding address holds according to the explorer.
    /// </summary>
    Task<UnilateralExitPageData> ReadAsync(string storeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that the operator has read and accepted the disclosure. Server-side state, not a UI
    /// checkbox: <see cref="QuoteAsync"/> refuses until this has been stored, the same pattern Stable
    /// Balance uses.
    /// </summary>
    Task<UnilateralExitOpResult> AcknowledgeDisclosureAsync(string storeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Quotes an auto-selected exit and persists it as the store's active record, awaiting funding.
    /// </summary>
    /// <remarks>
    /// Guards: wallet running, disclosure acknowledged, fee rate in [1, 500], destination
    /// parses for the store's network, no other active record. An empty auto-selection (nothing worth
    /// exiting at this rate) and a quote whose fee exceeds what it recovers are refusals, not errors.
    /// The quoted leaf ids are persisted on the record so the build re-quotes those exact leaves.
    /// </remarks>
    Task<UnilateralExitOpResult> QuoteAsync(
        string storeId,
        long feeRateSatPerVbyte,
        string destinationAddress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-quotes the record's own leaves, gathers its funding, and builds the signed transaction set onto the
    /// record.
    /// </summary>
    /// <remarks>
    /// A first build discovers the funding on the record's address and spends the smallest single output that
    /// covers the fresh quote. A rebuild — after a Redo, an unverified status, or to raise the fee — passes back
    /// every output earlier builds committed (<see cref="UnilateralExitRecord.FundingUtxosJson"/>) plus anything
    /// else confirmed on the address, and the SDK follows the committed ones to whatever they became. It
    /// re-checks recoverable-exceeds-fee against the fresh quote before signing (the persisted quote is display
    /// state, not the guard). Safe to call again after a failure: the SDK resumes from chain state and a
    /// shortfall lands on the record as <see cref="UnilateralExitRecord.LastError"/>.
    /// </remarks>
    /// <param name="feeRateSatPerVbyte">
    /// Null builds at the record's own rate. A value re-prices this build at that rate and, if it succeeds, makes
    /// it the record's rate — the SDK's way to raise the fee on an exit that stopped confirming. A built exit's
    /// rate may stay or rise but not fall, because a replacement has to pay more than what it replaces.
    /// </param>
    Task<UnilateralExitOpResult> BuildAsync(
        string storeId,
        string recordId,
        long? feeRateSatPerVbyte,
        CancellationToken cancellationToken = default);

    /// <summary>Builds at the record's own rate; see the overload.</summary>
    Task<UnilateralExitOpResult> BuildAsync(string storeId, string recordId, CancellationToken cancellationToken = default) =>
        BuildAsync(storeId, recordId, null, cancellationToken);

    /// <summary>
    /// Marks the record abandoned so the store can start over. Abandoning moves no money and cancels
    /// nothing on-chain: transactions already broadcast stay valid, which the page says out loud.
    /// </summary>
    Task<UnilateralExitOpResult> AbandonAsync(string storeId, string recordId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a built record completed once Spark's check reports it <see cref="SparkExitVerdict.Done"/> — every
    /// transaction, the sweep included, in a block. Without it, Abandon would be the only way a finished exit
    /// ever leaves the active state, and abandoning is the wrong verb for success.
    /// </summary>
    /// <remarks>
    /// Checked rather than taken on trust, because completing an exit takes its signed transactions off the
    /// page, and completing one early hides the steps still to broadcast. Nothing watches the chain between
    /// presses, so the check runs as part of this call; a verdict other than Done is a refusal that says what
    /// the check found.
    /// </remarks>
    /// <param name="confirmedWithoutVerdict">
    /// The operator's own statement that the sweep confirmed, for when a check cannot answer — the wallet will
    /// not start, the chain service will not respond, or the last build returned nothing to check. Completes
    /// without asking, and is logged as such.
    /// </param>
    Task<UnilateralExitOpResult> MarkCompletedAsync(
        string storeId,
        string recordId,
        bool confirmedWithoutVerdict,
        CancellationToken cancellationToken = default);

    /// <summary>Completes only on a Done verdict; see the overload.</summary>
    Task<UnilateralExitOpResult> MarkCompletedAsync(
        string storeId,
        string recordId,
        CancellationToken cancellationToken = default) =>
        MarkCompletedAsync(storeId, recordId, false, cancellationToken);

    /// <summary>
    /// Asks the chain how far a built exit has got, refreshes the record's stored transaction statuses from the
    /// answer, and reports the SDK's verdict on the caller's result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Safe to call as often as an operator likes: it broadcasts nothing and signs nothing.</b> The SDK's
    /// check reads the chain and nothing else — no leaves, no funding, no signer — which is what makes a built
    /// exit followable from a stored record alone, days after the build, on a plugin that has been restarted
    /// since. It does need the store's wallet <em>running</em>: the check is a method on the connected SDK
    /// instance, and the plugin has no chain-only handle to ask instead, so a stopped wallet is refused with
    /// that reason rather than reported as a chain failure.
    /// </para>
    /// <para>
    /// The refreshed transactions are persisted in place of the stored set, replacing the statuses with what the
    /// chain now reports. The <see cref="SparkExitVerdict"/> is deliberately <b>not</b> persisted: it is derived
    /// state, recomputed by the SDK from the chain on every call, and a stored copy would be a claim about the
    /// chain that goes stale the moment it is written — an operator would see "on track" on a page rendered from
    /// a row nothing has refreshed. It travels back on <see cref="UnilateralExitOpResult.Verdict"/> instead.
    /// </para>
    /// <para>
    /// Refused for anything that is not <see cref="UnilateralExitStatus.Built"/>: there is no transaction set to
    /// check before a build, and nothing to say about one after it has been completed or abandoned.
    /// </para>
    /// </remarks>
    Task<UnilateralExitOpResult> CheckAsync(
        string storeId,
        string recordId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues a pasted unilateral-exit backup blob for import in the pending slot (see
    /// <see cref="IExitStateBackupStore.AddPendingAsync"/>), where no automatic pass can overwrite it; or, when
    /// the argument is null or blank, clears the queue, the automatic backup and the deprecated settings slot.
    /// The caller imports the queue into the running wallet through
    /// <c>ISparkStoreRuntime.ImportPendingExitStateAsync</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The blob is sensitive and must never be logged or rendered.</b> It carries every leaf of the wallet and
    /// the transactions under them, so it discloses the balance, how that balance is split and what the wallet
    /// has received and spent. Anything that shows it — an error message, a log line, a page — leaks the
    /// store's financial history to whoever can read that surface.
    /// </para>
    /// <para>
    /// <b>The format is not validated here, deliberately.</b> It is the SDK's own opaque encoding and the SDK is
    /// the only thing that can judge whether a value is usable; a validator invented on this side would reject
    /// valid backups from a future SDK, which is the one input that has to keep working — the operator pasting it
    /// is doing so because the wallet's own storage is already lost. Only an absurd size is refused, because the
    /// SDK documents a real wallet's backup as reaching several megabytes, so a value past the cap is a paste
    /// error rather than a backup.
    /// </para>
    /// </remarks>
    /// <param name="exitState">The exported blob, or null/blank to clear what is stored.</param>
    Task<UnilateralExitOpResult> SetExitStateBackupAsync(
        string storeId,
        string? exitState,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the explorer override used for funding discovery. Null or blank clears it. This is the
    /// feature's one piece of real configuration, so it is settable from the page that reports it
    /// missing; validation (an absolute http/https base URL with no credentials, query or fragment, and not a
    /// link-local, unspecified or multicast address) is here, not in the controller.
    /// </summary>
    /// <remarks>
    /// Who may call it is the one guard this service cannot hold, because it does not see the caller: the value
    /// makes the server send requests to a host of the setter's choosing on every page view, so the controller
    /// restricts it to server administrators. Anything else that calls this must do the same.
    /// </remarks>
    Task<UnilateralExitOpResult> SetExplorerUrlAsync(string storeId, string? esploraApiUrl, CancellationToken cancellationToken = default);
}

/// <summary>
/// What happened when a write was attempted. <paramref name="Error"/> is merchant-facing copy, set
/// exactly when <paramref name="Success"/> is false; <paramref name="Record"/> is the record the
/// attempt created or updated, when one exists either way.
/// </summary>
/// <remarks>
/// <see cref="Verdict"/> is set by <see cref="ISparkUnilateralExitService.CheckAsync"/> and by nothing
/// else; every other path leaves it null, which is why it defaults. It is a <b>snapshot of the chain at
/// the moment of the call</b> and is deliberately not persisted — the SDK recomputes it from the chain
/// on every check, so a stored copy would be a stale claim about the chain rendered as if it were live.
/// </remarks>
public sealed record UnilateralExitOpResult(
    bool Success,
    string? Error,
    UnilateralExitRecord? Record,
    SparkExitVerdict? Verdict = null);

/// <summary>
/// Everything the exit page renders in one read. The service is the only reader and writer of the
/// record's JSON columns: the page receives typed data here and no other layer deserializes the blob,
/// so the write format has exactly one owner.
/// </summary>
/// <param name="WalletRunning">False hides every form: nothing can be quoted without a live wallet.</param>
/// <param name="DisclosureAcknowledged">Gates the quote form behind the disclosure form.</param>
/// <param name="BalanceSats">The wallet balance, for context next to the quote form.</param>
/// <param name="RecommendedFeeRateSatPerVbyte">
/// A rate fetched from the block explorer for the quote form to open at, or null when there is no recommendation
/// to be had — off mainnet with no explorer override, or the explorer was unreachable. Populated only while no
/// exit is in flight, because that is the only time the form renders and its rate is the only thing the answer
/// would decide. <b>Null is not zero and is not a default:</b> the caller falls back to
/// <see cref="SparkUnilateralExitService.DefaultFeeRateSatPerVbyte"/>, and a service that invented a rate the
/// explorer did not report would be putting a claim about the fee market in front of an operator who is about to
/// fund an exit at it.
/// </param>
/// <param name="ActiveRecord">The store's one in-flight exit (awaiting funding or built), or null.</param>
/// <param name="History">Newest-first <b>terminal</b> records (completed/abandoned), bounded, with the
/// heavy JSON columns left unloaded — the history table renders five scalar columns and must not drag
/// every signed transaction set out of the database to do it.</param>
/// <param name="FundingReceivedSat">
/// Total confirmed satoshis the explorer reports on the active record's funding address, or null when
/// there is no active record awaiting funding, no explorer is configured for this network, or the
/// explorer was unreachable — the page distinguishes "unknown" from zero.
/// </param>
/// <param name="FundingLargestOutputSat">
/// The largest single confirmed output on the funding address. This, not <paramref name="FundingReceivedSat"/>,
/// is the number the build's single-output rule is judged by, and the page compares this one against
/// the requirement so split funding never reads as complete.
/// </param>
/// <param name="LeafCount">Leaves pinned by the active record's quote, or null without one.</param>
/// <param name="FundingKeyPath">
/// The BIP32 path of the active record's funding key, for hand recovery of funding sats from the seed.
/// </param>
/// <param name="Transactions">
/// The active record's built transaction set, deserialized and sanity-checked by the service, or null
/// when there is no built set or the column is unreadable (see <paramref name="TransactionsUnreadable"/>).
/// </param>
/// <param name="TransactionsUnreadable">
/// True when a built record's transaction column could not be read back as a well-formed set — malformed
/// syntax or structurally null members. The page renders that as an explanation, never as an exception.
/// </param>
/// <param name="PendingBroadcast">
/// The subset of <paramref name="Transactions"/> whose status says it may be broadcast right now, in the
/// SDK's own order — the transactions the page tells the operator to send, and the reason it exists is that
/// a built exit runs to a dozen rows of which at most one or two are actionable at any moment. Null when
/// there is no built set, or when the set read back was empty.
/// </param>
/// <param name="StatusesReadUtc">
/// When the statuses in <paramref name="Transactions"/> were last read from the chain — by the build that stored
/// them or by the last check. Nothing refreshes them between presses, so the page states this beside them. Null for
/// a set stored before the time was recorded, and when there is no readable set.
/// </param>
/// <param name="FailedLeaves">
/// Leaves the last build could not recover because the operators refused to co-sign them, with the SDK's
/// reason, or null when none were reported. Part of the built set rather than of the exit: the transactions
/// beside them are a complete recovery of everything else, and each of these needs an attempt of its own.
/// </param>
/// <param name="LoadError">
/// Set when the read itself failed — the exit records could not be loaded — and every other member is then the
/// empty page. The view renders this instead of any form: an empty page with a quote form beside it would read
/// as "no exit is in progress" when the truth is "this page could not tell".
/// </param>
public sealed record UnilateralExitPageData(
    bool WalletRunning,
    bool DisclosureAcknowledged,
    long BalanceSats,
    long? RecommendedFeeRateSatPerVbyte,
    UnilateralExitRecord? ActiveRecord,
    IReadOnlyList<UnilateralExitRecord> History,
    long? FundingReceivedSat,
    long? FundingLargestOutputSat,
    int? LeafCount,
    string? FundingKeyPath,
    IReadOnlyList<SparkExitTransaction>? Transactions,
    bool TransactionsUnreadable,
    IReadOnlyList<SparkExitTransaction>? PendingBroadcast,
    string? LoadError = null,
    DateTimeOffset? StatusesReadUtc = null,
    IReadOnlyList<SparkExitFailedLeaf>? FailedLeaves = null);
