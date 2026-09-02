using System.Numerics;
using Breez.Sdk.Spark;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// Sweeping to an EVM address: the rail, the units, the guards, and what replaces the crash-safety primitive.
/// </summary>
/// <remarks>
/// <para>
/// Split from <c>SparkSweepEngineTests</c> because almost nothing is shared beyond the engine's sequence. A
/// cross-chain sweep is quoted differently (the quote debits more than it is asked for), guarded differently
/// (the fee is a ratio inside the destination asset, not a flat number of satoshi), denominated differently
/// when Stable Balance is on, and — on that path — <em>recovered</em> differently, because the SDK rejects an
/// idempotency key on any send with a token leg.
/// </para>
/// <para>
/// Mainnet throughout, because cross-chain is hard-gated to it: the SDK throws at connect on any other network.
/// </para>
/// </remarks>
public class SparkCrossChainSweepTests
{
    private const string StoreId = "store-1";
    private const string Evm = "0x742d35Cc6634C0532925a3b844Bc454e4438f44e";
    private static readonly DateTimeOffset Origin = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #region The happy path, funded from satoshi

    [Fact]
    public async Task A_cross_chain_sweep_sends_the_sweepable_balance_and_records_the_provider()
    {
        var h = Harness(balanceSats: 500_000);

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Swept, result.Kind);

        var send = Assert.Single(h.Sdk.CrossChainSendCalls);
        Assert.Equal(Evm, send.RecipientAddress);
        Assert.Equal(SparkCrossChainProvider.Orchestra, send.Route.Provider);
        Assert.Equal("arbitrum", send.Route.Chain);
        Assert.Equal("USDT", send.Route.Asset);

        // Denominated in satoshi, because Stable Balance is off and the sweep is funded from the sats balance —
        // less the 1% margin the provider's overpay comes out of. Asking for the whole 500,000 would be asking
        // for a refusal, because the quote debits more than it is asked for.
        var amount = Assert.IsType<SparkSendAmount.Bitcoin>(send.Amount);
        Assert.Equal(495_000, amount.Sats);

        // The record says which rail, which provider, and what should arrive.
        var record = result.Record!;
        Assert.Equal(SweepDestinationKind.EvmAddress, record.DestinationKind);
        Assert.Equal(SparkCrossChainProvider.Orchestra, record.Provider);
        Assert.Equal("arbitrum", record.DestinationChain);
        Assert.Equal("USDT", record.DestinationAsset);
        Assert.Equal(6, record.DestinationAssetDecimals);
        Assert.NotNull(record.EstimatedOutBaseUnits);
        Assert.Equal(SparkConversionStatus.Pending, record.ConversionStatus);

        // Sent, not Confirmed. The payment reaching the provider is not the money arriving on Arbitrum, and
        // there is no event that will say when it does.
        Assert.Equal(SweepRecordStatus.Sent, record.Status);
        Assert.Null(record.DeliveredAmountBaseUnits);
    }

    /// <summary>
    /// The store's chosen slippage reaches the SDK, rather than the SDK's own looser fallback.
    /// </summary>
    /// <remarks>
    /// Worth pinning because the failure is invisible: leaving the SDK's cross-chain slippage unset falls back
    /// to 100 bps, ten times Stable Balance's default on a neighbouring config, and on a $35 sweep that is up to
    /// $0.35 of tolerated slippage nobody chose.
    /// </remarks>
    [Fact]
    public async Task The_configured_slippage_is_what_the_send_carries()
    {
        var h = Harness(balanceSats: 500_000, configure: s => s.CrossChainSlippageBps = 25);

        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(25u, Assert.Single(h.Sdk.CrossChainSendCalls).MaxSlippageBps);
    }

    [Fact]
    public async Task An_unset_slippage_uses_the_plugin_default_rather_than_the_SDKs()
    {
        var h = Harness(balanceSats: 500_000, configure: s => s.CrossChainSlippageBps = null);

        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(
            SweepSettings.DefaultCrossChainSlippageBps,
            Assert.Single(h.Sdk.CrossChainSendCalls).MaxSlippageBps);
    }

    /// <summary>
    /// A sats-funded cross-chain send carries the record's idempotency key.
    /// </summary>
    /// <remarks>
    /// The path on which the idempotency-key primitive still holds: the first leg is a Spark <em>sats</em>
    /// transfer to the provider's deposit address, so the key is accepted and becomes the payment id. The record says so, which
    /// is what lets recovery choose the point-lookup branch.
    /// </remarks>
    [Fact]
    public async Task A_satoshi_funded_send_keeps_the_idempotency_key_and_says_so()
    {
        var h = Harness(balanceSats: 500_000);

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        var record = result.Record!;
        Assert.True(record.IdempotencyKeyAccepted);
        Assert.Equal(record.IdempotencyKey, Assert.Single(h.Sdk.CrossChainSendCalls).IdempotencyKey);
    }

    /// <summary>
    /// The record, with its provider quote id, is written before the send is issued.
    /// </summary>
    /// <remarks>
    /// Asserted as an ordering on the shared monotonic write log rather than as two call counts, which would
    /// pass just as happily with the writes reversed. On this rail the quote id is belt-and-braces; on the token
    /// rail it is the only handle on the send, and it is written by the same line.
    /// </remarks>
    [Fact]
    public async Task The_record_and_its_provider_quote_id_exist_before_anything_is_sent()
    {
        var h = Harness(balanceSats: 500_000);

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);
        var key = result.Record!.IdempotencyKey;

        var added = h.Log.Entries.IndexOf($"sweep:add:{key}");
        var sent = h.Log.Entries.IndexOf($"sdk:cc-send:{key}");

        Assert.True(added >= 0, Trace(h));
        Assert.True(sent >= 0, Trace(h));
        Assert.True(added < sent, "the cross-chain send was issued before its record existed: " + Trace(h));

        // The quote id was on the row at insert time, not patched in afterwards.
        Assert.False(string.IsNullOrEmpty(h.Records.Records[key].ProviderQuoteId));

        // And the engine's own ordering: sync before the balance read that sizes the sweep.
        var sync = h.Log.Entries.IndexOf("sdk:sync");
        var read = h.Log.Entries.IndexOf("sdk:getinfo:synced");
        Assert.True(sync >= 0 && read > sync, Trace(h));
    }

    #endregion

    #region The overpay, which is the guard a naive sweep fails

    /// <summary>
    /// A sweep is refused when the quote would debit more than is sweepable.
    /// </summary>
    /// <remarks>
    /// <b>The single most likely way to get a cross-chain sweep wrong.</b> The provider overpays the source leg
    /// to absorb its fee and slippage, so a sweep sized to exactly the sweepable balance cannot be funded — and
    /// the amount the engine asks for is, by construction, exactly the sweepable balance. Without this guard the
    /// refusal would come from the service provider, after a record had been written and with the outcome
    /// unknown, rather than before anything moved.
    /// </remarks>
    [Fact]
    public async Task A_sweep_whose_overpay_exceeds_the_balance_is_refused_before_it_is_sent()
    {
        var h = Harness(balanceSats: 200_000, configure: s => s.ReserveSats = 0);

        // A pad far wider than the 1% margin the engine leaves. Not contrived: the pad absorbs the provider's
        // fee as well as slippage, so it moves with the market and is not derivable from any setting — which is
        // exactly why the quote has to be checked rather than trusted to fit.
        h.Sdk.CrossChainOverpayBps = 1_500;

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Equal(SweepRefusalCode.CrossChainOverpayExceedsBalance, result.Record!.RefusalCode);
        Assert.Empty(h.Sdk.CrossChainSendCalls);
        Assert.Equal(200_000, h.Sdk.BalanceSats);
    }

    /// <summary>
    /// A reserve large enough to cover the overpay lets the same sweep through.
    /// </summary>
    /// <remarks>
    /// The positive counterpart to the refusal above, and the reason it is needed: without it, a guard that
    /// refused unconditionally would pass the test above and never sweep anything.
    /// </remarks>
    [Fact]
    public async Task A_reserve_that_covers_the_overpay_lets_the_sweep_through()
    {
        var h = Harness(balanceSats: 200_000, configure: s => s.ReserveSats = 5_000);

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Swept, result.Kind);
        var send = Assert.Single(h.Sdk.CrossChainSendCalls);
        // (200,000 - 5,000 reserve) less the 1% margin.
        var requested = Assert.IsType<SparkSendAmount.Bitcoin>(send.Amount).Sats;
        Assert.Equal(193_050, requested);

        // Debited by amountIn, which is more than the amount asked for — so what is left is less than the
        // reserve plus the margin, and strictly less than it would be if only `requested` had been taken.
        Assert.True(
            h.Sdk.BalanceSats < 200_000 - requested,
            $"the wallet was debited only the requested amount ({h.Sdk.BalanceSats} left)");
    }

    #endregion

    #region Routing

    /// <summary>
    /// An empty route list is a configuration fault, not an unreachable destination.
    /// </summary>
    /// <remarks>
    /// <b>The trap the spike flagged first.</b> With the SDK's cross-chain configuration unset,
    /// <c>GetCrossChainRoutes</c> returns an empty array and <em>no error</em> — the identical call went from 0
    /// routes to 54 purely by setting the config. Reported as "no route to this chain" it looks like something a
    /// merchant fixes by choosing another chain, and they would try every one of them. The refusal code has to
    /// be the one that says the feature is off.
    /// </remarks>
    [Fact]
    public async Task No_routes_at_all_is_reported_as_the_feature_being_unavailable()
    {
        var h = Harness(balanceSats: 500_000);
        h.Sdk.CrossChainConfigured = false;

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Equal(SweepRefusalCode.CrossChainUnavailable, result.Record!.RefusalCode);
        Assert.NotEqual(SweepRefusalCode.NoCrossChainRoute, result.Record.RefusalCode);

        // And the message does not send the merchant off changing chains.
        Assert.Contains("not configured", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(h.Sdk.CrossChainSendCalls);
    }

    /// <summary>
    /// The production client is what turns an empty route table into that refusal.
    /// </summary>
    /// <remarks>
    /// The test above drives the fake, which raises the exception itself — so on its own it would pass against
    /// a client that returned an empty list and let the engine call it "no route to this chain". This asserts
    /// the rule where it actually lives. Everything else on the client needs a live native handle; this one
    /// decision is pure policy, which is why it is a static worth calling directly.
    /// </remarks>
    [Fact]
    public void The_client_refuses_an_empty_route_table_rather_than_returning_one()
    {
        var thrown = Assert.Throws<SparkCrossChainNotConfiguredException>(
            () => SparkSdkClient.RequireRoutes(0, Evm));

        Assert.Equal(Evm, thrown.Address);
        // The message has to say this is a configuration fault, because the obvious reading — "no route to this
        // chain" — sends a merchant off trying every chain there is.
        Assert.Contains("configuration fault", thrown.Message, StringComparison.OrdinalIgnoreCase);

        // And a non-empty table is not refused, so the rule is not simply "always throw".
        SparkSdkClient.RequireRoutes(1, Evm);
    }

    /// <summary>
    /// A destination whose only routes are Boltz is reported as having no route.
    /// </summary>
    /// <remarks>
    /// Every Boltz prepare currently fails — three chains, three amounts, six attempts, all with
    /// <c>BTC/TBTC pair not found</c> — from a machine that could reach Boltz perfectly well. So the route list
    /// cannot be trusted as offered, and a Boltz-only destination must be refused <em>before</em> a record
    /// exists rather than discovered at prepare with the outcome unknown.
    /// </remarks>
    [Fact]
    public async Task A_destination_only_Boltz_serves_is_refused_without_attempting_it()
    {
        var h = Harness(balanceSats: 500_000, configure: s =>
        {
            s.EvmChain = "polygon";
            s.EvmAsset = "USDT0";
        });

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Equal(SweepRefusalCode.NoCrossChainRoute, result.Record!.RefusalCode);

        // Not even quoted: the provider filter runs before the prepare that would have failed.
        Assert.Empty(h.Sdk.CrossChainQuoteCalls);
        Assert.Empty(h.Sdk.CrossChainSendCalls);
    }

    /// <summary>
    /// The asset is matched exactly, so USDT0 is never substituted for USDT.
    /// </summary>
    /// <remarks>
    /// <c>USDT0</c> is the LayerZero omnichain token: a genuinely different asset that a merchant expecting
    /// Tether will not accept. A prefix or <c>Contains</c> match would deliver it silently, and the merchant
    /// would find out on the far side.
    /// </remarks>
    [Fact]
    public async Task USDT0_is_not_delivered_to_a_store_that_asked_for_USDT()
    {
        var h = Harness(balanceSats: 500_000, configure: s => s.EvmChain = "polygon");

        // Polygon carries only USDT0 in the route table, and the store asked for USDT.
        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Equal(SweepRefusalCode.NoCrossChainRoute, result.Record!.RefusalCode);
        Assert.Empty(h.Sdk.CrossChainSendCalls);

        // The refusal names the chains that do carry it, so the merchant has somewhere to go.
        Assert.Contains("arbitrum", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// An amount below the provider's own floor is a refusal, not a fault.
    /// </summary>
    /// <remarks>
    /// The floor is enforced server-side and the SDK exposes no getter for it — the spike had to binary-search
    /// it — so "too small" arrives as a <c>NetworkException</c> carrying the provider's prose. Classified as a
    /// network failure it would tell a merchant the provider is unreachable when their only problem is a small
    /// balance.
    /// </remarks>
    [Fact]
    public async Task An_amount_below_the_providers_floor_is_reported_as_a_minimum_not_an_outage()
    {
        var h = Harness(
            balanceSats: 1_200,
            configure: s =>
            {
                s.MinimumSweepSats = Constants.MinimumOnchainSendSats;
                s.BalanceThresholdSats = 1;
            });

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Equal(SweepRefusalCode.BelowMinimumSweep, result.Record!.RefusalCode);
        Assert.Empty(h.Sdk.CrossChainSendCalls);
    }

    #endregion

    #region The fee guard

    /// <summary>
    /// The fee ceiling is applied to the provider's spread, measured inside the destination asset.
    /// </summary>
    /// <remarks>
    /// A ratio of gross to net, because the quote reports its fee in destination-asset base units and its
    /// service fee in a third asset again — adding them would be adding different currencies. The ratio is
    /// unit-free, which is what lets one guard cover both funding rails.
    /// </remarks>
    [Fact]
    public async Task A_spread_above_the_stores_ceiling_is_refused_and_nothing_is_sent()
    {
        var h = Harness(balanceSats: 500_000, configure: s => s.MaxFeePercent = 0.1);

        // 3.4% spread against a 0.1% ceiling.
        h.Sdk.CrossChainFeeBps = 340;

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Equal(SweepRefusalCode.FeeAboveLimit, result.Record!.RefusalCode);
        Assert.Equal(500_000, h.Sdk.BalanceSats);
    }

    [Fact]
    public async Task A_spread_within_the_ceiling_is_accepted()
    {
        var h = Harness(balanceSats: 500_000, configure: s => s.MaxFeePercent = 1.0);
        h.Sdk.CrossChainFeeBps = 34;

        Assert.Equal(SweepOutcomeKind.Swept, (await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct)).Kind);
    }

    /// <summary>
    /// The hard backstop applies to cross-chain sweeps too, whatever the store configured.
    /// </summary>
    /// <remarks>
    /// The cooperative-exit path already has this and the reason carries over unchanged: sweeping is automatic,
    /// so there has to be a number above which the plugin refuses to pay, and it must not be removable through
    /// configuration. A percentage of zero falls back to the default rather than meaning "no limit".
    /// </remarks>
    [Fact]
    public async Task Clearing_the_percentage_falls_back_to_the_default_rather_than_allowing_anything()
    {
        var h = Harness(balanceSats: 500_000, configure: s =>
        {
            // What a merchant would have to do to disable it, if it were disableable.
            s.MaxFeePercent = 0;
            s.MaxFeeFlatSats = long.MaxValue;
        });

        // 5%: past the 3% default the zero falls back to, and deliberately inside the value guard's 10% band so
        // that what refuses this is the fee ceiling and not something else.
        h.Sdk.CrossChainFeeBps = 500;

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Equal(SweepRefusalCode.FeeAboveLimit, result.Record!.RefusalCode);
        Assert.Empty(h.Sdk.CrossChainSendCalls);
    }

    /// <summary>
    /// A merchant-configured ceiling of 90% still does not authorise losing 20% of a sweep.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The test above only exercises the <em>fallback</em>: a percentage of zero becomes the 3% default, which
    /// catches a 5% spread on its own and would do so with every backstop deleted. This is the case that needs
    /// one — a merchant who has explicitly typed 90%.
    /// </para>
    /// <para>
    /// <b>Which guard refuses it is worth stating plainly.</b> On the cooperative-exit path the answer is
    /// <see cref="SweepSettings.HardMaxFeePercent"/>, the 50% line no configuration lifts. On this path that
    /// line is unreachable, because the value guard's 10% band is strictly tighter and fires first — so the
    /// backstop is subsumed here rather than redundant, and the property that actually holds is the one
    /// asserted: no configuration authorises an absurd loss, whichever guard is the one that says so.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_configured_ceiling_of_ninety_percent_still_does_not_authorise_an_absurd_loss()
    {
        var h = Harness(balanceSats: 500_000, configure: s => s.MaxFeePercent = 90);

        // 20%: comfortably inside the merchant's stated 90% ceiling.
        h.Sdk.CrossChainFeeBps = 2_000;

        var refused = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, refused.Kind);
        Assert.Equal(SweepRefusalCode.CrossChainValueUnverifiable, refused.Record!.RefusalCode);
        Assert.Empty(h.Sdk.CrossChainSendCalls);
        Assert.Equal(500_000, h.Sdk.BalanceSats);

        // And a spread inside every line is still allowed, so this is a line rather than a blanket refusal.
        var allowed = Harness(balanceSats: 500_000, configure: s => s.MaxFeePercent = 90);
        allowed.Sdk.CrossChainFeeBps = 200;

        Assert.Equal(
            SweepOutcomeKind.Swept,
            (await allowed.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct)).Kind);
    }

    /// <summary>
    /// A quote that claims to deliver at least as much as it takes in is refused rather than read.
    /// </summary>
    /// <remarks>
    /// Not a hypothetical shape of paranoia: computing a fee from it yields a negative number, and a negative
    /// fee passes every ceiling there is. Refusing is the only reading that cannot authorise an unbounded
    /// spread.
    /// </remarks>
    [Fact]
    public async Task A_quote_that_cannot_be_read_as_a_quote_is_refused()
    {
        var h = Harness(balanceSats: 500_000);
        // A negative spread: estimatedOut above assetAmountIn.
        h.Sdk.CrossChainFeeBps = -100;

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Equal(SweepRefusalCode.CrossChainQuoteUnusable, result.Record!.RefusalCode);
        Assert.Empty(h.Sdk.CrossChainSendCalls);
    }

    /// <summary>
    /// The guard runs against the quote the send commits to, not the earlier estimate.
    /// </summary>
    /// <remarks>
    /// A cross-chain quote lives about a minute and the spread it names is not a promise. A caller that checked
    /// only the pre-flight quote would commit to whatever the send came back with — so the approval callback
    /// inside the send is the enforcement point, and a refusal there means nothing was sent.
    /// </remarks>
    [Fact]
    public async Task A_spread_that_widens_between_the_quote_and_the_send_is_refused_at_the_send()
    {
        var h = Harness(balanceSats: 500_000, configure: s => s.MaxFeePercent = 1.0);
        h.Sdk.CrossChainFeeBps = 34;

        // Widen it the moment the pre-flight quote has been taken.
        h.Sdk.CrossChainQuoteCalls.Clear();
        // Widened to 5%: past the store's 1% ceiling and inside the value guard's band, so the refusal that
        // follows is unambiguously the fee guard running against the committed quote.
        var widening = new WideningFee(h.Sdk, whenQuoted: 34, thenBps: 500);

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.True(widening.Widened, "the pre-flight quote was never taken, so the test proved nothing");
        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Equal(SweepRefusalCode.FeeAboveLimit, result.Record!.RefusalCode);

        // The send was reached and vetoed, so nothing left the wallet.
        Assert.Single(h.Sdk.CrossChainSendCalls);
        Assert.Equal(500_000, h.Sdk.BalanceSats);
        Assert.Equal(SweepRecordStatus.Refused, result.Record.Status);
    }

    #endregion

    #region The value guard, which the spread guard structurally cannot be

    /// <summary>
    /// A quote priced far below the market is refused, however small a spread it claims.
    /// </summary>
    /// <remarks>
    /// <b>The hole this closes.</b> The spread guard compares <c>assetAmountIn</c> against <c>estimatedOut</c> —
    /// both from the same quote and in the same asset — so it bounds what the provider <em>says</em> it is
    /// charging and never the rate it applies. The quote below offers about $100 of USDT for 500,000 satoshi
    /// (about $320), states a 0.34% spread, clears the 3% default and the 50% backstop, and loses two-thirds of
    /// the merchant's money. Only a price from outside the quote can see it.
    /// </remarks>
    [Fact]
    public async Task A_quote_priced_far_below_the_market_is_refused_despite_a_tiny_stated_spread()
    {
        var h = Harness(balanceSats: 500_000);

        // Roughly a third of the real rate: ~$100 of USDT for ~$320 of satoshi.
        h.Sdk.CrossChainRate = (100_000_000, 500_000);
        h.Sdk.CrossChainFeeBps = 34;

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Equal(SweepRefusalCode.CrossChainValueUnverifiable, result.Record!.RefusalCode);
        Assert.Empty(h.Sdk.CrossChainSendCalls);
        Assert.Equal(500_000, h.Sdk.BalanceSats);

        // The stated spread really was tiny, so the refusal came from the value check and not from the fee one.
        var quoted = Assert.Single(h.Sdk.CrossChainQuoteCalls);
        Assert.NotNull(quoted);
    }

    /// <summary>
    /// A fairly priced quote is not refused, so the value guard is a band rather than a blanket.
    /// </summary>
    [Fact]
    public async Task A_fairly_priced_quote_passes_the_value_guard()
    {
        var h = Harness(balanceSats: 500_000);

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Swept, result.Kind);
        Assert.Equal(new[] { (StoreId, "USD") }, h.Oracle.Calls.Distinct());
    }

    /// <summary>
    /// An amount too large to convert to dollars is refused, never waved through unchecked.
    /// </summary>
    /// <remarks>
    /// The fail-open this closes: the conversion used to report an overflowing value as zero, and the guard
    /// read zero as "the quote-shape check already refused this" — so the quotes at their most absurd were
    /// exactly the ones that skipped the value check. A value the guard cannot express is a quote it cannot
    /// vouch for, and a quote it cannot vouch for does not send.
    /// </remarks>
    [Fact]
    public async Task An_amount_too_large_to_value_is_refused_rather_than_skipping_the_value_guard()
    {
        // 10^40 base units of a 6-decimal token: 10^34 whole dollars, far beyond what a decimal can carry.
        var h = Harness(balanceSats: 500_000, stableBalance: BigInteger.Pow(10, 40));

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Equal(SweepRefusalCode.CrossChainValueUnverifiable, result.Record!.RefusalCode);
        Assert.Contains("too large", result.Reason, StringComparison.Ordinal);
        Assert.Empty(h.Sdk.CrossChainSendCalls);
    }

    /// <summary>
    /// No rate means no sweep, rather than a sweep nobody checked.
    /// </summary>
    /// <remarks>
    /// BTCPay's rate providers are third-party HTTP, so "no rate right now" is ordinary. Waving the sweep
    /// through would make the guard bypassable by exactly the kind of hiccup during which a bad quote is
    /// hardest to notice. Refusing costs nothing: sweeping is automatic and the next pass tries again.
    /// </remarks>
    [Fact]
    public async Task A_sweep_is_refused_when_no_rate_is_available_to_check_it_against()
    {
        var h = Harness(balanceSats: 500_000);
        h.Oracle.Unavailable = true;

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Equal(SweepRefusalCode.CrossChainValueUnverifiable, result.Record!.RefusalCode);
        Assert.Empty(h.Sdk.CrossChainSendCalls);
    }

    /// <summary>
    /// A token-funded sweep is value-checked without any rate at all.
    /// </summary>
    /// <remarks>
    /// Both sides are USD-pegged, so the comparison is arithmetic and needs no price — which also means a
    /// rate-provider outage cannot stop a Stable Balance store from sweeping. Asserted by making the rate
    /// unavailable and checking the sweep still goes.
    /// </remarks>
    [Fact]
    public async Task A_token_funded_sweep_needs_no_rate_because_both_sides_are_dollars()
    {
        var h = Harness(balanceSats: 500_000, stableBalance: 35_600_000);
        h.Oracle.Unavailable = true;

        Assert.Equal(
            SweepOutcomeKind.Swept, (await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct)).Kind);
        Assert.Empty(h.Oracle.Calls);
    }

    /// <summary>
    /// A token-funded sweep delivering far less than it takes is still refused.
    /// </summary>
    /// <remarks>
    /// The counterpart to the test above: rate-free must not mean unchecked.
    /// </remarks>
    [Fact]
    public async Task A_token_funded_sweep_that_would_lose_most_of_its_value_is_refused()
    {
        var h = Harness(balanceSats: 500_000, stableBalance: 35_600_000);
        // Half the dollars in, delivered out.
        h.Sdk.CrossChainFeeBps = 5_000;

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Equal(SweepRefusalCode.CrossChainValueUnverifiable, result.Record!.RefusalCode);
        Assert.Empty(h.Sdk.CrossChainSendCalls);
    }

    /// <summary>
    /// An asset whose value the plugin cannot establish is refused rather than assumed.
    /// </summary>
    /// <remarks>
    /// The value check treats one unit of the destination asset as one dollar, which is only true because the
    /// plugin advertises USD stablecoins. For anything else it has no basis at all — and an unchecked quote is
    /// the whole problem — so it declines rather than guessing.
    /// </remarks>
    [Fact]
    public async Task An_asset_the_plugin_cannot_value_is_refused_rather_than_assumed_to_be_a_dollar()
    {
        var h = Harness(balanceSats: 500_000, configure: s => s.EvmAsset = "WBTC");
        h.Sdk.CrossChainRoutes.Add(new SparkCrossChainRoute(
            SparkCrossChainProvider.Orchestra, "arbitrum", "42161", "WBTC",
            "0x2f2a2543b76a4166549f7aab2e75bef0aefc5b0f", 8,
            [SparkCrossChainSource.Bitcoin], "route:orchestra:arbitrum:WBTC"));

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Equal(SweepRefusalCode.CrossChainValueUnverifiable, result.Record!.RefusalCode);
        Assert.Contains("WBTC", result.Reason, StringComparison.Ordinal);
        Assert.Empty(h.Sdk.CrossChainSendCalls);
    }

    #endregion

    #region Stable Balance funds the sweep in a different unit

    /// <summary>
    /// With Stable Balance active the sweep is funded from the token, in the token's own units.
    /// </summary>
    /// <remarks>
    /// <b>The unit trap, end to end.</b> The wallet holds 500,000 sats <em>and</em> $35.60 of USDB; a sweep that
    /// read the wrong balance would send four orders of magnitude of the wrong thing. What decides is the union
    /// the amount is carried in, and the assertion is on its case, not on a number that could belong to either.
    /// </remarks>
    [Fact]
    public async Task A_stable_balance_store_sweeps_the_token_and_not_the_satoshi_balance()
    {
        var h = Harness(balanceSats: 500_000, stableBalance: 35_600_000);

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Swept, result.Kind);

        var send = Assert.Single(h.Sdk.CrossChainSendCalls);
        var amount = Assert.IsType<SparkSendAmount.Token>(send.Amount);
        Assert.Equal(FakeSparkSdkClient.Usdb, amount.Identifier);
        Assert.Equal(6u, amount.Decimals);

        // 99% of the balance, leaving room for the provider's overpay — the sats balance is untouched.
        Assert.Equal(new BigInteger(35_244_000), amount.BaseUnits);
        Assert.Equal(500_000, h.Sdk.BalanceSats);

        // And the record reports it in the token's units rather than as a satoshi figure of zero.
        Assert.Equal(0, result.Record!.AmountSats);
        Assert.Equal("35244000", result.Record.SourceAmountBaseUnits);
        Assert.Equal("35.244", result.Record.DescribeAmount());
    }

    /// <summary>
    /// The sats-side balance check is not applied to a token-funded sweep.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>amountIn</c> is a satoshi figure whose meaning was only ever observed for a sats-funded send; what it
    /// holds for a token source is unverified. The fake returns <c>long.MaxValue</c> there deliberately, so a
    /// caller that applied the sats balance check unconditionally would refuse every token sweep.
    /// </para>
    /// <para>
    /// This is the test that fails if someone later "tidies" the guard into applying to both branches.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_unreadable_satoshi_figure_on_a_token_quote_does_not_refuse_the_sweep()
    {
        var h = Harness(balanceSats: 1_000, stableBalance: 35_600_000);

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        // The sats balance is far below anything, and the quote's amountIn is long.MaxValue. Neither matters.
        Assert.Equal(SweepOutcomeKind.Swept, result.Kind);
        Assert.IsType<SparkSendAmount.Token>(Assert.Single(h.Sdk.CrossChainSendCalls).Amount);
    }

    /// <summary>
    /// A token balance below the store's floor is refused, in the token's own units.
    /// </summary>
    /// <remarks>
    /// The satoshi minimum is meaningless here — the amount is not in satoshi and there is no local exchange
    /// rate to convert one floor into the other — so the store carries a separate whole-unit floor. Refusing in
    /// dollars is also the only refusal a merchant can act on.
    /// </remarks>
    [Fact]
    public async Task A_token_balance_below_the_stable_floor_is_refused()
    {
        // $5 held against a $20 floor.
        var h = Harness(balanceSats: 500_000, stableBalance: 5_000_000);

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Equal(SweepRefusalCode.BelowMinimumSweep, result.Record!.RefusalCode);
        Assert.Contains("USDB", result.Reason, StringComparison.Ordinal);
        Assert.Empty(h.Sdk.CrossChainSendCalls);
    }

    /// <summary>
    /// A token-funded send carries no idempotency key, and the record says the key is not a payment id.
    /// </summary>
    /// <remarks>
    /// <b>This is the interaction that removes the idempotency key's crash-safety primitive.</b> The SDK rejects
    /// a key on any send with a token transfer leg — the fake throws exactly as it does — so the engine must not
    /// pass one, and must record that it did not. A row that claimed otherwise would send recovery looking for a
    /// payment id that was never issued, and the absence of it reads as "the sweep never happened".
    /// </remarks>
    [Fact]
    public async Task A_token_funded_send_carries_no_idempotency_key_and_the_record_records_that()
    {
        var h = Harness(balanceSats: 500_000, stableBalance: 35_600_000);

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Swept, result.Kind);
        Assert.Null(Assert.Single(h.Sdk.CrossChainSendCalls).IdempotencyKey);

        var record = result.Record!;
        Assert.False(record.IdempotencyKeyAccepted);
        // The quote id is what is left, and it is on the row.
        Assert.False(string.IsNullOrEmpty(record.ProviderQuoteId));
    }

    /// <summary>
    /// The SDK genuinely rejects a key on a token leg, so the engine's omission is load-bearing.
    /// </summary>
    /// <remarks>
    /// Without this the test above would pass against an engine that omitted the key for no reason, and a later
    /// change that started passing one would look harmless. Driving the seam directly is what says the omission
    /// is the difference between a send and a failure.
    /// </remarks>
    [Fact]
    public async Task Passing_an_idempotency_key_on_a_token_leg_fails_the_send()
    {
        var sdk = new FakeSparkSdkClient();
        var route = sdk.CrossChainRoutes[0];

        var rejection = await Assert.ThrowsAsync<SdkException.InvalidInput>(() => sdk.SendCrossChainAsync(
            route,
            Evm,
            SparkSendAmount.FromTokenBaseUnits(FakeSparkSdkClient.Usdb, 35_600_000, 6),
            maxSlippageBps: 50,
            idempotencyKey: Guid.NewGuid().ToString(),
            approveQuote: _ => null,
            Ct));

        Assert.Contains("Idempotency key is not supported", rejection.v1, StringComparison.Ordinal);

        // And the real client refuses before the SDK is even reached, so the reason is legible.
        Assert.Contains("cannot carry an idempotency key", NoKeyOnTokenLegMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// A Bitcoin-rail sweep says so when Stable Balance has taken the funds.
    /// </summary>
    /// <remarks>
    /// The interaction that would otherwise look like a broken plugin. With Stable Balance active the sats
    /// balance has been converted away, so a cooperative exit finds nothing and refuses on the economic floor —
    /// truthfully, and without mentioning the $300 of stablecoin sitting next to it. A merchant reading
    /// "only 400 sat is sweepable" has no way to connect the two.
    /// </remarks>
    [Fact]
    public async Task A_bitcoin_destination_explains_that_stable_balance_is_holding_the_funds()
    {
        var h = Harness(
            balanceSats: 400,
            stableBalance: 300_000_000,
            configure: s =>
            {
                s.DestinationMode = SweepDestinationMode.StaticAddress;
                s.StaticAddress = "bc1qar0srrr7xfkvy5l643lydnw9re59gtzzwf5mdq";
                s.BalanceThresholdSats = 1;
            });

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Equal(SweepRefusalCode.StableBalanceHoldsTheFunds, result.Record!.RefusalCode);
        Assert.Contains("300 USDB", result.Reason, StringComparison.Ordinal);
        Assert.Contains("Stable Balance", result.Reason, StringComparison.Ordinal);
    }

    #endregion

    /// <summary>
    /// A send is refused when its committed quote id could not be recorded.
    /// </summary>
    /// <remarks>
    /// The write can fail for a real reason: the row is no longer <c>Pending</c> because another pass resolved
    /// it, or a concurrent write got there first. Proceeding anyway would issue a payment that nothing has a
    /// recoverable record of — which on the token path means a payment nothing can ever match, the exact
    /// failure this whole scheme exists to prevent.
    /// </remarks>
    [Fact]
    public async Task A_send_whose_committed_quote_cannot_be_recorded_is_refused_rather_than_issued()
    {
        var h = Harness(balanceSats: 500_000, stableBalance: 35_600_000);
        h.Records.RefuseQuoteWrites = true;

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        // The send was reached and vetoed inside the approval callback, so nothing was issued.
        Assert.Single(h.Sdk.CrossChainSendCalls);
        Assert.Empty(h.Sdk.Payments);
        Assert.Contains("not sent", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A store whose stored minimum is below the cross-chain floor is refused at the send, not only on save.
    /// </summary>
    /// <remarks>
    /// The floor exists because a bridge's cost curve is punishing at the bottom — about 3.3% at the protocol
    /// minimum against about 0.34% at 50,000 satoshi. Form validation refuses to <em>store</em> a lower one, but
    /// this project's own doctrine calls that a courtesy: a settings blob can arrive from a restored backup, a
    /// hand edit, or a store that switched destination mode after configuring a coop-exit-shaped minimum. The
    /// engine has to apply the floor itself.
    /// </remarks>
    [Fact]
    public async Task A_stored_minimum_below_the_cross_chain_floor_is_still_refused_at_the_send()
    {
        // 20,000 sats sweepable: fine for a cooperative exit, below the 50,000-sat cross-chain floor. The stored
        // minimum says 1,000, which no form would have accepted.
        var h = Harness(
            balanceSats: 20_000,
            configure: s =>
            {
                s.MinimumSweepSats = 1_000;
                s.BalanceThresholdSats = 1;
            });

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Equal(SweepRefusalCode.BelowMinimumSweep, result.Record!.RefusalCode);
        Assert.Empty(h.Sdk.CrossChainSendCalls);

        // And the same stored minimum on a Bitcoin destination is honoured as written, so the floor is applied
        // to the rail it belongs to rather than to everything.
        var exit = Harness(
            balanceSats: 20_000,
            configure: s =>
            {
                s.MinimumSweepSats = 1_000;
                s.BalanceThresholdSats = 1;
                s.DestinationMode = SweepDestinationMode.StaticAddress;
                s.StaticAddress = "bc1qar0srrr7xfkvy5l643lydnw9re59gtzzwf5mdq";
                // A flat exit fee is 2,190 sats against 20,000, so the default 3%% ceiling would refuse this for
                // a reason that has nothing to do with the floor under test.
                s.MaxFeePercent = 20;
            });

        Assert.Equal(SweepOutcomeKind.Swept, (await exit.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct)).Kind);
    }

    /// <summary>
    /// The hard fee backstop is still a backstop, asserted where it is reachable.
    /// </summary>
    /// <remarks>
    /// <b>Through the engine it is not reachable</b>: the value guard's 10% band fires before the 50% line ever
    /// could, so no end-to-end test can exercise it and a mutation that deletes it survives. It is kept anyway —
    /// it costs nothing and it is the guard that holds if the value check is ever narrowed or bypassed — so it
    /// is asserted directly instead of pretending an integration test covers it.
    /// </remarks>
    [Fact]
    public void No_configured_percentage_lifts_the_hard_fee_backstop()
    {
        var settings = new SweepSettings { MaxFeePercent = 90 };
        var amount = SparkSendAmount.FromSats(500_000);

        // A 60% spread: inside the merchant's 90%, outside the 50% line.
        var refused = SparkSweepEngine.ApproveCrossChainQuote(settings, amount, Quote(1_000_000, 400_000), 500_000);

        Assert.NotNull(refused);
        Assert.Equal(SweepRefusalCode.FeeAboveLimit, refused!.Code);

        // And a 40% spread is inside both, so the line is a line.
        Assert.Null(SparkSweepEngine.ApproveCrossChainQuote(
            settings, amount, Quote(1_000_000, 600_000), 500_000));
    }

    private static SparkCrossChainQuote Quote(int assetIn, int estimatedOut) => new(
        new SparkCrossChainRoute(
            SparkCrossChainProvider.Orchestra, "arbitrum", "42161", "USDT", null, 6,
            [SparkCrossChainSource.Bitcoin], "handle"),
        Evm,
        AmountInSats: 500_000,
        AssetAmountIn: assetIn,
        EstimatedOut: estimatedOut,
        FeeAmount: assetIn - estimatedOut,
        ServiceFeeAmount: 0,
        ServiceFeeAsset: "USDC",
        SourceTransferFeeSats: 0,
        ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(60),
        ProviderQuoteId: "q",
        ProviderDepositAddress: "spark1");

    #region Recovery, which is different on each rail

    /// <summary>
    /// A crashed token sweep is resolved by scanning for its provider quote id, and never re-sent.
    /// </summary>
    /// <remarks>
    /// <b>The replacement for the crash-safety primitive, stated as behaviour.</b> There is no payment id to
    /// look up, because no idempotency key was accepted; the provider's quote id was written before the send and
    /// appears on the resulting payment's conversion info, so a scan is the only honest way to answer "did it
    /// send?". A retry would be a second payment, since nothing would deduplicate it.
    /// </remarks>
    [Fact]
    public async Task A_crashed_token_sweep_is_found_by_its_quote_id_rather_than_retried()
    {
        var h = Harness(balanceSats: 500_000, stableBalance: 35_600_000);

        // A row that was written, sent, and then lost track of — the shape a crash mid-send leaves behind.
        var row = new SweepRecord
        {
            IdempotencyKey = "row-key-not-a-payment-id",
            StoreId = StoreId,
            DestinationAddress = Evm,
            DestinationMode = SweepDestinationMode.EvmAddress,
            DestinationKind = SweepDestinationKind.EvmAddress,
            IdempotencyKeyAccepted = false,
            ProviderQuoteId = "q_arbitrum_35244000",
            Status = SweepRecordStatus.Pending,
            CreatedAt = Origin,
            AttemptCount = 1
        };
        await h.Records.AddAsync(row, Ct);

        // The payment the send actually produced, under an id nothing wrote down.
        h.Sdk.Seed(new SparkPayment(
            "sdk-id-nobody-recorded",
            SparkPaymentDirection.Send,
            SparkPaymentStatus.Completed,
            SparkPaymentMethod.Token,
            0, 0, Origin.AddSeconds(5), null, null, null, null, null,
            new SparkConversionState(
                SparkCrossChainProvider.Orchestra, SparkConversionStatus.Completed,
                "q_arbitrum_35244000", "order-1", 35_100_000, Evm, "arbitrum", "USDT", 6)));

        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        var resolved = await h.Records.GetAsync(StoreId, row.IdempotencyKey, Ct);
        Assert.Equal(SweepRecordStatus.Confirmed, resolved!.Status);
        Assert.Equal(SparkConversionStatus.Completed, resolved.ConversionStatus);
        Assert.Equal("35100000", resolved.DeliveredAmountBaseUnits);
        // The provider order id rides on the same recovery resolution as the delivered amount: it is the handle
        // a stuck-delivery investigation quotes at the provider, and the crash-recovery poll is exactly the
        // path that used to drop it (the initial send had already persisted it).
        Assert.Equal("order-1", resolved.ProviderOrderId);

        // Never looked the row's own key up as a payment id — it is not one.
        Assert.DoesNotContain(row.IdempotencyKey, h.Sdk.GetPaymentCalls);
        // And the scan was bounded rather than unpaged.
        Assert.All(h.Sdk.ListQueries, q => Assert.True(q.Limit is > 0 and <= 100));
    }

    /// <summary>
    /// A token sweep sent through the engine is recoverable afterwards, end to end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The test that would have caught the worst defect in this wave.</b> The engine takes a pre-flight quote
    /// to decide whether to sweep, and the SDK prepares <em>again</em> inside the send — Orchestra mints a fresh
    /// quote id per prepare, so the id from the pre-flight quote is not the id the send commits to. Persisting
    /// the wrong one meant recovery could never match: a crash after the send reached the provider would scan,
    /// find nothing, and close the row as never sent, for a sweep that had actually delivered USDT. The row
    /// would then leave the unresolved list, so the delivery was never polled either.
    /// </para>
    /// <para>
    /// Every earlier test seeded a payment by hand, so none of them could see it. This one sends through the
    /// engine and then recovers what the engine actually produced — and it only has teeth because the fake now
    /// mints a fresh quote id per prepare, as the provider does.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_token_sweep_sent_through_the_engine_is_found_again_by_its_committed_quote_id()
    {
        var h = Harness(balanceSats: 500_000, stableBalance: 35_600_000);

        var sent = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);
        Assert.Equal(SweepOutcomeKind.Swept, sent.Kind);

        var key = sent.Record!.IdempotencyKey;
        var stored = await h.Records.GetAsync(StoreId, key, Ct);

        // The id on the row is the one the send committed to, not the pre-flight one. Two prepares happened.
        Assert.Equal(2, h.Sdk.CrossChainQuoteCalls.Count + h.Sdk.ApprovedCrossChainQuotes.Count);
        var committed = Assert.Single(h.Sdk.ApprovedCrossChainQuotes);
        var preflight = Assert.Single(h.Sdk.CrossChainQuoteCalls);
        Assert.Equal(committed.ProviderQuoteId, stored!.ProviderQuoteId);

        // And they genuinely differ, so the assertion above is not passing by coincidence.
        var payment = h.Sdk.Payments.Last();
        Assert.NotEqual(preflight.Route.Chain + "-preflight", stored.ProviderQuoteId);
        Assert.NotNull(payment.Conversion!.ProviderQuoteId);
        Assert.Equal(payment.Conversion.ProviderQuoteId, stored.ProviderQuoteId);

        // The row is put back into the unresolved state a crash would have left it in, and recovery is asked to
        // find it. It has no idempotency key to look up, so this can only work by the quote id.
        Assert.True(await h.Records.TryResolveAsync(
            StoreId, key, [SweepRecordStatus.Sent],
            new SweepResolution(SweepRecordStatus.Pending, null, null, null, Origin), Ct));

        h.Sdk.GetPaymentCalls.Clear();
        h.Sdk.CrossChainSendCalls.Clear();
        h.Time.Advance(TimeSpan.FromMinutes(1));

        // The wallet has been swept, as it would have been. Without this the recovery pass would resolve the
        // row, find the store unblocked and legitimately start a fresh sweep — correct behaviour, but it would
        // make "nothing was re-sent" ambiguous about which send it meant.
        h.Sdk.TokenBalances.Clear();
        h.Sdk.BalanceSats = 0;

        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        var recovered = await h.Records.GetAsync(StoreId, key, Ct);
        Assert.Equal(SweepRecordStatus.Sent, recovered!.Status);
        Assert.Equal(SparkConversionStatus.Pending, recovered.ConversionStatus);
        // The order id the crash-window recovery poll resolves from is the same one the send reported: the
        // crash happened before the initial resolution, so the recovery poll is the only thing that can
        // persist it.
        Assert.Equal(payment.Conversion!.ProviderOrderId, recovered.ProviderOrderId);

        // Found by scanning, not by a point lookup, and never re-sent.
        Assert.DoesNotContain(key, h.Sdk.GetPaymentCalls);
        Assert.Empty(h.Sdk.CrossChainSendCalls);
    }

    /// <summary>
    /// The quote-id scan reads oldest-first, so a busy store does not hide the row it is looking for.
    /// </summary>
    /// <remarks>
    /// The scan is anchored to the record's creation time and bounded, so its target is the <em>oldest</em> row
    /// in the window. Paging newest-first spends the whole budget walking away from it — invisible on a quiet
    /// store and total on a busy one.
    /// </remarks>
    [Fact]
    public async Task The_quote_id_scan_pages_towards_its_target_rather_than_away_from_it()
    {
        var h = Harness(balanceSats: 500_000, stableBalance: 35_600_000);

        await h.Records.AddAsync(
            new SweepRecord
            {
                IdempotencyKey = "row-oldest",
                StoreId = StoreId,
                DestinationMode = SweepDestinationMode.EvmAddress,
                DestinationKind = SweepDestinationKind.EvmAddress,
                IdempotencyKeyAccepted = false,
                ProviderQuoteId = "q_target",
                Status = SweepRecordStatus.Pending,
                CreatedAt = Origin,
                AttemptCount = 1
            },
            Ct);

        // The target, at the start of the window.
        h.Sdk.Seed(new SparkPayment(
            "sdk-target", SparkPaymentDirection.Send, SparkPaymentStatus.Completed, SparkPaymentMethod.Token,
            0, 0, Origin, null, null, null, null, null,
            new SparkConversionState(
                SparkCrossChainProvider.Orchestra, SparkConversionStatus.Completed, "q_target", "order", 35_000_000)));

        // Buried under more sends than the scan will read, all newer.
        for (var i = 0; i < 300; i++)
        {
            h.Sdk.Seed(new SparkPayment(
                $"noise-{i}", SparkPaymentDirection.Send, SparkPaymentStatus.Completed, SparkPaymentMethod.Spark,
                1, 0, Origin.AddSeconds(10 + i), null, null, null, null, null));
        }

        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        var resolved = await h.Records.GetAsync(StoreId, "row-oldest", Ct);
        Assert.Equal(SweepRecordStatus.Confirmed, resolved!.Status);
        Assert.All(h.Sdk.ListQueries, q => Assert.True(q.Ascending));
    }

    /// <summary>
    /// A token sweep whose payment cannot be found is written off, not retried.
    /// </summary>
    /// <remarks>
    /// The honest failure. The scan is the strongest evidence available and it is not proof, so the row is
    /// closed with a message that says so — and, critically, nothing sends again. A retry here would be a
    /// second payment out of a balance that may already be gone.
    /// </remarks>
    [Fact]
    public async Task A_token_sweep_with_no_matching_payment_is_never_retried()
    {
        var h = Harness(balanceSats: 500_000, stableBalance: 35_600_000);

        await h.Records.AddAsync(
            new SweepRecord
            {
                IdempotencyKey = "row-key-2",
                StoreId = StoreId,
                DestinationMode = SweepDestinationMode.EvmAddress,
                DestinationKind = SweepDestinationKind.EvmAddress,
                IdempotencyKeyAccepted = false,
                ProviderQuoteId = "q_that_matches_nothing",
                Status = SweepRecordStatus.Pending,
                CreatedAt = Origin,
                AttemptCount = 1
            },
            Ct);

        // Inside the grace period: still blocking, nothing sent.
        var first = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);
        Assert.Equal(SweepOutcomeKind.InFlight, first.Kind);
        Assert.Empty(h.Sdk.CrossChainSendCalls);

        // Past it: written off, with a message that does not claim the money is safe.
        h.Time.Advance(SparkSweepEngine.UnresolvedGrace + TimeSpan.FromMinutes(1));
        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        var closed = await h.Records.GetAsync(StoreId, "row-key-2", Ct);
        Assert.Equal(SweepRecordStatus.Failed, closed!.Status);
        Assert.Contains("not proof", closed.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not be retried", closed.Error!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A token write-off is gated on the token balance exactly as a sats write-off is gated on sats.
    /// </summary>
    /// <remarks>
    /// The hazard is sharper here than on the sats rail: the row itself is never retried, but writing it off
    /// unblocks this very pass to plan a fresh sweep from the token balance — and a token send cannot carry an
    /// idempotency key, so nothing at the provider can dedupe a second one. When the held balance is below what
    /// the row says was sent, the send may well have happened and just not be visible yet; the row must block,
    /// bounded by the same escalation window as the sats gate.
    /// </remarks>
    [Fact]
    public async Task A_token_write_off_is_blocked_while_the_token_balance_suggests_the_send_happened()
    {
        // The row says 35.6 USDB went out; the wallet holds 30. The shortfall keeps it blocking...
        var h = Harness(balanceSats: 500_000, stableBalance: 30_000_000);

        await h.Records.AddAsync(
            new SweepRecord
            {
                IdempotencyKey = "row-key-3",
                StoreId = StoreId,
                DestinationMode = SweepDestinationMode.EvmAddress,
                DestinationKind = SweepDestinationKind.EvmAddress,
                IdempotencyKeyAccepted = false,
                ProviderQuoteId = "q_that_matches_nothing",
                SourceTokenIdentifier = FakeSparkSdkClient.Usdb.Value,
                SourceAmountBaseUnits = "35600000",
                Status = SweepRecordStatus.Pending,
                CreatedAt = Origin,
                AttemptCount = 1
            },
            Ct);

        h.Time.Advance(SparkSweepEngine.UnresolvedGrace + TimeSpan.FromMinutes(1));
        var blocked = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.InFlight, blocked.Kind);
        Assert.Empty(h.Sdk.CrossChainSendCalls);
        Assert.Equal(SweepRecordStatus.Pending, (await h.Records.GetAsync(StoreId, "row-key-3", Ct))!.Status);

        // ...and past the escalation window it closes with a reason naming what was observed.
        h.Time.Advance(SparkSweepEngine.ShortfallWriteOffAge);
        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        var closed = await h.Records.GetAsync(StoreId, "row-key-3", Ct);
        Assert.Equal(SweepRecordStatus.Failed, closed!.Status);
        Assert.Contains("held less than the sweep would have sent", closed.Error);
    }

    /// <summary>
    /// A row with no recorded quote id is closed rather than guessed at.
    /// </summary>
    /// <remarks>
    /// Unreachable through the engine, which writes the quote id before the send — but a row can arrive from a
    /// restored backup or a partially-written insert, and the safe direction is always to refuse to send.
    /// </remarks>
    [Fact]
    public async Task A_token_row_with_no_quote_id_blocks_rather_than_sending_again()
    {
        var h = Harness(balanceSats: 500_000, stableBalance: 35_600_000);

        await h.Records.AddAsync(
            new SweepRecord
            {
                IdempotencyKey = "row-key-3",
                StoreId = StoreId,
                DestinationMode = SweepDestinationMode.EvmAddress,
                DestinationKind = SweepDestinationKind.EvmAddress,
                IdempotencyKeyAccepted = false,
                ProviderQuoteId = null,
                Status = SweepRecordStatus.Pending,
                CreatedAt = Origin,
                AttemptCount = 1
            },
            Ct);

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.InFlight, result.Kind);
        Assert.Empty(h.Sdk.CrossChainSendCalls);
    }

    /// <summary>
    /// A sats-funded cross-chain row is still resolved by its idempotency key.
    /// </summary>
    /// <remarks>
    /// The other half of the branch. Widening recovery to scan for a quote id everywhere would work, but it
    /// would throw away a point lookup that is definitive in favour of a scan that is not — so the branch has to
    /// be exercised in both directions.
    /// </remarks>
    [Fact]
    public async Task A_satoshi_funded_cross_chain_row_is_resolved_by_its_key()
    {
        var h = Harness(balanceSats: 500_000);

        const string key = "8f4d1f0e-2b3e-4b17-9c8a-7f0c2a0f9e40";
        await h.Records.AddAsync(
            new SweepRecord
            {
                IdempotencyKey = key,
                StoreId = StoreId,
                DestinationMode = SweepDestinationMode.EvmAddress,
                DestinationKind = SweepDestinationKind.EvmAddress,
                IdempotencyKeyAccepted = true,
                ProviderQuoteId = "q_something",
                Status = SweepRecordStatus.Pending,
                CreatedAt = Origin,
                AttemptCount = 1
            },
            Ct);

        h.Sdk.Seed(new SparkPayment(
            key, SparkPaymentDirection.Send, SparkPaymentStatus.Pending, SparkPaymentMethod.Spark,
            500_000, 0, Origin.AddSeconds(5), null, null, null, null, null,
            new SparkConversionState(
                SparkCrossChainProvider.Orchestra, SparkConversionStatus.Pending, "q_something", "order-2")));

        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Contains(key, h.Sdk.GetPaymentCalls);
        var resolved = await h.Records.GetAsync(StoreId, key, Ct);
        // Sent, not Confirmed: the provider has not delivered.
        Assert.Equal(SweepRecordStatus.Sent, resolved!.Status);
        Assert.Equal(SparkConversionStatus.Pending, resolved.ConversionStatus);
    }

    /// <summary>
    /// A token-funded send whose committed quote carries no id is refused, not issued.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Raised by an external audit (H-2). The engine writes the committed provider quote id inside the approval
    /// callback because it is the only recovery handle a token-funded send has — the SDK rejects an idempotency
    /// key on any send with a token leg. That write only happened when there <em>was</em> an id; a committed
    /// quote without one skipped the block entirely and the send went out regardless. The result would be an
    /// irreversible cross-chain payment with nothing to match it against, which the recovery pass then closes as
    /// "cannot be established" without re-sending: money delivered, unmatchable, written off.
    /// </para>
    /// <para>
    /// Both shapes of the hazard. <c>0</c> is a provider that never names a quote. <c>1</c> is the worse one and
    /// the one the audit describes: the pre-flight quote had an id, so the row carries a handle that looks
    /// usable and belongs to a quote that was never committed to.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task A_token_send_whose_committed_quote_has_no_id_is_refused_rather_than_issued(int keptPrepares)
    {
        var h = Harness(balanceSats: 500_000, stableBalance: 35_600_000);
        h.Sdk.NullProviderQuoteIdAfterPrepares = keptPrepares;

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Equal(SweepRefusalCode.QuoteFailed, result.Record!.RefusalCode);
        Assert.Equal(SweepRecordStatus.Refused, result.Record.Status);

        // The refusal is the whole point: nothing was sent, and the wallet still holds its stablecoin.
        Assert.Empty(h.Sdk.Payments);
        Assert.Equal(new BigInteger(35_600_000), Assert.Single(h.Sdk.TokenBalances).BaseUnits);

        // And it says so in terms a merchant can act on, rather than naming a field.
        Assert.Contains("not sent", result.Reason, StringComparison.OrdinalIgnoreCase);

        // The send was reached — the refusal happens in the approval callback, which is the only moment both
        // facts hold — so a test asserting "the SDK was never called" would be asserting the wrong thing.
        Assert.Single(h.Sdk.CrossChainSendCalls);
    }

    /// <summary>
    /// The same missing id on the satoshi rail does not stop anything, because the key is the handle there.
    /// </summary>
    /// <remarks>
    /// <b>The half of the H-2 fix that is easy to get wrong.</b> A sats-funded cross-chain send carries the
    /// record's idempotency key, the SDK deduplicates on it, and recovery looks the payment up by it directly —
    /// so a quote that names itself is a reporting convenience there and nothing more. A guard that refused on a
    /// missing provider quote id everywhere would turn perfectly recoverable sends into refusals, and a store
    /// whose provider stopped populating the field would simply stop sweeping. The rail distinction is the
    /// guard, so it is asserted from both sides.
    /// </remarks>
    [Fact]
    public async Task A_satoshi_funded_send_with_no_provider_quote_id_is_still_sent()
    {
        var h = Harness(balanceSats: 500_000);
        h.Sdk.NullProviderQuoteIdAfterPrepares = 0;

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Swept, result.Kind);
        Assert.Equal(SweepRecordStatus.Sent, result.Record!.Status);

        // Nothing to record, and nothing lost by it: the payment is the key.
        Assert.Null(result.Record.ProviderQuoteId);
        Assert.True(result.Record.IdempotencyKeyAccepted);
        Assert.Equal(result.Record.IdempotencyKey, Assert.Single(h.Sdk.Payments).SdkPaymentId);
        Assert.Equal(result.Record.IdempotencyKey, Assert.Single(h.Sdk.CrossChainSendCalls).IdempotencyKey);
    }

    /// <summary>
    /// A conversion Spark is holding in RefundNeeded prompts a refund on the pass that observes it.
    /// </summary>
    /// <remarks>
    /// <b>Driven from the sweep walk rather than from a loop of its own</b>, which is the point: this plugin
    /// already requires reconciliation of its own because the SDK's events are unreliable, and for conversions
    /// it is worse — there is no event at all. Extending the pass that already runs before every
    /// sweep keeps one mechanism rather than two, and a stuck conversion is also a conversion blocking this
    /// store's sweeps.
    /// </remarks>
    [Fact]
    public async Task A_conversion_needing_a_refund_is_refunded_by_the_pass_that_sees_it()
    {
        var h = Harness(balanceSats: 500_000);

        const string key = "8f4d1f0e-2b3e-4b17-9c8a-7f0c2a0f9e41";
        await h.Records.AddAsync(
            new SweepRecord
            {
                IdempotencyKey = key,
                StoreId = StoreId,
                DestinationMode = SweepDestinationMode.EvmAddress,
                DestinationKind = SweepDestinationKind.EvmAddress,
                IdempotencyKeyAccepted = true,
                Status = SweepRecordStatus.Pending,
                CreatedAt = Origin,
                AttemptCount = 1
            },
            Ct);

        h.Sdk.Seed(new SparkPayment(
            key, SparkPaymentDirection.Send, SparkPaymentStatus.Pending, SparkPaymentMethod.Spark,
            500_000, 0, Origin.AddSeconds(5), null, null, null, null, null,
            new SparkConversionState(
                SparkCrossChainProvider.Orchestra, SparkConversionStatus.RefundNeeded, "q_stuck", "order-3")));

        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(1, h.Sdk.RefundPendingConversionsCalls);

        var row = await h.Records.GetAsync(StoreId, key, Ct);
        Assert.Equal(SparkConversionStatus.RefundNeeded, row!.ConversionStatus);
        // Sent, not Failed: the money has left and a refund is in flight, so nothing should invite a retry.
        Assert.Equal(SweepRecordStatus.Sent, row.Status);
        Assert.Contains("refund", row.Error!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A healthy pass does not call the refund endpoint.
    /// </summary>
    /// <remarks>
    /// The counterpart that stops the test above passing against an engine that refunds unconditionally — which
    /// would be a network call every two minutes per store, for nothing.
    /// </remarks>
    [Fact]
    public async Task A_pass_with_nothing_stuck_does_not_ask_for_a_refund()
    {
        var h = Harness(balanceSats: 500_000);

        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(0, h.Sdk.RefundPendingConversionsCalls);
    }

    /// <summary>
    /// An unknown-outcome cross-chain send leaves the row pending and blocks the store.
    /// </summary>
    /// <remarks>
    /// The case the whole quote-id scheme exists for. A network failure after the send has left says nothing
    /// about whether it arrived, so the row stays Pending and no new sweep starts — for a token sweep in
    /// particular, sending again would be a second payment.
    /// </remarks>
    [Fact]
    public async Task An_unknown_outcome_leaves_the_row_pending_and_stops_further_sweeps()
    {
        var h = Harness(balanceSats: 500_000, stableBalance: 35_600_000);
        h.Sdk.FailCrossChainSendWith = new SdkException.NetworkException("@v1=connection reset");

        var first = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.Unresolved, first.Kind);
        Assert.Equal(SweepRecordStatus.Pending, h.Records.Records[first.Record!.IdempotencyKey].Status);
        Assert.Contains("cannot be retried safely", first.Reason, StringComparison.OrdinalIgnoreCase);

        // The next pass sends nothing: it finds the unresolved row first.
        h.Sdk.FailCrossChainSendWith = null;
        h.Sdk.CrossChainSendCalls.Clear();

        var second = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, cancellationToken: Ct);

        Assert.Equal(SweepOutcomeKind.InFlight, second.Kind);
        Assert.Empty(h.Sdk.CrossChainSendCalls);
    }

    #endregion

    #region Preview

    [Fact]
    public async Task A_preview_quotes_the_cross_chain_sweep_without_sending_or_recording()
    {
        var h = Harness(balanceSats: 500_000);

        var preview = await h.Engine.PreviewAsync(StoreId, Ct);

        Assert.True(preview.CanSweep);
        Assert.Null(preview.Quote);
        Assert.NotNull(preview.CrossChainQuote);
        Assert.Equal("arbitrum", preview.CrossChainQuote!.Route.Chain);
        Assert.Equal(SparkCrossChainProvider.Orchestra, preview.CrossChainQuote.Route.Provider);
        Assert.Equal(495_000, Assert.IsType<SparkSendAmount.Bitcoin>(preview.Amount).Sats);

        Assert.Empty(h.Records.Records);
        Assert.Empty(h.Sdk.CrossChainSendCalls);
        Assert.Equal(500_000, h.Sdk.BalanceSats);
    }

    /// <summary>
    /// A preview reports the refusal a real sweep would hit, without recording one.
    /// </summary>
    /// <remarks>
    /// Shares the decision methods with the run, so the confirmation page cannot promise something the engine
    /// would then decline — and describes it in the same words, so a merchant does not see two accounts of one
    /// problem.
    /// </remarks>
    [Fact]
    public async Task A_preview_reports_a_routing_refusal_without_writing_a_row()
    {
        var h = Harness(balanceSats: 500_000);
        h.Sdk.CrossChainConfigured = false;

        var preview = await h.Engine.PreviewAsync(StoreId, Ct);

        Assert.False(preview.CanSweep);
        Assert.NotNull(preview.RefusalReason);
        Assert.Contains("not configured", preview.RefusalReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(h.Records.Records);
    }

    #endregion

    private const string NoKeyOnTokenLegMessage =
        "A cross-chain send funded from a token balance cannot carry an idempotency key";

    /// <summary>Widens the fake's spread once the pre-flight quote has been taken.</summary>
    /// <remarks>
    /// A callback on the fake's quote list would be neater, but the list is a plain list; polling it from the
    /// engine's own call sequence is enough, because the pre-flight quote and the send's quote are two separate
    /// calls and the fake records both.
    /// </remarks>
    private sealed class WideningFee
    {
        public WideningFee(FakeSparkSdkClient sdk, long whenQuoted, long thenBps)
        {
            sdk.CrossChainFeeBps = whenQuoted;
            Sdk = sdk;
            Then = thenBps;
            sdk.WhenQuoted = () =>
            {
                if (Widened)
                    return;
                Widened = true;
                Sdk.CrossChainFeeBps = Then;
            };
        }

        private FakeSparkSdkClient Sdk { get; }
        private long Then { get; }
        public bool Widened { get; private set; }
    }

    private static string Trace(TestHarness h) => string.Join(" -> ", h.Log.Entries);

    private sealed record TestHarness(
        SparkSweepEngine Engine,
        FakeSparkSdkClient Sdk,
        InMemorySweepRecordStore Records,
        FakeSparkStoreSettingsStore Settings,
        StubTimeProvider Time,
        WriteLog Log,
        FakeCrossChainValueOracle Oracle,
        FakeSparkStoreRuntime Runtime);

    /// <param name="stableBalance">
    /// USDB base units the wallet holds. Non-zero also switches Stable Balance on, because the two together are
    /// what put a sweep on the token rail — the setting alone does not, since the conversion runs in the
    /// background and may not have happened.
    /// </param>
    private static TestHarness Harness(
        long balanceSats,
        BigInteger? stableBalance = null,
        Action<SweepSettings>? configure = null)
    {
        var log = new WriteLog();
        var sdk = new FakeSparkSdkClient(log) { BalanceSats = balanceSats };
        var records = new InMemorySweepRecordStore(log);
        var runtime = new FakeSparkStoreRuntime();
        var settings = new FakeSparkStoreSettingsStore(runtime: runtime);
        var time = new StubTimeProvider(Origin);
        var oracle = new FakeCrossChainValueOracle();

        var sweep = new SweepSettings
        {
            Enabled = true,
            BalanceThresholdSats = 100_000,
            MinimumSweepSats = SweepSettings.DefaultCrossChainMinimumSweepSats,
            DestinationMode = SweepDestinationMode.EvmAddress,
            EvmAddress = Evm,
            EvmChain = "arbitrum",
            EvmAsset = "USDT",
            CrossChainSlippageBps = SweepSettings.DefaultCrossChainSlippageBps
        };
        configure?.Invoke(sweep);

        var stable = new StableBalanceSettings();
        if (stableBalance is { } held)
        {
            stable.Enabled = true;
            stable.DisclosureAcknowledged = true;
            sdk.StableBalanceActiveLabel = stable.EffectiveLabel;
            sdk.TokenBalances.Add(new SparkTokenBalance(
                FakeSparkSdkClient.Usdb, held, "USDB", "Bitcoin USD", 6, IsFreezable: true));
        }

        settings.Settings[StoreId] = new SparkSettings
        {
            ProtectedMnemonic = "protected",
            PaymentKey = "key",
            Sweep = sweep,
            StableBalance = stable
        };

        runtime.Clients[StoreId] = sdk;

        var resolver = new SweepDestinationResolver(
            new FakeSweepAddressSource(), NBitcoin.Network.Main, NullLogger<SweepDestinationResolver>.Instance);

        var engine = new SparkSweepEngine(
            settings, runtime, records, resolver,
            new CrossChainRouteResolver(NullLogger<CrossChainRouteResolver>.Instance),
            oracle,
            new FakeSweepTransactionLabeler(),
            time, NullLogger<SparkSweepEngine>.Instance);

        return new TestHarness(engine, sdk, records, settings, time, log, oracle, runtime);
    }
}
