using System.Numerics;
using Breez.Sdk.Spark;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Xunit;
using SdkPaymentStatus = Breez.Sdk.Spark.PaymentStatus;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The path from an SDK event to a settled invoice, driven through the real <c>SparkService</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What was missing, and why it is a gap rather than a duplicate.</b> The settlement <em>logic</em> is
/// covered thoroughly by <c>SparkSettlementReconcilerTests</c>, and the mapping from an SDK payment to the
/// plugin's own shape by <c>SparkPaymentMapperTests</c>. Neither touches the <b>wiring</b> between them:
/// event → envelope → direction filter → deposit filter → payment-hash check → authoritative status re-read →
/// reconciler. Every one of those steps is a private method on <c>SparkService</c>, which is why they were
/// repeatedly flagged and repeatedly skipped. A correct reconciler reached by wiring that filters on the wrong
/// thing settles nothing, or settles the wrong leg, and both suites stay green.
/// </para>
/// <para>
/// <b>No new seam was needed.</b> <c>FakeSparkSdkClientFactory</c> already keeps the
/// <c>ChannelWriter&lt;SparkEventEnvelope&gt;</c> it was handed for each store, and the service's own consumer
/// loop reads it — so a test writes an envelope exactly where the real <c>SparkEventListenerAdapter</c> writes
/// one, and everything downstream is production code. Nothing here was widened to <c>internal</c> and nothing
/// is reimplemented. The envelopes carry real <c>Breez.Sdk.Spark.Payment</c> records, constructed the way
/// <c>SparkPaymentMapperTests</c> constructs them, because the mapper is part of what is under test.
/// </para>
/// <para>
/// <b>The consumer is asynchronous, so every assertion waits for an observable effect</b> rather than sleeping
/// a fixed interval. <see cref="WaitFor"/> polls a predicate to a timeout and fails with a message naming what
/// never happened; a fixed sleep would be either flaky or slow, and on a green run these settle in
/// milliseconds.
/// </para>
/// </remarks>
public class SparkServiceEventWiringTests
{
    private const string StoreId = "wired-store";
    private const string OtherStoreId = "wired-store-2";

    private static readonly string Hash = PaymentFixture.PaymentHash;
    private static readonly string OtherHash = PaymentFixture.OtherPaymentHash;

    // ---------------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------------

    private static SparkHtlcDetails Htlc(string hash) =>
        new(hash, PaymentFixture.Preimage, 0, SparkHtlcStatus.PreimageShared);

    private static Payment LightningLeg(
        string id,
        PaymentType type,
        string hash,
        SdkPaymentStatus status = SdkPaymentStatus.Completed,
        long amount = 1_000,
        long fees = 0) =>
        new(
            id: id,
            paymentType: type,
            status: status,
            amount: new BigInteger(amount),
            fees: new BigInteger(fees),
            timestamp: 1_785_806_574,
            method: PaymentMethod.Lightning,
            details: new PaymentDetails.Lightning(
                description: "an invoice",
                invoice: "lnbcrt-one",
                destinationPubkey: "02fe4b",
                htlcDetails: Htlc(hash),
                lnurlPayInfo: null,
                lnurlWithdrawInfo: null,
                lnurlReceiveMetadata: null,
                conversionInfo: null),
            conversionDetails: null!);

    /// <summary>An auto-claimed on-chain deposit: a Receive with no payment hash and a claim fee netted out.</summary>
    private static Payment Deposit(string id, long amount = 99_901, long fees = 99) =>
        new(
            id: id,
            paymentType: PaymentType.Receive,
            status: SdkPaymentStatus.Completed,
            amount: new BigInteger(amount),
            fees: new BigInteger(fees),
            timestamp: 1_785_847_217,
            method: PaymentMethod.Deposit,
            details: new PaymentDetails.Deposit("e2e11469", 1),
            conversionDetails: null!);

    private static InvoiceRecord Unpaid(string storeId, string hash) => new()
    {
        PaymentHash = hash,
        StoreId = storeId,
        Bolt11 = "lnbcrt-one",
        AmountMsat = 1_000_000,
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        Status = InvoiceRecordStatus.Unpaid
    };

    /// <summary>A started service with one configured store and one unpaid invoice on it.</summary>
    private static async Task<SparkServiceHarness> StartedAsync(
        TimeSpan? confirmStatusDeadline = null,
        params string[] storeIds)
    {
        var h = SparkServiceHarness.Create(confirmStatusDeadline: confirmStatusDeadline);
        var ids = storeIds.Length == 0 ? [StoreId] : storeIds;
        for (var i = 0; i < ids.Length; i++)
            h.SeedStore(ids[i], SparkServiceHarness.MnemonicFor(i + 1));

        await h.Service.StartAsync(CancellationToken.None);
        return h;
    }

    private static void Emit(SparkServiceHarness h, string storeId, SparkEventKind kind, Payment? payment) =>
        Assert.True(
            h.Sdk.EventWriters[storeId].TryWrite(new SparkEventEnvelope(storeId, kind, payment)),
            "the event channel refused the envelope");

    // ---------------------------------------------------------------------------------------------------
    // The happy path, so the negative tests below cannot pass by the wiring being dead
    // ---------------------------------------------------------------------------------------------------

    [Fact(Timeout = 60_000)]
    public async Task A_completed_receive_event_settles_its_invoice()
    {
        using var h = await StartedAsync();
        h.Invoices.Seed(Unpaid(StoreId, Hash));

        Emit(h, StoreId, SparkEventKind.PaymentSucceeded, LightningLeg("recv-1", PaymentType.Receive, Hash));

        await WaitFor(() => h.Invoices.Records[Hash].Status is InvoiceRecordStatus.Paid,
            "the invoice was never settled");
        // Credited the amount actually received, not the amount invoiced — the whole point of settling from
        // the payment rather than from the invoice. 1 000 sat received against a 1 000 000 msat invoice.
        Assert.Equal(1_000_000, h.Invoices.Records[Hash].AmountReceivedMsat);
        Assert.Equal(PaymentFixture.Preimage, h.Invoices.Records[Hash].Preimage);
    }

    // ---------------------------------------------------------------------------------------------------
    // Direction: the send leg of a self-payment must never settle the receive leg
    // ---------------------------------------------------------------------------------------------------

    [Fact(Timeout = 60_000)]
    public async Task The_send_leg_of_a_self_payment_does_not_settle_its_own_invoice()
    {
        // A self-payment produces two SDK Payment rows sharing one payment hash. The send leg carries the
        // routing fee, so settling from it would credit the merchant the wrong number — and it is the leg that
        // arrives for a payout the merchant made, where there is no invoice to settle at all. Filtering on
        // direction is what separates them; matching on payment hash alone finds both.
        //
        // NOTE ON WHAT THIS ASSERTS, because the obvious version of this test does not work. The invariant is
        // defended TWICE: here in the wiring, and again by SparkSettlementReconciler.ApplyAsync, which throws
        // ArgumentException("Only an inbound payment can settle an invoice"). Deleting the wiring's filter
        // therefore still leaves the invoice unpaid — the send leg reaches the reconciler, the reconciler
        // throws, and the consumer loop logs it and carries on. A test that only asserted "still unpaid"
        // passed against the mutation and was measuring the backstop, not the guard it named.
        //
        // So this pins which mechanism ran: the wiring recognises the send and reports it at Debug, and NO
        // event-processing error is logged. Reaching the reconciler is a defect even though it is a contained
        // one, because it converts an ordinary self-payment into an exception on the store's event loop.
        using var h = await StartedAsync();
        h.Invoices.Seed(Unpaid(StoreId, Hash));

        Emit(h, StoreId, SparkEventKind.PaymentSucceeded,
            LightningLeg("send-1", PaymentType.Send, Hash, amount: 1_000, fees: 3));

        // The wiring's own line, naming the send by its SDK id — the positive signal that the send leg really
        // was processed and not merely still sitting in the queue.
        await WaitFor(() => h.Log.AllText.Contains("send-1"), "the send leg was never processed");
        Assert.Contains("Spark send send-1", h.Log.AllText);

        // It never reached the reconciler.
        Assert.DoesNotContain("failed to process a Spark", h.Log.AllText);
        Assert.DoesNotContain("Only an inbound payment", h.Log.AllText);

        // And of course the invoice is untouched.
        Assert.Equal(InvoiceRecordStatus.Unpaid, h.Invoices.Records[Hash].Status);

        // Belt and braces: a genuine receive on the same store still settles, so none of the above is passing
        // because the loop had quietly died.
        h.Invoices.Seed(Unpaid(StoreId, OtherHash));
        Emit(h, StoreId, SparkEventKind.PaymentSucceeded, LightningLeg("recv-2", PaymentType.Receive, OtherHash));
        await WaitFor(() => h.Invoices.Records[OtherHash].Status is InvoiceRecordStatus.Paid,
            "the follow-up receive never settled");
    }

    // ---------------------------------------------------------------------------------------------------
    // Deposits: a Receive that is not a Lightning receive
    // ---------------------------------------------------------------------------------------------------

    [Fact(Timeout = 60_000)]
    public async Task A_claimed_deposit_is_not_treated_as_an_invoice_receive()
    {
        // A deposit is a Receive with method Deposit and no payment hash by nature. It must not reach the
        // reconciler, and — the part worth pinning — it must not be logged as an unattributable payment
        // either, because that warning is how an operator finds real money that could not be matched.
        using var h = await StartedAsync();

        Emit(h, StoreId, SparkEventKind.PaymentSucceeded, Deposit("dep-1"));

        await WaitFor(() => h.Log.AllText.Contains("on-chain deposit"),
            "the deposit was never reported");
        Assert.DoesNotContain("cannot be matched to a BTCPay invoice", h.Log.AllText);
        // Netted amount, not the gross deposit.
        Assert.Contains("99901", h.Log.AllText.Replace(",", "").Replace(" sat", ""));
    }

    [Fact(Timeout = 60_000)]
    public async Task A_lightning_receive_with_no_payment_hash_is_reported_rather_than_dropped()
    {
        // A direct Spark transfer: real money that settles nothing. It has to be loud, because it is the one
        // case where funds arrive and no invoice will ever be marked paid.
        using var h = await StartedAsync();
        var noHash = new Payment(
            id: "transfer-1",
            paymentType: PaymentType.Receive,
            status: SdkPaymentStatus.Completed,
            amount: new BigInteger(2_500),
            fees: BigInteger.Zero,
            timestamp: 1_785_806_574,
            method: PaymentMethod.Spark,
            details: null!,
            conversionDetails: null!);

        Emit(h, StoreId, SparkEventKind.PaymentSucceeded, noHash);

        await WaitFor(() => h.Log.AllText.Contains("cannot be matched to a BTCPay invoice"),
            "an unattributable receive was not reported");
    }

    // ---------------------------------------------------------------------------------------------------
    // USDC/USDT: a Receive with no payment hash that is not unattributable at all
    // ---------------------------------------------------------------------------------------------------

    /// <summary>An open USDC quote on Base for one invoice, as the quote endpoint would have recorded it.</summary>
    private static void SeedStablecoinQuote(SparkServiceHarness h, string quoteId = "quote-1")
    {
        h.Stablecoins.Invoices.Add("usdc-invoice", StoreId, 10m, (Services.StablecoinPayments.Usdc, ["base"]));
        h.Stablecoins.Quotes.Quotes.Add(new StablecoinQuote
        {
            Id = quoteId,
            StoreId = StoreId,
            InvoiceId = "usdc-invoice",
            PaymentMethodId = "USDC-FLINT",
            Chain = "base",
            ChainId = "8453",
            Asset = "USDC",
            ContractAddress = "0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913",
            Decimals = 6,
            DepositAddress = "0x00000000000000000000000000000000000000aa",
            DepositBaseUnits = "10080000",
            AskedBaseUnits = "10080000",
            PaymentRequest = "0x00000000000000000000000000000000000000aa",
            DueAmount = 10m,
            FeeAmount = 0.08m,
            ExpectedReceivedBaseUnits = "10000",
            DestinationAsset = "BTC",
            ServiceFeeBaseUnits = "80000",
            ServiceFeeAsset = "USDC",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(13)
        });
    }

    /// <summary>The inbound transfer the provider delivers a USDC quote with, as the SDK reports it.</summary>
    private static Payment CrossChainArrival(string id, bool withConversion = true) =>
        new(
            id: id,
            paymentType: PaymentType.Receive,
            status: SdkPaymentStatus.Completed,
            amount: new BigInteger(10_000),
            fees: BigInteger.Zero,
            timestamp: (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            method: PaymentMethod.Spark,
            details: new PaymentDetails.Spark(
                invoiceDetails: null!,
                htlcDetails: null!,
                conversionInfo: withConversion
                    ? new ConversionInfo.Orchestra(
                        orderId: "order-1",
                        quoteId: "orchestra-quote-1",
                        readToken: null!,
                        chain: "base",
                        chainId: "8453",
                        asset: "USDC",
                        recipientAddress: "spark1pgssfakewalletaddress",
                        assetAmountIn: new BigInteger(10_080_000),
                        estimatedOut: new BigInteger(10_000),
                        deliveredAmount: new BigInteger(10_004),
                        externalTxHash: "0xpayertx",
                        status: ConversionStatus.Completed,
                        feeAmount: new BigInteger(76_000),
                        serviceFeeAmount: new BigInteger(80_000),
                        serviceFeeAsset: "USDC",
                        serviceFeeAssetDecimals: 6,
                        assetDecimals: 6,
                        assetContract: "0x833589fcd6edb6e08f4c7c32d4f71b54bda02913")
                    : null!),
            conversionDetails: null!);

    [Fact(Timeout = 60_000)]
    public async Task A_usdc_receive_is_credited_to_the_invoice_its_quote_was_made_for()
    {
        using var h = await StartedAsync();
        SeedStablecoinQuote(h);

        // Metadata-updated is how the provider's details usually arrive, after the transfer itself.
        Emit(h, StoreId, SparkEventKind.PaymentMetadataUpdated, CrossChainArrival("usdc-pay-1"));

        var invoice = h.Stablecoins.Invoices.Invoices["usdc-invoice"];
        await WaitFor(() => invoice.Payments.Count == 1, "the USDC payment was never credited");
        Assert.Equal(10.08m, invoice.Payments[0].Value);
        Assert.Equal(0.08m, invoice.Payments[0].Fee);
        Assert.Equal("0xpayertx", invoice.Payments[0].Details.ExternalTxHash);
        Assert.DoesNotContain("cannot be matched to a BTCPay invoice", h.Log.AllText);

        // A duplicate of the same arrival credits nothing more.
        Emit(h, StoreId, SparkEventKind.PaymentSucceeded, CrossChainArrival("usdc-pay-1"));
        await WaitForStable(() => invoice.Payments.Count);
        Assert.Single(invoice.Payments);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_transfer_awaiting_its_provider_details_is_not_reported_as_unattributable()
    {
        // The SDK reports the inbound transfer before the provider confirms the order, with nothing in it to
        // attribute it by. While the store has open quotes that is the expected first sighting of one of them,
        // not money nothing can explain — the warning is reserved for that.
        using var h = await StartedAsync();
        SeedStablecoinQuote(h);

        Emit(h, StoreId, SparkEventKind.PaymentSucceeded, CrossChainArrival("usdc-pay-1", withConversion: false));
        await WaitFor(() => h.Log.AllText.Contains("waiting for the provider's details"),
            "the detail-less transfer was not recognised as a likely USDC/USDT receive");
        Assert.DoesNotContain("cannot be matched to a BTCPay invoice", h.Log.AllText);
        Assert.Empty(h.Stablecoins.Invoices.Invoices["usdc-invoice"].Payments);

        // Then the details arrive and it is credited.
        Emit(h, StoreId, SparkEventKind.PaymentMetadataUpdated, CrossChainArrival("usdc-pay-1"));
        await WaitFor(() => h.Stablecoins.Invoices.Invoices["usdc-invoice"].Payments.Count == 1,
            "the USDC payment was never credited once its details arrived");
    }

    // ---------------------------------------------------------------------------------------------------
    // Pending: the recorded SDK id is what lets a never-arriving completion still resolve
    // ---------------------------------------------------------------------------------------------------

    [Fact(Timeout = 60_000)]
    public async Task A_pending_receive_records_the_SDK_payment_id_without_settling()
    {
        // The SDK has been observed emitting only PaymentPending for a payment that completed, with the
        // completion visible solely from a later storage read. Recording the id here is what turns the
        // reconciliation task's recovery into a point lookup instead of a ten-page history scan — and a scan
        // anchored to the invoice's creation time is exactly what stops finding it as a wallet gets busy.
        using var h = await StartedAsync();
        h.Invoices.Seed(Unpaid(StoreId, Hash));

        Emit(h, StoreId, SparkEventKind.PaymentPending,
            LightningLeg("pending-1", PaymentType.Receive, Hash, SdkPaymentStatus.Pending));

        await WaitFor(() => h.Invoices.Records[Hash].SdkPaymentId is not null,
            "the pending payment's SDK id was never recorded against the invoice");

        Assert.Equal("pending-1", h.Invoices.Records[Hash].SdkPaymentId);
        // Pending is not paid. The status re-read said Pending too, so nothing settles.
        Assert.Equal(InvoiceRecordStatus.Unpaid, h.Invoices.Records[Hash].Status);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_pending_event_whose_payment_has_already_completed_settles_on_the_re_read()
    {
        // Pending is handled exactly like succeeded on purpose: the event's own status is not authoritative.
        // Here the event says Pending and the SDK says Completed, which is the observed real-world shape.
        using var h = await StartedAsync();
        h.Invoices.Seed(Unpaid(StoreId, Hash));

        var completed = SparkPaymentMapper.Map(
            LightningLeg("late-1", PaymentType.Receive, Hash), new StubBolt11Parser());
        h.Sdk.Clients[StoreId].PaymentsById["late-1"] = completed;

        Emit(h, StoreId, SparkEventKind.PaymentPending,
            LightningLeg("late-1", PaymentType.Receive, Hash, SdkPaymentStatus.Pending));

        await WaitFor(() => h.Invoices.Records[Hash].Status is InvoiceRecordStatus.Paid,
            "a pending event for an already-completed payment did not settle");
    }

    // ---------------------------------------------------------------------------------------------------
    // The status re-read, and the deadline that bounds it
    // ---------------------------------------------------------------------------------------------------

    [Fact(Timeout = 60_000)]
    public async Task A_hung_status_re_read_does_not_stall_the_stores_event_queue()
    {
        // The re-read is on the store's single event-consumer loop. No SDK call can be cancelled, so without a
        // deadline one hung service-provider read stalls every later event for that store behind it — including
        // the completion of a different invoice, which is money the merchant never sees marked paid.
        using var h = await StartedAsync(confirmStatusDeadline: TimeSpan.FromMilliseconds(250));
        h.Invoices.Seed(Unpaid(StoreId, Hash));
        h.Invoices.Seed(Unpaid(StoreId, OtherHash));
        h.Sdk.Clients[StoreId].HangGetPayment = true;

        Emit(h, StoreId, SparkEventKind.PaymentSucceeded, LightningLeg("hung-1", PaymentType.Receive, Hash));
        Emit(h, StoreId, SparkEventKind.PaymentSucceeded, LightningLeg("hung-2", PaymentType.Receive, OtherHash));

        // Both settle: the deadline gives up on the re-read and falls back to the event payload, which is the
        // documented behaviour — a wrongly optimistic status is corrected by the store's compare-and-set,
        // whereas a dropped settlement is only corrected minutes later by the reconciliation task.
        await WaitFor(
            () => h.Invoices.Records[Hash].Status is InvoiceRecordStatus.Paid
                  && h.Invoices.Records[OtherHash].Status is InvoiceRecordStatus.Paid,
            "a hung status re-read stalled the store's event queue");

        Assert.Contains("using the event payload", h.Log.AllText);
    }

    [Fact(Timeout = 60_000)]
    public async Task The_authoritative_status_beats_the_events_own_payload()
    {
        // The inverse of the fallback: when the SDK *is* reachable and disagrees with the event, the SDK wins.
        // An event claiming Completed for a payment that actually failed would otherwise settle an invoice
        // that was never paid.
        using var h = await StartedAsync();
        h.Invoices.Seed(Unpaid(StoreId, Hash));

        var actuallyFailed = SparkPaymentMapper.Map(
            LightningLeg("disagree-1", PaymentType.Receive, Hash, SdkPaymentStatus.Failed),
            new StubBolt11Parser());
        h.Sdk.Clients[StoreId].PaymentsById["disagree-1"] = actuallyFailed;

        Emit(h, StoreId, SparkEventKind.PaymentSucceeded,
            LightningLeg("disagree-1", PaymentType.Receive, Hash));

        await WaitFor(() => h.Sdk.Clients[StoreId].GetPaymentCalls.Contains("disagree-1"),
            "the status was never re-read");
        // Give the consumer a moment past the re-read to do the wrong thing, if it were going to.
        await WaitForStable(() => h.Invoices.Records[Hash].Status);

        Assert.Equal(InvoiceRecordStatus.Unpaid, h.Invoices.Records[Hash].Status);
    }

    // ---------------------------------------------------------------------------------------------------
    // Isolation between stores
    // ---------------------------------------------------------------------------------------------------

    [Fact(Timeout = 60_000)]
    public async Task One_stores_failing_event_does_not_starve_another_store()
    {
        // Each store has its own consumer loop, so this is really asking whether a throw escapes one loop and
        // kills it. It must not, and it must not take the other store's loop with it either.
        using var h = await StartedAsync(confirmStatusDeadline: null, StoreId, OtherStoreId);
        h.Invoices.Seed(Unpaid(StoreId, Hash));
        h.Invoices.Seed(Unpaid(OtherStoreId, OtherHash));

        // The first store's SDK throws on every call, including the status re-read.
        h.Sdk.Clients[StoreId].FailWith = new InvalidOperationException("this wallet is broken");

        Emit(h, StoreId, SparkEventKind.PaymentSucceeded, LightningLeg("broken-1", PaymentType.Receive, Hash));
        Emit(h, OtherStoreId, SparkEventKind.PaymentSucceeded,
            LightningLeg("healthy-1", PaymentType.Receive, OtherHash));

        await WaitFor(() => h.Invoices.Records[OtherHash].Status is InvoiceRecordStatus.Paid,
            "a healthy store's event was starved by a broken store's");

        // And the broken store's own loop is still alive: a later event on it is still processed.
        h.Sdk.Clients[StoreId].FailWith = null;
        h.Invoices.Seed(Unpaid(StoreId, PaymentFixture.KnownPaymentHashVector));
        Emit(h, StoreId, SparkEventKind.PaymentSucceeded,
            LightningLeg("recovered-1", PaymentType.Receive, PaymentFixture.KnownPaymentHashVector));

        await WaitFor(
            () => h.Invoices.Records[PaymentFixture.KnownPaymentHashVector].Status is InvoiceRecordStatus.Paid,
            "a store's event loop died after one of its events failed");
    }

    // ---------------------------------------------------------------------------------------------------
    // Kinds that must do nothing
    // ---------------------------------------------------------------------------------------------------

    [Fact(Timeout = 60_000)]
    public async Task A_failed_payment_event_leaves_the_invoice_payable()
    {
        // A failed inbound HTLC leaves the invoice payable, so there is nothing to record — and nothing to
        // cancel either. Cancelling here would refuse a later, successful attempt at the same invoice.
        using var h = await StartedAsync();
        h.Invoices.Seed(Unpaid(StoreId, Hash));

        Emit(h, StoreId, SparkEventKind.PaymentFailed, LightningLeg("failed-1", PaymentType.Receive, Hash,
            SdkPaymentStatus.Failed));

        h.Invoices.Seed(Unpaid(StoreId, OtherHash));
        Emit(h, StoreId, SparkEventKind.PaymentSucceeded, LightningLeg("after-1", PaymentType.Receive, OtherHash));
        await WaitFor(() => h.Invoices.Records[OtherHash].Status is InvoiceRecordStatus.Paid,
            "the follow-up receive never settled");

        Assert.Equal(InvoiceRecordStatus.Unpaid, h.Invoices.Records[Hash].Status);
    }

    [Fact(Timeout = 60_000)]
    public async Task An_event_with_no_payment_payload_is_ignored_rather_than_throwing()
    {
        // PaymentSucceeded carries a nullable payment on the SDK's own type. The switch guards on it with a
        // `when` clause; without that guard this is a null dereference on the consumer loop, which would kill
        // the store's event processing for the life of the process.
        using var h = await StartedAsync();
        h.Invoices.Seed(Unpaid(StoreId, Hash));

        Emit(h, StoreId, SparkEventKind.PaymentSucceeded, payment: null);
        Emit(h, StoreId, SparkEventKind.PaymentSucceeded, LightningLeg("after-null", PaymentType.Receive, Hash));

        await WaitFor(() => h.Invoices.Records[Hash].Status is InvoiceRecordStatus.Paid,
            "a null-payload event killed the store's event loop");
    }

    // ---------------------------------------------------------------------------------------------------

    private static async Task WaitFor(Func<bool> condition, string because)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Fail(because);
    }

    /// <summary>
    /// Waits until a value has stopped changing, for the negative assertions.
    /// </summary>
    /// <remarks>
    /// A negative assertion needs the system to have finished doing whatever it was going to do. Sampling until
    /// the value is stable is weaker than a positive signal — which is why every negative test above also
    /// asserts a positive one — but it is what keeps "it has not settled yet" from reading as "it will never
    /// settle".
    /// </remarks>
    private static async Task WaitForStable<T>(Func<T> read)
    {
        var last = read();
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            var next = read();
            if (Equals(next, last))
                continue;
            last = next;
            i = 0;
        }
    }
}
