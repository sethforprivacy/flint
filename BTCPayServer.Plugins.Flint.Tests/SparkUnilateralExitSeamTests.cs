using System;
using System.Linq;
using Breez.Sdk.Spark;
using BTCPayServer.Plugins.Flint.Sdk;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The unilateral-exit seam's translation layer, which is pure and therefore the only part of that surface a
/// test can reach without a funded wallet and reachable operators.
/// </summary>
/// <remarks>
/// Everything asserted here is a place where the SDK's shape and the plugin's disagree, and where getting it
/// wrong is silent: a status union whose <c>Ready</c> case is the only one that authorises a broadcast, an
/// optional selection whose empty case means the opposite of what it looks like, a quote that echoes the request
/// back, the one typed error that carries a number worth acting on, and — the bridge nothing else covers — the
/// reconstruction of a stored exit back into the SDK's response, because <c>CheckUnilateralExit</c> judges the
/// reconstruction rather than anything the plugin holds.
/// </remarks>
public class SparkUnilateralExitSeamTests
{
    private const string Destination = "bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080";

    [Fact]
    public void No_leaf_ids_selects_automatically()
    {
        Assert.IsType<ExitLeafSelection.Auto>(SparkSdkClient.ToSdkLeafSelection(null));
        Assert.IsType<ExitLeafSelection.Auto>(SparkSdkClient.ToSdkLeafSelection([]));
    }

    [Fact]
    public void Leaf_ids_pin_the_selection_in_order()
    {
        var selection = Assert.IsType<ExitLeafSelection.Specific>(
            SparkSdkClient.ToSdkLeafSelection(["leaf-b", "leaf-a"]));

        Assert.Equal(["leaf-b", "leaf-a"], selection.leafIds);
    }

    /// <remarks>
    /// Rejected rather than filtered. A hole in a persisted leaf list would quote a <em>smaller</em> exit than
    /// the one the operator has already funded a UTXO for, and nothing downstream could tell.
    /// </remarks>
    [Fact]
    public void A_blank_leaf_id_is_refused()
    {
        Assert.Throws<ArgumentException>(() => SparkSdkClient.ToSdkLeafSelection(["leaf-a", "  "]));
    }

    [Fact]
    public void Transaction_kinds_are_mapped_by_name()
    {
        Assert.Equal(SparkExitTxKind.Fanout, SparkSdkClient.MapExitTxKind(UnilateralExitTxKind.FanOut));
        Assert.Equal(SparkExitTxKind.TreeNode, SparkSdkClient.MapExitTxKind(UnilateralExitTxKind.Node));
        Assert.Equal(SparkExitTxKind.Refund, SparkSdkClient.MapExitTxKind(UnilateralExitTxKind.Refund));
        Assert.Equal(SparkExitTxKind.Sweep, SparkSdkClient.MapExitTxKind(UnilateralExitTxKind.Sweep));
    }

    /// <summary>
    /// Every case the SDK's status union can report maps to the readiness the page and the record switch on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mapped by case rather than by ordinal, and the assertion that matters most is the last group: a
    /// transaction the SDK reports as <em>waiting</em> must not come back broadcastable. That invariant used to
    /// be expressible only as "the two enums are ordered differently, so do not cast" — SDK 0.25 replaced the
    /// flat enum with a union, so it is now stated directly and the whole class of cast bug is gone by
    /// construction.
    /// </para>
    /// <para>
    /// The harm the waiting case prevents: a tree node whose CSV timelock has not matured is
    /// <b>invalid</b>, not merely early. An operator handed it as "send this now" gets a rejected broadcast at
    /// best, and at worst pushes a transaction that a sibling has already spent the same output of the
    /// statechain for.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_chain_reported_status_becomes_the_readiness_that_decides_a_broadcast()
    {
        // A confirmed transaction carries the height its children's timelocks count from.
        var confirmed = SparkSdkClient.MapExitTxStatus(new ExitTransactionStatus.Confirmed(812_345));
        Assert.Equal(SparkExitTxReadiness.Confirmed, confirmed.Readiness);
        Assert.Equal(812_345u, confirmed.BlockHeight);
        Assert.False(confirmed.CanBroadcast);

        // Confirmed with a null height is its own case: the SDK reports it and it must not become an exception.
        var confirmedWithoutHeight = SparkSdkClient.MapExitTxStatus(new ExitTransactionStatus.Confirmed(null));
        Assert.Equal(SparkExitTxReadiness.Confirmed, confirmedWithoutHeight.Readiness);
        Assert.Null(confirmedWithoutHeight.BlockHeight);

        var ready = SparkSdkClient.MapExitTxStatus(new ExitTransactionStatus.Ready());
        Assert.Equal(SparkExitTxReadiness.Ready, ready.Readiness);
        Assert.True(ready.CanBroadcast);

        // The one the ordering test used to protect, now stated as the behaviour it was protecting: waiting is
        // never permission to broadcast.
        var waitingOnDependencies =
            SparkSdkClient.MapExitTxStatus(new ExitTransactionStatus.WaitingForDependencies());
        Assert.Equal(SparkExitTxReadiness.Waiting, waitingOnDependencies.Readiness);
        Assert.False(waitingOnDependencies.CanBroadcast);

        // A timelock keeps the height that makes "not yet" a number an operator can act on, rather than an
        // instruction to keep refreshing the page.
        var waitingOnTimelock =
            SparkSdkClient.MapExitTxStatus(new ExitTransactionStatus.WaitingForTimelock(901_200));
        Assert.Equal(SparkExitTxReadiness.Waiting, waitingOnTimelock.Readiness);
        Assert.Equal(901_200u, waitingOnTimelock.SpendableAtHeight);
        Assert.False(waitingOnTimelock.CanBroadcast);

        // And the two heights stay in their own fields: a waiting transaction reports no block height, so
        // nothing downstream can read a spendable-at height as "this already confirmed".
        Assert.Null(waitingOnTimelock.BlockHeight);

        var unverified = SparkSdkClient.MapExitTxStatus(new ExitTransactionStatus.Unverified());
        Assert.Equal(SparkExitTxReadiness.Unverified, unverified.Readiness);
        Assert.False(unverified.CanBroadcast);
    }

    /// <summary>
    /// <c>Ready</c> is the only readiness that authorises a broadcast, and it survives the trip back to the SDK.
    /// </summary>
    /// <remarks>
    /// The round trip is what makes the mapping a bijection rather than a lossy collapse, and the SDK is handed
    /// this back on every check. If <c>Ready</c> came back as anything else, an operator would be told to
    /// broadcast a transaction the check had just been shown as ready.
    /// </remarks>
    [Fact]
    public void A_readiness_survives_the_trip_back_to_the_SDK()
    {
        Assert.IsType<ExitTransactionStatus.Ready>(
            SparkSdkClient.ToSdkExitTxStatus(new SparkExitTxStatus(SparkExitTxReadiness.Ready)));

        Assert.IsType<ExitTransactionStatus.Unverified>(
            SparkSdkClient.ToSdkExitTxStatus(new SparkExitTxStatus(SparkExitTxReadiness.Unverified)));

        var confirmed = Assert.IsType<ExitTransactionStatus.Confirmed>(
            SparkSdkClient.ToSdkExitTxStatus(
                new SparkExitTxStatus(SparkExitTxReadiness.Confirmed, BlockHeight: 700_000)));
        Assert.Equal(700_000u, confirmed.blockHeight);

        // Both of the plugin's waiting cases go back as a wait, because the SDK replaces the status from the
        // chain anyway and the one thing that must never happen is a wait returning as permission to send.
        Assert.False(
            SparkSdkClient.ToSdkExitTxStatus(new SparkExitTxStatus(SparkExitTxReadiness.Waiting))
                is ExitTransactionStatus.Ready);
    }

    /// <summary>
    /// The default-initialised readiness is "waiting", so a value that was never set cannot authorise a broadcast.
    /// </summary>
    /// <remarks>
    /// A missing JSON field, a column added to an existing row, or a <c>default</c> in a switch all produce a
    /// zero-valued <see cref="SparkExitTxReadiness"/>, and on this surface a zero that meant "ready" would be
    /// instructions to push a timelocked transaction out. The plugin's own ordering puts <c>Waiting</c> first
    /// deliberately; the SDK's union has no ordinal at all to mirror, which is why this is asserted here rather
    /// than as an enum comparison.
    /// </remarks>
    [Fact]
    public void The_default_readiness_is_waiting_rather_than_ready()
    {
        Assert.Equal(SparkExitTxReadiness.Waiting, default(SparkExitTxReadiness));
        Assert.False(new SparkExitTxStatus(default).CanBroadcast);
    }

    [Fact]
    public void A_quote_carries_every_figure_the_binding_reports()
    {
        var quote = SparkSdkClient.MapExitQuote(new PrepareUnilateralExitResponse(
            leaves: [new UnilateralExitLeaf("leaf-a", 40_000), new UnilateralExitLeaf("leaf-b", 10_000)],
            recoverableValueSat: 50_000,
            totalFeeSat: 3_000,
            cpfpFeeSat: 0,
            fanoutFeeSat: 500,
            sweepFeeSat: 0,
            singleUtxoFundingSat: 4_200,
            perBranchFunding: [new PerBranchFunding("leaf-a", 3_000), new PerBranchFunding("leaf-b", 1_200)],
            feeRateSatPerVbyte: 7,
            destination: Destination,
            exitChainState: new ExitChainState([], [], [], [], [])));

        Assert.Equal(50_000, quote.RecoverableValueSat);
        Assert.Equal(3_000, quote.TotalFeeSat);
        Assert.Equal(500, quote.FanoutFeeSat);
        Assert.Equal(4_200, quote.SingleUtxoFundingSat);
        Assert.Equal(7UL, quote.FeeRateSatPerVbyte);
        Assert.Equal(Destination, quote.Destination);
        Assert.Equal(["leaf-a", "leaf-b"], quote.Leaves.Select(leaf => leaf.LeafId));
        Assert.Equal(40_000, quote.Leaves[0].ValueSat);
        Assert.Equal(["leaf-a", "leaf-b"], quote.PerBranchFunding.Select(branch => branch.LeafId));
        Assert.Equal(1_200, quote.PerBranchFunding[1].FundingSat);
        Assert.False(quote.IsEmpty);
    }

    /// <remarks>
    /// The case a caller must be able to report as "nothing worth exiting at this fee rate" rather than as a
    /// failure: automatic selection legitimately comes back with nothing.
    /// </remarks>
    [Fact]
    public void An_empty_selection_is_a_quote_rather_than_a_fault()
    {
        var quote = SparkSdkClient.MapExitQuote(new PrepareUnilateralExitResponse(
            leaves: [],
            recoverableValueSat: 0,
            totalFeeSat: 0,
            cpfpFeeSat: 0,
            fanoutFeeSat: 0,
            sweepFeeSat: 0,
            singleUtxoFundingSat: 0,
            perBranchFunding: [],
            feeRateSatPerVbyte: 1,
            destination: Destination,
            exitChainState: new ExitChainState([], [], [], [], [])));

        Assert.True(quote.IsEmpty);
        Assert.Empty(quote.Leaves);
        Assert.Empty(quote.PerBranchFunding);
    }

    /// <remarks>
    /// Every amount on this surface is a <c>u64</c>. Clamping rather than wrapping is what keeps an absurd value
    /// from arriving as a negative fee, which would pass every "is this worth exiting" comparison.
    /// </remarks>
    [Fact]
    public void Amounts_beyond_long_range_are_clamped_rather_than_wrapped()
    {
        var quote = SparkSdkClient.MapExitQuote(new PrepareUnilateralExitResponse(
            leaves: [new UnilateralExitLeaf("leaf-a", ulong.MaxValue)],
            recoverableValueSat: ulong.MaxValue,
            totalFeeSat: ulong.MaxValue,
            cpfpFeeSat: 0,
            fanoutFeeSat: ulong.MaxValue,
            sweepFeeSat: 0,
            singleUtxoFundingSat: ulong.MaxValue,
            perBranchFunding: [],
            feeRateSatPerVbyte: 1,
            destination: Destination,
            exitChainState: new ExitChainState([], [], [], [], [])));

        Assert.Equal(long.MaxValue, quote.RecoverableValueSat);
        Assert.Equal(long.MaxValue, quote.TotalFeeSat);
        Assert.Equal(long.MaxValue, quote.Leaves[0].ValueSat);
    }

    [Fact]
    public void A_tree_node_keeps_its_child_its_timelock_and_its_dependencies()
    {
        var mapped = SparkSdkClient.MapExitTransaction(new UnilateralExitTransaction(
            UnilateralExitTxKind.Node,
            nodeId: "node-1",
            txid: "aa",
            txHex: "0200aa",
            cpfpTxHex: "0200cpfp",
            csvTimelockBlocks: 1_008,
            dependsOn: ["fanout"],
            status: new ExitTransactionStatus.Ready()));

        Assert.Equal(SparkExitTxKind.TreeNode, mapped.Kind);
        Assert.Equal("node-1", mapped.NodeId);
        Assert.Equal("0200cpfp", mapped.CpfpTxHex);
        Assert.Equal(1_008u, mapped.CsvTimelockBlocks!.Value);
        Assert.Equal(["fanout"], mapped.DependsOn);
        Assert.True(mapped.RequiresPackageBroadcast);
        Assert.Equal(SparkExitTxReadiness.Ready, mapped.Status.Readiness);
    }

    /// <remarks>
    /// The fan-out and the sweep belong to no node and pay their own fee, so all three optional fields are null
    /// and the transaction is broadcast alone. Asserted because packaging is read off
    /// <see cref="SparkExitTransaction.RequiresPackageBroadcast"/> rather than off the kind.
    /// </remarks>
    [Fact]
    public void A_standalone_transaction_needs_no_package()
    {
        var mapped = SparkSdkClient.MapExitTransaction(new UnilateralExitTransaction(
            UnilateralExitTxKind.Sweep,
            nodeId: null,
            txid: "bb",
            txHex: "0200bb",
            cpfpTxHex: null,
            csvTimelockBlocks: null,
            dependsOn: null!,
            status: new ExitTransactionStatus.Unverified()));

        Assert.Null(mapped.NodeId);
        Assert.Null(mapped.CpfpTxHex);
        Assert.Null(mapped.CsvTimelockBlocks);
        Assert.Empty(mapped.DependsOn);
        Assert.False(mapped.RequiresPackageBroadcast);
        Assert.Equal(SparkExitTxReadiness.Unverified, mapped.Status.Readiness);
        Assert.False(mapped.Status.CanBroadcast);
    }

    [Fact]
    public void A_funding_output_is_offered_to_the_SDK_as_P2WPKH()
    {
        var input = Assert.IsType<CpfpInput.P2wpkh>(SparkSdkClient.ToSdkFundingInput(
            new SparkExitFundingUtxo("cc", 3, 5_000, "02aabb")));

        Assert.Equal("cc", input.txid);
        Assert.Equal(3u, input.vout);
        Assert.Equal(5_000UL, input.value);
        Assert.Equal("02aabb", input.pubkey);
    }

    [Fact]
    public void A_worthless_funding_output_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SparkSdkClient.ToSdkFundingInput(new SparkExitFundingUtxo("cc", 0, 0, "02aabb")));
    }

    /// <summary>
    /// The echo check that stands between a quote and a signed sweep.
    /// </summary>
    /// <remarks>
    /// The prepared response is handed straight back to the build, which signs the sweep against
    /// <em>its</em> destination rather than against the argument the caller passed — so a response describing a
    /// different address would hand an operator transactions paying somewhere else.
    /// </remarks>
    [Fact]
    public void A_quote_for_a_different_destination_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => SparkSdkClient.RequireQuoteEchoesRequest(
            Response(Destination, 7), 7, "bcrt1qsomewhereelse0000000000000000000000000"));
    }

    [Fact]
    public void A_quote_at_a_different_fee_rate_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => SparkSdkClient.RequireQuoteEchoesRequest(
            Response(Destination, 9), 7, Destination));
    }

    /// <remarks>
    /// bech32 and bech32m are case-insensitive, so an address pasted in upper case is the same address. The
    /// check exists to catch a <em>different</em> destination, not a differently spelled one.
    /// </remarks>
    [Fact]
    public void A_bech32_address_in_another_case_is_the_same_destination()
    {
        SparkSdkClient.RequireQuoteEchoesRequest(
            Response(Destination.ToUpperInvariant(), 7), 7, Destination);
    }

    [Fact]
    public void A_CPFP_shortfall_becomes_a_typed_error_carrying_the_amount_that_would_work()
    {
        var translated = Assert.IsType<SparkExitFundingShortfallException>(
            SparkErrors.TranslateUnilateralExit(new SdkException.InsufficientCpfpFunds(9_500)));

        Assert.Equal(9_500, translated.RequiredSat);
        Assert.Contains("9,500", translated.Message);
        Assert.DoesNotContain("@v1=", translated.Message);
    }

    /// <remarks>
    /// Null rather than the original exception, so the client can use it as an exception filter and let anything
    /// else escape with its own stack rather than re-throwing a copy.
    /// </remarks>
    [Fact]
    public void Any_other_failure_is_left_alone()
    {
        Assert.Null(SparkErrors.TranslateUnilateralExit(new SdkException.NetworkException("@v1=offline")));
    }

    /// <remarks>
    /// There is exactly one translation left. SDK 0.25 removed <c>SdkException.FundingUtxoConflict</c> along with
    /// the build shape that produced it — a spent funding output is now followed to whatever it became rather
    /// than reported — so the filter must not claim a conflict it no longer recognises.
    /// </remarks>
    [Fact]
    public void Only_the_CPFP_shortfall_is_translated()
    {
        Assert.IsType<SparkExitFundingShortfallException>(
            SparkErrors.TranslateUnilateralExit(new SdkException.InsufficientCpfpFunds(1)));

        Assert.Null(SparkErrors.TranslateUnilateralExit(new SdkException.InsufficientFunds(tokenIdentifier: null)));
    }

    [Fact]
    public void The_exit_errors_never_reach_a_merchant_with_a_UniFFI_prefix()
    {
        var described = SparkErrors.Describe(new SdkException.InsufficientCpfpFunds(1_234));
        Assert.False(string.IsNullOrWhiteSpace(described));
        Assert.DoesNotContain("@v1=", described);
    }

    /// <summary>
    /// A stored exit rebuilt into the SDK's response keeps every field <c>CheckUnilateralExit</c> judges it by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the bridge between the persisted record and the SDK, and nothing else crosses it.</b> The
    /// plugin never holds the SDK's response — it holds a serialised <see cref="SparkExitResult"/> on a database
    /// row, possibly days old, and <c>CheckUnilateralExit</c> is handed the result of rebuilding that back into
    /// the SDK's own type. So a field this loses is a field the SDK never sees, and the SDK reads
    /// <c>dependsOn</c> to decide whether a transaction is waiting on a confirmation, <c>csvTimelockBlocks</c> to
    /// decide whether its lock has matured, and <c>cpfpTxHex</c> to know the fee-paying child exists at all.
    /// </para>
    /// <para>
    /// <b>The two failures that would be silent and expensive.</b> A dropped <c>dependsOn</c> edge makes a
    /// node look independent, so the check reports it ready and the operator broadcasts a transaction whose
    /// parent has not confirmed — rejected, or worse, mined against a statechain state that has moved. A lost
    /// <c>cpfpTxHex</c> turns a package into a transaction paying no fee, and the operator is left with a
    /// stuck zero-fee parent and no explanation. Neither throws, and neither is visible on any other test.
    /// </para>
    /// <para>
    /// The whole set is asserted, not just the interesting transaction, because the check is handed all of them:
    /// a <c>Zip</c>-shaped rebuild that dropped the last entry would leave the sweep out of the exit the SDK was
    /// asked to judge.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_stored_exit_rebuilt_for_the_SDK_keeps_every_dependency_edge_and_hex()
    {
        var fanout = new SparkExitTransaction(
            SparkExitTxKind.Fanout, null, "txid:fanout", "0200fanout", null, null, [],
            new SparkExitTxStatus(SparkExitTxReadiness.Confirmed, BlockHeight: 800_100));

        var node = new SparkExitTransaction(
            SparkExitTxKind.TreeNode, "node:leaf-a", "txid:node:leaf-a", "0200nodeleaf-a",
            "0200cpfpleaf-a", 1_008, ["txid:fanout"],
            new SparkExitTxStatus(SparkExitTxReadiness.Waiting, SpendableAtHeight: 801_108));

        var refund = new SparkExitTransaction(
            SparkExitTxKind.Refund, "node:leaf-b", "txid:refund:leaf-b", "0200refundleaf-b",
            "0200cpfprefund", 144, ["txid:fanout", "txid:node:leaf-b"],
            new SparkExitTxStatus(SparkExitTxReadiness.Ready));

        var sweep = new SparkExitTransaction(
            SparkExitTxKind.Sweep, null, "txid:sweep", "0200sweep", null, null,
            ["txid:node:leaf-a", "txid:refund:leaf-b"],
            new SparkExitTxStatus(SparkExitTxReadiness.Unverified));

        var exit = new SparkExitResult(
            350_000,
            4_100,
            [fanout, node, refund, sweep],
            [new SparkExitLeaf("leaf-a", 300_000), new SparkExitLeaf("leaf-b", 50_000)]);

        var rebuilt = SparkSdkClient.ToSdkExit(exit);

        // The totals and the leaves the check echoes back for a caller to store over what it had.
        Assert.Equal(350_000UL, rebuilt.recoverableValueSat);
        Assert.Equal(4_100UL, rebuilt.totalFeeSat);
        Assert.Equal(["leaf-a", "leaf-b"], rebuilt.leaves.Select(leaf => leaf.leafId));
        Assert.Equal(300_000UL, rebuilt.leaves[0].value);

        // Order preserved: the SDK's topological order is the operator's broadcast schedule and nothing
        // downstream re-derives it.
        Assert.Equal(4, rebuilt.transactions.Length);

        Assert.Equal(UnilateralExitTxKind.FanOut, rebuilt.transactions[0].kind);
        Assert.Equal("txid:fanout", rebuilt.transactions[0].txid);
        Assert.Equal("0200fanout", rebuilt.transactions[0].txHex);
        Assert.Empty(rebuilt.transactions[0].dependsOn);

        // The node: the CPFP child, the timelock and the dependency edge all have to survive, because these are
        // the three fields that decide how and when it may be broadcast.
        var rebuiltNode = rebuilt.transactions[1];
        Assert.Equal(UnilateralExitTxKind.Node, rebuiltNode.kind);
        Assert.Equal("node:leaf-a", rebuiltNode.nodeId);
        Assert.Equal("0200nodeleaf-a", rebuiltNode.txHex);
        Assert.Equal("0200cpfpleaf-a", rebuiltNode.cpfpTxHex);
        Assert.Equal(1_008u, rebuiltNode.csvTimelockBlocks);
        Assert.Equal(["txid:fanout"], rebuiltNode.dependsOn);

        // A refund and a node with more than one parent, so the rebuild is not merely preserving a single edge.
        Assert.Equal(UnilateralExitTxKind.Refund, rebuilt.transactions[2].kind);
        Assert.Equal(["txid:fanout", "txid:node:leaf-b"], rebuilt.transactions[2].dependsOn);

        Assert.Equal(UnilateralExitTxKind.Sweep, rebuilt.transactions[3].kind);
        Assert.Null(rebuilt.transactions[3].cpfpTxHex);
        Assert.Equal(["txid:node:leaf-a", "txid:refund:leaf-b"], rebuilt.transactions[3].dependsOn);

        // The statuses go back as the cases the stored readiness came from, heights included — the SDK replaces
        // them from the chain, but a wait must not come back as a ready.
        var ready = Assert.IsType<ExitTransactionStatus.Confirmed>(rebuilt.transactions[0].status);
        Assert.Equal(800_100u, ready.blockHeight);
        var waiting = Assert.IsType<ExitTransactionStatus.WaitingForDependencies>(
            rebuilt.transactions[1].status);
        Assert.NotNull(waiting);
        Assert.IsType<ExitTransactionStatus.Ready>(rebuilt.transactions[2].status);
        Assert.IsType<ExitTransactionStatus.Unverified>(rebuilt.transactions[3].status);
    }

    /// <remarks>
    /// The check reads the chain and nothing else, so the funding outputs are absent by design — the SDK
    /// follows them at build time and an exit being followed does not rebuild. Asserted because a rebuild that
    /// invented a funding input would hand the SDK an output the plugin has no key for.
    /// </remarks>
    [Fact]
    public void A_rebuilt_exit_carries_no_funding_inputs()
    {
        var rebuilt = SparkSdkClient.ToSdkExit(new SparkExitResult(0, 0, [], []));

        Assert.Empty(rebuilt.transactions);
        Assert.Empty(rebuilt.leaves);
        Assert.Empty(rebuilt.fundingInputs);
    }

    private static PrepareUnilateralExitResponse Response(string destination, ulong feeRate) =>
        new(
            leaves: [],
            recoverableValueSat: 0,
            totalFeeSat: 0,
            cpfpFeeSat: 0,
            fanoutFeeSat: 0,
            sweepFeeSat: 0,
            singleUtxoFundingSat: 0,
            perBranchFunding: [],
            feeRateSatPerVbyte: feeRate,
            destination: destination,
            exitChainState: new ExitChainState([], [], [], [], []));
}
