using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The unilateral-exit service: the guards in front of a signed exit, and what gets persisted when one is built.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing this service does can be undone by the plugin, and nothing it does can be redone by the SDK.</b>
/// The transactions come back signed and unbroadcast, they exist only in the record's <c>TransactionsJson</c>, and
/// the on-chain fees have to be paid up front out of a funding UTXO the operator sends by hand. So the tests here
/// are almost entirely about refusals — the ones that stop an exit that costs more than it recovers, that stop a
/// second exit committing the same leaves twice, and that stop "the explorer did not answer" reading as "no
/// funding has arrived".
/// </para>
/// <para>
/// <b>Why the whole class is one non-parallel collection.</b> The feature gate is an environment variable, which
/// is process-global state: a test that toggles it while another class reads it would make both flaky in a way
/// that reproduces once a week. Every test here therefore owns the variable for its duration through
/// <see cref="Harness"/>, and the collection is serialised against the rest of the suite.
/// </para>
/// </remarks>
[Collection(UnilateralExitTestCollection.Name)]
public class SparkUnilateralExitServiceTests
{
    private const string StoreId = "store-1";

    /// <summary>The BIP39 test vector, so the derived funding address below is a reproducible pin.</summary>
    private const string Mnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    /// <summary>
    /// <c>m/84'/1'/4607060'/0/0</c> of <see cref="Mnemonic"/> on regtest.
    /// </summary>
    /// <remarks>
    /// Hard-coded rather than re-derived in the test, which would only assert that NBitcoin agrees with itself.
    /// Pinned, because changing the derivation path silently is how an operator ends up funding an address the
    /// plugin can no longer spend from — and the funding key's whole reason for living at an absurd account index
    /// is that it must never move. See <c>Constants.UnilateralExitFundingAccount</c>.
    /// </remarks>
    private const string FundingAddress = "bcrt1qluxw544vs8huwqyxvwqx4x75x5v7mgfkamt2pd";

    private const string Destination = "bcrt1qtxwcjjvf4ny9wsw9emgnpazey2vde3xhnyqpw0";
    private const string MainnetDestination = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";

    private const string FundingTxid =
        "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #region The feature gate

    /// <summary>
    /// With the gate off the service behaves as if the feature does not exist, on every method.
    /// </summary>
    /// <remarks>
    /// The controller's 404 is a courtesy and not the enforcement: a Greenfield endpoint, a scheduled task or a
    /// second controller added later would each have to remember the gate, and this is the one place that cannot
    /// forget it. The read reports an absent feature rather than the store's real acknowledgement, so nothing
    /// leaks through a surface the gate is supposed to have closed.
    /// </remarks>
    [Fact]
    public async Task Every_entry_point_behaves_as_if_the_feature_does_not_exist_when_the_gate_is_off()
    {
        using var harness = Harness.Create(featureEnabled: false);
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));

        var page = await harness.Service.ReadAsync(StoreId, Ct);
        Assert.False(page.WalletRunning);
        Assert.False(page.DisclosureAcknowledged);
        Assert.Equal(0, page.BalanceSats);
        Assert.Null(page.ActiveRecord);
        Assert.Empty(page.History);
        Assert.Null(page.FundingReceivedSat);
        Assert.Null(page.FundingLargestOutputSat);
        Assert.Null(page.LeafCount);
        Assert.Null(page.FundingKeyPath);
        Assert.Null(page.Transactions);
        Assert.False(page.TransactionsUnreadable);

        foreach (var attempt in new[]
                 {
                     await harness.Service.AcknowledgeDisclosureAsync(StoreId, Ct),
                     await harness.Service.SetExplorerUrlAsync(StoreId, "https://explorer.test/api", Ct),
                     await harness.Service.QuoteAsync(StoreId, 10, Destination, Ct),
                     await harness.Service.BuildAsync(StoreId, "whatever", Ct),
                     await harness.Service.MarkCompletedAsync(StoreId, "whatever", Ct),
                     await harness.Service.AbandonAsync(StoreId, "whatever", Ct)
                 })
        {
            Assert.False(attempt.Success);
            Assert.Equal(SparkUnilateralExitService.FeatureDisabled, attempt.Error);
        }

        // And nothing reached the wallet or the database on the way to those refusals.
        Assert.Empty(harness.Sdk.ExitQuoteCalls);
        Assert.Empty(harness.Records.Records);
        Assert.Empty(harness.Settings.Writes);
    }

    #endregion

    #region The disclosure gate

    /// <summary>
    /// Quoting is refused until the acknowledgement is <em>stored</em>, and so is building.
    /// </summary>
    /// <remarks>
    /// Both, deliberately. The build is the call that produces signed transactions, and it is reachable directly
    /// from its own POST — so a gate enforced only on the quote would be a gate with a documented bypass for
    /// anybody holding an exit id.
    /// </remarks>
    [Fact]
    public async Task Quoting_and_building_are_refused_until_the_disclosure_is_stored()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: false);
        harness.WithLeaves(("leaf-a", 500_000));

        var quote = await harness.Service.QuoteAsync(StoreId, 10, Destination, Ct);

        Assert.False(quote.Success);
        Assert.Equal(SparkUnilateralExitService.DisclosureRequired, quote.Error);
        Assert.Empty(harness.Sdk.ExitQuoteCalls);
        Assert.Empty(harness.Records.Records);

        // A record that exists from before the acknowledgement was revoked cannot be built either.
        var record = harness.Seed();
        var build = await harness.Service.BuildAsync(StoreId, record.Id, Ct);

        Assert.False(build.Success);
        Assert.Equal(SparkUnilateralExitService.DisclosureRequired, build.Error);
        Assert.Empty(harness.Sdk.ExitBuildCalls);
    }

    /// <summary>The acknowledgement is stored in the store's settings, and is idempotent.</summary>
    [Fact]
    public async Task Acknowledging_the_disclosure_stores_it_once()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: false);

        var first = await harness.Service.AcknowledgeDisclosureAsync(StoreId, Ct);

        Assert.True(first.Success);
        Assert.True(harness.Settings.Settings[StoreId]!.UnilateralExit.DisclosureAcknowledged);
        var writes = harness.Settings.Writes.Count;

        var second = await harness.Service.AcknowledgeDisclosureAsync(StoreId, Ct);

        Assert.True(second.Success);
        // No second write: storing settings tears down and reconnects the store's wallet, which is not something
        // to do on a button press that changes nothing.
        Assert.Equal(writes, harness.Settings.Writes.Count);
    }

    /// <summary>An unconfigured store is refused rather than provisioned by a side effect.</summary>
    [Fact]
    public async Task A_store_without_Flint_is_refused()
    {
        using var harness = Harness.Create();

        var ack = await harness.Service.AcknowledgeDisclosureAsync(StoreId, Ct);
        var quote = await harness.Service.QuoteAsync(StoreId, 10, Destination, Ct);

        Assert.Equal(SparkUnilateralExitService.NotConfigured, ack.Error);
        Assert.Equal(SparkUnilateralExitService.NotConfigured, quote.Error);
        Assert.Empty(harness.Settings.Writes);
    }

    #endregion

    #region Quoting

    /// <summary>
    /// The fee rate has to be inside the documented band, however it arrived.
    /// </summary>
    /// <remarks>
    /// The rate multiplies across every transaction in the tree, so a mistyped one is not one expensive
    /// transaction — it is an expensive exit and a funding requirement to match. Zero and negative are checked as
    /// well as absurd, because the value is cast to an unsigned rate on the way to the SDK and a negative would
    /// arrive there as an astronomical one.
    /// </remarks>
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    [InlineData(501L)]
    [InlineData(long.MaxValue)]
    public async Task A_fee_rate_outside_the_band_is_refused(long feeRate)
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));

        var result = await harness.Service.QuoteAsync(StoreId, feeRate, Destination, Ct);

        Assert.False(result.Success);
        Assert.Contains("between", result.Error);
        Assert.Empty(harness.Sdk.ExitQuoteCalls);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(500L)]
    public async Task The_band_ends_are_accepted(long feeRate)
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));

        var result = await harness.Service.QuoteAsync(StoreId, feeRate, Destination, Ct);

        Assert.True(result.Success, result.Error);
        Assert.Equal((ulong)feeRate, Assert.Single(harness.Sdk.ExitQuoteCalls).FeeRateSatPerVbyte);
    }

    /// <summary>
    /// The destination is parsed for this server's network, and this is the last place it can be.
    /// </summary>
    /// <remarks>
    /// A mainnet-shaped address is a perfectly valid string on regtest and vice versa, and the destination is
    /// baked into the signed sweep — so a wrong-network address that got past here would produce a transaction
    /// that can never be broadcast, discovered days into a multi-level exit.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-address")]
    [InlineData(MainnetDestination)]
    public async Task A_destination_that_is_not_valid_here_is_refused(string destination)
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));

        var result = await harness.Service.QuoteAsync(StoreId, 10, destination, Ct);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Empty(harness.Sdk.ExitQuoteCalls);
    }

    /// <summary>
    /// An empty automatic selection is reported as "nothing worth exiting", not as a failure.
    /// </summary>
    /// <remarks>
    /// The SDK returns no leaves whenever none of them clears the requested fee rate. That is the normal answer
    /// for a small balance at a busy fee market, and a merchant told "the exit failed" would retry it for ever.
    /// </remarks>
    [Fact]
    public async Task An_empty_selection_says_there_is_nothing_worth_exiting()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);

        var result = await harness.Service.QuoteAsync(StoreId, 10, Destination, Ct);

        Assert.False(result.Success);
        Assert.Equal(SparkUnilateralExitService.NothingWorthExiting, result.Error);
        // Nothing recorded: there is no exit here to fund or abandon later.
        Assert.Empty(harness.Records.Records);
    }

    /// <summary>An exit that costs more than it recovers is refused, and nothing is recorded.</summary>
    [Fact]
    public async Task A_quote_whose_fee_exceeds_what_it_recovers_is_refused()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.Sdk.ExitTotalFeeSat = 4_000;
        harness.WithLeaves(("leaf-a", 3_500));

        var result = await harness.Service.QuoteAsync(StoreId, 10, Destination, Ct);

        Assert.False(result.Success);
        Assert.Contains("more than it recovers", result.Error);
        Assert.Empty(harness.Records.Records);
    }

    /// <summary>
    /// A successful quote pins the leaf set and issues the funding address.
    /// </summary>
    /// <remarks>
    /// The leaf ids are the reason the row is durable at all: the build re-quotes <em>these</em> leaves, so the
    /// operator cannot fund one exit and build another after the wallet's tree has moved under them.
    /// </remarks>
    [Fact]
    public async Task A_successful_quote_persists_the_leaf_ids_the_funding_address_and_the_figures()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 300_000), ("leaf-b", 200_000));

        var result = await harness.Service.QuoteAsync(StoreId, 12, Destination, Ct);

        Assert.True(result.Success, result.Error);
        var record = Assert.IsType<UnilateralExitRecord>(result.Record);

        Assert.Equal(UnilateralExitStatus.AwaitingFunding, record.Status);
        Assert.Equal(StoreId, record.StoreId);
        Assert.Equal(Destination, record.DestinationAddress);
        Assert.Equal(12, record.FeeRateSatPerVbyte);
        Assert.Equal(500_000, record.RecoverableValueSat);
        Assert.Equal(harness.Sdk.ExitTotalFeeSat, record.TotalFeeSat);
        Assert.Equal(harness.Sdk.ExitSingleUtxoFundingSat, record.SingleUtxoFundingSat);
        Assert.Equal(FundingAddress, record.FundingAddress);
        Assert.Equal(0, record.FundingKeyIndex);
        Assert.Equal(harness.Now, record.CreatedUtc);
        Assert.Null(record.TransactionsJson);
        Assert.Null(record.FundingUtxosJson);

        Assert.Equal(
            ["leaf-a", "leaf-b"],
            JsonSerializer.Deserialize<string[]>(record.LeafIdsJson)!);

        // Quoted automatically: the first quote is the SDK's choice of what is worth exiting.
        Assert.Null(Assert.Single(harness.Sdk.ExitQuoteCalls).LeafIds);

        // And the row is in storage, not only in the result.
        Assert.Equal(record.Id, harness.Records.Single()!.Id);
    }

    /// <summary>
    /// Each exit gets its own funding address, so one exit's leftovers can never fund the next.
    /// </summary>
    /// <remarks>
    /// <b>A fixed address per store would be a trap rather than a saving.</b> Sats left behind by an abandoned
    /// exit sit on the address the next exit tells the operator to fund, so the next build selects a leftover —
    /// wrong size at best, and at worst large enough to satisfy the new requirement, which makes a build succeed
    /// against funding nobody just sent. The index is allocated one past every index the store has ever issued,
    /// terminal exits included, so it is never reused either.
    /// </remarks>
    [Fact]
    public async Task Each_exit_gets_its_own_funding_address()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));

        var first = await harness.Service.QuoteAsync(StoreId, 10, Destination, Ct);
        Assert.True(first.Success, first.Error);
        Assert.Equal(FundingAddress, first.Record!.FundingAddress);
        Assert.Equal(0, first.Record.FundingKeyIndex);

        Assert.True((await harness.Service.AbandonAsync(StoreId, first.Record.Id, Ct)).Success);

        var second = await harness.Service.QuoteAsync(StoreId, 10, Destination, Ct);
        Assert.True(second.Success, second.Error);
        Assert.Equal(1, second.Record!.FundingKeyIndex);
        Assert.NotEqual(first.Record.FundingAddress, second.Record.FundingAddress);

        // And the address is the one the pinned path derives, which is what makes stranded funding recoverable.
        Assert.True(SparkExitFundingKey.TryDerive(Mnemonic, Network.RegTest, 1, out var key, out _));
        using (key)
        {
            Assert.Equal(key!.Address, second.Record.FundingAddress);
        }
    }

    /// <summary>
    /// A store with an exit in flight cannot quote a second one, and is shown the first.
    /// </summary>
    /// <remarks>
    /// Both non-terminal statuses hold the store. Two exits would compete for the same leaves, and the SDK
    /// reports that as a conflict only <em>after</em> one of them has committed — too late to be a useful
    /// refusal.
    /// </remarks>
    [Theory]
    [InlineData(UnilateralExitStatus.AwaitingFunding)]
    [InlineData(UnilateralExitStatus.Built)]
    public async Task A_store_with_an_exit_in_flight_cannot_quote_another(UnilateralExitStatus status)
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        var existing = harness.Seed(status: status);

        var result = await harness.Service.QuoteAsync(StoreId, 10, Destination, Ct);

        Assert.False(result.Success);
        Assert.Contains("already has an exit in progress", result.Error);
        Assert.Equal(existing.Id, result.Record?.Id);
        Assert.Empty(harness.Sdk.ExitQuoteCalls);
    }

    /// <summary>
    /// One exit operation at a time per store, whatever calls arrive.
    /// </summary>
    /// <remarks>
    /// Driven from inside the SDK's own prepare rather than by racing two threads, which is what makes it a test
    /// rather than a coin flip: the second call happens while the first is provably mid-flight.
    /// </remarks>
    [Fact]
    public async Task A_second_operation_is_refused_while_one_is_in_flight()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));

        UnilateralExitOpResult? reentrant = null;
        harness.Sdk.WhenExitQuoted = () =>
        {
            // Everything the nested call touches answers synchronously, so this completes before returning.
            var nested = harness.Service.QuoteAsync(StoreId, 10, Destination, CancellationToken.None);
            Assert.True(nested.IsCompleted, "the re-entrant quote should not have reached anything awaitable");
            reentrant = nested.Result;
        };

        var outer = await harness.Service.QuoteAsync(StoreId, 10, Destination, Ct);

        Assert.True(outer.Success, outer.Error);
        Assert.NotNull(reentrant);
        Assert.False(reentrant.Success);
        Assert.Equal(SparkUnilateralExitService.OperationInFlight, reentrant.Error);
        // Exactly one record: the re-entrant attempt created nothing.
        Assert.NotNull(harness.Records.Single());
    }

    /// <summary>A seed this server can no longer decrypt is refused before the SDK is asked anything.</summary>
    [Fact]
    public async Task A_store_whose_seed_cannot_be_read_is_refused()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.Settings.Settings[StoreId]!.ProtectedMnemonic = "not something this keyring can unprotect";
        harness.WithLeaves(("leaf-a", 500_000));

        var result = await harness.Service.QuoteAsync(StoreId, 10, Destination, Ct);

        Assert.False(result.Success);
        Assert.Contains("recovery phrase", result.Error);
        Assert.Empty(harness.Sdk.ExitQuoteCalls);
        Assert.Empty(harness.Records.Records);
    }

    /// <summary>A store with no running wallet cannot quote.</summary>
    [Fact]
    public async Task A_stopped_wallet_cannot_quote()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true, walletRunning: false);

        var result = await harness.Service.QuoteAsync(StoreId, 10, Destination, Ct);

        Assert.False(result.Success);
        Assert.Equal(SparkUnilateralExitService.WalletNotRunning, result.Error);
    }

    #endregion

    #region Funding discovery

    /// <summary>
    /// A funding address short of the requirement refuses the build and says so on the record.
    /// </summary>
    /// <remarks>
    /// The status deliberately stays <c>AwaitingFunding</c>: the exit is not broken, it is underfunded, and the
    /// operator's next step is a top-up rather than a new quote. The explanation lives on the row so it is next to
    /// the funding instructions instead of in a log.
    /// </remarks>
    [Fact]
    public async Task An_underfunded_exit_is_refused_and_the_reason_is_recorded()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        var record = harness.Seed(singleUtxoFundingSat: 4_200);
        harness.Explorer(Utxo(1_000));

        var result = await harness.Service.BuildAsync(StoreId, record.Id, Ct);

        Assert.False(result.Success);
        Assert.Contains("4,200", result.Error);

        var stored = harness.Records.Records[record.Id];
        Assert.Equal(UnilateralExitStatus.AwaitingFunding, stored.Status);
        Assert.Equal(result.Error, stored.LastError);
        Assert.Null(stored.TransactionsJson);
        // Nothing was built, so nothing was signed.
        Assert.Empty(harness.Sdk.ExitBuildCalls);
    }

    /// <summary>
    /// Unconfirmed outputs do not count towards the funding requirement.
    /// </summary>
    /// <remarks>
    /// Not conservatism. Every transaction in the exit is a CPFP child of this output, so funding from an
    /// unconfirmed one makes the whole tree a package descending from an unconfirmed parent — and mempool policy
    /// bounds how deep and how large such a package may be. The exit would be rejected as non-relayable somewhere
    /// in the middle, after the fan-out had been broadcast and paid for.
    /// </remarks>
    [Fact]
    public async Task An_unconfirmed_funding_output_does_not_count()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        var record = harness.Seed(singleUtxoFundingSat: 4_200);
        harness.Explorer(Utxo(50_000, confirmed: false));

        var result = await harness.Service.BuildAsync(StoreId, record.Id, Ct);

        Assert.False(result.Success);
        Assert.Contains("no confirmed output", result.Error);
        Assert.Empty(harness.Sdk.ExitBuildCalls);

        // And the page says the same thing: zero confirmed, not fifty thousand.
        var page = await harness.Service.ReadAsync(StoreId, Ct);
        Assert.Equal(0, page.FundingReceivedSat);
    }

    /// <summary>
    /// Several outputs that add up are still not one output that suffices.
    /// </summary>
    /// <remarks>
    /// The SDK spends a single P2WPKH outpoint for CPFP, so a total is not a qualification — and the refusal has
    /// to say that, because "the address holds more than you asked for and the build still refuses" is otherwise
    /// indistinguishable from a bug.
    /// </remarks>
    [Fact]
    public async Task Two_outputs_that_add_up_do_not_fund_an_exit()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        var record = harness.Seed(singleUtxoFundingSat: 4_200);
        harness.Explorer(Utxo(3_000, vout: 0), Utxo(3_000, vout: 1));

        var result = await harness.Service.BuildAsync(StoreId, record.Id, Ct);

        Assert.False(result.Success);
        Assert.Contains("single output", result.Error);
        Assert.Contains("6,000", result.Error);
        Assert.Empty(harness.Sdk.ExitBuildCalls);
    }

    /// <summary>
    /// An explorer that cannot be read leaves the funding <em>unknown</em>, never zero.
    /// </summary>
    /// <remarks>
    /// <b>The distinction is the point of the nullable.</b> An operator who has already sent the funding sats
    /// reads "0 sat received" as "my transaction has not confirmed yet" and waits — on a confirmation that
    /// happened hours ago, because the explorer URL was wrong.
    /// </remarks>
    [Fact]
    public async Task An_unreachable_explorer_reports_unknown_rather_than_zero()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.Seed();
        harness.ExplorerOffline();

        var page = await harness.Service.ReadAsync(StoreId, Ct);

        Assert.Null(page.FundingReceivedSat);
        Assert.NotNull(page.ActiveRecord);
    }

    /// <summary>And a build against an unreadable explorer refuses with something an operator can act on.</summary>
    [Fact]
    public async Task An_unreachable_explorer_refuses_the_build_readably()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        var record = harness.Seed();
        harness.ExplorerFails();

        var result = await harness.Service.BuildAsync(StoreId, record.Id, Ct);

        Assert.False(result.Success);
        Assert.Contains("block explorer could not be read", result.Error);
        Assert.Equal(result.Error, harness.Records.Records[record.Id].LastError);
        Assert.Empty(harness.Sdk.ExitBuildCalls);
    }

    /// <summary>
    /// Off mainnet, an unset explorer URL is a refusal naming the setting.
    /// </summary>
    /// <remarks>
    /// mempool.space has no regtest, so falling back to it there would answer every lookup with "nothing found"
    /// — which reads exactly like an unconfirmed funding transaction.
    /// </remarks>
    [Fact]
    public async Task A_regtest_store_with_no_explorer_configured_is_told_to_set_one()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true, esploraApiUrl: null);
        harness.WithLeaves(("leaf-a", 500_000));
        var record = harness.Seed();

        var page = await harness.Service.ReadAsync(StoreId, Ct);
        Assert.Null(page.FundingReceivedSat);

        var result = await harness.Service.BuildAsync(StoreId, record.Id, Ct);

        Assert.False(result.Success);
        Assert.Contains("esplora API URL", result.Error);
    }

    /// <summary>The page reports what the explorer confirmed, in satoshi.</summary>
    [Fact]
    public async Task The_page_reports_the_confirmed_funding_balance()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.Seed();
        harness.Explorer(Utxo(5_000, vout: 0), Utxo(2_500, vout: 1), Utxo(9_000, vout: 2, confirmed: false));

        var page = await harness.Service.ReadAsync(StoreId, Ct);

        Assert.Equal(7_500, page.FundingReceivedSat);
    }

    #endregion

    #region Building

    /// <summary>
    /// A funded exit builds, and everything the operator needs to broadcast it is on the row.
    /// </summary>
    /// <remarks>
    /// The transactions are the whole product of this feature and they exist nowhere else — the SDK will not hand
    /// them back without a fresh build — so this asserts they round-trip out of the column, CPFP child and
    /// dependency order included.
    /// </remarks>
    [Fact]
    public async Task A_funded_exit_builds_and_persists_its_transactions_and_totals()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 300_000), ("leaf-b", 200_000));
        var record = harness.Seed(
            leafIds: ["leaf-a", "leaf-b"],
            singleUtxoFundingSat: 4_200,
            lastError: "not enough on the funding address");
        harness.Explorer(Utxo(10_000));

        var result = await harness.Service.BuildAsync(StoreId, record.Id, Ct);

        Assert.True(result.Success, result.Error);

        var stored = harness.Records.Records[record.Id];
        Assert.Equal(UnilateralExitStatus.Built, stored.Status);
        Assert.Equal(500_000, stored.RecoverableValueSat);
        Assert.Equal(harness.Sdk.ExitTotalFeeSat, stored.TotalFeeSat);
        // Cleared by a build that got further: the previous attempt's complaint must not sit next to the result.
        Assert.Null(stored.LastError);

        var funding = JsonSerializer.Deserialize<SparkExitFundingUtxo[]>(stored.FundingUtxosJson!)!;
        var spent = Assert.Single(funding);
        Assert.Equal(FundingTxid, spent.Txid);
        Assert.Equal(10_000, spent.ValueSat);
        Assert.False(string.IsNullOrWhiteSpace(spent.PubkeyHex));

        // Default serializer options both ways, so any reader deserialising the seam records plainly gets them
        // back — which is what the exit page does with this column.
        var transactions = JsonSerializer.Deserialize<SparkExitTransaction[]>(stored.TransactionsJson!)!;
        Assert.Equal(4, transactions.Length);
        Assert.Equal(SparkExitTxKind.Fanout, transactions[0].Kind);
        Assert.Equal(SparkExitTxKind.Sweep, transactions[^1].Kind);
        Assert.Equal(SparkExitTxStatus.Unconfirmed, transactions[0].Status);

        var node = transactions.First(tx => tx.Kind is SparkExitTxKind.TreeNode);
        Assert.True(node.RequiresPackageBroadcast);
        Assert.Equal(1_008u, node.CsvTimelockBlocks!.Value);
        Assert.Equal(["txid:fanout"], node.DependsOn);

        // The build spent exactly the one output it was funded with, and it had a key for it.
        var call = Assert.Single(harness.Sdk.ExitBuildCalls);
        Assert.Equal(FundingTxid, Assert.Single(call.FundingUtxos).Txid);
        Assert.Equal(32, call.FundingSecretKeyLength);
        Assert.Null(call.Rejection);
    }

    /// <summary>
    /// The build re-quotes the leaves the record was pinned to, not whatever the SDK would pick now.
    /// </summary>
    /// <remarks>
    /// Automatic selection is free to choose a different set on every call, and the funding output the operator
    /// paid for was sized for the first set. Re-quoting with the pinned ids is what makes a resume mean the same
    /// exit — so the assertion is on the arguments, not on the outcome.
    /// </remarks>
    [Fact]
    public async Task The_build_re_quotes_the_pinned_leaves()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 300_000), ("leaf-b", 200_000), ("leaf-c", 100_000));
        var record = harness.Seed(leafIds: ["leaf-a", "leaf-b"], singleUtxoFundingSat: 4_200);
        harness.Explorer(Utxo(10_000));

        var result = await harness.Service.BuildAsync(StoreId, record.Id, Ct);

        Assert.True(result.Success, result.Error);
        // Both quotes the build takes — the one that prices the funding requirement and the one the SDK takes
        // inside the atomic build — name the pinned ids. Neither may fall back to automatic selection.
        Assert.Equal(2, harness.Sdk.ExitQuoteCalls.Count);
        Assert.All(harness.Sdk.ExitQuoteCalls, call => Assert.Equal(["leaf-a", "leaf-b"], call.LeafIds));
        // leaf-c was never funded for, so it is not in the built exit however attractive it looks.
        Assert.Equal(500_000, harness.Records.Records[record.Id].RecoverableValueSat);
    }

    /// <summary>
    /// A quote that went stale between the funding and the build is refused, and nothing is signed.
    /// </summary>
    /// <remarks>
    /// A unilateral-exit quote has no expiry and no id: it goes stale <em>silently</em> as the wallet's tree moves
    /// under it. So the guard cannot live on the persisted figures — the build re-prices the pinned leaves before
    /// it looks at funding at all, and the SDK's own approval callback checks again against the quote it takes
    /// inside the build.
    /// </remarks>
    [Fact]
    public async Task A_build_whose_leaves_have_vanished_is_refused_before_anything_is_signed()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        var record = harness.Seed(leafIds: ["leaf-a"], singleUtxoFundingSat: 4_200);
        harness.Explorer(Utxo(10_000));

        // The wallet's tree moves before the build's own quote is taken.
        harness.Sdk.ExitLeaves.Clear();

        var result = await harness.Service.BuildAsync(StoreId, record.Id, Ct);

        Assert.False(result.Success);
        Assert.Contains("no longer in this wallet", result.Error);

        var stored = harness.Records.Records[record.Id];
        Assert.Equal(UnilateralExitStatus.AwaitingFunding, stored.Status);
        Assert.Equal(result.Error, stored.LastError);
        Assert.Null(stored.TransactionsJson);
        // Refused by the re-quote, so the SDK was never asked to build and the funding key was never handed over.
        Assert.Empty(harness.Sdk.ExitBuildCalls);
    }

    /// <summary>
    /// The build's own veto still fires when the quote moves between the re-price and the build.
    /// </summary>
    /// <remarks>
    /// The re-price and the atomic build are two SDK calls, so the tree can move between them — which is the
    /// whole reason the seam takes a veto rather than trusting a quote handed in from outside. This drives the
    /// change from inside the SDK's own prepare, so the second quote provably differs from the first.
    /// </remarks>
    [Fact]
    public async Task A_wallet_that_moves_between_the_re_price_and_the_build_is_vetoed()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        var record = harness.Seed(leafIds: ["leaf-a"], singleUtxoFundingSat: 4_200);
        harness.Explorer(Utxo(10_000));

        var quotes = 0;
        harness.Sdk.WhenExitQuoted = () =>
        {
            // After the re-price has been answered, and before the build's own quote is taken.
            if (++quotes == 1)
                harness.Sdk.ExitLeaves.Clear();
        };

        var result = await harness.Service.BuildAsync(StoreId, record.Id, Ct);

        Assert.False(result.Success);
        Assert.Contains("no longer in this wallet", result.Error);
        // The SDK recorded the veto and built nothing.
        Assert.NotNull(Assert.Single(harness.Sdk.ExitBuildCalls).Rejection);
        Assert.Null(harness.Records.Records[record.Id].TransactionsJson);
    }

    /// <summary>
    /// A funding requirement that grew since the quote is re-priced first, so a correct top-up is selectable.
    /// </summary>
    /// <remarks>
    /// <b>This is the deadlock the build's ordering exists to prevent.</b> The requirement moves with the fee
    /// market. If the funding output were selected against the figure the record was created with, an operator
    /// who sent exactly what the refusal asked for would find that output ignored — selection would keep taking
    /// the smaller one that satisfied the stale figure, the veto would keep refusing it, and the exit would never
    /// build however much was sent. So the re-price comes first, its requirement is persisted, and the number on
    /// the page is the number the selection uses.
    /// </remarks>
    [Fact]
    public async Task A_requirement_that_grew_is_re_priced_before_the_funding_is_selected()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        var record = harness.Seed(leafIds: ["leaf-a"], singleUtxoFundingSat: 4_200);

        // The fee market moved: the same leaves now need a much larger funding output than the record says.
        harness.Sdk.ExitSingleUtxoFundingSat = 40_000;
        harness.Explorer(Utxo(4_200, vout: 0));

        var refused = await harness.Service.BuildAsync(StoreId, record.Id, Ct);

        Assert.False(refused.Success);
        Assert.Contains("40,000", refused.Error);
        // The fresh requirement is on the row, so the page asks for the amount the next attempt will judge by.
        Assert.Equal(40_000, harness.Records.Records[record.Id].SingleUtxoFundingSat);

        // The operator sends exactly what they were asked for, as a single new output. The old 4,200 output is
        // still there and is still the smallest — which is what used to make this unbuildable for ever.
        harness.Explorer(Utxo(4_200, vout: 0), Utxo(40_000, vout: 1));

        var built = await harness.Service.BuildAsync(StoreId, record.Id, Ct);

        Assert.True(built.Success, built.Error);
        var spent = Assert.Single(
            JsonSerializer.Deserialize<SparkExitFundingUtxo[]>(
                harness.Records.Records[record.Id].FundingUtxosJson!)!);
        Assert.Equal(40_000, spent.ValueSat);
        Assert.Equal(1u, spent.Vout);
    }

    /// <summary>A fresh quote that no longer pays for itself is vetoed too.</summary>
    [Fact]
    public async Task A_build_that_would_now_cost_more_than_it_recovers_is_vetoed()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        var record = harness.Seed(leafIds: ["leaf-a"], singleUtxoFundingSat: 4_200);
        harness.Explorer(Utxo(10_000));

        // The fee market moved: the same leaves now cost more to force on-chain than they hold.
        harness.Sdk.ExitTotalFeeSat = 900_000;

        var result = await harness.Service.BuildAsync(StoreId, record.Id, Ct);

        Assert.False(result.Success);
        Assert.Contains("costs more than it recovers", result.Error);
        Assert.Equal(UnilateralExitStatus.AwaitingFunding, harness.Records.Records[record.Id].Status);
        Assert.Null(harness.Records.Records[record.Id].TransactionsJson);
    }

    /// <summary>
    /// A requirement that grew inside the build names the largest output on the address, not the chosen one.
    /// </summary>
    /// <remarks>
    /// The two are different numbers whenever an operator has funded more than once: the build spends the
    /// <em>smallest</em> output that covers the requirement, so telling them "the address holds X in its largest
    /// output" while quoting the one that was picked is simply false — and it is false in the direction that
    /// makes them send sats they did not need to.
    /// </remarks>
    [Fact]
    public async Task A_veto_for_a_grown_requirement_reports_the_largest_output_on_the_address()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        var record = harness.Seed(leafIds: ["leaf-a"], singleUtxoFundingSat: 4_200);
        harness.Explorer(Utxo(10_000, vout: 0), Utxo(12_000, vout: 1));

        var quotes = 0;
        harness.Sdk.WhenExitQuoted = () =>
        {
            // The requirement grows between the re-price (which picks the 10,000 output) and the build's own
            // quote, so the veto is the thing that refuses.
            if (++quotes == 1)
                harness.Sdk.ExitSingleUtxoFundingSat = 20_000;
        };

        var result = await harness.Service.BuildAsync(StoreId, record.Id, Ct);

        Assert.False(result.Success);
        Assert.Contains("20,000", result.Error);
        Assert.Contains("12,000", result.Error);
        Assert.DoesNotContain("10,000", result.Error);
        // The requirement the veto judged by is on the row, so the next attempt selects against the same number.
        Assert.Equal(20_000, harness.Records.Records[record.Id].SingleUtxoFundingSat);
    }

    /// <summary>
    /// A request abandoned after the SDK has signed still gets its transactions written.
    /// </summary>
    /// <remarks>
    /// <b>This is the one write in the plugin that must not be cancellable.</b> The signed set exists in this
    /// process and nowhere else, and the SDK will not hand it back without a fresh build against a fresh funding
    /// output — so a merchant who closed the tab must not lose the exit they just paid the fan-out fee for.
    /// </remarks>
    [Fact]
    public async Task A_build_cancelled_after_signing_still_persists_its_transactions()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        var record = harness.Seed(leafIds: ["leaf-a"], singleUtxoFundingSat: 4_200);
        harness.Explorer(Utxo(10_000));

        using var cancelled = new CancellationTokenSource();
        var quotes = 0;
        harness.Sdk.WhenExitQuoted = () =>
        {
            // The second quote is the one the SDK takes inside the build, so this cancels the request while the
            // exit is about to be signed.
            if (++quotes == 2)
                cancelled.Cancel();
        };

        var result = await harness.Service.BuildAsync(StoreId, record.Id, cancelled.Token);

        Assert.True(result.Success, result.Error);
        var stored = harness.Records.Records[record.Id];
        Assert.Equal(UnilateralExitStatus.Built, stored.Status);
        Assert.NotNull(stored.TransactionsJson);
    }

    /// <summary>
    /// The SDK's own funding failures arrive as readable copy on the record rather than as an exception.
    /// </summary>
    /// <remarks>
    /// Both of these mean the operator has something to do — top up, or send fresh funds because the output was
    /// spent from under the exit — and both leave the exit exactly where it was, because nothing was built.
    /// </remarks>
    [Fact]
    public async Task The_SDK_s_funding_failures_land_on_the_record_as_words()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        var shortfallRecord = harness.Seed(id: "exit-shortfall", leafIds: ["leaf-a"], singleUtxoFundingSat: 4_200);
        harness.Explorer(Utxo(10_000));
        harness.Sdk.FailExitBuildWith = new SparkExitFundingShortfallException(99_000);

        var shortfall = await harness.Service.BuildAsync(StoreId, shortfallRecord.Id, Ct);

        Assert.False(shortfall.Success);
        Assert.Contains("99,000", shortfall.Error);
        Assert.Equal(shortfall.Error, harness.Records.Records[shortfallRecord.Id].LastError);
        Assert.Equal(
            UnilateralExitStatus.AwaitingFunding,
            harness.Records.Records[shortfallRecord.Id].Status);

        harness.Sdk.FailExitBuildWith = new SparkExitFundingUtxoConflictException(FundingTxid, 0);

        var conflict = await harness.Service.BuildAsync(StoreId, shortfallRecord.Id, Ct);

        Assert.False(conflict.Success);
        Assert.Contains(FundingTxid, conflict.Error);
        Assert.Contains("already spent", conflict.Error);
    }

    /// <summary>A build against an unknown exit, or one that is finished, is refused.</summary>
    [Fact]
    public async Task Building_an_unknown_or_finished_exit_is_refused()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        harness.Explorer(Utxo(10_000));

        var missing = await harness.Service.BuildAsync(StoreId, "exit-nowhere", Ct);
        Assert.Equal(SparkUnilateralExitService.ExitNotFound, missing.Error);

        var finished = harness.Seed(id: "exit-done", status: UnilateralExitStatus.Completed);
        var result = await harness.Service.BuildAsync(StoreId, finished.Id, Ct);

        Assert.False(result.Success);
        Assert.Contains("finished", result.Error);
        Assert.Empty(harness.Sdk.ExitBuildCalls);
    }

    /// <summary>
    /// An exit whose funding address the store's seed no longer derives is refused, with the path to recover it.
    /// </summary>
    /// <remarks>
    /// Reachable by replacing a store's seed between the quote and the build. The plugin cannot sign for the
    /// output the operator funded, and the honest answer includes where their sats still are.
    /// </remarks>
    [Fact]
    public async Task An_exit_whose_funding_key_no_longer_derives_is_refused()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        var record = harness.Seed(fundingAddress: "bcrt1qsomeotheraddressentirely");
        harness.Explorer(Utxo(10_000));

        var result = await harness.Service.BuildAsync(StoreId, record.Id, Ct);

        Assert.False(result.Success);
        Assert.Contains("no longer derives", result.Error);
        Assert.Contains("m/84'/1'/4607060'/0/0", result.Error);
        Assert.Empty(harness.Sdk.ExitBuildCalls);
    }

    #endregion

    #region Abandoning

    /// <summary>
    /// Abandoning frees the store for a fresh quote, which is the only reason the status exists.
    /// </summary>
    /// <remarks>
    /// It moves no money and cancels nothing on-chain — a point the page has to make out loud — but without it an
    /// exit with no way forward would block every later one for ever.
    /// </remarks>
    [Fact]
    public async Task Abandoning_an_exit_frees_the_store()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        var record = harness.Seed();

        var abandoned = await harness.Service.AbandonAsync(StoreId, record.Id, Ct);

        Assert.True(abandoned.Success, abandoned.Error);
        Assert.Equal(UnilateralExitStatus.Abandoned, harness.Records.Records[record.Id].Status);

        // Idempotent: a second press is not an error.
        Assert.True((await harness.Service.AbandonAsync(StoreId, record.Id, Ct)).Success);

        var quote = await harness.Service.QuoteAsync(StoreId, 10, Destination, Ct);

        Assert.True(quote.Success, quote.Error);
        Assert.NotEqual(record.Id, quote.Record!.Id);
    }

    /// <summary>An exit belonging to another store is invisible, not merely unmodifiable.</summary>
    [Fact]
    public async Task Another_store_s_exit_cannot_be_built_or_abandoned()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        harness.Explorer(Utxo(10_000));
        var victim = harness.Seed(storeId: "store-2");

        Assert.Equal(
            SparkUnilateralExitService.ExitNotFound,
            (await harness.Service.BuildAsync(StoreId, victim.Id, Ct)).Error);
        Assert.Equal(
            SparkUnilateralExitService.ExitNotFound,
            (await harness.Service.AbandonAsync(StoreId, victim.Id, Ct)).Error);

        Assert.Equal(UnilateralExitStatus.AwaitingFunding, harness.Records.Records[victim.Id].Status);
    }

    /// <summary>
    /// Abandoning cannot land on a row a build filled with signed transactions while it was being read.
    /// </summary>
    /// <remarks>
    /// Two browser tabs, or two servers behind one database. The abandon read the row while it was awaiting
    /// funding; by the time it writes, a build has put the exit's only copy of its signed transactions on it. The
    /// compare-and-set is what makes that write miss rather than clobber.
    /// </remarks>
    [Fact]
    public async Task An_abandon_that_read_a_stale_row_does_not_clobber_a_build()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        var record = harness.Seed(leafIds: ["leaf-a"], singleUtxoFundingSat: 4_200);
        harness.Explorer(Utxo(10_000));

        UnilateralExitOpResult? abandoned = null;
        harness.Sdk.WhenExitQuoted = () =>
        {
            // Inside the build, so the abandon provably reads the row before the build has written to it. The
            // single-flight gate refuses it, which is the first line of defence.
            harness.Sdk.WhenExitQuoted = null;
            abandoned = harness.Service.AbandonAsync(StoreId, record.Id, CancellationToken.None)
                .GetAwaiter().GetResult();
        };

        Assert.True((await harness.Service.BuildAsync(StoreId, record.Id, Ct)).Success);

        Assert.False(abandoned!.Success);
        Assert.Equal(SparkUnilateralExitService.OperationInFlight, abandoned.Error);

        var stored = harness.Records.Records[record.Id];
        Assert.Equal(UnilateralExitStatus.Built, stored.Status);
        Assert.NotNull(stored.TransactionsJson);

        // And the durable half: an abandon carrying the row as it looked before the build is refused outright.
        var stale = InMemoryUnilateralExitRecordStore.Copy(record);
        stale.Status = UnilateralExitStatus.Abandoned;
        Assert.False(await harness.Records.UpdateAsync(
            stale, UnilateralExitStatus.AwaitingFunding, Ct));
        Assert.NotNull(harness.Records.Records[record.Id].TransactionsJson);
    }

    #endregion

    #region Finishing

    /// <summary>
    /// Marking a built exit completed frees the store, and it is the right verb for a finished exit.
    /// </summary>
    /// <remarks>
    /// Nothing here watches the chain, so this is the operator's statement rather than an observation. Without it
    /// abandoning would be the only way a finished exit ever left the active state — and telling a merchant to
    /// "abandon" the exit that recovered their money is a lie the page would have to keep telling.
    /// </remarks>
    [Fact]
    public async Task Marking_a_built_exit_completed_frees_the_store()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        var record = harness.Seed(status: UnilateralExitStatus.Built);

        var completed = await harness.Service.MarkCompletedAsync(StoreId, record.Id, Ct);

        Assert.True(completed.Success, completed.Error);
        Assert.Equal(UnilateralExitStatus.Completed, harness.Records.Records[record.Id].Status);

        // Idempotent, like abandoning: a second press is not an error.
        Assert.True((await harness.Service.MarkCompletedAsync(StoreId, record.Id, Ct)).Success);

        var quote = await harness.Service.QuoteAsync(StoreId, 10, Destination, Ct);
        Assert.True(quote.Success, quote.Error);
    }

    /// <summary>
    /// An exit that was never built, or was abandoned, cannot be declared finished.
    /// </summary>
    /// <remarks>
    /// The abandoned branch is what makes abandoning's own "already finished" refusal reachable: the two terminal
    /// states each refuse the other's verb, so a stale form post cannot rewrite which one happened.
    /// </remarks>
    [Fact]
    public async Task Only_a_built_exit_can_be_marked_completed()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);

        var waiting = harness.Seed(id: "exit-waiting");
        var notBuilt = await harness.Service.MarkCompletedAsync(StoreId, waiting.Id, Ct);
        Assert.False(notBuilt.Success);
        Assert.Contains("not been built", notBuilt.Error);
        Assert.Equal(UnilateralExitStatus.AwaitingFunding, harness.Records.Records[waiting.Id].Status);

        Assert.True((await harness.Service.AbandonAsync(StoreId, waiting.Id, Ct)).Success);
        var abandoned = await harness.Service.MarkCompletedAsync(StoreId, waiting.Id, Ct);
        Assert.False(abandoned.Success);
        Assert.Contains("abandoned", abandoned.Error);

        Assert.Equal(
            SparkUnilateralExitService.ExitNotFound,
            (await harness.Service.MarkCompletedAsync(StoreId, "exit-nowhere", Ct)).Error);
    }

    /// <summary>A completed exit refuses to be abandoned, which is the branch that used to be unreachable.</summary>
    [Fact]
    public async Task A_completed_exit_cannot_be_abandoned()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        var record = harness.Seed(status: UnilateralExitStatus.Built);
        Assert.True((await harness.Service.MarkCompletedAsync(StoreId, record.Id, Ct)).Success);

        var abandoned = await harness.Service.AbandonAsync(StoreId, record.Id, Ct);

        Assert.False(abandoned.Success);
        Assert.Contains("already recorded as finished", abandoned.Error);
        Assert.Equal(UnilateralExitStatus.Completed, harness.Records.Records[record.Id].Status);
    }

    #endregion

    #region The explorer setting

    /// <summary>
    /// The explorer override is settable from the page that reports it missing, and validated here.
    /// </summary>
    /// <remarks>
    /// It is the feature's one piece of real configuration, and off mainnet nothing works without it — so it
    /// belongs on the page that refuses for want of it rather than three clicks away. The validation lives in the
    /// service because the controller holds no policy.
    /// </remarks>
    [Fact]
    public async Task The_explorer_url_is_stored_validated_and_clearable()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true, esploraApiUrl: null);

        var set = await harness.Service.SetExplorerUrlAsync(StoreId, " https://explorer.test/api/ ", Ct);

        Assert.True(set.Success, set.Error);
        // Trimmed, and the trailing slash removed so the path built onto it is never doubled.
        Assert.Equal(
            "https://explorer.test/api",
            harness.Settings.Settings[StoreId]!.UnilateralExit.EsploraApiUrl);

        var writes = harness.Settings.Writes.Count;

        // No write for a press that changes nothing: storing settings tears down and reconnects the wallet.
        Assert.True((await harness.Service.SetExplorerUrlAsync(StoreId, "https://explorer.test/api", Ct)).Success);
        Assert.Equal(writes, harness.Settings.Writes.Count);

        // Blank clears it, which is the only way back to the mainnet default.
        Assert.True((await harness.Service.SetExplorerUrlAsync(StoreId, "  ", Ct)).Success);
        Assert.Null(harness.Settings.Settings[StoreId]!.UnilateralExit.EsploraApiUrl);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://explorer.example/api")]
    [InlineData("/relative/api")]
    public async Task An_unusable_explorer_url_is_refused_before_it_is_stored(string candidate)
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true, esploraApiUrl: "https://good.test/api");

        var result = await harness.Service.SetExplorerUrlAsync(StoreId, candidate, Ct);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        // The working value is untouched: a rejected edit must not take the store off its explorer.
        Assert.Equal("https://good.test/api", harness.Settings.Settings[StoreId]!.UnilateralExit.EsploraApiUrl);
        Assert.Empty(harness.Settings.Writes);
    }

    [Fact]
    public async Task Setting_the_explorer_url_on_an_unconfigured_store_is_refused()
    {
        using var harness = Harness.Create();

        var result = await harness.Service.SetExplorerUrlAsync(StoreId, "https://explorer.test/api", Ct);

        Assert.Equal(SparkUnilateralExitService.NotConfigured, result.Error);
        Assert.Empty(harness.Settings.Writes);
    }

    #endregion

    #region The page read

    /// <summary>The read reports the wallet, the balance, the active exit and the history in one pass.</summary>
    [Fact]
    public async Task The_page_read_reports_the_wallet_the_balance_and_the_history()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.Sdk.BalanceSats = 640_000;
        harness.Seed(id: "exit-old", status: UnilateralExitStatus.Abandoned, minutesOld: 60);
        var active = harness.Seed(id: "exit-live", leafIds: ["leaf-a", "leaf-b"], fundingKeyIndex: 3);
        harness.Explorer(Utxo(4_200));

        var page = await harness.Service.ReadAsync(StoreId, Ct);

        Assert.True(page.WalletRunning);
        Assert.True(page.DisclosureAcknowledged);
        Assert.Equal(640_000, page.BalanceSats);
        Assert.Equal(active.Id, page.ActiveRecord?.Id);
        // Terminal rows only: the active exit has its own panel, and listing it twice invites an operator to read
        // the history row as a second exit.
        Assert.Equal(["exit-old"], page.History.Select(r => r.Id).ToArray());
        Assert.Equal(4_200, page.FundingReceivedSat);
        Assert.Equal(4_200, page.FundingLargestOutputSat);
        Assert.Equal(2, page.LeafCount);
        // The path an operator needs to sweep the funding address by hand if they abandon this exit.
        Assert.Equal("m/84'/1'/4607060'/0/3", page.FundingKeyPath);
        Assert.Null(page.Transactions);
        Assert.False(page.TransactionsUnreadable);
    }

    /// <summary>
    /// The page is told both what the funding address holds and what its largest single output holds.
    /// </summary>
    /// <remarks>
    /// The sum is the misleading figure: an exit is funded from one output, so an address holding twice the
    /// requirement across two outputs funds nothing. A page reporting only the total would tell an operator they
    /// were done while every build refused.
    /// </remarks>
    [Fact]
    public async Task The_page_read_separates_the_funding_total_from_its_largest_output()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.Seed(singleUtxoFundingSat: 4_200);
        harness.Explorer(Utxo(2_500, vout: 0), Utxo(3_000, vout: 1));

        var page = await harness.Service.ReadAsync(StoreId, Ct);

        Assert.Equal(5_500, page.FundingReceivedSat);
        Assert.Equal(3_000, page.FundingLargestOutputSat);
    }

    /// <summary>
    /// Rendering the page derives no key, so nothing unprotects the merchant's seed on a page load.
    /// </summary>
    /// <remarks>
    /// Measuring an address takes no key at all — only a build needs one — and a read path that unprotected the
    /// seed on every load would be paying a real risk for nothing. Asserted through a store whose seed cannot be
    /// decrypted: the funding figures still come back, which they could not if the read derived anything.
    /// </remarks>
    [Fact]
    public async Task The_page_read_reports_funding_without_the_store_s_seed()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.Seed(fundingKeyIndex: 2);
        harness.Explorer(Utxo(4_200));
        harness.Settings.Settings[StoreId]!.ProtectedMnemonic = "not something this keyring can unprotect";

        var page = await harness.Service.ReadAsync(StoreId, Ct);

        Assert.Equal(4_200, page.FundingReceivedSat);
        Assert.Equal(4_200, page.FundingLargestOutputSat);
        // The path is arithmetic on the record's index, not a derivation, so it survives too.
        Assert.Equal("m/84'/1'/4607060'/0/2", page.FundingKeyPath);
    }

    /// <summary>
    /// A built exit's transactions come back typed, deserialised by the one layer that writes them.
    /// </summary>
    /// <remarks>
    /// The page is the only reader, and it reads them from here rather than from the column: one owner for the
    /// write format means the controller and the view cannot disagree with the service about what is in it.
    /// </remarks>
    [Fact]
    public async Task The_page_read_hands_back_a_built_exit_s_transactions_typed()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        var record = harness.Seed(leafIds: ["leaf-a"], singleUtxoFundingSat: 4_200);
        harness.Explorer(Utxo(10_000));
        Assert.True((await harness.Service.BuildAsync(StoreId, record.Id, Ct)).Success);

        var page = await harness.Service.ReadAsync(StoreId, Ct);

        Assert.False(page.TransactionsUnreadable);
        Assert.NotNull(page.Transactions);
        Assert.Equal(SparkExitTxKind.Fanout, page.Transactions[0].Kind);
        Assert.Equal(SparkExitTxKind.Sweep, page.Transactions[^1].Kind);
        Assert.True(page.Transactions.Any(tx => tx.RequiresPackageBroadcast));
        // A built exit is not waiting on funding, so nothing asks the explorer about it any more.
        Assert.Null(page.FundingReceivedSat);
    }

    /// <summary>
    /// A transaction column that cannot be read back becomes an explanation, never an exception.
    /// </summary>
    /// <remarks>
    /// Both shapes matter. Malformed JSON is the obvious one; the subtle one is JSON that parses into a record
    /// with null members, because <c>System.Text.Json</c> applies no null checks to a positional record's
    /// parameters — so <c>[{}]</c> yields a transaction with a null txid and a null dependency list, which the
    /// page would render as broadcast instructions.
    /// </remarks>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("[{}]")]
    [InlineData("""[{"Txid":"aa","TxHex":"0200","DependsOn":[],"Kind":99,"Status":0}]""")]
    public async Task An_unreadable_transaction_column_is_reported_rather_than_thrown(string stored)
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        var record = harness.Seed(status: UnilateralExitStatus.Built);
        harness.Records.Records[record.Id].TransactionsJson = stored;

        var page = await harness.Service.ReadAsync(StoreId, Ct);

        Assert.True(page.TransactionsUnreadable);
        Assert.Null(page.Transactions);
        // And the record itself is still on the page, so the operator can abandon it.
        Assert.Equal(record.Id, page.ActiveRecord?.Id);
    }

    /// <summary>
    /// A wallet that is down still renders the page, and a balance that cannot be read is not an exception.
    /// </summary>
    /// <remarks>
    /// The history and the active exit are precisely what an operator came to look at when the wallet is in
    /// trouble, so nothing about reading the balance may take the page down with it.
    /// </remarks>
    [Fact]
    public async Task The_page_read_survives_a_wallet_that_is_down()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.Seed(id: "exit-live");
        harness.Sdk.FailWith = new InvalidOperationException("the wallet is wedged");

        var page = await harness.Service.ReadAsync(StoreId, Ct);

        Assert.True(page.WalletRunning);
        Assert.Equal(0, page.BalanceSats);
        Assert.Equal("exit-live", page.ActiveRecord?.Id);

        harness.Runtime.Clients.Remove(StoreId);
        var stopped = await harness.Service.ReadAsync(StoreId, Ct);

        Assert.False(stopped.WalletRunning);
        Assert.Equal("exit-live", stopped.ActiveRecord?.Id);
    }

    /// <summary>A built exit does not keep asking the explorer about funding it has already committed.</summary>
    [Fact]
    public async Task A_built_exit_reports_no_funding_balance()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.Seed(status: UnilateralExitStatus.Built);
        harness.Explorer(Utxo(4_200));

        var page = await harness.Service.ReadAsync(StoreId, Ct);

        Assert.Null(page.FundingReceivedSat);
        Assert.Equal(0, harness.ExplorerRequests);
    }

    #endregion

    #region The funding key

    /// <summary>
    /// The funding key is derived at the plugin's own hardened account, and it is a pinned path.
    /// </summary>
    /// <remarks>
    /// <b>Both halves of this are load-bearing.</b> The account index has to stay away from BIP84 account 0,
    /// because on a store provisioned from the BTCPay hot wallet that account <em>is</em> the merchant's own
    /// wallet — their coin selection could spend the funding UTXO out from under a half-broadcast exit. And the
    /// path has to stay put, because funding already sent to an address derived from the old path is only
    /// recoverable by hand.
    /// </remarks>
    [Fact]
    public void The_funding_key_is_derived_at_the_plugin_s_own_hardened_account()
    {
        Assert.Equal("84'/1'/4607060'/0/0", SparkExitFundingKey.KeyPathFor(Network.RegTest, 0).ToString());
        Assert.Equal("84'/0'/4607060'/0/0", SparkExitFundingKey.KeyPathFor(Network.Main, 0).ToString());
        Assert.Equal("84'/1'/4607060'/0/7", SparkExitFundingKey.KeyPathFor(Network.RegTest, 7).ToString());

        // BIP32 reserves the top bit of a child number for hardening, so an index above int.MaxValue is not an
        // address index at all — refused rather than wrapped into a key for a different address.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SparkExitFundingKey.KeyPathFor(Network.RegTest, (uint)int.MaxValue + 1));

        Assert.True(SparkExitFundingKey.TryDerive(Mnemonic, Network.RegTest, 0, out var regtest, out var error));
        Assert.Null(error);
        using (regtest)
        {
            Assert.Equal(FundingAddress, regtest!.Address);
            // Compressed, and the public half only: 33 bytes as hex.
            Assert.Equal(66, regtest.PubkeyHex.Length);
            Assert.Equal(32, regtest.Secret.Length);
        }

        // Mainnet derives a different key as well as a different address: the coin type is part of the path.
        Assert.True(SparkExitFundingKey.TryDerive(Mnemonic, Network.Main, 0, out var mainnet, out _));
        using (mainnet)
        {
            Assert.StartsWith("bc1q", mainnet!.Address);
            Assert.NotEqual(regtest!.PubkeyHex, mainnet.PubkeyHex);
        }

        // And a different address index is a different key, which is the whole reason one exit's leftovers
        // cannot land on the next exit's funding address.
        Assert.True(SparkExitFundingKey.TryDerive(Mnemonic, Network.RegTest, 1, out var second, out _));
        using (second)
        {
            Assert.NotEqual(FundingAddress, second!.Address);
        }
    }

    /// <summary>Disposing the key zeroes it, and using it afterwards is an error rather than a silent zero key.</summary>
    [Fact]
    public void Disposing_the_funding_key_zeroes_the_secret()
    {
        Assert.True(SparkExitFundingKey.TryDerive(Mnemonic, Network.RegTest, 0, out var key, out _));
        var secret = key!.Secret;
        Assert.Contains(secret, b => b != 0);

        key.Dispose();
        key.Dispose();

        Assert.All(secret, b => Assert.Equal(0, b));
        // A signer built over 32 zero bytes fails a long way from the mistake, so this throws instead.
        Assert.Throws<ObjectDisposedException>(() => key.Secret);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a mnemonic at all")]
    [InlineData("abandon abandon abandon")]
    public void An_unusable_phrase_is_a_refusal_rather_than_an_exception(string? phrase)
    {
        Assert.False(SparkExitFundingKey.TryDerive(phrase, Network.RegTest, 0, out var key, out var error));
        Assert.Null(key);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    #endregion

    #region The explorer's own rules

    /// <summary>The default explorer applies on mainnet only; elsewhere the override is required.</summary>
    [Fact]
    public void The_default_explorer_is_mainnet_only()
    {
        Assert.True(SparkExitFundingExplorer.TryResolveBaseUrl(
            new UnilateralExitSettings(), mainnet: true, out var mainnet, out _));
        Assert.Equal(SparkExitFundingExplorer.MainnetDefaultApiUrl, mainnet);

        Assert.False(SparkExitFundingExplorer.TryResolveBaseUrl(
            new UnilateralExitSettings(), mainnet: false, out _, out var error));
        Assert.Contains("esplora API URL", error);

        // A configured override wins on either network, and its trailing slash is not doubled into the path.
        Assert.True(SparkExitFundingExplorer.TryResolveBaseUrl(
            new UnilateralExitSettings { EsploraApiUrl = "https://explorer.example/api/" },
            mainnet: false,
            out var configured,
            out _));
        Assert.Equal("https://explorer.example/api", configured);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://explorer.example/api")]
    [InlineData("/relative/api")]
    public void An_unusable_explorer_url_is_refused(string configured)
    {
        Assert.False(SparkExitFundingExplorer.TryResolveBaseUrl(
            new UnilateralExitSettings { EsploraApiUrl = configured }, mainnet: true, out _, out var error));
        Assert.NotNull(error);
    }

    /// <summary>
    /// An output whose txid is not 32 bytes of hex is dropped rather than passed to the SDK.
    /// </summary>
    /// <remarks>
    /// Dropped and not refused, so one junk row from a third party cannot hide the real funding output — the same
    /// discipline the sweep labeller applies to a provider-supplied txid.
    /// </remarks>
    [Fact]
    public async Task An_output_with_a_malformed_txid_is_dropped()
    {
        using var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.Seed();
        harness.ExplorerBody(
            """[{"txid":"../../etc/passwd","vout":0,"value":9000,"status":{"confirmed":true}},"""
            + Utxo(4_200, vout: 3)
            + "]");

        var page = await harness.Service.ReadAsync(StoreId, Ct);

        Assert.Equal(4_200, page.FundingReceivedSat);
    }

    #endregion

    /// <summary>One entry of an esplora <c>/address/{address}/utxo</c> response.</summary>
    private static string Utxo(long valueSat, uint vout = 0, bool confirmed = true) =>
        string.Format(
            CultureInfo.InvariantCulture,
            """{{"txid":"{0}","vout":{1},"value":{2},"status":{{"confirmed":{3}}}}}""",
            FundingTxid,
            vout,
            valueSat,
            confirmed ? "true" : "false");

    /// <summary>
    /// The service under test with every collaborator faked, and the feature gate held for the test's duration.
    /// </summary>
    /// <remarks>
    /// The settings store is built <em>without</em> a runtime on purpose: modelling the SDK reconnect a settings
    /// write causes would replace the fake wallet mid-test, and what these tests need to observe is the
    /// acknowledgement landing in storage rather than the reconnect that follows it. The reconnect itself is
    /// covered where it matters, in the Stable Balance tests.
    /// </remarks>
    private sealed class Harness : IDisposable
    {
        private const string Variable = "FLINT_EXPERIMENTAL_UNILATERAL_EXIT";

        private readonly string? _previous;
        private readonly ExplorerHandler _handler = new();

        private Harness(bool featureEnabled)
        {
            _previous = Environment.GetEnvironmentVariable(Variable);
            Environment.SetEnvironmentVariable(Variable, featureEnabled ? "1" : null);

            Protector = new SparkMnemonicProtector(new EphemeralDataProtectionProvider());
            Runtime.Clients[StoreId] = Sdk;

            Service = new SparkUnilateralExitService(
                Settings,
                Runtime,
                Records,
                Protector,
                new SparkExitFundingExplorer(
                    new ExplorerClientFactory(_handler),
                    NullLogger<SparkExitFundingExplorer>.Instance),
                Network.RegTest,
                new StubTimeProvider(Now),
                NullLogger<SparkUnilateralExitService>.Instance);
        }

        public static Harness Create(bool featureEnabled = true) => new(featureEnabled);

        public DateTimeOffset Now { get; } = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

        public FakeSparkSdkClient Sdk { get; } = new();

        public FakeSparkStoreRuntime Runtime { get; } = new();

        public FakeSparkStoreSettingsStore Settings { get; } = new();

        public InMemoryUnilateralExitRecordStore Records { get; } = new();

        public SparkMnemonicProtector Protector { get; }

        /// <summary>How many lookups actually reached the explorer.</summary>
        public int ExplorerRequests => _handler.Requests;

        public SparkUnilateralExitService Service { get; }

        /// <summary>Gives the store a Spark configuration, and optionally a stored acknowledgement.</summary>
        public void Configure(
            bool acknowledged = false,
            bool walletRunning = true,
            string? esploraApiUrl = "http://explorer.test/api")
        {
            Settings.Settings[StoreId] = new SparkSettings
            {
                ProtectedMnemonic = Protector.Protect(Mnemonic),
                SeedSource = SeedSource.Imported,
                UnilateralExit = new UnilateralExitSettings
                {
                    DisclosureAcknowledged = acknowledged,
                    EsploraApiUrl = esploraApiUrl
                }
            };

            if (!walletRunning)
                Runtime.Clients.Remove(StoreId);
        }

        /// <summary>The leaves an automatic selection would find.</summary>
        public void WithLeaves(params (string LeafId, long ValueSat)[] leaves)
        {
            Sdk.ExitLeaves.Clear();
            foreach (var (leafId, valueSat) in leaves)
                Sdk.ExitLeaves.Add(new SparkExitLeaf(leafId, valueSat));
        }

        /// <summary>Answers explorer lookups with these outputs.</summary>
        public void Explorer(params string[] utxos) =>
            _handler.Body = "[" + string.Join(",", utxos) + "]";

        /// <summary>Answers explorer lookups with a body of the test's own.</summary>
        public void ExplorerBody(string body) => _handler.Body = body;

        /// <summary>An explorer that refuses to connect: an air-gapped or misconfigured host.</summary>
        public void ExplorerOffline() => _handler.Offline = true;

        /// <summary>An explorer that answers, badly.</summary>
        public void ExplorerFails(HttpStatusCode status = HttpStatusCode.ServiceUnavailable) =>
            _handler.Status = status;

        /// <summary>
        /// An exit already in storage, as a quote would have left it.
        /// </summary>
        /// <remarks>
        /// The insert is asserted rather than ignored. The store refuses a second active exit for one store — the
        /// production unique index, reproduced in the fake — so a test that seeded two of them would otherwise
        /// carry on against a row that was never stored.
        /// </remarks>
        public UnilateralExitRecord Seed(
            string? id = null,
            string? storeId = null,
            UnilateralExitStatus status = UnilateralExitStatus.AwaitingFunding,
            string[]? leafIds = null,
            long singleUtxoFundingSat = 4_200,
            string? fundingAddress = null,
            string? lastError = null,
            int minutesOld = 0,
            long fundingKeyIndex = 0)
        {
            var record = new UnilateralExitRecord
            {
                Id = id ?? "exit-" + Guid.NewGuid().ToString("N"),
                StoreId = storeId ?? StoreId,
                Status = status,
                CreatedUtc = Now.AddMinutes(-minutesOld),
                UpdatedUtc = Now.AddMinutes(-minutesOld),
                DestinationAddress = Destination,
                FeeRateSatPerVbyte = 10,
                LeafIdsJson = JsonSerializer.Serialize(leafIds ?? ["leaf-a"]),
                RecoverableValueSat = 500_000,
                TotalFeeSat = 3_000,
                SingleUtxoFundingSat = singleUtxoFundingSat,
                FundingAddress = fundingAddress
                                 ?? FundingAddressFor(fundingKeyIndex),
                FundingKeyIndex = fundingKeyIndex,
                LastError = lastError
            };

            var created = Records.CreateAsync(record, CancellationToken.None).GetAwaiter().GetResult();
            Assert.True(created, "the seeded exit was refused by the store");
            return record;
        }

        /// <summary>
        /// The funding address <see cref="Mnemonic"/> derives at one index on regtest.
        /// </summary>
        /// <remarks>
        /// Derived rather than pinned for indexes other than zero, which is the one index worth pinning (see
        /// <see cref="FundingAddress"/>): a seeded row has to agree with what the build re-derives, or every test
        /// at a non-zero index would refuse on the address-mismatch guard instead of testing what it meant to.
        /// </remarks>
        private static string FundingAddressFor(long index)
        {
            if (index == 0)
                return FundingAddress;

            Assert.True(SparkExitFundingKey.TryDerive(
                Mnemonic, Network.RegTest, (uint)index, out var key, out _));
            using (key)
            {
                return key!.Address;
            }
        }

        public void Dispose() => Environment.SetEnvironmentVariable(Variable, _previous);

        /// <summary>
        /// An esplora endpoint a test can change after the service has been built.
        /// </summary>
        /// <remarks>
        /// Its own handler rather than the suite's <c>StubHttpMessageHandler</c>, whose response is fixed at
        /// construction: the funding on the address is arranged per test, and often after the record it belongs to
        /// exists.
        /// </remarks>
        private sealed class ExplorerHandler : HttpMessageHandler
        {
            public string Body { get; set; } = "[]";

            public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

            public bool Offline { get; set; }

            public int Requests { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Requests++;

                if (Offline)
                {
                    return Task.FromException<HttpResponseMessage>(
                        new HttpRequestException("no route to host"));
                }

                return Task.FromResult(new HttpResponseMessage(Status)
                {
                    Content = new StringContent(Body, System.Text.Encoding.UTF8, "application/json")
                });
            }
        }

        private sealed class ExplorerClientFactory : IHttpClientFactory
        {
            private readonly HttpMessageHandler _handler;

            public ExplorerClientFactory(HttpMessageHandler handler) => _handler = handler;

            public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
        }
    }
}

/// <summary>
/// Serialises everything that toggles the unilateral-exit feature gate.
/// </summary>
/// <remarks>
/// The gate is an environment variable, and an environment variable is shared by every test in the process. A
/// class that flips it while another reads it produces a failure that reproduces about once a week, which is the
/// worst kind — so the collection is not parallelised against the rest of the suite.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UnilateralExitTestCollection
{
    public const string Name = "UnilateralExitFeatureGate";
}

/// <summary>
/// The record-store contract, asserted against the in-memory fake the service tests run on.
/// </summary>
/// <remarks>
/// Lives beside those tests because the fake arrived with them. The point is stated in
/// <c>UnilateralExitRecordStoreContractTests</c>: the service tests are worthless if this store and the
/// production one disagree, and the disagreement that would matter most — an update that quietly rewrites the
/// destination or the leaf set an operator funded against — is one no service test could see.
/// </remarks>
public class InMemoryUnilateralExitRecordStoreTests : UnilateralExitRecordStoreContractTests
{
    protected override Task<IUnilateralExitRecordStore> CreateStoreAsync() =>
        Task.FromResult<IUnilateralExitRecordStore>(new InMemoryUnilateralExitRecordStore());
}
