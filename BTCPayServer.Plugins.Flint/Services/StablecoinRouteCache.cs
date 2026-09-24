using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Sdk;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// Each store's list of USDC/USDT receive routes, read from the provider at most every
/// <see cref="StablecoinPayments.RouteCacheTtl"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Invoice creation reads this, and must not wait on the provider to do it.</b> A cold or stale entry starts one
/// fetch, shared by every caller that arrives while it runs, and a caller waits for it only up to its own deadline
/// — past that it gets the previous list if there is one, or nothing, and the fetch finishes in the background for
/// the next invoice. A failed fetch keeps the previous list and is not retried for a minute, so a provider outage
/// costs checkout its stablecoin option rather than a stampede of retries from every new invoice.
/// </para>
/// <para>
/// <b>Keyed by the SDK instance as well as the store.</b> A route handle is the SDK's own object and is passed back
/// to it; after a reconnect or a seed change the list is read again from the instance that will be quoting.
/// </para>
/// </remarks>
public sealed class StablecoinRouteCache
{
    /// <summary>How long a failed fetch keeps the store from asking again.</summary>
    internal static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly ILogger<StablecoinRouteCache> _logger;

    public StablecoinRouteCache(TimeProvider time, ILogger<StablecoinRouteCache> logger)
    {
        _time = time;
        _logger = logger;
    }

    private sealed class Entry
    {
        public required ISparkSdkClient Sdk { get; init; }
        public IReadOnlyList<SparkCrossChainReceiveRoute>? Routes { get; set; }

        /// <summary>Until when <see cref="Routes"/> is served without asking the provider again.</summary>
        public DateTimeOffset FreshUntil { get; set; }

        /// <summary>After a failed fetch, until when no new one is started, whether or not a list exists.</summary>
        public DateTimeOffset RetryAfter { get; set; }

        public Task? Refresh { get; set; }
    }

    /// <summary>
    /// The store's receive routes, or null when none have ever been read and none arrive within
    /// <paramref name="deadline"/>.
    /// </summary>
    public async Task<IReadOnlyList<SparkCrossChainReceiveRoute>?> GetAsync(
        string storeId,
        ISparkSdkClient sdk,
        TimeSpan deadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        ArgumentNullException.ThrowIfNull(sdk);

        Task refresh;
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(storeId, out entry!) || !ReferenceEquals(entry.Sdk, sdk))
            {
                entry = new Entry { Sdk = sdk };
                _entries[storeId] = entry;
            }

            var now = _time.GetUtcNow();
            if (entry.Routes is { } fresh && now < entry.FreshUntil)
                return fresh;
            if (entry.Refresh is null && now < entry.RetryAfter)
                return entry.Routes;

            entry.Refresh ??= RefreshAsync(storeId, entry);
            refresh = entry.Refresh;
        }

        try
        {
            await refresh.WaitAsync(deadline, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogDebug(
                "Store {StoreId}: the USDC/USDT network list took longer than {Seconds}s; using what is cached",
                storeId, deadline.TotalSeconds);
        }

        return entry.Routes;
    }

    /// <summary>Drops a store's list, so the next read goes to the provider. For a reconnect or a removal.</summary>
    public void Invalidate(string storeId)
    {
        lock (_entries)
            _entries.TryRemove(storeId, out _);
    }

    private async Task RefreshAsync(string storeId, Entry entry)
    {
        // Yield first, so the caller holding the lock has released it before any SDK work begins.
        await Task.Yield();
        try
        {
            var routes = await entry.Sdk.GetCrossChainReceiveRoutesAsync().ConfigureAwait(false);
            lock (_entries)
            {
                entry.Routes = routes;
                entry.FreshUntil = _time.GetUtcNow() + StablecoinPayments.RouteCacheTtl;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Store {StoreId}: could not read the USDC/USDT networks from Spark ({Reason}); checkout offers "
                + "what was last read, if anything, and asks again in a minute",
                storeId, SparkErrors.Describe(ex));
            lock (_entries)
                entry.RetryAfter = _time.GetUtcNow() + FailureBackoff;
        }
        finally
        {
            lock (_entries)
                entry.Refresh = null;
        }
    }
}
