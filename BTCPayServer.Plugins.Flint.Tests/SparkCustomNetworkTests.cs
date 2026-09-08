using Breez.Sdk.Spark;
using BTCPayServer.Plugins.Flint.Sdk;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The local-regtest network descriptor and the SDK config it produces.
/// </summary>
/// <remarks>
/// Both halves are asserted against real values rather than shapes. A descriptor that parses into a
/// half-formed signing set, or a config whose sparkConfig was built and then not applied, produces a connect
/// that succeeds and goes on talking to Lightspark's hosted regtest — which is the failure mode these tests
/// exist to make impossible, because a green local-regtest run against the hosted network proves nothing.
/// </remarks>
public class SparkCustomNetworkTests
{
    private const string FirstIdentifier =
        "0000000000000000000000000000000000000000000000000000000000000001";

    private const string SecondIdentifier =
        "0000000000000000000000000000000000000000000000000000000000000002";

    private const string CaCertPem = "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n";

    private const string SspIdentityPublicKey =
        "0322ca18fc0f26c1a1e6e0f7a4a8f6f4c0e2c1a5b6d7e8f9a0b1c2d3e4f5a6b7c8";

    [Fact]
    public void A_descriptor_parses_into_the_network_it_describes()
    {
        var network = SparkCustomNetworkFile.Parse(Descriptor());

        Assert.Equal(FirstIdentifier, network.CoordinatorIdentifier);
        Assert.Equal(2u, network.Threshold);
        Assert.Equal("http://127.0.0.1:30000", network.EsploraUrl);
        Assert.Equal("http://127.0.0.1:5000", network.Ssp.BaseUrl);
        Assert.Equal(SspIdentityPublicKey, network.Ssp.IdentityPublicKey);
        Assert.Equal("graphql/spark/rc", network.Ssp.SchemaEndpoint);

        Assert.Collection(
            network.Operators,
            first =>
            {
                Assert.Equal(0u, first.Id);
                Assert.Equal(FirstIdentifier, first.Identifier);
                Assert.Equal("https://localhost:8535", first.Address);
                Assert.Equal(CaCertPem, first.CaCertPem);
            },
            second =>
            {
                Assert.Equal(1u, second.Id);
                Assert.Equal(SecondIdentifier, second.Identifier);
                Assert.Equal("https://localhost:8536", second.Address);
                // Absent rather than empty: null is what tells the SDK to use the machine trust store.
                Assert.Null(second.CaCertPem);
            });
    }

    /// <summary>
    /// The <c>fixture</c> object is the test suite's half of the file, and is ignored rather than rejected.
    /// </summary>
    /// <remarks>
    /// Stated as its own test because the two halves evolve independently: the fixture scripts grow
    /// container names and credentials as the stack does, and none of that may make the plugin refuse a file
    /// it can otherwise read.
    /// </remarks>
    [Fact]
    public void Fixture_details_and_unknown_properties_are_ignored()
    {
        var json = Descriptor(extra: "\"somethingTheFixtureAddedLater\": { \"nested\": [1, 2, 3] },");

        var network = SparkCustomNetworkFile.Parse(json);

        Assert.Equal(2, network.Operators.Count);
    }

    [Fact]
    public void A_missing_coordinator_identifier_is_named()
    {
        var error = Assert.Throws<FormatException>(
            () => SparkCustomNetworkFile.Parse(Descriptor(coordinator: null)));

        Assert.Contains("missing coordinatorIdentifier", error.Message);
    }

    /// <summary>
    /// Identifiers must be 64 hex characters.
    /// </summary>
    /// <remarks>
    /// The SDK parses them as 32-byte keys, so a short or non-hex one is a signing failure several seconds
    /// into a connect that names neither the operator nor the field.
    /// </remarks>
    [Fact]
    public void A_coordinator_identifier_that_is_not_64_hex_is_rejected()
    {
        var error = Assert.Throws<FormatException>(
            () => SparkCustomNetworkFile.Parse(Descriptor(coordinator: "00")));

        Assert.Contains("coordinatorIdentifier", error.Message);
        Assert.Contains("64 hex characters", error.Message);
    }

    [Fact]
    public void An_operator_identifier_that_is_not_64_hex_names_its_index()
    {
        var error = Assert.Throws<FormatException>(
            () => SparkCustomNetworkFile.Parse(Descriptor(secondIdentifier: "not-hex")));

        Assert.Contains("operator 1's identifier", error.Message);
        Assert.Contains("64 hex characters", error.Message);
    }

    /// <summary>
    /// A threshold above the size of the signing set can never be met.
    /// </summary>
    /// <remarks>
    /// Checked here because the SDK does not check it, and what it produces instead is a wallet that
    /// connects and then fails every signing operation.
    /// </remarks>
    [Fact]
    public void A_threshold_above_the_operator_count_is_rejected()
    {
        var error = Assert.Throws<FormatException>(
            () => SparkCustomNetworkFile.Parse(Descriptor(threshold: "4")));

        Assert.Contains("threshold (4)", error.Message);
        Assert.Contains("2 operator(s)", error.Message);
    }

    [Fact]
    public void A_zero_or_missing_threshold_is_rejected()
    {
        foreach (var threshold in new[] { "0", "null" })
        {
            var error = Assert.Throws<FormatException>(
                () => SparkCustomNetworkFile.Parse(Descriptor(threshold: threshold)));

            Assert.Contains("at least 1", error.Message);
        }
    }

    [Fact]
    public void An_empty_operator_list_is_rejected()
    {
        var error = Assert.Throws<FormatException>(
            () => SparkCustomNetworkFile.Parse(Descriptor(operators: "[]")));

        Assert.Contains("no operators", error.Message);
    }

    /// <summary>
    /// Operator addresses must be https.
    /// </summary>
    /// <remarks>
    /// The SDK speaks gRPC over TLS to them and nothing else, so a plain-http address is not a weaker
    /// configuration, it is a connect that never completes.
    /// </remarks>
    [Fact]
    public void A_plain_http_operator_address_is_rejected()
    {
        var error = Assert.Throws<FormatException>(
            () => SparkCustomNetworkFile.Parse(Descriptor(secondAddress: "http://localhost:8536")));

        Assert.Contains("operator 1's address", error.Message);
        Assert.Contains("https URL", error.Message);
    }

    [Fact]
    public void A_missing_operator_address_names_its_index()
    {
        var error = Assert.Throws<FormatException>(
            () => SparkCustomNetworkFile.Parse(Descriptor(secondAddress: "")));

        Assert.Contains("operator 1's address", error.Message);
    }

    [Fact]
    public void An_ssp_without_an_identity_key_is_rejected()
    {
        var error = Assert.Throws<FormatException>(
            () => SparkCustomNetworkFile.Parse(Descriptor(sspIdentityPublicKey: "")));

        Assert.Contains("ssp identityPublicKey", error.Message);
    }

    [Fact]
    public void An_ssp_without_a_base_url_is_rejected()
    {
        var error = Assert.Throws<FormatException>(
            () => SparkCustomNetworkFile.Parse(Descriptor(sspBaseUrl: "")));

        Assert.Contains("ssp baseUrl", error.Message);
    }

    [Fact]
    public void A_relative_esplora_url_is_rejected()
    {
        var error = Assert.Throws<FormatException>(
            () => SparkCustomNetworkFile.Parse(Descriptor(esploraUrl: "127.0.0.1:30000")));

        Assert.Contains("esploraUrl", error.Message);
        Assert.Contains("absolute http(s) URL", error.Message);
    }

    [Fact]
    public void An_optional_schema_endpoint_may_be_absent()
    {
        var network = SparkCustomNetworkFile.Parse(Descriptor(sspSchemaEndpoint: null));

        // Null, not empty: an empty schema endpoint is a 404 on every SSP call, where null is the SDK's own.
        Assert.Null(network.Ssp.SchemaEndpoint);
    }

    [Fact]
    public void Malformed_json_is_reported_as_such()
    {
        var error = Assert.Throws<FormatException>(() => SparkCustomNetworkFile.Parse("{ not json"));

        Assert.Contains("not valid JSON", error.Message);
    }

    [Fact]
    public void Load_round_trips_a_descriptor_from_disk_and_names_a_bad_one()
    {
        var path = Path.Combine(Path.GetTempPath(), $"spark-network-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, Descriptor());
            Assert.Equal(FirstIdentifier, SparkCustomNetworkFile.Load(path).CoordinatorIdentifier);

            // The path is the only part of a descriptor failure the caller did not supply, and in CI it is
            // the difference between "the descriptor is wrong" and "a stale descriptor is being read".
            File.WriteAllText(path, Descriptor(threshold: "9"));
            var error = Assert.Throws<FormatException>(() => SparkCustomNetworkFile.Load(path));
            Assert.Contains(path, error.Message);
            Assert.Contains("2 operator(s)", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// An unset variable reads as "no local stack", which is what the opt-in suite skips on.
    /// </summary>
    /// <remarks>
    /// Blank counts as unset. A shell that exports the variable empty — which is what an unsubstituted
    /// workflow expression produces — would otherwise fail the whole suite on a missing file rather than
    /// skipping it, and a skip is the honest outcome when no stack was started.
    /// </remarks>
    [Fact]
    public void An_unset_environment_variable_yields_no_network()
    {
        var original = Environment.GetEnvironmentVariable(SparkCustomNetworkFile.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(SparkCustomNetworkFile.EnvironmentVariable, null);
            Assert.Null(SparkCustomNetworkFile.TryLoadFromEnvironment());

            Environment.SetEnvironmentVariable(SparkCustomNetworkFile.EnvironmentVariable, "   ");
            Assert.Null(SparkCustomNetworkFile.TryLoadFromEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable(SparkCustomNetworkFile.EnvironmentVariable, original);
        }
    }

    [Fact]
    public void The_custom_network_replaces_the_signing_set_and_the_SSP()
    {
        var network = SparkCustomNetworkFile.Parse(Descriptor());

        var applied = SparkSdkClientFactory.ApplyCustomNetwork(
            BreezSdkSparkMethods.DefaultConfig(Network.Regtest), Options(network), NullLogger.Instance);

        var sparkConfig = applied.sparkConfig;
        Assert.NotNull(sparkConfig);
        Assert.Equal(FirstIdentifier, sparkConfig.coordinatorIdentifier);
        Assert.Equal(2u, sparkConfig.threshold);
        Assert.Equal("http://127.0.0.1:5000", sparkConfig.sspConfig?.baseUrl);
        Assert.Equal(SspIdentityPublicKey, sparkConfig.sspConfig?.identityPublicKey);
        Assert.Equal("graphql/spark/rc", sparkConfig.sspConfig?.schemaEndpoint);

        Assert.Collection(
            sparkConfig.signingOperators,
            first =>
            {
                Assert.Equal(0u, first.id);
                Assert.Equal("https://localhost:8535", first.address);
                Assert.Equal(CaCertPem, first.caCertPem);
            },
            second =>
            {
                Assert.Equal(1u, second.id);
                Assert.Equal("https://localhost:8536", second.address);
                Assert.Null(second.caCertPem);
            });

        // Not one operator of the hosted network survives. Asserted separately because a partially replaced
        // signing set is the failure that still connects.
        Assert.DoesNotContain(
            sparkConfig.signingOperators,
            op => op.address.Contains("lightspark.com") || op.address.Contains("breez.technology"));
    }

    [Fact]
    public void The_withdraw_parameters_of_the_default_config_are_preserved()
    {
        var defaults = BreezSdkSparkMethods.DefaultConfig(Network.Regtest);
        var expected = defaults.sparkConfig;
        Assert.NotNull(expected);

        var applied = SparkSdkClientFactory.ApplyCustomNetwork(
            defaults, Options(SparkCustomNetworkFile.Parse(Descriptor())), NullLogger.Instance);

        // A cooperative exit quoted against the wrong bond is rejected by the operators, so these are the
        // fields a from-scratch sparkConfig would silently get wrong.
        var sparkConfig = applied.sparkConfig;
        Assert.NotNull(sparkConfig);
        Assert.Equal(expected.expectedWithdrawBondSats, sparkConfig.expectedWithdrawBondSats);
        Assert.Equal(
            expected.expectedWithdrawRelativeBlockLocktime,
            sparkConfig.expectedWithdrawRelativeBlockLocktime);
        Assert.Equal(expected.maxTokenTransactionInputs, sparkConfig.maxTokenTransactionInputs);
        Assert.Equal(defaults.maxConcurrentClaims, applied.maxConcurrentClaims);
        Assert.Equal(defaults.maxDepositClaimFee, applied.maxDepositClaimFee);
    }

    [Fact]
    public void Every_hosted_Breez_service_is_switched_off()
    {
        var defaults = BreezSdkSparkMethods.DefaultConfig(Network.Regtest);

        // Guards the assertions below: were a future SDK to ship these already off, they would pass without
        // the code under test having done anything.
        Assert.NotNull(defaults.realTimeSyncServerUrl);
        Assert.True(defaults.useDefaultExternalInputParsers);
        Assert.Equal(60u, defaults.syncIntervalSecs);

        var applied = SparkSdkClientFactory.ApplyCustomNetwork(
            defaults,
            Options(SparkCustomNetworkFile.Parse(Descriptor()), apiKey: "a-hosted-key"),
            NullLogger.Instance);

        Assert.Null(applied.realTimeSyncServerUrl);
        Assert.Null(applied.apiKey);
        Assert.Null(applied.lnurlDomain);
        Assert.False(applied.useDefaultExternalInputParsers);
        Assert.False(applied.preferSparkOverLightning);
        Assert.True(applied.privateEnabledDefault);
        Assert.Equal(SparkSdkClientFactory.CustomNetworkSyncIntervalSecs, applied.syncIntervalSecs);

        var leafOptimization = applied.leafOptimizationConfig;
        var tokenOptimization = applied.tokenOptimizationConfig;
        Assert.NotNull(leafOptimization);
        Assert.NotNull(tokenOptimization);
        Assert.False(leafOptimization.autoEnabled);
        Assert.False(tokenOptimization.autoEnabled);

        // Patched, not replaced: the thresholds stay whatever this SDK version ships.
        var defaultTokenOptimization = defaults.tokenOptimizationConfig;
        Assert.NotNull(defaultTokenOptimization);
        Assert.Equal(defaultTokenOptimization.minOutputsThreshold, tokenOptimization.minOutputsThreshold);
        Assert.Equal(defaultTokenOptimization.targetOutputCount, tokenOptimization.targetOutputCount);

        // The one toggle where this plugin departs from the reference client: the settlement path is the
        // in-process event stream the background tasks feed, so a run with them off would exercise a wallet
        // that is not the one the plugin ships.
        Assert.True(applied.backgroundTasksEnabled);
    }

    [Fact]
    public void A_connect_without_a_custom_network_is_untouched()
    {
        var defaults = BreezSdkSparkMethods.DefaultConfig(Network.Regtest);

        var applied = SparkSdkClientFactory.ApplyCustomNetwork(
            defaults,
            new SparkConnectOptions("store", "mnemonic", null, "key", Network.Regtest),
            NullLogger.Instance);

        Assert.Equal(defaults, applied);
    }

    /// <summary>
    /// A custom signing set on mainnet stops the connect rather than being ignored.
    /// </summary>
    /// <remarks>
    /// What it guards against is not a misconfiguration but the shape of a wallet takeover: an arbitrary
    /// signing set holding real funds. Dropping the network silently would be the worse of the two outcomes,
    /// because the wallet would then come up looking correct.
    /// </remarks>
    [Fact]
    public void A_custom_network_on_mainnet_is_refused()
    {
        var network = SparkCustomNetworkFile.Parse(Descriptor());

        var error = Assert.Throws<ArgumentException>(() => SparkSdkClientFactory.ApplyCustomNetwork(
            BreezSdkSparkMethods.DefaultConfig(Network.Mainnet),
            Options(network, Network.Mainnet),
            NullLogger.Instance));

        Assert.Contains("only supported on Regtest", error.Message);
    }

    private static SparkConnectOptions Options(
        SparkCustomNetwork network, Network sdkNetwork = Network.Regtest, string? apiKey = null) =>
        new("store", "mnemonic", null, apiKey, sdkNetwork, customNetwork: network);

    /// <summary>
    /// A descriptor in the shape write-network.sh emits, <c>fixture</c> object included, with one field at a
    /// time swapped out for an invalid one.
    /// </summary>
    private static string Descriptor(
        string? coordinator = FirstIdentifier,
        string threshold = "2",
        string? operators = null,
        string secondIdentifier = SecondIdentifier,
        string secondAddress = "https://localhost:8536",
        string sspBaseUrl = "http://127.0.0.1:5000",
        string sspIdentityPublicKey = SspIdentityPublicKey,
        string? sspSchemaEndpoint = "graphql/spark/rc",
        string esploraUrl = "http://127.0.0.1:30000",
        string extra = "") =>
        $$"""
        {
          "coordinatorIdentifier": {{Text(coordinator)}},
          "threshold": {{threshold}},
          "operators": {{operators ?? $$"""
          [
            {
              "id": 0,
              "identifier": "{{FirstIdentifier}}",
              "address": "https://localhost:8535",
              "identityPublicKey": "03dfbdff4b6332c220f8fa2ba8ed496c698ceada563fa01b67d9983bfc5c95e763",
              "caCertPem": "{{CaCertPem.Replace("\n", "\\n")}}"
            },
            {
              "id": 1,
              "identifier": "{{secondIdentifier}}",
              "address": "{{secondAddress}}",
              "identityPublicKey": "03e625e9768651c9be268e287245cc33f96a68ce9141b0b4769205db027ee8ed77"
            }
          ]
          """}},
          "ssp": {
            "baseUrl": "{{sspBaseUrl}}",
            "identityPublicKey": "{{sspIdentityPublicKey}}",
            "schemaEndpoint": {{Text(sspSchemaEndpoint)}}
          },
          "esploraUrl": "{{esploraUrl}}",
          {{extra}}
          "fixture": {
            "bitcoindContainer": "bitcoind-1",
            "bitcoindRpcUser": "cashu",
            "bitcoindRpcPassword": "cashu",
            "lndContainer": "lnd-1",
            "clnContainer": "clightning-1",
            "sspAdminToken": "regtest-spark-admin-token",
            "sspContainer": "spark-ssp"
          }
        }
        """;

    private static string Text(string? value) => value is null ? "null" : $"\"{value}\"";
}
