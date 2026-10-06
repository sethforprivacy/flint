using System;
using System.Security.Cryptography;
using System.Text;
using BTCPayServer.Lightning;

namespace BTCPayServer.Plugins.Flint;

/// <summary>How a connection string relates to this plugin.</summary>
public enum SparkConnectionStringParseResult
{
    /// <summary>Not ours. The caller must return null with no error so other handlers get a turn.</summary>
    NotOurs,

    /// <summary>Ours, but malformed. The caller must return null with an error.</summary>
    Invalid,

    /// <summary>Ours and well formed.</summary>
    Ok
}

/// <summary>
/// Parsing and formatting of <c>type=flint;store-id=&lt;id&gt;;key=&lt;secret&gt;</c>.
/// </summary>
/// <remarks>
/// Separate from <see cref="SparkConnectionStringHandler"/> so the parsing rules can be tested without
/// a running service, and so the setup UI has one place to generate a connection string from.
/// </remarks>
public static class SparkConnectionString
{
    public const string StoreIdKey = "store-id";
    public const string PaymentKeyKey = "key";

    /// <summary>Number of random bytes in a generated payment key.</summary>
    private const int PaymentKeyBytes = 32;

    /// <summary>
    /// Builds the connection string for a store. This is what the plugin writes into the store's
    /// <c>BTC-LN</c> payment method config, and what <c>SparkLightningClient.ToString()</c> must return —
    /// BTCPay persists <c>client.ToString()</c> as the connection string.
    /// </summary>
    public static string Format(string storeId, string paymentKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        ArgumentException.ThrowIfNullOrEmpty(paymentKey);
        return $"type={Constants.ConnectionStringType};{StoreIdKey}={storeId};{PaymentKeyKey}={paymentKey}";
    }

    /// <summary>
    /// Generates a payment key. There is no <c>server=</c> component anywhere in this connection string. Up to
    /// BTCPay 2.4.4 that made core's <c>IsSafe</c> check pass, so a non-admin could paste it into core's Lightning
    /// form; from 2.4.5 core treats a string without <c>server=</c> as unsafe and only a server admin can paste it
    /// there. Nothing depends on either: the plugin writes the store's configuration itself, without that check.
    /// </summary>
    public static string GeneratePaymentKey() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(PaymentKeyBytes));

    public static SparkConnectionStringParseResult Parse(
        string? connectionString,
        out string? storeId,
        out string? paymentKey,
        out string? error)
    {
        storeId = null;
        paymentKey = null;
        error = null;

        if (string.IsNullOrWhiteSpace(connectionString))
            return SparkConnectionStringParseResult.NotOurs;

        System.Collections.Generic.Dictionary<string, string> values;
        string? type;
        try
        {
            values = LightningConnectionStringHelper.ExtractValues(connectionString, out type);
        }
        catch (FormatException)
        {
            // Malformed beyond the point where the type can be read (missing 'type', duplicate keys, a
            // component with no '='). We cannot claim it, so we stay silent and let BTCPay report that
            // no handler understood it.
            return SparkConnectionStringParseResult.NotOurs;
        }

        if (!string.Equals(type, Constants.ConnectionStringType, StringComparison.OrdinalIgnoreCase))
            return SparkConnectionStringParseResult.NotOurs;

        if (!values.TryGetValue(StoreIdKey, out var parsedStoreId) || string.IsNullOrWhiteSpace(parsedStoreId))
        {
            error = $"The key '{StoreIdKey}' is mandatory for {Constants.ConnectionStringType} connection strings";
            return SparkConnectionStringParseResult.Invalid;
        }

        if (!values.TryGetValue(PaymentKeyKey, out var parsedKey) || string.IsNullOrWhiteSpace(parsedKey))
        {
            error = $"The key '{PaymentKeyKey}' is mandatory for {Constants.ConnectionStringType} connection strings";
            return SparkConnectionStringParseResult.Invalid;
        }

        storeId = parsedStoreId;
        paymentKey = parsedKey;
        return SparkConnectionStringParseResult.Ok;
    }

    /// <summary>
    /// Compares two payment keys without leaking their contents through timing.
    /// </summary>
    /// <remarks>
    /// Both sides are hashed first so the comparison is over fixed-length inputs:
    /// <see cref="CryptographicOperations.FixedTimeEquals"/> short-circuits on a length mismatch, which
    /// on raw keys would leak the key length. The connection string is attacker-supplied on any request
    /// that saves a Lightning payment method, so this is a real oracle if done naively.
    /// </remarks>
    public static bool PaymentKeyMatches(string? expected, string? supplied)
    {
        if (expected is null || supplied is null)
            return false;
        Span<byte> expectedHash = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> suppliedHash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(expected), expectedHash);
        SHA256.HashData(Encoding.UTF8.GetBytes(supplied), suppliedHash);
        return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
    }
}
