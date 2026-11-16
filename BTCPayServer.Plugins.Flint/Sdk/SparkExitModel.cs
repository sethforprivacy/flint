using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

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
/// Mapped explicitly from the SDK's <c>RecoveryTxKind</c> rather than cast: the SDK spells two of them
/// <c>FanOut</c> and <c>Node</c>, so name-based mapping is what survives an SDK bump that inserts a variant.
/// </para>
/// <para>
/// <b>The member order is persisted state.</b> A built exit is stored as JSON with these values as integers
/// (the exit service's serialiser has no enum-to-string converter, and adding one would silently reinterpret
/// every row already written), so a new member is <em>appended</em> and never inserted. Inserting one renames
/// every kind an operator's stored exit already holds — a tree node would come back as whatever was put in
/// front of it, and the page would hand out broadcast instructions for the wrong shape.
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
    Sweep,

    /// <summary>
    /// A cooperative recovery: one transaction per leaf, for a leaf the Spark operators already moved on-chain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Appended, not inserted — see the remark on this type's member order.</b> It is also the one kind that
    /// is not an exit step: the operators co-sign it, it spends an on-chain output rather than the pre-signed
    /// tree, it pays its own fee out of the funds it recovers, and it carries no CPFP child, no timelock and no
    /// dependencies. Broadcast it alone, through any node, as soon as the build returns it.
    /// </para>
    /// <para>
    /// It exists because SDK 0.28's recovery covers both ways a leaf can be got back: a unilateral exit, and
    /// this, for leaves the operators took on-chain before the exit reached them. A set can hold both, because
    /// the SDK picks per leaf from what it finds on-chain.
    /// </para>
    /// </remarks>
    Cooperative
}

/// <summary>
/// How one leaf's funds will be recovered, which decides whether that leaf needs a Bitcoin UTXO of the
/// operator's to pay for its release.
/// </summary>
/// <remarks>
/// The SDK's <c>RecoveryMethod</c>, mapped by name. This is shown to an operator rather than acted on: it
/// explains why a quote asks for funding at all (a unilateral leaf pays no fee of its own, so its CPFP children
/// have to be funded) and why it may not (a cooperative leaf pays its fee out of its own value).
/// </remarks>
public enum SparkExitRecoveryMethod
{
    /// <summary>
    /// The Spark operators already moved this leaf's funds on-chain, so they co-sign one transaction sending
    /// them to the destination. Needs the operators reachable and no funding UTXO.
    /// </summary>
    Cooperative,

    /// <summary>
    /// The leaf is taken on-chain through its own pre-signed transactions, needing nothing from the operators
    /// and paying every fee from a funding UTXO the operator supplies.
    /// </summary>
    Unilateral
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
/// automatic selection picks whichever leaves are worth recovering at that moment and at that fee rate; asking
/// again later can select a different set, which would build a different exit against a funding UTXO sized for
/// the first one. Re-quoting with these exact ids (<c>Specific</c>) is what makes a resume mean the same exit.
/// </para>
/// <para>
/// The binding's <c>RecoverFundsLeaf</c> carries an id, a value and <see cref="Method"/> — there is no per-leaf
/// fee field, so there is none here. Fees are reported for the recovery as a whole on
/// <see cref="SparkExitQuote"/> and per branch on <see cref="SparkExitBranchFunding"/>.
/// </para>
/// </remarks>
/// <param name="Method">
/// How the SDK will get this leaf back, or null where the caller only knows the leaf's identity. Read by the
/// page to explain the quote — a cooperative leaf needs no funding from the operator — and never by the
/// build's arithmetic, which uses the SDK's own figures. Null is what the check's reconstruction passes, since
/// the record it works from stores leaf ids and nothing else; see
/// <see cref="SparkSdkClient.ToSdkExit"/>.
/// </param>
public sealed record SparkExitLeaf(
    string LeafId,
    long ValueSat,
    SparkExitRecoveryMethod? Method = null)
{
    /// <summary>
    /// True when this leaf is recovered by an operator co-signature rather than by unrolling its tree.
    /// </summary>
    /// <remarks>
    /// The one predicate the funding explanation and the page both read, so "needs no funding" cannot come to
    /// mean two different things in two places. An unknown method reads as not cooperative, which is the safe
    /// direction: it keeps a funding requirement in front of the operator rather than dropping one.
    /// </remarks>
    public bool IsCooperative => Method is SparkExitRecoveryMethod.Cooperative;
}

/// <summary>
/// A leaf the quote's selection covered but did not recover, and why.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reported because silence would be a lie.</b> Before SDK 0.28 a leaf was simply absent from a quote when it
/// was not worth exiting, and "nothing worth exiting right now" covered it. The SDK now says which leaves it
/// left out and for what reason, and several of those reasons are actionable — too small for the fee rate,
/// funds not found on-chain, operators unreachable, or a recovery that cannot be built at all — so an operator
/// looking at a shorter list than they expected is told why rather than left to guess.
/// </para>
/// <para>
/// <see cref="Reason"/> is a sentence assembled from the SDK's own union, not a mirrored enum: the set of reasons
/// is the SDK's to extend, and a plugin-side enum would turn its next addition into a deserialisation failure or
/// a silently wrong label. Nothing branches on it.
/// </para>
/// </remarks>
public sealed record SparkExitSkippedLeaf(string LeafId, long ValueSat, string Reason);

/// <summary>
/// A leaf the build left out because the operators would not co-sign its recovery.
/// </summary>
/// <remarks>
/// A cooperative recovery is co-signed leaf by leaf, and one the operators refuse does not fail the build: the
/// SDK lists it here and builds the rest. That leaf needs a recovery of its own, so the build reports it rather
/// than presenting a complete-looking exit that quietly recovered less than the quote said.
/// </remarks>
public sealed record SparkExitFailedLeaf(string LeafId, string Reason);

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
/// <b>An empty <see cref="Leaves"/> list is a normal answer, not an error.</b> With automatic selection the
/// SDK returns nothing at all when no leaf is worth recovering at the requested fee rate, and that has to be
/// reported to a merchant as "nothing worth recovering right now" rather than as a fault. The leaves it looked
/// at and left out are then in <see cref="Skipped"/>, which is what turns that answer into a reason.
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
/// <param name="TotalFeeSat">
/// Every on-chain fee the exit will pay, fan-out included — and paid from two places. The fan-out and the CPFP
/// children are paid by the funding, the final sweep by the recovered value, so what arrives is
/// <paramref name="RecoverableValueSat"/> less the sweep's fee plus whatever funding was not spent;
/// recoverable minus this total counts the funding-paid fees twice.
/// </param>
/// <param name="SingleUtxoFundingSat">
/// The amount that must sit on the funding address as <b>one</b> UTXO. This is the number an operator funds
/// against: the plugin spends a single P2WPKH output, so two outputs each half this size do not qualify. It is
/// zero exactly when the recovery needs no funding at all — an all-cooperative quote, whose leaves pay their
/// own fees out of their own value — and <see cref="RequiresFunding"/> says so explicitly rather than leaving
/// a caller to infer it from a zero.
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
/// <param name="FinishedLeafIds">
/// Leaves whose exit the chain shows as already over — their refund was swept, or their branch was taken by a
/// spend the exit cannot continue from — read off the SDK's <c>exit_chain_state</c>. The SDK leaves such a leaf
/// out of <see cref="Leaves"/> even when it was named, so this is what tells a leaf that is <em>done</em> apart
/// from a leaf that is <em>gone</em>: the SDK also silently skips a named leaf missing from its local storage
/// (spent by a payment, reshaped by leaf optimisation), and a caller resuming a pinned exit has to refuse that
/// case rather than build a smaller exit than the one that was funded. Null reads as none.
/// </param>
/// <param name="RequiresFunding">
/// Whether this recovery needs a Bitcoin UTXO of the operator's at all. False only when every selected leaf is
/// cooperative, in which case the build takes no funding inputs and no signer and the page must not ask for
/// sats — a recovery of nothing but already-on-chain leaves is free to build and needs nobody's coins.
/// </param>
/// <param name="Skipped">
/// The leaves this selection covered and left out, with the reason. Null reads as none.
/// </param>
public sealed record SparkExitQuote(
    long RecoverableValueSat,
    long TotalFeeSat,
    long SingleUtxoFundingSat,
    IReadOnlyList<SparkExitLeaf> Leaves,
    long FanoutFeeSat,
    IReadOnlyList<SparkExitBranchFunding> PerBranchFunding,
    ulong FeeRateSatPerVbyte,
    string Destination,
    IReadOnlyList<string>? FinishedLeafIds = null,
    bool RequiresFunding = true,
    IReadOnlyList<SparkExitSkippedLeaf>? Skipped = null)
{
    /// <summary>True when the quote selected nothing — see the remarks on this type.</summary>
    public bool IsEmpty => Leaves.Count == 0;

    /// <summary>
    /// The pinned leaves this quote no longer covers and the chain does not show as finished, in the order given.
    /// </summary>
    /// <remarks>
    /// Non-empty means the quote describes a smaller exit than the one pinned — the leaves went missing from
    /// the wallet's storage or became unexitable — and a resume must not quietly build it.
    /// </remarks>
    public IReadOnlyList<string> UncoveredOf(IReadOnlyList<string> pinned)
    {
        var covered = new HashSet<string>(Leaves.Select(leaf => leaf.LeafId), StringComparer.Ordinal);
        covered.UnionWith(FinishedLeafIds ?? []);
        return pinned.Where(id => !covered.Contains(id)).ToList();
    }
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

    /// <summary>True when <paramref name="value"/> is a txid as Bitcoin spells one: 64 hex digits.</summary>
    /// <remarks>
    /// <para>
    /// <b>These two predicates exist because the page turns stored strings into a shell command.</b> The exit
    /// page renders <c>bitcoin-cli submitpackage '["…","…"]'</c> for an operator to paste into a terminal, built
    /// from <see cref="TxHex"/> and <see cref="CpfpTxHex"/> as they sit in the database. Hex cannot carry a quote,
    /// a bracket or a semicolon, so a value that is hex cannot change what the pasted command does — and a value
    /// that is not hex is not a transaction anyway, so refusing it costs nothing a real exit has.
    /// </para>
    /// <para>
    /// Either case is accepted: Bitcoin Core accepts both, and a row that was written in upper case by some
    /// earlier tool is no less a transaction for it.
    /// </para>
    /// </remarks>
    public static bool IsTxid(string? value) => value is { Length: 64 } && IsHex(value);

    /// <summary>True when <paramref name="value"/> is non-empty, even-length hex — a serialised transaction.</summary>
    /// <remarks>See <see cref="IsTxid"/> for why this is checked at all.</remarks>
    public static bool IsTransactionHex(string? value) =>
        value is { Length: > 0 } && value.Length % 2 == 0 && IsHex(value);

    private static bool IsHex(string value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigit(character))
                return false;
        }

        return true;
    }
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
/// <param name="FeeRateSatPerVbyte">
/// The rate the built transactions were signed at. Carried so the check's reconstruction is the same document
/// the build returned: <c>CheckRecoverFunds</c> takes a whole response, and a zero here would be a different
/// one. Nothing in the check branches on it.
/// </param>
/// <param name="Destination">
/// The address those transactions pay. Carried for the same reason as <paramref name="FeeRateSatPerVbyte"/>,
/// and it is also what a follow-up page shows when the record's own copy has moved on.
/// </param>
/// <param name="Failed">
/// Leaves the operators refused to co-sign, with the SDK's reason. Not part of this recovery: each needs one of
/// its own. Null reads as none, which is the ordinary case — only a cooperative leaf can land here.
/// </param>
public sealed record SparkExitResult(
    long RecoverableValueSat,
    long TotalFeeSat,
    ulong FeeRateSatPerVbyte,
    string Destination,
    IReadOnlyList<SparkExitTransaction> Transactions,
    IReadOnlyList<SparkExitLeaf> Leaves,
    IReadOnlyList<SparkExitFailedLeaf>? Failed = null);

/// <summary>
/// What to do with a built exit, as the chain currently reports it.
/// </summary>
/// <remarks>
/// The SDK's <c>RecoveryVerdict</c>. Three cases rather than a boolean because the three call for
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
