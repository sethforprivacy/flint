using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Rating;
using BTCPayServer.Services.Rates;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// What one satoshi is worth, for sanity-checking a bridge provider's quote.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the sweep engine needs a price at all.</b> The cross-chain fee guard compares
/// <c>assetAmountIn</c> against <c>estimatedOut</c> — two figures from the same quote, in the same asset — so it
/// bounds the spread the provider <em>states</em> and says nothing about the rate it applies. A quote offering
/// $100 of USDT for 500,000 satoshi (about $320 at the price this was written) reports a 0.34% spread, clears
/// the 3% default and the 50% backstop, and loses the merchant about 68%. Nothing inside the quote can catch
/// that; only an outside price can.
/// </para>
/// <para>
/// Deliberately an interface with a very small surface. The engine must not grow a dependency on BTCPay's rate
/// subsystem — it is a singleton constructed early, while BTCPay is still building its own graph, and what such
/// a type may pull in is sharply constrained.
/// </para>
/// </remarks>
public interface ICrossChainValueOracle
{
    /// <summary>
    /// The value of one satoshi in <paramref name="currencyCode"/>, or null when no rate could be obtained.
    /// </summary>
    /// <remarks>
    /// Null is a real answer and the caller must treat it as one: the sweep is <b>refused</b>, not allowed
    /// through unchecked. Refusing is cheap here — sweeping is automatic and a refusal is a designed steady
    /// state, so the next pass simply tries again once rates are back.
    /// </remarks>
    Task<decimal?> TryGetSatoshiValueAsync(
        string storeId,
        string currencyCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The mid-market value of one satoshi in <paramref name="currencyCode"/> through the store's rules, or null when
    /// no rate could be obtained — for valuing what arrived rather than guarding what leaves.
    /// </summary>
    /// <remarks>
    /// The midpoint, because the store's own spread widens its bid and ask around it (BTCPay's <c>Spread</c> setting)
    /// and has nothing to do with what a delivery is worth: valued at the bid, a store with a 2% spread would read
    /// every repriced payment as 2% short.
    /// </remarks>
    Task<decimal?> TryGetSatoshiMidValueAsync(
        string storeId,
        string currencyCode,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="ICrossChainValueOracle"/> over BTCPay's own rate providers, using the store's own rate rules.
/// </summary>
/// <remarks>
/// <para>
/// The store's rules rather than a hard-coded exchange, so the price this is judged against is the price the
/// merchant already trusts for their invoices.
/// </para>
/// <para>
/// Every dependency is resolved through <see cref="Func{TResult}"/>. That is the required pattern for anything
/// reachable from a type BTCPay may construct while building itself: resolving these eagerly puts a cycle in the
/// container's object graph that it cannot detect, and an undetected cycle is a silent permanent hang of
/// BTCPay's startup rather than an error.
/// </para>
/// </remarks>
public sealed class BTCPayCrossChainValueOracle : ICrossChainValueOracle
{
    /// <summary>How long a fetched rate is reused for.</summary>
    /// <remarks>
    /// This is a sanity check against catastrophic mispricing, not a trading signal, so a rate a minute old is
    /// entirely good enough — and a sweep pass runs every two minutes per store, which would otherwise mean a
    /// rate fetch per store per pass forever.
    /// </remarks>
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    private readonly Func<RateFetcher> _rateFetcher;
    private readonly Func<DefaultRulesCollection> _defaultRules;
    private readonly Func<StoreRepository> _stores;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BTCPayCrossChainValueOracle> _logger;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        (string StoreId, string Currency, bool Mid), (decimal Value, DateTimeOffset FetchedAt)> _cache = new();

    public BTCPayCrossChainValueOracle(
        Func<RateFetcher> rateFetcher,
        Func<DefaultRulesCollection> defaultRules,
        Func<StoreRepository> stores,
        TimeProvider timeProvider,
        ILogger<BTCPayCrossChainValueOracle> logger)
    {
        _rateFetcher = rateFetcher;
        _defaultRules = defaultRules;
        _stores = stores;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task<decimal?> TryGetSatoshiValueAsync(
        string storeId,
        string currencyCode,
        CancellationToken cancellationToken = default) =>
        FetchAsync(storeId, currencyCode, mid: false, cancellationToken);

    public Task<decimal?> TryGetSatoshiMidValueAsync(
        string storeId,
        string currencyCode,
        CancellationToken cancellationToken = default) =>
        FetchAsync(storeId, currencyCode, mid: true, cancellationToken);

    private async Task<decimal?> FetchAsync(
        string storeId,
        string currencyCode,
        bool mid,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        ArgumentException.ThrowIfNullOrEmpty(currencyCode);

        var key = (storeId, currencyCode.ToUpperInvariant(), mid);
        var now = _timeProvider.GetUtcNow();

        if (_cache.TryGetValue(key, out var cached) && now - cached.FetchedAt < CacheFor)
            return cached.Value;

        try
        {
            var store = await _stores().FindStore(storeId).ConfigureAwait(false);
            if (store is null)
                return null;

            var rules = store.GetStoreBlob().GetRateRules(_defaultRules());
            var result = await _rateFetcher()
                .FetchRate(new CurrencyPair("BTC", key.Item2), rules, null, cancellationToken)
                .ConfigureAwait(false);

            // For the guard, bid, not ask, and not the mid: this is the conservative side for someone selling
            // bitcoin, so a wide spread makes the guard slightly stricter rather than slightly laxer.
            if (result?.BidAsk is not { } bidAsk || bidAsk.Bid <= 0 || (mid && bidAsk.Ask <= 0))
            {
                _logger.LogWarning(
                    "Store {StoreId}: no BTC/{Currency} rate available to sanity-check a cross-chain quote",
                    storeId, key.Item2);
                return null;
            }

            var perSatoshi = (mid ? (bidAsk.Bid + bidAsk.Ask) / 2m : bidAsk.Bid) / 100_000_000m;
            _cache[key] = (perSatoshi, now);
            return perSatoshi;
        }
        catch (Exception ex)
        {
            // Never throws out of a sweep pass. A missing rate is reported as null and the caller refuses.
            _logger.LogWarning(ex,
                "Store {StoreId}: could not read a BTC/{Currency} rate to sanity-check a cross-chain quote",
                storeId, key.Item2);
            return null;
        }
    }
}
