using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.HostedServices;
using BTCPayServer.Plugins.Flint.Data;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// Drives <see cref="SparkSweepEngine"/> over every running store on a timer.
/// </summary>
/// <remarks>
/// <para>
/// Registered with BTCPay's <c>AddScheduledTask&lt;SweepTask&gt;</c>, which runs <see cref="Do"/> on a fixed
/// interval and logs rather than rethrows. All this class does is enumerate stores and keep one store's failure
/// from stopping the rest; every decision about whether and what to sweep lives in the engine, so that the
/// "sweep now" button goes through identical code.
/// </para>
/// <para>
/// <b>Why every running store, every pass, including stores with sweeping switched off.</b> A pass does more than
/// compare a balance to a threshold: its first job is to resolve any sweep whose outcome is unknown, which is how a
/// crash mid-send is recovered. A store that has since had sweeping disabled can still own such a record, and
/// leaving it unresolved would leave the merchant's history lying about where their money went. The engine skips
/// the disabled store's threshold check immediately afterwards, at the cost of one database read.
/// </para>
/// <para>
/// Only stores with a <em>running</em> wallet are visited, because resolving a record needs the SDK to ask. A store
/// whose wallet will not start therefore keeps its unresolved records until it does — which is correct: there is no
/// way to learn the outcome without the wallet, and the reason the wallet is down is already on the status page.
/// </para>
/// <para>
/// <b>On the interval.</b> Sweep frequency does not affect what sweeping costs: the trigger is a balance
/// threshold, not a clock, so a shorter interval does not buy more cooperative exits — it only shortens the delay
/// between crossing the threshold and leaving Spark, and the delay before a crashed in-flight sweep is resolved.
/// What it does cost is one <c>SyncWallet</c> plus one <c>GetInfo</c> per configured store per pass, about a
/// second of mostly-waiting work. Two minutes keeps both latencies to a couple of minutes at well under a percent
/// duty cycle, and being coprime with nothing in particular it drifts relative to the one-minute reconciliation
/// pass rather than contending with it on the same store's SDK on every single tick.
/// </para>
/// <para>
/// <b>The pass is bounded, and the store order rotates.</b> "About a second per store" is a healthy store's
/// figure, and there is nothing underneath it: the engine makes no deadline-bounded SDK call, so one wallet
/// whose <c>SyncWallet</c> never returns would hold one of BTCPay's three shared scheduled-task workers for the
/// life of the process and every store after it in the list would never be swept again.
/// <see cref="SparkStorePassScheduler"/> bounds both halves of that — see its remarks for the arithmetic.
/// </para>
/// </remarks>
public class SweepTask : IPeriodicTask
{
    private readonly SparkService _sparkService;
    private readonly SparkSweepEngine _engine;
    private readonly SparkStorePassScheduler _pass;
    private readonly ILogger<SweepTask> _logger;

    public SweepTask(
        SparkService sparkService,
        SparkSweepEngine engine,
        TimeProvider timeProvider,
        ILogger<SweepTask> logger)
    {
        _sparkService = sparkService;
        _engine = engine;
        _logger = logger;

        // Held on the task rather than created per pass, because the rotation position is the whole point and
        // it has to survive from one pass to the next. BTCPay registers a scheduled task with
        // TryAddSingleton, so there is exactly one of these per process.
        _pass = new SparkStorePassScheduler(
            "sweep", Constants.SweepPassBudget, Constants.SweepStoreDeadline, timeProvider, logger);
    }

    public async Task Do(CancellationToken cancellationToken)
    {
        var storeIds = await _sparkService.GetRunningStoreIds().ConfigureAwait(false);

        await _pass.RunAsync(
                storeIds,
                SweepStoreAsync,
                // Per store and non-fatal. The engine already handles every failure it can classify; anything
                // reaching here is a bug or an infrastructure failure, and it must not stop the other stores
                // from having their in-flight sweeps resolved.
                (storeId, ex) =>
                    _logger.LogError(ex, "Store {StoreId}: the automatic Spark sweep pass failed", storeId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SweepStoreAsync(string storeId, CancellationToken cancellationToken)
    {
        var result = await _engine
            .RunAsync(storeId, SweepTrigger.Automatic, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Only the interesting outcomes are logged at Information. A skip is the overwhelmingly common case —
        // most stores sit below their threshold most of the time — and logging it would bury the one line an
        // operator wants in a hundred they do not.
        if (result.Kind is SweepOutcomeKind.Swept)
        {
            _logger.LogInformation(
                "Store {StoreId}: automatic Spark sweep — {Reason}", storeId, result.Reason);
        }
        else
        {
            _logger.LogDebug(
                "Store {StoreId}: automatic Spark sweep {Kind} — {Reason}",
                storeId, result.Kind, result.Reason);
        }
    }
}
