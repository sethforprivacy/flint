using System;
using System.Net.Http;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Rates;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.Flint;

/// <summary>
/// Plugin entry point: declares the BTCPay dependency and wires up DI.
/// </summary>
public class SparkPlugin : BaseBTCPayServerPlugin
{
    public override string Identifier => Constants.PluginIdentifier;

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new IBTCPayServerPlugin.PluginDependency
        {
            Identifier = nameof(BTCPayServer),
            Condition = $">={Constants.MinBTCPayServerVersion}"
        }
    ];

    public override void Execute(IServiceCollection services)
    {
        // Seam over the Breez SDK. Registered as interfaces so the storage backend can be swapped for
        // the SDK's Postgres one later, and so the logic above it stays unit-testable.
        services.AddSingleton<ISparkStorageProvider, FileSparkStorageProvider>();
        services.AddSingleton<IBolt11Parser>(provider =>
        {
            // BOLT11 parsing is network-specific and the network is fixed for the life of the process, so
            // it is resolved once here rather than per call.
            var networkProvider = provider.GetRequiredService<BTCPayNetworkProvider>();
            var network = SparkNetworks.ToNBitcoinNetwork(networkProvider.NetworkType);
            if (network is null)
            {
                // Unsupported chain (testnet, signet). SparkService refuses to start any store on such a
                // server, so this parser is never actually used; it is created against mainnet rules only
                // so DI resolution does not fail at startup and hide the real, clearer error.
                network = Network.Main;
            }

            return new NBitcoinBolt11Parser(
                network, provider.GetRequiredService<ILogger<NBitcoinBolt11Parser>>());
        });
        services.AddSingleton<ISparkSdkClientFactory, SparkSdkClientFactory>();

        // Spark's own published health, read through the SDK's static entry point. No wallet involved, so it
        // is a singleton with no per-store state.
        services.AddSingleton<ISparkNetworkStatusProbe, SparkNetworkStatusProbe>();

        // Mnemonic encryption at rest.
        services.AddSingleton<SparkMnemonicProtector>();

        // Settlement fan-out. One instance, shared by the reconciler (publisher) and every WaitInvoice
        // session (subscribers).
        services.AddSingleton<SparkSettlementBroadcaster>();

        // Routing a settlement to the BTCPay invoice its BOLT11 was minted for, which the settlement
        // notification above cannot be relied on to achieve: BTCPay's Lightning listener watches only each
        // invoice's current payment prompt, so a superseded BOLT11 that is paid after a restart settles here
        // and reaches no invoice. The gateway is the thin part that talks to BTCPay; the creditor holds every
        // decision, including the refusal to credit another store's invoice, and is unit-tested against a fake
        // of the gateway.
        services.AddSingleton<IInvoiceCreditGateway, BTCPayInvoiceCreditGateway>();
        services.AddSingleton<SparkInvoiceCreditor>();

        // The plugin's own payment-hash → invoice association, kept from every prompt-mint event regardless
        // of LUD-21. BTCPay indexes an LNURL prompt's hash into its AddressInvoices table only while LUD-21
        // is enabled, so a store whose merchant disabled it by hand would otherwise leave a late payment to a
        // superseded LNURL BOLT11 unattributable after a restart; this index is the fallback the gateway
        // consults when core's own table has nothing (see SparkInvoicePaymentHashIndexer for the writer).
        services.AddSingleton<IInvoicePaymentHashIndex, EfInvoicePaymentHashIndex>();
        services.AddSingleton<SparkInvoicePaymentHashIndexer>();
        services.AddSingleton<IHostedService>(provider =>
            provider.GetRequiredService<SparkInvoicePaymentHashIndexer>());

        // The single path from "a Spark receive happened" to "a BTCPay invoice is paid", shared by the event
        // consumer, BTCPay's GetInvoice lookup and the reconciliation task.
        services.AddSingleton<SparkSettlementReconciler>();

        // Deferred resolution of the two core types whose own construction enumerates plugin contributions.
        // This is not a style choice and must not be "tidied" into constructor injection: injecting either of
        // them directly puts a cycle in the container's object graph that it cannot detect (the cycle runs
        // through factory delegates), and the container answers an undetected cycle by recursing until its
        // StackGuard forks resolution onto a second thread and deadlocks on the root lock the first thread
        // holds. That is a permanent hang of BTCPay startup, before any startup task or hosted service runs.
        // SparkConnectionStringHandler and BTCPayStoreLightningConfigStore document the exact cycle.
        services.TryAddSingleton<Func<ISparkClientResolver>>(provider =>
            provider.GetRequiredService<ISparkClientResolver>);
        services.TryAddSingleton<Func<PaymentMethodHandlerDictionary>>(provider =>
            provider.GetRequiredService<PaymentMethodHandlerDictionary>);
        // PaymentService touches the same graph — its own constructor takes PaymentMethodHandlerDictionary — and
        // is deferred to keep that rule uniform rather than because eager injection is known to hang today: the
        // cycle above is broken at SparkConnectionStringHandler's Func<ISparkClientResolver>.
        // BTCPayInvoiceCreditGateway documents the path.
        services.TryAddSingleton<Func<PaymentService>>(provider =>
            provider.GetRequiredService<PaymentService>);

        // Ownership of the store's BTC-LN payment method config. The wiring class holds every decision about
        // when the plugin may write or clear it and is unit-tested against a fake of the store seam; the store
        // itself is the thin part that talks to BTCPay.
        services.AddSingleton<IStoreLightningConfigStore, BTCPayStoreLightningConfigStore>();
        services.AddSingleton<SparkLightningWiring>();

        // Cross-store enforcement. The save-time refusal lives in SparkLightningClient.Validate (which reads
        // the configured store off the request via IHttpContextAccessor below); this sweep is the backstop
        // for configurations that predate it. Registered as itself and through a deferred Func, because it
        // depends on SparkService (its settings store) and SparkService reaches it back through that Func.
        services.AddSingleton<ISparkStoreSource, BTCPayStoreSource>();
        services.AddSingleton<SparkLightningConfigSweeper>();
        services.TryAddSingleton<Func<SparkLightningConfigSweeper>>(provider =>
            provider.GetRequiredService<SparkLightningConfigSweeper>);

        // The request context Validate reads to learn which store a connection string is being saved on.
        // ASP.NET does not register this by default (BTCPay does not either), so the plugin does, idempotently.
        services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();

        // Per-store SDK lifecycle owner. Registered once and exposed as itself (views and the setup
        // controller resolve it directly), as the connection-string resolver, as the settings store the
        // provisioner writes through, and as an IHostedService so BTCPay starts it during host startup.
        services.AddSingleton<SparkService>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<SparkService>());
        services.AddSingleton<ISparkClientResolver>(provider => provider.GetRequiredService<SparkService>());
        services.AddSingleton<ISparkStoreSettingsStore>(provider => provider.GetRequiredService<SparkService>());
        services.AddSingleton<ISparkStoreRuntime>(provider => provider.GetRequiredService<SparkService>());

        // The setup flow's decisions, kept out of the controller so they can be tested.
        services.AddSingleton<SparkStoreProvisioner>();

        // What "status" is, for whoever is asking. Shared by the status page and the Greenfield status endpoint so
        // the two cannot report different facts, or gather them in a different order.
        services.AddSingleton<SparkStoreStatusReader>();

        // Seed reuse from the store's on-chain hot wallet. Scoped because core's HotwalletSafe is resolved
        // per request and the read is authorised against the signed-in principal.
        services.AddScoped<IHotWalletSeedReader, BTCPayHotWalletSeedReader>();

        // Which seed a seed source produces, and the hot-wallet policy gate over all three. Scoped because it reads
        // the hot-wallet seed, which is authorised against the calling principal — a cookie principal on the setup
        // page, an API-key principal on the Greenfield endpoint, judged identically by core's own helper.
        services.AddScoped<SparkSeedResolver>();

        // Lightning connection-string handler. BTCPay resolves IEnumerable<ILightningConnectionStringHandler>
        // and offers each handler the string until one claims it, so the concrete type must be
        // registered separately to keep a single instance.
        services.AddSingleton<SparkConnectionStringHandler>();
        services.AddSingleton<ILightningConnectionStringHandler>(provider =>
            provider.GetRequiredService<SparkConnectionStringHandler>());

        // Sweep destinations. The address source is the thin part that talks to the store's wallet and NBXplorer;
        // the resolver holds the rules about which mode wins and when to refuse, and is unit-tested against a fake
        // of the source.
        services.AddSingleton<ISweepAddressSource, BTCPayWalletSweepAddressSource>();

        // Labels a sweep's transaction in the store's wallet once its txid is known, so the transactions list
        // says where the money came from instead of showing an anonymous incoming transaction.
        services.AddSingleton<ISweepTransactionLabeler, BTCPayWalletSweepTransactionLabeler>();
        services.AddSingleton(provider =>
        {
            // The chain is fixed for the life of the process, so it is resolved once here rather than per sweep.
            // Null for a chain Spark does not support; SparkService refuses to start any wallet on such a server,
            // so no sweep can be reached, and the resolver validates against mainnet rules rather than failing DI
            // and hiding the real error.
            var networkProvider = provider.GetRequiredService<BTCPayNetworkProvider>();
            return new SweepDestinationResolver(
                provider.GetRequiredService<ISweepAddressSource>(),
                SparkNetworks.ToNBitcoinNetwork(networkProvider.NetworkType),
                provider.GetRequiredService<ILogger<SweepDestinationResolver>>());
        });

        // Which cross-chain route a sweep takes. Separate from the destination resolver because it needs a live
        // SDK handle and because its rules are about provider availability rather than about addresses: Boltz
        // routes appear in the SDK's table and every one of them currently fails at prepare, so the list cannot
        // be trusted as offered.
        services.AddSingleton<CrossChainRouteResolver>();

        // Which cross-chain destinations the settings page offers. A singleton because the whole point of it is
        // that one cached projection is shared by every store and every request: a per-request instance would be
        // a cold cache on every page view, which is the fan-out this exists to avoid.
        //
        // AddHttpClient rather than a hand-built HttpClient so socket lifetime is the factory's problem rather
        // than ours. Named, so the timeout below applies to this endpoint alone. The timeout is a second
        // backstop under the catalogue's own cancellation — it bounds the request where the token bounds the
        // whole operation — and the User-Agent is there so the provider can see who is calling and tell a
        // BTCPay plugin apart from a scraper.
        services.AddHttpClient(CrossChainCatalog.HttpClientName, client =>
        {
            client.Timeout = CrossChainCatalog.FetchTimeout;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"BTCPayServer.Plugins.Flint/{typeof(SparkPlugin).Assembly.GetName().Version}");
        })
        // Redirects are not followed. The catalogue response is parsed into routing decisions a
        // sweep can spend against, so it is only trusted from the endpoint the plugin itself
        // named: a 3xx to a different host — a compromised or misconfigured CDN, an open
        // redirect on the path — would otherwise be answered by whoever it points at, and the
        // default handler follows up to twenty of them transparently. A redirect is a fetch
        // failure here, which the catalogue's own cache-and-skip handling already treats as
        // "no routes this round", not as routes.
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false
        });
        services.AddSingleton<CrossChainCatalog>();

        // What a satoshi is worth, for the one cross-chain guard that cannot be computed from the quote: the
        // spread a provider states says nothing about the rate it applies. Every dependency is deferred through
        // Func<T> — this is reachable from a singleton the host constructs while building itself, and an
        // eagerly-resolved cycle through BTCPay's own graph is a silent permanent startup hang rather than an
        // error: ServiceProvider detects cycles only in the call-site graph it builds statically, and a
        // GetRequiredService inside a registration lambda is opaque to it.
        services.TryAddSingleton<Func<RateFetcher>>(provider =>
            provider.GetRequiredService<RateFetcher>);
        services.TryAddSingleton<Func<DefaultRulesCollection>>(provider =>
            provider.GetRequiredService<DefaultRulesCollection>);
        services.TryAddSingleton<Func<StoreRepository>>(provider =>
            provider.GetRequiredService<StoreRepository>);
        services.AddSingleton<ICrossChainValueOracle, BTCPayCrossChainValueOracle>();

        // Webhook notifier for post-sweep notifications. Named client so the timeout applies to delivery
        // alone and does not share the catalogue's socket pool. Failures are logged as warnings.
        services.AddHttpClient(SparkSweepWebhookNotifier.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"BTCPayServer.Plugins.Flint/{typeof(SparkPlugin).Assembly.GetName().Version}");
        });
        services.AddSingleton<SparkSweepWebhookNotifier>();

        // The sweep engine. One path for automatic and manual sweeps; every economic and safety guard lives here
        // rather than in the controller or the view.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SparkSweepEngine>();

        // On-chain deposits: the static address, the unclaimed list, and the manual claim. Shared by the status
        // page and the Greenfield endpoint so neither can decide on its own what a safe claim ceiling is.
        services.AddSingleton<SparkDepositService>();

        // Stable Balance. The mainnet check is resolved once here, because the chain is fixed for the life of
        // the process and because the answer is a refusal rather than a behaviour change: the SDK accepts a
        // stable-balance configuration on regtest and then silently never converts.
        services.AddSingleton(provider =>
        {
            var networkProvider = provider.GetRequiredService<BTCPayNetworkProvider>();
            return new SparkStableBalanceService(
                provider.GetRequiredService<ISparkStoreSettingsStore>(),
                provider.GetRequiredService<ISparkStoreRuntime>(),
                SparkNetworks.ToNBitcoinNetwork(networkProvider.NetworkType) == Network.Main,
                provider.GetRequiredService<ILogger<SparkStableBalanceService>>());
        });

        // Reading, validating and writing a store's sweep configuration, plus paging its history. Shared by the
        // settings form and the Greenfield sweep endpoints so a configuration one accepts is one the other accepts.
        services.AddSingleton<SparkSweepSettingsService>();

        // The Greenfield endpoints' OpenAPI fragment, merged into BTCPay's /swagger/v1/swagger.json. Depends on
        // nothing on purpose — see the class remarks, and the Func<T> note above.
        services.AddSingleton<ISwaggerProvider, SparkSwaggerProvider>();

        // Durable invoice/sweep storage in the plugin's own Postgres schema.
        services.AddSingleton<SparkPluginDbContextFactory>();
        services.AddSingleton<IInvoiceRecordStore, EfInvoiceRecordStore>();
        services.AddSingleton<IOutgoingPaymentStore, EfOutgoingPaymentStore>();
        services.AddSingleton<ISweepRecordStore, EfSweepRecordStore>();
        services.AddDbContext<SparkPluginDbContext>((provider, options) =>
        {
            var factory = provider.GetRequiredService<SparkPluginDbContextFactory>();
            factory.ConfigureBuilder(options);
        });

        services.AddStartupTask<SparkMigrationStartupTask>();

        // Settlement reconciliation. This is not a safety net, it is the settlement guarantee: the SDK drops
        // completion events, and BTCPay does not re-poll pending invoices (its one-minute timer only checks
        // connections). One minute matches the resolution merchants already expect from Lightning checkout.
        services.AddScheduledTask<SparkReconciliationTask>(Constants.ReconciliationInterval);

        // Auto-sweep. Sweep frequency does not change what sweeping costs — the trigger is a balance threshold,
        // not a clock — so this interval only bounds two latencies: how long a balance sits above the threshold
        // before it leaves Spark, and how long a sweep interrupted by a crash stays unresolved. It costs one
        // SyncWallet plus one GetInfo per configured store per pass. See SweepTask for the full argument.
        services.AddScheduledTask<SweepTask>(Constants.SweepInterval);

        // Payment-hash retention. The indexer's write gate stops this plugin recording prompts on a server
        // with no Flint store; this pass retires rows — on any server — older than the credit walk's own
        // 14-day listing floor, which is the last moment any reader can still consult them. Hourly, because
        // the pass is one indexed DELETE and the freshest a row can die is bounded by this interval; see
        // SparkPaymentHashRetentionTask for why it is its own task and not a line in the reconciliation pass.
        services.AddScheduledTask<SparkPaymentHashRetentionTask>(Constants.PaymentHashRetentionInterval);

        // Cross-store Lightning configuration backstop. Every HTTP path is refused at save time, so this only
        // has to catch configurations written outside HTTP (a direct database edit, another plugin); half an
        // hour bounds how long one can survive between restarts for one store-table load per pass. The
        // launcher fires the first pass immediately at startup, so SparkService deliberately does not run
        // its own. See SparkLightningConfigSweepTask.
        services.AddScheduledTask<SparkLightningConfigSweepTask>(Constants.ConfigSweepInterval);

        // Daily registry check: fires a webhook when a new Flint version appears on the plugin registry.
        services.AddHttpClient(SparkPluginUpdateChecker.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"BTCPayServer.Plugins.Flint/{typeof(SparkPlugin).Assembly.GetName().Version}");
        });
        services.AddScheduledTask<SparkPluginUpdateChecker>(TimeSpan.FromDays(1));

        // UI extension points. Paths are relative to Views/Shared/ and resolved as partials.
        services.AddUIExtension("ln-payment-method-setup-tabhead", "Spark/LNPaymentMethodSetupTabhead");
        services.AddUIExtension("ln-payment-method-setup-tab", "Spark/LNPaymentMethodSetupTab");
        services.AddUIExtension("store-integrations-nav", "Spark/SparkNav");

        // Sweeping as setup step 2 and as a section of the status page, through the extension points those two
        // views already publish.
        services.AddUIExtension("spark-setup-post-body", "Spark/SparkSweepSetupStep");
        services.AddUIExtension("spark-status-post-body", "Spark/SparkSweepStatus");

        base.Execute(services);
    }
}
