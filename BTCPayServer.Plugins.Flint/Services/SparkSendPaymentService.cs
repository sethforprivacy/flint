using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Sdk;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// Sends outgoing Lightning payments from a store's Spark wallet.
/// </summary>
/// <remarks>
/// Supports two destination formats:
/// <list type="bullet">
///   <item>A BOLT11 invoice: the amount must be embedded unless <c>amountSats</c> is supplied.</item>
///   <item>A Lightning Address (<c>user@domain.com</c>): requires an explicit <c>amountSats</c>.</item>
/// </list>
/// <b>Idempotency:</b> each call mints a fresh UUID idempotency key. The SDK deduplicates by that key
/// within a wallet session, but this service does not persist the key across restarts. A crash during
/// send leaves the outcome unknown until the SDK's own history is queried.
/// </remarks>
public sealed class SparkSendPaymentService(
    ISparkStoreRuntime runtime,
    ISparkStoreSettingsStore settingsStore,
    IHttpClientFactory httpClientFactory,
    IBolt11Parser bolt11Parser,
    ILogger<SparkSendPaymentService> logger)
{
    /// <summary>
    /// Sends a Lightning payment from the store's Spark wallet.
    /// </summary>
    /// <param name="storeId">The BTCPay store whose wallet funds the payment.</param>
    /// <param name="destination">A BOLT11 invoice or a Lightning Address (<c>user@domain.com</c>).</param>
    /// <param name="amountSats">
    /// Required for a Lightning Address destination and for an amountless BOLT11. Ignored when the
    /// BOLT11 carries its own amount.
    /// </param>
    /// <param name="maxFeeSats">
    /// Maximum routing fee the caller will accept, in satoshi. When null, the default cap
    /// (<see cref="Constants.DefaultMaxFeePercent"/> % or <see cref="Constants.DefaultMaxFeeFloorSats"/> sat,
    /// whichever is larger) applies.
    /// </param>
    /// <param name="cancellationToken">Caller-supplied cancellation.</param>
    /// <returns>The outcome; never throws for domain errors.</returns>
    public async Task<SparkSendOutcome> SendAsync(
        string storeId,
        string destination,
        long? amountSats,
        long? maxFeeSats,
        CancellationToken cancellationToken)
    {
        if (maxFeeSats is < 0)
            return SparkSendOutcome.Failure("invalid-fee",
                "maxFeeSats must be zero or positive.");

        if (maxFeeSats > MaxFeeCapSats)
            return SparkSendOutcome.Failure("invalid-fee",
                string.Format(CultureInfo.InvariantCulture,
                    "maxFeeSats cannot exceed {0} sat.", MaxFeeCapSats));

        if (await settingsStore.GetAsync(storeId).ConfigureAwait(false) is null)
            return SparkSendOutcome.Failure("spark-not-configured",
                "Flint is not set up for this store.");

        var sdk = await runtime.GetSdkClientAsync(storeId).ConfigureAwait(false);
        if (sdk is null)
            return SparkSendOutcome.Failure("spark-not-running",
                "The Spark wallet for this store is not running. Try again in a moment.");

        string bolt11;
        if (IsLightningAddress(destination))
        {
            if (amountSats is null or <= 0)
                return SparkSendOutcome.Failure("amount-required",
                    "amountSats is required when paying a Lightning Address.");

            var resolved = await ResolveLightningAddressAsync(
                destination, amountSats.Value, cancellationToken).ConfigureAwait(false);

            if (resolved.Error is not null)
                return SparkSendOutcome.Failure("lightning-address-error", resolved.Error);

            bolt11 = resolved.Bolt11!;
        }
        else
        {
            bolt11 = destination;
        }

        var idempotencyKey = Guid.NewGuid().ToString();

        try
        {
            var result = await sdk.SendBolt11Async(
                    bolt11,
                    amountSats,
                    idempotencyKey,
                    quote => ApproveQuote(quote, amountSats ?? quote.AmountSats, maxFeeSats),
                    completionTimeout: null,
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.RejectedReason is not null)
                return SparkSendOutcome.Failure("fee-too-high", result.RejectedReason);

            var p = result.Payment!;
            return SparkSendOutcome.Success(new SparkSendPaymentResult(
                PaymentId: idempotencyKey,
                PaymentHash: p.PaymentHash,
                Bolt11: bolt11,
                AmountSats: p.AmountSats,
                FeeSats: p.FeeSats,
                PaidAt: p.Timestamp));
        }
        catch (Exception ex)
        {
            var description = SparkErrors.Describe(ex);

            if (SparkErrors.IsInsufficientFunds(ex))
                return SparkSendOutcome.Failure("insufficient-balance", description);

            if (SparkErrors.IsInvalidInput(ex))
                return SparkSendOutcome.Failure("invalid-invoice", description);

            // Anything else (network, timeout) may be in-flight. Flag it explicitly so callers don't retry blind.
            logger.LogWarning(ex, "Store {StoreId}: send-payment to {Destination} returned unknown outcome",
                storeId, destination);

            return SparkSendOutcome.Failure("unknown", description);
        }
    }

    public async Task<IReadOnlyList<SparkPayment>> ListSentAsync(
        string storeId,
        int limit,
        CancellationToken cancellationToken)
    {
        var sdk = await runtime.GetSdkClientAsync(storeId).ConfigureAwait(false);
        if (sdk is null)
            return [];
        return await sdk.ListPaymentsAsync(
            new SparkListPaymentsQuery(
                Direction: SparkPaymentDirection.Send,
                CompletedOnly: true,
                Limit: limit),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SparkPayment>> ListAllAsync(
        string storeId,
        SparkPaymentDirection? direction,
        DateTimeOffset? from,
        int skip,
        int limit,
        CancellationToken cancellationToken)
    {
        var sdk = await runtime.GetSdkClientAsync(storeId).ConfigureAwait(false);
        if (sdk is null)
            return [];
        return await sdk.ListPaymentsAsync(
            new SparkListPaymentsQuery(
                Direction: direction,
                CompletedOnly: true,
                From: from,
                Offset: skip,
                Limit: limit),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<SparkReceiveResult?> ReceiveBolt11Async(
        string storeId,
        string? description,
        long? amountSats,
        CancellationToken cancellationToken)
    {
        var sdk = await runtime.GetSdkClientAsync(storeId).ConfigureAwait(false);
        if (sdk is null)
            return null;
        return await sdk.ReceiveBolt11Async(
            description ?? "",
            amountSats,
            expirySecs: 86400,
            cancellationToken).ConfigureAwait(false);
    }

    // ----- fee approval -----

    private static string? ApproveQuote(SparkSendQuote quote, long amountSats, long? maxFeeSats)
    {
        if (quote.FeeSats < 0)
            return string.Format(
                CultureInfo.InvariantCulture,
                "The routing fee quoted by Spark is negative ({0} sat), which cannot be bounded. "
                + "The payment was refused.", quote.FeeSats);

        long cap;
        if (maxFeeSats is not null)
        {
            cap = maxFeeSats.Value;
        }
        else
        {
            cap = Math.Max(
                (long)Math.Floor(amountSats * Constants.DefaultMaxFeePercent / 100d),
                Constants.DefaultMaxFeeFloorSats);
        }

        if (quote.FeeSats <= cap)
            return null;

        return string.Format(
            CultureInfo.InvariantCulture,
            "Routing fee {0} sat exceeds the {1} limit of {2} sat. "
            + "Raise maxFeeSats in the request to allow a higher fee.",
            quote.FeeSats,
            maxFeeSats is not null ? "requested" : "default",
            cap);
    }

    // ----- Lightning Address resolution (LUD-16) -----

    private static bool IsLightningAddress(string destination)
    {
        // LastIndexOf: "user@real.com@attacker" should split at the last @, making the
        // domain "attacker" (which then fails SSRF checks) rather than "real.com@attacker"
        // which in RFC 3986 URL syntax would be treated as userinfo=real.com, host=attacker.
        var at = destination.LastIndexOf('@');
        return at > 0 && at < destination.Length - 1 && !destination.Contains(' ', StringComparison.Ordinal);
    }

    private async Task<(string? Bolt11, string? Error)> ResolveLightningAddressAsync(
        string address, long amountSats, CancellationToken cancellationToken)
    {
        var at = address.LastIndexOf('@');
        var user = address[..at];
        var domain = address[(at + 1)..];

        var ssrfError = await CheckSsrfAsync(domain, cancellationToken).ConfigureAwait(false);
        if (ssrfError is not null)
            return (null, ssrfError);

        var http = httpClientFactory.CreateClient(HttpClientName);

        LnurlPayResponse? lnurl;
        try
        {
            // Uri.EscapeDataString encodes path-special chars (/, ?, #) in the user portion so
            // they cannot alter the URL structure or inject query parameters.
            var wellKnown = $"https://{domain}/.well-known/lnurlp/{Uri.EscapeDataString(user)}";
            lnurl = await http.GetFromJsonAsync<LnurlPayResponse>(
                wellKnown, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (null, $"Could not reach the Lightning Address server: {ex.Message}");
        }

        if (lnurl?.Callback is null)
            return (null, "The Lightning Address server returned an invalid response.");

        // Validate the callback URL before following it: must be HTTPS and must resolve
        // to the same domain we already verified. A malicious LNURL server cannot redirect
        // us to an internal endpoint by returning a different host in the callback field.
        if (!Uri.TryCreate(lnurl.Callback, UriKind.Absolute, out var callbackUri)
            || !string.Equals(callbackUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            return (null, "The Lightning Address server returned a callback URL that is not HTTPS.");

        // Allow the callback on the same host or a subdomain/parent of the metadata host.
        // Providers like Blink serve the well-known from blink.sv but route callbacks through
        // api.blink.sv. Completely unrelated domains are still blocked.
        var callbackHost = callbackUri.Host;
        var sameOrRelated =
            string.Equals(callbackHost, domain, StringComparison.OrdinalIgnoreCase)
            || callbackHost.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)
            || domain.EndsWith("." + callbackHost, StringComparison.OrdinalIgnoreCase);
        if (!sameOrRelated)
            return (null,
                "The Lightning Address server returned a callback URL on an unrelated host. " +
                "Callbacks must be on the same domain or a subdomain of the Lightning Address domain.");

        // SSRF-check the callback host independently - the metadata host passed but the callback
        // might resolve to a private address on a different subdomain.
        if (!string.Equals(callbackHost, domain, StringComparison.OrdinalIgnoreCase))
        {
            var callbackSsrfError = await CheckSsrfAsync(callbackHost, cancellationToken).ConfigureAwait(false);
            if (callbackSsrfError is not null)
                return (null, callbackSsrfError);
        }

        var amountMsats = amountSats * 1000L;
        if (lnurl.MinSendable > 0 && amountMsats < lnurl.MinSendable)
            return (null, string.Format(
                CultureInfo.InvariantCulture,
                "Amount {0} sat is below the minimum of {1} sat for this Lightning Address.",
                amountSats, lnurl.MinSendable / 1000));

        if (lnurl.MaxSendable > 0 && amountMsats > lnurl.MaxSendable)
            return (null, string.Format(
                CultureInfo.InvariantCulture,
                "Amount {0} sat exceeds the maximum of {1} sat for this Lightning Address.",
                amountSats, lnurl.MaxSendable / 1000));

        var sep = lnurl.Callback.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var callbackUrl = $"{lnurl.Callback.TrimEnd('?')}{sep}amount={amountMsats}";

        LnurlPayInvoiceResponse? invoiceResponse;
        try
        {
            invoiceResponse = await http.GetFromJsonAsync<LnurlPayInvoiceResponse>(
                callbackUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (null, $"Failed to fetch invoice from Lightning Address: {ex.Message}");
        }

        if (string.IsNullOrEmpty(invoiceResponse?.Pr))
            return (null, "The Lightning Address server did not return an invoice.");

        // Verify the returned BOLT11 encodes the exact amount we requested.
        // A rogue LNURL server could embed a larger amount; we would otherwise pay it
        // because the SDK uses the invoice's embedded amount for fixed-amount invoices.
        var info = bolt11Parser.Parse(invoiceResponse.Pr);
        if (info is null)
            return (null, "The Lightning Address server returned an invoice that could not be parsed.");

        var expectedMsat = amountSats * 1000L;
        if (info.AmountMsat is null)
            return (null, "The Lightning Address server returned an amountless invoice. A fixed amount is required.");

        if (info.AmountMsat.Value != expectedMsat)
            return (null, string.Format(
                CultureInfo.InvariantCulture,
                "The Lightning Address server returned an invoice for {0} msat but {1} msat ({2} sat) was requested. " +
                "The payment was refused to prevent overpayment.",
                info.AmountMsat.Value, expectedMsat, amountSats));

        return (invoiceResponse.Pr, null);
    }

    // ----- SSRF protection -----

    // Resolves the domain to its IP addresses and rejects any that fall in RFC 1918,
    // loopback, or link-local ranges. This prevents Lightning Address destinations like
    // user@127.0.0.1 or user@169.254.169.254 from probing internal services.
    private static async Task<string?> CheckSsrfAsync(string host, CancellationToken ct)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }
        catch
        {
            // DNS failure: let the HTTP call fail naturally with a network error.
            return null;
        }

        foreach (var ip in addresses)
        {
            if (IsPrivateOrReservedAddress(ip))
                return "Lightning Address destinations must resolve to a public IP address.";
        }

        return null;
    }

    private static bool IsPrivateOrReservedAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal;

        var b = ip.GetAddressBytes();
        return b[0] == 10                                         // 10.0.0.0/8
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)         // 172.16.0.0/12
            || (b[0] == 192 && b[1] == 168)                      // 192.168.0.0/16
            || (b[0] == 169 && b[1] == 254)                      // 169.254.0.0/16 link-local
            || b[0] == 127                                        // 127.0.0.0/8 (belt+suspenders)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);       // 100.64.0.0/10 carrier-grade NAT
    }

    /// <summary>Named HttpClient for Lightning Address resolution.</summary>
    public const string HttpClientName = "SparkSendPaymentService";

    /// <summary>
    /// Hard ceiling on caller-supplied <c>maxFeeSats</c>. Prevents a single call from
    /// approving an arbitrarily large routing fee on a shared or multi-user store.
    /// 100,000 sat (0.001 BTC) is already several times the realistic fee for any
    /// Lightning payment; legitimate callers should never need more.
    /// </summary>
    public const long MaxFeeCapSats = 100_000L;

    private sealed class LnurlPayResponse
    {
        [JsonPropertyName("callback")]
        public string? Callback { get; set; }

        [JsonPropertyName("minSendable")]
        public long MinSendable { get; set; }

        [JsonPropertyName("maxSendable")]
        public long MaxSendable { get; set; }

        [JsonPropertyName("tag")]
        public string? Tag { get; set; }
    }

    private sealed class LnurlPayInvoiceResponse
    {
        [JsonPropertyName("pr")]
        public string? Pr { get; set; }
    }
}

/// <summary>The result of a send: either a success with payment details, or a failure with an error code.</summary>
public sealed record SparkSendOutcome
{
    public bool Succeeded { get; private init; }
    public SparkSendPaymentResult? Payment { get; private init; }
    public string? ErrorCode { get; private init; }
    public string? ErrorMessage { get; private init; }

    public static SparkSendOutcome Success(SparkSendPaymentResult payment) =>
        new() { Succeeded = true, Payment = payment };

    public static SparkSendOutcome Failure(string code, string message) =>
        new() { Succeeded = false, ErrorCode = code, ErrorMessage = message };
}

/// <summary>Payment details returned on a successful send.</summary>
public sealed record SparkSendPaymentResult(
    string PaymentId,
    string? PaymentHash,
    string Bolt11,
    long AmountSats,
    long FeeSats,
    DateTimeOffset PaidAt);
