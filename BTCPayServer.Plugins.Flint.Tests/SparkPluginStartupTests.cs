using System.Diagnostics;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.Flint.Controllers;
using BTCPayServer.Hosting;
using BTCPayServer.Lightning;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.Bitcoin;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using NBXplorer;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The plugin composed into BTCPay's real container, resolved the way BTCPay's host resolves it.
/// </summary>
/// <remarks>
/// <para>
/// These exist because of a bug that reached a real deployment while 579 tests passed: none of them ever built
/// a container containing both BTCPay's services and the plugin's, so none of them could see that the two
/// graphs formed a cycle. The plugin's <c>SparkConnectionStringHandler</c> is constructed by BTCPay from
/// <em>inside</em> the construction of <c>PaymentMethodHandlerDictionary</c>
/// (<c>PaymentMethodHandlerDictionary</c> → <c>IEnumerable&lt;IPaymentMethodHandler&gt;</c> →
/// <c>LightningLikePaymentHandler</c> → <c>LightningClientFactoryService</c> →
/// <c>IEnumerable&lt;ILightningConnectionStringHandler&gt;</c>), and the handler used to pull
/// <c>SparkService</c>, which reached <c>PaymentMethodHandlerDictionary</c> again through
/// <c>SparkLightningWiring</c>.
/// </para>
/// <para>
/// Because two edges of that cycle run through factory delegates, <c>ServiceProvider</c> cannot see it when it
/// builds its call-site graph, so it does not report a circular dependency: it recurses, and at depth its
/// <c>StackGuard</c> continues resolution on a second thread while the first waits for it holding the
/// container's root lock. The result is a permanent deadlock of BTCPay's startup — no exception, no log line,
/// no database connection, and the plugin is never disabled automatically because nothing ever threw.
/// </para>
/// <para>
/// So every assertion here is bounded by <see cref="ResolveTimeout"/> and each resolution runs on its own
/// background thread: a regression must fail the test rather than hang the test run forever.
/// </para>
/// </remarks>
public class SparkPluginStartupTests
{
    /// <summary>
    /// How long a resolution may take before it is treated as hung.
    /// </summary>
    /// <remarks>
    /// A healthy resolution of BTCPay's whole graph plus the plugin's takes well under a second, so this is
    /// roughly two orders of magnitude of headroom — enough that a loaded CI machine cannot make it flaky, while
    /// keeping a regression run to a couple of minutes rather than forever. The failure being guarded against is
    /// unbounded rather than slow: it never completes at any timeout.
    /// </remarks>
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(30);

    private readonly ITestOutputHelper _output;

    public SparkPluginStartupTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// The two resolutions <c>Program.Main</c> performs, via <c>IHost.StartWithTasksAsync</c>, in that order.
    /// </summary>
    /// <remarks>
    /// This is the test that would have caught the deadlock. <c>StartWithTasksAsync</c> resolves
    /// <c>IEnumerable&lt;IStartupTask&gt;</c> and then <c>IHost.StartAsync</c> resolves
    /// <c>IEnumerable&lt;IHostedService&gt;</c>; both materialise every registration eagerly, and the plugin
    /// contributes to both. The hang happened during the first of them, which is why the real deployment showed
    /// no database connection at all.
    /// </remarks>
    [Fact]
    public void BTCPays_startup_resolutions_complete()
    {
        using var host = SparkTestHost.Create(_output);

        var startupTasks = host.Resolve(
            "IEnumerable<IStartupTask> (what IHost.StartWithTasksAsync resolves before starting the host)",
            provider => provider.GetServices<IStartupTask>().ToList());

        // Pins the plugin's own contribution, so a future refactor that drops the migration task is a failure
        // here rather than a silently unmigrated schema.
        Assert.Contains(startupTasks, task => task is SparkMigrationStartupTask);

        var hostedServices = host.Resolve(
            "IEnumerable<IHostedService> (what IHost.StartAsync resolves)",
            provider => provider.GetServices<IHostedService>().ToList());

        Assert.Contains(hostedServices, service => service is SparkService);
    }

    /// <summary>
    /// The exact re-entrancy the deadlock came from, asserted from both ends.
    /// </summary>
    /// <remarks>
    /// Resolved in both orders on purpose. Which of the two the host happens to reach first depends on
    /// registration and startup order in core, so a fix that only holds when the dictionary is built first is
    /// not a fix.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_plugins_lightning_handler_and_BTCPays_payment_handlers_resolve_in_either_order(
        bool dictionaryFirst)
    {
        using var host = SparkTestHost.Create(_output);

        if (dictionaryFirst)
        {
            host.Resolve(
                "PaymentMethodHandlerDictionary",
                provider => provider.GetRequiredService<PaymentMethodHandlerDictionary>());
            host.Resolve(
                "IEnumerable<ILightningConnectionStringHandler>",
                provider => provider.GetServices<ILightningConnectionStringHandler>().ToList());
        }
        else
        {
            host.Resolve(
                "IEnumerable<ILightningConnectionStringHandler>",
                provider => provider.GetServices<ILightningConnectionStringHandler>().ToList());
            host.Resolve(
                "PaymentMethodHandlerDictionary",
                provider => provider.GetRequiredService<PaymentMethodHandlerDictionary>());
        }

        // Resolving them must also have produced a usable handler for our own connection strings, otherwise a
        // "fix" that simply stopped registering the handler would pass the timeout assertions above.
        var handlers = host.Resolve(
            "IEnumerable<ILightningConnectionStringHandler>",
            provider => provider.GetServices<ILightningConnectionStringHandler>().ToList());
        Assert.Contains(handlers, handler => handler is SparkConnectionStringHandler);
    }

    /// <summary>
    /// The whole plugin graph, resolved through the singletons BTCPay and the plugin's own controllers use.
    /// </summary>
    /// <remarks>
    /// Broader than the two tests above and cheap to keep: it fails on any future service the plugin adds that
    /// re-enters core's graph, not only on the connection-string handler.
    /// </remarks>
    [Fact]
    public void Every_singleton_the_plugin_registers_resolves()
    {
        using var host = SparkTestHost.Create(_output);

        foreach (var type in new[]
                 {
                     typeof(SparkService),
                     typeof(ISparkClientResolver),
                     typeof(ISparkStoreSettingsStore),
                     typeof(ISparkStoreRuntime),
                     typeof(SparkStoreProvisioner),
                     typeof(SparkStoreStatusReader),
                     typeof(SparkLightningWiring),
                     typeof(IStoreLightningConfigStore),
                     typeof(SparkSweepEngine),
                     typeof(SparkSweepSettingsService),
                     typeof(SweepDestinationResolver),
                     typeof(ISweepAddressSource),
                     // Reaches core's graph for IHttpClientFactory (as does SparkExitFundingExplorer below).
                     // It is registered alongside its own named client, so this fails if that registration
                     // is ever dropped in favour of assuming core made one.
                     typeof(CrossChainCatalog),
                     typeof(IUnilateralExitRecordStore),
                     typeof(SparkExitFundingExplorer),
                     typeof(ISparkUnilateralExitService),
                     typeof(SparkReconciliationTask),
                     typeof(SweepTask),
                     typeof(SparkConnectionStringHandler),
                     typeof(ISparkSdkClientFactory),
                     typeof(ISparkNetworkStatusProbe),
                     typeof(IBolt11Parser),
                     typeof(IInvoiceRecordStore),
                     typeof(IOutgoingPaymentStore),
                     typeof(ISweepRecordStore),
                     typeof(SparkPluginDbContextFactory)
                 })
        {
            var resolved = host.Resolve(type.Name, provider => provider.GetRequiredService(type));
            Assert.NotNull(resolved);
        }
    }

    /// <summary>
    /// Everything the plugin registers per request, resolved inside a scope the way a request would.
    /// </summary>
    /// <remarks>
    /// A separate test because these cannot come from the root provider in a container built with scope validation.
    /// They are as capable of re-entering core's graph as a singleton is — <c>SparkSeedResolver</c> reaches core's
    /// authorisation services and its hot-wallet reader reaches the service provider itself — so leaving them out
    /// would leave the plugin's request-time graph unchecked.
    /// </remarks>
    [Fact]
    public void Every_scoped_service_the_plugin_registers_resolves()
    {
        using var host = SparkTestHost.Create(_output);

        foreach (var type in new[]
                 {
                     typeof(IHotWalletSeedReader),
                     typeof(SparkSeedResolver)
                 })
        {
            var resolved = host.Resolve(
                type.Name,
                provider =>
                {
                    using var scope = provider.CreateScope();
                    return scope.ServiceProvider.GetRequiredService(type);
                });
            Assert.NotNull(resolved);
        }
    }

    /// <summary>
    /// The plugin's OpenAPI contribution is registered, and BTCPay would merge it.
    /// </summary>
    /// <remarks>
    /// Registered as <c>ISwaggerProvider</c> only, so this asserts on the enumeration core's swagger action
    /// resolves rather than on the concrete type. It also fetches the document, because a registration that throws
    /// when read — a renamed embedded resource, a build that dropped it — would otherwise only surface as a broken
    /// <c>/swagger/v1/swagger.json</c> on a live server.
    /// </remarks>
    [Fact]
    public async Task The_plugins_swagger_fragment_is_registered_and_readable()
    {
        using var host = SparkTestHost.Create(_output);

        var providers = host.Resolve(
            "IEnumerable<ISwaggerProvider>",
            provider => provider.GetServices<ISwaggerProvider>().ToList());

        var spark = Assert.Single(providers.OfType<SparkSwaggerProvider>());
        var document = await spark.Fetch();

        Assert.NotNull(document["paths"]?["/api/v1/stores/{storeId}/spark"]);
    }

    /// <summary>
    /// BTCPay serialises Greenfield responses with the framework's default Newtonsoft settings.
    /// </summary>
    /// <remarks>
    /// The assumption the plugin's OpenAPI fragment and its validation-error paths both rest on. Those settings
    /// carry a camelCase naming strategy and <em>no</em> enum converter, which is why every documented member name
    /// is camelCase and every enum property on the API models is annotated individually. If a future BTCPay release
    /// configures its own resolver, the fragment starts lying about the wire format and the errors start naming
    /// fields nobody sent — so the assumption is checked against the real container rather than assumed.
    /// </remarks>
    [Fact]
    public void BTCPays_JSON_settings_are_the_ones_the_API_models_are_written_against()
    {
        using var host = SparkTestHost.Create(_output);

        var settings = host.Resolve(
            "IOptions<MvcNewtonsoftJsonOptions>",
            provider => provider.GetRequiredService<IOptions<MvcNewtonsoftJsonOptions>>().Value.SerializerSettings);

        var probe = new { BalanceSats = 1L, Speed = SweepConfirmationSpeed.Medium };

        Assert.Equal(
            JsonConvert.SerializeObject(probe, Fakes.ApiJson.Settings),
            JsonConvert.SerializeObject(probe, settings));

        // Pinned explicitly as well, so a change to both at once is still a failure rather than a silent agreement.
        Assert.Equal("""{"balanceSats":1,"speed":1}""", JsonConvert.SerializeObject(probe, settings));
    }

    /// <summary>
    /// Both of the plugin's controllers are constructible from the real container, inside a request scope.
    /// </summary>
    /// <remarks>
    /// <c>AddControllersAsServices</c> means BTCPay activates controllers from the container, so a controller whose
    /// constructor asks for something the plugin forgot to register is a 500 on every request to it — and, for the
    /// Greenfield surface, a 500 that no page ever shows anyone. Resolving both here is the cheapest way to make
    /// that a build-time failure instead.
    /// </remarks>
    [Fact]
    public void Both_of_the_plugins_controllers_resolve()
    {
        using var host = SparkTestHost.Create(_output);

        foreach (var type in new[] { typeof(SparkController), typeof(GreenfieldSparkController) })
        {
            var resolved = host.Resolve(
                type.Name,
                provider =>
                {
                    using var scope = provider.CreateScope();
                    return ActivatorUtilities.CreateInstance(scope.ServiceProvider, type);
                });
            Assert.NotNull(resolved);
        }
    }

    /// <summary>
    /// BTCPay's own services plus the plugin's, in one container, with a hard timeout on every resolution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Composed through BTCPay's own extension methods (<c>AddBTCPayServer</c>, its <c>BitcoinPlugin</c>, and
    /// <c>Startup.CreateBootstrap</c>) rather than a hand-written approximation, so it cannot quietly stop
    /// resembling the real container when core changes. The plugin is executed through
    /// <c>PluginServiceCollection</c>, which is what <c>PluginManager</c> hands to <c>Execute</c>.
    /// </para>
    /// <para>
    /// No database, NBXplorer or network access is involved: the deadlock was purely a container-resolution
    /// failure, and the options that would need a live database (<c>DatabaseOptions</c> and friends) are
    /// configured lazily by core and never read during resolution.
    /// </para>
    /// </remarks>
    private sealed class SparkTestHost : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly string _dataDir;
        private readonly ITestOutputHelper? _output;

        private SparkTestHost(ServiceProvider provider, string dataDir, ITestOutputHelper? output)
        {
            _provider = provider;
            _dataDir = dataDir;
            _output = output;
        }

        public static SparkTestHost Create(ITestOutputHelper? output = null)
        {
            var dataDir = Path.Combine(Path.GetTempPath(), "spark-startup-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataDir);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["network"] = "regtest",
                    ["chains"] = "btc",
                    ["datadir"] = dataDir,
                    // Never connected to. Core only reads this when something opens a connection, which
                    // resolution does not, but it has to be present or DatabaseOptions throws if anything does.
                    ["postgres"] = "Host=127.0.0.1;Port=1;Database=spark-startup-tests;Username=postgres",
                    ["BTC.explorer.url"] = "http://127.0.0.1:1/"
                })
                .Build();

            var services = new ServiceCollection();

            // Registered by the generic host in production, not by Startup.
            services.AddSingleton<IConfiguration>(configuration);
            services.AddLogging();
            services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment(dataDir));

            // BTCPay's real ConfigureServices, not an approximation of it. That includes AddBTCPayServer,
            // data protection, MVC, and AddPlugins — which runs core's own system plugins, among them
            // BitcoinPlugin. BitcoinPlugin is what registers the BTC network and the LightningLikePaymentHandler
            // that sits on the dependency cycle, so without it there would be no cycle here to catch.
            var startup = new Startup(configuration);
            startup.ConfigureServices(services);

            // AddPlugins only loads plugins it finds on disk, and the plugin directory under this test's
            // throwaway data directory is empty. So this plugin is executed the way PluginManager would have:
            // through a PluginServiceCollection carrying the same bootstrap provider.
            using var bootstrap = Startup.CreateBootstrap(configuration, startup.Logs, startup.LoggerFactory);
            new SparkPlugin().Execute(new PluginServiceCollection(services, bootstrap));

            return new SparkTestHost(services.BuildServiceProvider(), dataDir, output);
        }

        /// <summary>
        /// Resolves something from the container, failing the test if it does not finish within
        /// <see cref="ResolveTimeout"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// On a dedicated thread rather than through <c>Task.Run</c> and <c>Task.Wait(timeout)</c>: the failure
        /// mode being guarded against parks a thread inside <c>ServiceProvider</c>'s <c>StackGuard</c>, which
        /// itself queues work to the thread pool. Running the resolution on a pool thread would let a hang
        /// starve the pool and stall the rest of the test run instead of failing this test.
        /// </para>
        /// <para>
        /// The thread is a background thread, so a hung resolution — which holds the container's root lock and
        /// can never be cancelled or interrupted — does not stop the process from exiting once the assertion has
        /// failed.
        /// </para>
        /// </remarks>
        public T Resolve<T>(string what, Func<IServiceProvider, T> resolve)
        {
            T result = default!;
            Exception? failure = null;
            var stopwatch = Stopwatch.StartNew();

            var thread = new Thread(() =>
            {
                try
                {
                    result = resolve(_provider);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            })
            {
                IsBackground = true,
                Name = $"spark-resolve-{what}"
            };

            thread.Start();

            if (!thread.Join(ResolveTimeout))
            {
                Assert.Fail(
                    $"Resolving {what} did not complete within {ResolveTimeout.TotalSeconds:0}s, so BTCPay's "
                    + "startup would hang on a server with this plugin installed. The known cause is a cycle "
                    + "between the plugin's service graph and BTCPay's: SparkConnectionStringHandler is built "
                    + "from inside PaymentMethodHandlerDictionary's construction, so anything it pulls in must "
                    + "not lead back to PaymentMethodHandlerDictionary. ServiceProvider cannot report such a "
                    + "cycle when it runs through factory delegates — it recurses until StackGuard forks onto "
                    + "another thread and deadlocks on the root lock. See SparkConnectionStringHandler.");
            }

            _output?.WriteLine($"Resolved {what} in {stopwatch.ElapsedMilliseconds} ms");

            if (failure is not null)
            {
                throw new InvalidOperationException(
                    $"Resolving {what} threw. The plugin must compose into BTCPay's container cleanly.",
                    failure);
            }

            return result;
        }

        /// <summary>
        /// The one thing the generic web host supplies that a bare <c>ServiceCollection</c> does not.
        /// </summary>
        /// <remarks>
        /// A stand-in rather than a real hosting environment because nothing on the paths under test reads it
        /// for anything but its name: core's <c>BTCPayServerEnvironment</c> takes it in its constructor, and that
        /// constructor is on the graph these tests resolve.
        /// </remarks>
        private sealed class TestWebHostEnvironment : IWebHostEnvironment
        {
            public TestWebHostEnvironment(string rootPath)
            {
                ContentRootPath = rootPath;
                WebRootPath = rootPath;
                ContentRootFileProvider = new PhysicalFileProvider(rootPath);
                WebRootFileProvider = ContentRootFileProvider;
            }

            public string EnvironmentName { get; set; } = Environments.Production;
            public string ApplicationName { get; set; } = "BTCPayServer";
            public string WebRootPath { get; set; }
            public IFileProvider WebRootFileProvider { get; set; }
            public string ContentRootPath { get; set; }
            public IFileProvider ContentRootFileProvider { get; set; }
        }

        public void Dispose()
        {
            _provider.Dispose();
            try
            {
                Directory.Delete(_dataDir, recursive: true);
            }
            catch (IOException)
            {
                // A temp directory left behind is not worth failing a test over.
            }
        }
    }
}
