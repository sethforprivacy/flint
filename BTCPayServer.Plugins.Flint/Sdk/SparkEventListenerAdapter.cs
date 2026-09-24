using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Breez.Sdk.Spark;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>Kind of SDK event, flattened so consumers never switch on SDK types.</summary>
public enum SparkEventKind
{
    Synced,

    /// <summary>
    /// A payment completed — <b>not</b> a reliable settlement trigger on its own. It has been observed
    /// firing twice for one payment, and being skipped entirely for a completed receive.
    /// </summary>
    PaymentSucceeded,

    /// <summary>
    /// A payment started. Treated as "go and check this one", because for at least one completed receive
    /// this was the <em>only</em> event that ever arrived.
    /// </summary>
    PaymentPending,

    PaymentFailed,

    /// <summary>An on-chain static deposit was auto-claimed into the wallet.</summary>
    ClaimedDeposits,

    /// <summary>An on-chain static deposit was seen, before it is claimed.</summary>
    NewDeposits,

    /// <summary>
    /// A payment the SDK already reported gained provider metadata — for this plugin, the Orchestra conversion
    /// details on an inbound USDC/USDT receive.
    /// </summary>
    /// <remarks>
    /// Routed exactly like a completion, because for a cross-chain receive it usually <em>is</em> the
    /// completion as far as the plugin can tell: the inbound Spark transfer is reported first as a plain
    /// transfer with nothing tying it to a quote, and the conversion details arrive only with this event, once
    /// the provider confirms the order.
    /// </remarks>
    PaymentMetadataUpdated,

    Other
}

/// <summary>One SDK event, tagged with the store whose instance emitted it.</summary>
public sealed record SparkEventEnvelope(string StoreId, SparkEventKind Kind, Payment? Payment);

/// <summary>
/// The SDK event listener. Enqueues and returns; all real work happens on the consumer loop.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class is load-bearing for the stability of the entire BTCPay process, and its constraints
/// are not negotiable.</b> The SDK dispatches events synchronously and inline on an SDK-owned thread:
/// the call that emitted the event does not return until every listener's task completes. Measured
/// consequences (spike notes §7):
/// </para>
/// <list type="bullet">
/// <item><description>A listener that sleeps 3 s makes <c>SyncWallet()</c> take 4.8 s instead of 0.8 s
/// — a slow listener inflates <em>every</em> SDK call the plugin makes.</description></item>
/// <item><description>A listener that throws synchronously, returns a faulted task, returns null, or
/// returns a task that never completes <b>hangs the whole process permanently</b>. All four variants
/// were tested; in every case the emitting call never returned, no exception surfaced, no timeout
/// fired, and the process had to be killed. There is no recovery.</description></item>
/// </list>
/// <para>
/// Therefore: the whole body is wrapped in <c>try/catch(Exception)</c>; nothing is awaited; the method
/// is <b>not</b> <c>async</c> (an <c>async</c> method that throws before its first await still returns
/// a faulted task, and marking it <c>async</c> invites someone to add an <c>await</c> later); and the
/// return value is always the already-completed <see cref="Task.CompletedTask"/>.
/// </para>
/// <para>
/// The writer must be a bounded channel and this class must only ever use the non-blocking
/// <c>TryWrite</c>. That pairing means a stalled consumer degrades into refused-and-logged
/// notifications — which the plugin's own reconciliation task then recovers — rather than into
/// unbounded memory growth or a blocked SDK thread. The channel is created with
/// <c>BoundedChannelFullMode.Wait</c> precisely because the drop modes return <c>true</c> and evict
/// silently, which would hide the loss.
/// </para>
/// </remarks>
public sealed class SparkEventListenerAdapter : EventListener
{
    private readonly string _storeId;
    private readonly ChannelWriter<SparkEventEnvelope> _writer;
    private readonly ILogger _logger;
    private long _droppedEvents;

    public SparkEventListenerAdapter(string storeId, ChannelWriter<SparkEventEnvelope> writer, ILogger logger)
    {
        _storeId = storeId ?? throw new ArgumentNullException(nameof(storeId));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Number of events this listener could not enqueue. Surfaced for diagnostics.
    /// </summary>
    /// <remarks>
    /// Read and written with <see cref="Interlocked"/> because the SDK dispatches from several of its own
    /// threads — observed on thread ids 5, 7 and 10 — so a plain increment could lose counts.
    /// </remarks>
    public long DroppedEvents => Interlocked.Read(ref _droppedEvents);

    public Task OnEvent(SdkEvent @event)
    {
        try
        {
            // Dropped here rather than downstream: the SDK's background leaf optimisation emits a stream
            // of these (ten in one four-minute window during the funded run, with per-round progress) and
            // nothing in this plugin acts on them. Letting them through would consume queue capacity that
            // settlement events need.
            if (@event is SdkEvent.AutoOptimization)
                return Task.CompletedTask;

            var envelope = @event switch
            {
                SdkEvent.PaymentSucceeded succeeded =>
                    new SparkEventEnvelope(_storeId, SparkEventKind.PaymentSucceeded, succeeded.payment),
                SdkEvent.PaymentPending pending =>
                    new SparkEventEnvelope(_storeId, SparkEventKind.PaymentPending, pending.payment),
                SdkEvent.PaymentFailed failed =>
                    new SparkEventEnvelope(_storeId, SparkEventKind.PaymentFailed, failed.payment),
                SdkEvent.Synced => new SparkEventEnvelope(_storeId, SparkEventKind.Synced, null),
                SdkEvent.ClaimedDeposits => new SparkEventEnvelope(_storeId, SparkEventKind.ClaimedDeposits, null),
                SdkEvent.NewDeposits => new SparkEventEnvelope(_storeId, SparkEventKind.NewDeposits, null),
                SdkEvent.PaymentMetadataUpdated updated =>
                    new SparkEventEnvelope(_storeId, SparkEventKind.PaymentMetadataUpdated, updated.payment),
                _ => new SparkEventEnvelope(_storeId, SparkEventKind.Other, null)
            };

            if (!_writer.TryWrite(envelope))
            {
                // Either the channel is closed (this store is being torn down) or the consumer has fallen
                // behind. Both are survivable: the plugin's reconciliation task re-checks every unpaid invoice
                // against the service provider on a timer and at startup.
                var dropped = Interlocked.Increment(ref _droppedEvents);
                _logger.LogWarning(
                    "Dropped a Spark {Kind} event for store {StoreId} ({Dropped} dropped in total); any "
                    + "settlement it carried will be recovered by the reconciliation task",
                    envelope.Kind, _storeId, dropped);
            }
        }
        catch (Exception ex)
        {
            // Must never propagate: an exception here deadlocks the SDK dispatcher permanently.
            try
            {
                _logger.LogError(ex, "Failed to enqueue a Spark event for store {StoreId}", _storeId);
            }
            catch
            {
                // Even the logger must not be allowed to throw out of this method. There is nowhere
                // left to report to, and a process-wide deadlock is strictly worse than a lost log line.
            }
        }

        return Task.CompletedTask;
    }
}
