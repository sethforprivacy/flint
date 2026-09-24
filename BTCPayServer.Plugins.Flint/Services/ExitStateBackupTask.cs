using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.HostedServices;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// Periodically takes the automatic exit-state backups that are due.
/// </summary>
/// <remarks>
/// <para>
/// The safety half of the arrangement, not the trigger half: <see cref="ExitStateBackupScheduler"/> has
/// already decided that an event-driven refresh is worth taking, and a pass whose only job would be to ask
/// <c>ShouldTake</c> costs one dictionary read per store. One minute matches the resolution every other
/// scheduled pass in this plugin works at, and it is what bounds the latency of the debounced refreshes —
/// a deposit burst schedules its backup, and this task is when it lands.
/// </para>
/// <para>
/// Registered through BTCPay's <c>AddScheduledTask</c>, which runs <see cref="Do"/> on a fixed interval
/// and logs rather than rethrows. <see cref="SparkService.TakeDueExitStateBackupsAsync"/> additionally
/// guarantees it does not throw on per-store failures, so a wallet that cannot export cannot wedge this
/// task or any other store's backup.
/// </para>
/// </remarks>
public class ExitStateBackupTask : IPeriodicTask
{
    private readonly SparkService _sparkService;

    public ExitStateBackupTask(SparkService sparkService)
    {
        _sparkService = sparkService;
    }

    public Task Do(CancellationToken cancellationToken) =>
        _sparkService.TakeDueExitStateBackupsAsync(cancellationToken);
}
