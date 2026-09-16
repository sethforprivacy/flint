using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Sdk;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// What an explorer said about a funding address: the confirmed outputs on it, or why nothing could be said.
/// </summary>
/// <remarks>
/// <b>"None found" and "could not look" are different answers and must never collapse into one.</b> An operator
/// who has already sent the funding sats reads "0 sat on the funding address" as "my transaction has not
/// confirmed yet" and waits — possibly for hours, on a confirmation that already happened, because the explorer
/// URL was wrong. So a failure carries <see cref="Error"/> and a null <see cref="Utxos"/>, and
/// <see cref="SparkUnilateralExitService.BuildAsync"/> refuses instead of reporting a shortfall it did not
/// measure. <see cref="SparkExitFundingBalance"/> keeps the same distinction for the read path.
/// </remarks>
/// <param name="Utxos">
/// The confirmed outputs, possibly empty. Null exactly when the lookup failed.
/// </param>
/// <param name="Error">Merchant-facing reason the lookup failed, set exactly when <paramref name="Utxos"/> is null.</param>
public sealed record SparkExitFundingLookup(IReadOnlyList<SparkExitFundingUtxo>? Utxos, string? Error)
{
    public static SparkExitFundingLookup Found(IReadOnlyList<SparkExitFundingUtxo> utxos) => new(utxos, null);

    public static SparkExitFundingLookup Failed(string error) => new(null, error);
}

/// <summary>
/// What a funding address holds, for a caller that only needs the numbers.
/// </summary>
/// <remarks>
/// <para>
/// The read path's answer, and it exists so that rendering the exit page needs no key material: measuring an
/// address takes no public key, whereas every <see cref="SparkExitFundingUtxo"/> carries one because the SDK
/// needs it to build a witness. Only a build derives the funding key.
/// </para>
/// <para>
/// <b>Both figures, because the sum is the misleading one.</b> An exit is funded by a single output, so an
/// address holding twice the requirement across two outputs funds nothing — and a page reporting only the total
/// would tell an operator they are done while every build refuses.
/// <see cref="LargestOutputSat"/> is the number the requirement is judged against.
/// </para>
/// </remarks>
/// <param name="TotalSat">Confirmed satoshi on the address, or null when the explorer could not be read.</param>
/// <param name="LargestOutputSat">
/// The largest single confirmed output, zero when there is none, and null on the same terms as
/// <paramref name="TotalSat"/>.
/// </param>
/// <param name="Error">Merchant-facing reason the lookup failed, set exactly when the two figures are null.</param>
public sealed record SparkExitFundingBalance(long? TotalSat, long? LargestOutputSat, string? Error)
{
    public static SparkExitFundingBalance Unknown(string error) => new(null, null, error);
}

/// <summary>
/// Finds the confirmed on-chain outputs sitting on a unilateral exit's funding address.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing else can answer this question.</b> The funding output is an ordinary UTXO on a key derived outside
/// Spark's tree (<see cref="SparkExitFundingKey"/>), so the SDK has never heard of it; and the address is in none
/// of the store's derivation schemes, so NBXplorer has not either. That leaves a block explorer, which is why
/// there is an override for operators who would rather not tell mempool.space which address funds their exit.
/// </para>
/// <para>
/// <b>Confirmed only, and that is an economic decision rather than caution.</b> Every transaction in the exit is
/// a CPFP child of this output. Spending an unconfirmed funding UTXO would make the whole exit a package
/// descending from an unconfirmed parent, and mempool policy limits how deep and how large such a package may be
/// — an exit tree is dozens of transactions across many levels, so the packages would be rejected as
/// non-relayable somewhere in the middle, after the operator had already broadcast the fan-out and paid for it.
/// Waiting one confirmation costs ten minutes; discovering the limit halfway through costs the fan-out fee and a
/// re-quote.
/// </para>
/// <para>
/// <b>This explorer is trusted with nothing.</b> A wrong or hostile one can make a build refuse (it reports no
/// UTXO) or fail at signing time (it reports one that does not exist); it cannot move a satoshi anywhere, because
/// the destination lives in the transactions the SDK signs and the funding key never leaves the plugin. Outputs
/// whose txid is not 32 bytes of hex are dropped rather than passed on, on the same principle as the sweep
/// labeller's: a malformed identifier from a third party should not become an argument to the SDK.
/// </para>
/// </remarks>
public sealed class SparkExitFundingExplorer
{
    /// <summary>
    /// The named <see cref="HttpClient"/> this uses, registered in <c>SparkPlugin</c> with its own timeout.
    /// </summary>
    /// <remarks>
    /// Named rather than default so the short timeout below applies to this endpoint alone, and so the factory
    /// owns socket lifetime — the same arrangement <see cref="CrossChainCatalog"/> uses for its one endpoint.
    /// </remarks>
    public const string HttpClientName = "spark-exit-funding-explorer";

    /// <summary>
    /// The default explorer, used on mainnet when the store has configured no override.
    /// </summary>
    /// <remarks>
    /// A third party, and named in the settings copy as one. It is the same API surface as any esplora instance,
    /// so an operator who objects points <see cref="UnilateralExitSettings.EsploraApiUrl"/> at their own.
    /// </remarks>
    public const string MainnetDefaultApiUrl = "https://mempool.space/api";

    /// <summary>
    /// The whole lookup, including connect, response and parse.
    /// </summary>
    /// <remarks>
    /// Short because a request thread is waiting on it: this runs while the exit page renders and while a Build
    /// press is being answered. A slow explorer must degrade to "unknown" quickly rather than hold the page.
    /// </remarks>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The most of a response that will be read before it is abandoned.
    /// </summary>
    /// <remarks>
    /// A UTXO list for one address is a few kilobytes. The ceiling exists for the response that never ends, which
    /// <see cref="RequestTimeout"/> also bounds — belt and braces, because the timeout bounds the wait and this
    /// bounds the memory.
    /// </remarks>
    public const long MaxResponseBytes = 4L * 1024 * 1024;

    /// <summary>
    /// How long a recommended fee rate is reused before the explorer is asked for another one.
    /// </summary>
    /// <remarks>
    /// <b>Cached rather than fetched per render, because the exit page is a GET that any store viewer can reload in
    /// a loop.</b> A rate is not a per-viewer fact, and a third party's endpoint should not see one request per page
    /// view — nor should a page wait on the network when it already holds an answer from moments ago. Five minutes
    /// is about one block: short enough that the number still tracks a moving market, long enough that an operator
    /// reloading the page is answered from memory.
    /// </remarks>
    public static readonly TimeSpan RecommendedFeeRateTtl = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // esplora spells everything lower case; being insensitive also survives an instance that does not.
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SparkExitFundingExplorer> _logger;

    /// <summary>
    /// The recommended rate last read from each explorer URL.
    /// </summary>
    /// <remarks>
    /// Keyed by base URL because that is what an answer belongs to: two stores pointed at different esplora
    /// instances are asking different questions, and the process-wide mempool.space default must not answer for an
    /// operator's self-hosted explorer. The map is bounded by the distinct explorer URLs configured on the server,
    /// which is a handful, and an entry past its window is dropped as the next one is written — so a URL that stops
    /// being used does not sit here for the life of the process.
    /// </remarks>
    private readonly ConcurrentDictionary<string, CachedFeeRate> _recommendedFeeRates =
        new(StringComparer.Ordinal);

    /// <param name="timeProvider">
    /// Read for the cache window alone. Injected rather than taken from <see cref="TimeProvider.System"/> so a test
    /// can step over the window instead of waiting it out.
    /// </param>
    public SparkExitFundingExplorer(
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider,
        ILogger<SparkExitFundingExplorer> logger)
    {
        _httpClientFactory = httpClientFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// The explorer base URL to use for a store, or the reason there is none.
    /// </summary>
    /// <remarks>
    /// <b>Off mainnet a missing override is a refusal, not a fallback.</b> mempool.space has no regtest, so
    /// pointing at it there would answer every lookup with "no outputs found" — indistinguishable from an
    /// unconfirmed funding transaction, and an operator would wait on a confirmation that already happened. The
    /// honest answer names the setting.
    /// </remarks>
    public static bool TryResolveBaseUrl(
        UnilateralExitSettings? settings,
        bool mainnet,
        out string? baseUrl,
        out string? error)
    {
        var configured = settings?.EsploraApiUrl;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!TryNormaliseApiUrl(configured, out var normalised, out var fragment))
            {
                baseUrl = null;
                error = "The block-explorer URL configured for exit funding cannot be used: " + fragment
                        + ". Correct it in this store's exit settings.";
                return false;
            }

            baseUrl = normalised;
            error = null;
            return true;
        }

        if (!mainnet)
        {
            baseUrl = null;
            error = "No block explorer is configured for exit funding, and there is no default off mainnet: "
                    + "mempool.space has no regtest. Set the esplora API URL on this page to an explorer that "
                    + "can see this chain.";
            return false;
        }

        baseUrl = MainnetDefaultApiUrl;
        error = null;
        return true;
    }

    /// <summary>
    /// Canonicalises an operator-supplied explorer base URL, or says why it is unusable.
    /// </summary>
    /// <remarks>
    /// One owner for the rule, called both when the setting is stored (so a typo is refused while the operator is
    /// looking at the form) and when it is used (so a value that arrived from a backup, an API call or a hand
    /// edit is refused rather than concatenated into a request URL). The trailing slash is trimmed here so the
    /// path built below never doubles it.
    /// </remarks>
    /// <param name="error">
    /// A merchant-facing sentence fragment naming what is wrong, set exactly when this returns false.
    /// </param>
    public static bool TryNormaliseApiUrl(string? candidate, out string? normalised, out string? error)
    {
        normalised = null;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            error = "no address was supplied";
            return false;
        }

        var trimmed = candidate.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "it is not an absolute http:// or https:// address";
            return false;
        }

        normalised = trimmed;
        error = null;
        return true;
    }

    /// <summary>
    /// Lists the confirmed outputs on <paramref name="address"/>, tagged with the public key that spends them.
    /// </summary>
    /// <param name="pubkeyHex">
    /// The compressed public key for the address, copied onto every output. The explorer does not report it — a
    /// P2WPKH script carries only the hash — and the SDK needs it to build the witness it will ask the signer to
    /// sign, so it comes from the derivation rather than from the wire.
    /// </param>
    /// <remarks>
    /// <para>
    /// The build path. Requiring the public key here rather than making it optional is deliberate: an output
    /// tagged with the wrong key, or with none, fails deep inside the SDK's witness construction, so the only
    /// caller that can produce one of these is the one that has derived the key. Everything that merely wants to
    /// know what the address holds uses <see cref="MeasureConfirmedAsync"/> and derives nothing.
    /// </para>
    /// <para>
    /// The order of the returned list carries no meaning. Which output to spend is the service's policy — it
    /// takes the smallest one that covers the requirement, so an over-funded address keeps its larger output
    /// intact — and sorting here would look like that decision had already been made.
    /// </para>
    /// <para>
    /// Never throws for a network or parse failure; those come back as
    /// <see cref="SparkExitFundingLookup.Failed"/>. Cancellation does propagate, because a cancelled request is
    /// the caller going away rather than an explorer being unreachable.
    /// </para>
    /// </remarks>
    public async Task<SparkExitFundingLookup> ListConfirmedAsync(
        string baseUrl,
        string address,
        string pubkeyHex,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pubkeyHex);

        var (outputs, error) = await FetchConfirmedAsync(baseUrl, address, cancellationToken)
            .ConfigureAwait(false);

        return outputs is null
            ? SparkExitFundingLookup.Failed(error!)
            : SparkExitFundingLookup.Found(outputs
                .Select(output => new SparkExitFundingUtxo(
                    output.Txid, output.Vout, output.ValueSat, pubkeyHex))
                .ToList());
    }

    /// <summary>
    /// What <paramref name="address"/> holds in confirmed satoshi, in total and in its largest single output.
    /// </summary>
    /// <remarks>
    /// The read path, and the reason it exists is that rendering the exit page must not derive the store's
    /// funding key: measuring an address needs no key at all, and a page that unprotected the merchant's seed on
    /// every load would be paying a real risk for nothing. Failures come back as
    /// <see cref="SparkExitFundingBalance.Unknown"/> — never as zero, which an operator would read as "my
    /// funding has not confirmed yet".
    /// </remarks>
    public async Task<SparkExitFundingBalance> MeasureConfirmedAsync(
        string baseUrl,
        string address,
        CancellationToken cancellationToken = default)
    {
        var (outputs, error) = await FetchConfirmedAsync(baseUrl, address, cancellationToken)
            .ConfigureAwait(false);

        if (outputs is null)
            return SparkExitFundingBalance.Unknown(error!);

        return new SparkExitFundingBalance(
            outputs.Sum(output => output.ValueSat),
            outputs.Count == 0 ? 0 : outputs.Max(output => output.ValueSat),
            null);
    }

    /// <summary>
    /// The rate the explorer would put a transaction in the next few blocks at, clamped to what the quote form
    /// accepts, or null when no recommendation can be had.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The half-hour estimate, deliberately, not the fastest one on offer.</b> An exit is not one transaction: it
    /// is a chain of dozens the operator broadcasts by hand over hours to days, every one of them paying whatever
    /// rate was chosen here. The fastest rate overpays on all of them; a floor rate risks the one failure this flow
    /// cannot recover from, a half-broadcast exit whose fan-out is already spent and whose tree will not relay until
    /// a fee the operator cannot easily raise arrives. The half-hour estimate is the middle of that tradeoff — it
    /// confirms while the operator is still at the keyboard for the part of the exit where that matters, without
    /// pricing every transaction in the tree like an emergency.
    /// </para>
    /// <para>
    /// <b>Null means "no recommendation", never "zero".</b> Off mainnet with no override there is no explorer to ask,
    /// and a fetch that fails is an ordinary state of the world rather than an error: the caller falls back to
    /// <see cref="SparkUnilateralExitService.DefaultFeeRateSatPerVbyte"/> and the exit stays usable. So every
    /// failure — unreachable host, timeout, HTTP error, unparseable body, oversized body — returns null instead of
    /// throwing, and nothing about the exit page depends on a third party being up.
    /// </para>
    /// </remarks>
    public async Task<long?> RecommendFeeRateSatPerVbyteAsync(
        bool mainnet,
        UnilateralExitSettings? settings,
        CancellationToken cancellationToken = default)
    {
        // Off mainnet with no override this refuses, which is expected — mempool.space has no regtest — and the
        // caller's fallback is what makes it harmless here. See TryResolveBaseUrl.
        if (!TryResolveBaseUrl(settings, mainnet, out var baseUrl, out _))
            return null;

        if (TryReadCachedFeeRate(baseUrl!, out var cached))
            return cached;

        var recommended = await FetchRecommendedFeeRateAsync(baseUrl!, cancellationToken).ConfigureAwait(false);

        // Stored whether or not there was an answer. A fetch that failed is precisely the one that must not be
        // repeated on every render while the explorer is down.
        RememberFeeRate(baseUrl!, recommended);

        return recommended;
    }

    /// <summary>
    /// The cached answer for one explorer URL, when one arrived inside the last
    /// <see cref="RecommendedFeeRateTtl"/>.
    /// </summary>
    /// <remarks>
    /// A clock that moves backwards keeps the answer rather than discarding it: the entry is only stale when the
    /// measured age reaches the window, so a negative age reads as fresh, which is the harmless direction to be
    /// wrong in.
    /// </remarks>
    private bool TryReadCachedFeeRate(string baseUrl, out long? satPerVbyte)
    {
        satPerVbyte = null;

        if (!_recommendedFeeRates.TryGetValue(baseUrl, out var cached))
            return false;

        if (_timeProvider.GetUtcNow() - cached.FetchedAt >= RecommendedFeeRateTtl)
            return false;

        satPerVbyte = cached.SatPerVbyte;
        return true;
    }

    /// <summary>
    /// Remembers one answer for one URL, dropping the entries that have already outlived the window.
    /// </summary>
    /// <remarks>
    /// The sweep on write is what keeps this map from becoming a permanent record of every explorer URL the process
    /// has ever been pointed at. It costs a walk over a handful of entries, on an operation that happens at most
    /// once per window per URL.
    /// </remarks>
    private void RememberFeeRate(string baseUrl, long? satPerVbyte)
    {
        var now = _timeProvider.GetUtcNow();

        foreach (var (url, entry) in _recommendedFeeRates)
        {
            if (now - entry.FetchedAt >= RecommendedFeeRateTtl)
                _recommendedFeeRates.TryRemove(url, out _);
        }

        _recommendedFeeRates[baseUrl] = new CachedFeeRate(satPerVbyte, now);
    }

    /// <summary>
    /// One HTTP round trip to an esplora instance's recommended-fee endpoint.
    /// </summary>
    /// <returns>
    /// The half-hour rate clamped into the bounds the quote form enforces, or null when no usable rate could be
    /// read — including every failure the caller must not see as an exception.
    /// </returns>
    private async Task<long?> FetchRecommendedFeeRateAsync(string baseUrl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        var url = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/v1/fees/recommended",
            baseUrl.TrimEnd('/'));

        try
        {
            using var deadline = new CancellationTokenSource(RequestTimeout);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, deadline.Token);

            var client = _httpClientFactory.CreateClient(HttpClientName);

            using var response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, bounded.Token)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            await using var body = await response.Content
                .ReadAsStreamAsync(bounded.Token)
                .ConfigureAwait(false);

            var payload = await ReadBoundedAsync(body, bounded.Token).ConfigureAwait(false);
            var reported = JsonSerializer.Deserialize<RecommendedFees>(payload, JsonOptions);

            var rate = reported?.HalfHourFee;

            // Zero or negative is not a rate, it is a body that says nothing about the market: a zeroed
            // placeholder, or a number that arrived in a shape the parse left empty. Refused rather than clamped up,
            // because clamping would turn "the explorer told us nothing" into "the explorer said one satoshi".
            if (rate is null or <= 0)
            {
                _logger.LogWarning(
                    "The block explorer at {Url} reported no usable half-hour fee rate", baseUrl);
                return null;
            }

            // Only the ceiling is ever reached: anything under the floor is zero or negative, refused just above.
            return Math.Clamp(
                rate.Value,
                SparkUnilateralExitService.MinFeeRateSatPerVbyte,
                SparkUnilateralExitService.MaxFeeRateSatPerVbyte);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Warning rather than error: nothing is broken by this, and the page still offers a rate to quote at.
            _logger.LogWarning(ex, "Could not read a recommended fee rate from {Url}", baseUrl);

            return null;
        }
    }

    /// <summary>One explorer URL's last answer, and when it arrived.</summary>
    private readonly record struct CachedFeeRate(long? SatPerVbyte, DateTimeOffset FetchedAt);

    /// <summary>
    /// The one HTTP round trip both public methods share: the address's confirmed outputs, untagged.
    /// </summary>
    /// <returns>
    /// The outputs, possibly empty, and a null error; or a null list and a merchant-facing reason. Exactly one of
    /// the two is set, which is what keeps "none found" and "could not look" from collapsing into one answer.
    /// </returns>
    private async Task<(IReadOnlyList<ConfirmedOutput>? Outputs, string? Error)> FetchConfirmedAsync(
        string baseUrl,
        string address,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        var url = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/address/{1}/utxo",
            baseUrl.TrimEnd('/'),
            Uri.EscapeDataString(address));

        try
        {
            using var deadline = new CancellationTokenSource(RequestTimeout);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, deadline.Token);

            var client = _httpClientFactory.CreateClient(HttpClientName);

            using var response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, bounded.Token)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            await using var body = await response.Content
                .ReadAsStreamAsync(bounded.Token)
                .ConfigureAwait(false);

            var payload = await ReadBoundedAsync(body, bounded.Token).ConfigureAwait(false);
            var reported = JsonSerializer.Deserialize<List<EsploraUtxo>>(payload, JsonOptions);

            var outputs = new List<ConfirmedOutput>();
            foreach (var candidate in reported ?? [])
            {
                if (candidate.Status?.Confirmed is not true)
                    continue;

                // See the class remarks: a third party's identifier is validated before it can become an argument
                // to the SDK. Dropped rather than refused, so one junk row cannot hide the real funding output.
                var txid = SparkLightningClient.NormaliseHash(candidate.Txid);
                if (txid is null || candidate.Value <= 0)
                {
                    _logger.LogWarning(
                        "Exit funding lookup for {Address} skipped an unusable output reported by {Url}",
                        address, baseUrl);
                    continue;
                }

                outputs.Add(new ConfirmedOutput(txid, candidate.Vout, candidate.Value));
            }

            return (outputs, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Warning rather than error: nothing is broken by this, and the two surfaces above both have a
            // sensible "unknown" to render.
            _logger.LogWarning(ex,
                "Could not read the exit funding address {Address} from {Url}", address, baseUrl);

            return (null,
                "The block explorer could not be read, so it is not known what is on the exit funding address "
                + $"yet: {Describe(ex)}");
        }
    }

    /// <summary>One confirmed output as the explorer described it, before any key is attached to it.</summary>
    private readonly record struct ConfirmedOutput(string Txid, uint Vout, long ValueSat);

    /// <summary>
    /// Reads a response body, refusing one that goes past <see cref="MaxResponseBytes"/>.
    /// </summary>
    /// <remarks>
    /// Applied while reading rather than off <c>Content-Length</c>, because a chunked response does not have one
    /// — and a response with no declared length is exactly the case worth defending against.
    /// </remarks>
    private static async Task<byte[]> ReadBoundedAsync(Stream body, CancellationToken cancellationToken)
    {
        using var collected = new MemoryStream();
        var chunk = new byte[8 * 1024];

        while (true)
        {
            var read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            if (collected.Length + read > MaxResponseBytes)
            {
                throw new InvalidOperationException(
                    "the explorer's answer was larger than this plugin will read");
            }

            collected.Write(chunk, 0, read);
        }

        return collected.ToArray();
    }

    private static string Describe(Exception exception) => exception switch
    {
        OperationCanceledException => "the explorer did not answer in time",
        HttpRequestException http => http.StatusCode is { } status
            ? string.Format(CultureInfo.InvariantCulture, "the explorer answered {0:D}", (int)status)
            : "the explorer could not be reached",
        JsonException => "the explorer's answer was not in the expected format",
        _ => exception.Message
    };

    /// <summary>
    /// The one field of esplora's <c>GET /v1/fees/recommended</c> this plugin reads, out of the five it reports.
    /// </summary>
    /// <remarks>
    /// Only the half-hour estimate is bound, because it is the one <see cref="RecommendFeeRateSatPerVbyteAsync"/>
    /// chose and binding the others would suggest a policy this class does not have. Nullable so a body that omits
    /// the field is a null rather than a zero; a body that spells it as something other than a number fails the
    /// parse, which the caller treats the same way — see the remarks on that method.
    /// </remarks>
    private sealed record RecommendedFees(
        [property: JsonPropertyName("halfHourFee")] long? HalfHourFee);

    /// <summary>One entry of esplora's <c>GET /address/{address}/utxo</c>.</summary>
    /// <remarks>
    /// Only the four fields the plugin uses are bound. esplora also reports the block height and time of the
    /// confirming block, and neither decides anything here: one confirmation is the bar, and it is
    /// <see cref="EsploraUtxoStatus.Confirmed"/> that states it.
    /// </remarks>
    private sealed record EsploraUtxo(
        [property: JsonPropertyName("txid")] string? Txid,
        [property: JsonPropertyName("vout")] uint Vout,
        [property: JsonPropertyName("value")] long Value,
        [property: JsonPropertyName("status")] EsploraUtxoStatus? Status);

    private sealed record EsploraUtxoStatus(
        [property: JsonPropertyName("confirmed")] bool Confirmed);
}
