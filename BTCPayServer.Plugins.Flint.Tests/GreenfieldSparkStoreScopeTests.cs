using System.Reflection;
using Microsoft.AspNetCore.Mvc.Routing;
using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Flint.Controllers;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Models;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// Every Greenfield endpoint must act on the store BTCPay authorised, not on a store id the request supplied.
/// </summary>
/// <remarks>
/// <para>
/// The same regression suite the MVC controller has, against the same mechanism, because Greenfield does not fix
/// it. Core's <c>BuiltInPermissionScopeProvider</c> resolves the store scope from route data, then the query
/// string, then the form — and <b>never from a JSON body</b>. The ASP.NET Core model binder, however, will happily
/// populate a property from that body. So an endpoint that acted on a bound <c>storeId</c> would be authorised
/// against the caller's own store and then act on somebody else's, exactly as the MVC hole did, with the
/// consequence that another store's Lightning wallet is re-pointed or its balance swept to the caller's address.
/// </para>
/// <para>
/// A unit test cannot drive the binder, so it reproduces the <em>outcome</em> of that binding directly: the
/// authorised store in <c>HttpContext</c> is the caller's, and the value handed to the action is the victim's.
/// Authorisation is faked as <em>succeeding</em> throughout, because a fake that refused would make every one of
/// these pass for the wrong reason — which is why each refusal has a positive counterpart below.
/// </para>
/// </remarks>
public class GreenfieldSparkStoreScopeTests
{
    private const string AttackerAddress = "bcrt1qt8hufshrz62z5vj4q40uqx6c6ytlujy5s03gwm";

    [Fact]
    public async Task Status_refuses_a_store_id_that_is_not_the_authorised_store()
    {
        var h = SparkSurfaceHarness.Create();

        // A read, but a read of another store's balance and Spark identity.
        var result = await h.Api.GetStatus(SparkSurfaceHarness.VictimStore, CancellationToken.None);

        AssertStoreNotFound(result);
    }

    [Fact]
    public async Task Provisioning_refuses_a_store_id_that_is_not_the_authorised_store()
    {
        var h = SparkSurfaceHarness.Create();

        var result = await h.Api.Provision(
            SparkSurfaceHarness.VictimStore,
            new SparkProvisionRequest { SeedSource = "import", Mnemonic = SparkSurfaceHarness.ValidMnemonic },
            CancellationToken.None);

        AssertStoreNotFound(result);

        // The whole point: the victim keeps its own seed, its own payment key and its own Lightning node.
        var victim = h.Settings.Settings[SparkSurfaceHarness.VictimStore]!;
        Assert.Equal("victim-protected", victim.ProtectedMnemonic);
        Assert.Equal(SparkSurfaceHarness.VictimPaymentKey, victim.PaymentKey);
        Assert.Equal(
            SparkSurfaceHarness.VictimNode,
            h.Lightning.Stores[SparkSurfaceHarness.VictimStore].ConnectionString);
        Assert.Empty(h.Settings.Writes);
        Assert.Empty(h.Lightning.Writes);

        // And nothing looked at the victim's on-chain wallet on the way through.
        Assert.Empty(h.SeedReader.Reads);
    }

    [Fact]
    public async Task Removal_refuses_a_store_id_that_is_not_the_authorised_store()
    {
        var h = SparkSurfaceHarness.Create();

        var result = await h.Api.Remove(SparkSurfaceHarness.VictimStore, CancellationToken.None);

        AssertStoreNotFound(result);
        Assert.NotNull(h.Settings.Settings[SparkSurfaceHarness.VictimStore]);
        Assert.Empty(h.Settings.Writes);
        Assert.Equal(
            SparkSurfaceHarness.VictimNode,
            h.Lightning.Stores[SparkSurfaceHarness.VictimStore].ConnectionString);
    }

    [Fact]
    public async Task Reading_sweep_configuration_refuses_a_store_id_that_is_not_the_authorised_store()
    {
        var h = SparkSurfaceHarness.Create();

        var result = await h.Api.GetSweepConfiguration(
            SparkSurfaceHarness.VictimStore, 0, 25, CancellationToken.None);

        AssertStoreNotFound(result);
        // Not even a peek at the victim's wallet, which would leak whether it has one.
        Assert.Empty(h.SweepAddresses.Calls);
    }

    [Fact]
    public async Task Writing_sweep_configuration_refuses_a_store_id_that_is_not_the_authorised_store()
    {
        var h = SparkSurfaceHarness.Create();

        var result = await h.Api.UpdateSweepConfiguration(
            SparkSurfaceHarness.VictimStore,
            new SweepSettingsInput
            {
                Enabled = true,
                DestinationMode = SweepDestinationMode.StaticAddress,
                // The caller's own address. Accepting this would point the victim's auto-sweep at it.
                StaticAddress = AttackerAddress
            },
            CancellationToken.None);

        AssertStoreNotFound(result);
        Assert.Empty(h.Settings.Writes);

        var victim = h.Settings.Settings[SparkSurfaceHarness.VictimStore]!;
        Assert.False(victim.Sweep.Enabled);
        Assert.Equal(SweepDestinationMode.StoreWallet, victim.Sweep.DestinationMode);
        Assert.Null(victim.Sweep.StaticAddress);
    }

    [Fact]
    public async Task Previewing_a_sweep_refuses_a_store_id_that_is_not_the_authorised_store()
    {
        var h = SparkSurfaceHarness.Create();

        var result = await h.Api.Sweep(
            SparkSurfaceHarness.VictimStore, new SparkSweepRequest { Preview = true }, CancellationToken.None);

        AssertStoreNotFound(result);
        // No quote against the victim's wallet, which would leak its balance into the response.
        Assert.Empty(h.SweepAddresses.Calls);
        Assert.Empty(h.VictimWallet.OnchainQuoteCalls);
    }

    [Fact]
    public async Task Sweeping_refuses_a_store_id_that_is_not_the_authorised_store()
    {
        // The sharpest one in this file: this endpoint moves money.
        var h = SparkSurfaceHarness.Create();

        var result = await h.Api.Sweep(SparkSurfaceHarness.VictimStore, null, CancellationToken.None);

        AssertStoreNotFound(result);
        Assert.Empty(h.SweepRecords.Records);
        Assert.Equal(5_000_000, h.VictimWallet.BalanceSats);
        Assert.Empty(h.VictimWallet.OnchainSendCalls);
    }

    [Fact]
    public async Task Reading_deposits_refuses_a_store_id_that_is_not_the_authorised_store()
    {
        var h = SparkSurfaceHarness.Create();

        var result = await h.Api.GetDeposits(SparkSurfaceHarness.VictimStore, CancellationToken.None);

        AssertStoreNotFound(result);
        // Not even the victim's deposit address, which is a piece of the victim's wallet the caller could then
        // watch on-chain.
        Assert.Empty(h.VictimWallet.ClaimCalls);
    }

    [Fact]
    public async Task Claiming_a_deposit_refuses_a_store_id_that_is_not_the_authorised_store()
    {
        // A money-moving endpoint: a claim spends the victim's deposit on a fee.
        var h = SparkSurfaceHarness.Create();
        h.VictimWallet.UnclaimedDeposits.Add(new SparkDepositInfo(
            "8808985e78ad465c25727d5ad749f60a5787855d4f1ddffebfc4afb4dbde1b37", 0, 60_000, IsMature: true,
            new SparkDepositClaimFailure(SparkDepositClaimFailureKind.MaxFeeExceeded, "too dear", 420)));

        var result = await h.Api.ClaimDeposit(
            SparkSurfaceHarness.VictimStore,
            new SparkClaimDepositRequest
            {
                TxId = "8808985e78ad465c25727d5ad749f60a5787855d4f1ddffebfc4afb4dbde1b37",
                Vout = 0
            },
            CancellationToken.None);

        AssertStoreNotFound(result);
        Assert.Empty(h.VictimWallet.ClaimCalls);
        Assert.Single(h.VictimWallet.UnclaimedDeposits);
    }

    [Fact]
    public async Task Reading_stable_balance_refuses_a_store_id_that_is_not_the_authorised_store()
    {
        var h = SparkSurfaceHarness.Create();

        var result = await h.Api.GetStableBalance(SparkSurfaceHarness.VictimStore, CancellationToken.None);

        AssertStoreNotFound(result);
    }

    [Fact]
    public async Task Writing_stable_balance_refuses_a_store_id_that_is_not_the_authorised_store()
    {
        // The sharpest of the four: enabling this converts the victim's entire balance into a stablecoin.
        var h = SparkSurfaceHarness.Create(mainnet: true);

        var result = await h.Api.UpdateStableBalance(
            SparkSurfaceHarness.VictimStore,
            new StableBalanceInput { Enabled = true, DisclosureAcknowledged = true },
            CancellationToken.None);

        AssertStoreNotFound(result);
        Assert.Empty(h.Settings.Writes);
        Assert.False(h.Settings.Settings[SparkSurfaceHarness.VictimStore]!.StableBalance.Enabled);
        Assert.Empty(h.VictimWallet.StableBalanceCalls);
        Assert.Null(h.VictimWallet.StableBalanceActiveLabel);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("provision")]
    [InlineData("remove")]
    [InlineData("sweep-get")]
    [InlineData("sweep-put")]
    [InlineData("sweep-post")]
    [InlineData("deposit-get")]
    [InlineData("deposit-claim")]
    [InlineData("stable-get")]
    [InlineData("stable-put")]
    [InlineData("sync")]
    [InlineData("sweep-record")]
    public async Task Every_endpoint_refuses_when_no_store_was_authorised_at_all(string endpoint)
    {
        // The other half of the guard: HttpContext carries no authorised store, which is what a future filter
        // change or a route that stopped resolving one would produce. Nothing may fall back to the route value.
        var h = SparkSurfaceHarness.Create(authoriseStore: null);

        var result = endpoint switch
        {
            "status" => await h.Api.GetStatus(SparkSurfaceHarness.AttackerStore, CancellationToken.None),
            "provision" => await h.Api.Provision(
                SparkSurfaceHarness.AttackerStore,
                new SparkProvisionRequest { SeedSource = "generate" },
                CancellationToken.None),
            "remove" => await h.Api.Remove(SparkSurfaceHarness.AttackerStore, CancellationToken.None),
            "sweep-get" => await h.Api.GetSweepConfiguration(
                SparkSurfaceHarness.AttackerStore, 0, 25, CancellationToken.None),
            "sweep-put" => await h.Api.UpdateSweepConfiguration(
                SparkSurfaceHarness.AttackerStore, new SweepSettingsInput(), CancellationToken.None),
            "deposit-get" => await h.Api.GetDeposits(
                SparkSurfaceHarness.AttackerStore, CancellationToken.None),
            "deposit-claim" => await h.Api.ClaimDeposit(
                SparkSurfaceHarness.AttackerStore,
                new SparkClaimDepositRequest { TxId = "abc", Vout = 0 },
                CancellationToken.None),
            "stable-get" => await h.Api.GetStableBalance(
                SparkSurfaceHarness.AttackerStore, CancellationToken.None),
            "stable-put" => await h.Api.UpdateStableBalance(
                SparkSurfaceHarness.AttackerStore, new StableBalanceInput(), CancellationToken.None),
            "sync" => await h.Api.SyncBalance(SparkSurfaceHarness.AttackerStore, CancellationToken.None),
            "sweep-record" => await h.Api.GetSweepRecord(SparkSurfaceHarness.AttackerStore, "key", CancellationToken.None),
            _ => await h.Api.Sweep(SparkSurfaceHarness.AttackerStore, null, CancellationToken.None)
        };

        AssertStoreNotFound(result);
        Assert.Empty(h.Settings.Writes);
        Assert.Empty(h.SweepRecords.Records);
    }

    /// <summary>
    /// Every action on the controller is covered by the "no authorised store" theory above.
    /// </summary>
    /// <remarks>
    /// The theory is a hand-written list, and a hand-written list is a list somebody forgets to extend — which
    /// is exactly how a new endpoint ships without the guard that the last cross-store hole was fixed by.
    /// Counting the controller's actions against it means adding one without adding a case fails here.
    /// </remarks>
    [Fact]
    public void The_no_authorised_store_theory_covers_every_action_on_the_controller()
    {
        // Server-level endpoints (no {storeId} in route) do not use ResolveStore and are not
        // covered by this theory; they are gated by CanModifyServerSettings instead.
        var actions = typeof(GreenfieldSparkController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => typeof(Task<IActionResult>).IsAssignableFrom(m.ReturnType))
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>()
                .Any(a => a.Template?.Contains("{storeId}", StringComparison.Ordinal) == true))
            .Select(m => m.Name)
            .ToList();

        var cases = typeof(GreenfieldSparkStoreScopeTests)
            .GetMethod(nameof(Every_endpoint_refuses_when_no_store_was_authorised_at_all))!
            .GetCustomAttributes<InlineDataAttribute>()
            .Count();

        Assert.Equal(actions.Count, cases);
    }

    /// <summary>
    /// An empty Stable Balance body is refused rather than read as "switch it off".
    /// </summary>
    /// <remarks>
    /// <b>Every other PUT in this API treats an omitted body as "replace with defaults", and that is wrong
    /// here.</b> The default is <c>enabled: false</c>, and applying it converts the store's whole stablecoin
    /// balance back to Bitcoin at the DEX spread — a money movement, queued by a caller who sent no body and
    /// passed no disclosure gate. An accidental PUT is not consent to convert.
    /// </remarks>
    [Fact]
    public async Task An_empty_stable_balance_body_does_not_quietly_convert_the_balance_back()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, mainnet: true);
        h.Settings.Settings[SparkSurfaceHarness.AttackerStore]!.StableBalance = new StableBalanceSettings
        {
            Enabled = true,
            DisclosureAcknowledged = true
        };
        var wallet = h.WalletOf(SparkSurfaceHarness.AttackerStore);
        wallet.StableBalanceActiveLabel = StableBalanceSettings.DefaultLabel;

        var result = await h.Api.UpdateStableBalance(
            SparkSurfaceHarness.AttackerStore, null, CancellationToken.None);

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, unprocessable.StatusCode);

        // Nothing was stored and nothing was deactivated.
        Assert.True(h.Settings.Settings[SparkSurfaceHarness.AttackerStore]!.StableBalance.Enabled);
        Assert.Empty(wallet.StableBalanceCalls);
        Assert.Equal(StableBalanceSettings.DefaultLabel, wallet.StableBalanceActiveLabel);
    }

    /// <summary>
    /// A write the wallet refused answers with the store's real state, not a bare error.
    /// </summary>
    /// <remarks>
    /// <b>This is the response a mainnet failure was read from, and it was unreadable.</b> The setting is
    /// stored and the wallet is not changed, so <c>desiredActive</c>, <c>activeLabel</c> and
    /// <c>needsReapply</c> are exactly the three facts a caller needs — and a plain API error returned none of
    /// them, leaving an operator unable to tell from the API whether their balance had moved.
    /// </remarks>
    [Fact]
    public async Task A_stable_balance_write_the_wallet_refuses_still_reports_the_stores_real_state()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, mainnet: true);
        var wallet = h.WalletOf(SparkSurfaceHarness.AttackerStore);

        // Carried across the reconnect the write triggers, so the failure is met on the live handle.
        wallet.FailStableBalanceWith = new Breez.Sdk.Spark.SdkException.Generic(
            "@v1=Stable balance is not configured");
        wallet.TokenBalances.Add(new SparkTokenBalance(
            FakeSparkSdkClient.Usdb, 235_824, "USDB", "Bitcoin USD", 6, IsFreezable: true));

        var result = await h.Api.UpdateStableBalance(
            SparkSurfaceHarness.AttackerStore,
            new StableBalanceInput { Enabled = false },
            CancellationToken.None);

        var unprocessable = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, unprocessable.StatusCode);

        // The state, not an error code.
        var state = Assert.IsType<SparkStableBalanceData>(unprocessable.Value);
        Assert.False(state.DesiredActive);
        Assert.NotNull(state.Message);

        // And it does not read as success: the wallet is holding a stablecoin it reports nothing active for.
        Assert.True(state.HoldingUnmanagedBalance);
        Assert.True(state.NeedsReapply);
        Assert.NotNull(state.Balance);
    }

    /// <summary>
    /// An explicit off is honoured, so the refusal above is about the omission and not about switching off.
    /// </summary>
    [Fact]
    public async Task An_explicit_off_is_accepted()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, mainnet: true);

        var result = await h.Api.UpdateStableBalance(
            SparkSurfaceHarness.AttackerStore,
            new StableBalanceInput { Enabled = false },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Single(h.WalletOf(SparkSurfaceHarness.AttackerStore).StableBalanceCalls);
    }

    #region Positive counterparts

    [Fact]
    public async Task Status_answers_for_the_authorised_store()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true);

        var status = AssertOk<SparkStatusData>(
            await h.Api.GetStatus(SparkSurfaceHarness.AttackerStore, CancellationToken.None));

        Assert.True(status.Configured);
        Assert.True(status.WalletRunning);
        // The authorised store's own balance, not the victim's 5,000,000.
        Assert.Equal(500_000, status.BalanceSats);
    }

    [Fact]
    public async Task Provisioning_proceeds_for_the_authorised_store()
    {
        var h = SparkSurfaceHarness.Create();

        var response = AssertOk<SparkProvisionResponse>(await h.Api.Provision(
            SparkSurfaceHarness.AttackerStore,
            new SparkProvisionRequest { SeedSource = "import", Mnemonic = SparkSurfaceHarness.ValidMnemonic },
            CancellationToken.None));

        Assert.True(response.Status.Configured);
        Assert.NotNull(h.Settings.Settings[SparkSurfaceHarness.AttackerStore]);
        Assert.Equal(SparkSurfaceHarness.AttackerStore, Assert.Single(h.Lightning.Writes).StoreId);

        // And the victim was never touched on the way through.
        Assert.Equal(
            "victim-protected", h.Settings.Settings[SparkSurfaceHarness.VictimStore]!.ProtectedMnemonic);
    }

    [Fact]
    public async Task Removal_proceeds_for_the_authorised_store()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true);
        h.Lightning.Add(
            SparkSurfaceHarness.AttackerStore,
            SparkConnectionString.Format(
                SparkSurfaceHarness.AttackerStore, SparkSurfaceHarness.VictimPaymentKey));

        var result = await h.Api.Remove(SparkSurfaceHarness.AttackerStore, CancellationToken.None);

        Assert.IsType<OkResult>(result);
        Assert.Null(h.Settings.Settings[SparkSurfaceHarness.AttackerStore]);

        // Its own Lightning configuration was cleared, and the victim's was not.
        Assert.Null(h.Lightning.Stores[SparkSurfaceHarness.AttackerStore].ConnectionString);
        Assert.Equal(
            SparkSurfaceHarness.VictimNode,
            h.Lightning.Stores[SparkSurfaceHarness.VictimStore].ConnectionString);
    }

    [Fact]
    public async Task Sweeping_proceeds_for_the_authorised_store()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true);

        var result = AssertOk<SparkSweepResultData>(
            await h.Api.Sweep(SparkSurfaceHarness.AttackerStore, null, CancellationToken.None));

        Assert.Equal(SweepOutcomeKind.Swept, result.Outcome);
        var record = Assert.Single(h.SweepRecords.Records).Value;
        Assert.Equal(SparkSurfaceHarness.AttackerStore, record.StoreId);
        Assert.Equal(SweepTrigger.Manual, record.Trigger);

        // The victim's wallet was never read, quoted or spent.
        Assert.Equal(5_000_000, h.VictimWallet.BalanceSats);
        Assert.Empty(h.VictimWallet.OnchainSendCalls);
    }

    #endregion

    /// <summary>
    /// No request model may carry a store id, on any surface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A structural guard rather than a behavioural one, and the cheapest possible defence against the original
    /// defect coming back. The MVC hole existed because a bindable <c>storeId</c> was reachable from the request
    /// body while authorisation resolved the store from the route; the body cannot name a store BTCPay never
    /// authorised if no bindable member exists for it to name.
    /// </para>
    /// <para>
    /// <b>The type list is derived from the controller, not written down.</b> It used to be three names typed by
    /// hand, and by the time Wave 7 added two more body-bound models the guard had silently stopped covering
    /// them — a guard that protects whatever somebody remembered is a guard that protects less every wave.
    /// Reflecting over the <c>[FromBody]</c> parameters means a new endpoint is covered the moment it exists.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_API_request_model_has_a_bindable_store_id()
    {
        var bodyTypes = BodyBoundModels();

        // A scan that found nothing would pass vacuously forever.
        Assert.NotEmpty(bodyTypes);

        foreach (var type in bodyTypes)
        {
            foreach (var property in type.GetProperties())
            {
                Assert.False(
                    property.Name.Contains("store", StringComparison.OrdinalIgnoreCase),
                    $"{type.Name}.{property.Name} looks like it names a store. A Greenfield request body must not "
                    + "be able to: BTCPay resolves the authorised store from route data, never from the body, so a "
                    + "bindable store member is the cross-store hole that was already found once in the MVC "
                    + "controller.");
            }
        }
    }

    /// <summary>
    /// The derived list actually reaches the models this wave added.
    /// </summary>
    /// <remarks>
    /// The reflection above would pass just as happily if it found only one type, so this pins that it finds the
    /// ones a hand-written list had already missed.
    /// </remarks>
    [Theory]
    [InlineData(typeof(SparkProvisionRequest))]
    [InlineData(typeof(SparkSweepRequest))]
    [InlineData(typeof(SweepSettingsInput))]
    [InlineData(typeof(SparkClaimDepositRequest))]
    [InlineData(typeof(StableBalanceInput))]
    public void Every_body_bound_model_is_reached_by_the_store_id_guard(Type expected)
    {
        Assert.Contains(expected, BodyBoundModels());
    }

    /// <summary>
    /// Every type the controller binds from a request body.
    /// </summary>
    /// <remarks>
    /// <b>Shared by the guard and by the check that the guard reaches the right types</b>, deliberately. Two
    /// copies of this derivation would let the guard's copy be narrowed — accidentally or by a well-meaning
    /// filter — while the coverage check went on passing against its own untouched copy, which is precisely the
    /// failure the coverage check exists to prevent.
    /// </remarks>
    private static List<Type> BodyBoundModels() =>
        typeof(GreenfieldSparkController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(m => m.GetParameters())
            .Where(p => p.GetCustomAttribute<FromBodyAttribute>() is not null)
            .Select(p => Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType)
            .Distinct()
            .ToList();

    private static void AssertStoreNotFound(IActionResult result)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, objectResult.StatusCode);

        // "Not found" rather than "forbidden" on purpose: it says nothing about whether the other store exists.
        var error = Assert.IsType<GreenfieldAPIError>(objectResult.Value);
        Assert.Equal("store-not-found", error.Code);
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<T>(ok.Value);
    }
}
