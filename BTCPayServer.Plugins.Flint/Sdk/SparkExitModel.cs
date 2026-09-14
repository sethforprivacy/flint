using System;
using System.Collections.Generic;
using System.Globalization;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>
/// What one transaction in a unilateral exit is for, which is what decides how it may be broadcast.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not decoration — broadcast order and packaging are read off it.</b> The SDK builds and signs the
/// whole exit and then <em>never broadcasts anything</em>, so an operator (or a later phase of this plugin) has
/// to push the transactions out by hand in the right shape: the fan-out alone, then each tree node together
/// with its own CPFP child as a package, waiting for the CSV timelock between levels, and the sweep alone at
/// the end. Sending a tree node without its child leaves an unconfirmable transaction paying no fee.
/// </para>
/// <para>
/// Mapped explicitly from the SDK's <c>UnilateralExitTxKind</c> rather than cast: the SDK spells the first two
/// <c>FanOut</c> and <c>Node</c>, so name-based mapping is what survives an SDK bump that inserts a variant.
/// </para>
/// </remarks>
public enum SparkExitTxKind
{
    /// <summary>
    /// The one transaction that splits the CPFP funding UTXO into a fee output per branch. Broadcast first, on
    /// its own, and confirmed before anything else goes out — every other transaction's fee comes from it.
    /// </summary>
    Fanout,

    /// <summary>
    /// A statechain tree node, unrolling one level of the tree toward a leaf. Carries a CSV timelock and a CPFP
    /// child, and <b>must</b> be broadcast as a package with that child.
    /// </summary>
    TreeNode,

    /// <summary>A refund transaction claiming a leaf once its timelock has expired.</summary>
    Refund,

    /// <summary>
    /// The final transaction moving the recovered coins to the operator's destination address. Broadcast alone,
    /// after everything it depends on has confirmed.
    /// </summary>
    Sweep
}

/// <summary>
/// Whether the chain has seen a given exit transaction yet, as the SDK's chain service reports it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the SDK's <c>ExitTransactionStatus</c> union, collapsed into one type that carries its case.</b>
/// SDK 0.25 replaced the flat <c>Confirmed</c>/<c>Unconfirmed</c>/<c>Unverified</c> enum with a union, because
/// the useful question stopped being "is it mined" and became "may I broadcast it yet, and if not, what am I
/// waiting for". A flat enum cannot answer that, and a plugin-side mirror of the union would put
/// <c>Breez.Sdk.Spark</c> types into the persisted record, which is exactly what this seam exists to prevent.
/// A single type with a <see cref="Readiness"/> discriminant carries the same information and survives
/// serialisation.
/// </para>
/// <para>
/// The member order of <see cref="SparkExitTxReadiness"/> is deliberately <em>not</em> the SDK's. Nothing in the
/// SDK's union has an ordinal to mirror, and this order puts <see cref="SparkExitTxReadiness.Waiting"/>
/// first so that a default-initialised value, a missing JSON field, or a column added to an existing row all
/// read as "not ready to broadcast" rather than as "ready". Ordering it the other way is instructions to
/// broadcast a transaction whose timelock has not matured.
/// </para>
/// </remarks>
public enum SparkExitTxReadiness
{
    /// <summary>
    /// Its inputs are not yet where they need to be — either something in <see cref="SparkExitTransaction.DependsOn"/>
    /// has not confirmed, or its CSV timelock has not matured. <b>Do not broadcast.</b>
    /// </summary>
    Waiting,

    /// <summary>Broadcast it now. Sending one that is already sent is harmless.</summary>
    Ready,

    /// <summary>Already mined; skip it and broadcast the next step.</summary>
    Confirmed,

    /// <summary>
    /// The SDK could not read the chain for this transaction, so it cannot say whether broadcasting is safe.
    /// Not a failure and not a confirmation — the SDK's own guidance is to build the exit again once the chain
    /// service is healthy. An operator must check the transaction themselves before treating it as either.
    /// </summary>
    Unverified
}

/// <summary>
/// Where one transaction of an exit stands: its readiness, and whatever height that readiness implies.
/// </summary>
/// <remarks>
/// <para>
/// The two heights are meaningful in different cases and null otherwise, kept as two fields rather than one so
/// that neither can be read as the other. <see cref="BlockHeight"/> is set only for
/// <see cref="SparkExitTxReadiness.Confirmed"/> and is the height the transaction landed at, which is what a
/// child's CSV timelock counts from. <see cref="SpendableAtHeight"/> is set only for
/// <see cref="SparkExitTxReadiness.Waiting"/> when the input is confirmed but the timelock has not matured, and
/// is the first block the transaction can be mined in. The SDK reports either as nullable, so both are
/// nullable here.
/// </para>
/// <para>
/// The readiness is what the page and the record switch on; the heights are shown to an operator so "wait" is
/// a number rather than an instruction to keep refreshing.
/// </para>
/// </remarks>
public sealed record SparkExitTxStatus(
    SparkExitTxReadiness Readiness,
    uint? BlockHeight = null,
    uint? SpendableAtHeight = null)
{
    /// <summary>Shorthand for the state every freshly built, unbroadcast transaction is in.</summary>
    public static SparkExitTxStatus Ready { get; } = new(SparkExitTxReadiness.Ready);

    /// <summary>True when this transaction may be broadcast right now.</summary>
    /// <remarks>
    /// The single predicate the page and the operator's checklist both use, so "ready" cannot come to mean two
    /// different things in two places.
    /// </remarks>
    public bool CanBroadcast => Readiness is SparkExitTxReadiness.Ready;
}

/// <summary>
/// One statechain leaf a quoted exit would recover.
/// </summary>
/// <remarks>
/// <para>
/// <b>The leaf ids are the resumable identity of an exit and must be persisted.</b> A quote taken with
/// <c>Auto</c> selection picks whichever leaves are worth exiting at that moment and at that fee rate; asking
/// again later can select a different set, which would build a different exit against a funding UTXO sized for
/// the first one. Re-quoting with these exact ids (<c>Specific</c>) is what makes a resume mean the same exit.
/// </para>
/// <para>
/// The binding's <c>UnilateralExitLeaf</c> carries only an id and a value — there is no per-leaf fee field, so
/// there is none here. Fees are reported for the exit as a whole on <see cref="SparkExitQuote"/> and per branch
/// on <see cref="SparkExitBranchFunding"/>.
/// </para>
/// </remarks>
public sealed record SparkExitLeaf(string LeafId, long ValueSat);

/// <summary>
/// How much of the CPFP funding one branch of the tree needs.
/// </summary>
/// <remarks>
/// The breakdown behind <see cref="SparkExitQuote.SingleUtxoFundingSat"/>. Shown to an operator so a partially
/// funded exit is legible — the fan-out creates one fee output per branch, so a shortfall does not fail evenly
/// across the tree — and deliberately <em>not</em> used for any funding decision: the plugin funds from a
/// single UTXO, and the amount to check against is the single-UTXO total.
/// </remarks>
public sealed record SparkExitBranchFunding(string LeafId, long FundingSat);

/// <summary>
/// What a unilateral exit would recover and what it would cost, before any transaction exists.
/// </summary>
/// <remarks>
/// <para>
/// <b>An empty <see cref="Leaves"/> list is a normal answer, not an error.</b> With <c>Auto</c> selection the
/// SDK returns nothing at all when no leaf is worth exiting at the requested fee rate, and that has to be
/// reported to a merchant as "nothing worth exiting right now" rather than as a fault.
/// </para>
/// <para>
/// Unlike the cooperative-exit quote this one has no expiry and no id: it is a local computation over the
/// wallet's tree plus a fee rate, so nothing server-side is being held. It is still not carried across a
/// request boundary, because the tree changes as payments settle and the leaf set would drift — see
/// <see cref="ISparkSdkClient.UnilateralExitAsync"/>, which re-quotes inside the build for that reason.
/// </para>
/// <para>
/// Every amount here is satoshi. There is no token or base-unit ambiguity anywhere on the exit surface — the
/// SDK types them all as <c>u64</c> sats — so none of the <see cref="SparkSendAmount"/> machinery applies.
/// </para>
/// </remarks>
/// <param name="RecoverableValueSat">
/// The gross value of the selected leaves. <b>Fees are not netted out of it</b>, so a caller deciding whether
/// an exit is worth doing must compare this against <paramref name="TotalFeeSat"/> itself.
/// </param>
/// <param name="TotalFeeSat">Every on-chain fee the exit will pay, fan-out included.</param>
/// <param name="SingleUtxoFundingSat">
/// The amount that must sit on the funding address as <b>one</b> UTXO. This is the number an operator funds
/// against: the plugin spends a single P2WPKH output, so two outputs each half this size do not qualify.
/// </param>
/// <param name="FanoutFeeSat">
/// The fan-out transaction's own fee, part of <paramref name="TotalFeeSat"/>. Called out separately because it
/// is the one fee that is spent before any coin has been recovered.
/// </param>
/// <param name="FeeRateSatPerVbyte">
/// The rate the SDK quoted at, echoed back from the request. Carried so a UI shows the rate the numbers
/// actually belong to rather than the one a form field happens to hold.
/// </param>
/// <param name="Destination">
/// The address the sweep will pay, echoed back from the request. <see cref="SparkSdkClient"/> asserts this
/// matches what was asked for before it builds anything, because the built sweep is signed against whatever
/// this says.
/// </param>
public sealed record SparkExitQuote(
    long RecoverableValueSat,
    long TotalFeeSat,
    long SingleUtxoFundingSat,
    IReadOnlyList<SparkExitLeaf> Leaves,
    long FanoutFeeSat,
    IReadOnlyList<SparkExitBranchFunding> PerBranchFunding,
    ulong FeeRateSatPerVbyte,
    string Destination)
{
    /// <summary>True when the quote selected nothing — see the remarks on this type.</summary>
    public bool IsEmpty => Leaves.Count == 0;
}

/// <summary>
/// One confirmed on-chain output that will pay the exit's fees.
/// </summary>
/// <remarks>
/// <para>
/// P2WPKH only, matching the single <c>CpfpFundingKind</c> the plugin asks for. The SDK also supports P2TR and
/// an arbitrary script, and neither is offered: the funding key is derived on a fixed BIP84 path, so the script
/// type is not a choice a merchant makes, and a mismatch between the funding kind quoted and the input actually
/// supplied produces a signature that does not verify.
/// </para>
/// <para>
/// <see cref="PubkeyHex"/> is the compressed public key for the output's script, not the script itself. It is
/// passed to the SDK so it can build the witness it will later ask the signer to sign; the private half never
/// leaves the plugin except as the seed for the one-shot signer.
/// </para>
/// </remarks>
public sealed record SparkExitFundingUtxo(string Txid, uint Vout, long ValueSat, string PubkeyHex)
{
    /// <summary>A stable key for one output, for a form post and for de-duplication.</summary>
    public string OutPoint => $"{Txid}:{Vout.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>
/// One signed, <b>unbroadcast</b> transaction of an exit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here has been sent anywhere.</b> The SDK builds and signs the whole exit and stops; broadcasting
/// is entirely manual in this phase. That is what makes the accompanying fields load-bearing rather than
/// informational: <see cref="DependsOn"/> says which confirmations to wait for, <see cref="CsvTimelockBlocks"/>
/// says how long a wait stands between one level and the next, and <see cref="CpfpTxHex"/> being non-null means
/// this transaction pays no fee of its own and is unconfirmable unless the two go out together as a package.
/// </para>
/// <para>
/// <see cref="TxHex"/> is raw transaction hex and safe to display and copy. It contains no key material.
/// </para>
/// </remarks>
/// <param name="NodeId">
/// The statechain node this transaction unrolls, or null for the transactions that belong to no single node —
/// the fan-out and the sweep.
/// </param>
/// <param name="CpfpTxHex">
/// The child that pays this transaction's fee, or null when it pays its own. When set, both must be broadcast
/// in one package (<c>bitcoin-cli submitpackage</c>); broadcasting the parent alone gets it rejected or leaves
/// it stuck at zero fee.
/// </param>
/// <param name="CsvTimelockBlocks">
/// Blocks that must pass after the parent confirms before this transaction is valid, or null when there is no
/// timelock. This is where the multi-day cost of a unilateral exit lives, and it is per level rather than
/// once for the whole exit.
/// </param>
/// <param name="DependsOn">
/// Txids that must confirm before this transaction may be broadcast. <b>Not the ordering</b> — the SDK returns
/// the list in a valid topological broadcast order already, and that is the upstream contract this plugin
/// relies on rather than something re-derived here. What this field is for is the <em>waiting</em>: it names
/// which confirmations to check for before pushing this one out, which is what turns a correct order into a
/// correct schedule. It has to survive persistence for the same reason the hex does — the operator broadcasts
/// from the stored row, possibly days later.
/// </param>
public sealed record SparkExitTransaction(
    SparkExitTxKind Kind,
    string? NodeId,
    string Txid,
    string TxHex,
    string? CpfpTxHex,
    uint? CsvTimelockBlocks,
    IReadOnlyList<string> DependsOn,
    SparkExitTxStatus Status)
{
    /// <summary>
    /// True when this transaction and <see cref="CpfpTxHex"/> must be submitted together as a package.
    /// </summary>
    /// <remarks>
    /// Read off the presence of the child rather than off <see cref="Kind"/>. The kinds that need a package
    /// today are the tree nodes, but the SDK decides which transactions carry a CPFP child, and hard-coding the
    /// correspondence would silently drop a child the SDK started attaching elsewhere.
    /// </remarks>
    public bool RequiresPackageBroadcast => CpfpTxHex is not null;
}

/// <summary>
/// A built exit: the quote it committed to, plus every transaction an operator has to broadcast.
/// </summary>
/// <remarks>
/// The totals are re-reported by the SDK from the build rather than copied from the quote, so they are the
/// figures the signed transactions actually implement. <see cref="Leaves"/> is likewise the set the build used;
/// it should match the ids the quote was pinned to, and persisting it is what lets a later reconciliation say
/// which leaves are now committed to on-chain transactions.
/// </remarks>
/// <param name="Transactions">
/// Every transaction of the exit, in a valid topological broadcast order — the SDK's own ordering, kept as it
/// came. Persisted and rendered in this order, so nothing above the seam sorts or re-derives it; each entry's
/// <see cref="SparkExitTransaction.DependsOn"/> says which confirmations to wait for before pushing it out.
/// </param>
public sealed record SparkExitResult(
    long RecoverableValueSat,
    long TotalFeeSat,
    IReadOnlyList<SparkExitTransaction> Transactions,
    IReadOnlyList<SparkExitLeaf> Leaves);

/// <summary>
/// What to do with a built exit, as the chain currently reports it.
/// </summary>
/// <remarks>
/// The SDK's <c>UnilateralExitVerdict</c>. Three cases rather than a boolean because the three call for
/// genuinely different actions, and — the important part — "this cannot finish" is not an error: the money is
/// still recoverable, it just needs a new exit built from the same leaves.
/// </remarks>
public enum SparkExitVerdict
{
    /// <summary>
    /// On track. Broadcast every transaction whose <see cref="SparkExitTransaction.Status"/> is ready, and call
    /// again later. This is the ordinary state of an exit that is part-way through its timelocks.
    /// </summary>
    Valid,

    /// <summary>
    /// Every transaction has confirmed, the sweep included. The money is at the destination address and there
    /// is nothing left to do.
    /// </summary>
    Done,

    /// <summary>
    /// This transaction set can no longer finish: something on-chain stopped matching it — a different refund
    /// for a leaf confirmed, a step was fee-bumped in a way these transactions cannot follow, or funding they
    /// counted on went elsewhere. <b>The fix is always the same: quote and build the exit again, naming the
    /// same leaves.</b> The funds are not lost.
    /// </summary>
    Redo
}

/// <summary>
/// The result of asking the chain how far a built exit has got.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Transactions"/> replaces the set that was passed in: these are the same transactions with their
/// statuses brought up to date, and they are what a caller should store back over what it had. Everything else
/// is the SDK's own echo of the exit it was handed, and the two totals are carried again rather than assumed
/// unchanged.
/// </para>
/// <para>
/// The verdict is what a caller switches on, and it is the only field that decides an action. It is
/// deliberately not derived from the statuses by this plugin: the SDK reads the chain tip to tell "waiting"
/// from "cannot finish", and re-deriving it here would be a second opinion with less information.
/// </para>
/// </remarks>
/// <param name="Verdict">What to do next — see <see cref="SparkExitVerdict"/>.</param>
/// <param name="Transactions">
/// Every transaction of the exit, in the SDK's own broadcast order, with chain-reported statuses.
/// </param>
public sealed record SparkExitProgress(
    SparkExitVerdict Verdict,
    long RecoverableValueSat,
    long TotalFeeSat,
    IReadOnlyList<SparkExitTransaction> Transactions);

/// <summary>
/// What an import of an exit-state backup actually took, and what it left behind.
/// </summary>
/// <remarks>
/// <para>
/// The counts are not decoration: importing is the recovery path of last resort, and a caller that reports
/// only "imported" would be telling an operator their backup is in place when the part of it that matters was
/// skipped. Each number names a different reason nothing was restored, and they call for different responses.
/// </para>
/// <para>
/// The two benign skips — <see cref="SkippedForeignLeaves"/> and <see cref="SkippedChains"/> — are normal for a
/// backup taken from a wallet that has since moved on, because an imported copy is used only for a leaf the
/// wallet has nothing usable for. <see cref="SkippedConflictingLeaves"/> is the one that means data could not
/// be put back: the copy disagrees with a node the wallet already holds on a value that cannot change over a
/// node's lifetime, so one of the two copies is simply wrong and nothing in that entry is trusted.
/// </para>
/// </remarks>
public sealed record SparkExitStateImport(
    uint ImportedLeaves,
    uint SkippedForeignLeaves,
    uint SkippedConflictingLeaves,
    uint SkippedChains)
{
    /// <summary>
    /// True when no leaf's exit data was restored at all.
    /// </summary>
    /// <remarks>
    /// Worth naming because it is the answer that looks like success from the outside — the call returned, the
    /// blob parsed, nothing threw — while the wallet is exactly as un-exitable as it was before.
    /// </remarks>
    public bool RestoredNothing => ImportedLeaves == 0;
}

/// <summary>
/// Raised when the caller's quote approval callback vetoed an exit, so nothing was built.
/// </summary>
/// <remarks>
/// <para>
/// An exception rather than a field on <see cref="SparkExitResult"/>, which is the opposite of what the send
/// paths do — and the difference is deliberate. A vetoed <c>SendBolt11Async</c> has to be reported as a value
/// because "we chose not to pay" and "the payment failed" are different outcomes for a payout, and a caller
/// that treated a refusal as an error would retry it. Here there is nothing to distinguish: the SDK broadcasts
/// nothing, so a veto has moved no money and changed no state, and a result type with an empty transaction
/// list would invite a caller to persist it as a successful build.
/// </para>
/// <para>
/// The message is the callback's own, so it is already fit to show a merchant.
/// </para>
/// </remarks>
public sealed class SparkExitRefusedException : InvalidOperationException
{
    public SparkExitRefusedException(string reason)
        : base(reason)
    {
        Reason = reason;
    }

    /// <summary>The refusal the approval callback returned, verbatim.</summary>
    public string Reason { get; }
}
