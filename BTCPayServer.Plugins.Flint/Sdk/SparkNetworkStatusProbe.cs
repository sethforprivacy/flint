using System;
using System.Threading;
using System.Threading.Tasks;
using Breez.Sdk.Spark;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>
/// The real <see cref="ISparkNetworkStatusProbe"/>, over the SDK's static <c>GetSparkStatus</c>.
/// </summary>
/// <remarks>
/// <para>
/// Bounded by <see cref="SparkDeadline"/> and never allowed to throw. This runs on a request thread rendering
/// a status page, the call is a network round trip to a third party, and no SDK call can be cancelled — so a
/// slow or unreachable status endpoint must degrade to "unknown" rather than hang the page or 500 it.
/// </para>
/// <para>
/// The result is served from a process-wide cache (<see cref="SuccessTtl"/> for a status, shorter
/// <see cref="RetryTtl"/> after a failure). <c>GetSparkStatus</c> describes the Spark network as a whole —
/// it is a process-global static carrying the provider's own <c>lastUpdated</c> — so re-running it per store
/// per request multiplies an identical third-party round trip across every polled store and every concurrent
/// viewer, which is request amplification against a provider this plugin does not own. The single-flight gate
/// additionally folds concurrent first calls into one round trip. The cached value is diagnostic, never
/// financial: no settlement, sweep or credit decision reads it.
/// </para>
/// <para>
/// The 5-second page budget is per caller, not per probe: a caller that arrives while another is mid-probe
/// spends at most the same total wait, and leaves with the previous cache (possibly null, possibly stale) if
/// even that expires. The in-flight probe itself keeps running under its own deadline and refreshes the cache.
/// </para>
/// <para>
/// Loading the native library is a side effect of the first SDK call in the process (~450 ms). In practice
/// <c>SparkService.StartAsync</c> has already paid that cost by initialising logging, so this is not the
/// first call; the deadline covers the case where it is.
/// </para>
/// </remarks>
public sealed class SparkNetworkStatusProbe : ISparkNetworkStatusProbe
{
    /// <summary>
    /// Shorter than <see cref="Constants.SdkCallDeadline"/>: that budget is sized for background loops, and a
    /// page a merchant is waiting on cannot spend 30 s on an optional detail.
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a successful read is reused across stores and callers. Well below the cadence at which a
    /// network-status outage becomes operationally interesting, and above any plausible page-poll rate.
    /// </summary>
    internal static readonly TimeSpan SuccessTtl = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How long a failed read is held before trying again. Short enough that a status page opened after an
    /// outage ends shows recovery promptly; long enough that a dead endpoint is not re-struck per request.
    /// </summary>
    internal static readonly TimeSpan RetryTtl = TimeSpan.FromSeconds(10);

    private readonly ILogger<SparkNetworkStatusProbe> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<ILogger, CancellationToken, Task<SparkNetworkStatus?>> _probe;
    private readonly Func<DateTimeOffset> _utcNow;

    // Cache fields are touched from many request threads without a lock around the read. Reference and
    // 64-bit-tick access is atomic on the runtimes BTCPay ships; tearing would at worst serve one probe's
    // status with another's expiry tick, which degrades to an early re-probe or a brief stale read — both
    // benign for a diagnostic. The gate serialises writes.
    private SparkNetworkStatus? _cached;
    private long _expiresUtcTicks;

    public SparkNetworkStatusProbe(ILogger<SparkNetworkStatusProbe> logger)
        : this(logger, static (lg, ct) => ProbeCoreAsync(lg, ct), static () => DateTimeOffset.UtcNow)
    {
    }

    /// <summary>
    /// Test seam: the SDK call and the clock, so cache semantics are testable without a network.
    /// </summary>
    internal SparkNetworkStatusProbe(
        ILogger<SparkNetworkStatusProbe> logger,
        Func<ILogger, CancellationToken, Task<SparkNetworkStatus?>> probe,
        Func<DateTimeOffset> utcNow)
    {
        _logger = logger;
        _probe = probe;
        _utcNow = utcNow;
    }

    /// <inheritdoc />
    public async Task<SparkNetworkStatus?> TryGetAsync(CancellationToken cancellationToken = default)
    {
        if (_utcNow() < FromTicks(Volatile.Read(ref _expiresUtcTicks)))
            return _cached;

        // Bounded wait, own-budget: never more than Deadline for the whole call, and never a throw on
        // timeout — the previous cache (or null) is the honest answer for a caller that could not get in.
        var entered = false;
        try
        {
            entered = await _gate.WaitAsync(Deadline, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return _cached;
        }

        if (!entered)
            return _cached;

        try
        {
            // Double-check under the gate: a probe launched between the fast-path read and the gate is
            // exactly what single-flight means — reuse its result, do not re-probe.
            if (_utcNow() < FromTicks(Volatile.Read(ref _expiresUtcTicks)))
                return _cached;

            var status = await _probe(_logger, cancellationToken).ConfigureAwait(false);
            _cached = status;
            // Volatile.Write after the value write: a reader that sees the new expiry also sees the new
            // status (release ordering), never the reverse.
            Volatile.Write(ref _expiresUtcTicks, _utcNow().Add(status is null ? RetryTtl : SuccessTtl).UtcTicks);
            return status;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static DateTimeOffset FromTicks(long ticks) => new(ticks, TimeSpan.Zero);

    private static async Task<SparkNetworkStatus?> ProbeCoreAsync(
        ILogger logger, CancellationToken cancellationToken)
    {
        // Kept static and exception-free by the same rules as before: the deadline degrades a slow
        // endpoint to null, and the catch is the last line of defence against a throw from the FFI boundary.
        try
        {
            var status = await SparkDeadline
                .OrNullAsync(
                    // No proxy: the plugin configures none anywhere else either, and this probe must reach the
                    // same network the wallets do.
                    BreezSdkSparkMethods.GetSparkStatus(new GetSparkStatusRequest(proxy: null)),
                    Deadline,
                    () => logger.LogDebug(
                        "Reading the Spark network status exceeded {Seconds}s", Deadline.TotalSeconds),
                    cancellationToken)
                .ConfigureAwait(false);

            if (status is null)
                return null;

            return new SparkNetworkStatus(
                status.status.ToString(),
                // Unix seconds, as a u64. Clamped rather than trusted: a malformed value from a third-party
                // endpoint must not throw out of a status page.
                ToTimestamp(status.lastUpdated));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read the Spark network status");
            return null;
        }
    }


    private static DateTimeOffset ToTimestamp(ulong unixSeconds)
    {
        const long maxUnixSeconds = 253_402_300_799; // 9999-12-31T23:59:59Z, the limit DateTimeOffset accepts.
        var seconds = unixSeconds > maxUnixSeconds ? maxUnixSeconds : (long)unixSeconds;
        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }
}
