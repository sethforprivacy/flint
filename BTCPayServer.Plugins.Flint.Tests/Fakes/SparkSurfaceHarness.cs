using System.Security.Claims;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Flint.Controllers;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// One store, one victim store, one set of fakes — and <b>both</b> of the plugin's request surfaces built over them.
/// </summary>
/// <remarks>
/// <para>
/// Shared deliberately. The cookie-authenticated pages and the Greenfield API are supposed to reach the same
/// decisions through the same services, and two harnesses would let them drift apart in the tests before they
/// drifted apart in production. Building both controllers from one graph means a test can assert that a refusal on
/// one surface is a refusal on the other, and that neither has its own copy of a guard.
/// </para>
/// <para>
/// The <c>HttpContext</c> is authorised for <see cref="AttackerStore"/> and a fully configured
/// <see cref="VictimStore"/> sits alongside it — Spark set up, its own payment key, its own node configuration, a
/// balance worth stealing — so a guard that fails open has something to be caught taking.
/// </para>
/// </remarks>
public sealed class SparkSurfaceHarness
{
    public const string AttackerStore = "attacker-store";
    public const string VictimStore = "victim-store";

    public const string ValidMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    public const string VictimNode = "type=lnd-rest;server=https://10.0.0.5:8080/;macaroon=deadbeef";
    public const string VictimPaymentKey = "1111222233334444555566667777888899990000aaaabbbbccccddddeeeeffff";

    private SparkSurfaceHarness(
        SparkController mvc,
        GreenfieldSparkController api,
        FakeSparkStoreSettingsStore settings,
        FakeStoreLightningConfigStore lightning,
        FakeHotWalletSeedReader seedReader,
        InMemorySweepRecordStore sweepRecords,
        FakeSweepAddressSource sweepAddresses,
        FakeSparkStoreRuntime runtime,
        WriteLog writeLog,
        CapturingLogger<SparkStoreProvisioner> provisionerLog,
        SparkMnemonicProtector protector,
        SparkSweepEngine sweepEngine,
        SparkDepositService depositService,
        SparkStableBalanceService stableBalanceService,
        CrossChainCatalog crossChainCatalog,
        StubHttpMessageHandler crossChainRequests)
    {
        CrossChainCatalog = crossChainCatalog;
        CrossChainRequests = crossChainRequests;
        Protector = protector;
        SweepEngine = sweepEngine;
        DepositService = depositService;
        StableBalanceService = stableBalanceService;
        Mvc = mvc;
        Api = api;
        Settings = settings;
        Lightning = lightning;
        SeedReader = seedReader;
        SweepRecords = sweepRecords;
        SweepAddresses = sweepAddresses;
        Runtime = runtime;
        WriteLog = writeLog;
        ProvisionerLog = provisionerLog;
    }

    /// <summary>The cookie-authenticated pages.</summary>
    public SparkController Mvc { get; }

    /// <summary>The Greenfield API, over the identical service graph.</summary>
    public GreenfieldSparkController Api { get; }

    public FakeSparkStoreSettingsStore Settings { get; }
    public FakeStoreLightningConfigStore Lightning { get; }
    public FakeHotWalletSeedReader SeedReader { get; }
    public InMemorySweepRecordStore SweepRecords { get; }
    public FakeSweepAddressSource SweepAddresses { get; }
    public FakeSparkStoreRuntime Runtime { get; }

    /// <summary>The single ordered log of writes across the fakes, for asserting ordering invariants.</summary>
    public WriteLog WriteLog { get; }

    /// <summary>Everything the provisioner logged, so "no seed material is logged" is falsifiable.</summary>
    public CapturingLogger<SparkStoreProvisioner> ProvisionerLog { get; }

    /// <summary>
    /// The same protector the provisioner used, so a test can read back what was actually stored.
    /// </summary>
    /// <remarks>
    /// This is the only way to assert that an imported phrase was <em>normalised</em> on the way in rather than
    /// merely accepted — and normalisation is what stops one seed looking like two wallets to the
    /// single-instance-per-wallet guard.
    /// </remarks>
    public SparkMnemonicProtector Protector { get; }

    /// <summary>The phrase a store's settings actually hold, or null when it has none.</summary>
    public string? StoredMnemonic(string storeId) =>
        Protector.TryUnprotect(Settings.Settings.GetValueOrDefault(storeId)?.ProtectedMnemonic);

    /// <summary>
    /// The one engine both surfaces sweep through, exposed so a test can also drive an <em>automatic</em> pass —
    /// which is how a recurring refusal, and therefore the history the API reports, comes about.
    /// </summary>
    public SparkSweepEngine SweepEngine { get; }

    /// <summary>The one deposit service both surfaces read and claim through.</summary>
    public SparkDepositService DepositService { get; }

    /// <summary>The one Stable Balance service both surfaces read and write through.</summary>
    public SparkStableBalanceService StableBalanceService { get; }

    /// <summary>
    /// The destination catalogue the sweep page's pickers are drawn from.
    /// </summary>
    /// <remarks>
    /// Wired to a handler that cannot reach anything (see <see cref="Create"/>), so every page rendered through
    /// this harness renders the built-in fallback. That is the state a server with no outbound network is
    /// permanently in, and the one the settings page has to keep working in.
    /// </remarks>
    public CrossChainCatalog CrossChainCatalog { get; }

    /// <summary>Every HTTP request the catalogue made, so "rendering costs no round trip" is falsifiable.</summary>
    public StubHttpMessageHandler CrossChainRequests { get; }

    public FakeSparkSdkClient VictimWallet => (FakeSparkSdkClient)Runtime.Clients[VictimStore];

    public FakeSparkSdkClient WalletOf(string storeId) => (FakeSparkSdkClient)Runtime.Clients[storeId];

    /// <param name="allowHotWalletForAll">
    /// The server policy both surfaces gate every seed source on. False models a server where non-admins may not
    /// create hot wallets.
    /// </param>
    /// <param name="hotWalletSeed">
    /// What the store's on-chain wallet reports. Defaults to "not a hot wallet", which is the common case.
    /// </param>
    /// <param name="authoriseStore">
    /// The store BTCPay's authorisation filter recorded — the only trustworthy statement of which store a request
    /// may touch. Null models a request no store was authorised for at all.
    /// </param>
    /// <param name="serverAdmin">
    /// Whether the caller holds BTCPay's server-admin role. False by default, because that is what a store
    /// manager is: authorised for the store, and not an operator of the host it runs on.
    /// </param>
    /// <param name="crossChainRoutes">
    /// What the provider's route table answers, or null for a server that cannot reach it. Null by default
    /// because that is the state the sweep page has to keep rendering and saving in, and because a test that did
    /// not ask for a live catalogue should not quietly get one.
    /// </param>
    /// <param name="unilateralExit">
    /// The unilateral-exit service the pages call, or null for one that refuses everything.
    /// </param>
    public static SparkSurfaceHarness Create(
        bool allowHotWalletForAll = true,
        HotWalletSeedResult? hotWalletSeed = null,
        string? authoriseStore = AttackerStore,
        bool configureAttackerStore = false,
        bool mainnet = false,
        bool serverAdmin = false,
        string? crossChainRoutes = null,
        ISparkUnilateralExitService? unilateralExit = null)
    {
        var writeLog = new WriteLog();

        var lightning = FakeStoreLightningConfigStore
            .WithStore(AttackerStore, writeLog: writeLog)
            .Add(VictimStore, VictimNode);

        var wiring = new SparkLightningWiring(lightning, NullLogger<SparkLightningWiring>.Instance);
        var settings = new FakeSparkStoreSettingsStore(wiring, writeLog);

        // The victim is a going concern: Spark configured, its own payment key, its own node config.
        settings.Settings[VictimStore] = new SparkSettings
        {
            ProtectedMnemonic = "victim-protected",
            PaymentKey = VictimPaymentKey,
            SeedSource = SeedSource.Generated
        };

        if (configureAttackerStore)
        {
            settings.Settings[AttackerStore] = new SparkSettings
            {
                ProtectedMnemonic = "attacker-protected",
                PaymentKey = VictimPaymentKey,
                SeedSource = SeedSource.Generated
            };
        }

        var seedReader = new FakeHotWalletSeedReader(hotWalletSeed);

        var sweepRecords = new InMemorySweepRecordStore(writeLog);
        var sweepAddresses = new FakeSweepAddressSource();
        var runtime = new FakeSparkStoreRuntime();

        // Both stores have running wallets with real balances, so an escaped guard would have something to move.
        // Both write into the shared log, so "the record was persisted before the send" is assertable as an
        // ordering rather than as two independent call counts.
        runtime.Clients[VictimStore] = new FakeSparkSdkClient(writeLog) { BalanceSats = 5_000_000 };
        runtime.Clients[AttackerStore] = new FakeSparkSdkClient(writeLog) { BalanceSats = 500_000 };

        // Mainnet is opt-in because the two post-MVP features are mainnet-only and the rest of the suite is
        // not. Note that a mainnet harness leaves FakeSweepAddressSource handing out regtest addresses, so a
        // store-wallet destination is correctly unusable there — which is fine, and honest: every test that
        // asks for mainnet is testing a cross-chain or Stable Balance path.
        var network = mainnet ? Network.Main : Network.RegTest;

        var resolver = new SweepDestinationResolver(
            sweepAddresses, network, NullLogger<SweepDestinationResolver>.Instance);
        var valueOracle = new FakeCrossChainValueOracle();
        var sweepEngine = new SparkSweepEngine(
            settings, runtime, sweepRecords, resolver,
            new CrossChainRouteResolver(NullLogger<CrossChainRouteResolver>.Instance),
            valueOracle,
            new FakeSweepTransactionLabeler(),
            TimeProvider.System, NullLogger<SparkSweepEngine>.Instance);

        var provisionerLog = new CapturingLogger<SparkStoreProvisioner>();
        var protector = new SparkMnemonicProtector(new EphemeralDataProtectionProvider());
        var provisioner = new SparkStoreProvisioner(settings, wiring, protector, provisionerLog);

        var seedResolver = new SparkSeedResolver(
            seedReader,
            new FakeAuthorizationService(),
            new FakeSettingsAccessor<PoliciesSettings>(
                new PoliciesSettings { AllowHotWalletForAll = allowHotWalletForAll }));

        var statusReader = new SparkStoreStatusReader(
            settings, runtime, new FakeSparkNetworkStatusProbe(), wiring,
            NullLogger<SparkStoreStatusReader>.Instance);

        var sweepSettings = new SparkSweepSettingsService(
            settings, runtime, resolver, sweepAddresses, sweepRecords,
            NullLogger<SparkSweepSettingsService>.Instance);

        var depositService = new SparkDepositService(
            settings, runtime, NullLogger<SparkDepositService>.Instance);

        var stableBalanceService = new SparkStableBalanceService(
            settings, runtime, mainnet, NullLogger<SparkStableBalanceService>.Instance);

        // Unreachable unless a test says otherwise. The sweep page must render and save on a server with no
        // outbound network at all, so that is what the harness models by default: every fetch fails, the
        // catalogue never leaves its fallback, and a test that finds a fetched chain in the picker without
        // asking for one is looking at a bug.
        var crossChainRequests = crossChainRoutes is null
            ? StubHttpMessageHandler.Offline()
            : StubHttpMessageHandler.Returning(crossChainRoutes);
        var crossChainCatalog = new CrossChainCatalog(
            new StubHttpClientFactory(crossChainRequests),
            TimeProvider.System,
            NullLogger<CrossChainCatalog>.Instance);

        // Refuses everything unless a test supplies its own. A page test that did not ask for an exit service
        // should not be able to quote one by accident, and an unstubbed call failing loudly beats it returning
        // a plausible-looking empty page.
        var exit = unilateralExit ?? new UnavailableUnilateralExitService();

        var mvc = new SparkController(
            settings, provisioner, wiring, seedResolver, statusReader, sweepEngine, sweepSettings,
            depositService, stableBalanceService, exit, crossChainCatalog,
            new FakeAuthorizationService(), NullLogger<SparkController>.Instance);

        var api = new GreenfieldSparkController(
            settings,
            provisioner, seedResolver, statusReader, sweepSettings, sweepEngine,
            depositService, stableBalanceService,
            NullLogger<GreenfieldSparkController>.Instance);

        var store = authoriseStore is null ? null : new StoreData { Id = authoriseStore };

        BindContext(mvc, store, withTempData: true, serverAdmin);
        BindContext(api, store, withTempData: false, serverAdmin);

        return new SparkSurfaceHarness(
            mvc, api, settings, lightning, seedReader, sweepRecords, sweepAddresses, runtime, writeLog,
            provisionerLog, protector, sweepEngine, depositService, stableBalanceService,
            crossChainCatalog, crossChainRequests);
    }

    /// <summary>
    /// Gives a controller the request context it needs outside a pipeline, with the authorised store set the way
    /// BTCPay's authorisation filter sets it.
    /// </summary>
    private static void BindContext(
        ControllerBase controller, StoreData? store, bool withTempData, bool serverAdmin = false)
    {
        // RedirectToAction reaches for IUrlHelperFactory through RequestServices, so the MVC success paths need one.
        var services = new ServiceCollection();
        services.AddSingleton<IUrlHelperFactory>(new UrlHelperFactory());

        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            // The role claim is what User.IsInRole reads, and ClaimsIdentity resolves it through the default
            // role claim type unless told otherwise — so this is the same shape core's own principal has.
            User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    serverAdmin
                        ? [
                            new Claim(ClaimTypes.NameIdentifier, "attacker"),
                            new Claim(ClaimTypes.Role, Roles.ServerAdmin)
                        ]
                        : [new Claim(ClaimTypes.NameIdentifier, "attacker")],
                    "Identity.Application"))
        };

        // What BTCPay's authorisation filter sets, and the only trustworthy statement of which store this request
        // was allowed to touch. Null when nothing was authorised.
        httpContext.SetStoreData(store);

        // ControllerActionDescriptor specifically: ControllerContext rejects a bare ActionDescriptor.
        controller.ControllerContext = new ControllerContext(
            new ActionContext(
                httpContext, new RouteData(), new ControllerActionDescriptor(), new ModelStateDictionary()));

        if (withTempData && controller is Controller mvc)
            mvc.TempData = new TempDataDictionary(httpContext, new NullTempDataProvider());
    }

    /// <summary>
    /// The default unilateral-exit service: a store with nothing in flight, and a refusal for every write.
    /// </summary>
    /// <remarks>
    /// The exit flow is behind an environment switch and off for the whole suite bar the tests that turn it on,
    /// so this exists to satisfy the constructor rather than to be exercised. It answers the read with an empty,
    /// unacknowledged store — the state every other page test is implicitly asserting nothing about — and
    /// refuses every write with a sentence that names itself, so a test that unexpectedly reaches one sees where
    /// it came from.
    /// </remarks>
    private sealed class UnavailableUnilateralExitService : ISparkUnilateralExitService
    {
        private static UnilateralExitOpResult Refused =>
            new(false, "No unilateral-exit service was supplied to this test harness.", null);

        public Task<UnilateralExitPageData> ReadAsync(string storeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new UnilateralExitPageData(
                    WalletRunning: false,
                    DisclosureAcknowledged: false,
                    BalanceSats: 0,
                    ActiveRecord: null,
                    History: [],
                    FundingReceivedSat: null,
                    FundingLargestOutputSat: null,
                    LeafCount: null,
                    FundingKeyPath: null,
                    Transactions: null,
                    TransactionsUnreadable: false));

        public Task<UnilateralExitOpResult> AcknowledgeDisclosureAsync(
            string storeId, CancellationToken cancellationToken = default) => Task.FromResult(Refused);

        public Task<UnilateralExitOpResult> QuoteAsync(
            string storeId,
            long feeRateSatPerVbyte,
            string destinationAddress,
            CancellationToken cancellationToken = default) => Task.FromResult(Refused);

        public Task<UnilateralExitOpResult> BuildAsync(
            string storeId, string recordId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Refused);

        public Task<UnilateralExitOpResult> AbandonAsync(
            string storeId, string recordId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Refused);

        public Task<UnilateralExitOpResult> MarkCompletedAsync(
            string storeId, string recordId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Refused);

        public Task<UnilateralExitOpResult> SetExplorerUrlAsync(
            string storeId, string? esploraApiUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(Refused);
    }
}
