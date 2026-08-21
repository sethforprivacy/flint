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
/// The member order is deliberately <em>not</em> the SDK's. <c>ConfirmationStatus</c> is ordered
/// <c>Confirmed = 0, Unconfirmed = 1, Unverified = 2</c>; this enum puts <see cref="Unconfirmed"/> at 0 so that
/// a default-initialised value, a missing JSON field, or a column added to an existing row all read as "not
/// confirmed" rather than as "confirmed". That also means a numeric cast between the two would swap exactly the
/// pair whose confusion matters most, which is why <see cref="SparkSdkClient"/> maps them by name.
/// </remarks>
public enum SparkExitTxStatus
{
    /// <summary>Broadcast (or buildable) but not yet mined.</summary>
    Unconfirmed,

    /// <summary>Mined.</summary>
    Confirmed,

    /// <summary>
    /// The SDK could not reach a chain service to say either way. Not a failure and not a confirmation — an
    /// operator must check the transaction themselves before treating it as either.
    /// </summary>
    Unverified
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
