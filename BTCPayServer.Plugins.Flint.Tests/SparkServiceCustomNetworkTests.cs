using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The one production read of <c>SPARK_LOCAL_REGTEST_NETWORK</c>: <c>SparkService</c> hands a locally hosted
/// signing set to the SDK on regtest, and refuses to look at the variable anywhere else.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam the <c>BtcpayE2E</c> suite stands on. Without it, a BTCPay Server running beside the local
/// Spark stack would connect every store to Spark's own hosted regtest, the wallet would never see the money
/// the fixture sent it, and the whole suite would fail as an unexplained funding timeout. With it, the e2e
/// tests observe the plugin's real store-connect path.
/// </para>
/// <para>
/// The mainnet half is the one that matters for safety, and it is asserted against the environment variable
/// <em>set</em>. Merely observing that mainnet passes null with the variable unset would prove nothing: it is
/// null then either way. <see cref="SparkSdkClientFactory.ApplyCustomNetwork"/> refuses a custom network off
/// regtest as a second, independent guard — <see cref="SparkCustomNetworkTests"/> holds that end — but a
/// belt-and-braces pair is only worth having if both halves are actually tested.
/// </para>
/// </remarks>
[Collection(SparkNetworkEnvironmentCollection.Name)]
public class SparkServiceCustomNetworkTests
{
    private const string StoreId = "store-on-a-local-stack";

    /// <summary>
    /// A descriptor good enough for the loader, which validates every field it models.
    /// </summary>
    /// <remarks>
    /// Two operators at a threshold of two rather than the fixture's three, because the loader's only rule
    /// about the count is that the threshold fits inside it, and a shorter document is easier to read. The
    /// certificate is a stub: nothing here performs a TLS handshake, and the loader asks only that the field is
    /// non-blank.
    /// </remarks>
    private const string Descriptor = """
        {
          "coordinatorIdentifier": "0000000000000000000000000000000000000000000000000000000000000001",
          "threshold": 2,
          "operators": [
            {
              "id": 0,
              "identifier": "0000000000000000000000000000000000000000000000000000000000000001",
              "address": "https://localhost:8535",
              "identityPublicKey": "03dfbdff4b6332c220f8fa2ba8ed496c698ceada563fa01b67d9983bfc5c95e763",
              "caCertPem": "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n"
            },
            {
              "id": 1,
              "identifier": "0000000000000000000000000000000000000000000000000000000000000002",
              "address": "https://localhost:8536",
              "identityPublicKey": "03e625e9768651c9be268e287245cc33f96a68ce9141b0b4769205db027ee8ed77",
              "caCertPem": "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n"
            }
          ],
          "ssp": {
            "baseUrl": "http://127.0.0.1:5000",
            "identityPublicKey": "0322ca18fc0f26c1a1e6e0f7a4a8f6f4c0e2c1a5b6d7e8f9a0b1c2d3e4f5a6b7c8",
            "schemaEndpoint": "graphql/spark/rc"
          },
          "esploraUrl": "http://127.0.0.1:30000",
          "fixture": {
            "bitcoindContainer": "cashu-bitcoind-1",
            "bitcoindRpcUser": "cashu",
            "bitcoindRpcPassword": "cashu",
            "lndContainer": "cashu-lnd-1-1"
          }
        }
        """;

    [Fact]
    public async Task A_regtest_store_connects_to_the_network_the_descriptor_names()
    {
        using var descriptor = new TemporaryDescriptor(Descriptor);
        using var scope = SparkNetworkEnvironment.PointedAt(descriptor.Path);

        using var harness = SparkServiceHarness.Create();
        harness.SeedStore(StoreId, SparkServiceHarness.MnemonicFor(1));

        await harness.Service.StartAsync(TestContext.Current.CancellationToken);

        var options = Assert.Contains(StoreId, harness.Sdk.Options);
        var network = Assert.IsType<SparkCustomNetwork>(options.CustomNetwork);

        // Asserted field by field rather than as "not null": the failure this guards against is a descriptor
        // that was read and then not carried through, which a null check would catch, and one that was read
        // from a *stale* file, which it would not.
        Assert.Equal(
            "0000000000000000000000000000000000000000000000000000000000000001",
            network.CoordinatorIdentifier);
        Assert.Equal(2u, network.Threshold);
        Assert.Equal(2, network.Operators.Count);
        Assert.Equal("https://localhost:8535", network.Operators[0].Address);
        Assert.Equal("http://127.0.0.1:5000", network.Ssp.BaseUrl);
        Assert.Equal("http://127.0.0.1:30000", network.EsploraUrl);

        // Reported once, at Information, and naming the variable — so the log of a run that went wrong answers
        // "which Spark network was this server actually on?" without anyone having to guess.
        var reported = harness.Log.Lines
            .Where(line => line.Contains(SparkCustomNetworkFile.EnvironmentVariable, StringComparison.Ordinal))
            .ToList();
        Assert.Single(reported);
        Assert.Contains("http://127.0.0.1:5000", reported[0]);
    }

    /// <summary>
    /// On mainnet the variable is not read at all, so a mainnet store gets the SDK's own network.
    /// </summary>
    /// <remarks>
    /// The variable is deliberately set for the duration of this test, pointing at a descriptor that parses.
    /// Everything a merchant's money depends on is on the other side of this assertion: a mainnet wallet
    /// connected to somebody else's signing set is not a misconfiguration, it is a wallet whose keys are
    /// shared with whoever runs that stack.
    /// </remarks>
    [Fact]
    public async Task A_mainnet_store_ignores_the_descriptor_even_when_the_variable_is_set()
    {
        using var descriptor = new TemporaryDescriptor(Descriptor);
        using var scope = SparkNetworkEnvironment.PointedAt(descriptor.Path);

        using var harness = SparkServiceHarness.Create(chain: ChainName.Mainnet);
        harness.SeedStore(StoreId, SparkServiceHarness.MnemonicFor(2));

        await harness.Service.StartAsync(TestContext.Current.CancellationToken);

        var options = Assert.Contains(StoreId, harness.Sdk.Options);
        Assert.Null(options.CustomNetwork);
        Assert.Equal(Breez.Sdk.Spark.Network.Mainnet, options.Network);

        // And nothing was said about it either, because nothing looked.
        Assert.DoesNotContain(
            harness.Log.Lines,
            line => line.Contains(SparkCustomNetworkFile.EnvironmentVariable, StringComparison.Ordinal));
    }

    /// <summary>
    /// A descriptor that cannot be read refuses to start the wallet rather than falling back silently.
    /// </summary>
    /// <remarks>
    /// The silent fallback is the worse outcome by a distance: the store would come up healthy against Spark's
    /// hosted regtest, and the e2e suite would spend ten minutes waiting for money that was sent to a wallet
    /// on a different network. A refusal names the variable in the log and leaves the store visibly down.
    /// </remarks>
    [Fact]
    public async Task An_unreadable_descriptor_stops_the_store_rather_than_falling_back()
    {
        using var descriptor = new TemporaryDescriptor("{ \"threshold\": 2 }");
        using var scope = SparkNetworkEnvironment.PointedAt(descriptor.Path);

        using var harness = SparkServiceHarness.Create();
        harness.SeedStore(StoreId, SparkServiceHarness.MnemonicFor(3));

        await harness.Service.StartAsync(TestContext.Current.CancellationToken);

        // No connect was attempted at all: the descriptor is resolved before the storage claim is taken, so a
        // broken one costs nothing and cannot leave a lock behind.
        Assert.DoesNotContain(StoreId, harness.Sdk.Connects);
        Assert.Contains(
            harness.Log.Lines,
            line => line.Contains(SparkCustomNetworkFile.EnvironmentVariable, StringComparison.Ordinal)
                    && line.Contains("write-network.sh", StringComparison.Ordinal));
    }

    /// <summary>A descriptor on disk, deleted with the test.</summary>
    private sealed class TemporaryDescriptor : IDisposable
    {
        public TemporaryDescriptor(string json)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"spark-network-{Guid.NewGuid():N}.json");
            File.WriteAllText(Path, json);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>
/// Sets <c>SPARK_LOCAL_REGTEST_NETWORK</c> for the life of a test and puts it back afterwards.
/// </summary>
/// <remarks>
/// Environment variables are process-global, which is why every test that touches this one belongs to
/// <see cref="SparkNetworkEnvironmentCollection"/> and runs alone. The restore is unconditional: a test that
/// left the variable set would make the entire local-regtest suite stop skipping and then fail on a
/// descriptor that no longer exists.
/// </remarks>
internal sealed class SparkNetworkEnvironment : IDisposable
{
    private readonly string? _original;

    private SparkNetworkEnvironment(string? original) => _original = original;

    public static SparkNetworkEnvironment PointedAt(string? path)
    {
        var original = Environment.GetEnvironmentVariable(SparkCustomNetworkFile.EnvironmentVariable);
        Environment.SetEnvironmentVariable(SparkCustomNetworkFile.EnvironmentVariable, path);
        return new SparkNetworkEnvironment(original);
    }

    public void Dispose() =>
        Environment.SetEnvironmentVariable(SparkCustomNetworkFile.EnvironmentVariable, _original);
}

/// <summary>
/// The collection every test that writes <c>SPARK_LOCAL_REGTEST_NETWORK</c> joins.
/// </summary>
/// <remarks>
/// xunit runs separate test classes in parallel by default, and an environment variable is per process — so
/// without this, a class that sets the variable and a class that clears it can and do interleave. The
/// collection has no fixture: serialising the classes is the whole of what it is for.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SparkNetworkEnvironmentCollection
{
    public const string Name = "Spark network environment variable";
}
