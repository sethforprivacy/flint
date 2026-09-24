using System.Numerics;
using System.Threading.Channels;
using Breez.Sdk.Spark;
using BTCPayServer.Plugins.Flint.Sdk;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using SdkPaymentStatus = Breez.Sdk.Spark.PaymentStatus;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The listener-safety contract, written first on purpose.
/// </summary>
/// <remarks>
/// The SDK dispatches events synchronously and inline: the call that emitted the event does not return
/// until every listener's task completes. A listener that throws, returns a faulted task, returns null, or
/// returns a task that never completes deadlocks the entire BTCPay process permanently — verified in the
/// spike across four separate variants, none of which produced an exception, a timeout or a crash. These
/// tests exist so nobody reintroduces that by "improving" the adapter.
/// </remarks>
public class SparkEventListenerAdapterTests
{
    private static Payment SamplePayment(PaymentType type = PaymentType.Receive) => new(
        id: "p1",
        paymentType: type,
        status: SdkPaymentStatus.Completed,
        amount: new BigInteger(1000),
        fees: BigInteger.Zero,
        timestamp: 1_785_806_574,
        method: PaymentMethod.Lightning,
        details: new PaymentDetails.Lightning("d", "lnbcrt-one", "02", null!, null, null, null, null),
        conversionDetails: null!);

    private static (SparkEventListenerAdapter Adapter, Channel<SparkEventEnvelope> Channel) Create(
        int capacity = 8,
        // Wait, not DropWrite: only Wait makes TryWrite report a full queue instead of evicting silently.
        BoundedChannelFullMode fullMode = BoundedChannelFullMode.Wait)
    {
        var channel = Channel.CreateBounded<SparkEventEnvelope>(
            new BoundedChannelOptions(capacity) { FullMode = fullMode });
        var adapter = new SparkEventListenerAdapter("store-1", channel.Writer, NullLogger.Instance);
        return (adapter, channel);
    }

    [Fact]
    public void OnEvent_returns_an_already_completed_task()
    {
        var (adapter, _) = Create();

        var task = adapter.OnEvent(new SdkEvent.PaymentSucceeded(SamplePayment()));

        // Not merely "completes quickly": it must be already-completed on return. Anything the SDK has to
        // wait on stalls the emitting call, and anything that never completes hangs the process.
        Assert.True(task.IsCompletedSuccessfully);
        Assert.Same(Task.CompletedTask, task);
    }

    [Fact]
    public void OnEvent_enqueues_a_settlement_with_its_payment_and_store()
    {
        var (adapter, channel) = Create();
        var payment = SamplePayment();

        adapter.OnEvent(new SdkEvent.PaymentSucceeded(payment));

        Assert.True(channel.Reader.TryRead(out var envelope));
        Assert.Equal("store-1", envelope!.StoreId);
        Assert.Equal(SparkEventKind.PaymentSucceeded, envelope.Kind);
        Assert.Same(payment, envelope.Payment);
    }

    [Theory]
    [InlineData(SparkEventKind.PaymentPending)]
    [InlineData(SparkEventKind.PaymentFailed)]
    public void OnEvent_tags_the_other_payment_events(SparkEventKind expected)
    {
        var (adapter, channel) = Create();
        var payment = SamplePayment();

        SdkEvent evt = expected is SparkEventKind.PaymentPending
            ? new SdkEvent.PaymentPending(payment)
            : new SdkEvent.PaymentFailed(payment);
        adapter.OnEvent(evt);

        Assert.True(channel.Reader.TryRead(out var envelope));
        Assert.Equal(expected, envelope!.Kind);
    }

    [Fact]
    public void OnEvent_handles_events_that_carry_no_payment()
    {
        var (adapter, channel) = Create();

        adapter.OnEvent(new SdkEvent.Synced());
        adapter.OnEvent(new SdkEvent.ClaimedDeposits([]));
        adapter.OnEvent(new SdkEvent.LightningAddressChanged(null!));

        Assert.Equal(3, channel.Reader.Count);
        Assert.True(channel.Reader.TryRead(out var synced));
        Assert.Equal(SparkEventKind.Synced, synced!.Kind);
        Assert.Null(synced.Payment);
    }

    [Fact]
    public void AutoOptimization_events_are_dropped_without_consuming_queue_capacity()
    {
        // The SDK's background leaf optimisation emits a steady stream of these — ten in one four-minute
        // window during the funded run — and nothing in the plugin acts on them. Letting them through would
        // evict settlement events from a bounded queue.
        var (adapter, channel) = Create(capacity: 2);

        for (var i = 0; i < 10; i++)
        {
            adapter.OnEvent(new SdkEvent.AutoOptimization(new AutoOptimizationEvent.Started(1)));
            adapter.OnEvent(new SdkEvent.AutoOptimization(new AutoOptimizationEvent.Completed()));
        }

        adapter.OnEvent(new SdkEvent.PaymentSucceeded(SamplePayment()));

        Assert.Equal(1, channel.Reader.Count);
        Assert.True(channel.Reader.TryRead(out var envelope));
        Assert.Equal(SparkEventKind.PaymentSucceeded, envelope!.Kind);
        Assert.Equal(0, adapter.DroppedEvents);
    }

    [Fact]
    public void A_metadata_update_is_forwarded_with_its_payment()
    {
        // The one event that carries a cross-chain receive's conversion details. Folded into Other, a USDC or
        // USDT payment would reach the plugin only through the next reconciliation pass.
        var (adapter, channel) = Create();
        var payment = SamplePayment();

        adapter.OnEvent(new SdkEvent.PaymentMetadataUpdated(payment));

        Assert.True(channel.Reader.TryRead(out var envelope));
        Assert.Equal(SparkEventKind.PaymentMetadataUpdated, envelope!.Kind);
        Assert.Same(payment, envelope.Payment);
    }

    [Fact]
    public void Deposit_events_are_forwarded()
    {
        // On-chain deposits are real money arriving and the operator should see them, even though they settle
        // no BTCPay invoice.
        var (adapter, channel) = Create();

        adapter.OnEvent(new SdkEvent.NewDeposits([]));
        adapter.OnEvent(new SdkEvent.ClaimedDeposits([]));

        Assert.True(channel.Reader.TryRead(out var newDeposits));
        Assert.Equal(SparkEventKind.NewDeposits, newDeposits!.Kind);
        Assert.True(channel.Reader.TryRead(out var claimed));
        Assert.Equal(SparkEventKind.ClaimedDeposits, claimed!.Kind);
    }

    [Fact]
    public void A_duplicated_settlement_event_is_forwarded_twice_for_the_consumer_to_deduplicate()
    {
        // PaymentSucceeded has been observed firing twice for one payment, on two threads 57 ms apart. The
        // adapter deliberately does not try to deduplicate — it has no durable state and cannot do so
        // correctly. Deduplication is the store's compare-and-set.
        var (adapter, channel) = Create();
        var payment = SamplePayment();

        adapter.OnEvent(new SdkEvent.PaymentSucceeded(payment));
        adapter.OnEvent(new SdkEvent.PaymentSucceeded(payment));

        Assert.Equal(2, channel.Reader.Count);
    }

    [Fact]
    public void A_null_event_does_not_propagate()
    {
        var (adapter, _) = Create();

        var task = adapter.OnEvent(null!);

        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public void A_full_queue_drops_and_counts_rather_than_blocking()
    {
        var (adapter, channel) = Create(capacity: 2);

        for (var i = 0; i < 5; i++)
        {
            var task = adapter.OnEvent(new SdkEvent.PaymentSucceeded(SamplePayment()));
            Assert.True(task.IsCompletedSuccessfully);
        }

        Assert.Equal(2, channel.Reader.Count);
        Assert.Equal(3, adapter.DroppedEvents);
    }

    [Fact]
    public void A_completed_channel_does_not_propagate_an_error()
    {
        // What happens during shutdown: the service completes the writer while the SDK is still dispatching.
        var (adapter, channel) = Create();
        channel.Writer.Complete();

        var task = adapter.OnEvent(new SdkEvent.PaymentSucceeded(SamplePayment()));

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(1, adapter.DroppedEvents);
    }

    [Fact]
    public void A_throwing_writer_does_not_propagate()
    {
        var adapter = new SparkEventListenerAdapter("store-1", new ThrowingWriter(), NullLogger.Instance);

        var task = adapter.OnEvent(new SdkEvent.PaymentSucceeded(SamplePayment()));

        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public void A_throwing_logger_does_not_propagate()
    {
        // Belt and braces: even the failure reporting path must not throw back into the SDK's dispatcher.
        var adapter = new SparkEventListenerAdapter(
            "store-1", new ThrowingWriter(), new ThrowingLogger());

        var task = adapter.OnEvent(new SdkEvent.PaymentSucceeded(SamplePayment()));

        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public void OnEvent_does_no_work_beyond_enqueueing()
    {
        // The real property is structural, not temporal: a wall-clock bound would pass even if the method did
        // something a hundred times slower. What must hold is that every call completes synchronously and the
        // only observable effect is one queued envelope — no awaiting, no SDK calls, no I/O.
        var (adapter, channel) = Create(capacity: 1024);

        for (var i = 0; i < 1000; i++)
        {
            var task = adapter.OnEvent(new SdkEvent.PaymentSucceeded(SamplePayment()));
            Assert.True(task.IsCompletedSuccessfully);
            Assert.Same(Task.CompletedTask, task);
        }

        Assert.Equal(1000, channel.Reader.Count);
        Assert.Equal(0, adapter.DroppedEvents);
    }

    private sealed class ThrowingWriter : ChannelWriter<SparkEventEnvelope>
    {
        public override bool TryWrite(SparkEventEnvelope item) => throw new InvalidOperationException("boom");

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class ThrowingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            throw new InvalidOperationException("boom");

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => throw new InvalidOperationException("boom");
    }
}
