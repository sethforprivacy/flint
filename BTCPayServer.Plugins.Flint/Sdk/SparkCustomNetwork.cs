using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>
/// One Spark signing operator of a privately hosted Spark network.
/// </summary>
/// <param name="Id">
/// The operator's index in the signing set. It is not decorative: the SDK addresses operators by id, so an
/// id that disagrees with the network's own numbering produces signing failures rather than a config error.
/// </param>
/// <param name="Identifier">
/// The 64-hex operator identifier. On the reference regtest stack these are the hex of <c>id + 1</c>, but
/// nothing here assumes that — only that it is 64 hex characters, which is what the SDK will parse.
/// </param>
/// <param name="CaCertPem">
/// The PEM of the CA that signed this operator's TLS certificate, or null to use the machine trust store.
/// A locally generated stack signs its own certificates, so leaving this null there fails the handshake.
/// </param>
public sealed record SparkCustomNetworkOperator(
    uint Id,
    string Identifier,
    string Address,
    string IdentityPublicKey,
    string? CaCertPem);

/// <summary>
/// The Spark Service Provider of a privately hosted Spark network.
/// </summary>
/// <param name="SchemaEndpoint">
/// The GraphQL path the SSP serves, or null for the SDK's own default. It differs between SSP builds, and a
/// wrong one is a 404 on every SSP call rather than a startup error.
/// </param>
public sealed record SparkCustomNetworkSsp(
    string BaseUrl,
    string IdentityPublicKey,
    string? SchemaEndpoint);

/// <summary>
/// A complete description of a privately hosted Spark network: its signing set, its SSP and its chain source.
/// </summary>
/// <remarks>
/// This exists for one reason — running the plugin's real-SDK tests against a local stack instead of the
/// hosted Lightspark regtest. It is deliberately not reachable from any merchant-facing setting: a merchant
/// who could point the wallet at an arbitrary signing set could be pointed at somebody else's.
/// </remarks>
public sealed record SparkCustomNetwork(
    string CoordinatorIdentifier,
    uint Threshold,
    IReadOnlyList<SparkCustomNetworkOperator> Operators,
    SparkCustomNetworkSsp Ssp,
    string EsploraUrl);

/// <summary>
/// Reads a <see cref="SparkCustomNetwork"/> from the JSON descriptor a local stack's fixture scripts emit.
/// </summary>
/// <remarks>
/// <para>
/// The descriptor is written by <c>e2e/local-regtest/write-network.sh</c> once the stack is up, because most
/// of it cannot be known ahead of time: the SSP's identity pubkey is generated on first boot and the
/// operators' certificates are generated per stack.
/// </para>
/// <para>
/// The file carries a <c>fixture</c> object as well — container names, RPC credentials, the SSP admin token —
/// which the test suite uses to drive bitcoind and the Lightning counterparties over <c>docker exec</c>.
/// That half is none of the plugin's business and is ignored here rather than modelled, so a fixture-side
/// change cannot break the loader.
/// </para>
/// <para>
/// Everything is validated up front. The alternative is handing a half-formed signing set to the SDK, which
/// reports it as a TLS or signing failure several seconds into a connect, naming nothing.
/// </para>
/// </remarks>
public static class SparkCustomNetworkFile
{
    /// <summary>Environment variable holding the path to the descriptor.</summary>
    public const string EnvironmentVariable = "SPARK_LOCAL_REGTEST_NETWORK";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        // Unknown members are ignored (the default) on purpose: `fixture` is one, and so is anything the
        // fixture scripts start emitting for the test suite before the plugin knows about it.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Reads and validates the descriptor at <paramref name="path"/>.</summary>
    /// <exception cref="FormatException">The file is not a valid descriptor.</exception>
    public static SparkCustomNetwork Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var json = File.ReadAllText(path);
        try
        {
            return Parse(json);
        }
        catch (FormatException ex)
        {
            // The path is the only part of the failure the caller did not already supply, and in CI it is
            // the difference between "the descriptor is wrong" and "a stale descriptor is being read".
            throw new FormatException($"{path}: {ex.Message}", ex);
        }
    }

    /// <summary>Validates the descriptor named by <see cref="EnvironmentVariable"/>, or null if unset.</summary>
    /// <remarks>
    /// Returns null for an unset *or blank* variable, so that a shell that exports the variable empty reads
    /// the same as one that never set it. This is what the test suites skip on.
    /// </remarks>
    public static SparkCustomNetwork? TryLoadFromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return string.IsNullOrWhiteSpace(path) ? null : Load(path);
    }

    /// <summary>Parses and validates a descriptor.</summary>
    /// <exception cref="FormatException">The JSON is malformed, or a required field is missing or invalid.</exception>
    public static SparkCustomNetwork Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        Descriptor? descriptor;
        try
        {
            descriptor = JsonSerializer.Deserialize<Descriptor>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"the Spark network descriptor is not valid JSON: {ex.Message}", ex);
        }

        if (descriptor is null)
        {
            throw new FormatException("the Spark network descriptor is empty.");
        }

        var coordinator = Hex64(descriptor.CoordinatorIdentifier, "coordinatorIdentifier");

        if (descriptor.Operators is not { Count: > 0 } operators)
        {
            throw new FormatException("the Spark network descriptor lists no operators.");
        }

        if (descriptor.Threshold is not { } threshold || threshold == 0)
        {
            throw new FormatException("the Spark network descriptor's threshold must be at least 1.");
        }

        // A threshold above the signing-set size can never be met, so every signing operation would fail.
        // Caught here because the SDK does not check it and the resulting failures name neither number.
        if (threshold > operators.Count)
        {
            throw new FormatException(
                $"the Spark network descriptor's threshold ({threshold}) exceeds its "
                + $"{operators.Count} operator(s).");
        }

        var parsed = new List<SparkCustomNetworkOperator>(operators.Count);
        for (var index = 0; index < operators.Count; index++)
        {
            var op = operators[index]
                ?? throw new FormatException($"operator {index} of the Spark network descriptor is null.");

            if (op.Id is not { } id)
            {
                throw new FormatException($"operator {index} of the Spark network descriptor has no id.");
            }

            parsed.Add(new SparkCustomNetworkOperator(
                id,
                Hex64(op.Identifier, $"operator {index}'s identifier"),
                HttpsAddress(op.Address, $"operator {index}'s address"),
                Required(op.IdentityPublicKey, $"operator {index}'s identityPublicKey"),
                string.IsNullOrWhiteSpace(op.CaCertPem) ? null : op.CaCertPem));
        }

        if (descriptor.Ssp is not { } ssp)
        {
            throw new FormatException("the Spark network descriptor has no ssp section.");
        }

        var sspConfig = new SparkCustomNetworkSsp(
            AbsoluteUrl(ssp.BaseUrl, "the ssp baseUrl"),
            Required(ssp.IdentityPublicKey, "the ssp identityPublicKey"),
            string.IsNullOrWhiteSpace(ssp.SchemaEndpoint) ? null : ssp.SchemaEndpoint);

        return new SparkCustomNetwork(
            coordinator,
            threshold,
            parsed,
            sspConfig,
            AbsoluteUrl(descriptor.EsploraUrl, "the esploraUrl"));
    }

    private static string Required(string? value, string field) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new FormatException($"the Spark network descriptor is missing {field}.")
            : value.Trim();

    private static string Hex64(string? value, string field)
    {
        var candidate = Required(value, field);
        if (candidate.Length != 64 || !IsHex(candidate))
        {
            throw new FormatException(
                $"the Spark network descriptor's {field} must be 64 hex characters, not \"{candidate}\".");
        }

        return candidate;
    }

    /// <summary>
    /// Operator addresses must be https: the SDK talks gRPC-over-TLS to them and nothing else, so a plain
    /// http address is not a weaker configuration, it is a connect that never completes.
    /// </summary>
    private static string HttpsAddress(string? value, string field)
    {
        var candidate = AbsoluteUrl(value, field);
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new FormatException(
                $"the Spark network descriptor's {field} must be an https URL, not \"{candidate}\".");
        }

        return candidate;
    }

    private static string AbsoluteUrl(string? value, string field)
    {
        var candidate = Required(value, field);
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new FormatException(
                $"the Spark network descriptor's {field} must be an absolute http(s) URL, "
                + $"not \"{candidate}\".");
        }

        return candidate;
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The wire shape. Every member is nullable so that a missing field is reported by name below rather
    /// than as a deserialization error that says only which JSON path failed.
    /// </summary>
    private sealed record Descriptor
    {
        [JsonPropertyName("coordinatorIdentifier")]
        public string? CoordinatorIdentifier { get; init; }

        [JsonPropertyName("threshold")]
        public uint? Threshold { get; init; }

        [JsonPropertyName("operators")]
        public IReadOnlyList<OperatorDescriptor?>? Operators { get; init; }

        [JsonPropertyName("ssp")]
        public SspDescriptor? Ssp { get; init; }

        [JsonPropertyName("esploraUrl")]
        public string? EsploraUrl { get; init; }
    }

    private sealed record OperatorDescriptor
    {
        [JsonPropertyName("id")]
        public uint? Id { get; init; }

        [JsonPropertyName("identifier")]
        public string? Identifier { get; init; }

        [JsonPropertyName("address")]
        public string? Address { get; init; }

        [JsonPropertyName("identityPublicKey")]
        public string? IdentityPublicKey { get; init; }

        [JsonPropertyName("caCertPem")]
        public string? CaCertPem { get; init; }
    }

    private sealed record SspDescriptor
    {
        [JsonPropertyName("baseUrl")]
        public string? BaseUrl { get; init; }

        [JsonPropertyName("identityPublicKey")]
        public string? IdentityPublicKey { get; init; }

        [JsonPropertyName("schemaEndpoint")]
        public string? SchemaEndpoint { get; init; }
    }
}
