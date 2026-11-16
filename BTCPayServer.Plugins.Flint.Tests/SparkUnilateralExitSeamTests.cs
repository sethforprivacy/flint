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

    /// <remarks>
    /// <b>Automatic means every leaf, not only the ones that already left the balance.</b> SDK 0.28 replaced
    /// <c>Auto</c> with two cases, and the difference between them is the whole feature: <c>RecoverableOnly</c>
    /// covers funds the operators have already moved on-chain, while <c>All</c> covers those <em>and</em> the
    /// balance still in Spark — which is what a merchant exiting an uncooperative operator needs. Selecting
    /// <c>RecoverableOnly</c> here would report "nothing worth recovering" for exactly the funds the page exists
    /// to rescue, so this is asserted rather than left to the type name.
    /// </remarks>
    [Fact]
    public void No_leaf_ids_selects_every_leaf_rather_than_only_what_already_left_the_balance()
    {
        Assert.IsType<ExitLeafSelection.All>(SparkSdkClient.ToSdkLeafSelection(null));
        Assert.IsType<ExitLeafSelection.All>(SparkSdkClient.ToSdkLeafSelection([]));
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

    /// <remarks>
    /// Including the cooperative case, which is the one an SDK bump added rather than renamed: a set that can
    /// hold a cooperative recovery is a set the page has to give different broadcast instructions for, and the
    /// mapping is by name precisely so an added variant arrives as a failure here rather than as a cast.
    /// </remarks>
    [Fact]
    public void Transaction_kinds_are_mapped_by_name()
    {
        Assert.Equal(SparkExitTxKind.Cooperative, SparkSdkClient.MapExitTxKind(RecoveryTxKind.Cooperative));
        Assert.Equal(SparkExitTxKind.Fanout, SparkSdkClient.MapExitTxKind(RecoveryTxKind.FanOut));
        Assert.Equal(SparkExitTxKind.TreeNode, SparkSdkClient.MapExitTxKind(RecoveryTxKind.Node));
        Assert.Equal(SparkExitTxKind.Refund, SparkSdkClient.MapExitTxKind(RecoveryTxKind.Refund));
        Assert.Equal(SparkExitTxKind.Sweep, SparkSdkClient.MapExitTxKind(RecoveryTxKind.Sweep));
    }

    /// <summary>
    /// A cooperative recovery goes out alone, immediately, and needs no package.
    /// </summary>
    /// <remarks>
    /// It is the one kind that is not an exit step: the operators co-sign it against an output they already put
    /// on-chain, it pays its own fee out of the funds it recovers, and it has no child, no timelock and no
    /// dependency. Asserted because the page reads <see cref="SparkExitTransaction.RequiresPackageBroadcast"/>
    /// off the child rather than off the kind — so this proves the no-child shape really does read as "broadcast
    /// alone" for the new kind, and not as a tree node whose child went missing.
    /// </remarks>
    [Fact]
    public void A_cooperative_recovery_is_standalone_and_ready_when_it_is_returned()
    {
        var mapped = SparkSdkClient.MapExitTransaction(new RecoveryTransaction(
            RecoveryTxKind.Cooperative,
            nodeId: "leaf-a",
            txid: "cc",
            txHex: "0200cc",
            cpfpTxHex: null,
            csvTimelockBlocks: null,
            dependsOn: [],
            status: new ExitTransactionStatus.Ready()));

        Assert.Equal(SparkExitTxKind.Cooperative, mapped.Kind);
        Assert.Equal("leaf-a", mapped.NodeId);
        Assert.Null(mapped.CpfpTxHex);
        Assert.Null(mapped.CsvTimelockBlocks);
        Assert.Empty(mapped.DependsOn);
        Assert.False(mapped.RequiresPackageBroadcast);
        Assert.True(mapped.Status.CanBroadcast);
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
        var quote = SparkSdkClient.MapExitQuote(new PrepareRecoverFundsResponse(
            leaves:
            [
                new RecoverFundsLeaf("leaf-a", 40_000, RecoveryMethod.Unilateral),
                new RecoverFundsLeaf("leaf-b", 10_000, RecoveryMethod.Cooperative)
            ],
            skipped: [],
            recoverableValueSats: 50_000,
            totalFeeSats: 3_000,
            cooperativeFeeSats: 0,
            cpfpFeeSats: 0,
            fanoutFeeSats: 500,
            refundFeeSats: 0,
            sweepFeeSats: 0,
            funding: new RecoveryFunding(
                singleUtxoSats: 4_200,
                perBranch:
                [
                    new PerBranchFunding("leaf-a", 3_000),
                    new PerBranchFunding("leaf-b", 1_200)
                ],
                strategy: CpfpFundingStrategy.PerBranch),
            feeRateSatPerVbyte: 7,
            destination: Destination,
            exitChainState: new ExitChainState([], [], [], [], [])));

        Assert.Equal(50_000, quote.RecoverableValueSat);
        Assert.Equal(3_000, quote.TotalFeeSat);
        Assert.Equal(500, quote.FanoutFeeSat);
        Assert.Equal(4_200, quote.SingleUtxoFundingSat);
        Assert.True(quote.RequiresFunding);
        Assert.Equal(7UL, quote.FeeRateSatPerVbyte);
        Assert.Equal(Destination, quote.Destination);
        Assert.Equal(["leaf-a", "leaf-b"], quote.Leaves.Select(leaf => leaf.LeafId));
        Assert.Equal(40_000, quote.Leaves[0].ValueSat);
        // The per-leaf method reaches the page, because it is what explains why the quote wants funding at all.
        Assert.Equal(SparkExitRecoveryMethod.Unilateral, quote.Leaves[0].Method);
        Assert.False(quote.Leaves[0].IsCooperative);
        Assert.True(quote.Leaves[1].IsCooperative);
        Assert.Equal(["leaf-a", "leaf-b"], quote.PerBranchFunding.Select(branch => branch.LeafId));
        Assert.Equal(1_200, quote.PerBranchFunding[1].FundingSat);
        Assert.False(quote.IsEmpty);
    }

    /// <summary>
    /// A quote whose leaves are all cooperative needs no Bitcoin from the operator, and says so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The SDK reports this by <em>omitting</em> the funding requirement, so the plugin's job is to translate an
    /// absence into a positive statement rather than into a zero an operator is asked to fund. The failure this
    /// prevents is concrete: a page that rendered "send 0 sat" and waited would leave a merchant with a
    /// recoverable balance and no button that would build it.
    /// </para>
    /// <para>
    /// The single-UTXO figure is asserted at zero as well, because that is the number the funding address is
    /// sized for and the one a rebuild's veto compares against; a null read as one sat, or a missing field read
    /// as the last quote's figure, would both be wrong in the same direction.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_recovery_with_nothing_to_fund_says_so_rather_than_asking_for_zero_sat()
    {
        var quote = SparkSdkClient.MapExitQuote(Response(Destination, 7, requiresFunding: false));

        Assert.False(quote.RequiresFunding);
        Assert.Equal(0, quote.SingleUtxoFundingSat);
        Assert.Empty(quote.PerBranchFunding);
        Assert.All(quote.Leaves, leaf => Assert.True(leaf.IsCooperative));

        // And the other half of the translation: a quote that does need funding still says it does.
        Assert.True(SparkSdkClient.MapExitQuote(Response(Destination, 7)).RequiresFunding);
    }

    /// <summary>
    /// The leaves a selection covered and left out arrive with a reason a merchant can act on.
    /// </summary>
    /// <remarks>
    /// Before SDK 0.28 a skipped leaf was simply absent, and "nothing worth exiting right now" had to cover
    /// every case. Several of the new reasons are actionable — cheaper fees would include the leaf, or the
    /// operators were unreachable — and conflating them would tell an operator to wait for the fee market when
    /// the real answer was to try again in a minute.
    /// </remarks>
    [Fact]
    public void A_skipped_leaf_carries_the_reason_that_decides_what_happens_next()
    {
        var quote = SparkSdkClient.MapExitQuote(Response(
            Destination,
            7,
            skipped:
            [
                new SkippedLeaf("leaf-small", 900, new SkippedLeafReason.FeeExceedsValue()),
                new SkippedLeaf("leaf-swept-out", 0, new SkippedLeafReason.NotRecoverable("no exit data"))
            ]));

        Assert.Equal(["leaf-small", "leaf-swept-out"], quote.Skipped!.Select(leaf => leaf.LeafId));
        Assert.Equal(900, quote.Skipped[0].ValueSat);
        Assert.Contains("cost more than it is worth", quote.Skipped[0].Reason);
        // The SDK's own message is carried, because it is the part that says what is wrong with the leaf.
        Assert.Contains("no exit data", quote.Skipped[1].Reason);
    }

    /// <remarks>
    /// <para>
    /// Unlike every other mapping on this seam, an unrecognised <em>reason</em> does not refuse the quote. The
    /// information is a label on a leaf that is not being recovered, so failing here would take a recoverable
    /// balance away from an operator over a description — the exact failure this feature exists to prevent.
    /// </para>
    /// <para>
    /// The unrecognised case is exercised with a local subclass rather than left to inspection, because the
    /// fallback only ever runs when the SDK adds a variant: a binding update would otherwise turn this into the
    /// quote-refusing failure the fallback exists to avoid, and nothing would have caught it.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_unrecognised_skipping_reason_is_described_rather_than_refused()
    {
        Assert.Equal("Spark did not say why", SparkSdkClient.DescribeSkippedReason(null));

        var unknown = SparkSdkClient.DescribeSkippedReason(new UnknownSkippedReason());
        Assert.Contains("does not recognise", unknown);
        Assert.Contains(nameof(UnknownSkippedReason), unknown);

        // A known reason says what it means, and is not described as unknown.
        var known = SparkSdkClient.DescribeSkippedReason(new SkippedLeafReason.FeeExceedsValue());
        Assert.Contains("cost more than it is worth", known);
        Assert.DoesNotContain("does not recognise", known);
    }

    /// <summary>
    /// A skipping reason from an SDK newer than this plugin, which is the only way the fallback is reached.
    /// </summary>
    private sealed record UnknownSkippedReason : SkippedLeafReason;

    /// <summary>
    /// A cooperative leaf the operators refused is reported as a leaf needing its own attempt, not as a failed
    /// build.
    /// </summary>
    /// <remarks>
    /// The build is not atomic across leaves: the SDK co-signs them one at a time, and one refusal leaves the
    /// rest built. Silently dropping the refused leaf would present a partial recovery as a complete one, which
    /// is why the reason — including the fee rate a replacement would need — reaches the operator.
    /// </remarks>
    [Fact]
    public void A_refused_cooperative_leaf_reports_the_reason_and_the_rate_a_retry_needs()
    {
        Assert.Contains(
            "could not co-sign",
            SparkSdkClient.DescribeCooperativeFailure(
                new CooperativeRecoveryError.OperatorsUnavailable("busy")));
        Assert.Contains("busy", SparkSdkClient.DescribeCooperativeFailure(
            new CooperativeRecoveryError.OperatorsUnavailable("busy")));

        var tooLow = SparkSdkClient.DescribeCooperativeFailure(
            new CooperativeRecoveryError.ReplacementFeeTooLow(
                requiredFeeSats: 1_250, requiredFeeRateSatPerVbyte: 12));
        Assert.Contains("12 sat/vB", tooLow);
        Assert.Contains("1,250", tooLow);
    }

    /// <remarks>
    /// The SDK leaves a finished leaf out of a quote even when it is named, and it also leaves out — silently — a
    /// named leaf missing from its local storage. The chain read is the only thing that tells the two apart, so
    /// a leaf whose refund was swept and a leaf whose branch stopped must both come through as finished, and a
    /// refund merely on-chain (the sweep still to come) must not.
    /// </remarks>
    [Fact]
    public void A_quote_reports_which_leaves_the_chain_shows_as_finished()
    {
        var quote = SparkSdkClient.MapExitQuote(new PrepareRecoverFundsResponse(
            leaves: [new RecoverFundsLeaf("leaf-live", 40_000, RecoveryMethod.Unilateral)],
            skipped: [],
            recoverableValueSats: 40_000,
            totalFeeSats: 3_000,
            cooperativeFeeSats: 0,
            cpfpFeeSats: 0,
            fanoutFeeSats: 0,
            refundFeeSats: 0,
            sweepFeeSats: 0,
            funding: new RecoveryFunding(
                singleUtxoSats: 4_200, perBranch: [], strategy: CpfpFundingStrategy.PerBranch),
            feeRateSatPerVbyte: 7,
            destination: Destination,
            exitChainState: new ExitChainState(
                [],
                [
                    new ExitRefund("leaf-swept", new ExitRefundState.Swept()),
                    new ExitRefund("leaf-refunded", new ExitRefundState.OnChain("0200", 0, 39_000, 800_000))
                ],
                ["leaf-stopped"],
                [],
                [])));

        Assert.Equal(["leaf-swept", "leaf-stopped"], quote.FinishedLeafIds);

        // Pinned to all four plus one that simply is not there: only the missing one is uncovered. The refunded
        // leaf is not finished and not in the quote, so it counts as uncovered too — its sweep is still ahead.
        Assert.Equal(
            ["leaf-refunded", "leaf-gone"],
            quote.UncoveredOf(["leaf-live", "leaf-swept", "leaf-refunded", "leaf-stopped", "leaf-gone"]));
    }

    [Fact]
    public void A_missing_chain_state_reads_as_nothing_finished()
    {
        Assert.Empty(SparkSdkClient.MapFinishedLeafIds(null));
        Assert.Empty(SparkSdkClient.MapFinishedLeafIds(new ExitChainState([], [null!], null!, [], [])));
    }

    /// <remarks>
    /// The case a caller must be able to report as "nothing worth exiting at this fee rate" rather than as a
    /// failure: automatic selection legitimately comes back with nothing.
    /// </remarks>
    [Fact]
    public void An_empty_selection_is_a_quote_rather_than_a_fault()
    {
        var quote = SparkSdkClient.MapExitQuote(Response(Destination, 1));

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
        var quote = SparkSdkClient.MapExitQuote(new PrepareRecoverFundsResponse(
            leaves: [new RecoverFundsLeaf("leaf-a", ulong.MaxValue, RecoveryMethod.Unilateral)],
            skipped: [],
            recoverableValueSats: ulong.MaxValue,
            totalFeeSats: ulong.MaxValue,
            cooperativeFeeSats: 0,
            cpfpFeeSats: 0,
            fanoutFeeSats: ulong.MaxValue,
            refundFeeSats: 0,
            sweepFeeSats: 0,
            funding: new RecoveryFunding(
                singleUtxoSats: ulong.MaxValue, perBranch: [], strategy: CpfpFundingStrategy.PerBranch),
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
        var mapped = SparkSdkClient.MapExitTransaction(new RecoveryTransaction(
            RecoveryTxKind.Node,
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
        var mapped = SparkSdkClient.MapExitTransaction(new RecoveryTransaction(
            RecoveryTxKind.Sweep,
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
        Assert.Equal(5_000UL, input.valueSats);
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
            SparkErrors.TranslateUnilateralExit(new SdkException.InsufficientCpfpFunds(9_500, docsUrl: null)));

        Assert.Equal(9_500, translated.RequiredSat);
        Assert.Contains("9,500", translated.Message);
        Assert.DoesNotContain("@v1=", translated.Message);
    }

    /// <remarks>
    /// <para>
    /// Null rather than the original exception, so the client can use it as an exception filter and let anything
    /// else escape with its own stack rather than re-throwing a copy.
    /// </para>
    /// <para>
    /// There is exactly one translation left. SDK 0.25 removed <c>SdkException.FundingUtxoConflict</c> along with
    /// the build shape that produced it — a spent funding output is now followed to whatever it became rather
    /// than reported — so the filter must not claim a conflict it no longer recognises, and the ordinary
    /// <c>InsufficientFunds</c> is a different shortfall from the CPFP one, not another spelling of it.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("network")]
    [InlineData("insufficient-funds")]
    public void Any_other_failure_is_left_alone(string failure)
    {
        SdkException exception = failure switch
        {
            "network" => new SdkException.NetworkException("@v1=offline"),
            "insufficient-funds" => new SdkException.InsufficientFunds(tokenIdentifier: null),
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "No exception for this row.")
        };

        Assert.Null(SparkErrors.TranslateUnilateralExit(exception));
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
            7,
            Destination,
            [fanout, node, refund, sweep],
            [
                new SparkExitLeaf("leaf-a", 300_000, SparkExitRecoveryMethod.Unilateral),
                new SparkExitLeaf("leaf-b", 50_000, SparkExitRecoveryMethod.Cooperative)
            ]);

        var rebuilt = SparkSdkClient.ToSdkExit(exit);

        // The totals and the leaves the check echoes back for a caller to store over what it had.
        Assert.Equal(350_000UL, rebuilt.recoverableValueSats);
        Assert.Equal(4_100UL, rebuilt.totalFeeSats);
        // The rate and the destination travel with the set: the check takes a whole response, and a zero here
        // would be a different document from the one the build returned.
        Assert.Equal(7UL, rebuilt.feeRateSatPerVbyte);
        Assert.Equal(Destination, rebuilt.destination);
        Assert.Equal(["leaf-a", "leaf-b"], rebuilt.leaves.Select(leaf => leaf.leafId));
        Assert.Equal(300_000UL, rebuilt.leaves[0].valueSats);
        Assert.Equal(RecoveryMethod.Unilateral, rebuilt.leaves[0].method);
        Assert.Equal(RecoveryMethod.Cooperative, rebuilt.leaves[1].method);

        // Order preserved: the SDK's topological order is the operator's broadcast schedule and nothing
        // downstream re-derives it.
        Assert.Equal(4, rebuilt.transactions.Length);

        Assert.Equal(RecoveryTxKind.FanOut, rebuilt.transactions[0].kind);
        Assert.Equal("txid:fanout", rebuilt.transactions[0].txid);
        Assert.Equal("0200fanout", rebuilt.transactions[0].txHex);
        Assert.Empty(rebuilt.transactions[0].dependsOn);

        // The node: the CPFP child, the timelock and the dependency edge all have to survive, because these are
        // the three fields that decide how and when it may be broadcast.
        var rebuiltNode = rebuilt.transactions[1];
        Assert.Equal(RecoveryTxKind.Node, rebuiltNode.kind);
        Assert.Equal("node:leaf-a", rebuiltNode.nodeId);
        Assert.Equal("0200nodeleaf-a", rebuiltNode.txHex);
        Assert.Equal("0200cpfpleaf-a", rebuiltNode.cpfpTxHex);
        Assert.Equal(1_008u, rebuiltNode.csvTimelockBlocks);
        Assert.Equal(["txid:fanout"], rebuiltNode.dependsOn);

        // A refund and a node with more than one parent, so the rebuild is not merely preserving a single edge.
        Assert.Equal(RecoveryTxKind.Refund, rebuilt.transactions[2].kind);
        Assert.Equal(["txid:fanout", "txid:node:leaf-b"], rebuilt.transactions[2].dependsOn);

        Assert.Equal(RecoveryTxKind.Sweep, rebuilt.transactions[3].kind);
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
        var rebuilt = SparkSdkClient.ToSdkExit(new SparkExitResult(0, 0, 0, Destination, [], []));

        Assert.Empty(rebuilt.transactions);
        Assert.Empty(rebuilt.leaves);
        Assert.Empty(rebuilt.fundingInputs);
    }

    /// <summary>
    /// A quote shaped like the binding's, with everything a caller does not care about left at its empty value.
    /// </summary>
    /// <remarks>
    /// <paramref name="requiresFunding"/> defaults to true because that is the ordinary shape — a unilateral
    /// leaf pays no fee of its own, so it always needs a UTXO of the operator's. The false case is the SDK's
    /// absent funding requirement, spelled as a flag so a caller says what it means rather than passing a null
    /// that a helper could plausibly have treated as "use the default".
    /// </remarks>
    private static PrepareRecoverFundsResponse Response(
        string destination,
        ulong feeRate,
        bool requiresFunding = true,
        SkippedLeaf[]? skipped = null,
        RecoverFundsLeaf[]? leaves = null) =>
        new(
            leaves: leaves ?? (requiresFunding
                ? []
                : [new RecoverFundsLeaf("leaf-a", 40_000, RecoveryMethod.Cooperative)]),
            skipped: skipped ?? [],
            recoverableValueSats: 0,
            totalFeeSats: 0,
            cooperativeFeeSats: 0,
            cpfpFeeSats: 0,
            fanoutFeeSats: 0,
            refundFeeSats: 0,
            sweepFeeSats: 0,
            funding: requiresFunding
                ? new RecoveryFunding(
                    singleUtxoSats: 0, perBranch: [], strategy: CpfpFundingStrategy.PerBranch)
                : null,
            feeRateSatPerVbyte: feeRate,
            destination: destination,
            exitChainState: new ExitChainState([], [], [], [], []));
}
