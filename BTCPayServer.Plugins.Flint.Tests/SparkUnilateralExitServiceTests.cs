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
/// </remarks>
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

    /// <summary>The fan-out a first build's funding became once it confirmed.</summary>
    private const string FanoutTxid =
        "f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0";

    /// <summary>A CPFP child's change output, still on the funding script.</summary>
    private const string ChangeTxid =
        "c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4";

    /// <summary>A second payment the operator sent to the funding address.</summary>
    private const string TopUpTxid =
        "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();

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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var transactions = StoredSet(stored.TransactionsJson);
        Assert.Equal(4, transactions.Length);
        Assert.Equal(SparkExitTxKind.Fanout, transactions[0].Kind);
        Assert.Equal(SparkExitTxKind.Sweep, transactions[^1].Kind);
        // Readiness round-trips through the column as its own enum, which is what the page switches on to decide
        // what to tell an operator to broadcast. The default fake set is Ready, so every transaction is
        // broadcastable — the state a fresh build is in immediately after the fan-out confirms.
        Assert.Equal(SparkExitTxReadiness.Ready, transactions[0].Status.Readiness);

        var node = transactions.First(tx => tx.Kind is SparkExitTxKind.TreeNode);
        Assert.True(node.RequiresPackageBroadcast);
        Assert.Equal(1_008u, node.CsvTimelockBlocks!.Value);
        Assert.Equal([FakeSparkSdkClient.ExitTxid("fanout")], node.DependsOn);

        // The build spent exactly the one output it was funded with, and it had a key for it.
        var call = Assert.Single(harness.Sdk.ExitBuildCalls);
        Assert.Equal(FundingTxid, Assert.Single(call.FundingUtxos).Txid);
        Assert.Equal(32, call.FundingSecretKeyLength);
        Assert.Null(call.Rejection);
    }

    /// <summary>
    /// A recovery of nothing but already-on-chain leaves builds with no funding, no signing key and no explorer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is SDK 0.28's cooperative half: a leaf the operators took on-chain is paid out by one transaction they
    /// co-sign, and that transaction pays its own fee out of the value it recovers. So there is nothing to fund —
    /// and, the part that would break quietly, nothing to look up: the build must not consult the esplora API for
    /// a funding address nothing will spend, because then a store with no explorer configured could not recover
    /// funds that need no explorer.
    /// </para>
    /// <para>
    /// The signing key is asserted at zero length rather than merely "not used": the client materialises the
    /// key only when the quote asks for funding, and handing the SDK a signer alongside no funding inputs is
    /// exactly the combination its contract rejects.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_recovery_of_only_cooperative_leaves_builds_without_funding_or_an_explorer()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true, esploraApiUrl: null);
        harness.WithCooperativeLeaves(("leaf-onchain", 40_000));
        var record = harness.Seed(leafIds: ["leaf-onchain"], singleUtxoFundingSat: 0);

        var result = await harness.Service.BuildAsync(StoreId, record.Id, Ct);

        Assert.True(result.Success, result.Error);

        var call = Assert.Single(harness.Sdk.ExitBuildCalls);
        Assert.Empty(call.FundingUtxos);
        Assert.Equal(0, call.FundingSecretKeyLength);
        Assert.Null(call.Rejection);

        // Nothing was asked of the chain: the whole build came out of the SDK's local state.
        Assert.Equal(0, harness.ExplorerRequests);

        var stored = harness.Records.Records[record.Id];
        Assert.Equal(UnilateralExitStatus.Built, stored.Status);
        Assert.False(stored.RequiresFunding);

        var transactions = StoredSet(stored.TransactionsJson);
        var cooperative = Assert.Single(transactions, tx => tx.Kind is SparkExitTxKind.Cooperative);
        // It pays its own fee, so it carries no child to package with and waits for nothing.
        Assert.False(cooperative.RequiresPackageBroadcast);
        Assert.Null(cooperative.CsvTimelockBlocks);
        Assert.Empty(cooperative.DependsOn);
    }

    /// <summary>
    /// A cooperative leaf the operators refused is kept beside the set it could not recover, with the reason.
    /// </summary>
    /// <remarks>
    /// The build is not atomic across leaves, so a refusal leaves everything else signed and stored while one
    /// leaf still needs an attempt of its own. Dropping that fact would turn a partial recovery into a
    /// complete-looking one — and it has to survive a page reload, because the operator who reads it may come
    /// back days later, long after the press that produced it.
    /// </remarks>
    [Fact]
    public async Task A_refused_cooperative_leaf_is_persisted_with_its_reason()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true, esploraApiUrl: null);
        harness.WithCooperativeLeaves(("leaf-a", 40_000), ("leaf-b", 25_000));
        harness.Sdk.ExitRefusedLeaves.Add(new SparkExitFailedLeaf(
            "leaf-b", "the operators could not co-sign it"));
        var record = harness.Seed(leafIds: ["leaf-a", "leaf-b"], singleUtxoFundingSat: 0);

        var built = await harness.Service.BuildAsync(StoreId, record.Id, Ct);
        Assert.True(built.Success, built.Error);

        var page = await harness.Service.ReadAsync(StoreId, Ct);

        var refused = Assert.Single(page.FailedLeaves!);
        Assert.Equal("leaf-b", refused.LeafId);
        Assert.Contains("co-sign", refused.Reason);

        // The rest of the recovery is still a recovery: the transactions the operator has to broadcast are on
        // the row, and the refused leaf did not take them with it.
        Assert.NotEmpty(page.Transactions!);
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
    /// The SDK's own funding shortfall arrives as readable copy on the record rather than as an exception.
    /// </summary>
    /// <remarks>
    /// It means the operator has something to do — top up the funding address — and it leaves the exit exactly
    /// where it was, because nothing was built. The number the SDK named is what makes that copy actionable.
    /// </remarks>
    [Fact]
    public async Task The_SDK_s_funding_failure_lands_on_the_record_as_words()
    {
        var harness = Harness.Create();
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
        Assert.Null(harness.Records.Records[shortfallRecord.Id].TransactionsJson);
    }

    /// <summary>A build against an unknown exit, or one that is finished, is refused.</summary>
    [Fact]
    public async Task Building_an_unknown_or_finished_exit_is_refused()
    {
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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

    /// <summary>
    /// A build that comes back with no transactions at all is a successful build, not a refusal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the semantic change of the SDK bump, and the one a merchant would notice.</b> In 0.25 the SDK
    /// reads confirmed chain state before building, so a resumed build whose steps have already gone out and
    /// confirmed has nothing left to hand back — while the leaves it was pinned to are still perfectly well
    /// present in the wallet. That is a successful build with an empty set, and it replaces the stored set.
    /// </para>
    /// <para>
    /// The harm in getting it wrong is a trap with no way out: the exit has been force-closed on chain, the SDK
    /// says there is nothing left to do, and a plugin that reads that as "the build failed" leaves the record at
    /// <c>AwaitingFunding</c> asking the operator to fund an exit that is already done. It would also keep the
    /// previous attempt's complaint on the row, so the page would show a stale error next to a finished exit.
    /// </para>
    /// <para>
    /// The empty set is deliberately not the same condition as an empty <em>quote</em> — the leaves are still
    /// pinned and still in the wallet here, which is what tells the two apart. See
    /// <c>A_build_whose_leaves_have_vanished_is_refused_before_anything_is_signed</c> for the other side.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_build_that_returns_no_transactions_is_a_successful_build()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        var record = harness.Seed(
            leafIds: ["leaf-a"], singleUtxoFundingSat: 4_200, lastError: "not enough on the funding address");
        harness.Explorer(Utxo(10_000));

        // The leaves are exactly where they were; what has changed is that the chain already has every step
        // this exit planned, so the SDK has nothing left to sign.
        harness.Sdk.ExitBuildsNoTransactions = true;

        var result = await harness.Service.BuildAsync(StoreId, record.Id, Ct);

        Assert.True(result.Success, result.Error);

        var stored = harness.Records.Records[record.Id];
        Assert.Equal(UnilateralExitStatus.Built, stored.Status);
        // An empty array rather than null: "built, with nothing left to broadcast" and "never built" are
        // different states and the page renders them differently.
        Assert.Empty(StoredSet(stored.TransactionsJson));
        // The previous attempt's complaint does not sit next to a successful build.
        Assert.Null(stored.LastError);

        // And it reads back as what it is — an empty set — rather than as a damaged one. Reporting it unreadable
        // told the operator their only copy of the exit was broken and made the page's empty branch unreachable.
        var page = await harness.Service.ReadAsync(StoreId, Ct);
        Assert.False(page.TransactionsUnreadable);
        Assert.NotNull(page.Transactions);
        Assert.Empty(page.Transactions);
        Assert.Null(page.PendingBroadcast);

        // A check has nothing to ask the chain about, says so, and does not write that down as a failure.
        var check = await harness.Service.CheckAsync(StoreId, record.Id, Ct);
        Assert.False(check.Success);
        Assert.Equal(SparkUnilateralExitService.EmptySetNothingToCheck, check.Error);
        Assert.Empty(harness.Sdk.ExitCheckCalls);
        Assert.Null(harness.Records.Records[record.Id].LastError);
    }

    /// <summary>
    /// A build that returns fewer transactions than the stored set replaces it rather than merging into it.
    /// </summary>
    /// <remarks>
    /// <b>A merge here would be actively dangerous rather than merely untidy.</b> The transaction a later build
    /// leaves out is one the chain says is already confirmed or already superseded; keeping it in the stored set
    /// means the page keeps rendering it as a step to broadcast, and an operator who broadcasts a stale
    /// transaction from a superseded exit is publishing a transaction that competes with, or spends an output
    /// already claimed by, the exit that actually went through. The statuses would also be a mixture of two
    /// different reads of the chain, which is a set no single moment ever produced.
    /// </remarks>
    [Fact]
    public async Task A_partial_rebuild_replaces_the_stored_set_rather_than_merging_with_it()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 300_000), ("leaf-b", 200_000));
        var record = harness.Seed(leafIds: ["leaf-a", "leaf-b"], singleUtxoFundingSat: 4_200);
        harness.Explorer(Utxo(10_000));

        Assert.True((await harness.Service.BuildAsync(StoreId, record.Id, Ct)).Success);
        var first = StoredSet(harness.Records.Records[record.Id].TransactionsJson);
        Assert.Equal(4, first.Length);
        Assert.Contains(first, tx => tx.Txid == FakeSparkSdkClient.ExitTxid("node:leaf-b"));

        // leaf-b's exit is over on chain — its refund was swept — so the next build has nothing to do for it and
        // the SDK says so, which is what separates this from a leaf that went missing.
        harness.Sdk.ExitFinishedLeafIds.Add("leaf-b");

        Assert.True((await harness.Service.BuildAsync(StoreId, record.Id, Ct)).Success);

        var second = StoredSet(harness.Records.Records[record.Id].TransactionsJson);

        Assert.Equal(3, second.Length);
        Assert.DoesNotContain(second, tx => tx.Txid == FakeSparkSdkClient.ExitTxid("node:leaf-b"));
        // The transaction the rebuild dropped is gone from the row, not left behind as a step to broadcast.
        Assert.NotEqual(4, second.Length);
        Assert.Contains(second, tx => tx.Txid == FakeSparkSdkClient.ExitTxid("node:leaf-a"));
    }

    #endregion

    #region The pinned leaves

    /// <summary>
    /// A build whose re-quote no longer covers every pinned leaf refuses, rather than building a smaller exit
    /// out of the same funding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The SDK skips a named leaf that is missing from its local storage without a word
    /// (<c>load_exit_tree_nodes</c>: "cannot exit it"), and nothing freezes the leaves an exit is pinned to: a
    /// Lightning payment, a payout, a Stable Balance conversion or the SDK's own leaf optimisation can spend or
    /// reshape one between the quote and the build. The quote that comes back is then a smaller exit, and a
    /// build that accepted any subset whose value beat its fee would commit the operator's funding to
    /// recovering less than they were shown.
    /// </para>
    /// <para>
    /// Refused before funding is looked at, so nothing is committed; the funding key path is named because the
    /// operator's way out is to abandon and recover what they sent.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_build_refuses_when_a_pinned_leaf_has_gone_missing()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 300_000));
        var record = harness.Seed(leafIds: ["leaf-a", "leaf-b"], singleUtxoFundingSat: 4_200);
        harness.Explorer(Utxo(10_000));

        var result = await harness.Service.BuildAsync(StoreId, record.Id, Ct);

        Assert.False(result.Success);
        Assert.Contains("1 of the 2 leaves", result.Error);
        Assert.Contains("m/84'/1'/4607060'/0/0", result.Error);
        Assert.Empty(harness.Sdk.ExitBuildCalls);
        Assert.Null(harness.Records.Records[record.Id].FundingUtxosJson);
    }

    /// <summary>
    /// The same refusal holds against the quote the build itself commits to, which can differ from the one just
    /// taken.
    /// </summary>
    [Fact]
    public async Task The_build_s_own_quote_is_held_to_the_pinned_leaves_too()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 300_000), ("leaf-b", 200_000));
        var record = harness.Seed(leafIds: ["leaf-a", "leaf-b"], singleUtxoFundingSat: 4_200);
        harness.Explorer(Utxo(10_000));

        // leaf-b is spent between the step-one re-quote and the quote the build commits to.
        var quotes = 0;
        harness.Sdk.WhenExitQuoted = () =>
        {
            if (++quotes == 1)
                harness.Sdk.ExitLeaves.RemoveAll(leaf => leaf.LeafId == "leaf-b");
        };

        var result = await harness.Service.BuildAsync(StoreId, record.Id, Ct);

        Assert.False(result.Success);
        Assert.Contains("1 of the 2 leaves", result.Error);
        // The veto refused it: the build was reached, and signed nothing.
        var call = Assert.Single(harness.Sdk.ExitBuildCalls);
        Assert.NotNull(call.Rejection);
        Assert.Equal(UnilateralExitStatus.AwaitingFunding, harness.Records.Records[record.Id].Status);
    }

    /// <summary>
    /// A pinned leaf whose exit is over on-chain is the one legitimate absence, and an exit whose every leaf is
    /// over says so instead of calling the leaves gone.
    /// </summary>
    [Fact]
    public async Task Leaves_that_finished_on_chain_are_not_mistaken_for_missing_ones()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        var record = await BuiltExit(harness);

        harness.Sdk.ExitFinishedLeafIds.Add("leaf-a");

        var result = await harness.Service.BuildAsync(StoreId, record.Id, Ct);

        Assert.False(result.Success);
        Assert.Equal(SparkUnilateralExitService.AllLeavesFinished, result.Error);
        Assert.NotEqual(SparkUnilateralExitService.LeavesGone, result.Error);
    }

    #endregion

    #region Rebuilding: funding a second attempt

    /// <summary>
    /// A rebuild hands the funding the first build committed back to the SDK, which follows it, instead of
    /// re-listing the address and asking for a second full funding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The scenario the SDK's "Funding a second attempt" exists for, and the one the page sends operators
    /// into.</b> A multi-leaf exit is built, its fan-out is broadcast and confirms, and a later check says Redo.
    /// The output the operator sent no longer exists: it became one fee output per branch, each well below a
    /// whole-exit requirement. Re-listing the address and picking the one biggest output — what every build used
    /// to do — finds nothing big enough and tells the operator to send the whole amount again, stranding the
    /// per-branch outputs on an address no wallet scans.
    /// </para>
    /// <para>
    /// The SDK's contract is to pass back the funding the stored response carries; it follows each outpoint to
    /// what it became and funds from there. So the assertion is on what reached the SDK: the committed outpoint,
    /// alongside what the address now shows.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_rebuild_hands_the_committed_funding_back_to_be_followed()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 300_000), ("leaf-b", 200_000));
        var record = harness.Seed(leafIds: ["leaf-a", "leaf-b"], singleUtxoFundingSat: 4_200);
        harness.Explorer(Utxo(10_000));

        Assert.True((await harness.Service.BuildAsync(StoreId, record.Id, Ct)).Success);
        var original = $"{FundingTxid}:0";
        Assert.Contains(original, harness.Records.Records[record.Id].FundingUtxosJson);

        // The fan-out confirmed: the operator's 10,000 sat output is spent, and what it became is two 4,000 sat
        // branch outputs on the same script. A rebuild at a higher requirement than either single one of them.
        harness.Sdk.ExitFundingSpentInto[original] =
        [
            new SparkExitFundingUtxo(FanoutTxid, 0, 4_000, "pubkey"),
            new SparkExitFundingUtxo(FanoutTxid, 1, 4_000, "pubkey")
        ];
        harness.Explorer(UtxoAt(FanoutTxid, 4_000, 0), UtxoAt(FanoutTxid, 4_000, 1));
        harness.Sdk.ExitSingleUtxoFundingSat = 6_000;
        var buildsBefore = harness.Sdk.ExitBuildCalls.Count;

        var rebuilt = await harness.Service.BuildAsync(StoreId, record.Id, Ct);

        Assert.True(rebuilt.Success, rebuilt.Error);
        var call = harness.Sdk.ExitBuildCalls[buildsBefore];
        // The committed outpoint went back, which is the whole fix: the SDK follows it.
        Assert.Contains(call.FundingUtxos, utxo => utxo.OutPoint == original);
        // And what the address shows went alongside, once each.
        Assert.Equal(3, call.FundingUtxos.Select(utxo => utxo.OutPoint).Distinct().Count());
        Assert.Equal(3, call.FundingUtxos.Count);
        Assert.All(call.FundingUtxos, utxo => Assert.NotEqual("pubkey", utxo.PubkeyHex));

        // The record remembers everything it was handed, so the next attempt can follow all of it again.
        var stored = JsonSerializer.Deserialize<SparkExitFundingUtxo[]>(
            harness.Records.Records[record.Id].FundingUtxosJson!)!;
        Assert.Equal(
            call.FundingUtxos.Select(utxo => utxo.OutPoint).Order(StringComparer.Ordinal),
            stored.Select(utxo => utxo.OutPoint).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A top-up sent to the funding address reaches a rebuild alongside the committed funding, and a rebuild
    /// that is still short says to send more — not to send the whole amount again as one output.
    /// </summary>
    /// <remarks>
    /// A first build funds from one output, so its shortfall has to say "topping up does not help". A rebuild is
    /// the opposite: the SDK sums what the committed funding has become plus anything else it is handed, so more
    /// of any shape helps, and the page's own advice for a fee that turned out too low — send more and build
    /// again — is only true if the new output is actually passed in.
    /// </remarks>
    [Fact]
    public async Task A_rebuild_takes_a_top_up_alongside_the_committed_funding()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        var record = await BuiltExit(harness);
        var original = $"{FundingTxid}:0";
        var builtFee = harness.Records.Records[record.Id].TotalFeeSat;

        // What is left of the first funding after its children confirmed: 1,000 sat of change.
        harness.Sdk.ExitFundingSpentInto[original] = [new SparkExitFundingUtxo(ChangeTxid, 0, 1_000, "pubkey")];
        harness.Explorer(UtxoAt(ChangeTxid, 1_000));
        harness.Sdk.ExitSingleUtxoFundingSat = 5_000;

        var short1 = await harness.Service.BuildAsync(StoreId, record.Id, Ct);

        Assert.False(short1.Success);
        Assert.Contains("Spark followed the funding", short1.Error);
        Assert.Contains("5,000 sat", short1.Error);
        Assert.DoesNotContain("topping up does not help", short1.Error);
        // A built exit stays built, and a failed rebuild leaves the figures of the set that is actually stored.
        var afterShort = harness.Records.Records[record.Id];
        Assert.Equal(UnilateralExitStatus.Built, afterShort.Status);
        Assert.Equal(builtFee, afterShort.TotalFeeSat);
        Assert.Equal(short1.Error, afterShort.LastError);

        // The operator sends 4,500 more as a new output. 1,000 + 4,500 covers the 5,000 the SDK asked for, and
        // no single output does.
        harness.Explorer(UtxoAt(ChangeTxid, 1_000), UtxoAt(TopUpTxid, 4_500));

        var rebuilt = await harness.Service.BuildAsync(StoreId, record.Id, Ct);

        Assert.True(rebuilt.Success, rebuilt.Error);
        var call = harness.Sdk.ExitBuildCalls[^1];
        Assert.Contains(call.FundingUtxos, utxo => utxo.OutPoint == original);
        Assert.Contains(call.FundingUtxos, utxo => utxo.OutPoint == $"{TopUpTxid}:0");
        Assert.Null(harness.Records.Records[record.Id].LastError);
    }

    /// <summary>A rebuild does not need the explorer: the committed funding is followed by the SDK itself.</summary>
    /// <remarks>
    /// The explorer is how a first build finds the output the operator sent. A rebuild already holds its
    /// outpoints, and the SDK reads the chain through its own chain service to follow them — so an explorer
    /// outage must not block a Redo that needs no new money, and when it does turn out short the message says
    /// that a top-up already on the address may not have been seen.
    /// </remarks>
    [Fact]
    public async Task A_rebuild_goes_ahead_on_the_committed_funding_when_the_explorer_is_down()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        var record = await BuiltExit(harness);
        harness.ExplorerOffline();

        var rebuilt = await harness.Service.BuildAsync(StoreId, record.Id, Ct);

        Assert.True(rebuilt.Success, rebuilt.Error);
        var call = harness.Sdk.ExitBuildCalls[^1];
        Assert.Equal([$"{FundingTxid}:0"], call.FundingUtxos.Select(utxo => utxo.OutPoint));

        harness.Sdk.ExitSingleUtxoFundingSat = 50_000;
        var shortfall = await harness.Service.BuildAsync(StoreId, record.Id, Ct);

        Assert.False(shortfall.Success);
        Assert.Contains("block explorer could not be read", shortfall.Error);
    }

    /// <summary>
    /// A committed-funding column that is present but unreadable stops a rebuild before anything is signed.
    /// </summary>
    /// <remarks>
    /// Building without it is the old failure in disguise: the SDK would be handed only whatever the address
    /// lists, and the operator asked to fund the exit a second time. The refusal names the key path, because
    /// that is how whatever is left on the address is recovered by hand.
    /// </remarks>
    [Theory]
    [InlineData("not json")]
    [InlineData("[null]")]
    [InlineData("""[{"Txid":"not-a-txid","Vout":0,"ValueSat":1000,"PubkeyHex":"02"}]""")]
    public async Task An_unreadable_committed_funding_column_refuses_a_rebuild(string stored)
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        var record = await BuiltExit(harness);
        harness.Records.Records[record.Id].FundingUtxosJson = stored;
        var buildsBefore = harness.Sdk.ExitBuildCalls.Count;

        var rebuilt = await harness.Service.BuildAsync(StoreId, record.Id, Ct);

        Assert.False(rebuilt.Success);
        Assert.Contains("funding it has already committed could not be read", rebuilt.Error);
        Assert.Contains("m/84'/1'/4607060'/0/0", rebuilt.Error);
        Assert.Equal(buildsBefore, harness.Sdk.ExitBuildCalls.Count);
        Assert.Equal(UnilateralExitStatus.Built, harness.Records.Records[record.Id].Status);
    }

    #endregion

    #region Raising the fee

    /// <summary>
    /// A rebuild at a higher rate re-prices the exit at that rate, signs at it, and records it with the new set.
    /// </summary>
    /// <remarks>
    /// The SDK's only fee bump is to quote again at the higher rate and build again: what has confirmed stays,
    /// and what has not is rebuilt at the new rate and replaces the earlier version on the network. A page that
    /// told the operator to "send more funding and build again" while every build reused the record's own rate
    /// was describing a fee bump that could not happen.
    /// </remarks>
    [Fact]
    public async Task A_rebuild_at_a_higher_rate_signs_at_it_and_records_it()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        var record = await BuiltExit(harness);
        Assert.Equal(10, harness.Records.Records[record.Id].FeeRateSatPerVbyte);
        var quotesBefore = harness.Sdk.ExitQuoteCalls.Count;

        var bumped = await harness.Service.BuildAsync(StoreId, record.Id, 25, Ct);

        Assert.True(bumped.Success, bumped.Error);
        // Both the re-quote and the build's own quote are at the new rate: the SDK signs what its quote priced.
        Assert.All(harness.Sdk.ExitQuoteCalls.Skip(quotesBefore), call => Assert.Equal(25UL, call.FeeRateSatPerVbyte));
        Assert.Equal(25UL, harness.Sdk.ExitBuildCalls[^1].FeeRateSatPerVbyte);
        Assert.Equal(25, harness.Records.Records[record.Id].FeeRateSatPerVbyte);

        // And it is the record's rate from now on, so a plain "build again" does not quietly fall back.
        Assert.True((await harness.Service.BuildAsync(StoreId, record.Id, Ct)).Success);
        Assert.Equal(25UL, harness.Sdk.ExitBuildCalls[^1].FeeRateSatPerVbyte);
    }

    /// <summary>
    /// A built exit refuses a rebuild at a lower rate, before anything is asked of the SDK.
    /// </summary>
    /// <remarks>
    /// A replacement has to pay a higher rate than what it replaces to relay, so a cheaper set could never
    /// displace a child already in mempools — the operator would hold two signed sets and only the old one could
    /// ever confirm. The refusal is not written to the row: nothing about the exit is wrong, the request was.
    /// </remarks>
    [Fact]
    public async Task A_built_exit_refuses_a_lower_rate()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        var record = await BuiltExit(harness);
        var quotesBefore = harness.Sdk.ExitQuoteCalls.Count;

        var lower = await harness.Service.BuildAsync(StoreId, record.Id, 5, Ct);

        Assert.False(lower.Success);
        Assert.Contains("cannot go lower", lower.Error);
        Assert.Equal(quotesBefore, harness.Sdk.ExitQuoteCalls.Count);
        var stored = harness.Records.Records[record.Id];
        Assert.Equal(10, stored.FeeRateSatPerVbyte);
        Assert.Null(stored.LastError);

        // Out of the quote band entirely is the quote's own refusal, word for word.
        var absurd = await harness.Service.BuildAsync(StoreId, record.Id, 5_000, Ct);
        Assert.Equal(SparkUnilateralExitService.FeeRateOutOfRange, absurd.Error);
    }

    /// <summary>
    /// A fee bump that fails leaves the stored set's rate and figures exactly as they were.
    /// </summary>
    /// <remarks>
    /// The page prints the rate beside the transactions, and those transactions were signed at the old one. A
    /// bump that could not be funded must not leave the row claiming a rate nothing on it was built at.
    /// </remarks>
    [Fact]
    public async Task A_fee_bump_that_fails_leaves_the_built_rate_and_figures_alone()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        var record = await BuiltExit(harness);
        var before = harness.Records.Records[record.Id];
        var (fee, funding, transactions) = (before.TotalFeeSat, before.SingleUtxoFundingSat, before.TransactionsJson);

        harness.Sdk.ExitSingleUtxoFundingSat = 90_000;
        harness.Sdk.ExitTotalFeeSat = 60_000;

        var bumped = await harness.Service.BuildAsync(StoreId, record.Id, 40, Ct);

        Assert.False(bumped.Success);
        var after = harness.Records.Records[record.Id];
        Assert.Equal(UnilateralExitStatus.Built, after.Status);
        Assert.Equal(10, after.FeeRateSatPerVbyte);
        Assert.Equal(fee, after.TotalFeeSat);
        Assert.Equal(funding, after.SingleUtxoFundingSat);
        Assert.Equal(transactions, after.TransactionsJson);
        Assert.Equal(bumped.Error, after.LastError);
    }

    /// <summary>
    /// An exit still awaiting funding can be built at a different rate, and the new rate lands with the new
    /// requirement even when the funding turns out short.
    /// </summary>
    /// <remarks>
    /// Before a first build nothing has been broadcast, so there is nothing to replace and no floor: the rate is
    /// simply the one this exit will be built at. It is persisted with the fresh quote for the reason that quote
    /// is persisted at all — the requirement the page shows has to be the one the next attempt is judged by.
    /// </remarks>
    [Fact]
    public async Task An_exit_awaiting_funding_takes_a_new_rate_with_its_requirement()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        var record = harness.Seed(leafIds: ["leaf-a"], singleUtxoFundingSat: 4_200);
        harness.Sdk.ExitSingleUtxoFundingSat = 6_000;
        harness.Explorer(Utxo(5_000));

        var lower = await harness.Service.BuildAsync(StoreId, record.Id, 3, Ct);

        Assert.False(lower.Success);
        var stored = harness.Records.Records[record.Id];
        Assert.Equal(3, stored.FeeRateSatPerVbyte);
        Assert.Equal(6_000, stored.SingleUtxoFundingSat);
        Assert.Equal(UnilateralExitStatus.AwaitingFunding, stored.Status);
    }

    #endregion

    #region Checking a built exit against the chain

    /// <summary>
    /// Checking refreshes the stored statuses from what the chain reports.
    /// </summary>
    /// <remarks>
    /// <b>The stored statuses are the only thing that turns "here are twelve transactions" into a schedule.</b>
    /// A built set is a snapshot of a chain that has since moved: the fan-out has confirmed, a node's timelock
    /// has matured, the sweep is still waiting. An operator who reloads the page has to be told which of those
    /// is true now, and the only source is the SDK's own read — so a check that answers without writing the
    /// refreshed set back leaves the page rendering yesterday's readiness for ever, which is how an operator ends
    /// up rebroadcasting a transaction that is already mined or waiting on one that is not.
    /// </remarks>
    [Fact]
    public async Task Checking_a_built_exit_persists_the_refreshed_transactions()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        var record = await BuiltExit(harness);

        // The chain has moved since the build: everything is now waiting on the fan-out's confirmation.
        harness.Sdk.ExitReadiness = SparkExitTxReadiness.Waiting;
        harness.Sdk.ExitSpendableAtHeight = 810_000;

        var result = await harness.Service.CheckAsync(StoreId, record.Id, Ct);

        Assert.True(result.Success, result.Error);
        Assert.Single(harness.Sdk.ExitCheckCalls);

        var refreshed = StoredSet(harness.Records.Records[record.Id].TransactionsJson);
        Assert.All(
            refreshed,
            tx => Assert.Equal(SparkExitTxReadiness.Waiting, tx.Status.Readiness));
        // The height that makes "not yet" a number travels with the status, so the page can say when.
        Assert.Equal(810_000u, refreshed[0].Status.SpendableAtHeight);
    }

    /// <summary>
    /// The verdict comes back on the result and is deliberately not written to the row.
    /// </summary>
    /// <remarks>
    /// The verdict is the SDK's reading of the chain at the instant of the call. Stored, it would be rendered
    /// later as a live claim about a chain nothing has re-read — an operator would see "on track" on a page
    /// served from a row written days ago. The refreshed <em>transactions</em> are stored because they are what
    /// the operator broadcasts against; the verdict is not, because it is only ever an answer to a question
    /// somebody just asked.
    /// </remarks>
    [Fact]
    public async Task The_check_verdict_is_reported_but_not_persisted()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        var record = await BuiltExit(harness);

        // The SDK's own scripted progression: this refresh says on track, the next says finished.
        harness.Sdk.CheckVerdict = SparkExitVerdict.Done;

        var done = await harness.Service.CheckAsync(StoreId, record.Id, Ct);

        Assert.True(done.Success, done.Error);
        Assert.Equal(SparkExitVerdict.Done, done.Verdict);

        // Nothing on the row carries it: the record's own columns are about the exit, not about the last time
        // anybody asked the chain.
        var stored = harness.Records.Records[record.Id];
        Assert.Equal(UnilateralExitStatus.Built, stored.Status);
        Assert.Null(stored.LastError);

        // And the next call asks again rather than replaying the stored answer.
        harness.Sdk.CheckVerdict = SparkExitVerdict.Redo;
        var redo = await harness.Service.CheckAsync(StoreId, record.Id, Ct);
        Assert.Equal(SparkExitVerdict.Redo, redo.Verdict);
        Assert.Equal(2, harness.Sdk.ExitCheckCalls.Count);
    }

    /// <summary>
    /// Checking an exit that was never built is refused: there is nothing on chain to ask about.
    /// </summary>
    /// <remarks>
    /// <b>The refusal is what keeps the verdict honest.</b> A check on an unbuilt row has no transaction set to
    /// hand the SDK, and the SDK judges the exit it is given — so answering it at all would mean either inventing
    /// a set or reporting a verdict about an exit that does not exist. Either way the page would show a chain
    /// verdict next to an exit that has not been built, and an operator would read "on track" as "the funding
    /// arrived and it is working".
    /// </remarks>
    [Fact]
    public async Task Checking_an_exit_that_is_not_built_is_refused()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));

        var awaiting = harness.Seed(id: "exit-waiting", leafIds: ["leaf-a"]);
        var notBuilt = await harness.Service.CheckAsync(StoreId, awaiting.Id, Ct);

        Assert.False(notBuilt.Success);
        Assert.Contains("not been built", notBuilt.Error);
        Assert.Null(notBuilt.Verdict);

        var abandoned = harness.Seed(id: "exit-abandoned", status: UnilateralExitStatus.Abandoned);
        var gone = await harness.Service.CheckAsync(StoreId, abandoned.Id, Ct);

        Assert.False(gone.Success);
        Assert.Contains("abandoned", gone.Error);

        Assert.Equal(
            SparkUnilateralExitService.ExitNotFound,
            (await harness.Service.CheckAsync(StoreId, "exit-nowhere", Ct)).Error);

        // Nothing reached the SDK on the way to any of those refusals.
        Assert.Empty(harness.Sdk.ExitCheckCalls);
    }

    /// <summary>
    /// Checking builds nothing, signs nothing and spends nothing — it is a read of the chain.
    /// </summary>
    /// <remarks>
    /// <b>This is the property that makes the button safe to press as often as an operator likes.</b> A check
    /// that took a quote, re-priced the leaves or asked the SDK to build would commit a fresh funding output and
    /// produce a second signed set over an exit that is already part-way through being broadcast — and the
    /// operator would have no way to tell from the page that pressing "check" had signed anything at all. It must
    /// also work from a record alone: the whole reason the check exists is following an exit whose wallet is
    /// gone.
    /// </remarks>
    [Fact]
    public async Task Checking_a_built_exit_builds_and_spends_nothing()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        var record = await BuiltExit(harness);

        var quotesAfterBuild = harness.Sdk.ExitQuoteCalls.Count;
        var buildsAfterBuild = harness.Sdk.ExitBuildCalls.Count;

        var result = await harness.Service.CheckAsync(StoreId, record.Id, Ct);

        Assert.True(result.Success, result.Error);
        Assert.Single(harness.Sdk.ExitCheckCalls);
        // No re-price, no re-quote, no second build: the count is unchanged from what the build itself did.
        Assert.Equal(quotesAfterBuild, harness.Sdk.ExitQuoteCalls.Count);
        Assert.Equal(buildsAfterBuild, harness.Sdk.ExitBuildCalls.Count);
        // And it did not need the store's seed at all, which is what makes a lost wallet recoverable.
        harness.Settings.Settings[StoreId]!.ProtectedMnemonic = "not something this keyring can unprotect";
        Assert.True((await harness.Service.CheckAsync(StoreId, record.Id, Ct)).Success);
    }

    /// <summary>
    /// A check whose answer cannot be written lands as a refusal rather than as a silent success.
    /// </summary>
    /// <remarks>
    /// The refreshed set is the point of the call. Reporting success while the compare-and-set missed would tell
    /// an operator the page is showing what the chain says when the row still holds the build-time statuses —
    /// and the comparison is not paranoia: an abandon or a completion from another tab between the read and the
    /// write is exactly what it is there for.
    /// </remarks>
    [Fact]
    public async Task A_check_that_cannot_write_its_refresh_back_reports_a_refusal()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        var record = await BuiltExit(harness);

        harness.Records.RefuseUpdates = true;

        var result = await harness.Service.CheckAsync(StoreId, record.Id, Ct);

        Assert.False(result.Success);
        Assert.Equal(SparkUnilateralExitService.ExitChangedUnderneath, result.Error);
    }

    /// <summary>
    /// A check whose chain read blows up leaves the stored set exactly as it was.
    /// </summary>
    /// <remarks>
    /// "Spark could not read the chain" and "the chain says every transaction is waiting" are opposite answers,
    /// and an operator who acted on the second when the first was true would rebroadcast a set they have already
    /// broadcast. So a failed check writes nothing, and says so.
    /// </remarks>
    [Fact]
    public async Task A_check_that_fails_leaves_the_stored_set_untouched()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        var record = await BuiltExit(harness);
        var before = harness.Records.Records[record.Id].TransactionsJson;

        harness.Sdk.FailCheckWith = new InvalidOperationException("the chain service is down");

        var result = await harness.Service.CheckAsync(StoreId, record.Id, Ct);

        Assert.False(result.Success);
        Assert.Contains("could not read the chain", result.Error);
        Assert.Equal(before, harness.Records.Records[record.Id].TransactionsJson);
        Assert.Equal(
            UnilateralExitStatus.Built,
            harness.Records.Records[record.Id].Status);
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
    /// Marking a built exit completed frees the store, and it is the right verb for a finished exit — once the
    /// chain says it is finished.
    /// </summary>
    /// <remarks>
    /// Without it abandoning would be the only way a finished exit ever left the active state — and telling a
    /// merchant to "abandon" the exit that recovered their money is a lie the page would have to keep telling.
    /// The completion asks Spark's check first, and completes on its Done.
    /// </remarks>
    [Fact]
    public async Task Marking_a_built_exit_completed_frees_the_store()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        var record = await BuiltExit(harness);
        harness.Sdk.CheckVerdict = SparkExitVerdict.Done;
        harness.Sdk.ExitReadiness = SparkExitTxReadiness.Confirmed;

        var completed = await harness.Service.MarkCompletedAsync(StoreId, record.Id, Ct);

        Assert.True(completed.Success, completed.Error);
        Assert.Equal(SparkExitVerdict.Done, completed.Verdict);
        Assert.Single(harness.Sdk.ExitCheckCalls);
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        var record = harness.Seed(status: UnilateralExitStatus.Built);
        Assert.True((await harness.Service.MarkCompletedAsync(
            StoreId, record.Id, confirmedWithoutVerdict: true, Ct)).Success);

        var abandoned = await harness.Service.AbandonAsync(StoreId, record.Id, Ct);

        Assert.False(abandoned.Success);
        Assert.Contains("already recorded as finished", abandoned.Error);
        Assert.Equal(UnilateralExitStatus.Completed, harness.Records.Records[record.Id].Status);
    }

    /// <summary>
    /// An exit the chain does not call finished is not marked completed unless the operator says so themselves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Completing takes the signed set off the page — the only place it is shown — so completing early is how an
    /// operator loses sight of steps still to broadcast, and the SDK can tell. "On track" is not finished, and
    /// "can no longer finish" is not finished either: each gets its own refusal, because the next step differs.
    /// </para>
    /// <para>
    /// The override exists for the cases a check cannot answer. A stopped wallet is the obvious one: the check is
    /// made through the running SDK, so it refuses, and the operator who has watched the sweep confirm in their
    /// own node must still be able to close the record.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(SparkExitVerdict.Valid, "still on track")]
    [InlineData(SparkExitVerdict.Redo, "can no longer finish")]
    public async Task An_unfinished_exit_is_not_marked_completed_on_a_plain_press(
        SparkExitVerdict verdict, string expected)
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        var record = await BuiltExit(harness);
        harness.Sdk.CheckVerdict = verdict;

        var result = await harness.Service.MarkCompletedAsync(StoreId, record.Id, Ct);

        Assert.False(result.Success);
        Assert.Contains(expected, result.Error);
        Assert.Contains("tick the confirmation", result.Error);
        Assert.Equal(verdict, result.Verdict);
        Assert.Equal(UnilateralExitStatus.Built, harness.Records.Records[record.Id].Status);
    }

    [Fact]
    public async Task A_stopped_wallet_blocks_the_check_but_not_the_operator_s_own_confirmation()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        var record = await BuiltExit(harness);
        harness.Runtime.Clients.Remove(StoreId);

        var check = await harness.Service.CheckAsync(StoreId, record.Id, Ct);
        Assert.False(check.Success);
        // Said as what it is: a check needs the wallet. Not a chain failure, and not a claim that it works stopped.
        Assert.Equal(SparkUnilateralExitService.CheckNeedsWallet, check.Error);

        var plain = await harness.Service.MarkCompletedAsync(StoreId, record.Id, Ct);
        Assert.False(plain.Success);
        Assert.Contains(SparkUnilateralExitService.CheckNeedsWallet, plain.Error);
        Assert.Equal(UnilateralExitStatus.Built, harness.Records.Records[record.Id].Status);

        var confirmed = await harness.Service.MarkCompletedAsync(
            StoreId, record.Id, confirmedWithoutVerdict: true, Ct);
        Assert.True(confirmed.Success, confirmed.Error);
        Assert.Null(confirmed.Verdict);
        Assert.Equal(UnilateralExitStatus.Completed, harness.Records.Records[record.Id].Status);
    }

    #endregion

    #region When the statuses were read

    /// <summary>
    /// The page says when the statuses it shows were last read from the chain, and a check moves that time on.
    /// </summary>
    /// <remarks>
    /// Nothing refreshes the stored statuses between presses, and a step's watchtower version becomes valid about
    /// fifty blocks after the step itself — so "Ready" as of yesterday is a different instruction from "Ready" as
    /// of a minute ago, and the page could not tell them apart. The time is stored in the same value as the set,
    /// so it cannot describe a different write than the statuses beside it.
    /// </remarks>
    [Fact]
    public async Task The_page_reports_when_the_statuses_were_last_read_from_the_chain()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        var record = await BuiltExit(harness);

        var built = await harness.Service.ReadAsync(StoreId, Ct);
        Assert.Equal(harness.Now, built.StatusesReadUtc);

        harness.Time.Advance(TimeSpan.FromHours(9));
        Assert.True((await harness.Service.CheckAsync(StoreId, record.Id, Ct)).Success);

        var checkedPage = await harness.Service.ReadAsync(StoreId, Ct);
        Assert.Equal(harness.Now.AddHours(9), checkedPage.StatusesReadUtc);
        Assert.NotNull(checkedPage.Transactions);
        Assert.False(checkedPage.TransactionsUnreadable);
    }

    /// <summary>
    /// A set stored as a bare array, before the read time was recorded, still reads — with no time rather than
    /// an invented one.
    /// </summary>
    [Fact]
    public async Task A_set_stored_before_the_read_time_was_recorded_still_reads()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        var record = await BuiltExit(harness);
        var legacy = JsonSerializer.Serialize(StoredSet(harness.Records.Records[record.Id].TransactionsJson));
        harness.Records.Records[record.Id].TransactionsJson = legacy;

        var page = await harness.Service.ReadAsync(StoreId, Ct);

        Assert.False(page.TransactionsUnreadable);
        Assert.NotNull(page.Transactions);
        Assert.NotEmpty(page.Transactions);
        Assert.Null(page.StatusesReadUtc);

        // And checking it writes the envelope, time included.
        Assert.True((await harness.Service.CheckAsync(StoreId, record.Id, Ct)).Success);
        Assert.StartsWith("{", harness.Records.Records[record.Id].TransactionsJson);
    }

    #endregion

    #region Deadlines on the SDK's exit calls

    /// <summary>
    /// An SDK exit call that never answers releases the store's gate at its deadline, instead of holding it for
    /// as long as an unreachable operator holds the call.
    /// </summary>
    /// <remarks>
    /// Preparing an exit begins with a best-effort refresh from the Spark operators, and the binding offers no
    /// cancellation. Without a deadline an operator that accepts connections and never answers would hold the
    /// request, and every other exit action for the store would answer "already running" until the process
    /// restarted. The call is abandoned, not cancelled — nothing it could do is broadcast — and the store's
    /// next action goes through.
    /// </remarks>
    [Fact]
    public async Task A_quote_that_never_answers_gives_up_and_frees_the_store()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        harness.Service.QuoteDeadline = TimeSpan.FromMilliseconds(50);
        harness.Sdk.HoldExitCalls = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await harness.Service.QuoteAsync(StoreId, 10, Destination, Ct);

        Assert.False(result.Success);
        Assert.Contains("did not finish quoting this exit", result.Error);
        Assert.Empty(harness.Records.Records);

        // The gate is free: the next action is answered on its merits, not with "already running".
        var acknowledged = await harness.Service.AcknowledgeDisclosureAsync(StoreId, Ct);
        Assert.NotEqual(SparkUnilateralExitService.OperationInFlight, acknowledged.Error);

        harness.Sdk.HoldExitCalls.SetResult();
    }

    [Fact]
    public async Task A_build_or_check_that_never_answers_gives_up_without_storing_anything()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        var record = await BuiltExit(harness);
        var storedSet = harness.Records.Records[record.Id].TransactionsJson;
        harness.Service.QuoteDeadline = TimeSpan.FromMilliseconds(50);
        harness.Service.BuildDeadline = TimeSpan.FromMilliseconds(50);
        harness.Service.CheckDeadline = TimeSpan.FromMilliseconds(50);
        harness.Sdk.HoldExitCalls = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var rebuilt = await harness.Service.BuildAsync(StoreId, record.Id, Ct);
        Assert.False(rebuilt.Success);
        Assert.Contains("did not finish re-pricing this exit", rebuilt.Error);

        var check = await harness.Service.CheckAsync(StoreId, record.Id, Ct);
        Assert.False(check.Success);
        Assert.Contains("did not finish checking this exit", check.Error);

        // The stored set is untouched by either, and the exit is still the operator's to act on.
        Assert.Equal(storedSet, harness.Records.Records[record.Id].TransactionsJson);
        Assert.Equal(UnilateralExitStatus.Built, harness.Records.Records[record.Id].Status);
        Assert.True((await harness.Service.AbandonAsync(StoreId, record.Id, Ct)).Success);

        harness.Sdk.HoldExitCalls.SetResult();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();

        var result = await harness.Service.SetExplorerUrlAsync(StoreId, "https://explorer.test/api", Ct);

        Assert.Equal(SparkUnilateralExitService.NotConfigured, result.Error);
        Assert.Empty(harness.Settings.Writes);
    }

    #endregion

    #region The exit-state backup

    /// <summary>
    /// A backup the operator pastes is queued for import in the file store — not written over the automatic
    /// backup, and not into the settings blob.
    /// </summary>
    /// <remarks>
    /// The automatic slot is the wallet's own latest export and every due pass replaces it, so a paste written
    /// there was gone before anything had imported it. The queue is the slot nothing automatic writes.
    /// </remarks>
    [Fact]
    public async Task A_pasted_backup_is_queued_for_import_and_not_written_over_the_automatic_backup()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        await harness.Backups.WriteAsync(StoreId, "the-wallet-s-own-export", "02aa", Ct);

        var result = await harness.Service.SetExitStateBackupAsync(StoreId, "opaque-sdk-backup", Ct);

        Assert.True(result.Success, result.Error);
        Assert.Equal(["opaque-sdk-backup"], harness.Backups.Pending(StoreId));
        Assert.Equal("the-wallet-s-own-export", harness.Backups.Stored(StoreId));
        // No settings write at all, on the one save action the operator triggers by hand: keeping
        // this value out of the settings column is the entire reason the file store exists.
        Assert.Empty(harness.Settings.Writes);
    }

    /// <summary>
    /// A paste past the ceiling is refused before anything is stored — almost always a mis-paste,
    /// and a file that large is neither safe to hold nor safe to import.
    /// </summary>
    [Fact]
    public async Task A_pasted_backup_past_the_size_ceiling_is_refused_before_anything_is_stored()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        var result = await harness.Service.SetExitStateBackupAsync(
            StoreId, new string('x', SparkUnilateralExitService.MaxExitStateBackupChars + 1), Ct);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("characters", result.Error);
        Assert.Empty(harness.Backups.WriteCalls);
        Assert.Empty(harness.Backups.PendingAddCalls);
        Assert.Empty(harness.Backups.DeleteCalls);
    }

    /// <summary>
    /// Re-pasting a backup already waiting does not queue a second copy.
    /// </summary>
    [Fact]
    public async Task Re_pasting_a_queued_backup_does_not_queue_it_twice()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);

        Assert.True((await harness.Service.SetExitStateBackupAsync(StoreId, "same-blob", Ct)).Success);
        Assert.True((await harness.Service.SetExitStateBackupAsync(StoreId, "same-blob", Ct)).Success);

        // A save button pressed twice, a page reloaded with the value still in the textarea: one copy
        // waits, not two multi-megabyte files.
        Assert.Equal(["same-blob"], harness.Backups.Pending(StoreId));
        Assert.Empty(harness.Backups.WriteCalls);
    }

    /// <summary>Clearing removes the automatic backup and everything waiting to be imported.</summary>
    [Fact]
    public async Task Clearing_the_backup_removes_the_automatic_backup_and_the_queue()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        await harness.Backups.WriteAsync(StoreId, "automatic", "02aa", Ct);
        await harness.Service.SetExitStateBackupAsync(StoreId, "to-be-cleared", Ct);

        Assert.True((await harness.Service.SetExitStateBackupAsync(StoreId, null, Ct)).Success);

        Assert.Null(harness.Backups.Stored(StoreId));
        Assert.Empty(harness.Backups.Pending(StoreId));
        Assert.Contains(StoreId, harness.Backups.DeleteCalls);
    }

    /// <summary>
    /// A clear press clears the deprecated settings slot as well, without a whole-blob settings write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The banner the operator is shown is the thing at stake.</b> Clearing reports that no backup is
    /// stored now and that a restart will import none — and the connect adopts from the deprecated slot
    /// whenever the file is empty. So a store that still held a value there (upgraded while the feature was
    /// off, or not reconnected since, or adoption's file write having failed) took the blob back on its next
    /// connect, under that sentence.
    /// </para>
    /// <para>
    /// Nothing is stored here before the press, which is the state the defect needed: a file that already
    /// reads as empty is what the unchanged-value comparison answers about, and the press used to stop there
    /// having touched nothing. The empty <c>Writes</c> is the other half — writing the whole settings blob to
    /// null one field would reconcile the store's wallet, and a press that stores nothing must not.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Clearing_the_backup_clears_the_deprecated_slot_without_a_settings_write()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.Settings.Settings[StoreId]!.UnilateralExit.ExitStateBackup = "legacy-blob";

        Assert.True((await harness.Service.SetExitStateBackupAsync(StoreId, null, Ct)).Success);

        Assert.Null(harness.Settings.Settings[StoreId]!.UnilateralExit.ExitStateBackup);
        Assert.Empty(harness.Settings.Writes);
    }

    [Fact]
    public async Task Storing_a_backup_on_an_unconfigured_store_is_refused()
    {
        var harness = Harness.Create();

        var result = await harness.Service.SetExitStateBackupAsync(StoreId, "some-blob", Ct);

        Assert.Equal(SparkUnilateralExitService.NotConfigured, result.Error);
        Assert.Empty(harness.Backups.WriteCalls);
        Assert.Empty(harness.Backups.PendingAddCalls);
    }

    #endregion

    #region The page read

    /// <summary>The read reports the wallet, the balance, the active exit and the history in one pass.</summary>
    [Fact]
    public async Task The_page_read_reports_the_wallet_the_balance_and_the_history()
    {
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
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
    [InlineData("[{}]")]
    [InlineData("""{"StatusesReadUtc":"2026-08-20T12:00:00+00:00"}""")]
    [InlineData("""{"StatusesReadUtc":"2026-08-20T12:00:00+00:00","Transactions":[{}]}""")]
    // Present but not what Bitcoin would produce. The page pastes the hex into a shell command, so a "hex" that
    // could carry a quote must be refused here rather than rendered.
    [InlineData("""[{"Txid":"aa","TxHex":"0200","DependsOn":[],"Kind":0,"Status":{"Readiness":1}}]""")]
    [InlineData("""[{"Txid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","TxHex":"0200'; rm -rf ~; '","DependsOn":[],"Kind":0,"Status":{"Readiness":1}}]""")]
    [InlineData("""[{"Txid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","TxHex":"0200","CpfpTxHex":"zz","DependsOn":[],"Kind":1,"Status":{"Readiness":1}}]""")]
    [InlineData("""[{"Txid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","TxHex":"020","DependsOn":[],"Kind":0,"Status":{"Readiness":1}}]""")]
    [InlineData("""[{"Txid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","TxHex":"0200","DependsOn":["not-a-txid"],"Kind":0,"Status":{"Readiness":1}}]""")]
    [InlineData("""[{"Txid":"aa","TxHex":"0200","DependsOn":[],"Kind":99,"Status":0}]""")]
    // The member most likely to be null, and the one that used to be dereferenced before it was checked: the
    // status is a reference record, and System.Text.Json fills an explicit or missing null into it regardless.
    [InlineData("""[{"Txid":"aa","TxHex":"0200","DependsOn":[],"Kind":0,"Status":null}]""")]
    [InlineData("""[{"Txid":"aa","TxHex":"0200","DependsOn":[],"Kind":0}]""")]
    [InlineData("""[{"Txid":"aa","TxHex":"0200","DependsOn":[null],"Kind":0,"Status":{"Readiness":1}}]""")]
    public async Task An_unreadable_transaction_column_is_reported_rather_than_thrown(string stored)
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        var record = harness.Seed(status: UnilateralExitStatus.Built);
        harness.Records.Records[record.Id].TransactionsJson = stored;

        var page = await harness.Service.ReadAsync(StoreId, Ct);

        Assert.True(page.TransactionsUnreadable);
        Assert.Null(page.Transactions);
        Assert.Null(page.LoadError);
        // And the record itself is still on the page, so the operator can abandon it.
        Assert.Equal(record.Id, page.ActiveRecord?.Id);

        // The check reads the same column and must refuse on it the same way, not throw on the way to the SDK.
        var check = await harness.Service.CheckAsync(StoreId, record.Id, Ct);
        Assert.False(check.Success);
        Assert.NotEqual(SparkUnilateralExitService.UnexpectedFailure, check.Error);
        Assert.Empty(harness.Sdk.ExitCheckCalls);
    }

    /// <summary>
    /// A database that will not answer becomes a refusal on every operation and a "could not read" page, never
    /// an exception.
    /// </summary>
    /// <remarks>
    /// These methods are called from request handlers, and BTCPay 2.4 answers an unhandled plugin exception during
    /// a request by disabling the plugin and restarting the server — every store's Lightning, over one exit page.
    /// The page read in particular must not fall back to the quote form: "no exit is in progress" on the strength
    /// of a query that never answered is an invitation to start a second one.
    /// </remarks>
    [Fact]
    public async Task A_database_that_will_not_answer_degrades_every_entry_point_instead_of_throwing()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.WithLeaves(("leaf-a", 500_000));
        var record = harness.Seed();
        harness.Records.FailReadsWith = new InvalidOperationException("the database is not answering");

        var page = await harness.Service.ReadAsync(StoreId, Ct);

        Assert.Equal(SparkUnilateralExitService.ExitsUnreadable, page.LoadError);
        Assert.Null(page.ActiveRecord);
        Assert.False(page.DisclosureAcknowledged);

        foreach (var attempt in new[]
                 {
                     await harness.Service.QuoteAsync(StoreId, 10, Destination, Ct),
                     await harness.Service.BuildAsync(StoreId, record.Id, Ct),
                     await harness.Service.CheckAsync(StoreId, record.Id, Ct),
                     await harness.Service.MarkCompletedAsync(StoreId, record.Id, Ct),
                     await harness.Service.AbandonAsync(StoreId, record.Id, Ct)
                 })
        {
            Assert.False(attempt.Success);
            Assert.Equal(SparkUnilateralExitService.UnexpectedFailure, attempt.Error);
        }

        // Nothing was signed on the way to those refusals.
        Assert.Empty(harness.Sdk.ExitBuildCalls);
        Assert.Equal(UnilateralExitStatus.AwaitingFunding, harness.Records.Records[record.Id].Status);

        // And the per-store gate was released each time, so the store is usable again once the database is.
        harness.Records.FailReadsWith = null;
        Assert.True((await harness.Service.AbandonAsync(StoreId, record.Id, Ct)).Success);
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
        var harness = Harness.Create();
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
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.Seed(status: UnilateralExitStatus.Built);
        harness.Explorer(Utxo(4_200));

        var page = await harness.Service.ReadAsync(StoreId, Ct);

        Assert.Null(page.FundingReceivedSat);
        Assert.Equal(0, harness.ExplorerRequests);
    }

    /// <summary>The quote form's opening rate is fetched while nothing is in flight, and not once something is.</summary>
    /// <remarks>
    /// A store with an exit already in flight has the rate it was quoted at on the record, and the page shows that
    /// one — so fetching a market rate on every view of that page would be an external round trip that decides
    /// nothing, from a page any store viewer can reload. The recommendation travels as null rather than as a number
    /// in that case: the service has no basis for one, and the fallback belongs to whoever renders the field.
    /// </remarks>
    [Fact]
    public async Task The_page_carries_a_recommendation_only_while_nothing_is_in_flight()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.ExplorerBody("""{"halfHourFee":7}""");

        var page = await harness.Service.ReadAsync(StoreId, Ct);

        Assert.Null(page.ActiveRecord);
        Assert.Equal(7, page.RecommendedFeeRateSatPerVbyte);
        Assert.Equal(1, harness.ExplorerRequests);

        harness.Seed(status: UnilateralExitStatus.Built);
        var inFlight = await harness.Service.ReadAsync(StoreId, Ct);

        Assert.NotNull(inFlight.ActiveRecord);
        Assert.Null(inFlight.RecommendedFeeRateSatPerVbyte);
        // Still one request in total, and a built exit asks for nothing at all — see
        // A_built_exit_reports_no_funding_balance — so this counts the recommendation and nothing else.
        Assert.Equal(1, harness.ExplorerRequests);
    }

    /// <summary>A store that has not accepted the disclosure is shown it, and the explorer is not asked anything.</summary>
    /// <remarks>
    /// The exit page is linked from every store's Advanced page, so it is opened by operators who only want to
    /// read what an exit involves. Until they accept it the page shows the disclosure and no quote form, so a
    /// market rate would be fetched for a field nobody sees — and on mainnet that fetch is this server telling
    /// mempool.space it exists. Nothing is quoted before the acknowledgement, and nothing is looked up either.
    /// </remarks>
    [Fact]
    public async Task An_unacknowledged_page_read_asks_the_explorer_nothing()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: false);
        harness.ExplorerBody("""{"halfHourFee":7}""");

        var page = await harness.Service.ReadAsync(StoreId, Ct);

        Assert.False(page.DisclosureAcknowledged);
        Assert.Null(page.RecommendedFeeRateSatPerVbyte);
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
        var harness = Harness.Create();
        harness.Configure(acknowledged: true);
        harness.Seed();
        harness.ExplorerBody(
            """[{"txid":"../../etc/passwd","vout":0,"value":9000,"status":{"confirmed":true}},"""
            + Utxo(4_200, vout: 3)
            + "]");

        var page = await harness.Service.ReadAsync(StoreId, Ct);

        Assert.Equal(4_200, page.FundingReceivedSat);
    }

    /// <summary>
    /// A well-formed answer is the half-hour estimate, and it is clamped to what the quote form will accept.
    /// </summary>
    /// <remarks>
    /// The half-hour estimate and not the fastest rate on offer, because this one number is paid by every
    /// transaction in a chain of dozens: pricing all of them at the panic rate overpays on each, and pricing them at
    /// a floor risks the failure this flow cannot recover from — a half-broadcast exit whose fan-out is already
    /// spent. The ceiling matters from the other side, and it is the suggestion's own rather than the form's: the
    /// recommendation is a third party's claim, and an explorer answering 900 — broken or hostile — must not make
    /// the most expensive exit the form accepts the default in front of an operator about to fund it.
    /// </remarks>
    [Fact]
    public async Task A_well_formed_answer_is_the_half_hour_rate_clamped_to_the_suggestion_cap()
    {
        var harness = Harness.Create();
        harness.ExplorerBody(
            """{"fastestFee":40,"halfHourFee":7,"hourFee":5,"economyFee":3,"minimumFee":1}""");

        Assert.Equal(7, await harness.ExplorerClient.RecommendFeeRateSatPerVbyteAsync(
            mainnet: true, new UnilateralExitSettings(), Ct));

        // The path, asserted because it is this plugin's contract with a third party rather than an internal
        // choice: {base}/v1/fees/recommended under the same default base URL the funding lookups use.
        Assert.Equal(
            SparkExitFundingExplorer.MainnetDefaultApiUrl + "/v1/fees/recommended",
            Assert.Single(harness.ExplorerUrls));

        // A fee spike, read after the cache window has passed — which is what an operator reloading the page
        // minutes later gets. The rate is what the form can quote at, not what the explorer said.
        harness.Time.Advance(SparkExitFundingExplorer.RecommendedFeeRateTtl);
        harness.ExplorerBody(
            """{"fastestFee":4000,"halfHourFee":900,"hourFee":800,"economyFee":700,"minimumFee":600}""");

        Assert.Equal(
            SparkExitFundingExplorer.MaxRecommendedFeeRateSatPerVbyte,
            await harness.ExplorerClient.RecommendFeeRateSatPerVbyteAsync(
                mainnet: true, new UnilateralExitSettings(), Ct));
        Assert.True(
            SparkExitFundingExplorer.MaxRecommendedFeeRateSatPerVbyte < SparkUnilateralExitService.MaxFeeRateSatPerVbyte);
    }

    /// <summary>
    /// An explorer base URL may be a scheme, a host, a port and a path — and nothing that would carry a secret
    /// into logs, reshape the request paths, or aim the server at its own cloud metadata service.
    /// </summary>
    /// <remarks>
    /// The page asks this host on every view by anyone who can view the store, so the setting is a standing
    /// instruction to the server. Loopback and private addresses stay allowed — a self-hosted esplora beside the
    /// server is the choice the override exists for, and only a server administrator can set it — while
    /// link-local, unspecified and multicast literals are never an explorer on any network.
    /// </remarks>
    [Theory]
    [InlineData("https://user:secret@esplora.example/api", "user name or password")]
    [InlineData("https://esplora.example/api?key=abc", "query string")]
    [InlineData("https://esplora.example/api#frag", "query string or a fragment")]
    [InlineData("http://169.254.169.254/latest", "link-local")]
    [InlineData("http://[fe80::1]/api", "link-local")]
    [InlineData("http://0.0.0.0:3002/api", "link-local")]
    [InlineData("http://224.0.0.1/api", "link-local")]
    public void An_explorer_url_that_is_never_an_explorer_is_refused(string candidate, string expected)
    {
        Assert.False(SparkExitFundingExplorer.TryNormaliseApiUrl(candidate, out var normalised, out var error));
        Assert.Null(normalised);
        Assert.Contains(expected, error);
    }

    [Theory]
    [InlineData("http://localhost:3002/api/")]
    [InlineData("http://127.0.0.1:3002/api")]
    [InlineData("http://10.21.21.26:3006/api")]
    [InlineData("http://esplora.local/api")]
    [InlineData("https://mempool.space/api")]
    public void A_self_hosted_or_public_explorer_is_accepted(string candidate)
    {
        Assert.True(SparkExitFundingExplorer.TryNormaliseApiUrl(candidate, out var normalised, out var error));
        Assert.Null(error);
        Assert.False(normalised!.EndsWith('/'));
    }

    /// <summary>
    /// A failed lookup names neither the full URL nor anything the HTTP stack said about it — not in the log,
    /// and not in the refusal the page and the record carry.
    /// </summary>
    /// <remarks>
    /// An esplora path can carry an access token, and a handler's exception message can carry the host, the
    /// resolved address and the whole request URL. The banner and the record's last error are read by anyone
    /// who can view the store's settings, and the log by whoever reads the server's logs.
    /// </remarks>
    [Fact]
    public async Task A_failed_lookup_keeps_the_explorer_path_and_the_handler_s_words_out_of_logs_and_errors()
    {
        var harness = Harness.Create();
        harness.Configure(acknowledged: true, esploraApiUrl: "https://esplora.example/api/SECRET-TOKEN");
        harness.WithLeaves(("leaf-a", 500_000));
        var record = harness.Seed(leafIds: ["leaf-a"]);
        harness.ExplorerOffline();

        var logger = new CapturingLogger<SparkExitFundingExplorer>();
        var explorer = new SparkExitFundingExplorer(harness.ClientFactory, harness.Time, logger);
        var lookup = await explorer.ListConfirmedAsync(
            "https://esplora.example/api/SECRET-TOKEN", FundingAddress, "02" + new string('a', 64), Ct);

        Assert.Null(lookup.Utxos);
        Assert.DoesNotContain("SECRET-TOKEN", lookup.Error);
        Assert.DoesNotContain("no route to host", lookup.Error);
        Assert.NotEmpty(logger.Lines);
        Assert.DoesNotContain("SECRET-TOKEN", logger.AllText);
        Assert.DoesNotContain("no route to host", logger.AllText);
        Assert.Contains("https://esplora.example", logger.AllText);

        var build = await harness.Service.BuildAsync(StoreId, record.Id, Ct);
        Assert.False(build.Success);
        Assert.DoesNotContain("SECRET-TOKEN", build.Error);
        Assert.DoesNotContain("SECRET-TOKEN", harness.Records.Records[record.Id].LastError);
    }

    /// <summary>
    /// Every way the lookup can fail is a null rate rather than an exception.
    /// </summary>
    /// <remarks>
    /// The exit page has to render on a server with no outbound network at all, so an unreachable explorer cannot
    /// travel as an exception: it would take down a GET whose worst honest answer is "no recommendation, quote at
    /// the fallback". The endless response is here because it is the failure that does not look like one — a 200
    /// with no end to it, where only the read ceiling can stop the wait.
    /// </remarks>
    [Fact]
    public async Task A_lookup_that_fails_is_null_rather_than_an_exception()
    {
        var settings = new UnilateralExitSettings { EsploraApiUrl = "http://explorer.test/api" };

        {
            var offline = Harness.Create();
            offline.ExplorerOffline();
            Assert.Null(await offline.ExplorerClient.RecommendFeeRateSatPerVbyteAsync(true, settings, Ct));
        }

        {
            var refused = Harness.Create();
            // An explorer rate-limiting this plugin, which is the answer that most needs to not become a retry.
            refused.ExplorerFails(HttpStatusCode.TooManyRequests);
            Assert.Null(await refused.ExplorerClient.RecommendFeeRateSatPerVbyteAsync(true, settings, Ct));
        }

        {
            var junk = Harness.Create();
            // A captive portal or a proxy's error page: a 200 whose body is not this API's JSON.
            junk.ExplorerBody("<html><body>maintenance</body></html>");
            Assert.Null(await junk.ExplorerClient.RecommendFeeRateSatPerVbyteAsync(true, settings, Ct));
        }

        {
            var endless = Harness.Create();
            endless.ExplorerEndless();
            Assert.Null(await endless.ExplorerClient.RecommendFeeRateSatPerVbyteAsync(true, settings, Ct));
        }
    }

    /// <summary>
    /// A body with no usable half-hour rate is null, whatever shape the rate is missing in.
    /// </summary>
    /// <remarks>
    /// Zero and negative are the dangerous ones: a zeroed placeholder, or a field the parse emptied, would render as
    /// a rate an operator accepts — in a form whose own minimum is one satoshi — and no transaction in the exit
    /// would relay at it. A non-number fails the parse instead, which has to reach the caller as the same "no
    /// recommendation" rather than as an exception on a page render.
    /// </remarks>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"fastestFee":12,"minimumFee":1}""")]
    [InlineData("""{"halfHourFee":null}""")]
    [InlineData("""{"halfHourFee":"7"}""")]
    [InlineData("""{"halfHourFee":0}""")]
    [InlineData("""{"halfHourFee":-3}""")]
    public async Task A_body_with_no_usable_rate_is_null(string body)
    {
        var harness = Harness.Create();
        harness.ExplorerBody(body);

        Assert.Null(await harness.ExplorerClient.RecommendFeeRateSatPerVbyteAsync(
            true, new UnilateralExitSettings(), Ct));
    }

    /// <summary>
    /// Off mainnet with no override there is no recommendation, and nothing is even asked.
    /// </summary>
    /// <remarks>
    /// mempool.space has no regtest, which is why the URL resolution refuses here — and for the recommendation the
    /// refusal has to cost nothing: the caller falls back to the plugin's own rate and the field still renders. The
    /// call count is the behaviour under test: a fetch attempted here would be a request to a third party that
    /// cannot answer the question, on every render of a regtest exit page.
    /// </remarks>
    [Fact]
    public async Task Off_mainnet_with_no_override_a_recommendation_is_not_even_attempted()
    {
        var harness = Harness.Create();

        Assert.Null(await harness.ExplorerClient.RecommendFeeRateSatPerVbyteAsync(
            mainnet: false, new UnilateralExitSettings(), Ct));

        Assert.Equal(0, harness.ExplorerRequests);
    }

    /// <summary>
    /// The recommendation is cached for its window instead of being fetched on every render.
    /// </summary>
    /// <remarks>
    /// The exit page is a GET any store viewer can reload in a loop, and a market rate is not a per-viewer fact:
    /// one request per page view would turn refreshing a page into load on a third party's endpoint and would make
    /// the field's presence depend on their latency. Once the window is out the market is asked again, because an
    /// operator about to fund an exit must not be shown an old rate as if it were current.
    /// </remarks>
    [Fact]
    public async Task The_recommendation_is_cached_rather_than_fetched_per_render()
    {
        var harness = Harness.Create();
        var settings = new UnilateralExitSettings();
        harness.ExplorerBody("""{"halfHourFee":7}""");

        Assert.Equal(7, await harness.ExplorerClient.RecommendFeeRateSatPerVbyteAsync(true, settings, Ct));
        Assert.Equal(7, await harness.ExplorerClient.RecommendFeeRateSatPerVbyteAsync(true, settings, Ct));
        Assert.Equal(1, harness.ExplorerRequests);

        harness.Time.Advance(SparkExitFundingExplorer.RecommendedFeeRateTtl);
        harness.ExplorerBody("""{"halfHourFee":9}""");

        Assert.Equal(9, await harness.ExplorerClient.RecommendFeeRateSatPerVbyteAsync(true, settings, Ct));
        Assert.Equal(2, harness.ExplorerRequests);
    }

    #endregion

    /// <summary>
    /// Seeds a funded exit and builds it, returning the row as the build left it.
    /// </summary>
    /// <remarks>
    /// Shared by the check tests, which all start from "an exit that is Built and whose transaction set is on the
    /// row" — reaching that state through <see cref="ISparkUnilateralExitService.BuildAsync"/> rather than by
    /// writing a JSON column by hand, so a check test cannot pass against a row shape the build would never
    /// produce.
    /// </remarks>
    private static async Task<UnilateralExitRecord> BuiltExit(Harness harness)
    {
        harness.WithLeaves(("leaf-a", 500_000));
        var record = harness.Seed(leafIds: ["leaf-a"], singleUtxoFundingSat: 4_200);
        harness.Explorer(Utxo(10_000));

        var built = await harness.Service.BuildAsync(StoreId, record.Id, Ct);
        Assert.True(built.Success, built.Error);
        return record;
    }

    /// <summary>
    /// The signed set a record stores, read the way the service reads it: the envelope carrying the time its
    /// statuses were read from the chain, or a bare array from before that was recorded.
    /// </summary>
    private static SparkExitTransaction[] StoredSet(string? json)
    {
        Assert.False(string.IsNullOrWhiteSpace(json), "the record holds no transaction set");
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind is JsonValueKind.Array
            ? JsonSerializer.Deserialize<SparkExitTransaction[]>(json)!
            : JsonSerializer.Deserialize<SparkExitTransaction[]>(
                document.RootElement.GetProperty("Transactions").GetRawText())!;
    }

    /// <summary>One confirmed entry of an esplora <c>/address/{address}/utxo</c> response, at a txid of the test's own.</summary>
    private static string UtxoAt(string txid, long valueSat, uint vout = 0) =>
        string.Format(
            CultureInfo.InvariantCulture,
            """{{"txid":"{0}","vout":{1},"value":{2},"status":{{"confirmed":true}}}}""",
            txid,
            vout,
            valueSat);

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
    /// The service under test with every collaborator faked.
    /// </summary>
    /// <remarks>
    /// The settings store is built <em>without</em> a runtime on purpose: modelling the SDK reconnect a settings
    /// write causes would replace the fake wallet mid-test, and what these tests need to observe is the
    /// acknowledgement landing in storage rather than the reconnect that follows it. The reconnect itself is
    /// covered where it matters, in the Stable Balance tests.
    /// </remarks>
    private sealed class Harness
    {
        private readonly ExplorerHandler _handler = new();

        private Harness()
        {
            Protector = new SparkMnemonicProtector(new EphemeralDataProtectionProvider());
            Runtime.Clients[StoreId] = Sdk;

            Time = new StubTimeProvider(Now);
            ClientFactory = new ExplorerClientFactory(_handler);
            ExplorerClient = new SparkExitFundingExplorer(
                ClientFactory,
                Time,
                NullLogger<SparkExitFundingExplorer>.Instance);

            Service = new SparkUnilateralExitService(
                Settings,
                Runtime,
                Records,
                Protector,
                ExplorerClient,
                Backups,
                Network.RegTest,
                Time,
                NullLogger<SparkUnilateralExitService>.Instance);
        }

        public static Harness Create() => new();

        public DateTimeOffset Now { get; } = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

        /// <summary>
        /// The clock the service and the explorer both read, so a test can step over the recommendation's cache
        /// window rather than wait for it.
        /// </summary>
        public StubTimeProvider Time { get; }

        /// <summary>
        /// The explorer instance the service was handed, exposed so the fetch rules can be exercised without the
        /// service in the way — and so a test can tell a fetch the service made from one it did not.
        /// </summary>
        public SparkExitFundingExplorer ExplorerClient { get; }

        /// <summary>The HTTP client factory behind <see cref="ExplorerClient"/>, for a second explorer instance.</summary>
        public IHttpClientFactory ClientFactory { get; }

        public FakeSparkSdkClient Sdk { get; } = new();

        public FakeSparkStoreRuntime Runtime { get; } = new();

        public FakeSparkStoreSettingsStore Settings { get; } = new();

        public InMemoryUnilateralExitRecordStore Records { get; } = new();

        /// <summary>Where exit-state backups land; the real one is a file per store.</summary>
        public FakeExitStateBackupStore Backups { get; } = new();

        public SparkMnemonicProtector Protector { get; }

        /// <summary>How many lookups actually reached the explorer.</summary>
        public int ExplorerRequests => _handler.Requests;

        /// <summary>The URLs the explorer was asked for, in order, so a test can tell the two endpoints apart.</summary>
        public IReadOnlyList<string> ExplorerUrls => _handler.Urls;

        /// <summary>An explorer that answers with a body that never ends: the response no header warned about.</summary>
        public void ExplorerEndless() => _handler.Endless = true;

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

        /// <summary>
        /// Leaves the Spark operators have already moved on-chain, so the SDK recovers them cooperatively.
        /// </summary>
        /// <remarks>
        /// A separate helper rather than a third tuple member on <see cref="WithLeaves"/>, because the method is
        /// the thing that decides whether the recovery needs funding at all — a caller that is not asking about
        /// that case should not have to name a method, and one that is should have it visible at the call site.
        /// </remarks>
        public void WithCooperativeLeaves(params (string LeafId, long ValueSat)[] leaves)
        {
            Sdk.ExitLeaves.Clear();
            foreach (var (leafId, valueSat) in leaves)
            {
                Sdk.ExitLeaves.Add(new SparkExitLeaf(
                    leafId, valueSat, SparkExitRecoveryMethod.Cooperative));
            }
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

            /// <summary>Answers with an endless chunked body, so only the read ceiling can stop it.</summary>
            public bool Endless { get; set; }

            public int Requests { get; private set; }

            public List<string> Urls { get; } = [];

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Requests++;

                lock (Urls)
                    Urls.Add(request.RequestUri?.ToString() ?? string.Empty);

                if (Offline)
                {
                    return Task.FromException<HttpResponseMessage>(
                        new HttpRequestException("no route to host"));
                }

                if (Endless)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new EndlessStream())
                    });
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
