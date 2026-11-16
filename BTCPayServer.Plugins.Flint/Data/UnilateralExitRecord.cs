using System;

namespace BTCPayServer.Plugins.Flint.Data;

/// <summary>
/// Durable record of one unilateral-exit attempt: the quote it was built from, the funding UTXOs it consumed,
/// and the signed transactions the operator still has to broadcast by hand.
/// </summary>
/// <remarks>
/// <para>
/// <b>This row is not a log. It is the only copy of the exit.</b> A cooperative exit (see
/// <see cref="SweepRecord"/>) is resolvable after a crash because the SDK holds it: the idempotency key becomes
/// a <c>Payment.id</c> and <c>GetPayment</c> answers definitively. A unilateral exit has no such backstop — the
/// SDK builds and signs the tree, hands the transactions back, and <em>never broadcasts</em>. Until every one of
/// them is confirmed, the signed hex in <see cref="TransactionsJson"/> is the merchant's claim on their own
/// money, and losing it means re-quoting and re-funding from scratch.
/// </para>
/// <para>
/// The row is written at quote time, before any funding exists, because the funding step is the part that takes
/// human time. An exit is quoted, then the operator sends sats to <see cref="FundingAddress"/> — possibly hours
/// later, possibly after a restart — and only then is it built. The leaf set is pinned across that gap by
/// <see cref="LeafIdsJson"/>: the build re-quotes with <c>ExitLeafSelection.Specific</c> naming exactly the
/// leaves the operator was shown a price for, so the second quote cannot silently become a different exit than
/// the one they funded.
/// </para>
/// <para>
/// The three JSON columns are plain <c>text</c> holding the seam DTOs (<c>SparkExitFundingUtxo[]</c>, and the
/// <c>SparkExitTransaction[]</c> with the time its statuses were read) and a bare <c>string[]</c> of leaf ids. Serialisation is deliberately the
/// caller's job rather than this entity's: the data layer stays free of the seam types, so nothing here has to
/// change when the SDK's exit shapes move under the next version bump. Exactly one caller does it — the exit
/// service — so the write format has a single owner and no other layer reads the blobs.
/// </para>
/// <para>
/// <b>An instance may be a partial row.</b> <see cref="IUnilateralExitRecordStore.ListTerminalForStoreAsync"/>
/// projects the history list without the three JSON columns, because a five-column table has no business
/// dragging every signed transaction set in a store's past out of the database. Such an instance carries an
/// empty <see cref="LeafIdsJson"/> and null blobs, which is safe to write back only because the store's update
/// coalesces those two blobs rather than assigning them — see
/// <see cref="IUnilateralExitRecordStore.UpdateAsync"/>.
/// </para>
/// </remarks>
public class UnilateralExitRecord
{
    /// <summary>Plugin-generated UUID, and this row's primary key.</summary>
    /// <remarks>
    /// Plugin-generated rather than taken from the SDK because the row exists before the SDK has been asked to
    /// build anything, and nothing in the exit flow is idempotent on an SDK-side identifier.
    /// </remarks>
    public string Id { get; set; } = null!;

    /// <summary>Store this exit belongs to. Indexed, and part of every read, so one store cannot see another's.</summary>
    public string StoreId { get; set; } = null!;

    /// <summary>How far this exit has got.</summary>
    public UnilateralExitStatus Status { get; set; } = UnilateralExitStatus.AwaitingFunding;

    /// <summary>When the exit was quoted.</summary>
    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>
    /// When the row last changed — funding discovered, transactions built, exit abandoned.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="CreatedUtc"/> because the gap between them is the operator's own waiting
    /// time, and an exit stuck in <see cref="UnilateralExitStatus.AwaitingFunding"/> for a week is a different
    /// situation from one quoted a minute ago. Stamped by the caller, which owns the clock.
    /// </remarks>
    public DateTimeOffset UpdatedUtc { get; set; }

    /// <summary>On-chain address the exited funds are swept to.</summary>
    /// <remarks>
    /// Recorded rather than re-resolved at build time: the destination is baked into the signed transactions, so
    /// it must be the address the operator was shown when they approved the exit, not whatever the settings say
    /// by the time the funding lands.
    /// </remarks>
    public string DestinationAddress { get; set; } = null!;

    /// <summary>Fee rate the stored figures, and any stored transactions, were priced at, in sat/vB.</summary>
    /// <remarks>
    /// <para>
    /// Set at quote time and changed only by a build that asks for a new rate — the SDK's way to raise the fee
    /// on an exit that stopped confirming is to quote again at a higher rate and build again, which replaces
    /// what has not confirmed. It is written together with the figures it produced and never on its own: with the
    /// fresh quote before a first build selects its funding, and with the signed set after a rebuild succeeds,
    /// so the page never shows a rate beside numbers that belong to another one. A built exit's rate never goes
    /// down, because a replacement has to pay more than what it replaces.
    /// </para>
    /// <para>
    /// A <c>long</c> rather than the seam's <c>ulong</c>, because Npgsql has no unsigned integer types and a
    /// negative rate is refused by the service's guard long before it reaches here.
    /// </para>
    /// </remarks>
    public long FeeRateSatPerVbyte { get; set; }

    /// <summary>
    /// The leaf ids from the first quote, as a JSON <c>string[]</c>.
    /// </summary>
    /// <remarks>
    /// <b>The reason this row is durable at all.</b> The first quote runs with <c>ExitLeafSelection.Auto</c>, and
    /// Auto is free to pick a different set on the next call — the wallet's leaves move under the SDK's
    /// background optimisation. Replaying Auto at build time would therefore price and sign an exit of a
    /// different set of leaves than the one whose funding requirement the operator satisfied. The build resumes
    /// with <c>Specific</c> naming these ids instead.
    /// </remarks>
    public string LeafIdsJson { get; set; } = null!;

    /// <summary>What the quote said would come back to the destination, in satoshi.</summary>
    public long RecoverableValueSat { get; set; }

    /// <summary>Total fee the quote attributed to the whole tree, in satoshi.</summary>
    /// <remarks>
    /// Held next to <see cref="RecoverableValueSat"/> because the guard that matters is the comparison between
    /// them: an exit that costs more than it recovers is refused, at quote time and again inside the build's
    /// approval callback, since the second quote can come back worse than the first.
    /// </remarks>
    public long TotalFeeSat { get; set; }

    /// <summary>
    /// Sats the operator must put on <see cref="FundingAddress"/> in a <b>single</b> UTXO.
    /// </summary>
    /// <remarks>
    /// Single because the SDK's quote sizes the requirement for exactly one funding input, and the first build
    /// selects exactly one: two outputs adding up to this figure are short by the second input's weight, and the
    /// funding instructions say "one output" so the rule the page states is the rule applied. A rebuild is
    /// different — it hands the SDK everything this exit committed plus anything else on the address, and the
    /// SDK sums what that has become (see <see cref="FundingUtxosJson"/>). Stored per row rather than recomputed
    /// because it is what the page tells the operator to send.
    /// <para>
    /// <b>Zero is a real answer and means "fund nothing".</b> Since SDK 0.28 a recovery can be entirely
    /// cooperative: the operators already moved those leaves on-chain, so their recovery pays its own fee out of
    /// their value and the operator supplies no Bitcoin at all. The SDK reports that as an absent funding
    /// requirement, and this column stores it as zero — no unilateral step pays no fee, so a nonzero requirement
    /// can never round to it. See <see cref="RequiresFunding"/>.
    /// </para>
    /// </remarks>
    public long SingleUtxoFundingSat { get; set; }

    /// <summary>
    /// Whether this exit needs the operator to fund anything at all — see
    /// <see cref="SingleUtxoFundingSat"/>.
    /// </summary>
    /// <remarks>
    /// The one place the zero-means-none rule is spelled out, so the page and the build cannot disagree about
    /// whether to ask for sats. Not mapped: derived from the column, which is persisted.
    /// </remarks>
    public bool RequiresFunding => SingleUtxoFundingSat > 0;

    /// <summary>
    /// P2WPKH address whose UTXOs pay the CPFP fees, derived from the store's Spark seed at the plugin's own
    /// hardened account and this row's <see cref="FundingKeyIndex"/>.
    /// </summary>
    /// <remarks>
    /// Stored rather than re-derived on every page load so the address the operator sent to is provably the one
    /// the build will spend from, even if the derivation path or the seed source changes later. It is a
    /// deliberately non-standard account so it can never collide with BTCPay's own BIP84 hot wallet on a shared
    /// seed — see <c>Constants.UnilateralExitFundingAccount</c>.
    /// </remarks>
    public string FundingAddress { get; set; } = null!;

    /// <summary>
    /// Address index of this exit's funding key inside the plugin's hardened account:
    /// <c>m/84'/{coin}'/4607060'/0/{index}</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One address per exit, not one per store.</b> A fixed index would hand every exit a store ever quotes
    /// the same funding address, and that is a trap rather than a convenience: sats left behind by an abandoned
    /// exit sit on the address the <em>next</em> exit tells the operator to fund, so the next build would select
    /// a leftover output — which may be the wrong size, and is in any case money the operator did not mean to
    /// commit. Worse, an old output large enough to satisfy a new requirement makes a build succeed against
    /// funding nobody just sent, which reads as the plugin spending stale coins on its own initiative.
    /// </para>
    /// <para>
    /// Identity, not state: set once at create time and never rewritten, because the address the operator funded
    /// is derived from it. Allocated as the store's highest existing index plus one — over every row including
    /// terminal ones, so an index is never reused even after an exit is abandoned. Two concurrent allocations
    /// could pick the same number; only one of them can insert, because
    /// <see cref="IUnilateralExitRecordStore.CreateAsync"/> is guarded by a unique index over the store's active
    /// exits.
    /// </para>
    /// <para>
    /// A <c>long</c> rather than a <c>uint</c> because Npgsql has no unsigned integer types. BIP32 non-hardened
    /// indexes stop at <see cref="int.MaxValue"/>, and the service refuses a row outside that range rather than
    /// wrapping it into a different key.
    /// </para>
    /// </remarks>
    public long FundingKeyIndex { get; set; }

    /// <summary>
    /// Every funding output a successful build was handed, as a JSON <c>SparkExitFundingUtxo[]</c>. Null until a
    /// build succeeds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An input to the next attempt, not a note about the last one.</b> Every build after the first passes
    /// these back to the SDK, which follows each outpoint to whatever an earlier attempt turned it into — a
    /// fan-out output, the change of a fee-bumping child, several steps of both — and keeps one spent only by an
    /// unconfirmed transaction, since that is this exit's own in-flight child and the rebuild replaces it. That is
    /// the SDK's documented second attempt, and it is why a Redo or a fee bump does not ask for a second full
    /// funding: re-listing the address would see neither an output spent by an in-flight child nor enough on any
    /// single per-branch output. The service adds any other confirmed output on the address alongside, and
    /// writes back the whole set it passed — which is exactly the <c>funding_inputs</c> the SDK's response echoes.
    /// </para>
    /// <para>
    /// Never cleared once written, and only ever grown: the signed transactions in <see cref="TransactionsJson"/>
    /// spend outpoints named here, so losing one would leave a set of transactions whose input nobody can
    /// identify and a rebuild that could no longer follow it. The store's update coalesces it for that reason.
    /// </para>
    /// </remarks>
    public string? FundingUtxosJson { get; set; }

    /// <summary>
    /// The signed transactions from the build, as JSON: an object holding the <c>SparkExitTransaction[]</c> and
    /// the time its statuses were last read from the chain. Null until the build runs. Rows written before the
    /// time was recorded hold the bare array, which still reads.
    /// </summary>
    /// <remarks>
    /// <b>The valuable column.</b> Nothing broadcasts these — not the plugin, not the SDK — so this text is the
    /// exit until the operator has pushed every package through <c>submitpackage</c> and the CSV timelocks have
    /// matured. Kept as the SDK returned it, including the CPFP child hex and the <c>dependsOn</c> ordering,
    /// because a package broadcast out of order is rejected and there is no second copy to re-derive it from.
    /// That is also why the store's update coalesces this column instead of assigning it: abandoning an exit,
    /// or recording why an attempt on it failed, must not be able to write a null over the only copy.
    /// </remarks>
    public string? TransactionsJson { get; set; }

    /// <summary>
    /// Why the last attempt on this row failed, in words fit for a merchant. Never contains secrets.
    /// </summary>
    /// <remarks>
    /// Set on a failed build and left in place, so an exit that is still <see cref="UnilateralExitStatus.AwaitingFunding"/>
    /// carries the explanation of why it is not <see cref="UnilateralExitStatus.Built"/> yet — underfunded,
    /// conflicting outpoint, operators unreachable. Cleared by a build that gets further.
    /// </remarks>
    public string? LastError { get; set; }

    /// <summary>
    /// True while this exit still occupies the store — it is either waiting for funding or holding signed
    /// transactions nobody has finished broadcasting.
    /// </summary>
    /// <remarks>
    /// This is what makes an exit single-flight per store, and it is enforced in the database rather than only in
    /// the service: a unique index over <see cref="StoreId"/> filtered to these two statuses means a second
    /// active row cannot be inserted even by a second server. The store's own queries repeat the status list
    /// rather than calling this, because EF cannot translate a computed property into SQL — if a status is ever
    /// added, the queries, the index filter and this property all have to be updated together.
    /// </remarks>
    public bool IsActive =>
        Status is UnilateralExitStatus.AwaitingFunding or UnilateralExitStatus.Built;
}

/// <summary>
/// How far a unilateral exit has got.
/// </summary>
/// <remarks>
/// Values are persisted, so existing members must never be renumbered; new ones may only be appended. Note that
/// the two non-terminal states are both "active" for the purposes of
/// <see cref="IUnilateralExitRecordStore.GetActiveForStoreAsync"/> — see <see cref="UnilateralExitRecord.IsActive"/>.
/// </remarks>
public enum UnilateralExitStatus
{
    /// <summary>
    /// Quoted, and waiting for the operator to put <see cref="UnilateralExitRecord.SingleUtxoFundingSat"/> sats
    /// on <see cref="UnilateralExitRecord.FundingAddress"/> in one UTXO. Nothing has been signed.
    /// </summary>
    AwaitingFunding = 0,

    /// <summary>
    /// Built and signed. <see cref="UnilateralExitRecord.TransactionsJson"/> holds transactions that
    /// <b>nothing has broadcast</b>; the operator does that by hand, in <c>dependsOn</c> order, and the exit is
    /// not finished until they have.
    /// </summary>
    Built = 1,

    /// <summary>
    /// Done with. Terminal. Normally recorded because Spark's check reported every transaction, the sweep
    /// included, in a block; the operator can also record it on their own word when a check cannot answer.
    /// Nothing watches the chain between presses, so it is never set on its own.
    /// </summary>
    Completed = 2,

    /// <summary>
    /// Abandoned by the operator. Terminal, and it frees the store for a fresh quote — which is the only reason
    /// it exists, since an exit with no path forward would otherwise block every later attempt.
    /// </summary>
    Abandoned = 3
}
