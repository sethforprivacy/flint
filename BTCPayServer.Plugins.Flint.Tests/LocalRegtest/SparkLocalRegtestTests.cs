using System.Security.Cryptography;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using BTCPayServer.Plugins.Flint.Tests.FundedRegtest;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Flint.Tests.LocalRegtest;

/// <summary>
/// The plugin's money paths against a <b>local</b> Spark network, with a real external Lightning
/// counterparty and a chain this suite mines itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>What only this suite can answer.</b> The funded suite settles a Lightning receive by paying the
/// invoice from the same wallet, because Lightspark's regtest offers no second node. That is enough to prove
/// the reconciler picks the Receive leg, but it leaves the ordinary case — a customer's node pays a
/// merchant's invoice — never once executed end to end. And a cooperative exit there confirms on somebody
/// else's schedule, so the sweep engine's Sent → Confirmed transition has only ever been observed as
/// "eventually". Here LND pays, LND is paid, and blocks appear because this suite mines them.
/// </para>
/// <para>
/// <b>Amounts.</b> Every test in this collection spends from one wallet funded once with
/// <see cref="LocalRegtestStack.FundingSats"/>, and the tests do not run in a guaranteed order, so each is
/// sized to leave the others enough: 5,000 in, 3,000 out, 30,000 through a cooperative exit, against
/// 100,000 of funding. The sweep test caps what it moves through the reserve rather than draining, for exactly that
/// reason.
/// </para>
/// <para>
/// Gated on <c>SPARK_LOCAL_REGTEST_NETWORK</c>. See <see cref="LocalRegtestStack"/>.
/// </para>
/// </remarks>
[Trait("Category", "LocalRegtest")]
[Collection(LocalRegtestStack.CollectionName)]
public class SparkLocalRegtestTests
{
    private readonly LocalRegtestStack _stack;

    public SparkLocalRegtestTests(LocalRegtestStack stack) => _stack = stack;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>What the external node pays the plugin's invoice.</summary>
    private const long ReceiveAmountSats = 5_000;

    /// <summary>What the plugin pays the external node's invoice.</summary>
    private const long SendAmountSats = 3_000;

    /// <summary>
    /// The floor for what a cooperative exit moves; the test may sweep more.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A fixed amount cannot work here. The exit fee does not scale with the amount — that much matches the
    /// hosted regtest, and is why <c>SweepSettings.MinimumSweepSats</c> exists — but on this stack it scales
    /// with the <em>chain</em>: the same 30,000-sat exit was quoted at <b>197 sats</b> on a freshly mined
    /// regtest chain and at <b>19,700 sats</b> an hour later, because every transaction the fixture sends
    /// specifies <c>fee_rate=100</c> and bitcoind's estimator eventually believes it. At the second figure a
    /// 30,000-sat sweep delivers 10,300 sats and the fee is 191% of it, which
    /// <c>SweepSettings.HardMaxFeePercent</c> refuses outright — correctly.
    /// </para>
    /// <para>
    /// So the test quotes the exit first and sizes the sweep off the quote, keeping the fee near a fifth of
    /// what the destination receives. The guard is then a real assertion rather than a coincidence of the
    /// fee market the fixture happens to be in.
    /// </para>
    /// </remarks>
    private const long ExitAmountSats = 30_000;

    /// <summary>
    /// What the sweep leaves untouched, so a test running after this one still has money.
    /// </summary>
    private const long ExitMarginSats = 10_000;

    /// <summary>The fee ceiling the sweep is configured with, and sized to stay inside.</summary>
    /// <remarks>
    /// Below <see cref="SweepSettings.HardMaxFeePercent"/> so the engine's own hard ceiling is not the thing
    /// under test; above the ratio the sizing below aims for, so a fee the quote did not predict still fails
    /// the guard rather than sliding through.
    /// </remarks>
    private const double ExitMaxFeePercent = 30.0;

    /// <summary>
    /// An invoice minted by the plugin and paid by an <b>external</b> Lightning node, settled through the
    /// plugin's own reconciler, with the SDK's log captured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the plugin's whole reason to exist — a merchant's invoice paid by a customer — and it has
    /// never run against a real SDK before, because the hosted regtest has nobody to pay it. The payer is the
    /// fixture's LND node, which reaches the wallet over its channel to the SSP's LDK node, so the payment
    /// crosses a real Lightning hop and a real Spark transfer.
    /// </para>
    /// <para>
    /// <b>Three claims, and each is checked against the counterparty rather than against the plugin.</b> The
    /// payment hash the plugin minted is the one LND paid; the preimage on the settled record is the one LND
    /// received; and that preimage hashes to that payment hash. A plugin that invented any of the three would
    /// still pass a single-wallet test.
    /// </para>
    /// <para>
    /// <b>The log assertion.</b> The preimage is read off the settled record — so it is the real one — and
    /// the forwarded lines are searched for it literally, exactly as the funded suite does. Same for the
    /// wallet's mnemonic. This is the assertion that would catch <c>SparkLogScrubber</c> letting secret
    /// material through, and it is worth repeating here because the SDK is talking to a different SSP
    /// implementation, which logs its own messages.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_external_node_pays_a_Flint_invoice_and_it_settles_from_the_receive_leg()
    {
        Assert.SkipUnless(LocalRegtestStack.IsEnabled, LocalRegtestStack.SkipReason);

        var store = new InMemoryInvoiceRecordStore();
        var client = BuildClient(store, out _);

        // Everything the SDK logs from here on belongs to this payment.
        var logFrom = _stack.ForwardedLineCount;

        var invoice = await client.CreateInvoice(
            new CreateInvoiceParams(
                LightMoney.Satoshis(ReceiveAmountSats),
                "flint local-regtest inbound probe",
                TimeSpan.FromMinutes(15)),
            Ct);

        Assert.StartsWith("lnbcrt", invoice.BOLT11);
        Assert.Equal(LightningInvoiceStatus.Unpaid, invoice.Status);

        var paid = await _stack.Control.LndPayInvoiceAsync(invoice.BOLT11, invoice.PaymentHash, 1_000, Ct);
        Assert.True(
            paid.Succeeded,
            $"the external LND node did not pay the invoice: status {paid.Status}. A failure here is the "
            + "fixture's Lightning liquidity or the SSP's LDK node, not the plugin — check that open-ssp "
            + "reports `ldk_mode: live` and that its channel to lnd-1 is active.");
        Assert.Equal(ReceiveAmountSats, paid.ValueSats);

        // Settlement is driven by polling the reconciler rather than by the event stream, for the reason the
        // funded suite gives: a completed receive was observed emitting only PaymentPending and never
        // PaymentSucceeded, with the completion visible from storage alone. SparkReconciliationTask exists
        // because of that, so this drives the mechanism that has to be right.
        var settled = await PollAsync(
            async () =>
            {
                var current = await client.GetInvoice(invoice.PaymentHash, Ct);
                return current.Status is LightningInvoiceStatus.Paid ? current : null;
            },
            TimeSpan.FromMinutes(3),
            "the invoice the external node paid never reached Paid");

        Assert.Equal(LightMoney.Satoshis(ReceiveAmountSats), settled.AmountReceived);

        var record = store.Records[invoice.PaymentHash];
        Assert.Equal(InvoiceRecordStatus.Paid, record.Status);
        Assert.NotNull(record.SettledAt);
        Assert.Equal(ReceiveAmountSats * 1000, record.AmountReceivedMsat);

        // §6.3, and here it is not a stipulation: the only Send leg in existence for this hash is on LND.
        Assert.NotNull(record.SdkPaymentId);
        var receiveLeg = await _stack.Sdk.GetPaymentAsync(record.SdkPaymentId!, Ct);
        Assert.NotNull(receiveLeg);
        Assert.Equal(SparkPaymentDirection.Receive, receiveLeg!.Direction);
        Assert.Equal(SparkPaymentStatus.Completed, receiveLeg.Status);

        Assert.False(
            string.IsNullOrEmpty(record.Preimage),
            "the settled record carries no preimage, so neither the proof-of-payment assertions nor the log "
            + "audit below can run. Either the SSP stopped reporting one or SparkPaymentMapper stopped "
            + "reading it.");

        // The counterparty's view. LND accepted this payment, so the preimage it holds is the one the
        // network settled on; a record carrying anything else would be the plugin reporting a payment that
        // did not happen the way it says.
        Assert.Equal(paid.Preimage, record.Preimage!.ToLowerInvariant());
        Assert.Equal(
            invoice.PaymentHash.ToLowerInvariant(),
            Sha256Hex(record.Preimage!));

        var forwarded = string.Join('\n', _stack.ForwardedSince(logFrom));

        Assert.False(
            FundedRegtestWallet.CountOccurrences(forwarded, record.Preimage!) > 0,
            "the payment preimage reached BTCPay's logger through SparkLogBridge. SparkLogScrubber did not "
            + "redact it. Preimage fingerprint: "
            + $"`{FundedRegtestWallet.Fingerprint(record.Preimage!)}` (SHA-256 prefix; not reversible) — "
            + "the fingerprint rather than the value, because this assertion lands in a public job log.");

        Assert.False(
            FundedRegtestWallet.SeedAppearsIn(forwarded, _stack.Mnemonic),
            "the wallet's BIP39 mnemonic reached BTCPay's logger. The wallet itself is disposable — it is "
            + "generated per run and funded from regtest — but the scrubber hole this proves is not.");
    }

    /// <summary>
    /// An invoice minted by an <b>external</b> Lightning node and paid through
    /// <c>SparkLightningClient.Pay</c>, confirmed settled on the payee.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The plugin's payout path, end to end, against a payee that is not itself. What the funded suite cannot
    /// check is the part that happens after the SDK returns: that the invoice on the other node is actually
    /// settled, and that the preimage BTCPay was handed is the one that settled it. A payout marked Completed
    /// against an unsettled invoice is the failure mode that matters, and it is invisible from inside one
    /// wallet.
    /// </para>
    /// <para>
    /// <c>Pay</c> is called through the plugin's own client rather than the SDK, so the outgoing record, the
    /// duplicate-claim guard and the fee guard all run — this is the path BTCPay's payout processor takes.
    /// </para>
    /// <para>
    /// <b>The send timeout is not decoration.</b> <c>SparkLightningClient</c> passes
    /// <c>PayInvoiceParams.SendTimeout</c> straight through as the SDK's <c>completionTimeoutSecs</c>, and
    /// with none set the SDK does not wait: against this SSP the send returned a <em>Pending</em> payment
    /// which the plugin correctly maps to <c>PayResult.Unknown</c> — while LND, checked afterwards, showed
    /// the invoice SETTLED. That is the plugin behaving as designed (BTCPay marks such a payout InProgress
    /// and its pending listener resolves it later), but it is not what this test is about, so a timeout is
    /// passed and <c>Ok</c> is required.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Flint_pays_an_external_invoice_and_the_payee_sees_it_settled()
    {
        Assert.SkipUnless(LocalRegtestStack.IsEnabled, LocalRegtestStack.SkipReason);
        await _stack.RequireBalanceAsync(SendAmountSats + 2_000, "an outbound Lightning payment", Ct);

        var store = new InMemoryInvoiceRecordStore();
        var client = BuildClient(store, out var log);

        var invoice = await _stack.Control.LndAddInvoiceAsync(
            SendAmountSats, $"flint local-regtest outbound {Guid.NewGuid():N}", Ct);

        var before = await _stack.Control.LndLookupInvoiceAsync(invoice.PaymentHash, Ct);
        Assert.False(before.Settled, "the invoice LND just minted is already settled");

        var response = await client.Pay(
            invoice.Bolt11,
            new PayInvoiceParams { SendTimeout = TimeSpan.FromSeconds(60) },
            Ct);

        Assert.True(
            response.Result is PayResult.Ok,
            $"the payout was reported as {response.Result}: {response.ErrorDetail}\n"
            + $"what the client logged:\n{log.AllText}");
        Assert.NotNull(response.Details);
        Assert.Equal(
            invoice.PaymentHash.ToLowerInvariant(),
            response.Details!.PaymentHash?.ToString()?.ToLowerInvariant());

        // The payee's own view, which is the only authority on whether this invoice was paid.
        var after = await PollAsync(
            async () =>
            {
                var state = await _stack.Control.LndLookupInvoiceAsync(invoice.PaymentHash, Ct);
                return state.Settled ? state : null;
            },
            TimeSpan.FromMinutes(2),
            $"LND never reported invoice {invoice.PaymentHash} as settled, although the plugin returned "
            + $"{response.Result}. A payout reported Ok against an unsettled invoice is the failure this "
            + "test exists for");

        Assert.Equal("SETTLED", after.State);
        Assert.Equal(SendAmountSats, after.AmountPaidSats);
        Assert.NotNull(after.Preimage);
        // The payee's preimage really is this invoice's, so it is a usable yardstick for the plugin's.
        Assert.Equal(invoice.PaymentHash.ToLowerInvariant(), Sha256Hex(after.Preimage!));

        // The path BTCPay takes when a PayResponse arrives without a preimage — LightningAutomatedPayoutProcessor
        // resolves the payout by hash — and the path that carries proof of payment to the merchant. Polled
        // rather than read once: the send returns as soon as the SSP says Completed, and the payment record it
        // wrote may not carry everything yet.
        var settledPayment = await PollAsync(
            async () =>
            {
                var payment = await client.GetPayment(invoice.PaymentHash, Ct);
                return payment is { Status: LightningPaymentStatus.Complete } ? payment : null;
            },
            TimeSpan.FromMinutes(2),
            $"the plugin never resolved its own send of {invoice.PaymentHash} to Complete, so a payout "
            + "waiting on it would be cancelled by BTCPay's pending listener");

        Assert.Equal(invoice.PaymentHash.ToLowerInvariant(), settledPayment.PaymentHash?.ToLowerInvariant());
        Assert.Equal(LightMoney.Satoshis(SendAmountSats), settledPayment.Amount);

        // <b>Every preimage the plugin reports must be the payee's.</b> Asserted wherever one appears — on
        // the PayResponse and on the resolved payment — rather than asserted to appear: against this SSP a
        // completed send was observed carrying no preimage at all on either surface, which costs the merchant
        // proof of payment but settles the payout correctly, and is the SSP's behaviour rather than the
        // plugin's. A *wrong* preimage would be the plugin's, and this is what would catch it.
        var reported = new (string Surface, string? Value)[]
        {
            ("the PayResponse", response.Details.Preimage?.ToString()),
            ("the resolved payment", settledPayment.Preimage)
        };
        foreach (var (surface, value) in reported)
        {
            if (!string.IsNullOrEmpty(value))
                Assert.Equal(after.Preimage, value.ToLowerInvariant());
        }

        Console.WriteLine(
            $"local-regtest: paid {SendAmountSats:N0} sats to LND ({invoice.PaymentHash}); "
            + $"fee {settledPayment.Fee}; preimage reported on "
            + $"[{string.Join(", ", reported.Where(r => !string.IsNullOrEmpty(r.Value)).Select(r => r.Surface))}]"
            + $"{(reported.All(r => string.IsNullOrEmpty(r.Value)) ? "nothing — the SSP reported none" : "")}");
    }

    /// <summary>
    /// A cooperative exit driven by the sweep engine, followed to <see cref="SweepRecordStatus.Confirmed"/>
    /// with this suite mining the blocks that confirm it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same shape as the funded suite's engine test, with the one thing that suite cannot have: control
    /// of the chain. There, Confirmed arrives when the hosted regtest happens to mine; here blocks are mined
    /// between polls, so reaching Confirmed is bounded in blocks rather than in luck, and a sweep that
    /// <em>never</em> confirms is distinguishable from one that is merely waiting.
    /// </para>
    /// <para>
    /// The destination is a fresh bitcoind address rather than the wallet's own deposit address. The funded
    /// suite sends to itself so the principal comes back to a scarce wallet; nothing is scarce here, and a
    /// third-party destination is both the realistic case and one where the exit's output can be inspected
    /// on-chain.
    /// </para>
    /// <para>
    /// Confirmation is driven by re-running the engine, because that is the only thing that advances a
    /// record: <c>RunAsync</c> resolves the store's unresolved rows before it considers a new sweep, and
    /// there is no separate confirm entry point. The reserve is set so the second pass finds nothing
    /// sweepable and merely resolves.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_cooperative_exit_sweep_reaches_Confirmed_once_its_transaction_is_mined()
    {
        Assert.SkipUnless(LocalRegtestStack.IsEnabled, LocalRegtestStack.SkipReason);
        await _stack.RequireBalanceAsync(ExitAmountSats + ExitMarginSats, "a cooperative-exit sweep", Ct);
        var balance = await _stack.SyncBalanceAsync(Ct);

        var destination = await _stack.Control.NewAddressAsync("flint-local-regtest-sweep",
            cancellationToken: Ct);

        // Quoted through the plugin's own read-only quote, at the floor amount, purely to learn what this
        // stack's fee market currently charges. The engine re-quotes for itself when it sends — a prepare is
        // only good for about a minute — so this is an estimate used for sizing and nothing else.
        SparkOnchainFeeQuote quoted;
        try
        {
            quoted = await _stack.Sdk.QuoteOnchainSendAsync(destination, ExitAmountSats, feesIncluded: true, Ct);
        }
        catch (Exception ex) when (SparkErrors.IsInsufficientFunds(ex))
        {
            // The wallet's own balance was checked above, so this is the SSP's: a cooperative exit needs it
            // to split leaves it can back, and it reports that shortfall as the wallet's. Worth naming,
            // because the message the SDK gives — "Tree service error: insufficient funds" — reads as though
            // this wallet were empty when it is not.
            var balanceNow = await _stack.SyncBalanceAsync(Ct);
            Assert.Fail(
                $"quoting a cooperative exit failed with \"{ex.Message}\" although the wallet holds "
                + $"{balanceNow:N0} sats, which means the service provider is out of Spark liquidity rather "
                + "than the wallet. Each run of this suite funds a fresh wallet out of the SSP's leaves, so a "
                + "stack that has run it several times needs topping up: POST "
                + "/admin/spark/deposit-address on the SSP with its admin token, pay the address from "
                + "bitcoind, mine 3, then POST /admin/spark/claim-deposit — which is what the fixture's own "
                + "cashu-spark-fund-ssp does. e2e/local-regtest/README.md has the commands.");
            throw;
        }
        var slowFee = quoted.SlowFeeSats;
        Assert.True(slowFee > 0, $"the SSP quoted a Slow cooperative-exit fee of {slowFee} sats");

        // Five times the fee, so the fee lands near 20% of what the destination receives — inside both
        // ExitMaxFeePercent and the engine's hard 50% ceiling with room for the re-quote to move.
        var amount = Math.Max(ExitAmountSats, slowFee * 5);
        if (amount > balance - ExitMarginSats)
        {
            Assert.Fail(
                $"this stack quotes {slowFee:N0} sats for a cooperative exit, so a sweep would have to move "
                + $"{amount:N0} sats to stay inside the engine's fee guards, and the wallet holds only "
                + $"{balance:N0}. Raise LocalRegtestStack.FundingSats — but check first that the SSP has "
                + "several times that in Spark liquidity, since it has to back the leaf splitting.");
        }

        var records = new InMemorySweepRecordStore();
        var engine = BuildEngine(records, new SweepSettings
        {
            Enabled = true,
            DestinationMode = SweepDestinationMode.StaticAddress,
            StaticAddress = destination,
            // Everything above the reserve is swept, so the reserve is how the amount is chosen — and how
            // the rest of the suite keeps its money.
            ReserveSats = balance - amount,
            BalanceThresholdSats = amount,
            MinimumSweepSats = amount / 2,
            DrainWhenSweeping = true,
            ConfirmationSpeed = SweepConfirmationSpeed.Slow,
            MaxFeePercent = ExitMaxFeePercent
        });

        var run = await engine.RunAsync(_stack.StoreId, SweepTrigger.Manual, Ct);
        Assert.True(
            run.Kind is SweepOutcomeKind.Swept,
            $"the sweep did not go out: {run.Kind} — {run.Reason}");

        var sent = Assert.Single(records.Records).Value;
        Assert.Equal(destination, sent.DestinationAddress);
        Assert.True(
            sent.Status is SweepRecordStatus.Sent or SweepRecordStatus.Confirmed,
            $"a sweep that reported Swept left its record at {sent.Status}");
        // Present from the first Sent write — the only handle an operator has on funds in flight.
        Assert.False(string.IsNullOrEmpty(sent.TxId), "the cooperative exit recorded no txid");

        // The engine's economics assume a flat fee that does not scale with the amount. Pinned here against
        // the local SSP as well as the hosted one, because this is a different SSP implementation quoting its
        // own fees and a change in them is exactly what MinimumSweepSats exists to survive.
        Assert.True(
            sent.QuotedFeeSats > 0 && sent.QuotedFeeSats < amount / 2,
            $"an exit fee of {sent.QuotedFeeSats} sats on a {amount} sat sweep is not a fee this engine's "
            + "economics were designed around (the guard that would refuse it is "
            + $"SweepSettings.HardMaxFeePercent at {SweepSettings.HardMaxFeePercent}%). The quote taken "
            + $"moments earlier said {slowFee} sats for the Slow tier.");

        // The exit's transaction must be in bitcoind's view before mining can confirm it. Asserted rather
        // than assumed: a txid the SSP reported but never broadcast would otherwise look identical to a
        // slow confirmation for the whole timeout.
        await PollAsync(
            async () =>
            {
                using var tx = await _stack.Control.BitcoinCliJsonAsync(
                    ["getrawtransaction", sent.TxId!, "true"], Ct);
                return tx.RootElement.TryGetProperty("txid", out _) ? sent.TxId : null;
            },
            TimeSpan.FromMinutes(2),
            $"the cooperative exit's transaction {sent.TxId} never appeared in bitcoind, so the SSP "
            + "reported a txid it did not broadcast");

        var minedBlocks = 0;
        var confirmed = await PollAsync(
            async () =>
            {
                // Three at a time, matching the fixture's own funding helpers: enough to move a transaction
                // out of the mempool and past the confirmation counts the SDK cares about without minting a
                // hundred blocks per poll.
                minedBlocks += 3;
                await _stack.Control.MineAsync(3, Ct);
                await engine.RunAsync(_stack.StoreId, SweepTrigger.Automatic, Ct);
                var current = await records.GetAsync(_stack.StoreId, sent.IdempotencyKey, Ct);
                return current?.Status is SweepRecordStatus.Confirmed ? current : null;
            },
            TimeSpan.FromMinutes(5),
            $"the sweep never reached Confirmed (txid {sent.TxId}) even after mining");

        Assert.Equal(SweepRecordStatus.Confirmed, confirmed.Status);
        Assert.NotNull(confirmed.CompletedAt);
        // The txid survives the Sent -> Confirmed resolution. A SweepResolution's nulls mean "nothing new to
        // say", not "clear it", and that is what keeps the handle.
        Assert.Equal(sent.TxId, confirmed.TxId);
        Assert.Null(confirmed.Error);

        // The destination actually received the money, on-chain. This is the assertion no amount of SDK
        // bookkeeping can substitute for: a Confirmed record whose output never landed is the worst possible
        // outcome for a sweep, and it is only visible from bitcoind.
        using var mined = await _stack.Control.BitcoinCliJsonAsync(
            ["getrawtransaction", sent.TxId!, "true"], Ct);
        var paidToDestination = mined.RootElement.GetProperty("vout").EnumerateArray()
            .Where(v => v.TryGetProperty("scriptPubKey", out var script)
                        && script.TryGetProperty("address", out var address)
                        && string.Equals(address.GetString(), destination, StringComparison.Ordinal))
            .Select(v => v.GetProperty("value").GetDecimal())
            .Sum();
        Assert.True(
            paidToDestination > 0m,
            $"the confirmed exit transaction {sent.TxId} pays nothing to {destination}");
        // Fees included, so the destination receives amount - fee. Compared loosely: the record's own
        // arithmetic is unit-tested, and what this asserts is that the two views agree on the same payment.
        var expectedSats = sent.AmountSats - sent.QuotedFeeSats;
        Assert.Equal(expectedSats, (long)decimal.Round(paidToDestination * 100_000_000m));

        Console.WriteLine(
            $"local-regtest: cooperative exit of {sent.AmountSats:N0} sats confirmed after "
            + $"{minedBlocks} mined blocks; quoted fee {sent.QuotedFeeSats:N0} sats, "
            + $"{expectedSats:N0} sats paid to {destination} in {sent.TxId}");
    }

    /// <summary>
    /// The plugin's Lightning client over the real wallet, wired as <c>SparkService</c> wires it.
    /// </summary>
    /// <remarks>
    /// A fresh store per test, because the invoice and outgoing records are per-store state and nothing here
    /// is testing what happens when two tests share them. The credit gateway is empty: there is no BTCPay
    /// here, so there is no invoice to credit, and the settlement path stays whole without it.
    /// </remarks>
    private SparkLightningClient BuildClient(
        InMemoryInvoiceRecordStore store, out CapturingLogger<SparkLightningClient> log)
    {
        log = new CapturingLogger<SparkLightningClient>();
        var broadcaster = new SparkSettlementBroadcaster(NullLogger<SparkSettlementBroadcaster>.Instance);
        var reconciler = new SparkSettlementReconciler(
            store,
            broadcaster,
            new SparkInvoiceCreditor(
                new FakeInvoiceCreditGateway(), store, NullLogger<SparkInvoiceCreditor>.Instance),
            NullLogger<SparkSettlementReconciler>.Instance);

        return new SparkLightningClient(
            _stack.StoreId,
            _stack.PaymentKey,
            _stack.Sdk,
            store,
            new InMemoryOutgoingPaymentStore(),
            reconciler,
            broadcaster,
            new NBitcoinBolt11Parser(Network.RegTest, NullLogger<NBitcoinBolt11Parser>.Instance),
            log);
    }

    /// <summary>
    /// The sweep engine, wired the way <c>SparkService</c> wires it, over the real wallet.
    /// </summary>
    /// <remarks>
    /// <see cref="TimeProvider.System"/> rather than the stub the unit tests use: the engine's grace and
    /// resolution windows are read off it, and against a real service they have to mean real time.
    /// </remarks>
    private SparkSweepEngine BuildEngine(InMemorySweepRecordStore records, SweepSettings sweep)
    {
        var settings = new FakeSparkStoreSettingsStore();
        settings.Settings[_stack.StoreId] = new SparkSettings
        {
            ProtectedMnemonic = "not-read-by-the-engine",
            PaymentKey = _stack.PaymentKey,
            Sweep = sweep
        };

        var runtime = new FakeSparkStoreRuntime();
        runtime.Clients[_stack.StoreId] = _stack.Sdk;

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

    private static string Sha256Hex(string hex) =>
        Convert.ToHexString(SHA256.HashData(Convert.FromHexString(hex))).ToLowerInvariant();

    /// <summary>
    /// Polls until <paramref name="attempt"/> produces a value, then fails with <paramref name="timeoutMessage"/>.
    /// </summary>
    /// <remarks>
    /// Same shape as the funded suite's, and generous for the same reason: nothing here measures latency, it
    /// measures whether a state is ever reached, and a tight deadline against a stack that is still settling
    /// turns a working plugin into a red CI run.
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
                // A transient failure from the stack mid-poll is not the answer; the deadline is.
                last = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        }

        Assert.Fail(last is null
            ? $"{timeoutMessage} within {timeout.TotalMinutes:0} minutes."
            : $"{timeoutMessage} within {timeout.TotalMinutes:0} minutes; the last attempt threw {last}");
        throw new InvalidOperationException("unreachable");
    }
}
