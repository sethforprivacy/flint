using System.Collections.Concurrent;
using System.Threading.Channels;
using BTCPayServer.Plugins.Flint.Sdk;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// An <see cref="ISparkSdkClientFactory"/> whose connect can be made to hang, fail, or succeed per store.
/// </summary>
/// <remarks>
/// <para>
/// <b>A hanging connect is modelled as a task that never completes and cannot be cancelled</b>, because that
/// is the only faithful model of the SDK: no <c>IBreezSdk</c> method takes a <see cref="CancellationToken"/>
/// and none can be aborted. A fake that honoured the token would let a caller "fix" the hazard by passing a
/// cancellation it does not actually have, and the test would pass against code that still hangs in
/// production.
/// </para>
/// <para>
/// <see cref="Release"/> then completes a hung connect, so a test can also prove what happens to a wallet that
/// arrives after its deadline: the plugin must not silently leave it running.
/// </para>
/// </remarks>
public sealed class FakeSparkSdkClientFactory : ISparkSdkClientFactory
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ISparkSdkClient>> _hung = new();

    /// <summary>Stores whose connect never completes on its own.</summary>
    public HashSet<string> HangFor { get; } = [];

    /// <summary>Stores whose connect throws.</summary>
    public Dictionary<string, Exception> FailFor { get; } = [];

    /// <summary>Every store a connect was attempted for, in order.</summary>
    public List<string> Connects { get; } = [];

    /// <summary>
    /// The options each store's most recent connect was made with.
    /// </summary>
    /// <remarks>
    /// Recorded because several of the connect options are decisions <see cref="Services.SparkService"/> makes
    /// and nothing downstream would reveal — the custom-network descriptor most of all, whose whole point is
    /// that it is null on mainnet whatever the environment says. Holding the object is safe here and nowhere
    /// else: it carries the mnemonic, and <see cref="SparkConnectOptions.ToString"/> is suppressed for that
    /// reason, so a test must never print one.
    /// </remarks>
    public ConcurrentDictionary<string, SparkConnectOptions> Options { get; } = new();

    /// <summary>The clients handed out, by store. A hung store appears only once it is released.</summary>
    public ConcurrentDictionary<string, FakeSparkSdkClient> Clients { get; } = new();

    /// <summary>The event writers handed to each connect, so a test can prove the channel was completed.</summary>
    public ConcurrentDictionary<string, ChannelWriter<SparkEventEnvelope>> EventWriters { get; } = new();

    public Task<ISparkSdkClient> ConnectAsync(
        SparkConnectOptions options,
        ChannelWriter<SparkEventEnvelope> eventWriter,
        CancellationToken cancellationToken = default)
    {
        lock (Connects)
            Connects.Add(options.StoreId);
        Options[options.StoreId] = options;
        EventWriters[options.StoreId] = eventWriter;

        if (FailFor.TryGetValue(options.StoreId, out var failure))
            return Task.FromException<ISparkSdkClient>(failure);

        if (HangFor.Contains(options.StoreId))
        {
            // Deliberately not linked to cancellationToken: see the class remarks.
            return _hung
                .GetOrAdd(options.StoreId,
                    _ => new TaskCompletionSource<ISparkSdkClient>(TaskCreationOptions.RunContinuationsAsynchronously))
                .Task;
        }

        var client = new FakeSparkSdkClient();
        Clients[options.StoreId] = client;
        return Task.FromResult<ISparkSdkClient>(client);
    }

    /// <summary>Completes a connect that was hung, as the SDK eventually would. Returns the client it produced.</summary>
    public FakeSparkSdkClient Release(string storeId)
    {
        var client = new FakeSparkSdkClient();
        Clients[storeId] = client;

        var pending = _hung.GetOrAdd(storeId,
            _ => new TaskCompletionSource<ISparkSdkClient>(TaskCreationOptions.RunContinuationsAsynchronously));
        pending.TrySetResult(client);
        return client;
    }
}
