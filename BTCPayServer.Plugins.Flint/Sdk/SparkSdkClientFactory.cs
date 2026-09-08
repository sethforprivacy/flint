using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Breez.Sdk.Spark;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>
/// Connects SDK instances via <c>SdkBuilder</c>, so the storage backend is injectable.
/// </summary>
public class SparkSdkClientFactory : ISparkSdkClientFactory
{
    private readonly ISparkStorageProvider _storageProvider;
    private readonly IBolt11Parser _bolt11Parser;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SparkSdkClientFactory> _logger;

    public SparkSdkClientFactory(
        ISparkStorageProvider storageProvider,
        IBolt11Parser bolt11Parser,
        ILoggerFactory loggerFactory)
    {
        _storageProvider = storageProvider;
        _bolt11Parser = bolt11Parser;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SparkSdkClientFactory>();
    }

    public async Task<ISparkSdkClient> ConnectAsync(
        SparkConnectOptions options,
        ChannelWriter<SparkEventEnvelope> eventWriter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(eventWriter);
        ArgumentException.ThrowIfNullOrEmpty(options.Mnemonic);

        // Every SDK DTO is an init-only record: `config.apiKey = x` does not compile (CS8852). `with`
        // is the only way to patch one.
        var config = BreezSdkSparkMethods.DefaultConfig(options.Network) with
        {
            apiKey = options.ApiKey
        };

        // Client mode (DefaultConfig, not DefaultServerConfig): server mode sets
        // backgroundTasksEnabled = false, which disables the in-process event stream this plugin's
        // settlement path is built on, and hard-fails on Stable Balance config.
        config = ApplyPostMvpConfig(config, options, _logger);
        config = ApplyCustomNetwork(config, options, _logger);

        var seed = new Seed.Mnemonic(options.Mnemonic, options.Passphrase);

        BreezSdk sdk;
        var builder = new SdkBuilder(config, seed);
        try
        {
            // A private network has no Lightspark chain API in front of it, so the chain source has to be
            // named explicitly — and before Build(), which is the only point the builder still accepts it.
            // Credentials are null because the fixture's Esplora is unauthenticated.
            if (options.CustomNetwork is { } customNetwork)
            {
                await builder
                    .WithRestChainService(customNetwork.EsploraUrl, ChainApiType.Esplora, null)
                    .ConfigureAwait(false);
            }

            switch (_storageProvider.GetTarget(options.StoreId))
            {
                case SparkStorageTarget.Directory directory:
                    await builder.WithDefaultStorage(directory.Path).ConfigureAwait(false);
                    break;
                case SparkStorageTarget.Backend backend:
                    // The SDK takes ownership of the StorageBackend handle; deliberately not disposed
                    // here, since doing so would free it out from under the instance that is using it.
                    await builder.WithStorageBackend(backend.Factory()).ConfigureAwait(false);
                    break;
                default:
                    throw new NotSupportedException("Unknown Spark storage target.");
            }

            sdk = await builder.Build().ConfigureAwait(false);
        }
        finally
        {
            // The builder is a native handle of its own and is finished with either way. Build() can be
            // called twice on one builder, producing two live instances on one wallet, so it must not
            // be kept around.
            builder.Dispose();
        }

        string? listenerId = null;
        try
        {
            var listener = new SparkEventListenerAdapter(
                options.StoreId,
                eventWriter,
                _loggerFactory.CreateLogger<SparkEventListenerAdapter>());
            listenerId = await sdk.AddEventListener(listener).ConfigureAwait(false);
            _logger.LogDebug(
                "Store {StoreId}: connected the Spark SDK on {Network} (listener {ListenerId})",
                options.StoreId, options.Network, listenerId);
        }
        catch (Exception)
        {
            // Without a listener there is no settlement path, so a half-built instance must not be
            // handed out — and must not be leaked either.
            sdk.Dispose();
            throw;
        }

        return new SparkSdkClient(
            options.StoreId,
            sdk,
            listenerId,
            _bolt11Parser,
            _loggerFactory.CreateLogger<SparkSdkClient>());
    }

    /// <summary>
    /// Applies the deposit, Stable Balance and cross-chain configuration onto a default config.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Internal and static so the three decisions here are directly testable without connecting anything. All
    /// three have failure modes that are silent, permanent, or both.
    /// </para>
    /// </remarks>
    internal static Config ApplyPostMvpConfig(Config config, SparkConnectOptions options, ILogger logger)
    {
        // 1. The deposit claim fee cap.
        //
        // DefaultConfig sets maxDepositClaimFee = Rate(1 sat/vB) on *both* networks, and it is a cap rather
        // than a bid: above it the claim simply never happens and the deposit sits unclaimed indefinitely. Even
        // at the unusually cheap 3 sat/vB the spike sampled, the default is below the market. Overriding it is
        // therefore not a tuning choice, it is the difference between an on-chain top-up arriving and appearing
        // to vanish. Note that null here would *disable* automatic claiming rather than restore a default,
        // which is why SparkDepositSettings.ToMaxFee can never produce one.
        if (options.MaxDepositClaimFee is { } maxDepositClaimFee)
        {
            config = config with { maxDepositClaimFee = ToSdkMaxFee(maxDepositClaimFee) };
            logger.LogDebug(
                "Store {StoreId}: deposits will be claimed at {Policy}", options.StoreId, maxDepositClaimFee);
        }

        // 2 and 3 both require the SDK's background tasks, and setting either without them is a *hard init
        // failure* — "stable_balance_config is not supported when background_tasks_enabled is false" and
        // "Cross-chain config must be unset when background tasks are disabled". A store whose connect throws
        // has no wallet at all, so this would take a working merchant's Lightning down rather than merely
        // failing to enable a new feature.
        //
        // Asserted rather than assumed. It is true today because this method is only ever handed a
        // DefaultConfig, but that is a property of a call site rather than a guarantee, and the whole cost of
        // being wrong lands on someone who was not using either feature.
        var wantsBackgroundTasks =
            options.StableBalance is not null || options.Network is Network.Mainnet;

        if (wantsBackgroundTasks && !config.backgroundTasksEnabled)
        {
            throw new SparkBackgroundTasksRequiredException(
                options.StableBalance is not null ? "Stable Balance" : "Cross-chain sending");
        }

        // 2. Stable Balance.
        //
        // Mainnet only. The SDK *accepts* a stable-balance config on regtest and then never converts, because
        // USDB does not exist there — a silent no-op is worse than a refusal, so it is not configured at all.
        //
        // defaultActiveLabel is deliberately null: it seeds the first run only, and a cached user setting takes
        // precedence over it forever after, so driving activation through the config would work exactly once.
        // Activation goes through UpdateUserSettings instead.
        if (options.StableBalance is { } stableBalance && options.Network is Network.Mainnet)
        {
            config = config with
            {
                stableBalanceConfig = new StableBalanceConfig(
                    [new StableBalanceToken(stableBalance.Label, stableBalance.Token.Value)],
                    defaultActiveLabel: null,
                    thresholdSats: stableBalance.ThresholdSats is > 0 and var threshold ? (ulong)threshold : null,
                    maxSlippageBps: stableBalance.MaxSlippageBps)
            };
        }

        // 3. Cross-chain.
        //
        // Set unconditionally on mainnet, whether or not any store sweeps cross-chain today, because leaving it
        // null does not disable the feature cleanly — it makes GetCrossChainRoutes return an EMPTY ARRAY WITH NO
        // ERROR. The spike watched the identical call go from 0 routes to 54 purely by setting this, and lost
        // real time to it. A merchant configuring an EVM destination would be told "no routes to this chain" and
        // would go on trying other chains forever.
        //
        // Hard-gated to mainnet in the other direction: a regtest connect carrying this throws
        // "Cross-chain sends are only available on Mainnet", which would stop the wallet starting at all.
        //
        // defaultSlippageBps is set explicitly rather than inherited. The SDK's fallback is 100 bps, which is
        // 10x Stable Balance's own default on a neighbouring config, and on a $35 sweep is up to $0.35 of
        // tolerated slippage.
        if (options.Network is Network.Mainnet)
        {
            config = config with
            {
                crossChainConfig = new CrossChainConfig(
                    defaultSlippageBps: SweepSettings.DefaultCrossChainSlippageBps,
                    defaultTargetOverpayBps: null)
            };
        }

        return config;
    }

    /// <summary>
    /// How often a wallet on a private network polls. Seconds rather than the SDK's 60, because a local
    /// stack mines on demand: a test that waits a minute for the next sync is a test that times out.
    /// </summary>
    internal const uint CustomNetworkSyncIntervalSecs = 2;

    /// <summary>
    /// Repoints the config at a privately hosted Spark network, or returns it untouched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Internal and static for the same reason as <see cref="ApplyPostMvpConfig"/>: these are the settings
    /// that decide whether a connect talks to the local stack or quietly keeps talking to Lightspark's, and
    /// that distinction is invisible from a connected client.
    /// </para>
    /// <para>
    /// Mirrors the Spark reference regtest client. The toggles are not tuning — each of them names a hosted
    /// service that does not exist on a private network, and every one left at its default is a background
    /// task retrying against the internet for the life of the wallet.
    /// </para>
    /// </remarks>
    internal static Config ApplyCustomNetwork(Config config, SparkConnectOptions options, ILogger logger)
    {
        if (options.CustomNetwork is not { } network)
        {
            return config;
        }

        // Refused rather than ignored on mainnet. A custom signing set is exactly the shape of a wallet
        // takeover — real funds, somebody else's operators — so the mismatch has to stop the connect.
        if (options.Network is not Network.Regtest)
        {
            throw new ArgumentException(
                $"A custom Spark network is only supported on {Network.Regtest}, not {options.Network}.",
                nameof(options));
        }

        // DefaultConfig carries a sparkConfig on both networks today; asserted rather than assumed, because
        // the fields kept below are withdraw parameters, and inventing values for those would be worse than
        // failing to start.
        if (config.sparkConfig is not { } sparkConfig)
        {
            throw new InvalidOperationException(
                "The Spark SDK's default config has no sparkConfig to base a custom network on.");
        }

        var operators = new SparkSigningOperator[network.Operators.Count];
        for (var index = 0; index < operators.Length; index++)
        {
            var op = network.Operators[index];
            operators[index] = new SparkSigningOperator(
                op.Id, op.Identifier, op.Address, op.IdentityPublicKey, op.CaCertPem);
        }

        config = config with
        {
            // Patched rather than constructed: expectedWithdrawBondSats and
            // expectedWithdrawRelativeBlockLocktime are protocol parameters the local stack shares with the
            // hosted one, and a cooperative exit quoted against the wrong bond is rejected by the operators.
            sparkConfig = sparkConfig with
            {
                coordinatorIdentifier = network.CoordinatorIdentifier,
                threshold = network.Threshold,
                signingOperators = operators,
                sspConfig = new SparkSspConfig(
                    network.Ssp.BaseUrl, network.Ssp.IdentityPublicKey, network.Ssp.SchemaEndpoint)
            },

            // Every hosted Breez service is switched off by name.
            //
            // apiKey and lnurlDomain address Breez infrastructure that knows nothing about this wallet, so a
            // key here is at best inert. realTimeSyncServerUrl is the one that matters: left at its default
            // the wallet keeps pushing its state to Breez's datasync server — a real endpoint, reachable from
            // CI — which is both an outbound leak from a supposedly private network and a source of sync
            // failures that look like local-stack faults. useDefaultExternalInputParsers pulls its parser
            // list over the network at connect time, from a service the stack does not have.
            apiKey = null,
            lnurlDomain = null,
            realTimeSyncServerUrl = null,
            useDefaultExternalInputParsers = false,

            // Routing and privacy defaults, pinned so the tests assert the SDK's behaviour and not the
            // release's choice of default: sends go over Lightning (which is what the fixture's LND and CLN
            // nodes are there to exercise), and transfers stay private.
            preferSparkOverLightning = false,
            privateEnabledDefault = true,

            syncIntervalSecs = CustomNetworkSyncIntervalSecs
        };

        // Leaf and token optimization run as background tasks that talk to the SSP on a timer. On a stack
        // that is torn down minutes after it is built they buy nothing, and their traffic interleaves with
        // whatever a test is asserting. Patched rather than replaced so that the multiplicity and output
        // thresholds stay whatever the SDK version ships.
        if (config.leafOptimizationConfig is { } leafOptimization)
        {
            config = config with
            {
                leafOptimizationConfig = leafOptimization with { autoEnabled = false }
            };
        }

        if (config.tokenOptimizationConfig is { } tokenOptimization)
        {
            config = config with
            {
                tokenOptimizationConfig = tokenOptimization with { autoEnabled = false }
            };
        }

        // backgroundTasksEnabled is deliberately left alone: the plugin's whole settlement path is the
        // in-process event stream those tasks feed, so a local-regtest run with them off would exercise a
        // wallet the plugin never ships. maxDepositClaimFee is likewise left as ApplyPostMvpConfig set it —
        // the reference client disables automatic claiming and claims by hand, which is the opposite of what
        // these tests exist to cover.

        logger.LogDebug(
            "Store {StoreId}: using a custom Spark network with {OperatorCount} operator(s) "
            + "(threshold {Threshold}), SSP {SspBaseUrl}, chain source {EsploraUrl}",
            options.StoreId, operators.Length, network.Threshold, network.Ssp.BaseUrl, network.EsploraUrl);

        return config;
    }

    private static MaxFee ToSdkMaxFee(SparkMaxFee maxFee) => maxFee switch
    {
        SparkMaxFee.Fixed fixedFee => new MaxFee.Fixed((ulong)Math.Max(0, fixedFee.Sats)),
        SparkMaxFee.Rate rate => new MaxFee.Rate((ulong)Math.Max(0, rate.SatPerVbyte)),
        SparkMaxFee.NetworkRecommended recommended =>
            new MaxFee.NetworkRecommended((ulong)Math.Max(0, recommended.LeewaySatPerVbyte)),
        _ => throw new ArgumentOutOfRangeException(nameof(maxFee), maxFee, "Unknown deposit claim fee policy.")
    };
}
