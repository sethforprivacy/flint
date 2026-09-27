using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Flint.Tests.FundedRegtest;

/// <summary>
/// The paths that only exist once money has actually moved, run against the real SDK and the real SSP.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this suite is for.</b> Every money path in this plugin is covered against
/// <see cref="FakeSparkSdkClient"/>, and the fake is a good one — it models the real SDK's hazards rather than
/// an idealised SDK. But a fake cannot answer three questions, and all three are the ones that cost money when
/// the answer changes:
/// </para>
/// <list type="number">
/// <item><description><b>What does the SDK log when a payment completes?</b> The plugin's audit of the SDK's
/// log output was done on an unfunded wallet, so the lines a preimage would ride on were never emitted. That is
/// the one stated gap in that analysis and the one thing <c>SparkLogScrubber</c>'s remarks say would overturn
/// its design.</description></item>
/// <item><description><b>Does a cooperative exit really behave the way the sweep engine assumes?</b> A flat fee
/// that ignores the amount, a txid present from the first pending event, a status that reaches Completed —
/// the engine's economics and its record lifecycle are built on all three.</description></item>
/// <item><description><b>Is a crash mid-sweep really recoverable?</b> The whole crash-safety story rests on the
/// SDK adopting the caller's idempotency key as <c>Payment.id</c>, which the SDK does not document. If that
/// ever stops being
/// true, a recovered record resolves to nothing and the engine closes a successful sweep as failed.</description></item>
/// </list>
/// <para>
/// Gated on <c>SPARK_REGTEST_SEED</c>. See <see cref="FundedRegtestWallet"/>.
/// </para>
/// </remarks>
[Trait("Category", "FundedRegtest")]
[Collection(FundedRegtestWallet.CollectionName)]
public class SparkFundedRegtestTests
{
    private readonly FundedRegtestWallet _wallet;

    public SparkFundedRegtestTests(FundedRegtestWallet wallet) => _wallet = wallet;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>What the self-payment moves. Small: the point is that it settles, not how much.</summary>
    private const long ReceiveAmountSats = 2_000;

    /// <summary>
    /// What a cooperative exit moves.
    /// </summary>
    /// <remarks>
    /// Not arbitrary. The exit fee is flat — 294 sats and 99,901 sats quoted identically — so on a small amount
    /// the fee is most of the payment, and <c>SweepSettings.HardMaxFeePercent</c> (50%) refuses it regardless of
    /// what the store configured. At ~1,950 sats of fee on the Slow tier, 20,000 puts the fee near 11% of what
    /// the destination receives, comfortably inside both guards without wasting money.
    /// </remarks>
    private const long ExitAmountSats = 20_000;

    /// <summary>
    /// A completed Lightning receive, settled through the plugin's own stack, with the SDK's log captured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A self-payment, deliberately.</b> One wallet paying its own invoice is the cheapest way to produce a
    /// real completed receive — no second funded wallet, no faucet round trip, a fee of about three sats — and
    /// it exercises an invariant a two-wallet test could not: §6.3, <em>never settle from the send leg</em>. The
    /// SDK produces <b>two</b> <c>Payment</c> rows for this, a Receive and a Send sharing one payment hash and
    /// one invoice, with different ids and different fees. The reconciler has to pick the Receive. Against the
    /// fake that is stipulated; here it is observed.
    /// </para>
    /// <para>
    /// <b>The log assertion is the point of the whole suite.</b> The preimage is read off the settled record —
    /// so it is the real one, not a shape — and the forwarded lines are searched for it literally. That is a
    /// decisive test rather than a heuristic: if the scrubber lets a preimage through, this goes red with the
    /// exact value in the message.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_completed_lightning_receive_settles_from_the_receive_leg_and_leaks_nothing_to_the_log()
    {
        Assert.SkipUnless(FundedRegtestWallet.IsEnabled, FundedRegtestWallet.SkipReason);
        await _wallet.RequireBalanceAsync(ReceiveAmountSats + 1_000, "a self-paid Lightning receive", Ct);

        var store = new InMemoryInvoiceRecordStore();
        var broadcaster = new SparkSettlementBroadcaster(NullLogger<SparkSettlementBroadcaster>.Instance);
        var reconciler = new SparkSettlementReconciler(
            store,
            broadcaster,
            // There is no BTCPay here, so no invoice to credit; an empty gateway keeps the settlement path
            // whole while leaving crediting to the tests that own it.
            new SparkInvoiceCreditor(
                new FakeInvoiceCreditGateway(), store, NullLogger<SparkInvoiceCreditor>.Instance),
            NullLogger<SparkSettlementReconciler>.Instance);
        var client = new SparkLightningClient(
            _wallet.StoreId,
            "funded-regtest-key",
            _wallet.Sdk,
            store,
            new InMemoryOutgoingPaymentStore(),
            reconciler,
            broadcaster,
            new NBitcoinBolt11Parser(Network.RegTest, NullLogger<NBitcoinBolt11Parser>.Instance),
            NullLogger.Instance);

        // Everything the SDK logs from here on belongs to this payment.
        var logFrom = _wallet.ForwardedLineCount;

        var invoice = await client.CreateInvoice(
            new CreateInvoiceParams(
                LightMoney.Satoshis(ReceiveAmountSats),
                "spark funded-regtest settlement probe",
                TimeSpan.FromMinutes(15)),
            Ct);

        Assert.StartsWith("lnbcrt", invoice.BOLT11);
        Assert.Equal(LightningInvoiceStatus.Unpaid, invoice.Status);
        _wallet.RegisterIdentifier("payment hash", invoice.PaymentHash);

        // The send leg, issued straight at the SDK rather than through the client: this is standing in for a
        // counterparty, and routing it through the plugin's own Pay path would also write an outgoing record
        // and muddy what the receive assertions are about.
        var sendResult = await _wallet.Sdk.SendBolt11Async(
            invoice.BOLT11,
            amountSats: null,
            idempotencyKey: Guid.NewGuid().ToString(),
            approveQuote: _ => null,
            completionTimeout: TimeSpan.FromSeconds(60),
            Ct);

        Assert.Null(sendResult.RejectedReason);
        Assert.NotNull(sendResult.Payment);

        // Settlement is driven by polling the reconciler rather than by the event stream. Not because events do
        // not work, but because they cannot be relied on to: a completed receive was observed emitting only
        // PaymentPending and never PaymentSucceeded, with the completion visible from storage alone. That is
        // exactly why SparkReconciliationTask exists, so this drives the mechanism that has to be right.
        var settled = await PollAsync(
            async () =>
            {
                var current = await client.GetInvoice(invoice.PaymentHash, Ct);
                return current.Status is LightningInvoiceStatus.Paid ? current : null;
            },
            TimeSpan.FromMinutes(3),
            "the self-paid invoice never reached Paid");

        Assert.Equal(LightMoney.Satoshis(ReceiveAmountSats), settled.AmountReceived);

        var record = store.Records[invoice.PaymentHash];
        Assert.Equal(InvoiceRecordStatus.Paid, record.Status);
        Assert.NotNull(record.SettledAt);
        Assert.Equal(ReceiveAmountSats * 1000, record.AmountReceivedMsat);

        // §6.3. The send leg has its own SDK payment id; crediting from it would settle the merchant's invoice
        // against the merchant's own outgoing payment.
        Assert.NotNull(record.SdkPaymentId);
        Assert.NotEqual(sendResult.Payment!.SdkPaymentId, record.SdkPaymentId);

        var receiveLeg = await _wallet.Sdk.GetPaymentAsync(record.SdkPaymentId!, Ct);
        Assert.NotNull(receiveLeg);
        Assert.Equal(SparkPaymentDirection.Receive, receiveLeg!.Direction);
        Assert.Equal(SparkPaymentStatus.Completed, receiveLeg.Status);

        // The preimage is the whole reason this suite exists. If the SSP did not report one there is nothing to
        // audit and saying so is better than passing quietly.
        Assert.False(
            string.IsNullOrEmpty(record.Preimage),
            "the settled record carries no preimage, so the log audit this suite exists for cannot run. Either "
            + "the SSP stopped reporting one or SparkPaymentMapper stopped reading it.");
        _wallet.RegisterIdentifier("preimage", record.Preimage);

        var forwarded = string.Join('\n', _wallet.ForwardedSince(logFrom));

        // The decisive assertion, and the one the log audit's stated gap asks for. Not a shape: the literal
        // preimage this payment produced, searched for in what the bridge actually forwarded.
        Assert.False(
            FundedRegtestWallet.CountOccurrences(forwarded, record.Preimage!) > 0,
            "the payment preimage reached BTCPay's logger through SparkLogBridge. SparkLogScrubber did not "
            + "redact it. Preimage fingerprint: "
            + $"`{FundedRegtestWallet.Fingerprint(record.Preimage!)}` (SHA-256 prefix; not reversible) — "
            + "this assertion lands in the public job log, so it names the fingerprint matching the "
            + "preimage row of the preimage-audit.md artefact rather than the preimage itself.");

        Assert.False(
            FundedRegtestWallet.SeedAppearsIn(forwarded, _wallet.Mnemonic),
            "the wallet's BIP39 mnemonic reached BTCPay's logger. This is a seed leak; treat the CI wallet as "
            + "compromised, rotate it, and do not download the run's artefacts.");

        // A redaction that never fires proves nothing about the scrubber, but the absence of a preimage in
        // ordinary debug output is itself the measurement §8.6 wants, so the count is reported rather than
        // asserted on. It lands in the artefact.
        Assert.NotEmpty(_wallet.ForwardedSince(logFrom));
    }

    /// <summary>
    /// A cooperative exit driven by the sweep engine, followed to <see cref="SweepRecordStatus.Confirmed"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The engine is wired exactly as production wires it, with one real SDK client in place of the fake. The
    /// destination is the wallet's own static deposit address, so the principal comes back on-chain instead of
    /// being burned — see <see cref="FundedRegtestWallet"/>.
    /// </para>
    /// <para>
    /// Confirmation is driven by re-running the engine, because that is the only thing that advances a record:
    /// <c>RunAsync</c> resolves the store's unresolved rows before it considers a new sweep, and there is no
    /// separate confirm entry point. Production drives the same loop from <c>SweepTask</c>. The reserve is set
    /// so the second pass finds nothing sweepable and merely resolves.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_cooperative_exit_sweep_runs_through_the_engine_and_reaches_Confirmed()
    {
        Assert.SkipUnless(FundedRegtestWallet.IsEnabled, FundedRegtestWallet.SkipReason);
        var balance = await RequireForExitAsync("a cooperative-exit sweep");

        var records = new InMemorySweepRecordStore();
        var engine = BuildEngine(records, new SweepSettings
        {
            Enabled = true,
            DestinationMode = SweepDestinationMode.StaticAddress,
            StaticAddress = _wallet.SweepDestination,
            // Everything above the reserve is swept, so the reserve is how the amount is chosen.
            ReserveSats = balance - ExitAmountSats,
            BalanceThresholdSats = ExitAmountSats,
            MinimumSweepSats = ExitAmountSats / 2,
            DrainWhenSweeping = true,
            ConfirmationSpeed = SweepConfirmationSpeed.Slow,
            MaxFeePercent = 25.0
        });

        var run = await engine.RunAsync(_wallet.StoreId, SweepTrigger.Manual, cancellationToken: Ct);
        Assert.True(
            run.Kind is SweepOutcomeKind.Swept,
            $"the sweep did not go out: {run.Kind} — {run.Reason}");

        var sent = Assert.Single(records.Records).Value;
        Assert.Equal(_wallet.SweepDestination, sent.DestinationAddress);
        Assert.True(
            sent.Status is SweepRecordStatus.Sent or SweepRecordStatus.Confirmed,
            $"a sweep that reported Swept left its record at {sent.Status}");
        // Present from the first Sent write — the only handle an operator has on funds in flight.
        Assert.False(string.IsNullOrEmpty(sent.TxId), "the cooperative exit recorded no txid");
        _wallet.RegisterIdentifier("sweep txid", sent.TxId);
        _wallet.RegisterIdentifier("sweep idempotency key", sent.IdempotencyKey);

        // The fee is flat and does not scale with the amount, which is the entire reason MinimumSweepSats
        // exists. Pinning it here is what would notice the SSP changing that.
        Assert.True(
            sent.QuotedFeeSats is > 0 and < ExitAmountSats,
            $"an exit fee of {sent.QuotedFeeSats} sats on a {ExitAmountSats} sat sweep is not a fee this "
            + "engine's economics were designed around");

        var confirmed = await PollAsync(
            async () =>
            {
                await engine.RunAsync(_wallet.StoreId, SweepTrigger.Automatic, cancellationToken: Ct);
                var current = await records.GetAsync(_wallet.StoreId, sent.IdempotencyKey, Ct);
                return current?.Status is SweepRecordStatus.Confirmed ? current : null;
            },
            TimeSpan.FromMinutes(5),
            $"the sweep never reached Confirmed (txid {sent.TxId})");

        Assert.Equal(SweepRecordStatus.Confirmed, confirmed.Status);
        Assert.NotNull(confirmed.CompletedAt);
        // The txid survives the Sent -> Confirmed resolution. A SweepResolution's nulls mean "nothing new to
        // say", not "clear it", and that is what keeps the handle.
        Assert.Equal(sent.TxId, confirmed.TxId);
        Assert.Null(confirmed.Error);
    }

    /// <summary>
    /// A sweep interrupted between the record insert and the send, resolved from its idempotency key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the crash the engine is built around, and the primitive it rests on is the SDK adopting the
    /// caller's UUID as <c>Payment.id</c>. The test stages it honestly: insert the row the engine would have
    /// inserted, perform the send out of band under that same key — as the crashed process had already done —
    /// then hand the store to a fresh engine and let its recovery walk find the payment.
    /// </para>
    /// <para>
    /// <b>The assertion that matters is the negative one.</b> Recovery must resolve, never re-send. The wallet's
    /// withdrawal ids are snapshotted before and after, and the only id allowed to appear between the two is the
    /// staged send's own — the resolved row must point at the payment that already existed, not at a second one.
    /// </para>
    /// <para>
    /// The engine's threshold is set above the balance so its only possible action is the recovery walk. A
    /// second sweep here would not merely be untidy — it would be the bug this test is looking for, arriving by
    /// a different route and passing unnoticed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_interrupted_sweep_is_resolved_from_its_idempotency_key_without_re_sending()
    {
        Assert.SkipUnless(FundedRegtestWallet.IsEnabled, FundedRegtestWallet.SkipReason);
        var balance = await RequireForExitAsync("an interrupted-sweep recovery");

        var idempotencyKey = Guid.NewGuid().ToString();
        var records = new InMemorySweepRecordStore();

        // Step one of the engine's own order of operations: the row exists before anything is sent, so the key
        // is durable even if the process dies on the next line.
        await records.AddAsync(
            new SweepRecord
            {
                StoreId = _wallet.StoreId,
                IdempotencyKey = idempotencyKey,
                DestinationAddress = _wallet.SweepDestination,
                DestinationMode = SweepDestinationMode.StaticAddress,
                AmountSats = ExitAmountSats,
                FeesIncluded = true,
                BalanceAtDecisionSats = balance,
                Status = SweepRecordStatus.Pending,
                Trigger = SweepTrigger.Manual,
                CreatedAt = DateTimeOffset.UtcNow
            },
            Ct);

        var before = await ListWithdrawalIdsAsync();

        // ...and then the crash, immediately after the SSP accepted. Out of band on purpose: the engine must
        // never learn about this send from anything except the key on the row.
        var send = await _wallet.Sdk.SendToBitcoinAddressAsync(
            _wallet.SweepDestination,
            ExitAmountSats,
            SparkOnchainSpeed.Slow,
            feesIncluded: true,
            idempotencyKey,
            approveQuote: _ => null,
            Ct);

        Assert.Null(send.RejectedReason);
        Assert.NotNull(send.Payment);
        // The idempotency key becomes the payment id — the primitive everything else here depends on.
        Assert.Equal(idempotencyKey, send.Payment!.SdkPaymentId);
        _wallet.RegisterIdentifier("recovered sweep txid", send.Payment.TxId);

        var engine = BuildEngine(records, new SweepSettings
        {
            Enabled = true,
            DestinationMode = SweepDestinationMode.StaticAddress,
            StaticAddress = _wallet.SweepDestination,
            // Unreachable, so the pass can only resolve. A manual trigger would relax this, which is why the
            // recovery run below is Automatic.
            BalanceThresholdSats = long.MaxValue / 4,
            MinimumSweepSats = long.MaxValue / 4,
            ConfirmationSpeed = SweepConfirmationSpeed.Slow
        });

        var resolved = await PollAsync(
            async () =>
            {
                await engine.RunAsync(_wallet.StoreId, SweepTrigger.Automatic, cancellationToken: Ct);
                var current = await records.GetAsync(_wallet.StoreId, idempotencyKey, Ct);
                return current?.Status is SweepRecordStatus.Sent or SweepRecordStatus.Confirmed ? current : null;
            },
            TimeSpan.FromMinutes(5),
            "the interrupted sweep was never resolved from its idempotency key");

        Assert.True(
            resolved.Status is SweepRecordStatus.Sent or SweepRecordStatus.Confirmed,
            $"a sweep the SSP accepted was resolved to {resolved.Status}");
        Assert.Null(resolved.Error);
        Assert.Equal(send.Payment.TxId, resolved.TxId);

        // The negative. One record in the store, and exactly one new withdrawal on the wallet — the staged
        // send itself, identified by the payment id the SDK minted from the idempotency key.
        Assert.Single(records.Records);
        var after = await ListWithdrawalIdsAsync();
        var appeared = after.Except(before).OrderBy(id => id).ToList();
        Assert.True(
            appeared.Contains(send.Payment.SdkPaymentId),
            $"the staged send ({send.Payment.SdkPaymentId}) never appeared in the wallet's withdrawal listing, "
            + "so the recovery pass resolved a payment the wallet does not show.");
        Assert.True(
            appeared.Count == 1,
            $"recovery re-sent: withdrawals [{string.Join(", ", appeared)}] appeared where only the staged send "
            + $"({send.Payment.SdkPaymentId}) should have. A second cooperative exit is real money.");
    }

    /// <summary>
    /// The engine, wired the way <c>SparkService</c> wires it, over the real wallet.
    /// </summary>
    /// <remarks>
    /// <see cref="TimeProvider.System"/> rather than the stub the unit tests use: the engine's grace and
    /// resolution windows are read off it, and against a real service they have to mean real time.
    /// </remarks>
    private SparkSweepEngine BuildEngine(InMemorySweepRecordStore records, SweepSettings sweep)
    {
        var settings = new FakeSparkStoreSettingsStore();
        settings.Settings[_wallet.StoreId] = new SparkSettings
        {
            ProtectedMnemonic = "not-read-by-the-engine",
            PaymentKey = "funded-regtest-key",
            Sweep = sweep
        };

        var runtime = new FakeSparkStoreRuntime();
        runtime.Clients[_wallet.StoreId] = _wallet.Sdk;

        return new SparkSweepEngine(
            settings,
            runtime,
            records,
            new SweepDestinationResolver(
                new FakeSweepAddressSource(),
                Network.RegTest,
                NullLogger<SweepDestinationResolver>.Instance),
            new CrossChainRouteResolver(NullLogger<CrossChainRouteResolver>.Instance),
            new FakeCrossChainValueOracle(),
            new FakeSweepTransactionLabeler(),
            TimeProvider.System,
            NullLogger<SparkSweepEngine>.Instance);
    }

    private async Task<long> RequireForExitAsync(string what)
    {
        // Headroom over the amount for the flat fee, which is quoted per tier and not known until the prepare.
        await _wallet.RequireBalanceAsync(ExitAmountSats + 10_000, what, Ct);
        return await _wallet.SyncBalanceAsync(Ct);
    }

    /// <summary>
    /// The payment ids of the newest withdrawals on the wallet, for a before/after diff.
    /// </summary>
    /// <remarks>
    /// Ids, not a count: the funded wallet's send history outgrew the listing window long ago, so every new
    /// withdrawal pushes an old one off the end and a count over the window never moves. The window is
    /// newest-first, which is the one property the diff needs — a withdrawal made between two calls is always
    /// inside it.
    /// </remarks>
    private async Task<IReadOnlySet<string>> ListWithdrawalIdsAsync()
    {
        var payments = await _wallet.Sdk.ListPaymentsAsync(
            new SparkListPaymentsQuery(SparkPaymentDirection.Send, Limit: 200), Ct);
        return payments
            .Where(p => p.Method is SparkPaymentMethod.Withdraw)
            .Select(p => p.SdkPaymentId)
            .ToHashSet();
    }

    /// <summary>
    /// Polls until <paramref name="attempt"/> produces a value, then fails with <paramref name="timeoutMessage"/>.
    /// </summary>
    /// <remarks>
    /// Generous intervals and long deadlines throughout this suite, on purpose. Nothing here is measuring
    /// latency; it is measuring whether a state is ever reached, and a tight timeout against a third-party
    /// regtest turns a working plugin into a red CI run.
    /// </remarks>
    private static async Task<T> PollAsync<T>(
        Func<Task<T?>> attempt,
        TimeSpan timeout,
        string timeoutMessage) where T : class
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (await attempt() is { } value)
                    return value;
                last = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A transient failure from the SSP mid-poll is not the answer; the deadline is.
                last = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), Ct);
        }

        Assert.Fail(last is null
            ? $"{timeoutMessage} within {timeout.TotalMinutes:0} minutes."
            : $"{timeoutMessage} within {timeout.TotalMinutes:0} minutes; the last attempt threw {last}");
        throw new InvalidOperationException("unreachable");
    }
}
