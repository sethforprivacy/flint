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
/// wrong is silent: two enums ordered differently, an optional selection whose empty case means the opposite of
/// what it looks like, a quote that echoes the request back, and two typed errors whose whole value is the
/// numbers they carry.
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

    [Fact]
    public void Confirmation_statuses_are_mapped_by_name()
    {
        Assert.Equal(SparkExitTxStatus.Confirmed, SparkSdkClient.MapExitTxStatus(ConfirmationStatus.Confirmed));
        Assert.Equal(
            SparkExitTxStatus.Unconfirmed, SparkSdkClient.MapExitTxStatus(ConfirmationStatus.Unconfirmed));
        Assert.Equal(SparkExitTxStatus.Unverified, SparkSdkClient.MapExitTxStatus(ConfirmationStatus.Unverified));
    }

    /// <summary>
    /// Guards the reason the status mapping is written out rather than cast.
    /// </summary>
    /// <remarks>
    /// The SDK orders its enum <c>Confirmed = 0, Unconfirmed = 1</c> and the plugin's is the other way round, so
    /// a numeric cast reports every unmined transaction as confirmed. This asserts the two orderings still
    /// disagree, so that an SDK bump which aligned them cannot quietly make a future cast look harmless.
    /// </remarks>
    [Fact]
    public void A_numeric_cast_between_the_status_enums_would_be_wrong()
    {
        Assert.NotEqual((int)ConfirmationStatus.Confirmed, (int)SparkExitTxStatus.Confirmed);
        Assert.Equal(0, (int)SparkExitTxStatus.Unconfirmed);
    }

    [Fact]
    public void A_quote_carries_every_figure_the_binding_reports()
    {
        var quote = SparkSdkClient.MapExitQuote(new PrepareUnilateralExitResponse(
            leaves: [new UnilateralExitLeaf("leaf-a", 40_000), new UnilateralExitLeaf("leaf-b", 10_000)],
            recoverableValueSat: 50_000,
            totalFeeSat: 3_000,
            fanoutFeeSat: 500,
            singleUtxoFundingSat: 4_200,
            perBranchFunding: [new PerBranchFunding("leaf-a", 3_000), new PerBranchFunding("leaf-b", 1_200)],
            feeRateSatPerVbyte: 7,
            destination: Destination));

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
            fanoutFeeSat: 0,
            singleUtxoFundingSat: 0,
            perBranchFunding: [],
            feeRateSatPerVbyte: 1,
            destination: Destination));

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
            fanoutFeeSat: ulong.MaxValue,
            singleUtxoFundingSat: ulong.MaxValue,
            perBranchFunding: [],
            feeRateSatPerVbyte: 1,
            destination: Destination));

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
            status: ConfirmationStatus.Unconfirmed));

        Assert.Equal(SparkExitTxKind.TreeNode, mapped.Kind);
        Assert.Equal("node-1", mapped.NodeId);
        Assert.Equal("0200cpfp", mapped.CpfpTxHex);
        Assert.Equal(1_008u, mapped.CsvTimelockBlocks!.Value);
        Assert.Equal(["fanout"], mapped.DependsOn);
        Assert.True(mapped.RequiresPackageBroadcast);
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
            status: ConfirmationStatus.Unverified));

        Assert.Null(mapped.NodeId);
        Assert.Null(mapped.CpfpTxHex);
        Assert.Null(mapped.CsvTimelockBlocks);
        Assert.Empty(mapped.DependsOn);
        Assert.False(mapped.RequiresPackageBroadcast);
        Assert.Equal(SparkExitTxStatus.Unverified, mapped.Status);
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

    [Fact]
    public void A_funding_conflict_becomes_a_typed_error_naming_the_outpoint()
    {
        var translated = Assert.IsType<SparkExitFundingUtxoConflictException>(
            SparkErrors.TranslateUnilateralExit(new SdkException.FundingUtxoConflict("dd", 2)));

        Assert.Equal("dd:2", translated.OutPoint);
        Assert.Contains("dd:2", translated.Message);
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

    [Fact]
    public void The_exit_errors_never_reach_a_merchant_with_a_UniFFI_prefix()
    {
        Exception[] errors =
        [
            new SdkException.InsufficientCpfpFunds(1_234),
            new SdkException.FundingUtxoConflict("ee", 1)
        ];

        foreach (var error in errors)
        {
            var described = SparkErrors.Describe(error);
            Assert.False(string.IsNullOrWhiteSpace(described));
            Assert.DoesNotContain("@v1=", described);
        }
    }

    private static PrepareUnilateralExitResponse Response(string destination, ulong feeRate) =>
        new([], 0, 0, 0, 0, [], feeRate, destination);
}
