using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Sdk;

namespace BTCPayServer.Plugins.Flint.Data;

/// <summary>
/// The terminal state a sweep record is being moved into, with everything learned about the exit.
/// </summary>
/// <param name="Status">The state to move to.</param>
/// <param name="FeeSats">Fee actually charged, when known. Null leaves any recorded value alone.</param>
/// <param name="TxId">Cooperative-exit txid, when known. Null leaves any recorded value alone.</param>
/// <param name="Error">Merchant-facing reason, for a refusal or failure.</param>
/// <param name="CompletedAt">When the outcome became known.</param>
/// <param name="RefusalCode">
/// The refusal's stable identity, when this resolution is a refusal. <see cref="SweepRefusalCode.None"/> leaves any
/// recorded value alone, so a Sent → Confirmed step does not erase one.
/// </param>
/// <remarks>
/// Null means "nothing new to say" for the two nullable value fields rather than "clear it". The distinction
/// matters on the Sent → Confirmed step, where the txid arrived with the pending event and the completion event
/// carries no new information: a coalescing write keeps the txid, an overwriting one would erase the only record
/// of which transaction the merchant's money is in.
/// </remarks>
/// <param name="ConversionStatus">
/// How far the bridge provider has got, for a cross-chain sweep. Null leaves any recorded value alone, on the
/// same "nothing new to say" reading as the two fields above — a cooperative exit never has one, and a
/// cross-chain row must not have its last known provider state erased by a resolution that could not read it.
/// </param>
/// <param name="DeliveredAmountBaseUnits">
/// What actually arrived at the destination, in destination-asset base units, once the provider reports it. Also
/// coalesced: it appears late, through no event, and a later poll that cannot see it must not unset it.
/// </param>
public sealed record SweepResolution(
    SweepRecordStatus Status,
    long? FeeSats,
    string? TxId,
    string? Error,
    DateTimeOffset CompletedAt,
    SweepRefusalCode RefusalCode = SweepRefusalCode.None,
    SparkConversionStatus? ConversionStatus = null,
    string? DeliveredAmountBaseUnits = null,
    string? ProviderOrderId = null);

/// <summary>
/// Durable storage for <see cref="SweepRecord"/>s.
/// </summary>
/// <remarks>
/// An interface rather than a direct <c>DbContext</c> dependency for the same reason as the invoice store's: the
/// sweep engine moves real money and has to be unit-testable without a Postgres server. The production
/// implementation is <see cref="EfSweepRecordStore"/>, and both are held to
/// <c>SweepRecordStoreContractTests</c>.
/// </remarks>
public interface ISweepRecordStore
{
    /// <summary>
    /// Inserts a sweep record. Throws when a row with the same idempotency key already exists — which would
    /// mean two sweeps were about to share a key, and must never be papered over.
    /// </summary>
    /// <remarks>
    /// Called <b>before</b> the send. If this throws, no send happens, which is the correct failure direction.
    /// </remarks>
    Task AddAsync(SweepRecord record, CancellationToken cancellationToken = default);

    /// <summary>One record, scoped to a store so one store cannot read another's sweeps.</summary>
    Task<SweepRecord?> GetAsync(
        string storeId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>Newest-first page of a store's sweeps, for the history table.</summary>
    Task<IReadOnlyList<SweepRecord>> ListAsync(
        string storeId,
        int offset,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Total number of sweep records for a store, for the pager.</summary>
    Task<int> CountAsync(string storeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every sweep a store sent or tried to send that was created in [<paramref name="from"/>,
    /// <paramref name="to"/>], oldest first, for BTCPay's reporting page.
    /// </summary>
    /// <remarks>
    /// <see cref="SweepRecordStatus.Refused"/> rows are left out: they record a decision not to move money, and a
    /// store sitting below its fee ceiling writes one every pass, so they would bury the sweeps a report is read for.
    /// </remarks>
    Task<IReadOnlyList<SweepRecord>> ListForReportAsync(
        string storeId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every record for a store that has not reached a settled outcome — every
    /// <see cref="SweepRecordStatus.Pending"/> row, and every <see cref="SweepRecordStatus.Sent"/> row created
    /// after <paramref name="sentCreatedAfter"/> — oldest first.
    /// </summary>
    /// <param name="sentCreatedAfter">
    /// Age bound on <see cref="SweepRecordStatus.Sent"/> rows only. A Sent exit is already on-chain with a
    /// recorded txid, so re-reading it forever to upgrade a label to "Confirmed" is a growing cost for no benefit.
    /// <para>
    /// <b>Pending rows are deliberately unbounded.</b> An earlier revision aged them out too, which meant a store
    /// whose wallet stayed down for longer than the window left a row that would never be resolved and never
    /// block again — a permanent "Unresolved" in the merchant's history, for a sweep that may well have gone
    /// through. A Pending row is exactly the thing that must be chased until it is answered.
    /// </para>
    /// </param>
    /// <remarks>
    /// This is the crash-recovery query. The engine resolves each of these against
    /// <c>GetPayment(idempotencyKey)</c> before it considers starting a new sweep, so a sweep that was in flight
    /// when the process died is settled by fact rather than guessed at, and never blind-retried.
    /// </remarks>
    Task<IReadOnlyList<SweepRecord>> ListUnresolvedAsync(
        string storeId,
        DateTimeOffset sentCreatedAfter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The row already standing for an ongoing automatic refusal of this kind, or null when there is not one.
    /// </summary>
    /// <param name="code">The refusal's stable identity. Never the rendered message — see
    /// <see cref="SweepRefusalCode"/>.</param>
    /// <param name="mode">
    /// The destination mode in force. Part of the match, so changing where sweeps go starts a fresh row rather
    /// than silently extending the tally of a refusal about the old destination.
    /// </param>
    /// <param name="activeSince">
    /// How recently the row must last have been seen to count as the same ongoing condition. A refusal that
    /// stopped for a while and came back is a new row, so the history shows both episodes.
    /// </param>
    /// <remarks>
    /// <para>
    /// This is what keeps a store parked on a refusal from accumulating a row every couple of minutes forever —
    /// which is the <em>expected</em> state for a default-configured store on mainnet, where broadcast fees are an
    /// order of magnitude above the regtest levels the defaults were calibrated against.
    /// </para>
    /// <para>
    /// Deliberately a search by reason rather than "is the newest row this refusal?". Any intervening row — a
    /// manual refusal, a successful sweep — would defeat the latter, and an ongoing condition does not stop being
    /// ongoing because something else happened once in the middle of it.
    /// </para>
    /// </remarks>
    Task<SweepRecord?> FindOpenRefusalAsync(
        string storeId,
        SweepRefusalCode code,
        SweepDestinationMode mode,
        DateTimeOffset activeSince,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records another sighting of an ongoing refusal: bumps its attempt count, moves its last-seen time, and
    /// refreshes its message so the figures shown are current.
    /// </summary>
    /// <remarks>
    /// Guarded on the row still being a refusal, so this can never touch a row that describes a real send.
    /// Returns false when it does not apply, and the caller then files a new row — failing closed towards
    /// recording too much rather than losing the fact that a sweep was declined.
    /// </remarks>
    Task<bool> TryRecordRepeatRefusalAsync(
        string storeId,
        string idempotencyKey,
        string error,
        DateTimeOffset seenAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the provider quote id a cross-chain send is actually committing to, before it is sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Separate from <see cref="AddAsync"/> because the id is not known when the row is written.</b> The
    /// engine takes a pre-flight quote to decide whether to sweep at all, then the SDK prepares again inside
    /// the send — and Orchestra mints a fresh quote id per prepare. The id on the row at insert time is
    /// therefore the wrong one, and on the token path, where it is the only handle on the send, "the wrong one"
    /// means recovery can never match and a delivered sweep is written off as never sent.
    /// </para>
    /// <para>
    /// Called from inside the send's approval callback, which is the one moment the committed quote is visible
    /// and the send has not yet happened.
    /// </para>
    /// <para>
    /// Guarded on the row still being <see cref="SweepRecordStatus.Pending"/>, so it cannot rewrite the identity
    /// of a sweep that has already resolved.
    /// </para>
    /// </remarks>
    /// <returns>True when this call is what changed the row.</returns>
    Task<bool> TryRecordProviderQuoteAsync(
        string storeId,
        string idempotencyKey,
        string providerQuoteId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a record to a terminal state, but only from one of <paramref name="allowedFrom"/>.
    /// </summary>
    /// <remarks>
    /// A compare-and-set, not a read-modify-write, and load-bearing: a manual sweep, the periodic task's
    /// crash-recovery pass and a retry can all be resolving the same record at once. Exactly one caller may be
    /// told true for a given transition, and that is the caller allowed to act on it — to log the sweep as
    /// having happened, or to free the store for a fresh attempt.
    /// </remarks>
    /// <returns>True when this call is what changed the row.</returns>
    Task<bool> TryResolveAsync(
        string storeId,
        string idempotencyKey,
        IReadOnlyCollection<SweepRecordStatus> allowedFrom,
        SweepResolution resolution,
        CancellationToken cancellationToken = default);
}
