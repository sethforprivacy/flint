using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using SdkNetwork = Breez.Sdk.Spark.Network;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// Owns the lifecycle of one long-lived Breez Spark SDK instance per configured store.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton and as an <c>IHostedService</c>. Derives from
/// <see cref="EventHostedServiceBase"/> so it can react to BTCPay's event aggregator (store deletion in
/// particular) on the base class's serialized event loop.
/// </para>
/// <para>
/// Instances run in the SDK's <em>client</em> mode, not server mode: server mode disables the in-process
/// event stream and hard-fails on Stable Balance config, which would foreclose the post-MVP roadmap
/// — an architectural decision taken before any code was written.
/// </para>
/// <para><b>The invariants this class exists to hold.</b></para>
/// <list type="number">
/// <item><description><b>Exactly one live SDK instance per wallet.</b> Nothing in the SDK enforces this: two
/// instances on one wallet and one non-WAL SQLite file connect happily and both mint invoices, which is a
/// lost-write hazard. Every create, replace and teardown goes through <see cref="_instanceLock"/>, an
/// instance is removed from <see cref="_instances"/> <em>before</em> it is disposed so no request can race
/// onto a disposed handle, and the guard is keyed on the wallet rather than the store — two stores
/// configured with the same seed are the same wallet.</description></item>
/// <item><description><b>Teardown is <c>Disconnect()</c> and <c>Dispose()</c>.</b> After <c>Disconnect()</c>
/// alone the instance still serves the network and still mints live invoices for a store the merchant just
/// disabled.</description></item>
/// <item><description><b>No real work in the SDK's event callback.</b> Events are dispatched inline on an SDK
/// thread and a listener that blocks or throws deadlocks the whole process. The listener only writes to its
/// store's channel; everything else happens on that store's consumer loop.</description></item>
/// <item><description><b>One store cannot stall another.</b> Each instance has its own event channel and its
/// own consumer, because handling an event involves an SDK call that cannot be cancelled.</description></item>
/// </list>
/// </remarks>
public class SparkService : EventHostedServiceBase, ISparkClientResolver, ISparkStoreSettingsStore,
    ISparkStoreRuntime
{
    /// <summary>
    /// How long a synchronous caller (the connection-string handler, invoked from BTCPay's Lightning client
    /// factory on a request thread) waits for startup before reporting a transient failure.
    /// </summary>
    /// <remarks>
    /// Deliberately short. Blocking a request thread for tens of seconds starves the thread pool, and during
    /// startup it can starve this service's own continuations — a checkout that waits 30 s and then fails is
    /// worse for everyone than one that fails immediately and is retried.
    /// </remarks>
    private static readonly TimeSpan StartupWaitTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Depth of a store's SDK event queue. Bounded so a stalled consumer refuses new events — which the
    /// reconciliation task then recovers — instead of growing without limit.
    /// </summary>
    private const int EventQueueCapacity = 1024;

    /// <summary>
    /// How long one store's SDK connect may take before it is abandoned and the store left not running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per store, deliberately, and not a budget for the whole startup: one store's broken wallet must not stop
    /// the next store's from being tried. Generous, because the cost of being wrong in the strict direction is a
    /// merchant whose Lightning is down until they notice, whereas the cost of being wrong in the lax direction
    /// is bounded by this value.
    /// </para>
    /// <para>
    /// Overridable only so tests can assert the abandonment without waiting the real deadline out. Nothing in
    /// production changes it.
    /// </para>
    /// </remarks>
    protected virtual TimeSpan ConnectDeadline => Constants.SdkCallDeadline;

    /// <summary>
    /// How long the authoritative status re-read in <see cref="ConfirmStatusAsync"/> may take.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ConnectDeadline"/> only because the two are exercised by different tests;
    /// both are <see cref="Constants.SdkCallDeadline"/> in production and nothing changes them there. This one
    /// bounds a call on a store's event-consumer loop rather than on the host's startup path, so what it
    /// protects is different: a hung service-provider read would otherwise stall every later event for that
    /// store behind it, including the completion of a different invoice.
    /// </remarks>
    protected virtual TimeSpan ConfirmStatusDeadline => Constants.SdkCallDeadline;

    /// <summary>
    /// How long an already-abandoned connect keeps its store's storage lock before the lock is released anyway.
    /// </summary>
    /// <remarks>
    /// Much longer than <see cref="ConnectDeadline"/>, because the two protect different things. The connect
    /// deadline stops one store delaying host startup; this one stops a permanently hung connect locking a store
    /// out of its own wallet until the process is restarted. Waiting minutes is fine — the store's Lightning is
    /// already down by this point — and the cost of releasing too eagerly is real: the lock is what stops a
    /// second SDK instance opening the same non-WAL SQLite storage, which is a lost-write hazard. Overridable
    /// only so tests need not wait it out.
    /// </remarks>
    protected virtual TimeSpan AbandonedConnectGraceDeadline => TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long an exit-state export or import is waited on before it is abandoned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The automatic pass runs on a loop it does not own.</b> BTCPay runs every scheduled task on a small
    /// shared pool of periodic loops — the rate refresh and this plugin's own settlement reconciliation among
    /// them — so an export that never returned would occupy one of those loops for the life of the process.
    /// No SDK call can be cancelled; the deadline bounds the <em>wait</em>, and the pass then skips that store
    /// while the abandoned call is still running rather than stacking another on top of it.
    /// </para>
    /// <para>
    /// Two minutes because both calls are local — they read and write the SDK's own storage, with no operator
    /// round trip — and a multi-megabyte wallet serialises in well under that; a call still running after it
    /// is stuck, not slow. The import shares it: a hung import at connect would otherwise hold the restore
    /// gate the automatic pass waits on. Overridable only so tests need not wait it out.
    /// </para>
    /// </remarks>
    protected virtual TimeSpan ExitStateCallDeadline => TimeSpan.FromMinutes(2);

    private readonly IStoreRepository _storeRepository;
    private readonly IOptions<DataDirectories> _dataDirectories;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly ISparkSdkClientFactory _sdkClientFactory;
    private readonly IInvoiceRecordStore _invoiceStore;
    private readonly IOutgoingPaymentStore _outgoingStore;
    private readonly SparkSettlementReconciler _reconciler;
    private readonly SparkSettlementBroadcaster _broadcaster;
    private readonly SparkMnemonicProtector _mnemonicProtector;
    private readonly SparkLightningWiring _lightningWiring;
    private readonly IBolt11Parser _bolt11Parser;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SparkService> _logger;

    /// <summary>
    /// Cached per-store settings, keyed by store id. Populated once in <see cref="StartAsync"/> and kept in
    /// sync by <see cref="Set"/>.
    /// </summary>
    private readonly ConcurrentDictionary<string, SparkSettings> _settings = new();

    /// <summary>
    /// Live SDK-backed instances, keyed by store id. Concurrent because views, the connection-string handler,
    /// the scheduled tasks and the event loops all read it.
    /// </summary>
    private readonly ConcurrentDictionary<string, SparkStoreInstance> _instances = new();

    /// <summary>
    /// Which store currently holds each wallet, keyed by a fingerprint of the seed. In-memory only, and never
    /// logged: it is derived from the mnemonic.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _walletOwners = new();

    /// <summary>
    /// Serialises instance creation, replacement and teardown. This is the single-flight guarantee the SDK
    /// does not provide.
    /// </summary>
    private readonly SemaphoreSlim _instanceLock = new(1, 1);

    /// <summary>
    /// Set once the local-regtest network descriptor has been reported, so the line appears one time per
    /// process rather than once per store and again on every wallet restart.
    /// </summary>
    /// <remarks>
    /// An <c>int</c> driven by <see cref="Interlocked.Exchange(ref int, int)"/> rather than a
    /// <c>bool</c>: stores are connected from the startup loop and from the provisioning path, so two
    /// threads can reach this and a plain read-then-write would log twice.
    /// </remarks>
    private int _customNetworkReported;

    /// <summary>
    /// Startup gate. Everything that reads <see cref="_settings"/> or <see cref="_instances"/> waits on this
    /// so a request arriving during host startup cannot observe an empty cache and conclude the store is
    /// unconfigured.
    /// </summary>
    private readonly TaskCompletionSource _startupGate =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The budget and rotation position shared by every reconciliation pass.
    /// </summary>
    /// <remarks>
    /// One per process, and deliberately shared between the scheduled task and the startup catch-up: they are
    /// the same walk over the same stores, so a second position would let the two disagree about which store is
    /// next and undo the round-robin. See <see cref="SparkStorePassScheduler"/> for why the walk is bounded.
    /// </remarks>
    private readonly SparkStorePassScheduler _reconciliationPass;

    /// <summary>
    /// The USDC/USDT path, deferred because it reads the store runtime this service is, and is reached from the
    /// payment-method handler dictionary this service must not be built from inside (see <c>SparkPlugin</c>).
    /// </summary>
    private readonly Func<StablecoinPaymentService> _stablecoinsFactory;

    /// <summary>
    /// Where each store's automatic exit-state backup is kept. One file per store, in a directory of the
    /// plugin's own; nothing else in this class opens it.
    /// </summary>
    private readonly IExitStateBackupStore _exitStateBackupStore;

    /// <summary>
    /// The debounce/safety-net decisions for those backups, plus the per-store content hash that stops an
    /// unchanged state being rewritten. Singleton because the requests arrive on per-store event loops and
    /// the scheduled pass consumes them from another thread entirely.
    /// </summary>
    private readonly ExitStateBackupScheduler _exitStateBackupScheduler;

    /// <summary>
    /// The clock every scheduled decision reads. Kept as a field because the backup pass compares it
    /// against times the event path recorded through the scheduler; a test advances one and observes the
    /// other.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    public SparkService(
        EventAggregator eventAggregator,
        IStoreRepository storeRepository,
        IOptions<DataDirectories> dataDirectories,
        BTCPayNetworkProvider networkProvider,
        ISparkSdkClientFactory sdkClientFactory,
        IInvoiceRecordStore invoiceStore,
        IOutgoingPaymentStore outgoingStore,
        SparkSettlementReconciler reconciler,
        SparkSettlementBroadcaster broadcaster,
        SparkMnemonicProtector mnemonicProtector,
        SparkLightningWiring lightningWiring,
        IBolt11Parser bolt11Parser,
        TimeProvider timeProvider,
        Func<StablecoinPaymentService> stablecoinsFactory,
        ILoggerFactory loggerFactory,
        IExitStateBackupStore exitStateBackupStore,
        ExitStateBackupScheduler exitStateBackupScheduler,
        ILogger<SparkService> logger) : base(eventAggregator, logger)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _reconciliationPass = new SparkStorePassScheduler(
            "reconciliation",
            Constants.ReconciliationPassBudget,
            Constants.ReconciliationStoreDeadline,
            timeProvider,
            logger);

        _storeRepository = storeRepository;
        _dataDirectories = dataDirectories;
        _networkProvider = networkProvider;
        _sdkClientFactory = sdkClientFactory;
        _invoiceStore = invoiceStore;
        _outgoingStore = outgoingStore;
        _reconciler = reconciler;
        _broadcaster = broadcaster;
        _mnemonicProtector = mnemonicProtector;
        _lightningWiring = lightningWiring;
        _bolt11Parser = bolt11Parser;
        _stablecoinsFactory = stablecoinsFactory;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _timeProvider = timeProvider;
        _exitStateBackupStore = exitStateBackupStore;
        _exitStateBackupScheduler = exitStateBackupScheduler;
    }

    #region Hosted service lifecycle

    protected override void SubscribeToEvents()
    {
        // A deleted store must not leave a running SDK instance behind.
        Subscribe<StoreEvent.Removed>();
        base.SubscribeToEvents();
    }

    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        if (evt is StoreEvent.Removed removed)
        {
            _settings.TryRemove(removed.StoreId, out _);
            await _instanceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await TeardownInstanceAsync(removed.StoreId).ConfigureAwait(false);
            }
            finally
            {
                _instanceLock.Release();
            }

            // The storage directory is deliberately left in place. It holds the SDK's record of settled
            // payments for a wallet whose seed the merchant may still control, and deleting a BTCPay store
            // is not evidence that the funds on that wallet have been swept.
            _logger.LogInformation(
                "Store {StoreId} removed; Spark instance shut down. Its SDK storage at {StorageDir} was left in place",
                removed.StoreId, GetWorkDir(removed.StoreId));

            // The exit-state backups for the same reason, and moved aside rather than left under their live
            // names: see SetAsideExitStateBackupsAsync.
            await SetAsideExitStateBackupsAsync(removed.StoreId).ConfigureAwait(false);
        }

        await base.ProcessEvent(evt, cancellationToken).ConfigureAwait(false);
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Process-global and one-shot; not per store. Also what loads the native library (~450 ms).
            SparkLogging.TryInitialise(
                Path.Combine(_dataDirectories.Value.DataDir, "Plugins", Constants.WorkDirName, "logs"),
                _loggerFactory.CreateLogger("Breez.Sdk.Spark"),
                Constants.SdkLogFilter);

            var stored = await _storeRepository.GetSettingsAsync<SparkSettings>(Constants.StoreSettingsKey)
                .ConfigureAwait(false);

            await _instanceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var (storeId, settings) in stored.Where(pair => pair.Value is not null))
                {
                    // Not cloned: these came straight out of the repository's own deserialisation, so nothing
                    // else holds a reference to them.
                    _settings[storeId] = settings!;
                    try
                    {
                        if (await StartInstanceAsync(storeId, settings!, cancellationToken).ConfigureAwait(false)
                            is { } declined)
                        {
                            // Already logged in detail by StartInstanceAsync; repeated here so one line in the
                            // startup log names both the store and the consequence.
                            _logger.LogError(
                                "Store {StoreId}: its Spark wallet did not start ({Reason}). Lightning payments "
                                + "for this store are unavailable until the configuration is corrected",
                                storeId, declined);
                        }
                    }
                    catch (Exception ex) when (SparkErrors.IsStorageFromNewerVersion(ex))
                    {
                        // Not a configuration problem, and the generic line below would say it is: the
                        // storage was migrated by a newer SDK (a plugin downgrade), and nothing in the
                        // store's settings can fix that. Said in plain words, with the directory, and
                        // without the exception — its text is the SDK's own and adds nothing an operator
                        // can act on.
                        _logger.LogError(
                            "Store {StoreId}: its Spark wallet storage at {StorageDir} was written by a newer "
                            + "version of the Flint plugin (and its Spark SDK) than the one installed now, so "
                            + "this version cannot open it. Install that version of the plugin or a newer one "
                            + "to bring this store's Lightning payments back. The wallet's funds are not "
                            + "affected, and its storage was left as it was",
                            storeId, GetWorkDir(storeId));
                    }
                    catch (Exception ex)
                    {
                        // Per-store and non-fatal: one broken store must not stop BTCPay from starting, and
                        // the merchant needs the server up in order to fix their configuration.
                        _logger.LogError(ex,
                            "Store {StoreId}: could not start its Spark wallet ({Reason}). Lightning payments "
                            + "for this store are unavailable until the configuration is corrected",
                            storeId, SparkErrors.Describe(ex));
                    }
                }
            }
            finally
            {
                _instanceLock.Release();
            }
        }
        finally
        {
            // Always open the gate, even on failure, otherwise every caller reports a transient failure.
            _startupGate.TrySetResult();
        }

        // Catch up on anything that settled while the process was down. Not awaited: it is a walk over
        // every store, and the host must not wait on it. The cross-store Lightning sweep is deliberately
        // not run here — SparkLightningConfigSweepTask's first pass fires at startup through BTCPay's
        // periodic task launcher, and running it from both places would walk the store table twice and
        // widen the window in which a sweep could rotate a victim's payment key twice.
        _ = Task.Run(async () =>
        {
            try
            {
                await ReconcileAllStoresAsync(CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!CancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "The Spark startup reconciliation pass failed");
            }
        }, CancellationToken);

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _instanceLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            foreach (var storeId in _instances.Keys.ToList())
                await TeardownInstanceAsync(storeId).ConfigureAwait(false);
        }
        finally
        {
            _instanceLock.Release();
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    #endregion

    #region Instance management

    /// <summary>
    /// Brings one store's instance up, replacing any existing one. Caller must hold
    /// <see cref="_instanceLock"/>.
    /// </summary>
    /// <returns>
    /// Null when the instance is running, or a merchant-facing reason why it is not.
    /// </returns>
    /// <remarks>
    /// Declining to start is not the same as failing. Four of the conditions below are configuration problems
    /// with no exception to throw, and returning silently made them indistinguishable from success — which let
    /// the setup flow tell a merchant Spark was ready and enable a Lightning payment method that could never
    /// take a payment. Each one is both logged for the operator and returned for the merchant, because the two
    /// need different amounts of detail.
    /// </remarks>
    private async Task<string?> StartInstanceAsync(
        string storeId,
        SparkSettings settings,
        CancellationToken cancellationToken)
    {
        await TeardownInstanceAsync(storeId).ConfigureAwait(false);

        var mnemonic = _mnemonicProtector.TryUnprotect(settings.ProtectedMnemonic);
        if (mnemonic is null)
        {
            _logger.LogWarning(
                "Store {StoreId}: no usable Spark seed. Either none has been configured yet, or the stored "
                + "seed cannot be decrypted with this server's data-protection keys and must be re-entered",
                storeId);
            return "The stored recovery phrase could not be read. This server's data-protection keys may have "
                   + "changed, so the phrase has to be entered again.";
        }

        if (settings.PaymentKey is null)
        {
            // Without a payment key the connection string cannot be verified, so the instance would be
            // unreachable anyway. Refusing to start it keeps a wallet from running with no way to use it.
            _logger.LogWarning(
                "Store {StoreId}: Spark settings have no payment key; complete the setup page first", storeId);
            return "This store's Spark configuration is incomplete.";
        }

        if (!SparkNetworks.TryGetSdkNetwork(_networkProvider.NetworkType, out var sdkNetwork, out var networkError))
        {
            _logger.LogError("Store {StoreId}: {Error}", storeId, networkError);
            return networkError;
        }

        // Resolved here — before the wallet-owner check, before the storage claim, before anything is
        // allocated — so that a descriptor that cannot be read costs nothing and names itself.
        var customNetwork = ResolveCustomNetwork(sdkNetwork, storeId, out var customNetworkError);
        if (customNetworkError is not null)
            return customNetworkError;

        // The SDK's hazard is per wallet, not per store: two instances on one seed corrupt one SQLite file
        // even though the storage directories differ. Two stores sharing a seed is not hypothetical — reusing
        // the BTCPay hot-wallet seed on two stores of the same server does it.
        // Passphrase kept alongside the seed in the key so that if Wave 3 ever offers one, two stores with the
        // same words but different passphrases are correctly treated as different wallets.
        var walletKey = DeriveWalletKey(mnemonic, passphrase: null, sdkNetwork);
        if (_walletOwners.TryGetValue(walletKey, out var owner) && owner != storeId)
        {
            _logger.LogError(
                "Store {StoreId}: refusing to start a Spark wallet because store {OwnerStoreId} is already "
                + "running the same seed. Two instances on one wallet corrupt its storage. Give this store its "
                + "own seed",
                storeId, owner);

            // The owning store is deliberately not named in the returned text: whoever is configuring this
            // store may have no business knowing which other store on this server holds that wallet.
            return "Another store on this server already uses this recovery phrase. Two stores cannot share one "
                   + "Spark wallet — it corrupts the wallet's storage — so this store needs its own phrase.";
        }

        // Taken before anything connects, and released only at teardown. The wallet-owner check above is an
        // in-memory dictionary and therefore per process: two BTCPay instances sharing one data directory each
        // pass it and each connect an SDK instance to the same non-WAL SQLite file. See SparkStorageLock for
        // why that is a corruption hazard rather than merely a duplicate-sweep one.
        var storageLock = SparkStorageLock.TryAcquire(GetWorkDir(storeId), out var lockReason, out var lockDetail);
        if (storageLock is null)
        {
            // The reason is logged as well as returned: an instance holding the claim and a
            // permission refusal arrive here through different exceptions and call for
            // different fixes, and a log line that pre-decided which one it was would
            // misdirect the operator on half of them. The OS's own words ride along as a
            // structured field — this line is the operator's log, the one place the denied
            // path belongs; the returned reason stays merchant-safe.
            _logger.LogError(
                "Store {StoreId}: refusing to start its Spark wallet. {LockReason} The storage "
                + "directory is {StorageDir}; run a single BTCPay instance against this data "
                + "directory. OS detail: {LockDetail}",
                storeId, lockReason, GetWorkDir(storeId), lockDetail ?? "none");
            return lockReason;
        }

        // Read before the connect creates anything: whether this wallet's storage already existed is what
        // tells the exit-state restore whether the stored backup can still help (see RestoreAutomaticLockedAsync).
        var storageWasEmpty = SdkStorageIsEmpty(GetWorkDir(storeId));

        // The two handoffs below — AbandonConnect in the timeout branch, and the instance registration —
        // are the only things that can own the claim past this point, so every other route out of the
        // guarded region goes through the finally: until it existed, only the timeout path released the
        // claim, and an already-faulted connect (SparkDeadline rethrows it rather than returning null)
        // leaked the FileShare.None handle so the store's own next attempt was refused as if a second
        // BTCPay held it.
        var handedOff = false;
        ISparkSdkClient? sdk = null;
        Channel<SparkEventEnvelope>? events = null;
        try
        {
            var options = new SparkConnectOptions(
                storeId,
                mnemonic,
                // NBXplorer does not store BIP39 passphrases, so a hot-wallet seed reused from BTCPay never
                // carries one, and a passphrase changes the Spark identity entirely. Defaulting one silently
                // would derive a different wallet from the seed the merchant backed up.
                passphrase: null,
                apiKey: string.IsNullOrWhiteSpace(settings.ApiKeyOverride)
                    ? Constants.BreezApiKey
                    : settings.ApiKeyOverride,
                network: sdkNetwork,
                // Always supplied, never left to the SDK. Its default is Rate(1 sat/vB) — a cap rather than a bid —
                // which is below the mainnet floor essentially always, and above it a deposit is never claimed and
                // never surfaces anywhere the merchant looks.
                maxDepositClaimFee: (settings.Deposits ?? new SparkDepositSettings()).ToMaxFee(),
                stableBalance: BuildStableBalance(settings),
                // Null unless this process was pointed at a privately hosted regtest network; see
                // ResolveCustomNetwork for why reading an environment variable here is safe.
                customNetwork: customNetwork);

            // Created before the SDK because the factory registers the event listener against this writer, and
            // events can arrive the moment it does. The channel buffers until the consumer starts below.
            events = Channel.CreateBounded<SparkEventEnvelope>(new BoundedChannelOptions(EventQueueCapacity)
            {
                // Wait, combined with the listener only ever using the non-blocking TryWrite. That pairing is the
                // one that reports a full queue: TryWrite returns false and the listener logs it. DropOldest and
                // DropWrite both return true and evict silently, and silently losing a settlement notification is
                // exactly the class of bug this plugin exists to avoid. The listener never blocks regardless,
                // because it never calls WriteAsync.
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });

            // Bounded, and this is the one call site where that matters most. This method runs on the host's
            // IHostedService.StartAsync path, once per configured store, inside _instanceLock — and
            // HostOptions.StartupTimeout is infinite by default. An unbounded await here is
            // therefore a silent, permanent hang of BTCPay's startup: no exception, so no log line and no
            // auto-disable, which is exactly how PR #6's deadlock presented. Today's SDK Connect does no network
            // I/O and returns in tens of milliseconds, but that is an SDK property and not a guarantee this plugin
            // holds, so the wait is bounded rather than trusted.
            var connect = _sdkClientFactory.ConnectAsync(options, events.Writer, cancellationToken);
            var deadline = ConnectDeadline;
            sdk = await SparkDeadline.OrNullAsync(
                    connect,
                    deadline,
                    () => _logger.LogError(
                        "Store {StoreId}: connecting its Spark wallet exceeded {Seconds}s, so it was abandoned and "
                        + "the store was left without a running wallet. BTCPay itself started normally. No SDK call "
                        + "can be cancelled, so the connect is still running and whatever it produces will be shut "
                        + "down; nothing will be started on this wallet until the store is reconfigured or the "
                        + "server is restarted",
                        storeId, deadline.TotalSeconds),
                    cancellationToken)
                .ConfigureAwait(false);

            if (sdk is null)
            {
                // The abandoned connect's cleanup owns the claim from this call's entry: releasing it here
                // would let the next attempt start a second instance on storage the late wallet is still
                // holding.
                handedOff = true;
                AbandonConnect(storeId, connect, events, storageLock);
                return "This store's Spark wallet did not finish connecting in time, so it is not running. Check "
                       + "the server logs, then reconfigure the store or restart the server to try again.";
            }

            var client = new SparkLightningClient(
                storeId,
                settings.PaymentKey,
                sdk,
                _invoiceStore,
                _outgoingStore,
                _reconciler,
                _broadcaster,
                _bolt11Parser,
                _loggerFactory.CreateLogger<SparkLightningClient>());

            var instance = new SparkStoreInstance(
                storeId, sdk, client, events, storageLock, storageWasEmpty, DeriveWalletTag(walletKey));
            _instances[storeId] = instance;
            // Owned from the registration, not from the constructor call: a SparkStoreInstance ctor throw
            // means the instance never accepted the lock.
            handedOff = true;
            _walletOwners[walletKey] = storeId;
            instance.StartConsumer(envelope => HandleEventAsync(envelope, instance), _logger);

            _logger.LogInformation("Store {StoreId}: Spark wallet connected on {Network}", storeId, sdkNetwork);

            // Connect does no network I/O and validates no credentials, so it is not evidence of health. The first
            // synced call costs ~2.2 s, which is why this is deliberately not awaited: N stores would otherwise
            // add N × 2.2 s to BTCPay's startup for information nothing is waiting on.
            _ = WarmUpAsync(instance);
            return null;
        }
        finally
        {
            if (!handedOff)
            {
                // Partial construction: an SDK that connected but that no instance ever adopted belongs to
                // nobody yet, so it dies here next to the claim it was meant to guard. Its event channel is
                // completed for the reason AbandonConnect completes one: no consumer was ever started for it,
                // so an open writer would only buffer envelopes silently, while a completed one makes the
                // listener's TryWrite report the refusal.
                events?.Writer.TryComplete();
                sdk?.Dispose();
                storageLock.Dispose();
            }
        }
    }

    /// <summary>
    /// The privately hosted Spark network this process was pointed at, or null for the one the SDK ships.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one production read of <see cref="SparkCustomNetworkFile.EnvironmentVariable"/>.</b> It exists so
    /// that a BTCPay Server running inside the local-regtest stack — the <c>BtcpayE2E</c> suite's whole point —
    /// exercises the plugin's <em>real</em> store-connect path against local operators, instead of the suite
    /// reaching around the plugin and connecting an SDK instance of its own. Everything the e2e suite observes
    /// (invoice settlement, the reconciler, the sweep engine, the Greenfield surface) therefore runs on the
    /// same code a mainnet store runs on, with only the signing set swapped.
    /// </para>
    /// <para>
    /// <b>Why an environment variable is safe here.</b> Three independent reasons, and any one of them would do:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// The network check is first, and the variable is <em>not read at all</em> off regtest. A mainnet or
    /// testnet server behaves exactly as it did before this method existed, whatever the environment says.
    /// </description></item>
    /// <item><description>
    /// <c>SparkSdkClientFactory</c> refuses a custom network on any network but
    /// <see cref="SdkNetwork.Regtest"/> anyway, so even a future caller that skipped the check above would get
    /// a throw rather than a wallet on somebody else's signing set.
    /// </description></item>
    /// <item><description>
    /// It is an environment variable of the BTCPay <em>process</em>, not a store setting and not anything a
    /// merchant or an API key can reach. See <see cref="SparkCustomNetwork"/> for why that boundary matters:
    /// whoever can set it can already replace the plugin.
    /// </description></item>
    /// </list>
    /// <para>
    /// A descriptor that exists but cannot be read is a refusal to start the wallet rather than a silent
    /// fallback to Spark's own regtest. The fallback is the worse failure: the store would come up healthy,
    /// against the wrong network, and the e2e suite would report a wallet that never sees its own money.
    /// </para>
    /// </remarks>
    private SparkCustomNetwork? ResolveCustomNetwork(SdkNetwork sdkNetwork, string storeId, out string? error)
    {
        error = null;

        // First, and load-bearing: see reason 1 in the remarks. Nothing below runs off regtest.
        if (sdkNetwork is not SdkNetwork.Regtest)
            return null;

        SparkCustomNetwork? network;
        try
        {
            network = SparkCustomNetworkFile.TryLoadFromEnvironment();
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(
                ex,
                "Store {StoreId}: {EnvironmentVariable} names a Spark network descriptor that could not be "
                + "read, so the store's wallet was not started. Regenerate it with "
                + "e2e/local-regtest/write-network.sh, or unset the variable to use Spark's own regtest",
                storeId, SparkCustomNetworkFile.EnvironmentVariable);

            error = "This server is configured with a local Spark regtest network descriptor that could not "
                    + "be read, so this store's wallet was not started. Check the server logs.";
            return null;
        }

        if (network is null)
            return null;

        // Once per process. Enough to answer "which Spark network is this server actually on?" from the log of
        // a run that has gone wrong, which is the question a locally hosted signing set makes worth asking.
        if (Interlocked.Exchange(ref _customNetworkReported, 1) == 0)
        {
            _logger.LogInformation(
                "Spark wallets on this server connect to the privately hosted regtest network described by "
                + "{EnvironmentVariable}: {OperatorCount} operator(s) at a threshold of {Threshold}, SSP "
                + "{SspBaseUrl}, chain source {EsploraUrl}. Spark's own regtest is not used",
                SparkCustomNetworkFile.EnvironmentVariable,
                network.Operators.Count,
                network.Threshold,
                network.Ssp.BaseUrl,
                network.EsploraUrl);
        }

        return network;
    }

    /// <summary>
    /// The stable-balance token list a store's wallet is configured with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Supplied whenever the store has a token to name — never gated on whether the feature is switched
    /// on.</b> The SDK draws a line the plugin has to respect: <c>stableBalanceConfig</c> declares which tokens
    /// are <em>available</em> to a wallet, while the active label decides which one is <em>on</em>. Conflating
    /// the two is not a tidiness question, it strands money.
    /// </para>
    /// <para>
    /// This gated the config on the enabled flag, and mainnet found the consequence. Saving
    /// <c>enabled: false</c> persists the settings first, which reconnects the wallet — so the wallet came back
    /// up with <em>no stable-balance config at all</em>, and the deactivation call that followed threw
    /// <c>Stable balance is not configured</c>. Deactivation was unreachable, and a merchant's balance was
    /// stranded in USDB with no route back through the plugin. Note the ordering that makes it inescapable: the
    /// state the deactivation needs has to be in the config the <em>reconnect</em> uses, so it cannot be applied
    /// afterwards.
    /// </para>
    /// <para>
    /// Declaring a token on a wallet that never activates it is inert: <c>defaultActiveLabel</c> is null, so the
    /// wallet starts deactivated and stays that way until <c>UpdateUserSettings</c> says otherwise. The cost is
    /// nothing; the benefit is that switching off always works.
    /// </para>
    /// <para>
    /// Not gated on the network here. The factory does that, so the rule lives in one place next to the reason
    /// it exists.
    /// </para>
    /// </remarks>
    internal static SparkStableBalanceConfiguration? BuildStableBalance(SparkSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var stable = settings.StableBalance ?? new StableBalanceSettings();
        if (stable.Token() is not { } token)
            return null;

        return new SparkStableBalanceConfiguration(
            token, stable.EffectiveLabel, stable.MaxSlippageBps, stable.AutoConvertThresholdSats);
    }

    /// <summary>
    /// Cleans up after a connect that missed its deadline, once it eventually finishes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Abandoning the <em>wait</em> does not abandon the call — no SDK method can be cancelled — so a connect
    /// that took too long will still, eventually, hand back a live wallet. Leaving that handle unreferenced is
    /// the worst of both worlds: a wallet nothing can reach, still holding the store's SQLite file and still
    /// able to mint invoices, while the plugin reports the store as not running and a later attempt would put a
    /// second instance on the same wallet. So the late arrival is disconnected and disposed rather than
    /// dropped.
    /// </para>
    /// <para>
    /// The event channel is completed immediately. No consumer was ever started for it, so anything the
    /// listener writes would only accumulate; a completed writer makes the listener's <c>TryWrite</c> report
    /// the refusal instead.
    /// </para>
    /// </remarks>
    private void AbandonConnect(
        string storeId,
        Task<ISparkSdkClient> connect,
        Channel<SparkEventEnvelope> events,
        SparkStorageLock storageLock)
    {
        events.Writer.TryComplete();

        _ = Task.Run(async () =>
        {
            try
            {
                ISparkSdkClient late;
                try
                {
                    // Bounded (audit finding InfraAndLogging F2). Awaiting the abandoned connect forever means
                    // the storage lock in the finally is never released, and that lock is a FileShare.None
                    // handle enforced between descriptors in the *same* process — so the store's own next
                    // attempt fails with "Another process is already using this store's Spark wallet storage",
                    // which is both wrong and unactionable. Reconfiguring cannot clear it; only a restart could.
                    // A connect that has not returned in this long is not coming back on any useful timescale,
                    // so stop waiting and let the store have its wallet back.
                    if (!await SparkDeadline.OrTimeoutAsync(
                                connect,
                                AbandonedConnectGraceDeadline,
                                () => _logger.LogError(
                                    "Store {StoreId}: its abandoned Spark connect has still not finished after "
                                    + "{Minutes} minutes. Releasing the storage lock so the store can be "
                                    + "reconfigured without restarting the server. The connect cannot be "
                                    + "cancelled and is still running: if it ever completes, its wallet is shut "
                                    + "down below, and until then this store must not be started again",
                                    storeId, AbandonedConnectGraceDeadline.TotalMinutes),
                                CancellationToken.None)
                            .ConfigureAwait(false))
                    {
                        // Deliberately does not fall through to the finally's release-then-return: the release is
                        // the point, and the continuation below still disposes whatever arrives late.
                        _ = connect.ContinueWith(
                            t =>
                            {
                                if (t.IsCompletedSuccessfully)
                                    t.Result.Dispose();
                            },
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                        return;
                    }

                    late = await connect.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        "Store {StoreId}: the abandoned Spark connect eventually failed ({Reason}). Nothing was "
                        + "left running", storeId, SparkErrors.Describe(ex));
                    return;
                }

                try
                {
                    // Disconnect before Dispose, as everywhere else: Disconnect alone leaves the wallet serving
                    // the network and minting invoices for a store this plugin has already given up on.
                    await late.DisconnectAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Store {StoreId}: could not cleanly disconnect the abandoned Spark wallet", storeId);
                }

                late.Dispose();
                _logger.LogWarning(
                    "Store {StoreId}: the abandoned Spark connect finished late; its wallet has been shut down "
                    + "and the store is still not running", storeId);
            }
            finally
            {
                // Last, and unconditionally: until the late wallet is gone, its storage is still in use.
                storageLock.Dispose();
            }
        });
    }

    private async Task WarmUpAsync(SparkStoreInstance instance)
    {
        var storeId = instance.StoreId;
        var sdk = instance.Sdk;

        // First, and finished before the sync call below — but not "before the sync" in any stronger sense:
        // the SDK starts its own background sync at connect, so the import overlaps it, and the ensureSynced
        // read below waits for that first sync rather than starting one. Nothing here depends on an order
        // between the two. The import contacts no operator and merges into whatever the wallet holds at that
        // moment; a leaf the sync learns about afterwards is one the import did not need to supply.
        //
        // What the ordering does buy is the restore gate: the automatic pass and the page's Export both wait
        // for it, so neither can replace a stored backup this restore has not finished with.
        try
        {
            await RestoreExitStateAsync(instance).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // RestoreExitStateAsync handles its own failures; this is the backstop. The type only — whatever
            // threw may have been handed a backup.
            _logger.LogWarning(
                "Store {StoreId}: restoring its exit-state backup failed unexpectedly ({ExceptionType})",
                storeId, ex.GetType().Name);
        }
        finally
        {
            // Opened whatever happened: a gate left shut would stop this store's automatic backups for the
            // life of the process. What the restore could not finish with is protected separately — see
            // ExitStateRuntime.NeedsRescue.
            instance.ExitState.MarkRestored();
        }

        try
        {
            var info = await sdk.GetInfoAsync(ensureSynced: true).ConfigureAwait(false);
            _logger.LogInformation(
                "Store {StoreId}: Spark wallet synced, identity {IdentityPubkey}, balance {BalanceSats} sat",
                storeId, info.IdentityPubkey, info.BalanceSats);
        }
        catch (ObjectDisposedException)
        {
            // The store was reconfigured or removed while the first sync was in flight. Expected.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Store {StoreId}: the Spark wallet connected but its first sync failed ({Reason}). Invoice "
                + "creation may fail until this resolves",
                storeId, SparkErrors.Describe(ex));
        }
    }

    /// <summary>
    /// Brings a store's stored exit-state backups into the wallet that just started — when they can help.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what makes the backups mean anything.</b> Without it they are values the plugin writes down
    /// and never reads. Two sources, in this order:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>The import queue</b> — pasted backups and earlier failed imports. Always imported: nothing in the
    /// queue is known to be in the wallet, which is the whole reason it is queued. Each one leaves the queue
    /// only once an import of it has returned; a failure leaves it for the next connect.
    /// </description></item>
    /// <item><description>
    /// <b>The automatic backup</b> — imported only when it can help. It is this wallet's own earlier export,
    /// so while the wallet's storage is intact it holds nothing the wallet lacks except leaves that have since
    /// been <em>spent</em> — and the SDK documents that importing an out-of-date export makes exactly those
    /// spendable again until the next refresh against the operators. Importing it on every connect (which
    /// includes every settings save) did that on every connect. See <see cref="RestoreAutomaticLockedAsync"/>
    /// for when it is imported now.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>Every failure here is logged and swallowed.</b> A store whose backup will not import still has a
    /// working Lightning wallet, and taking the wallet down over a recovery aid would trade a rare loss of
    /// exit data for a certain loss of payments. A backup that fails is never lost for it: it stays (or goes)
    /// into the queue, which no automatic pass writes, and the next connect tries again.
    /// </para>
    /// <para>
    /// <b>No blob is ever logged, not even in the failure path.</b> The lines name the store, the outcome and
    /// the counts, and exceptions by type only: an SDK that echoed its argument back in a message would put a
    /// wallet's whole history in the log.
    /// </para>
    /// </remarks>
    private async Task RestoreExitStateAsync(SparkStoreInstance instance)
    {
        var identity = await ReadWalletIdentityAsync(instance).ConfigureAwait(false);

        // Held for the whole restore. Unbounded here, because this is the first taker of this instance's gate
        // and every import under it is bounded by ExitStateCallDeadline; a paste arriving meanwhile waits,
        // bounded, and finds the queue already drained.
        await instance.ExitState.ImportGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await ImportQueuedLockedAsync(instance, identity).ConfigureAwait(false);
            await RestoreAutomaticLockedAsync(instance, identity).ConfigureAwait(false);
        }
        finally
        {
            instance.ExitState.ImportGate.Release();
        }
    }

    /// <summary>
    /// Imports every queued backup, oldest first. Caller holds the instance's import gate.
    /// </summary>
    private async Task<ExitStateImportReport> ImportQueuedLockedAsync(SparkStoreInstance instance, string? identity)
    {
        var storeId = instance.StoreId;

        IReadOnlyList<PendingExitStateBackup> queued;
        try
        {
            queued = await _exitStateBackupStore.ListPendingAsync(storeId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Store {StoreId}: its queued exit-state backups could not be listed, so none was imported",
                storeId);
            return new ExitStateImportReport(ExitStateImportOutcome.Failed, Failed: 1,
                Reason: "The queued exit-state backups could not be read. Check the server log.");
        }

        if (queued.Count == 0)
            return ExitStateImportReport.NothingPending;

        int imported = 0, failed = 0;
        uint restored = 0, foreign = 0, conflicting = 0, chains = 0;
        string? reason = null;

        foreach (var entry in queued)
        {
            string? blob;
            try
            {
                blob = await _exitStateBackupStore.ReadPendingAsync(storeId, entry.Id, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Store {StoreId}: a queued exit-state backup could not be read; it stays queued", storeId);
                failed++;
                reason ??= "A queued exit-state backup could not be read. Check the server log.";
                continue;
            }

            // Gone since the listing (a clear), or a file with nothing in it — which no import can use and no
            // caller ever queues. The empty one is removed so it is not retried forever.
            if (string.IsNullOrWhiteSpace(blob))
            {
                if (blob is not null)
                    await TryRemoveFromQueueAsync(storeId, entry.Id).ConfigureAwait(false);
                continue;
            }

            var (counts, failure) = await ImportBoundedAsync(instance, blob).ConfigureAwait(false);
            if (counts is null)
            {
                failed++;
                reason ??= failure;
                _logger.LogWarning(
                    "Store {StoreId}: a queued exit-state backup ({Length} characters, queued {QueuedAt:u}) could "
                    + "not be imported. It stays queued, is never replaced by the automatic backup, and is "
                    + "retried at the next connect",
                    storeId, blob.Length, entry.StoredAt);
                continue;
            }

            imported++;
            restored += counts.ImportedLeaves;
            foreign += counts.SkippedForeignLeaves;
            conflicting += counts.SkippedConflictingLeaves;
            chains += counts.SkippedChains;
            LogImportCounts(storeId, "a queued exit-state backup", counts);

            await SettleImportedQueueEntryAsync(storeId, entry.Id, counts).ConfigureAwait(false);

            // What the wallet holds just changed, and the automatic backup does not have it yet.
            _exitStateBackupScheduler.RequestRefresh(storeId);
        }

        return new ExitStateImportReport(
            failed > 0 ? ExitStateImportOutcome.Failed : ExitStateImportOutcome.Imported,
            imported, failed, restored, foreign, conflicting, chains, reason);
    }

    /// <summary>
    /// Takes an imported backup off the queue: removed, or — when every leaf in it was another wallet's —
    /// kept aside, because it is some wallet's exit data and this one's import says nothing about whose.
    /// </summary>
    private async Task SettleImportedQueueEntryAsync(string storeId, string id, SparkExitStateImport counts)
    {
        try
        {
            if (counts.RestoredNothing && counts.SkippedForeignLeaves > 0)
            {
                var aside = await _exitStateBackupStore
                    .SetAsidePendingAsync(storeId, id, ExitStateBackupSetAside.Foreign, CancellationToken.None)
                    .ConfigureAwait(false);
                _logger.LogWarning(
                    "Store {StoreId}: every leaf in a queued exit-state backup belonged to another wallet, so "
                    + "nothing was restored. It was kept aside as {File} in the plugin's exit-state directory",
                    storeId, aside);
                return;
            }

            await _exitStateBackupStore.DeletePendingAsync(storeId, id, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Imported, so nothing is at risk: it stays queued and the next connect imports it again.
            _logger.LogWarning(ex,
                "Store {StoreId}: a queued exit-state backup was imported but could not be taken off the "
                + "queue; it will be imported again at the next connect", storeId);
        }
    }

    private async Task TryRemoveFromQueueAsync(string storeId, string id)
    {
        try
        {
            await _exitStateBackupStore.DeletePendingAsync(storeId, id, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Store {StoreId}: could not remove an empty queued exit-state backup", storeId);
        }
    }

    /// <summary>
    /// Imports the automatic backup when it can help, and makes sure a failed import cannot cost it.
    /// Caller holds the instance's import gate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>When it is imported.</b> When the wallet's own storage was empty at this connect — lost, reset, or a
    /// fresh server restored from a data-directory backup — because then the backup is the only copy of the
    /// exit data there is, which is the case it exists for. And when its stamp does not describe it — written
    /// by an earlier build, or placed by hand — because then nothing says the wallet holds it. It is
    /// <em>not</em> imported when it is this wallet's own last export and the storage is intact: that export
    /// is older than the storage, so the only thing it could add is leaves spent since, made spendable again
    /// until the next refresh.
    /// </para>
    /// <para>
    /// <b>When the import fails, the backup goes into the queue before anything else can happen.</b> The
    /// automatic pass waits for this method (the restore gate) and then writes the wallet's own export —
    /// which, for a wallet whose storage was empty, is an export of almost nothing. Without the queue that
    /// write replaced the one copy of the exit data the import had just refused, within a minute of every
    /// restart. When even the queue cannot be written, the instance is marked so that nothing replaces the
    /// file until a later attempt has moved it (<see cref="ExitStateRuntime.NeedsRescue"/>).
    /// </para>
    /// </remarks>
    private async Task RestoreAutomaticLockedAsync(SparkStoreInstance instance, string? identity)
    {
        var storeId = instance.StoreId;

        string? backup;
        try
        {
            backup = await _exitStateBackupStore.ReadAsync(storeId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Unknown content is not "nothing stored": until it can be read and queued, nothing replaces it.
            instance.ExitState.NeedsRescue = true;
            _logger.LogWarning(ex,
                "Store {StoreId}: its stored exit-state backup could not be read, so it was not imported on this "
                + "connect; it will not be replaced until it can be read", storeId);
            return;
        }

        if (string.IsNullOrWhiteSpace(backup))
        {
            await AdoptLegacyLockedAsync(instance, identity).ConfigureAwait(false);
            return;
        }

        ExitStateBackupStamp? stamp = null;
        try
        {
            stamp = await _exitStateBackupStore.ReadStampAsync(storeId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Store {StoreId}: could not read its exit-state backup's stamp", storeId);
        }

        if (stamp?.Describes(backup) == true && !instance.ExitState.StorageWasEmpty)
        {
            _logger.LogDebug(
                "Store {StoreId}: its stored exit-state backup is this wallet's own last export and the wallet's "
                + "storage is intact, so it was not imported again", storeId);
            return;
        }

        var (counts, _) = await ImportBoundedAsync(instance, backup).ConfigureAwait(false);
        if (counts is not null)
        {
            LogImportCounts(storeId, "its stored exit-state backup", counts);

            try
            {
                if (counts.RestoredNothing && counts.SkippedForeignLeaves > 0)
                {
                    // Another wallet's, by the SDK's own account: a store re-provisioned onto a new seed by a
                    // build that did not stamp its files. Kept, under a name that says so, rather than left
                    // for this wallet's first pass to overwrite.
                    var aside = await _exitStateBackupStore
                        .SetAsideAsync(storeId, ExitStateBackupSetAside.Foreign, CancellationToken.None)
                        .ConfigureAwait(false);
                    _logger.LogWarning(
                        "Store {StoreId}: every leaf in its stored exit-state backup belonged to another wallet, "
                        + "so it was kept aside as {File} in the plugin's exit-state directory", storeId, aside);
                }
                else
                {
                    // The wallet now holds it, so the next connect need not import it again.
                    await _exitStateBackupStore.StampAsync(storeId, backup, identity, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "Store {StoreId}: could not record the import of its exit-state backup; the next connect "
                    + "imports it again", storeId);
            }

            _exitStateBackupScheduler.RequestRefresh(storeId);
            return;
        }

        try
        {
            await _exitStateBackupStore.AddPendingAsync(storeId, backup, CancellationToken.None)
                .ConfigureAwait(false);
            _logger.LogWarning(
                "Store {StoreId}: its stored exit-state backup could not be imported. The wallet is running; a "
                + "copy was queued ({Length} characters), is never replaced by the automatic backup, and is "
                + "retried at every connect until it imports", storeId, backup.Length);
        }
        catch (Exception ex)
        {
            instance.ExitState.NeedsRescue = true;
            _logger.LogError(ex,
                "Store {StoreId}: its stored exit-state backup could not be imported, and could not be queued "
                + "for another attempt either. It will not be replaced by an automatic backup until it has been "
                + "queued", storeId);
        }
    }

    /// <summary>
    /// Adopts a backup an earlier plugin version left in the store's settings, when the file holds none.
    /// Caller holds the instance's import gate.
    /// </summary>
    /// <remarks>
    /// A store upgrading from a version that kept the backup in its settings still has its only copy there,
    /// and losing a backup on an upgrade is losing the exit data of every leaf the old version had learned
    /// about. Imported from the old location first; the move is committed only once the import has
    /// succeeded, so a blob this SDK refuses stays where it is — and is queued as well, because once the
    /// automatic pass has written a file of its own, adoption is never consulted again.
    /// </remarks>
    private async Task AdoptLegacyLockedAsync(SparkStoreInstance instance, string? identity)
    {
        var storeId = instance.StoreId;

        string? legacy;
        try
        {
            var settings = await Get(storeId).ConfigureAwait(false);
            legacy = settings?.UnilateralExit?.ExitStateBackup;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Store {StoreId}: its settings could not be read, so a stored exit-state backup was not "
                + "imported on this connect", storeId);
            return;
        }

        if (string.IsNullOrWhiteSpace(legacy))
            return;

        var (counts, _) = await ImportBoundedAsync(instance, legacy).ConfigureAwait(false);
        if (counts is null)
        {
            try
            {
                await _exitStateBackupStore.AddPendingAsync(storeId, legacy, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Store {StoreId}: the exit-state backup at its old settings location could not be queued "
                    + "for another attempt; it stays at that location", storeId);
            }

            _logger.LogWarning(
                "Store {StoreId}: the exit-state backup at its old settings location could not be imported. It "
                + "stays there and was queued for another attempt at the next connect", storeId);
            return;
        }

        LogImportCounts(storeId, "the exit-state backup at its old settings location", counts);

        if (counts.RestoredNothing && counts.SkippedForeignLeaves > 0)
        {
            // Another wallet's — a seed change the settings carried it across. Not this wallet's backup, so
            // not written as one: kept aside, then cleared from the settings like any adopted blob.
            try
            {
                var aside = await _exitStateBackupStore
                    .KeepAsideAsync(storeId, legacy, ExitStateBackupSetAside.Foreign, CancellationToken.None)
                    .ConfigureAwait(false);
                await ClearExitStateBackupSlot(storeId).ConfigureAwait(false);
                _logger.LogWarning(
                    "Store {StoreId}: every leaf in the exit-state backup at its old settings location belonged to "
                    + "another wallet, so it was kept aside as {File} and cleared from the settings", storeId, aside);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Store {StoreId}: another wallet's exit-state backup at its old settings location could not be "
                    + "kept aside; it stays at that location", storeId);
            }

            return;
        }

        // After the import, never before: the old location is the only copy until the wallet has
        // demonstrably taken the blob back, and an adoption that cleared it on a failed import would
        // trade the backup for nothing.
        await AdoptLegacyBackupAsync(storeId, legacy, identity).ConfigureAwait(false);
        _exitStateBackupScheduler.RequestRefresh(storeId);
    }

    /// <summary>
    /// Imports one blob, bounded by <see cref="ExitStateCallDeadline"/>. Never throws.
    /// </summary>
    /// <returns>
    /// The counts when the import returned, or null and a merchant-facing reason that carries nothing of the
    /// blob. A timed-out import is still running in the SDK and may yet land; the caller treats it as failed,
    /// which at worst imports the same blob again later — harmless.
    /// </returns>
    private async Task<(SparkExitStateImport? Counts, string? Reason)> ImportBoundedAsync(
        SparkStoreInstance instance, string blob)
    {
        try
        {
            var counts = await SparkDeadline.OrNullAsync(
                    instance.Sdk.ImportUnilateralExitStateAsync(blob),
                    ExitStateCallDeadline,
                    () => _logger.LogWarning(
                        "Store {StoreId}: importing an exit-state backup exceeded {Seconds}s and was abandoned; "
                        + "the call cannot be cancelled and may still finish",
                        instance.StoreId, ExitStateCallDeadline.TotalSeconds),
                    CancellationToken.None)
                .ConfigureAwait(false);

            return counts is null
                ? (null, "Spark did not finish importing it in time.")
                : (counts, null);
        }
        catch (Exception ex)
        {
            // The type only, and a sentence of this plugin's own for the page: this call was handed the blob,
            // so nothing it says is repeated anywhere.
            _logger.LogWarning(
                "Store {StoreId}: Spark refused an exit-state backup import ({ExceptionType})",
                instance.StoreId, ex.GetType().Name);
            return (null, SparkErrors.IsInvalidInput(ex)
                ? "Spark rejected it as not an exit-state backup it can read for this network."
                : ex is ObjectDisposedException
                    ? "The wallet stopped while it was being imported."
                    : $"Spark could not import it ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// The operator's line for one import that returned.
    /// </summary>
    /// <remarks>
    /// Logged at information even when nothing was restored, because "the backup did not cover this wallet"
    /// is a fact the operator needs and cannot see anywhere else. The conflicting count is called out
    /// separately because it is the one that means data was refused rather than merely unnecessary.
    /// </remarks>
    private void LogImportCounts(string storeId, string what, SparkExitStateImport counts)
    {
        _logger.LogInformation(
            "Store {StoreId}: imported {What}: {Imported} leaves restored, {Foreign} foreign, {Conflicting} "
            + "conflicting, {Chains} already held",
            storeId, what, counts.ImportedLeaves, counts.SkippedForeignLeaves,
            counts.SkippedConflictingLeaves, counts.SkippedChains);

        if (counts.RestoredNothing && counts.SkippedConflictingLeaves > 0)
        {
            _logger.LogWarning(
                "Store {StoreId}: every leaf in {What} was refused as conflicting, so no exit data was restored. "
                + "The backup disagrees with exit data this wallet already holds", storeId, what);
        }
    }

    /// <summary>
    /// The wallet's identity public key, read once per instance; null when it cannot be read.
    /// </summary>
    /// <remarks>
    /// What stamps an automatic backup with the wallet that wrote it. A cached, unsynced read — the identity
    /// is fixed at connect — bounded like every other SDK call on a background path. Public information: the
    /// warm-up logs it on every start.
    /// </remarks>
    private async Task<string?> ReadWalletIdentityAsync(SparkStoreInstance instance)
    {
        if (instance.ExitState.Identity is { } known)
            return known;

        try
        {
            var info = await SparkDeadline.OrNullAsync(
                    instance.Sdk.GetInfoAsync(ensureSynced: false),
                    Constants.SdkCallDeadline,
                    () => { },
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (info?.IdentityPubkey is { Length: > 0 } identity)
                instance.ExitState.Identity = identity;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Store {StoreId}: could not read its wallet identity", instance.StoreId);
        }

        return instance.ExitState.Identity;
    }

    /// <summary>
    /// Whether a store's SDK storage directory holds nothing of the SDK's yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read before the connect, which creates the SDK's database: an empty directory then means the wallet
    /// starts with no local state at all — a first run, a deleted or lost directory, or a server restored
    /// from a backup that did not carry it — and that is the one situation in which the stored automatic
    /// backup holds exit data the wallet does not.
    /// </para>
    /// <para>
    /// "Nothing of the SDK's" is any file but the plugin's own lock, anywhere under the directory, rather
    /// than the SDK's database by name: the name and nesting are the SDK's internals and have moved before.
    /// An unreadable directory answers true — the error is toward importing, whose cost is bounded, and away
    /// from skipping a restore the wallet needed.
    /// </para>
    /// </remarks>
    internal static bool SdkStorageIsEmpty(string workDir)
    {
        try
        {
            if (!Directory.Exists(workDir))
                return true;

            return !Directory.EnumerateFiles(workDir, "*", SearchOption.AllDirectories)
                .Any(file => !string.Equals(Path.GetFileName(file), SparkStorageLock.FileName, StringComparison.Ordinal));
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// Completes once a store's current instance has finished its connect-time exit-state restore (or at
    /// once, when it has no instance). For tests, which otherwise have no way to know the fire-and-forget
    /// warm-up got that far.
    /// </summary>
    internal Task WhenExitStateRestoredAsync(string storeId) =>
        _instances.TryGetValue(storeId, out var instance) ? instance.ExitState.RestoredTask : Task.CompletedTask;

    /// <summary>
    /// Moves a store's exit-state backups aside when Flint is removed from it, so they are neither left under
    /// their live names nor deleted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not deleted</b>, for the reason the SDK's own storage directory is kept: removing Flint from a
    /// store is not evidence that the wallet's funds have been swept, and while the seed may still control
    /// them these files are its only device-proof exit data. <b>Not left under the live names</b>, because
    /// a later re-provision of the same store would then treat the old wallet's backup as its own — import
    /// it, and let its first pass overwrite it.
    /// </para>
    /// <para>
    /// Best effort and never throws: this runs after the teardown, on the removal path, and a filesystem
    /// fault here must not undo a removal the operator asked for. The log names the files, never content.
    /// </para>
    /// </remarks>
    private async Task SetAsideExitStateBackupsAsync(string storeId)
    {
        try
        {
            var kept = new List<string>();
            if (await _exitStateBackupStore
                    .SetAsideAsync(storeId, ExitStateBackupSetAside.Removed, CancellationToken.None)
                    .ConfigureAwait(false) is { } aside)
            {
                kept.Add(aside);
            }

            foreach (var entry in await _exitStateBackupStore
                         .ListPendingAsync(storeId, CancellationToken.None).ConfigureAwait(false))
            {
                if (await _exitStateBackupStore
                        .SetAsidePendingAsync(storeId, entry.Id, ExitStateBackupSetAside.Removed, CancellationToken.None)
                        .ConfigureAwait(false) is { } queued)
                {
                    kept.Add(queued);
                }
            }

            if (kept.Count > 0)
            {
                _logger.LogInformation(
                    "Store {StoreId}: Flint was removed, and its exit-state backups were kept as {Files} in the "
                    + "plugin's exit-state directory. They are that wallet's exit data; delete them only once "
                    + "its funds are safely elsewhere",
                    storeId, string.Join(", ", kept));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Store {StoreId}: Flint was removed, but its exit-state backups could not be moved aside; they "
                + "were left where they were", storeId);
        }
    }

    /// <summary>
    /// The settings to persist, minus a deprecated exit-state backup the stored configuration no longer has.
    /// Caller holds <see cref="_instanceLock"/>.
    /// </summary>
    /// <remarks>
    /// Nothing writes the deprecated slot any more; it is only ever cleared. A whole-settings write carrying
    /// a value the cache no longer holds is therefore a stale copy — read before an adoption or an operator's
    /// clear emptied the slot — and persisting it would bring a cleared backup back for the next connect to
    /// adopt, under a page that says none is stored.
    /// </remarks>
    private SparkSettings? WithoutReinstatedLegacyBackup(string storeId, SparkSettings? settings)
    {
        if (settings?.UnilateralExit is not { ExitStateBackup.Length: > 0 })
            return settings;

        var cached = _settings.GetValueOrDefault(storeId)?.UnilateralExit?.ExitStateBackup;
        if (!string.IsNullOrEmpty(cached))
            return settings;

        var copy = settings.Clone();
        copy.UnilateralExit!.ExitStateBackup = null;
        return copy;
    }

    /// <summary>
    /// Commits the adoption of a backup found at the old settings location: writes it to the file store,
    /// then clears the setting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only ever called after a successful import — the caller owns that ordering, because the setting is
    /// the only copy until the wallet has provably taken the blob back. Written as the automatic backup and
    /// stamped with this wallet's identity, because the wallet now holds it.
    /// </para>
    /// <para>
    /// The two steps fail differently and neither is worth an exception: while the write fails, the old
    /// location still holds the backup and the next connect adopts again; once the write has landed, the
    /// copy that counts is stored, and a setting that still names the value is inert — adoption is only
    /// ever consulted when the file is absent — so a failed clear costs one retry at most.
    /// </para>
    /// <para>
    /// The clearing itself is <see cref="ClearExitStateBackupSlot"/>'s, shared with the page's own clear of
    /// the backup — including why the row is rewritten through the repository rather than
    /// <see cref="Set"/>. Logs the length and nothing else, as everywhere this blob is handled.
    /// </para>
    /// </remarks>
    private async Task AdoptLegacyBackupAsync(string storeId, string backup, string? identity)
    {
        try
        {
            await _exitStateBackupStore.WriteAsync(storeId, backup, identity, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Store {StoreId}: its exit-state backup imported but could not be written to the plugin's "
                + "own file ({ExceptionType}); it stays at its old location and a later connect will "
                + "attempt the move again",
                storeId, ex.GetType().Name);
            return;
        }

        try
        {
            await ClearExitStateBackupSlot(storeId).ConfigureAwait(false);

            _logger.LogInformation(
                "Store {StoreId}: adopted an exit-state backup left by an earlier version of this plugin "
                + "into its own file and cleared the old setting ({Length} characters)",
                storeId, backup.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Store {StoreId}: its exit-state backup was adopted into the plugin's own file, but the "
                + "old setting could not be cleared; the stored file is the copy that counts",
                storeId);
        }
    }

    /// <summary>
    /// Clears a store's deprecated <see cref="UnilateralExitSettings.ExitStateBackup"/> slot — the
    /// persisted row and the cached instance alike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both, or the clear did not happen.</b> The cached instance is what every reader gets:
    /// <see cref="Get"/> hands back a clone of it, and both whole-settings writers rebuild from that clone
    /// — <c>SparkUnilateralExitService.SaveExitSettingsAsync</c> clones the settings and the exit section
    /// before storing, and <c>SparkStoreProvisioner</c> carries the previous exit settings across a
    /// re-provision. A row cleared while the cache kept the blob is therefore a row the next disclosure
    /// acknowledgement fills back in, and adoption is never consulted again once the file exists.
    /// </para>
    /// <para>
    /// <b>The row is read fresh rather than taken from the cache, and written through the repository rather
    /// than through <see cref="Set"/>.</b> <c>Set</c> reconciles the running instance, which would tear
    /// down and reconnect a wallet on a path where no operator asked for anything — the connect that is
    /// adopting right now, or a merchant's browser. A concurrent reconfiguration racing this write
    /// rewrites the slot from a fresh read rather than a cached one for the same reason: the cached blob
    /// may predate a change this method has no business reverting.
    /// </para>
    /// <para>
    /// <b>The cache is cleared in place, not dropped.</b> A store missing from the cache reads as "no
    /// Spark configuration" to every reader — <see cref="Get"/>, <see cref="Resolve"/>,
    /// <see cref="HasAnyStoreProvisioned"/> — so invalidating the entry would trade a stale field for a
    /// lie about the store existing at all.
    /// </para>
    /// <para>
    /// Row first, cache second, the way <see cref="Set"/> orders the same two steps: a process that died
    /// between them converges on the stored row at the next startup. Nothing names the value; it is the
    /// wallet's whole exit state.
    /// </para>
    /// </remarks>
    private async Task ClearExitStateBackupSlot(string storeId)
    {
        // Under the instance lock, the one Set persists under: this is a read-modify-write of the whole row,
        // and without the lock a Set landing between the read and the write was reverted by it — the store's
        // new configuration silently replaced by the one this method read a moment earlier. Never called
        // with the lock held (the connect's adoption runs on the fire-and-forget warm-up), so waiting here
        // cannot deadlock; it only queues behind a reconfiguration in flight.
        await _instanceLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var stored = await _storeRepository
                .GetSettingAsync<SparkSettings>(storeId, Constants.StoreSettingsKey)
                .ConfigureAwait(false);

            if (stored?.UnilateralExit is { } exit && !string.IsNullOrEmpty(exit.ExitStateBackup))
            {
                exit.ExitStateBackup = null;
                await _storeRepository
                    .UpdateSetting(storeId, Constants.StoreSettingsKey, stored)
                    .ConfigureAwait(false);
            }

            // Unconditionally, not only when the row moved: an empty row and a populated cache is a state
            // this method has been called to end regardless of which half is holding it.
            if (_settings.TryGetValue(storeId, out var cached) && cached.UnilateralExit is { } cachedExit)
                cachedExit.ExitStateBackup = null;
        }
        finally
        {
            _instanceLock.Release();
        }
    }

    /// <summary>
    /// Shuts one store's instance down. Caller must hold <see cref="_instanceLock"/>. Idempotent.
    /// </summary>
    private async Task TeardownInstanceAsync(string storeId)
    {
        // Removed from the dictionary first: any request that resolves the client after this point gets
        // "not configured" rather than an ObjectDisposedException on a checkout page.
        if (!_instances.TryRemove(storeId, out var instance))
            return;

        foreach (var (walletKey, owner) in _walletOwners)
        {
            if (owner == storeId)
                _walletOwners.TryRemove(walletKey, out _);
        }

        await instance.ShutdownAsync(_logger).ConfigureAwait(false);
        _logger.LogInformation("Store {StoreId}: Spark wallet shut down", storeId);
    }

    /// <summary>
    /// A stable fingerprint of the wallet a seed addresses, used only as a dictionary key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A hash of the seed material rather than the SDK's identity pubkey, because the pubkey needs a connected
    /// instance and the whole point is to decide <em>before</em> connecting. It is an equality proxy, which is
    /// all that is required: the same seed, passphrase and network are the same wallet. Never logged or
    /// persisted.
    /// </para>
    /// <para>
    /// The mnemonic is canonicalised through NBitcoin first, so cosmetic differences cannot defeat the guard.
    /// Hashing the raw string would let the same seed pasted with a double space, a tab, or different casing
    /// produce a different key — and then two live instances on one wallet, which is precisely the corruption
    /// this exists to prevent. Falls back to collapsing whitespace for a mnemonic NBitcoin cannot parse: such a
    /// seed will fail to connect anyway, but the guard should stay self-consistent rather than throw here.
    /// </para>
    /// </remarks>
    internal static string DeriveWalletKey(string mnemonic, string? passphrase, Breez.Sdk.Spark.Network network)
    {
        ArgumentNullException.ThrowIfNull(mnemonic);

        var material = $"{network}:{passphrase ?? string.Empty}:{CanonicaliseMnemonic(mnemonic)}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    /// <summary>
    /// A short name for the wallet behind <paramref name="walletKey"/> that can be written where others read it.
    /// </summary>
    /// <remarks>
    /// The wallet key is a bare hash of the recovery phrase, which has no business in an invoice's payment details:
    /// those are served over Greenfield. This is a domain-separated hash of it, cut to 128 bits — enough to tell a
    /// store's wallets apart, and nothing to start a search for the phrase from.
    /// </remarks>
    internal static string DeriveWalletTag(string walletKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(walletKey);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("flint/wallet-tag/v1:" + walletKey));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// The spelling of a mnemonic that <see cref="DeriveWalletKey"/> hashes.
    /// </summary>
    /// <remarks>
    /// Extracted from <see cref="DeriveWalletKey"/> so the invariant it shares with
    /// <c>SparkStoreProvisioner.TryNormalizeMnemonic</c> is directly checkable: a phrase stored by the setup
    /// flow must already be a fixed point of this function. Comparing two wallet keys cannot check that,
    /// because this canonicalises whatever it is given and would agree even if the setup flow normalised
    /// nothing at all.
    /// </remarks>
    internal static string CanonicaliseMnemonic(string mnemonic)
    {
        ArgumentNullException.ThrowIfNull(mnemonic);
        try
        {
            return string.Join(' ', new Mnemonic(mnemonic.Trim()).Words).ToLowerInvariant();
        }
        catch (Exception)
        {
            // Swallowed deliberately and without logging: the argument is seed material, so anything this threw
            // is not safe to record. An unparseable mnemonic is reported by the connect attempt instead.
            return string.Join(' ',
                mnemonic.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        }
    }

    #endregion

    #region Event consumption

    private async Task HandleEventAsync(SparkEventEnvelope envelope, SparkStoreInstance instance)
    {
        switch (envelope.Kind)
        {
            // Pending is handled exactly like succeeded, deliberately. The event stream is not reliable in
            // either direction: PaymentSucceeded has been seen firing twice for one payment on two threads
            // 57 ms apart, and a completed receive has been seen emitting only PaymentPending and never
            // PaymentSucceeded at all. Both paths therefore re-read the authoritative status and settle
            // through a compare-and-set, so a duplicate is harmless and a missing event is recoverable.
            case SparkEventKind.PaymentSucceeded when envelope.Payment is not null:
            case SparkEventKind.PaymentPending when envelope.Payment is not null:
            // A metadata update is a completion as far as a cross-chain receive is concerned: the conversion
            // details that tie the transfer to a quote arrive only with it. For anything else it is one more
            // harmless re-check through the same compare-and-set.
            case SparkEventKind.PaymentMetadataUpdated when envelope.Payment is not null:
                await HandleReceiveEventAsync(instance, envelope.Payment).ConfigureAwait(false);
                return;

            case SparkEventKind.PaymentFailed:
                // A failed inbound HTLC leaves the invoice payable, so there is nothing to record.
                _logger.LogDebug("Store {StoreId}: Spark payment failed", envelope.StoreId);
                return;

            case SparkEventKind.NewDeposits:
                // On-chain money detected before its claim. Not a leaf yet, so today's exit state does not
                // cover it — but the claim usually lands well inside the debounce, and a refresh requested
                // here means the pass that runs after it exports the leaf even if the claim event itself is
                // one of the drops the event channel is documented as producing.
                _logger.LogDebug("Store {StoreId}: Spark detected a new on-chain deposit", envelope.StoreId);
                _exitStateBackupScheduler.RequestRefresh(envelope.StoreId);
                return;

            case SparkEventKind.ClaimedDeposits:
                // Real money arriving on-chain, and the SDK claims it automatically. Worth an operator-level
                // line; the individual amounts show up as Deposit payments on the same event stream.
                _logger.LogInformation("Store {StoreId}: Spark claimed an on-chain deposit", envelope.StoreId);

                // A claimed deposit changes the wallet's leaf set, so it is the strongest signal that a fresh
                // exit-state backup is worth having. Never take one here — this runs on the store's event
                // consumer and an export is a live multi-megabyte SDK call; the scheduler coalesces the burst
                // and the scheduled pass pays it.
                _exitStateBackupScheduler.RequestRefresh(envelope.StoreId);
                return;

            case SparkEventKind.UnilateralExitStateChanged:
                // The SDK's own statement that an exit-state export taken before now is out of date: a leaf's
                // exit data was completed or rebuilt. It is the one trigger that covers every way the leaf set
                // moves — sends, swaps, renewals and the SDK's background optimisation, none of which is a
                // receive — and the only one that fires when a leaf that was exported *without* its data
                // becomes exportable with it. Requested, never taken here, for the reason the deposit cases
                // above give: this is the store's event loop and an export is a multi-megabyte SDK call; the
                // scheduler's debounce coalesces the bursts an optimisation round produces.
                _logger.LogDebug(
                    "Store {StoreId}: Spark reported its unilateral-exit data changed; a backup refresh was "
                    + "requested", envelope.StoreId);
                _exitStateBackupScheduler.RequestRefresh(envelope.StoreId);
                return;

            default:
                _logger.LogTrace("Store {StoreId}: Spark event {Kind}", envelope.StoreId, envelope.Kind);
                return;
        }
    }

    private async Task HandleReceiveEventAsync(SparkStoreInstance instance, Breez.Sdk.Spark.Payment sdkPayment)
    {
        var storeId = instance.StoreId;
        var cancellationToken = instance.ConsumerToken;
        var payment = SparkPaymentMapper.Map(sdkPayment, _bolt11Parser);

        if (payment.Direction is not SparkPaymentDirection.Receive)
        {
            // One of our own outgoing payments (a payout or a sweep). The send paths track their own results
            // through the idempotency key; there is no BTCPay invoice to settle here.
            //
            // Filtering on direction is not cosmetic: a self-payment produces two Payment rows for one payment
            // hash, and the send leg carries a fee the receive leg does not.
            _logger.LogDebug(
                "Store {StoreId}: Spark send {SdkPaymentId} is {Status} ({AmountSats} sat, {FeeSats} sat fee)",
                storeId, payment.SdkPaymentId, payment.Status, payment.AmountSats, payment.FeeSats);
            return;
        }

        // Any inbound payment moves exit data, so a backup refresh is warranted whether it arrives as a
        // Lightning receive or an on-chain deposit. One call here, past the direction filter, rather than
        // threaded into each branch: it fires before the Deposit branch below returns, so both are covered.
        _exitStateBackupScheduler.RequestRefresh(storeId);

        if (payment.Method is SparkPaymentMethod.Deposit)
        {
            // An auto-claimed on-chain static deposit. It has no payment hash by nature and settles no
            // invoice; the claim fee is already netted out of the amount.
            _logger.LogInformation(
                "Store {StoreId}: Spark credited {AmountSats} sat from an on-chain deposit "
                + "({FeeSats} sat claim fee, {Status})",
                storeId, payment.AmountSats, payment.FeeSats, payment.Status);
            return;
        }

        if (payment.PaymentHash is null
            && payment.Method is SparkPaymentMethod.Spark or SparkPaymentMethod.Token
            && await TryHandleStablecoinReceiveAsync(instance, payment, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (payment.PaymentHash is not { } paymentHash)
        {
            // A direct Spark transfer with no HTLC and no invoice: real money that cannot be attributed to a
            // BTCPay invoice. It sits in the balance and will be swept; it cannot settle anything.
            _logger.LogWarning(
                "Store {StoreId}: received {AmountSats} sat on Spark with no payment hash "
                + "(SDK payment {SdkPaymentId}); it cannot be matched to a BTCPay invoice",
                storeId, payment.AmountSats, payment.SdkPaymentId);
            return;
        }

        // Re-read rather than trusting the event's own status; it is the only authoritative answer.
        payment = await ConfirmStatusAsync(instance, payment, cancellationToken).ConfigureAwait(false);

        if (payment.Status is not SparkPaymentStatus.Completed)
        {
            // Still in flight. Record the SDK's id against the invoice so the reconciliation task can resolve
            // it with a point lookup instead of a history scan if the completion event never arrives.
            if (await _invoiceStore
                    .TryRecordSdkPaymentIdAsync(storeId, paymentHash, payment.SdkPaymentId, cancellationToken)
                    .ConfigureAwait(false))
            {
                _logger.LogDebug(
                    "Store {StoreId}: invoice {PaymentHash} has an inbound payment in flight ({SdkPaymentId})",
                    storeId, paymentHash, payment.SdkPaymentId);
            }

            return;
        }

        await _reconciler.ApplyAsync(storeId, payment, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Routes a USDC/USDT receive — a transfer from the bridge provider, with no payment hash — to the stablecoin
    /// path. False when it is not one, so the caller reports it as the unattributable transfer it then is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The provider's conversion details, which are what tie the transfer to a quote, can arrive after the
    /// transfer itself: the SDK first reports a plain Spark transfer and attaches the details once the provider
    /// confirms the order, announcing them with <c>PaymentMetadataUpdated</c>. So the stored row is re-read before
    /// deciding, and a detail-less transfer that arrives while the store has open quotes is left for that event —
    /// or the stablecoin reconciliation pass — rather than warned about as money nothing can attribute.
    /// </para>
    /// <para>
    /// A detail-less transfer on a store with no open quote falls through to the existing warning: nothing this
    /// plugin quoted can explain it.
    /// </para>
    /// </remarks>
    private async Task<bool> TryHandleStablecoinReceiveAsync(
        SparkStoreInstance instance,
        SparkPayment payment,
        CancellationToken cancellationToken)
    {
        var stablecoins = _stablecoinsFactory();
        if (!stablecoins.Available)
            return false;

        if (payment.Conversion is null || payment.Status is not SparkPaymentStatus.Completed)
            payment = await ConfirmStatusAsync(instance, payment, cancellationToken).ConfigureAwait(false);

        if (payment.Conversion is { Provider: SparkCrossChainProvider.Orchestra })
        {
            await stablecoins.TryCreditAsync(instance.StoreId, payment, cancellationToken).ConfigureAwait(false);
            return true;
        }

        // Some other conversion — a Stable Balance swap leg, say — is not a USDC/USDT receive waiting for its
        // details, whatever quotes are open.
        if (payment.Conversion is not null
            || !await stablecoins.HasOpenQuotesAsync(instance.StoreId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // Details normally follow within minutes. A transfer still bare an hour on is not waiting for them — the
        // case that shows up is a second deposit to a quote that already settled, which the provider delivers
        // after the SDK closed that quote's row, so no details ever come. Falling through hands it to the
        // unattributable-transfer warning instead of leaving it at Debug for as long as any quote is open.
        if (_timeProvider.GetUtcNow() - payment.Timestamp > StablecoinDetailsGrace)
            return false;

        _logger.LogDebug(
            "Store {StoreId}: Spark payment {SdkPaymentId} has no payment hash and no conversion details yet while "
            + "USDC/USDT quotes are open; waiting for the provider's details to attribute it",
            instance.StoreId, payment.SdkPaymentId);
        return true;
    }

    /// <summary>How long a bare transfer may wait for its conversion details before it is reported instead.</summary>
    internal static readonly TimeSpan StablecoinDetailsGrace = TimeSpan.FromHours(1);

    /// <summary>
    /// Re-reads a payment's status from the SDK, falling back to the event's own payload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fallback matters: if the SDK cannot be reached, or has not yet written the row the event refers to,
    /// taking the event at face value is better than dropping a settlement. A wrongly optimistic status is
    /// corrected by the compare-and-set in the store; a dropped one is only corrected by the reconciliation
    /// task, minutes later.
    /// </para>
    /// <para>
    /// Bounded by a deadline because no SDK call can be cancelled. A hung service-provider call would
    /// otherwise stall this store's whole event queue behind it.
    /// </para>
    /// </remarks>
    private async Task<SparkPayment> ConfirmStatusAsync(
        SparkStoreInstance instance,
        SparkPayment payment,
        CancellationToken cancellationToken)
    {
        try
        {
            var deadline = ConfirmStatusDeadline;
            var confirmed = await SparkDeadline.OrNullAsync(
                    instance.Sdk.GetPaymentAsync(payment.SdkPaymentId, cancellationToken),
                    deadline,
                    // The call keeps running — there is no way to cancel it — but this queue moves on.
                    () => _logger.LogWarning(
                        "Store {StoreId}: re-reading Spark payment {SdkPaymentId} exceeded {Seconds}s; using the "
                        + "event payload",
                        instance.StoreId, payment.SdkPaymentId, deadline.TotalSeconds),
                    cancellationToken)
                .ConfigureAwait(false);

            if (confirmed is null)
                return payment;

            if (confirmed.Status != payment.Status)
            {
                _logger.LogDebug(
                    "Store {StoreId}: Spark payment {SdkPaymentId} was reported as {EventStatus} but reads as "
                    + "{ActualStatus}",
                    instance.StoreId, payment.SdkPaymentId, payment.Status, confirmed.Status);
            }

            return confirmed;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Store {StoreId}: could not re-read Spark payment {SdkPaymentId}; using the event payload",
                instance.StoreId, payment.SdkPaymentId);
            return payment;
        }
    }

    #endregion

    #region Public surface

    /// <summary>
    /// Re-checks every running store's unpaid invoices against the Spark service. Returns the number settled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Driven by <see cref="SparkReconciliationTask"/> on a timer and once from <see cref="StartAsync"/>. See
    /// that class for why this is the plugin's settlement guarantee rather than a fallback.
    /// </para>
    /// <para>
    /// The running instances are the stores that can be <em>settled</em>, and they are all this can supply. They
    /// are not the whole set of stores the pass covers: crediting an already-recorded settlement onto its BTCPay
    /// invoice needs no wallet connection, so the reconciler widens this list with the stores its own record
    /// store says are awaiting a credit — otherwise a store whose Spark connection is broken would never retry
    /// money it had already received. That widening lives in the reconciler because the record store is what
    /// answers the question.
    /// </para>
    /// </remarks>
    public async Task<int> ReconcileAllStoresAsync(CancellationToken cancellationToken = default)
    {
        await _startupGate.Task.ConfigureAwait(false);

        // Snapshotted before the walk: a store reconfigured mid-pass would otherwise mutate the collection being
        // enumerated. The reconciler handles a handle that has since been disposed.
        var targets = _instances.Values
            .Select(instance => new SparkReconciliationTarget(instance.StoreId, instance.Sdk))
            .ToList();

        return await _reconciler
            .ReconcileStoresAsync(targets, _reconciliationPass, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Takes the automatic exit-state backup of every running store that is due, per
    /// <see cref="ExitStateBackupScheduler"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Driven solely by <see cref="ExitStateBackupTask"/>. Only running instances are enumerated, which is
    /// the skip-the-dead-wallet rule for free: an export is a live SDK call needing a connected wallet,
    /// and an instance being in <c>_instances</c> is the plugin's own definition of having one.
    /// </para>
    /// <para>
    /// <b>A store is skipped until its connect-time restore has finished.</b> The scheduler starts empty, so
    /// after a restart every store is due at once — and the restore is fire-and-forget. A pass that ran first
    /// replaced the stored backup with the wallet's own export before the import had read it; for a wallet
    /// whose storage was lost, that export is of almost nothing, and the one copy of its exit data was gone
    /// within a minute of the restart. Waiting costs a minute at most: the next pass finds it due still.
    /// </para>
    /// <para>
    /// <b>This method must never throw into the task loop.</b> Every per-store failure is caught, logged
    /// without the blob, and left for a later pass: the pass is the retry, and an exception that escaped
    /// would end the walk over the stores behind it. Cancellation is the one rethrow — the host is going
    /// down. The export itself is bounded by <see cref="ExitStateCallDeadline"/>: BTCPay runs this on one
    /// of a few shared periodic loops, and one hung wallet must not hold a loop the rate refresh and the
    /// settlement reconciliation also run on. A store whose abandoned export is still running is skipped
    /// rather than asked again, so a stuck SDK accumulates one call, not one a minute.
    /// </para>
    /// <para>
    /// <b>The blob never reaches the log on any path here</b> — lengths only, and exception types rather
    /// than exceptions.
    /// </para>
    /// </remarks>
    public async Task TakeDueExitStateBackupsAsync(CancellationToken cancellationToken)
    {
        await _startupGate.Task.ConfigureAwait(false);

        // One clock reading for the whole walk, so two stores cannot disagree about the same pass.
        var now = _timeProvider.GetUtcNow();

        // Snapshotted like the reconciliation walk: a store reconfigured mid-pass mutates the dictionary
        // being enumerated, and a store that goes away under an in-flight export loses only this pass.
        foreach (var instance in _instances.Values.ToList())
        {
            var storeId = instance.StoreId;
            try
            {
                if (!_exitStateBackupScheduler.ShouldTake(storeId, now))
                    continue;

                if (!instance.ExitState.Restored)
                {
                    _logger.LogDebug(
                        "Store {StoreId}: its exit-state backup was not taken because the wallet's startup "
                        + "import has not finished", storeId);
                    continue;
                }

                if (instance.ExitState.ExportInFlight is { IsCompleted: false })
                {
                    _logger.LogDebug(
                        "Store {StoreId}: an earlier exit-state export is still running, so none was started",
                        storeId);
                    continue;
                }

                // Before the export: a refresh requested while the export runs describes a change the
                // export may not contain, and must stay pending past this pass.
                var pass = _exitStateBackupScheduler.BeginPass(storeId);

                var export = instance.Sdk.ExportUnilateralExitStateAsync(cancellationToken);
                instance.ExitState.ExportInFlight = export;
                var exported = await SparkDeadline.OrNullAsync(
                        export,
                        ExitStateCallDeadline,
                        () => _logger.LogWarning(
                            "Store {StoreId}: its exit-state export exceeded {Seconds}s and was abandoned. The "
                            + "call cannot be cancelled; this store is skipped until it finishes, and a previous "
                            + "backup, if one exists, is still stored",
                            storeId, ExitStateCallDeadline.TotalSeconds),
                        cancellationToken)
                    .ConfigureAwait(false);

                // Timed out. Nothing recorded, so the request (if any) and the safety net stay armed.
                if (exported is null)
                    continue;

                if (string.IsNullOrWhiteSpace(exported))
                {
                    // No MarkSkipped on either path: an empty answer is not a report on the wallet's
                    // state, and serving a pending request with nothing would silently drop it. With
                    // nothing pending, though, the pass itself is worth recording — it is the only
                    // thing that stops a wallet with no exit state to export from being asked on every
                    // scheduled pass forever, and the safety net re-asks it on schedule.
                    if (_exitStateBackupScheduler.PendingSince(storeId) is null)
                        _exitStateBackupScheduler.MarkIdlePass(storeId, now);

                    _logger.LogInformation(
                        "Store {StoreId}: its exit-state export came back empty, so nothing was stored",
                        storeId);
                    continue;
                }

                var stored = await StoreOwnExportAsync(instance, exported, cancellationToken)
                    .ConfigureAwait(false);
                switch (stored)
                {
                    case OwnExportStored.Unchanged:
                        _exitStateBackupScheduler.MarkSkipped(storeId, now, pass);
                        break;

                    case OwnExportStored.Written:
                        // MarkTaken is this pass's own report, ordered after the write; the tracked store
                        // seam has already moved the scheduler's belief to these bytes.
                        _exitStateBackupScheduler.MarkTaken(storeId, now, pass);
                        _logger.LogInformation(
                            "Store {StoreId}: stored an automatic exit-state backup ({Length} characters)",
                            storeId, exported.Length);
                        break;

                    default:
                        // Held back deliberately (logged where it was decided); retried on a later pass.
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Type name only. And the pass deliberately records nothing here: the pending request (if
                // any) and the safety-net clock both stay armed, so the next pass retries this store.
                // Whatever backup was stored before this one is untouched, which is what a failure must mean.
                _logger.LogWarning(
                    "Store {StoreId}: its automatic exit-state backup failed ({ExceptionType}). A previous "
                    + "backup, if one exists, is still stored; this will be retried on a later pass",
                    storeId, ex.GetType().Name);
            }
        }
    }

    /// <summary>What <see cref="StoreOwnExportAsync"/> did with an export.</summary>
    private enum OwnExportStored
    {
        /// <summary>Byte-identical to what is stored; nothing was written.</summary>
        Unchanged,

        /// <summary>Written as the automatic backup.</summary>
        Written,

        /// <summary>Not written, and deliberately: something stored has to be secured first.</summary>
        HeldBack
    }

    /// <summary>
    /// Stores a wallet's own export as its automatic backup — the one write path the pass and the page's
    /// Export share. Throws on an IO failure; both callers catch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Held back</b> while the instance still has a stored backup it could not secure at connect
    /// (<see cref="ExitStateRuntime.NeedsRescue"/>) — until a retry here has queued it — and while the
    /// wallet's identity cannot be read but the stored backup names one: the write would then replace a file
    /// it cannot tell is not another wallet's. Both clear on their own; the next pass tries again.
    /// </para>
    /// <para>
    /// <b>Unchanged</b> is decided against the scheduler's belief, seeded once per process from the file, so an
    /// idle wallet is not rewritten every hour to say nothing — and the stamp is brought up to date on that
    /// path when it lags (an earlier build's file, or an identity that could not be read when it was
    /// written), because the stamp is what the next connect decides with.
    /// </para>
    /// </remarks>
    private async Task<OwnExportStored> StoreOwnExportAsync(
        SparkStoreInstance instance, string exported, CancellationToken cancellationToken)
    {
        var storeId = instance.StoreId;

        if (instance.ExitState.NeedsRescue && !await TryRescueAutomaticAsync(instance, cancellationToken).ConfigureAwait(false))
            return OwnExportStored.HeldBack;

        var identity = await ReadWalletIdentityAsync(instance).ConfigureAwait(false);

        // Seed once per process from the file, before judging a fresh export: a restarted server knows
        // nothing about what is stored, and a first pass that rewrote the file on every store would spend
        // a multi-megabyte write per store to say "unchanged".
        if (!_exitStateBackupScheduler.KnowsStoredContent(storeId))
        {
            _exitStateBackupScheduler.NoteStoredContent(
                storeId,
                await _exitStateBackupStore.ReadAsync(storeId, cancellationToken).ConfigureAwait(false));
        }

        var stamp = await _exitStateBackupStore.ReadStampAsync(storeId, cancellationToken).ConfigureAwait(false);

        if (_exitStateBackupScheduler.ContentUnchanged(storeId, exported))
        {
            if (stamp is null || !stamp.Describes(exported) || (stamp.WalletIdentity is null && identity is not null))
                await _exitStateBackupStore.StampAsync(storeId, exported, identity, cancellationToken)
                    .ConfigureAwait(false);

            return OwnExportStored.Unchanged;
        }

        if (identity is null && stamp?.WalletIdentity is not null)
        {
            _logger.LogInformation(
                "Store {StoreId}: its exit-state backup was not refreshed because the wallet's identity could not "
                + "be read, so it could not be told apart from another wallet's; this is retried on a later pass",
                storeId);
            return OwnExportStored.HeldBack;
        }

        await _exitStateBackupStore.WriteAsync(storeId, exported, identity, cancellationToken).ConfigureAwait(false);
        return OwnExportStored.Written;
    }

    /// <summary>
    /// Queues the automatic backup the connect could neither import nor queue, so the pass may replace it.
    /// </summary>
    private async Task<bool> TryRescueAutomaticAsync(SparkStoreInstance instance, CancellationToken cancellationToken)
    {
        var storeId = instance.StoreId;
        try
        {
            var backup = await _exitStateBackupStore.ReadAsync(storeId, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(backup))
                await _exitStateBackupStore.AddPendingAsync(storeId, backup, cancellationToken).ConfigureAwait(false);

            instance.ExitState.NeedsRescue = false;
            _logger.LogInformation(
                "Store {StoreId}: its stored exit-state backup, which the connect could not secure, was queued for "
                + "import; the automatic backup may replace the file again", storeId);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Store {StoreId}: its stored exit-state backup still cannot be secured ({ExceptionType}), so the "
                + "automatic backup will not replace it", storeId, ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// Imports this store's queued exit-state backups into its running wallet now. See
    /// <see cref="ISparkStoreRuntime.ImportPendingExitStateAsync"/>.
    /// </summary>
    public async Task<ExitStateImportReport> ImportPendingExitStateAsync(
        string storeId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(storeId))
            return ExitStateImportReport.NothingPending;

        try
        {
            await _startupGate.Task.ConfigureAwait(false);

            if (!_instances.TryGetValue(storeId, out var instance))
            {
                var queued = await _exitStateBackupStore.ListPendingAsync(storeId, cancellationToken)
                    .ConfigureAwait(false);
                return queued.Count == 0
                    ? ExitStateImportReport.NothingPending
                    : new ExitStateImportReport(ExitStateImportOutcome.WalletNotRunning);
            }

            // Bounded: a request thread waiting on a restore that is itself waiting on a slow import. The
            // queue is not lost by giving up — the restore holding the gate imports it, or the next connect.
            if (!await instance.ExitState.ImportGate.WaitAsync(ExitStateCallDeadline, cancellationToken)
                    .ConfigureAwait(false))
            {
                return new ExitStateImportReport(ExitStateImportOutcome.Busy);
            }

            try
            {
                var identity = await ReadWalletIdentityAsync(instance).ConfigureAwait(false);
                return await ImportQueuedLockedAsync(instance, identity).ConfigureAwait(false);
            }
            finally
            {
                instance.ExitState.ImportGate.Release();
            }
        }
        catch (Exception ex)
        {
            // Never out of a request path. The type only: the queue holds the operator's blob.
            _logger.LogWarning(
                "Store {StoreId}: importing its queued exit-state backups failed unexpectedly ({ExceptionType})",
                storeId, ex.GetType().Name);
            return new ExitStateImportReport(ExitStateImportOutcome.Failed, Failed: 1,
                Reason: "The import failed unexpectedly. Check the server log.");
        }
    }

    /// <summary>
    /// Exports the running wallet's exit state for the page, storing it as the automatic backup when that is
    /// safe. See <see cref="ISparkStoreRuntime.ExportExitStateAsync"/>.
    /// </summary>
    public async Task<ExitStateExportResult> ExportExitStateAsync(
        string storeId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(storeId))
            return new ExitStateExportResult(null, false, "No store was named, so there is nothing to export.");

        try
        {
            await _startupGate.Task.ConfigureAwait(false);

            if (!_instances.TryGetValue(storeId, out var instance))
            {
                return new ExitStateExportResult(null, false,
                    "This store's Spark wallet is not running, so its exit data cannot be read. Start the wallet "
                    + "and export again.");
            }

            string? exported;
            try
            {
                exported = await SparkDeadline.OrNullAsync(
                        instance.Sdk.ExportUnilateralExitStateAsync(cancellationToken),
                        ExitStateCallDeadline,
                        () => _logger.LogWarning(
                            "Store {StoreId}: an exit-state export from the page exceeded {Seconds}s and was "
                            + "abandoned", storeId, ExitStateCallDeadline.TotalSeconds),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The export is handed nothing, so its failure cannot carry a backup; the message still goes
                // through the scrubber every SDK failure goes through, because it is printed into a page.
                _logger.LogWarning(ex, "Store {StoreId}: could not export unilateral-exit state", storeId);
                return new ExitStateExportResult(null, false,
                    "Spark could not export this wallet's exit data: " + SparkErrors.Describe(ex)
                    + ". Nothing was changed; try again, and check the server log if it keeps failing.");
            }

            if (exported is null)
            {
                return new ExitStateExportResult(null, false,
                    "Spark did not finish exporting this wallet's exit data in time. Nothing was changed; try "
                    + "again in a moment.");
            }

            if (exported.Length == 0)
                return new ExitStateExportResult(exported, false);

            // The same bytes go into the store, so the copy the operator just read and the copy the plugin
            // keeps cannot disagree — unless the connect's restore is still working with the stored file,
            // which this write would replace underneath it.
            if (!instance.ExitState.Restored)
            {
                return new ExitStateExportResult(exported, false, NotStoredReason:
                    "This wallet is still importing its stored backup, so the copy below was not stored yet; the "
                    + "automatic backup stores it shortly.");
            }

            try
            {
                var stored = await StoreOwnExportAsync(instance, exported, cancellationToken).ConfigureAwait(false);
                return stored is OwnExportStored.HeldBack
                    ? new ExitStateExportResult(exported, false, NotStoredReason:
                        "The copy below was not stored: the backup already stored has to be secured first. Check "
                        + "the server log.")
                    : new ExitStateExportResult(exported, true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    "Store {StoreId}: an exit-state export from the page could not be stored ({ExceptionType})",
                    storeId, ex.GetType().Name);
                return new ExitStateExportResult(exported, false, NotStoredReason:
                    "The copy below could not be stored on this server. Check the server log.");
            }
        }
        catch (OperationCanceledException)
        {
            return new ExitStateExportResult(null, false, "The export was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Store {StoreId}: exporting its exit state failed unexpectedly ({ExceptionType})",
                storeId, ex.GetType().Name);
            return new ExitStateExportResult(null, false,
                "The exit data could not be exported. Check the server log.");
        }
    }

    /// <summary>
    /// Absolute path to the SDK storage directory for a store:
    /// <c>&lt;DataDir&gt;/Plugins/Spark/&lt;storeId&gt;</c>.
    /// </summary>
    public string GetWorkDir(string storeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        return FileSparkStorageProvider.GetStorageDirectory(_dataDirectories.Value.DataDir, storeId);
    }

    /// <summary>Settings for a store, or null when the store has not configured Spark.</summary>
    /// <remarks>
    /// <b>A copy, never the cached instance.</b> Handing the cache out by reference made every reader a
    /// potential writer of it: a caller that applied an edit and then failed to persist it left the cache
    /// holding a configuration that was never stored, and the sweep engine reads through this same cache. The
    /// clone is a few object allocations on a path that is already awaiting a gate, and it makes the read-only
    /// contract true rather than merely intended.
    /// </remarks>
    public async Task<SparkSettings?> Get(string storeId)
    {
        await _startupGate.Task.ConfigureAwait(false);
        return _settings.GetValueOrDefault(storeId)?.Clone();
    }

    /// <summary>Store ids with a live SDK instance.</summary>
    public async Task<IReadOnlyCollection<string>> GetRunningStoreIds()
    {
        await _startupGate.Task.ConfigureAwait(false);
        return _instances.Keys.ToList();
    }

    /// <summary>
    /// Whether any store on this server has Flint configured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The any-store form of <see cref="Get"/>, for the rare reader whose question is not about one store but
    /// about whether the plugin has anything to serve at all — today only the prompt-mint indexer, which gates
    /// its writes on it (see <see cref="SparkInvoicePaymentHashIndexer"/>). An empty cache before the load is
    /// not evidence that no store is configured, so the read waits on the startup gate — but on the bounded
    /// wait of <see cref="Resolve"/> (<see cref="StartupWaitTimeout"/>), not unboundedly: this runs on the
    /// indexer's server-wide event-loop path, and a hung SDK call at startup must not stall its ProcessEvents
    /// loop with an unbounded event channel. A wait that expires fails open — the caller proceeds as though a
    /// store existed and records — because a wrong "no" silently drops a row the reconciliation story cannot
    /// recover, while a wrong "yes" only costs a row retention will retire.
    /// </para>
    /// <para>
    /// A snapshot, not a lease: a store provisioned concurrently with the read may or may not be counted,
    /// which is exactly the granularity its one consumer needs — it re-reads on every event.
    /// </para>
    /// </remarks>
    public async Task<bool> HasAnyStoreProvisioned()
    {
        if (!_startupGate.Task.IsCompleted)
        {
            try
            {
                await _startupGate.Task.WaitAsync(StartupWaitTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return true;
            }
        }
        return !_settings.IsEmpty;
    }

    /// <summary>
    /// The live client for a store, or null when the store has not configured Spark or its instance failed to
    /// start.
    /// </summary>
    public async Task<SparkLightningClient?> GetClient(string? storeId)
    {
        if (string.IsNullOrEmpty(storeId))
            return null;
        await _startupGate.Task.ConfigureAwait(false);
        return _instances.TryGetValue(storeId, out var instance) ? instance.Client : null;
    }

    /// <summary>
    /// The SDK handle for a store, for callers that need more than the Lightning surface (the sweep task, and
    /// the status page). Null when the store has no live instance.
    /// </summary>
    public async Task<ISparkSdkClient?> GetSdkClient(string? storeId)
    {
        if (string.IsNullOrEmpty(storeId))
            return null;
        await _startupGate.Task.ConfigureAwait(false);
        return _instances.TryGetValue(storeId, out var instance) ? instance.Sdk : null;
    }

    /// <summary>
    /// The connection string a store's Lightning payment method should hold. Null when the store has no
    /// payment key yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Caller contract: <paramref name="authorisedStoreId"/> must be the store that was security-authorised
    /// for this request — for an HTTP request, <c>HttpContext.GetStoreDataOrNull()?.Id</c> — never an id
    /// bound from a form or query string. This returns the store's bearer spend credential and checks
    /// nothing itself about who is asking.
    /// </para>
    /// <para>
    /// The only callers are the two setup-tab extension-point partials, and both resolve the authorised
    /// store from HttpContext and refuse a mismatch before calling this.
    /// </para>
    /// </remarks>
    public async Task<string?> GetConnectionString(string authorisedStoreId)
    {
        var settings = await Get(authorisedStoreId).ConfigureAwait(false);
        return settings?.PaymentKey is { } key ? SparkConnectionString.Format(authorisedStoreId, key) : null;
    }

    /// <inheritdoc />
    public SparkClientResolution Resolve(string storeId, string paymentKey, Network network)
    {
        if (string.IsNullOrEmpty(storeId) || string.IsNullOrEmpty(paymentKey))
            return SparkClientResolution.Failed(NotConfiguredError);

        // The caller (BTCPay's Lightning client factory) is synchronous, so this is the one place the startup
        // gate has to be waited on rather than awaited. The wait is deliberately about a second: this runs on
        // request threads, and blocking many of them for longer starves the thread pool and can starve this
        // service's own startup continuations. A timeout is reported as transient rather than as "not
        // configured", because the two need different fixes.
        if (!_startupGate.Task.IsCompleted && !_startupGate.Task.Wait(StartupWaitTimeout))
        {
            _logger.LogWarning(
                "A Spark connection string for store {StoreId} was resolved before the plugin finished starting",
                storeId);
            return SparkClientResolution.Failed("The Spark plugin is still starting up; try again in a moment");
        }

        if (!SparkNetworks.TryGetSdkNetwork(network, out _, out var networkError))
            return SparkClientResolution.Failed(networkError!);

        if (!_settings.TryGetValue(storeId, out var settings) ||
            !SparkConnectionString.PaymentKeyMatches(settings.PaymentKey, paymentKey))
        {
            return SparkClientResolution.Failed(NotConfiguredError);
        }

        if (!_instances.TryGetValue(storeId, out var instance))
        {
            return SparkClientResolution.Failed(
                "This store's Spark wallet is not running; check the server logs and the store's Spark settings");
        }

        return SparkClientResolution.Resolved(instance.Client);
    }

    /// <summary>
    /// Persists settings for a store and reconciles the running instance with them. Passing null removes the
    /// configuration and shuts the instance down.
    /// </summary>
    /// <returns>
    /// Whether the store now has a running wallet, and why not when it does not. A caller that treats a
    /// returned result as success without checking <see cref="SparkSettingsApplied.WalletRunning"/> will
    /// happily configure a store whose wallet declined to start.
    /// </returns>
    public async Task<SparkSettingsApplied> Set(string storeId, SparkSettings? settings)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        await _startupGate.Task.ConfigureAwait(false);

        await _instanceLock.WaitAsync(CancellationToken).ConfigureAwait(false);
        try
        {
            settings = WithoutReinstatedLegacyBackup(storeId, settings);

            // Persist first: if the process dies between the two steps, the next startup converges on the
            // stored settings. The reverse order could leave a live wallet nothing has a record of.
            //
            // Under the instance lock, which it was not always: ClearExitStateBackupSlot rewrites the whole
            // row from a fresh read, and a persist outside the lock could land between that read and its
            // write and be reverted by it.
            //
            // The interface declares the value non-nullable while the implementation deletes the setting when
            // it is null, which is how removal works here — hence the suppression rather than a second code
            // path.
            await _storeRepository
                .UpdateSetting(storeId, Constants.StoreSettingsKey, settings!)
                .ConfigureAwait(false);

            if (settings is null)
            {
                _settings.TryRemove(storeId, out _);
                await TeardownInstanceAsync(storeId).ConfigureAwait(false);

                // Kept, under names that say the store no longer runs them — see the method.
                await SetAsideExitStateBackupsAsync(storeId).ConfigureAwait(false);

                // The plugin writes the store's BTC-LN payment method config, so it clears it too. Leaving a
                // connection string pointing at a wallet that no longer exists makes Lightning checkout fail
                // with "not configured for this store" rather than telling the merchant their Spark wallet was
                // removed. Only cleared when it still points at this store's Spark wallet — a merchant who
                // switched to their own node gets that configuration back untouched.
                //
                // After the teardown, not before: if this throws, the wallet is already down, which is the part
                // that matters. Failing before teardown would leave a live wallet with settings that say there
                // is none.
                await _lightningWiring.ClearIfOursAsync(storeId, CancellationToken).ConfigureAwait(false);

                // USDC and USDT land in this wallet too, so they go with it: left configured they could only ever
                // be offered as unavailable. Best effort, after everything that matters more has happened.
                try
                {
                    await _stablecoinsFactory().SetEnabledAsync(storeId, false, CancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Store {StoreId}: Spark was removed but its USDC/USDT payment methods could not be cleared",
                        storeId);
                }

                return SparkSettingsApplied.Removed;
            }

            // Cached as a copy, for the same reason Get hands one out: the caller still holds this object, and
            // an edit it makes after this returns must not reach into the cache.
            _settings[storeId] = settings.Clone();
            var declined = await StartInstanceAsync(storeId, settings, CancellationToken).ConfigureAwait(false);
            return declined is null
                ? SparkSettingsApplied.Running
                : SparkSettingsApplied.NotRunning(declined);
        }
        finally
        {
            _instanceLock.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Explicit implementations, so <see cref="ISparkStoreSettingsStore"/> adds no second public spelling of
    /// <see cref="Get"/> and <see cref="Set"/> to a class that already has plenty of surface.
    /// </remarks>
    Task<SparkSettings?> ISparkStoreSettingsStore.GetAsync(string storeId) => Get(storeId);

    /// <inheritdoc />
    Task<SparkSettingsApplied> ISparkStoreSettingsStore.SetAsync(string storeId, SparkSettings? settings) =>
        Set(storeId, settings);

    /// <inheritdoc />
    Task ISparkStoreSettingsStore.ClearExitStateBackupSlotAsync(string storeId) =>
        ClearExitStateBackupSlot(storeId);

    /// <inheritdoc />
    string ISparkStoreRuntime.GetStorageDirectory(string storeId) => GetWorkDir(storeId);

    /// <inheritdoc />
    Task<ISparkSdkClient?> ISparkStoreRuntime.GetSdkClientAsync(string storeId) => GetSdkClient(storeId);

    /// <inheritdoc />
    string? ISparkStoreRuntime.GetWalletTag(string storeId, ISparkSdkClient? sdk) =>
        _instances.TryGetValue(storeId, out var instance) && (sdk is null || ReferenceEquals(instance.Sdk, sdk))
            ? instance.WalletTag
            : null;

    /// <inheritdoc />
    Task<ExitStateImportReport> ISparkStoreRuntime.ImportPendingExitStateAsync(
        string storeId, CancellationToken cancellationToken) =>
        ImportPendingExitStateAsync(storeId, cancellationToken);

    /// <inheritdoc />
    Task<ExitStateExportResult> ISparkStoreRuntime.ExportExitStateAsync(
        string storeId, CancellationToken cancellationToken) =>
        ExportExitStateAsync(storeId, cancellationToken);

    #endregion

    /// <summary>
    /// Single message for "unknown store", "wrong key" and "no settings", so the handler cannot be used as an
    /// oracle for other stores' payment keys.
    /// </summary>
    private const string NotConfiguredError = "This Spark wallet is not configured for this store";

    /// <summary>
    /// One store's live SDK instance, the client that wraps it, and its own event queue and consumer.
    /// </summary>
    /// <remarks>
    /// The queue and consumer are per instance rather than shared, because handling an event makes an SDK call
    /// and no SDK call can be cancelled. A single global consumer would let one store's hung service-provider
    /// request stall settlement for every store on the server, and a single global queue would let one busy
    /// store's overflow drop another store's settlement events.
    /// </remarks>
    private sealed class SparkStoreInstance
    {
        private readonly Channel<SparkEventEnvelope> _events;
        private readonly CancellationTokenSource _consumerCts = new();
        private Task? _consumer;

        private readonly SparkStorageLock _storageLock;

        public SparkStoreInstance(
            string storeId,
            ISparkSdkClient sdk,
            SparkLightningClient client,
            Channel<SparkEventEnvelope> events,
            SparkStorageLock storageLock,
            bool storageWasEmpty,
            string walletTag)
        {
            StoreId = storeId;
            Sdk = sdk;
            Client = client;
            WalletTag = walletTag;
            _events = events;
            _storageLock = storageLock;
            ExitState = new ExitStateRuntime(storageWasEmpty);
        }

        /// <summary>This wallet's exit-state backup bookkeeping; see <see cref="ExitStateRuntime"/>.</summary>
        public ExitStateRuntime ExitState { get; }

        public string StoreId { get; }
        public ISparkSdkClient Sdk { get; }
        public SparkLightningClient Client { get; }

        /// <summary>Which wallet this is, publishably; see <see cref="DeriveWalletTag"/>.</summary>
        public string WalletTag { get; }

        public CancellationToken ConsumerToken => _consumerCts.Token;

        public void StartConsumer(Func<SparkEventEnvelope, Task> handler, ILogger logger)
        {
            _consumer = Task.Run(async () =>
            {
                try
                {
                    await foreach (var envelope in _events.Reader
                                       .ReadAllAsync(_consumerCts.Token)
                                       .ConfigureAwait(false))
                    {
                        try
                        {
                            await handler(envelope).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            // One bad event must not kill this store's loop; killing it would silently stop
                            // event-driven settlement for the store until the next reconciliation pass.
                            logger.LogError(ex,
                                "Store {StoreId}: failed to process a Spark {Kind} event", StoreId, envelope.Kind);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Teardown.
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Store {StoreId}: the Spark event consumer stopped. Settlements for this store will "
                        + "only be detected by reconciliation until it is reconfigured or the server restarts",
                        StoreId);
                }
            }, _consumerCts.Token);
        }

        /// <summary>
        /// How long teardown waits for the consumer to drain before cancelling it.
        /// </summary>
        /// <remarks>
        /// Deliberately much shorter than <see cref="Constants.SdkCallDeadline"/>. Teardown runs while the
        /// instance lock is held, and not only at shutdown: reconfiguring a store or deleting one goes through
        /// here too, so a long wait would stall the setup UI. Anything not drained in time is recovered by the
        /// reconciliation task.
        /// </remarks>
        private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

        public async Task ShutdownAsync(ILogger logger)
        {
            // Complete the writer before cancelling so anything already queued still drains.
            _events.Writer.TryComplete();

            // Disconnect (removes the listener, stops the sync loop) then Dispose (frees the handle).
            // Disconnect alone would leave the wallet minting invoices.
            //
            // Bounded, for the same reason the connect is (audit finding InfraAndLogging F1). This runs inside
            // the process-wide _instanceLock, from store reconfigure, store deletion and host shutdown alike. No
            // SDK call can be cancelled, so an unbounded await on a stalled Disconnect holds that lock forever:
            // every later setup save, every Greenfield provision or removal, and StopAsync itself queue behind it
            // until the process is killed — and in the StoreEvent.Removed path it also stalls BTCPay's serialised
            // event loop. Dispose still runs either way, which is what the storage lock actually depends on.
            await SparkDeadline.OrTimeoutAsync(
                    Sdk.DisconnectAsync(),
                    Constants.SdkCallDeadline,
                    () => logger.LogWarning(
                        "Store {StoreId}: disconnecting its Spark wallet exceeded {Seconds}s, so teardown stopped "
                        + "waiting on it. The disconnect cannot be cancelled and is still running; the handle is "
                        + "disposed regardless so the rest of this server keeps working",
                        StoreId, Constants.SdkCallDeadline.TotalSeconds),
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (_consumer is not null)
            {
                try
                {
                    await _consumer.WaitAsync(DrainTimeout).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                {
                    // An in-flight, uncancellable SDK call. Cancelled below; the handle is freed regardless.
                }
            }

            await _consumerCts.CancelAsync().ConfigureAwait(false);
            Sdk.Dispose();

            // After Dispose, never before: the claim says "a live SDK instance is using this directory", and
            // releasing it while one still is would let another process start a second one on the same file.
            _storageLock.Dispose();

            // The CancellationTokenSource is deliberately not disposed. A consumer still unwinding from an
            // uncancellable SDK call may yet read ConsumerToken, and an ObjectDisposedException there would be
            // logged as a settlement failure for a store that is simply going away. It holds no timer and no
            // unmanaged resource, and there is one per store lifetime, so leaving it is the cheaper trade.
        }
    }

    /// <summary>
    /// One wallet instance's exit-state backup bookkeeping: what the connect found, and what the automatic
    /// pass may and may not do yet.
    /// </summary>
    /// <remarks>
    /// Per instance rather than per store because every fact here is about one connect: a reconfigured store
    /// gets a fresh instance, a fresh restore and a fresh gate, and nothing a torn-down instance was doing
    /// can hold the new one back.
    /// </remarks>
    private sealed class ExitStateRuntime
    {
        private readonly TaskCompletionSource _restored =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private volatile string? _identity;
        private volatile bool _needsRescue;
        private volatile Task? _exportInFlight;

        public ExitStateRuntime(bool storageWasEmpty) => StorageWasEmpty = storageWasEmpty;

        /// <summary>
        /// Whether the wallet's SDK storage held nothing before this connect — the case in which the stored
        /// automatic backup holds exit data the wallet does not.
        /// </summary>
        public bool StorageWasEmpty { get; }

        /// <summary>Whether the connect-time restore has finished. The automatic pass and Export wait on it.</summary>
        public bool Restored => _restored.Task.IsCompleted;

        public Task RestoredTask => _restored.Task;

        public void MarkRestored() => _restored.TrySetResult();

        /// <summary>The wallet's identity public key once read; see <c>ReadWalletIdentityAsync</c>.</summary>
        public string? Identity
        {
            get => _identity;
            set => _identity = value;
        }

        /// <summary>
        /// The stored automatic backup could not be read, or could not be queued after a failed import, so
        /// nothing may replace it until a later attempt has queued it.
        /// </summary>
        public bool NeedsRescue
        {
            get => _needsRescue;
            set => _needsRescue = value;
        }

        /// <summary>The last export the automatic pass started, which may have been abandoned still running.</summary>
        public Task? ExportInFlight
        {
            get => _exportInFlight;
            set => _exportInFlight = value;
        }

        /// <summary>Serialises the imports into this wallet: the connect's restore and a paste's import.</summary>
        public SemaphoreSlim ImportGate { get; } = new(1, 1);
    }
}
