using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Flint.Data;

/// <summary>
/// Durable storage for <see cref="UnilateralExitRecord"/>s.
/// </summary>
/// <remarks>
/// An interface rather than a direct <c>DbContext</c> dependency for the same reason as the sweep store's: the
/// exit service decides whether real money is recoverable and has to be unit-testable without a Postgres server.
/// The production implementation is <see cref="EfUnilateralExitRecordStore"/>.
/// </remarks>
public interface IUnilateralExitRecordStore
{
    /// <summary>
    /// Inserts a freshly quoted exit, unless the store already has an active one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called after the quote and before the operator is shown a funding address, so a failure here means they
    /// are never told to send sats towards an exit that was not recorded — the correct failure direction, because
    /// sats on an unrecorded funding address are only recoverable by re-deriving the key by hand.
    /// </para>
    /// <para>
    /// <b>"One active exit per store" is a database guarantee, not a convention.</b> The service checks for an
    /// active row before quoting, but that check and this insert are two statements: a second server, or a second
    /// request that slipped past the in-process gate, could pass the check and then insert. The unique index over
    /// the store's active exits closes that window, and this method reports the collision as a refusal rather
    /// than letting a provider exception reach the service — which would otherwise turn a perfectly ordinary race
    /// into "the quote could not be recorded".
    /// </para>
    /// <para>
    /// A duplicate id still throws. That is a programming error rather than a race, and swallowing it would let a
    /// caller reusing an id believe its record was stored.
    /// </para>
    /// </remarks>
    /// <returns>
    /// True when the row was inserted; false when the store already has an exit awaiting funding or built.
    /// </returns>
    Task<bool> CreateAsync(UnilateralExitRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes back the mutable half of a row, but only while it is still in the status the caller read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Store-scoped, guarded on the id <b>and</b> on <paramref name="expectedStatus"/>, so this is a
    /// compare-and-set rather than a read-modify-write — the same discipline
    /// <see cref="ISweepRecordStore.TryResolveAsync"/> applies to a sweep. The status the caller read is the
    /// status its whole decision was made against: an abandon that started from a row awaiting funding must not
    /// land on the same row after a build has filled it with signed transactions, and a build that started from
    /// an awaiting row must not land after the operator abandoned it.
    /// </para>
    /// <para>
    /// <b>The identity of an exit is not writable.</b> Its store, destination, fee rate, creation time, funding
    /// address, funding key index and leaf set are fixed at quote time and this method leaves them alone even if
    /// the passed record disagrees — those are the values the operator approved and funded against, and the
    /// signed transactions are only meaningful relative to them.
    /// </para>
    /// <para>
    /// The two JSON blobs are coalesced rather than assigned: a null means "nothing new to say" and never "clear
    /// it". Those columns hold the exit's only copy of its signed transactions and the outpoint they spend, and
    /// the paths that write a status or an error — abandoning, recording a failure, a history row projected
    /// without its blobs — have no business erasing them.
    /// <see cref="UnilateralExitRecord.LastError"/> is the one exception and is assigned, because a build that
    /// gets further must be able to clear the previous attempt's complaint.
    /// </para>
    /// </remarks>
    /// <param name="expectedStatus">The status the caller read, and the only one this update may overwrite.</param>
    /// <returns>
    /// True when a row was updated; false when the store has no such exit or it has since moved out of
    /// <paramref name="expectedStatus"/>.
    /// </returns>
    Task<bool> UpdateAsync(
        UnilateralExitRecord record,
        UnilateralExitStatus expectedStatus,
        CancellationToken cancellationToken = default);

    /// <summary>One exit, whole, scoped to a store so one store cannot read another's.</summary>
    Task<UnilateralExitRecord?> GetAsync(
        string storeId,
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The store's exit that has not reached a terminal state, or null when there is none.
    /// </summary>
    /// <remarks>
    /// This is the single-flight guard the service reads before quoting: two exits would compete for the same
    /// leaves, so the second would build a tree over statechain nodes the first has already committed to signed
    /// transactions — and neither operator would know which set to broadcast. There can be at most one such row
    /// (see <see cref="CreateAsync"/>); the query is still ordered newest-first so that a database somehow holding
    /// two — restored from a backup taken before the index existed, say — describes the one the operator is
    /// looking at rather than an arbitrary one.
    /// </remarks>
    Task<UnilateralExitRecord?> GetActiveForStoreAsync(
        string storeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Newest-first page of a store's <b>finished</b> exits, for the history list on the exit page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Terminal statuses only — completed and abandoned. The active exit has its own panel on the page, and
    /// listing it twice invites an operator to read the history row's status as a second exit.
    /// </para>
    /// <para>
    /// <b>The JSON columns are deliberately not loaded.</b> The history table renders scalars; a store with
    /// twenty past exits would otherwise pull twenty signed transaction sets — the largest text in this schema —
    /// out of the database to render a date and a status. The returned rows therefore carry an empty
    /// <see cref="UnilateralExitRecord.LeafIdsJson"/> and null blobs, which is what
    /// <see cref="UpdateAsync"/>'s coalescing makes harmless.
    /// </para>
    /// </remarks>
    /// <param name="limit">Maximum rows to return. Must be positive.</param>
    Task<IReadOnlyList<UnilateralExitRecord>> ListTerminalForStoreAsync(
        string storeId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The funding-key index a new exit for this store should use: one past the highest ever issued.
    /// </summary>
    /// <remarks>
    /// Computed over <b>every</b> row of the store, terminal ones included, so an index is never reused. Reusing
    /// one would re-issue a funding address that may still hold sats from an abandoned exit, and the next build
    /// would then select a stale output as if the operator had just sent it — see
    /// <see cref="UnilateralExitRecord.FundingKeyIndex"/>. Zero for a store with no exits yet.
    /// </remarks>
    Task<long> NextFundingKeyIndexAsync(string storeId, CancellationToken cancellationToken = default);
}
