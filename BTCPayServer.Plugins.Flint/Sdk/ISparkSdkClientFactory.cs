using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SdkNetwork = Breez.Sdk.Spark.Network;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>
/// Everything needed to bring one store's SDK instance up.
/// </summary>
/// <remarks>
/// A class with a suppressed <see cref="ToString"/>, not a record. A record's generated <c>ToString</c> prints
/// every property, so any log line, exception message or debugger view that formatted one would print the
/// merchant's mnemonic in clear text.
/// </remarks>
public sealed class SparkConnectOptions
{
    public SparkConnectOptions(
        string storeId,
        string mnemonic,
        string? passphrase,
        string? apiKey,
        SdkNetwork network,
        SparkMaxFee? maxDepositClaimFee = null,
        SparkStableBalanceConfiguration? stableBalance = null,
        SparkCustomNetwork? customNetwork = null)
    {
        StoreId = storeId;
        Mnemonic = mnemonic;
        Passphrase = passphrase;
        ApiKey = apiKey;
        Network = network;
        MaxDepositClaimFee = maxDepositClaimFee;
        StableBalance = stableBalance;
        CustomNetwork = customNetwork;
    }

    public string StoreId { get; }

    /// <summary>The BIP39 mnemonic. Never log this, and never put it in a message.</summary>
    public string Mnemonic { get; }

    public string? Passphrase { get; }

    /// <summary>
    /// Null is accepted by the SDK and works on regtest; mainnet needs a real key. The key is not validated at
    /// connect time — nor at all on regtest — so a bad key surfaces later, on the first synced call.
    /// </summary>
    public string? ApiKey { get; }

    public SdkNetwork Network { get; }

    /// <summary>
    /// The cap on what the SDK may spend claiming an on-chain deposit. Null leaves the SDK's own default in
    /// place, which is <c>Rate(1 sat/vB)</c> and strands mainnet deposits — so the plugin always supplies one.
    /// </summary>
    public SparkMaxFee? MaxDepositClaimFee { get; }

    /// <summary>
    /// The wallet's stable-balance token list, or null to leave the feature unconfigured.
    /// </summary>
    /// <remarks>
    /// Configuring it does <b>not</b> activate it: the config only tells the wallet the token exists, and
    /// activation is a separate <c>UpdateUserSettings</c> call. That split is deliberate — a cached active
    /// label takes precedence over the config's own default, so driving activation through the config would
    /// work once and then silently stop.
    /// </remarks>
    public SparkStableBalanceConfiguration? StableBalance { get; }

    /// <summary>
    /// A privately hosted Spark network to connect to instead of the one the SDK ships, or null for the
    /// SDK's own. Legal only with <see cref="SdkNetwork.Regtest"/>.
    /// </summary>
    /// <remarks>
    /// Exists for the local-regtest test suite, and has no merchant-facing path into it — see
    /// <see cref="SparkCustomNetwork"/> for why that matters. Setting it replaces the whole signing set,
    /// so a wallet created against one network cannot be read on another.
    /// </remarks>
    public SparkCustomNetwork? CustomNetwork { get; }

    /// <summary>Deliberately says nothing: this object holds seed material.</summary>
    public override string ToString() => $"{nameof(SparkConnectOptions)}({StoreId}, {Network})";
}

/// <summary>
/// The one token a store's wallet is configured to hold a stable balance in.
/// </summary>
/// <param name="Label">
/// The plugin-chosen display label activation addresses this token by. Not a ticker and not the identifier.
/// </param>
/// <param name="ThresholdSats">
/// Balance at which the SDK auto-converts. Null leaves the service's minimum in force; a value below that
/// minimum is clamped up to it rather than honoured.
/// </param>
public sealed record SparkStableBalanceConfiguration(
    SparkTokenIdentifier Token,
    string Label,
    uint MaxSlippageBps,
    long? ThresholdSats);

/// <summary>
/// Creates connected <see cref="ISparkSdkClient"/> instances.
/// </summary>
/// <remarks>
/// <para>
/// <c>Connect</c> is cheap (~40 ms cold, ~5 ms warm) and does <b>no</b> network I/O and no credential
/// validation: it succeeds against a dead SSP, a bad API key, or no internet at all. A returned client
/// therefore is not evidence that the store is healthy — that requires a synced call.
/// </para>
/// <para>
/// Nothing in the SDK prevents two live instances sharing one wallet and one non-WAL SQLite file, and
/// doing so was observed to succeed in 6 ms. Single-flight per store is the caller's responsibility;
/// <c>SparkService</c> holds that lock.
/// </para>
/// </remarks>
public interface ISparkSdkClientFactory
{
    /// <param name="eventWriter">
    /// Bounded channel the instance's event listener writes to. See
    /// <see cref="SparkEventListenerAdapter"/> for why this must be a channel and not a callback.
    /// </param>
    Task<ISparkSdkClient> ConnectAsync(
        SparkConnectOptions options,
        ChannelWriter<SparkEventEnvelope> eventWriter,
        CancellationToken cancellationToken = default);
}
