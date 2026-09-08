using System.Linq;
using BTCPayServer.Configuration;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBXplorer;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// The real <see cref="SparkService"/>, over fakes, with a connect deadline short enough to test.
/// </summary>
/// <remarks>
/// <para>
/// The class under test rather than a stand-in for it. Its startup loop, its per-store instance lifecycle and
/// its settings cache are all things whose failure modes are invisible to a test that reimplements them —
/// notably the one this exists for: an SDK connect that never returns, on the host's
/// <c>IHostedService.StartAsync</c> path, which has no timeout of its own.
/// </para>
/// <para>
/// Nothing here touches a database or the SDK's native library beyond what <c>SparkService.StartAsync</c>
/// itself does. The store repository is BTCPay's own <c>IStoreRepository</c> contract, faked; every other
/// dependency is the plugin's own type built over fakes the rest of the suite already uses.
/// </para>
/// </remarks>
public sealed class SparkServiceHarness : IDisposable
{
    private readonly string _dataDir;
    private readonly Durable _durable;
    private readonly Deadlines _deadlines;
    private bool _ownsDataDir = true;

    /// <summary>
    /// Everything that outlives a restart of the process, and nothing that does not.
    /// </summary>
    /// <remarks>
    /// The split is the point of the restart seam. The database, BTCPay's store settings, BTCPay's invoice
    /// index and the data-protection keys survive a restart on a real server, so <see cref="Restart"/> carries
    /// them across; the service instance, the SDK connections and the settlement broadcaster do not, and are
    /// rebuilt. Carrying the wrong thing across would hide exactly the class of defect this exists to catch —
    /// notably an in-memory broadcaster that appears to keep working because the test kept it alive.
    /// </remarks>
    private sealed record Durable(
        FakeStoreRepository Stores,
        FakeStoreLightningConfigStore Lightning,
        SparkMnemonicProtector Protector,
        InMemoryInvoiceRecordStore Invoices,
        FakeInvoiceCreditGateway Credits,
        StubBolt11Parser Bolt11);

    private sealed record Deadlines(
        TimeSpan Connect,
        TimeSpan ConfirmStatus,
        TimeSpan AbandonedConnectGrace);

    /// <summary>
    /// The chain the service's network provider reports, carried across a <see cref="Restart"/>.
    /// </summary>
    /// <remarks>
    /// Regtest for every test but the custom-network one, which has to prove that the environment variable
    /// naming a local Spark stack is not read on mainnet. That is a property of the network, so the network
    /// has to be a parameter rather than a constant.
    /// </remarks>
    private readonly ChainName _chain;

    private SparkServiceHarness(
        TestableSparkService service,
        FakeSparkSdkClientFactory sdk,
        SparkSettlementBroadcaster broadcaster,
        CapturingLogger<SparkService> log,
        string dataDir,
        Durable durable,
        Deadlines deadlines,
        ChainName chain)
    {
        Service = service;
        Sdk = sdk;
        Broadcaster = broadcaster;
        Log = log;
        _dataDir = dataDir;
        _durable = durable;
        _deadlines = deadlines;
        _chain = chain;
    }

    public SparkService Service { get; }
    public FakeStoreRepository Stores => _durable.Stores;
    public FakeSparkSdkClientFactory Sdk { get; }
    public FakeStoreLightningConfigStore Lightning => _durable.Lightning;
    public SparkMnemonicProtector Protector => _durable.Protector;

    /// <summary>The invoice records the service settles against — the far end of the event wiring.</summary>
    public InMemoryInvoiceRecordStore Invoices => _durable.Invoices;

    /// <summary>
    /// BTCPay's side of the settlement: the payment-hash index it keeps and the payments it holds.
    /// </summary>
    public FakeInvoiceCreditGateway Credits => _durable.Credits;

    /// <summary>
    /// What a BOLT11 means. Carried across a restart because real BOLT11 parsing is deterministic — the
    /// registrations stand in for the invoices themselves, which of course outlive the process.
    /// </summary>
    public StubBolt11Parser Bolt11 => _durable.Bolt11;

    /// <summary>What a settled invoice is announced on, which is what wakes BTCPay's listening session.</summary>
    public SparkSettlementBroadcaster Broadcaster { get; }

    public CapturingLogger<SparkService> Log { get; }

    /// <summary>The BTCPay data directory this service was given, which is where its per-store storage lives.</summary>
    public string DataDir => _dataDir;

    /// <summary>
    /// Where a store's SDK storage lives, resolved the way the service resolves it.
    /// </summary>
    /// <remarks>
    /// Through the production helper rather than by re-spelling the layout: a test that hard-coded the path
    /// would keep passing after a change that moved it, while the guard it is checking stopped covering
    /// anything.
    /// </remarks>
    public string StorageDirFor(string storeId) =>
        FileSparkStorageProvider.GetStorageDirectory(_dataDir, storeId);

    /// <summary>A valid BIP39 phrase. Distinct phrases matter: the wallet-owner guard keys on the seed.</summary>
    public static string MnemonicFor(int index)
    {
        var entropy = new byte[16];
        entropy[0] = (byte)index;
        return new Mnemonic(Wordlist.English, entropy).ToString();
    }

    /// <param name="failWalletAdoption">
    /// Builds the service without the BOLT11 parser, so <c>SparkLightningClient</c>'s own ctor throws the
    /// moment it tries to adopt a connected wallet. The throw is production code's own argument check, not a
    /// fake standing in for it — the only thing this withholds is a real dependency.
    /// </param>
    /// <param name="chain">
    /// The chain the network provider reports. Regtest by default, which is what every test but the
    /// custom-network one wants; <see cref="ChainName.Mainnet"/> is how the mainnet half of that test is
    /// expressed.
    /// </param>
    public static SparkServiceHarness Create(
        TimeSpan? connectDeadline = null,
        TimeSpan? confirmStatusDeadline = null,
        TimeSpan? abandonedConnectGrace = null,
        bool failWalletAdoption = false,
        ChainName? chain = null)
    {
        var dataDir = Path.Combine(
            Path.GetTempPath(), "spark-service-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        return Create(
            dataDir,
            new Durable(
                new FakeStoreRepository(),
                FakeStoreLightningConfigStore.WithStore("unused"),
                new SparkMnemonicProtector(new EphemeralDataProtectionProvider()),
                new InMemoryInvoiceRecordStore(),
                new FakeInvoiceCreditGateway(),
                new StubBolt11Parser()),
            new Deadlines(
                connectDeadline ?? TimeSpan.FromMilliseconds(250),
                confirmStatusDeadline ?? Constants.SdkCallDeadline,
                // Long by default so the existing abandon tests still observe a late connect being adopted and
                // shut down; the release-the-lock test shortens it deliberately.
                abandonedConnectGrace ?? TimeSpan.FromMinutes(5)),
            chain ?? ChainName.Regtest,
            failWalletAdoption);
    }

    /// <summary>
    /// Stops this service and brings a fresh one up over the same durable state, as a server restart does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The returned harness is the live one and owns the data directory; this one must not be used again, and
    /// disposing it no longer deletes the directory. Disposing the replacement tears everything down.
    /// </para>
    /// <para>
    /// The data-protection provider is deliberately carried across rather than rebuilt: it is ephemeral, so a
    /// new one could not unprotect the mnemonics the seeded store settings already hold, and every store would
    /// fail to start for a reason that has nothing to do with what the test is asking.
    /// </para>
    /// </remarks>
    public SparkServiceHarness Restart()
    {
        StopService();
        _ownsDataDir = false;
        return Create(_dataDir, _durable, _deadlines, _chain);
    }

    private static SparkServiceHarness Create(
        string dataDir, Durable durable, Deadlines deadlines, ChainName chain,
        bool failWalletAdoption = false)
    {
        var log = new CapturingLogger<SparkService>();
        var logs = new Logs();
        logs.Configure(NullLoggerFactory.Instance);

        var stores = durable.Stores;
        var sdk = new FakeSparkSdkClientFactory();
        var lightning = durable.Lightning;
        var protector = durable.Protector;

        // Rebuilt on every start, exactly as on a real restart: it is in-memory fan-out to whatever listening
        // sessions exist now, and it is precisely the thing that cannot carry a pending notification across.
        var broadcaster = new SparkSettlementBroadcaster(NullLogger<SparkSettlementBroadcaster>.Instance);
        var invoices = durable.Invoices;
        var reconciler = new SparkSettlementReconciler(
            invoices,
            broadcaster,
            new SparkInvoiceCreditor(
                durable.Credits, invoices, NullLogger<SparkInvoiceCreditor>.Instance),
            NullLogger<SparkSettlementReconciler>.Instance);
        var wiring = new SparkLightningWiring(lightning, NullLogger<SparkLightningWiring>.Instance);

        // The one dependency a test may ask to be withheld. SparkLightningClient's own ctor rejects a null
        // BOLT11 parser, so a service built without one throws at the moment it adopts a connected wallet —
        // that throw is production code's own argument check, and nothing in production gained a seam for it.
        IBolt11Parser bolt11Parser = failWalletAdoption ? null! : durable.Bolt11;

        // The startup sweep is real here — it runs against the stores this harness knows about — so the
        // deferred factory is wired to a sweeper built over the same fakes. The sweeper is constructed after
        // the service because its settings store is the service itself; the closure makes that ordering safe.
        SparkLightningConfigSweeper? sweeper = null;

        var service = new TestableSparkService(
            deadlines.Connect,
            deadlines.ConfirmStatus,
            deadlines.AbandonedConnectGrace,
            new BTCPayServer.EventAggregator(logs),
            stores,
            Options.Create(new DataDirectories { DataDir = dataDir }),
            new BTCPayNetworkProvider([], new NBXplorerNetworkProvider(chain), logs),
            sdk,
            invoices,
            new InMemoryOutgoingPaymentStore(),
            reconciler,
            broadcaster,
            protector,
            wiring,
            bolt11Parser,
            TimeProvider.System,
            () => sweeper ?? throw new InvalidOperationException("harness sweep not wired"),
            NullLoggerFactory.Instance,
            log);

        sweeper = new SparkLightningConfigSweeper(
            new FakeStoreSource(lightning.Stores.Keys.ToArray()),
            wiring,
            service,
            NullLogger<SparkLightningConfigSweeper>.Instance);

        return new SparkServiceHarness(service, sdk, broadcaster, log, dataDir, durable, deadlines, chain);
    }

    /// <summary>Stores a store's Spark settings the way a previous run would have left them.</summary>
    public SparkServiceHarness SeedStore(string storeId, string mnemonic, string? paymentKey = null)
    {
        Stores.Seed(storeId, Constants.StoreSettingsKey, new SparkSettings
        {
            ProtectedMnemonic = Protector.Protect(mnemonic),
            PaymentKey = paymentKey ?? SparkConnectionString.GeneratePaymentKey(),
            SeedSource = SeedSource.Generated
        });
        return this;
    }

    public void Dispose()
    {
        StopService();

        if (!_ownsDataDir)
            return;

        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Shuts the service down without touching the durable state, bounded so a hang cannot hide a failure.
    /// </summary>
    /// <remarks>
    /// Bounded because <c>StopAsync</c> takes the same instance lock the startup loop holds. If a regression
    /// has put a never-returning await back on the startup path, that lock is held forever and an unbounded
    /// teardown would turn a failed assertion into a hung test run — hiding the very failure the assertion
    /// just reported.
    /// </remarks>
    private void StopService()
    {
        try
        {
            Service.StopAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
            // Teardown of a service that never fully started is not worth failing a test over.
        }
    }

    /// <summary>
    /// <see cref="SparkService"/> with the two SDK deadlines shortened so a test can wait them out.
    /// </summary>
    /// <remarks>
    /// The only things overridden, and only their durations. Everything the tests assert on — the startup loop,
    /// the abandonment, the settings cache, the whole event-consumption path — is the production
    /// implementation. Both default to the production value, so a test that does not care about a deadline is
    /// running the shipped one rather than a convenient one.
    /// </remarks>
    private sealed class TestableSparkService : SparkService
    {
        private readonly TimeSpan _connectDeadline;
        private readonly TimeSpan _confirmStatusDeadline;
        private readonly TimeSpan _abandonedConnectGrace;

        public TestableSparkService(
            TimeSpan connectDeadline,
            TimeSpan confirmStatusDeadline,
            TimeSpan abandonedConnectGrace,
            BTCPayServer.EventAggregator eventAggregator,
            BTCPayServer.Abstractions.Contracts.IStoreRepository storeRepository,
            IOptions<DataDirectories> dataDirectories,
            BTCPayNetworkProvider networkProvider,
            ISparkSdkClientFactory sdkClientFactory,
            Data.IInvoiceRecordStore invoiceStore,
            Data.IOutgoingPaymentStore outgoingStore,
            SparkSettlementReconciler reconciler,
            SparkSettlementBroadcaster broadcaster,
            SparkMnemonicProtector mnemonicProtector,
            SparkLightningWiring lightningWiring,
            IBolt11Parser bolt11Parser,
            TimeProvider timeProvider,
            Func<SparkLightningConfigSweeper> configSweeperFactory,
            ILoggerFactory loggerFactory,
            ILogger<SparkService> logger)
            : base(eventAggregator, storeRepository, dataDirectories, networkProvider, sdkClientFactory,
                invoiceStore, outgoingStore, reconciler, broadcaster, mnemonicProtector, lightningWiring,
                bolt11Parser, timeProvider, configSweeperFactory, loggerFactory, logger)
        {
            _connectDeadline = connectDeadline;
            _confirmStatusDeadline = confirmStatusDeadline;
            _abandonedConnectGrace = abandonedConnectGrace;
        }

        protected override TimeSpan ConnectDeadline => _connectDeadline;
        protected override TimeSpan ConfirmStatusDeadline => _confirmStatusDeadline;
        protected override TimeSpan AbandonedConnectGraceDeadline => _abandonedConnectGrace;
    }
}
