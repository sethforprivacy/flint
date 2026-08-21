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
/// This service holds every guard; the controller renders and redirects and decides nothing. All five
/// methods behave as if the feature does not exist when <see cref="Constants.UnilateralExitEnabled"/>
/// is false, because the controller's gate is a courtesy, not the enforcement.
/// </para>
/// <para>
/// <b>Nothing here broadcasts.</b> Phase 0 ends at a signed, ordered transaction set persisted on the
/// <see cref="UnilateralExitRecord"/>; the operator broadcasts each package themselves (fan-out first
/// and alone, then tree-node packages in <c>depends_on</c> order waiting for confirmation between,
/// refunds after their CSV timelocks, sweep last and alone). The SDK in use (0.22.0) still needs the
/// operators reachable to prepare an exit; exit-from-local-state arrives with a later SDK bump.
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
    /// Guards: feature gate, wallet running, disclosure acknowledged, fee rate in [1, 500], destination
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
    /// Discovers the funding UTXOs on the record's funding address, re-quotes the record's own leaves,
    /// and builds the signed transaction set onto the record.
    /// </summary>
    /// <remarks>
    /// Refuses when the discovered funding falls short of the quoted requirement, and re-checks
    /// recoverable-exceeds-fee against the fresh quote before signing (the persisted quote is display
    /// state, not the guard). Safe to call again after a failure: the SDK resumes from chain state and
    /// a shortfall or spent-funding conflict lands on the record as <see cref="UnilateralExitRecord.LastError"/>.
    /// </remarks>
    Task<UnilateralExitOpResult> BuildAsync(string storeId, string recordId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the record abandoned so the store can start over. Abandoning moves no money and cancels
    /// nothing on-chain: transactions already broadcast stay valid, which the page says out loud.
    /// </summary>
    Task<UnilateralExitOpResult> AbandonAsync(string storeId, string recordId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a built record completed: the operator confirms they have broadcast the set and the sweep
    /// has confirmed. The plugin cannot verify this itself in Phase 0 (nothing watches the chain), so
    /// this is the operator's statement of fact — but without it, Abandon would be the only way a
    /// finished exit ever leaves the active state, and abandoning is the wrong verb for success.
    /// </summary>
    Task<UnilateralExitOpResult> MarkCompletedAsync(string storeId, string recordId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the explorer override used for funding discovery. Null or blank clears it. This is the
    /// feature's one piece of real configuration, so it is settable from the page that reports it
    /// missing; validation (absolute http/https URL) is here, not in the controller.
    /// </summary>
    Task<UnilateralExitOpResult> SetExplorerUrlAsync(string storeId, string? esploraApiUrl, CancellationToken cancellationToken = default);
}

/// <summary>
/// What happened when a write was attempted. <paramref name="Error"/> is merchant-facing copy, set
/// exactly when <paramref name="Success"/> is false; <paramref name="Record"/> is the record the
/// attempt created or updated, when one exists either way.
/// </summary>
public sealed record UnilateralExitOpResult(bool Success, string? Error, UnilateralExitRecord? Record);

/// <summary>
/// Everything the exit page renders in one read. The service is the only reader and writer of the
/// record's JSON columns: the page receives typed data here and no other layer deserializes the blob,
/// so the write format has exactly one owner.
/// </summary>
/// <param name="WalletRunning">False hides every form: nothing can be quoted without a live wallet.</param>
/// <param name="DisclosureAcknowledged">Gates the quote form behind the disclosure form.</param>
/// <param name="BalanceSats">The wallet balance, for context next to the quote form.</param>
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
public sealed record UnilateralExitPageData(
    bool WalletRunning,
    bool DisclosureAcknowledged,
    long BalanceSats,
    UnilateralExitRecord? ActiveRecord,
    IReadOnlyList<UnilateralExitRecord> History,
    long? FundingReceivedSat,
    long? FundingLargestOutputSat,
    int? LeafCount,
    string? FundingKeyPath,
    IReadOnlyList<SparkExitTransaction>? Transactions,
    bool TransactionsUnreadable);
