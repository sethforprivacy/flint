using Breez.Sdk.Spark;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// On-chain deposits: the address, the deposits that never arrived, and the fee policy that decides which.
/// </summary>
/// <remarks>
/// <para>
/// <b>The failure this whole feature exists to prevent looks like theft.</b> Spark's default claim ceiling is
/// <c>Rate(1 sat/vB)</c> and it is a cap rather than a bid, so on mainnet — where even a cheap market runs at
/// 2–3 sat/vB — a deposit is never claimed. It does not error to the merchant, it does not appear in the
/// balance, and before this wave nothing read the one table it appears in. From the merchant's side they sent
/// Bitcoin to an address this plugin gave them and it vanished.
/// </para>
/// <para>
/// So the tests here are about visibility and about the guards on the recovery path, not about the SDK call.
/// </para>
/// </remarks>
public class SparkDepositServiceTests
{
    private const string StoreId = "store-1";
    private const string TxId = "8808985e78ad465c25727d5ad749f60a5787855d4f1ddffebfc4afb4dbde1b37";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #region The configured policy

    /// <summary>
    /// The plugin never leaves Spark's default claim ceiling in place, and never disables claiming.
    /// </summary>
    /// <remarks>
    /// Both halves matter and both are one keyword away from being wrong. <c>Rate(1)</c> is the default that
    /// strands deposits; <c>null</c> is not a default at all but a switch that <em>disables automatic
    /// claiming</em>, so a policy that produced one would be worse than the thing it replaced.
    /// </remarks>
    [Fact]
    public void The_claim_policy_tracks_the_mempool_and_is_never_absent()
    {
        var policy = new SparkDepositSettings().ToMaxFee();

        var recommended = Assert.IsType<SparkMaxFee.NetworkRecommended>(policy);
        Assert.Equal(SparkDepositSettings.DefaultClaimFeeLeewaySatPerVbyte, recommended.LeewaySatPerVbyte);

        // Not a fixed rate, whatever the leeway is set to — including the zero a partially-written settings blob
        // would deserialise to, which must not degrade into "the recommendation with no margin".
        foreach (var leeway in new long[] { 0, -1, 5, 100 })
        {
            var configured = new SparkDepositSettings { ClaimFeeLeewaySatPerVbyte = leeway }.ToMaxFee();
            var asRecommended = Assert.IsType<SparkMaxFee.NetworkRecommended>(configured);
            Assert.True(
                asRecommended.LeewaySatPerVbyte > 0,
                "a claim ceiling with no margin over the recommendation is how a deposit gets stranded");
        }
    }

    /// <summary>
    /// The documented maximum leeway binds on the value in force, not on a form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The cap used to be enforced nowhere.</b> It was written down as the largest leeway "the settings form
    /// will store", and there is no settings form — neither the MVC surface nor Greenfield binds this property,
    /// so the only way it ever holds a non-default is a hand-edited settings blob, which is exactly the input a
    /// form check cannot see. A blob carrying 100,000 went straight into <c>MaxFee.NetworkRecommended</c> at the
    /// next connect.
    /// </para>
    /// <para>
    /// That matters more here than a clamp usually would, because the rate is the <em>only</em> bound on an
    /// automatic claim: the 50%-of-deposit backstop lives on the manual path and the SDK's configuration has no
    /// amount-relative shape to express it in.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(101L)]
    [InlineData(1_000L)]
    [InlineData(100_000L)]
    [InlineData(long.MaxValue)]
    public void A_leeway_above_the_documented_maximum_is_clamped_however_it_arrived(long stored)
    {
        var settings = new SparkDepositSettings { ClaimFeeLeewaySatPerVbyte = stored };

        Assert.Equal(
            SparkDepositSettings.MaxClaimFeeLeewaySatPerVbyte, settings.EffectiveClaimFeeLeewaySatPerVbyte);

        // And the clamp reaches the thing that actually configures the SDK, not merely the property.
        var recommended = Assert.IsType<SparkMaxFee.NetworkRecommended>(settings.ToMaxFee());
        Assert.Equal(SparkDepositSettings.MaxClaimFeeLeewaySatPerVbyte, recommended.LeewaySatPerVbyte);
    }

    /// <summary>
    /// A leeway inside the range is passed through unchanged, so the clamp is a ceiling and not a setting.
    /// </summary>
    [Theory]
    [InlineData(1L)]
    [InlineData(2L)]
    [InlineData(37L)]
    [InlineData(SparkDepositSettings.MaxClaimFeeLeewaySatPerVbyte)]
    public void A_leeway_within_the_documented_maximum_is_left_alone(long stored)
    {
        Assert.Equal(
            stored,
            new SparkDepositSettings { ClaimFeeLeewaySatPerVbyte = stored }.EffectiveClaimFeeLeewaySatPerVbyte);
    }

    /// <summary>
    /// Zero and negatives still mean "unset", and unset still means the default rather than the cap.
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void A_leeway_of_zero_or_less_is_the_default_and_not_the_maximum(long stored)
    {
        var effective =
            new SparkDepositSettings { ClaimFeeLeewaySatPerVbyte = stored }.EffectiveClaimFeeLeewaySatPerVbyte;

        Assert.Equal(SparkDepositSettings.DefaultClaimFeeLeewaySatPerVbyte, effective);
    }

    /// <summary>
    /// The connect path applies the clamp, rather than the settings object merely knowing about it.
    /// </summary>
    /// <remarks>
    /// The same reasoning as <c>Initialisation_installs_the_clamped_filter_and_not_the_requested_one</c>: a
    /// clamp the configuration path routes around is not a guard, and the difference is one property reference.
    /// </remarks>
    [Fact]
    public void The_connect_configuration_carries_the_clamped_leeway_and_not_the_stored_one()
    {
        var applied = SparkSdkClientFactory.ApplyPostMvpConfig(
            BreezSdkSparkMethods.DefaultConfig(Network.Mainnet),
            new SparkConnectOptions(
                StoreId, "mnemonic", null, "key", Network.Mainnet,
                new SparkDepositSettings { ClaimFeeLeewaySatPerVbyte = 100_000 }.ToMaxFee()),
            NullLogger.Instance);

        var ceiling = Assert.IsType<MaxFee.NetworkRecommended>(applied.maxDepositClaimFee);
        Assert.Equal(
            (ulong)SparkDepositSettings.MaxClaimFeeLeewaySatPerVbyte, ceiling.leewaySatPerVbyte);
    }

    /// <summary>
    /// The connect configuration carries the policy rather than inheriting Spark's.
    /// </summary>
    /// <remarks>
    /// Asserted against the real config-building code, because the policy is worthless if it is computed and
    /// then not applied — and nothing else in the suite would notice.
    /// </remarks>
    [Fact]
    public void The_configured_wallet_does_not_use_the_default_that_strands_deposits()
    {
        var config = BreezSdkSparkMethods.DefaultConfig(Network.Mainnet);

        // The default this exists to replace.
        var shipped = Assert.IsType<MaxFee.Rate>(config.maxDepositClaimFee);
        Assert.Equal(1UL, shipped.satPerVbyte);

        var applied = SparkSdkClientFactory.ApplyPostMvpConfig(
            config,
            new SparkConnectOptions(
                StoreId, "mnemonic", null, "key", Network.Mainnet,
                new SparkDepositSettings().ToMaxFee()),
            NullLogger.Instance);

        var ceiling = Assert.IsType<MaxFee.NetworkRecommended>(applied.maxDepositClaimFee);
        Assert.Equal(
            (ulong)SparkDepositSettings.DefaultClaimFeeLeewaySatPerVbyte, ceiling.leewaySatPerVbyte);
    }

    /// <summary>
    /// A ceiling already below the market is flagged before a deposit is stranded, not after.
    /// </summary>
    /// <remarks>
    /// The only useful moment to say this is while the merchant still has time to change it. Compared against
    /// the half-hour rate rather than the fastest, because that is the tier a claim realistically needs.
    /// </remarks>
    [Fact]
    public async Task A_claim_ceiling_below_the_current_market_is_reported_as_such()
    {
        var h = Harness(configure: d => d.ClaimFeeLeewaySatPerVbyte = 1);
        // A market where economy plus one still does not reach the half-hour rate.
        h.Sdk.RecommendedFees = new SparkRecommendedFees(80, 60, 40, 20, 5);

        var view = await h.Service.ReadAsync(StoreId, Ct);

        Assert.True(view.ClaimPolicyLooksTooLow);
    }

    [Fact]
    public async Task A_claim_ceiling_that_clears_the_market_is_not_flagged()
    {
        var h = Harness();
        // The live sample the spike took: cheap, and the default leeway clears it.
        h.Sdk.RecommendedFees = new SparkRecommendedFees(3, 3, 2, 2, 1);

        Assert.False((await h.Service.ReadAsync(StoreId, Ct)).ClaimPolicyLooksTooLow);
    }

    #endregion

    #region Visibility

    /// <summary>
    /// A stuck deposit is reported as stuck, and a maturing one is not.
    /// </summary>
    /// <remarks>
    /// The list holds two quite different things and conflating them would be its own failure: an immature
    /// deposit needs nobody and telling a merchant it is stuck would send them to claim something they cannot.
    /// </remarks>
    [Fact]
    public async Task Only_a_matured_deposit_whose_claim_failed_needs_attention()
    {
        var h = Harness();
        h.Sdk.UnclaimedDeposits.Add(new SparkDepositInfo(TxId, 0, 60_000, IsMature: false));
        h.Sdk.UnclaimedDeposits.Add(new SparkDepositInfo(
            TxId, 1, 60_000, IsMature: true,
            new SparkDepositClaimFailure(
                SparkDepositClaimFailureKind.MaxFeeExceeded, "too dear", RequiredFeeSats: 420,
                RequiredFeeRateSatPerVbyte: 12)));

        var view = await h.Service.ReadAsync(StoreId, Ct);

        var stuck = Assert.Single(view.Stuck);
        Assert.Equal(1u, stuck.Vout);
        Assert.Equal(420, stuck.ClaimError!.RequiredFeeSats);

        var maturing = Assert.Single(view.Maturing);
        Assert.Equal(0u, maturing.Vout);
        Assert.False(maturing.NeedsAttention);
    }

    /// <summary>
    /// A failure to read the deposit address does not hide the unclaimed list.
    /// </summary>
    /// <remarks>
    /// Minting the address is a live service-provider call; listing unclaimed deposits is a local storage read.
    /// A merchant whose deposit is missing is on this page precisely because something is wrong, and the list is
    /// the thing they came for — so the two must degrade independently.
    /// </remarks>
    [Fact]
    public async Task An_unreadable_address_still_shows_the_deposits_that_never_arrived()
    {
        var h = Harness();
        h.Sdk.FailDepositAddressWith = new SdkException.NetworkException("@v1=service provider unreachable");
        h.Sdk.UnclaimedDeposits.Add(new SparkDepositInfo(
            TxId, 0, 60_000, IsMature: true,
            new SparkDepositClaimFailure(SparkDepositClaimFailureKind.MaxFeeExceeded, "too dear", 420)));

        var view = await h.Service.ReadAsync(StoreId, Ct);

        Assert.Null(view.Address);
        Assert.NotNull(view.AddressError);
        Assert.Single(view.Stuck);
    }

    /// <summary>
    /// The deposit address is fetched once per wallet, and refetched when the wallet changes.
    /// </summary>
    /// <remarks>
    /// The caching is keyed on the wallet identity as well as the store, and that is correctness rather than
    /// speed: replacing a store's seed gives it a different wallet with a different deposit address, and a cache
    /// keyed on the store alone would go on showing the old one — sending a merchant's top-up to a wallet the
    /// plugin no longer holds the seed for.
    /// </remarks>
    [Fact]
    public async Task The_address_is_cached_per_wallet_and_not_merely_per_store()
    {
        var h = Harness();

        await h.Service.ReadAsync(StoreId, Ct);
        await h.Service.ReadAsync(StoreId, Ct);

        // One network round trip for two reads of the same wallet.
        Assert.Equal(1, h.Log.Entries.Count(e => e == "sdk:deposit-address"));

        // A different wallet behind the same store: a new identity, and therefore a new address.
        h.Sdk.IdentityPubkey = "02bbbbbb";
        h.Sdk.DepositAddress = "bc1pdifferentwalletdifferentaddress0000000000000000000000000000";

        var view = await h.Service.ReadAsync(StoreId, Ct);

        Assert.Equal(2, h.Log.Entries.Count(e => e == "sdk:deposit-address"));
        Assert.Equal(h.Sdk.DepositAddress, view.Address);
    }

    /// <summary>
    /// A wallet that has not reported its identity yet gets a live address, never a cached one.
    /// </summary>
    /// <remarks>
    /// An empty identity is the absence of a cache key, not a cache key: keyed on it, every wallet this store
    /// has ever had would share one slot, and a request racing a new wallet's first sync after a seed change
    /// would be handed the previous wallet's address — a merchant top-up sent to a wallet the plugin may no
    /// longer hold the seed for. The SDK client is always the store's current wallet, so the fallback is a
    /// fresh read on every call until the identity is known.
    /// </remarks>
    [Fact]
    public async Task An_unknown_wallet_identity_bypasses_the_address_cache_in_both_directions()
    {
        var h = Harness();

        // The previous wallet cached its address under an empty identity...
        h.Sdk.IdentityPubkey = "";
        h.Sdk.DepositAddress = "bc1poldwalletaddress00000000000000000000000000000000000000000000";
        var first = await h.Service.ReadAsync(StoreId, Ct);
        Assert.Equal(h.Sdk.DepositAddress, first.Address);

        // ...and the new wallet, identity still unknown, must not be handed it.
        h.Sdk.DepositAddress = "bc1pnewwalletaddress00000000000000000000000000000000000000000000";
        var second = await h.Service.ReadAsync(StoreId, Ct);

        Assert.Equal(h.Sdk.DepositAddress, second.Address);
        // Two reads, two round trips: nothing was written into the cache under the empty key either.
        Assert.Equal(2, h.Log.Entries.Count(e => e == "sdk:deposit-address"));
    }

    #endregion

    #region Claiming

    /// <summary>
    /// A one-click claim uses the fee Spark said the claim needs.
    /// </summary>
    /// <remarks>
    /// That number is reported on the very error that stranded the deposit, so asking a merchant to supply one
    /// would be asking them to guess at something already known.
    /// </remarks>
    [Fact]
    public async Task A_claim_with_no_stated_fee_uses_the_one_Spark_asked_for()
    {
        var h = Harness();
        h.Sdk.UnclaimedDeposits.Add(Stuck(requiredFeeSats: 420));

        var outcome = await h.Service.ClaimAsync(StoreId, TxId, 0, requestedFeeSats: null, Ct);

        Assert.True(outcome.Succeeded);
        Assert.Equal(420, outcome.FeeSats);
        Assert.Equal(new SparkMaxFee.Fixed(420), Assert.Single(h.Sdk.ClaimCalls).MaxFee);
    }

    /// <summary>
    /// A fee above the store's ceiling is refused, and nothing is broadcast.
    /// </summary>
    [Fact]
    public async Task A_fee_above_the_stores_ceiling_is_refused()
    {
        var h = Harness(configure: d => d.MaxManualClaimFeeSats = 500);
        h.Sdk.UnclaimedDeposits.Add(Stuck(requiredFeeSats: 5_000));

        var outcome = await h.Service.ClaimAsync(StoreId, TxId, 0, null, Ct);

        Assert.Equal(SparkClaimStatus.Refused, outcome.Status);
        Assert.Empty(h.Sdk.ClaimCalls);
    }

    /// <summary>
    /// The refusal does not send the merchant looking for a control that does not exist.
    /// </summary>
    /// <remarks>
    /// The deposit policy has no write path on either surface — nothing binds it from a form or a request body
    /// — so "raise the limit on the deposit settings" described a page nobody can open. A refusal that names an
    /// imaginary remedy is worse than one that names none: it costs the merchant the search before they find
    /// out.
    /// </remarks>
    [Fact]
    public async Task A_refusal_over_the_ceiling_does_not_point_at_a_settings_page_that_does_not_exist()
    {
        var h = Harness(configure: d => d.MaxManualClaimFeeSats = 500);
        h.Sdk.UnclaimedDeposits.Add(Stuck(requiredFeeSats: 5_000));

        var outcome = await h.Service.ClaimAsync(StoreId, TxId, 0, null, Ct);

        Assert.Equal(SparkClaimStatus.Refused, outcome.Status);
        Assert.DoesNotContain("deposit settings", outcome.Message, StringComparison.OrdinalIgnoreCase);
        // Still says what would change the answer, rather than merely refusing.
        Assert.Contains("administrator", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// No configuration authorises spending most of a deposit to claim it.
    /// </summary>
    /// <remarks>
    /// <b>The guard Spark's own configuration cannot express.</b> A rate-based ceiling knows nothing about the
    /// size of the deposit the fee comes out of, so at a high enough fee rate a perfectly reasonable rate policy
    /// would spend more claiming a small deposit than the deposit is worth. This is the only place the two
    /// numbers are ever compared, and it holds even when the merchant has raised their own limit past it.
    /// </remarks>
    [Fact]
    public async Task A_fee_above_half_the_deposit_is_refused_however_high_the_limit_is_set()
    {
        var h = Harness(configure: d => d.MaxManualClaimFeeSats = long.MaxValue);
        // 6,000 sats to claim 10,000.
        h.Sdk.UnclaimedDeposits.Add(Stuck(requiredFeeSats: 6_000, amountSats: 10_000));

        var outcome = await h.Service.ClaimAsync(StoreId, TxId, 0, null, Ct);

        Assert.Equal(SparkClaimStatus.Refused, outcome.Status);
        Assert.Contains("50", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(h.Sdk.ClaimCalls);
    }

    /// <summary>
    /// A fee under both limits goes through, so the guards above are not just refusing everything.
    /// </summary>
    [Fact]
    public async Task A_fee_within_both_limits_is_claimed()
    {
        var h = Harness();
        h.Sdk.UnclaimedDeposits.Add(Stuck(requiredFeeSats: 4_000, amountSats: 60_000));

        var outcome = await h.Service.ClaimAsync(StoreId, TxId, 0, null, Ct);

        Assert.True(outcome.Succeeded);
        Assert.Single(h.Sdk.ClaimCalls);
        Assert.Empty(h.Sdk.UnclaimedDeposits);
    }

    /// <summary>
    /// An immature deposit is not claimable, and is told apart from a stuck one.
    /// </summary>
    /// <remarks>
    /// Spark claims it automatically at three confirmations. Reporting it as a failure would send a merchant
    /// chasing a problem that does not exist.
    /// </remarks>
    [Fact]
    public async Task An_immature_deposit_is_refused_with_an_explanation_rather_than_an_error()
    {
        var h = Harness();
        h.Sdk.UnclaimedDeposits.Add(new SparkDepositInfo(TxId, 0, 60_000, IsMature: false));

        var outcome = await h.Service.ClaimAsync(StoreId, TxId, 0, null, Ct);

        Assert.Equal(SparkClaimStatus.Refused, outcome.Status);
        Assert.Contains("nothing to fix", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(h.Sdk.ClaimCalls);
    }

    /// <summary>
    /// A deposit claimed since the page was rendered is reported as already gone, not as an error.
    /// </summary>
    /// <remarks>
    /// The state is re-read rather than trusted from the form, so a stale page cannot claim twice — and the
    /// most likely reason a deposit is missing from the list is that it succeeded.
    /// </remarks>
    [Fact]
    public async Task Claiming_a_deposit_that_is_no_longer_unclaimed_says_so()
    {
        var h = Harness();

        var outcome = await h.Service.ClaimAsync(StoreId, TxId, 0, null, Ct);

        Assert.Equal(SparkClaimStatus.Refused, outcome.Status);
        Assert.Contains("already have been claimed", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(h.Sdk.ClaimCalls);
    }

    /// <summary>
    /// A failed claim is reported and leaves the deposit claimable.
    /// </summary>
    /// <remarks>
    /// Reported rather than thrown, because a claim that failed broadcast nothing — so the deposit must stay in
    /// the list for another attempt rather than disappearing behind an exception.
    /// </remarks>
    [Fact]
    public async Task A_claim_Spark_refuses_leaves_the_deposit_where_it_was()
    {
        var h = Harness();
        h.Sdk.UnclaimedDeposits.Add(Stuck(requiredFeeSats: 420));
        h.Sdk.ClaimFailsWith = "the service provider declined";

        var outcome = await h.Service.ClaimAsync(StoreId, TxId, 0, null, Ct);

        Assert.Equal(SparkClaimStatus.Failed, outcome.Status);
        Assert.Single(h.Sdk.UnclaimedDeposits);
    }

    /// <summary>
    /// A store with no running wallet cannot claim, and is told that rather than a refusal.
    /// </summary>
    [Fact]
    public async Task A_store_with_no_running_wallet_cannot_claim()
    {
        var h = Harness(walletRunning: false);

        var outcome = await h.Service.ClaimAsync(StoreId, TxId, 0, null, Ct);

        Assert.Equal(SparkClaimStatus.Unavailable, outcome.Status);
    }

    #endregion

    #region The SDK's claim shapes (0.26)

    /// <summary>
    /// A deposit the SDK already credited early is not offered as unclaimed.
    /// </summary>
    /// <remarks>
    /// Since 0.26 an early-claimed deposit stays in <c>ListUnclaimedDeposits</c>, marked <c>Claimed</c>, until the
    /// provider spends its output. Listed as unclaimed it would read as money still on its way — and once
    /// matured, as a stuck deposit inviting a second claim of funds already in the balance.
    /// </remarks>
    [Fact]
    public void A_deposit_claimed_early_is_not_listed_as_unclaimed()
    {
        Assert.False(SparkSdkClient.IsUnclaimed(SdkDeposit(new InstantClaimStatus.Claimed())));
        Assert.True(SparkSdkClient.IsUnclaimed(SdkDeposit(new InstantClaimStatus.Submitted("claim-1"))));
        Assert.True(SparkSdkClient.IsUnclaimed(SdkDeposit(new InstantClaimStatus.Declined(null, 1))));
        Assert.True(SparkSdkClient.IsUnclaimed(SdkDeposit(null)));
    }

    /// <summary>
    /// A claim that settles later is a success; a deferred one is a failure that says why.
    /// </summary>
    [Fact]
    public void Claim_outcomes_map_onto_success_and_failure()
    {
        var submitted = SparkSdkClient.MapClaimOutcome(new ClaimDepositOutcome.Submitted(), new StubBolt11Parser());
        Assert.True(submitted.Succeeded);
        Assert.Null(submitted.Payment);
        Assert.Null(submitted.Error);

        var deferred = SparkSdkClient.MapClaimOutcome(
            new ClaimDepositOutcome.Deferred(new ClaimDeferredReason.MaxFeeExceeded(1_200, 800)),
            new StubBolt11Parser());
        Assert.False(deferred.Succeeded);
        Assert.Contains("1,200 sat", deferred.Error);
        Assert.Contains("800 sat", deferred.Error);
    }

    private static DepositInfo SdkDeposit(InstantClaimStatus? status) =>
        new(TxId, 0, 60_000, isMature: false, refundTx: null!, refundTxId: null!, refundState: null!,
            claimError: null!, instantClaimStatus: status!, maxClaimFee: null!);

    #endregion

    private static SparkDepositInfo Stuck(long requiredFeeSats, long amountSats = 60_000) =>
        new(TxId, 0, amountSats, IsMature: true,
            new SparkDepositClaimFailure(
                SparkDepositClaimFailureKind.MaxFeeExceeded,
                "The fee needed to claim this deposit is above the limit this store allows.",
                requiredFeeSats,
                RequiredFeeRateSatPerVbyte: 12));

    private sealed record TestHarness(SparkDepositService Service, FakeSparkSdkClient Sdk, WriteLog Log);

    private static TestHarness Harness(
        Action<SparkDepositSettings>? configure = null,
        bool walletRunning = true)
    {
        var log = new WriteLog();
        var sdk = new FakeSparkSdkClient(log);
        var settings = new FakeSparkStoreSettingsStore();
        var deposits = new SparkDepositSettings();
        configure?.Invoke(deposits);

        settings.Settings[StoreId] = new SparkSettings
        {
            ProtectedMnemonic = "protected",
            PaymentKey = "key",
            Deposits = deposits
        };

        var runtime = new FakeSparkStoreRuntime();
        if (walletRunning)
            runtime.Clients[StoreId] = sdk;

        return new TestHarness(
            new SparkDepositService(settings, runtime, NullLogger<SparkDepositService>.Instance), sdk, log);
    }
}
