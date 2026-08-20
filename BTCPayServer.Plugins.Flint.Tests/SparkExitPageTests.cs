using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Models;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The unilateral-exit page: the feature gate, the disclosure-first ordering, and what the controller and the
/// template make of the service's typed page data.
/// </summary>
/// <remarks>
/// <para>
/// The first thing being pinned here is not about exits at all: a feature behind an environment switch is
/// <em>invisible</em> when the switch is off. Every action, the GET included, answers <c>NotFound</c> from
/// inside the action, because a redirect or a validation error on one of them is already an admission that the
/// route exists. (The filters in front of the action still answer first — an unauthenticated caller gets the
/// pipeline's 401 whether the feature is on or off — which is why the controller's remarks say the gate hides
/// the flow from callers already entitled to be on this controller, not the route prefix from the world.)
/// </para>
/// <para>
/// The second is that the controller reads nothing. It used to deserialise the record's JSON columns itself,
/// which meant two sets of serialiser options for one format and a failure mode — an empty transaction table
/// for an exit worth a store's whole balance — that threw nothing. The service now owns both ends and this
/// class asserts the controller only copies fields across, including the "could not be read" flag it must
/// carry rather than smooth over.
/// </para>
/// <para>
/// The third is the template, asserted as text because no test in this suite renders a view (see
/// <see cref="ViewComponentCompatibilityTests"/> for why, and what it costs). What is checked there is
/// load-bearing and would not fail anything else: signed hex sits behind
/// <c>CanModifyStoreSettings</c>, the funding shortfall is judged by the largest single output rather than the
/// total, and no state of the page is a dead end whose only control is the one its own copy forbids.
/// </para>
/// <para>
/// <b>One class, in the serialised collection.</b> The gate is an environment variable, which is process-wide
/// state that xUnit's per-class parallelism would let two tests fight over. Every test here restores it in a
/// <c>finally</c>, and the class joins <see cref="UnilateralExitTestCollection"/> — the same collection
/// <see cref="SparkUnilateralExitServiceTests"/> uses — so the two classes that read the variable cannot run
/// at the same time as each other or as anything else.
/// </para>
/// </remarks>
[Collection(UnilateralExitTestCollection.Name)]
public class SparkExitPageTests
{
    private const string Store = SparkSurfaceHarness.AttackerStore;
    private const string Gate = "FLINT_EXPERIMENTAL_UNILATERAL_EXIT";

    /// <summary>A regtest address, so a destination in a test reads like one a merchant would type.</summary>
    private const string Destination = "bcrt1qt8hufshrz62z5vj4q40uqx6c6ytlujy5s03gwm";

    #region The gate

    [Fact]
    public async Task With_the_feature_off_every_exit_route_is_not_found()
    {
        using var gate = FeatureGate(enabled: false);

        // Deliberately a service that would answer happily. What must produce the 404 is the gate, not an
        // absent dependency — otherwise the test would pass on a build where the gate had been deleted.
        var exit = new StubExitService();
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, unilateralExit: exit);

        Assert.IsType<NotFoundResult>(await h.Mvc.Exit(Store, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await h.Mvc.AcknowledgeExit(Store, CancellationToken.None));
        Assert.IsType<NotFoundResult>(
            await h.Mvc.QuoteExit(
                Store,
                new SparkExitViewModel { FeeRateSatPerVbyte = 10, DestinationAddress = Destination },
                CancellationToken.None));
        Assert.IsType<NotFoundResult>(await h.Mvc.BuildExit(Store, "some-record", CancellationToken.None));
        Assert.IsType<NotFoundResult>(await h.Mvc.AbandonExit(Store, "some-record", CancellationToken.None));
        Assert.IsType<NotFoundResult>(await h.Mvc.CompleteExit(Store, "some-record", CancellationToken.None));
        Assert.IsType<NotFoundResult>(
            await h.Mvc.SetExitExplorer(Store, "https://esplora.example/api", CancellationToken.None));

        // And nothing reached the service, so a gate that 404'd after acting would still fail this.
        Assert.Empty(exit.Calls);
    }

    [Fact]
    public async Task With_the_feature_on_the_exit_routes_still_refuse_another_stores_id()
    {
        using var gate = FeatureGate(enabled: true);

        // The store the request was authorised for is the attacker's; the id on the route is the victim's. The
        // same hole the rest of this controller is guarded against (see SparkControllerStoreScopeTests), and a
        // feature gate is no substitute for the guard — an exit built for another store's leaves would send its
        // balance to an address this caller chose.
        var exit = new StubExitService();
        var h = SparkSurfaceHarness.Create(unilateralExit: exit);
        var victim = SparkSurfaceHarness.VictimStore;

        Assert.IsType<NotFoundResult>(await h.Mvc.Exit(victim, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await h.Mvc.AcknowledgeExit(victim, CancellationToken.None));
        Assert.IsType<NotFoundResult>(
            await h.Mvc.QuoteExit(
                victim,
                new SparkExitViewModel { FeeRateSatPerVbyte = 10, DestinationAddress = Destination },
                CancellationToken.None));
        Assert.IsType<NotFoundResult>(await h.Mvc.BuildExit(victim, "record-7", CancellationToken.None));
        Assert.IsType<NotFoundResult>(await h.Mvc.AbandonExit(victim, "record-7", CancellationToken.None));
        Assert.IsType<NotFoundResult>(await h.Mvc.CompleteExit(victim, "record-7", CancellationToken.None));
        Assert.IsType<NotFoundResult>(
            await h.Mvc.SetExitExplorer(victim, "https://esplora.example/api", CancellationToken.None));

        Assert.Empty(exit.Calls);
    }

    #endregion

    #region What the page shows

    [Fact]
    public async Task The_page_leads_with_the_disclosure_until_it_has_been_acknowledged()
    {
        using var gate = FeatureGate(enabled: true);

        var exit = new StubExitService
        {
            Page = Page(disclosureAcknowledged: false, balanceSats: 250_000)
        };

        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, unilateralExit: exit);

        var model = await RenderExit(h);

        Assert.Equal(Store, model.StoreId);
        Assert.False(model.DisclosureAcknowledged);
        Assert.True(model.WalletRunning);
        Assert.Equal(250_000, model.BalanceSats);
        Assert.Null(model.ActiveRecord);
        Assert.Empty(model.Transactions);
    }

    [Fact]
    public async Task An_acknowledged_store_with_nothing_in_flight_gets_the_quote_form()
    {
        using var gate = FeatureGate(enabled: true);

        var exit = new StubExitService { Page = Page(balanceSats: 900_000) };
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, unilateralExit: exit);

        var model = await RenderExit(h);

        Assert.True(model.DisclosureAcknowledged);
        Assert.Null(model.ActiveRecord);

        // Nothing pre-filled from a previous exit, because there is no previous exit to pre-fill from.
        Assert.Equal(0, model.FeeRateSatPerVbyte);
        Assert.Null(model.DestinationAddress);
        Assert.Null(model.LeafCount);
        Assert.Null(model.FundingKeyPath);
    }

    [Fact]
    public async Task A_record_awaiting_funding_carries_the_quote_the_funding_figures_and_the_key_path()
    {
        using var gate = FeatureGate(enabled: true);

        var record = AwaitingFunding();
        var exit = new StubExitService
        {
            // Split funding: 6,000 sats have arrived in total but the biggest single output is 2,500, and the
            // requirement is 4,300. The page has to be able to say "not enough" off the largest while still
            // reporting the total honestly, which is why both numbers travel.
            Page = Page(
                activeRecord: record,
                history: [record],
                fundingReceivedSat: 6_000,
                fundingLargestOutputSat: 2_500,
                leafCount: 2,
                fundingKeyPath: "m/84'/1'/4607060'/0/3")
        };

        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, unilateralExit: exit);

        var model = await RenderExit(h);

        Assert.Same(record, model.ActiveRecord);
        Assert.Equal(6_000, model.FundingReceivedSat);
        Assert.Equal(2_500, model.FundingLargestOutputSat);
        Assert.Equal(2, model.LeafCount);
        Assert.Equal("m/84'/1'/4607060'/0/3", model.FundingKeyPath);
        Assert.Equal(record.FeeRateSatPerVbyte, model.FeeRateSatPerVbyte);
        Assert.Equal(record.DestinationAddress, model.DestinationAddress);
        Assert.Empty(model.Transactions);
        Assert.False(model.TransactionsUnreadable);
        Assert.Single(model.History);
    }

    [Fact]
    public async Task An_unreachable_explorer_reaches_the_page_as_unknown_rather_than_as_zero()
    {
        using var gate = FeatureGate(enabled: true);

        var record = AwaitingFunding();
        var exit = new StubExitService
        {
            // Null rather than zero, on both figures. A merchant who read "unknown" as "my funding has not
            // arrived" would send it twice, and the second send would not combine with the first.
            Page = Page(activeRecord: record, fundingReceivedSat: null, fundingLargestOutputSat: null)
        };

        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, unilateralExit: exit);

        var model = await RenderExit(h);

        Assert.Null(model.FundingReceivedSat);
        Assert.Null(model.FundingLargestOutputSat);
    }

    [Fact]
    public async Task The_explorer_input_shows_what_is_stored_and_the_page_knows_its_network()
    {
        using var gate = FeatureGate(enabled: true);

        var exit = new StubExitService { Page = Page() };
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, unilateralExit: exit);
        h.Settings.Settings[Store]!.UnilateralExit.EsploraApiUrl = "http://localhost:3002/api";

        var model = await RenderExit(h);

        // Pre-filled on purpose: posting that form empty is how the override is cleared, so an input that
        // rendered blank while one was set would delete it the first time somebody pressed Save.
        Assert.Equal("http://localhost:3002/api", model.EsploraApiUrl);

        // Regtest, which is the case where the explorer is not a preference but a prerequisite. The name is
        // taken from NBitcoin rather than spelled out, because it is the copy the page prints and its casing is
        // NBitcoin's to choose.
        Assert.False(model.IsMainnet);
        Assert.Equal(NBitcoin.Network.RegTest.ChainName.ToString(), model.NetworkName);
    }

    [Fact]
    public async Task On_mainnet_the_page_says_so_and_starts_with_no_override()
    {
        using var gate = FeatureGate(enabled: true);

        var exit = new StubExitService { Page = Page() };
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, mainnet: true, unilateralExit: exit);

        var model = await RenderExit(h);

        Assert.True(model.IsMainnet);
        Assert.Null(model.EsploraApiUrl);
    }

    #endregion

    #region The built transaction set

    [Fact]
    public async Task A_built_record_reaches_the_page_as_transactions_to_broadcast()
    {
        using var gate = FeatureGate(enabled: true);

        // No JSON anywhere in this test. The service deserialises the record's column and hands over typed
        // transactions; the controller's only job is to carry them across without inventing an empty list.
        var record = Built();
        var exit = new StubExitService
        {
            Page = Page(activeRecord: record, history: [record], transactions: SignedExit())
        };

        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, unilateralExit: exit);

        var model = await RenderExit(h);

        Assert.False(model.TransactionsUnreadable);
        Assert.Equal(2, model.Transactions.Count);

        var fanout = model.Transactions[0];
        Assert.Equal(SparkExitTxKind.Fanout, fanout.Kind);
        Assert.Equal("aa11", fanout.Txid);
        Assert.Null(fanout.CpfpTxHex);
        Assert.False(fanout.RequiresPackageBroadcast);
        Assert.Empty(fanout.DependsOn);

        var node = model.Transactions[1];
        Assert.Equal(SparkExitTxKind.TreeNode, node.Kind);
        Assert.Equal("node-1", node.NodeId);
        Assert.True(node.RequiresPackageBroadcast);
        Assert.Equal("cpfphex", node.CpfpTxHex);
        Assert.Equal(144u, node.CsvTimelockBlocks);
        Assert.Equal(["aa11"], node.DependsOn);
        Assert.Equal(SparkExitTxStatus.Unconfirmed, node.Status);
    }

    [Fact]
    public async Task An_unreadable_transaction_column_is_carried_through_rather_than_smoothed_over()
    {
        using var gate = FeatureGate(enabled: true);

        var record = Built();
        var exit = new StubExitService
        {
            // What the service reports when the column will not parse or comes back structurally broken: no
            // transactions, and a flag saying that is not the same as none.
            Page = Page(activeRecord: record, transactions: null, transactionsUnreadable: true)
        };

        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, unilateralExit: exit);

        var model = await RenderExit(h);

        // The page renders "the log has the detail, build again" off this flag. An empty transaction list with
        // the flag clear would tell the merchant the opposite of the truth.
        Assert.True(model.TransactionsUnreadable);
        Assert.Empty(model.Transactions);
    }

    [Fact]
    public async Task A_built_record_with_no_transactions_is_distinguishable_from_an_unreadable_one()
    {
        using var gate = FeatureGate(enabled: true);

        var record = Built();
        var exit = new StubExitService
        {
            Page = Page(activeRecord: record, transactions: [], transactionsUnreadable: false)
        };

        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, unilateralExit: exit);

        var model = await RenderExit(h);

        Assert.Empty(model.Transactions);
        Assert.False(model.TransactionsUnreadable);
    }

    #endregion

    #region What the template does with it

    [Fact]
    public void A_packaged_transaction_is_shown_as_a_submitpackage_command()
    {
        // The wording is load-bearing: a tree transaction pays no fee of its own, so an operator who pastes
        // sendrawtransaction gets a rejection and no explanation, and the page is the only place that
        // distinction is made.
        var view = ExitTemplate();

        Assert.Contains("bitcoin-cli submitpackage", view);
        Assert.Contains("CpfpTxHex is { } cpfpTxHex", view);
        Assert.Contains("sendrawtransaction", view);
        Assert.Contains("SparkExitTransactions", view);
        Assert.Contains("SparkExitFundingAddress", view);
    }

    [Fact]
    public void Signed_hex_is_behind_the_permission_that_built_it()
    {
        // The hex is enough on its own to move this store's balance to the destination already baked into it,
        // so it belongs to whoever may modify the store, not to whoever may read the page. Asserted
        // structurally — the wrapper has to open immediately before the table — because a `permission`
        // attribute somewhere else in the file would satisfy a plain Contains while leaving the hex public.
        var view = ExitTemplate();

        Assert.Matches(
            new Regex(
                "<div permission=\"@Policies\\.CanModifyStoreSettings\">\\s*"
                + "<table class=\"table\" id=\"SparkExitTransactions\""),
            view);

        // And view-only access gets told why the table is missing rather than being shown an empty page.
        Assert.Contains("id=\"SparkExitTransactionsRestricted\"", view);
        Assert.Contains("not-permission=\"@Policies.CanModifyStoreSettings\"", view);

        // Every id that carries hex or a command lives after the wrapper opens and before it closes.
        var wrapper = view.IndexOf(
            "<div permission=\"@Policies.CanModifyStoreSettings\">", StringComparison.Ordinal);
        Assert.InRange(wrapper, 0, view.Length);
        foreach (var carrier in new[] { "SparkExitPackage@step", "SparkExitTxHex@step", "SparkExitCpfpHex@step" })
            Assert.True(view.IndexOf(carrier, StringComparison.Ordinal) > wrapper, carrier);
    }

    [Fact]
    public void The_funding_panel_judges_the_shortfall_by_the_largest_output_not_the_total()
    {
        // Five outputs adding up to the requirement fund nothing: the fee-bumping transaction spends one
        // outpoint. A page that compared the sum would tell a merchant they were funded while the build
        // refused, and the merchant would conclude the plugin was broken.
        var view = ExitTemplate();

        Assert.Contains("id=\"SparkExitFundingLargest\"", view);
        Assert.Contains("id=\"SparkExitFundingReceived\"", view);
        Assert.Contains("Model.FundingLargestOutputSat is { } largest", view);
        Assert.Contains("largest < record.SingleUtxoFundingSat", view);

        // The total is reported but must not be what a shortfall is judged by.
        Assert.DoesNotContain("received < record.SingleUtxoFundingSat", view);

        // And the copy has to say the single-output rule out loud, in both directions.
        Assert.Contains("one single output", view);
        Assert.Contains("as one new", view);
    }

    [Fact]
    public void No_state_of_the_page_is_a_dead_end()
    {
        // The unreadable branch tells the operator not to abandon the exit. If the only control it rendered
        // were the abandon button, the page would be telling them to do nothing and offering them one thing —
        // and they would press it.
        var view = ExitTemplate();

        Assert.Contains("id=\"SparkExitRebuildUnreadable\"", view);
        Assert.Contains("id=\"SparkExitRebuildEmpty\"", view);
        Assert.Contains("id=\"SparkExitRebuild\"", view);
        Assert.Contains("id=\"SparkExitAbandon\"", view);

        // The ending a successful exit deserves, and the funding key path that makes an abandoned one
        // recoverable by hand.
        Assert.Contains("id=\"SparkExitComplete\"", view);
        Assert.Contains("id=\"SparkExitFundingKeyPath\"", view);
        Assert.Contains("id=\"SparkExitExplorerForm\"", view);
    }

    [Fact]
    public void The_fee_input_takes_its_bounds_from_the_service()
    {
        // Two numbers typed into a template are two numbers to keep in step, and the one that mattered would
        // be the one nobody edited. The browser's hint and the server's refusal come from the same constants.
        var view = ExitTemplate();

        Assert.Contains("min=\"@SparkUnilateralExitService.MinFeeRateSatPerVbyte\"", view);
        Assert.Contains("max=\"@SparkUnilateralExitService.MaxFeeRateSatPerVbyte\"", view);
        Assert.DoesNotContain("min=\"1\" max=\"500\"", view);
    }

    #endregion

    #region Relaying the service's answer

    [Fact]
    public async Task An_acknowledgement_reports_success_and_returns_to_the_page()
    {
        using var gate = FeatureGate(enabled: true);

        var exit = new StubExitService { Result = new UnilateralExitOpResult(true, null, null) };
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, unilateralExit: exit);

        var result = await h.Mvc.AcknowledgeExit(Store, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(h.Mvc.Exit), redirect.ActionName);
        Assert.Equal(["Acknowledge"], exit.Calls);
        Assert.NotNull(h.Mvc.TempData[WellKnownTempData.SuccessMessage]);
        Assert.Null(h.Mvc.TempData[WellKnownTempData.ErrorMessage]);
    }

    [Fact]
    public async Task A_refused_quote_is_relayed_verbatim_and_returns_to_the_page()
    {
        using var gate = FeatureGate(enabled: true);

        const string refusal = "Nothing is worth exiting at 400 sat/vB.";
        var exit = new StubExitService { Result = new UnilateralExitOpResult(false, refusal, null) };
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, unilateralExit: exit);

        var result = await h.Mvc.QuoteExit(
            Store,
            new SparkExitViewModel { FeeRateSatPerVbyte = 400, DestinationAddress = Destination },
            CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);

        // The service's sentence, unedited. The controller has no opinion to add and no guard of its own to
        // report, so anything else here would be the page inventing a reason.
        Assert.Equal(refusal, h.Mvc.TempData[WellKnownTempData.ErrorMessage]);
        Assert.Null(h.Mvc.TempData[WellKnownTempData.SuccessMessage]);

        // Passed through untouched, including the fee rate the service will refuse: the bounds are its business.
        Assert.Equal(["Quote:400:" + Destination], exit.Calls);
    }

    [Fact]
    public async Task A_failed_build_is_relayed_and_the_record_id_reaches_the_service()
    {
        using var gate = FeatureGate(enabled: true);

        const string refusal = "The funding address holds 900 sats; this exit needs 4,300 in one UTXO.";
        var exit = new StubExitService { Result = new UnilateralExitOpResult(false, refusal, null) };
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, unilateralExit: exit);

        var result = await h.Mvc.BuildExit(Store, "record-7", CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(refusal, h.Mvc.TempData[WellKnownTempData.ErrorMessage]);
        Assert.Equal(["Build:record-7"], exit.Calls);
    }

    [Fact]
    public async Task Abandoning_says_out_loud_that_it_cancels_nothing()
    {
        using var gate = FeatureGate(enabled: true);

        var exit = new StubExitService { Result = new UnilateralExitOpResult(true, null, null) };
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, unilateralExit: exit);

        var result = await h.Mvc.AbandonExit(Store, "record-7", CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(["Abandon:record-7"], exit.Calls);

        // The one piece of copy worth pinning: "abandon" is the word a merchant reaches for when they want to
        // undo a broadcast, and this does not do that.
        var message = Assert.IsType<string>(h.Mvc.TempData[WellKnownTempData.SuccessMessage]);
        Assert.Contains("already broadcast is unaffected", message);
    }

    [Fact]
    public async Task Marking_an_exit_completed_says_it_moved_nothing()
    {
        using var gate = FeatureGate(enabled: true);

        var exit = new StubExitService { Result = new UnilateralExitOpResult(true, null, null) };
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, unilateralExit: exit);

        var result = await h.Mvc.CompleteExit(Store, "record-7", CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(h.Mvc.Exit), redirect.ActionName);
        Assert.Equal(["Complete:record-7"], exit.Calls);

        // Nothing here watches the chain, so the banner must not imply the plugin verified anything, and it has
        // to say that recording a completion is not itself an action on the money.
        var message = Assert.IsType<string>(h.Mvc.TempData[WellKnownTempData.SuccessMessage]);
        Assert.Contains("Nothing was broadcast or moved", message);
        Assert.Contains("your confirmation", message);
    }

    [Fact]
    public async Task A_refused_completion_is_relayed_like_any_other_refusal()
    {
        using var gate = FeatureGate(enabled: true);

        const string refusal = "This exit has not been built yet, so there is nothing to mark completed.";
        var exit = new StubExitService { Result = new UnilateralExitOpResult(false, refusal, null) };
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, unilateralExit: exit);

        await h.Mvc.CompleteExit(Store, "record-7", CancellationToken.None);

        Assert.Equal(refusal, h.Mvc.TempData[WellKnownTempData.ErrorMessage]);
        Assert.Null(h.Mvc.TempData[WellKnownTempData.SuccessMessage]);
    }

    [Fact]
    public async Task An_explorer_url_reaches_the_service_exactly_as_typed()
    {
        using var gate = FeatureGate(enabled: true);

        var exit = new StubExitService { Result = new UnilateralExitOpResult(true, null, null) };
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, unilateralExit: exit);

        var result = await h.Mvc.SetExitExplorer(Store, " http://localhost:3002/api ", CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);

        // Untrimmed and unexamined: whether that string is an acceptable URL is the service's judgement, and a
        // controller that pre-validated it would be a second opinion to keep in step with the first.
        Assert.Equal(["Explorer: http://localhost:3002/api "], exit.Calls);
        Assert.NotNull(h.Mvc.TempData[WellKnownTempData.SuccessMessage]);
    }

    [Fact]
    public async Task Clearing_the_explorer_says_what_clearing_it_costs()
    {
        using var gate = FeatureGate(enabled: true);

        var exit = new StubExitService { Result = new UnilateralExitOpResult(true, null, null) };
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, unilateralExit: exit);

        await h.Mvc.SetExitExplorer(Store, "   ", CancellationToken.None);

        Assert.Equal(["Explorer:   "], exit.Calls);

        // Off mainnet clearing the override leaves funding discovery with nothing to ask, and the banner is the
        // only place a merchant finds that out.
        var message = Assert.IsType<string>(h.Mvc.TempData[WellKnownTempData.SuccessMessage]);
        Assert.Contains("cleared", message);
    }

    [Fact]
    public async Task A_refused_explorer_url_is_relayed_verbatim()
    {
        using var gate = FeatureGate(enabled: true);

        const string refusal = "That is not an absolute http or https URL.";
        var exit = new StubExitService { Result = new UnilateralExitOpResult(false, refusal, null) };
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, unilateralExit: exit);

        await h.Mvc.SetExitExplorer(Store, "not-a-url", CancellationToken.None);

        Assert.Equal(refusal, h.Mvc.TempData[WellKnownTempData.ErrorMessage]);
        Assert.Null(h.Mvc.TempData[WellKnownTempData.SuccessMessage]);
    }

    [Fact]
    public async Task A_failure_with_no_reason_still_produces_a_banner()
    {
        using var gate = FeatureGate(enabled: true);

        // A service that fails without saying why is a bug, but a silent redirect looks exactly like success —
        // so the controller substitutes a sentence rather than leaving the merchant to guess.
        var exit = new StubExitService { Result = new UnilateralExitOpResult(false, null, null) };
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, unilateralExit: exit);

        await h.Mvc.AbandonExit(Store, "record-7", CancellationToken.None);

        Assert.NotNull(h.Mvc.TempData[WellKnownTempData.ErrorMessage]);
        Assert.Null(h.Mvc.TempData[WellKnownTempData.SuccessMessage]);
    }

    #endregion

    #region Fixtures

    /// <summary>The page's view model from one GET, with the boilerplate of unwrapping it out of the way.</summary>
    private static async Task<SparkExitViewModel> RenderExit(SparkSurfaceHarness h)
    {
        var view = Assert.IsType<ViewResult>(await h.Mvc.Exit(Store, CancellationToken.None));
        return Assert.IsType<SparkExitViewModel>(view.Model);
    }

    /// <summary>
    /// One service read, with every field named.
    /// </summary>
    /// <remarks>
    /// The page data has eleven members and most tests care about two of them. Named optional parameters keep
    /// each test's fixture to the fields it is actually about, and — unlike a positional constructor call —
    /// a field added to the record does not silently shift what an existing test was asserting.
    /// </remarks>
    private static UnilateralExitPageData Page(
        bool walletRunning = true,
        bool disclosureAcknowledged = true,
        long balanceSats = 0,
        UnilateralExitRecord? activeRecord = null,
        IReadOnlyList<UnilateralExitRecord>? history = null,
        long? fundingReceivedSat = null,
        long? fundingLargestOutputSat = null,
        int? leafCount = null,
        string? fundingKeyPath = null,
        IReadOnlyList<SparkExitTransaction>? transactions = null,
        bool transactionsUnreadable = false) =>
        new(
            walletRunning,
            disclosureAcknowledged,
            balanceSats,
            activeRecord,
            history ?? [],
            fundingReceivedSat,
            fundingLargestOutputSat,
            leafCount,
            fundingKeyPath,
            transactions,
            transactionsUnreadable);

    private static UnilateralExitRecord AwaitingFunding() => new()
    {
        Id = "record-7",
        StoreId = Store,
        Status = UnilateralExitStatus.AwaitingFunding,
        CreatedUtc = DateTimeOffset.UnixEpoch,
        UpdatedUtc = DateTimeOffset.UnixEpoch,
        DestinationAddress = Destination,
        FeeRateSatPerVbyte = 12,
        RecoverableValueSat = 400_000,
        TotalFeeSat = 9_000,
        SingleUtxoFundingSat = 4_300,
        FundingAddress = "bcrt1qfundingaddressfundingaddressfundingxyz"
    };

    private static UnilateralExitRecord Built()
    {
        var record = AwaitingFunding();
        record.Status = UnilateralExitStatus.Built;
        return record;
    }

    /// <summary>
    /// A minimal but shaped-like-the-real-thing exit: a fan-out that broadcasts alone, and one tree node that
    /// only works as a package with its CPFP child.
    /// </summary>
    private static SparkExitTransaction[] SignedExit() =>
    [
        new(SparkExitTxKind.Fanout, null, "aa11", "fanouthex", null, null, [], SparkExitTxStatus.Unconfirmed),
        new(SparkExitTxKind.TreeNode, "node-1", "bb22", "nodehex", "cpfphex", 144u, ["aa11"],
            SparkExitTxStatus.Unconfirmed)
    ];

    /// <summary>
    /// Sets the feature switch for one test and puts back whatever was there.
    /// </summary>
    /// <remarks>
    /// The variable is process-wide, and <see cref="Constants.UnilateralExitEnabled"/> is a property precisely so
    /// that this works — a cached <c>static readonly</c> would freeze whichever value the first test to load the
    /// class happened to see. Restoring the previous value rather than clearing it keeps a developer who exported
    /// the variable in their own shell from watching later tests behave differently.
    /// </remarks>
    private static IDisposable FeatureGate(bool enabled) => new EnvironmentSwitch(Gate, enabled ? "1" : null);

    private sealed class EnvironmentSwitch : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentSwitch(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }

    /// <summary>
    /// The exit service the page talks to: whatever <see cref="Page"/> says, whatever <see cref="Result"/> says,
    /// and a note of every call so "the controller decided nothing" is falsifiable.
    /// </summary>
    /// <remarks>
    /// Hand-rolled rather than mocked, matching the suite's other fakes, and every write returns the same result
    /// on purpose: these tests are about relaying and gating, so a per-method result table would be six places
    /// to keep in step for no assertion's benefit.
    /// </remarks>
    private sealed class StubExitService : ISparkUnilateralExitService
    {
        public UnilateralExitPageData Page { get; set; } =
            new(WalletRunning: true, DisclosureAcknowledged: false, BalanceSats: 0,
                ActiveRecord: null, History: [], FundingReceivedSat: null, FundingLargestOutputSat: null,
                LeafCount: null, FundingKeyPath: null, Transactions: null, TransactionsUnreadable: false);

        public UnilateralExitOpResult Result { get; set; } = new(true, null, null);

        /// <summary>Every call, in order, with the arguments that came off the form.</summary>
        public List<string> Calls { get; } = [];

        public Task<UnilateralExitPageData> ReadAsync(string storeId, CancellationToken cancellationToken = default)
        {
            Calls.Add("Read");
            return Task.FromResult(Page);
        }

        public Task<UnilateralExitOpResult> AcknowledgeDisclosureAsync(
            string storeId, CancellationToken cancellationToken = default)
        {
            Calls.Add("Acknowledge");
            return Task.FromResult(Result);
        }

        public Task<UnilateralExitOpResult> QuoteAsync(
            string storeId,
            long feeRateSatPerVbyte,
            string destinationAddress,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"Quote:{feeRateSatPerVbyte}:{destinationAddress}");
            return Task.FromResult(Result);
        }

        public Task<UnilateralExitOpResult> BuildAsync(
            string storeId, string recordId, CancellationToken cancellationToken = default)
        {
            Calls.Add($"Build:{recordId}");
            return Task.FromResult(Result);
        }

        public Task<UnilateralExitOpResult> AbandonAsync(
            string storeId, string recordId, CancellationToken cancellationToken = default)
        {
            Calls.Add($"Abandon:{recordId}");
            return Task.FromResult(Result);
        }

        public Task<UnilateralExitOpResult> MarkCompletedAsync(
            string storeId, string recordId, CancellationToken cancellationToken = default)
        {
            Calls.Add($"Complete:{recordId}");
            return Task.FromResult(Result);
        }

        public Task<UnilateralExitOpResult> SetExplorerUrlAsync(
            string storeId, string? esploraApiUrl, CancellationToken cancellationToken = default)
        {
            Calls.Add($"Explorer:{esploraApiUrl ?? "(null)"}");
            return Task.FromResult(Result);
        }
    }

    /// <summary>The exit template's own text, for the assertions no unrendered view model can carry.</summary>
    private static string ExitTemplate() => File.ReadAllText(
        Path.Combine(RepositoryRoot, "BTCPayServer.Plugins.Flint", "Views", "Spark", "Exit.cshtml"));

    /// <summary>
    /// Repository root, from this file's compile-time path — the same trick
    /// <see cref="ViewComponentCompatibilityTests"/> uses, and for the same reason: the output directory's depth
    /// below the project is an MSBuild detail.
    /// </summary>
    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(ThisFile(), "..", ".."));

    private static string ThisFile([CallerFilePath] string path = "") => path;

    #endregion
}
