using System.Globalization;
using System.Text.Json;
using BTCPayServer.Plugins.Flint.Tests.LocalRegtest;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests.BtcpayE2E;

/// <summary>
/// Flint doing its job inside a real BTCPay Server: an invoice a customer's node pays, a payout the store's
/// Lightning API sends, and a cooperative exit that confirms on a chain this suite mines.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these three and not more.</b> Each one closes a gap that no other suite in this repository can
/// reach, and every one of them is a gap between the plugin and its <em>host</em> rather than inside the
/// plugin:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>An invoice reaching <c>Settled</c>.</b> The LocalRegtest suite proves the plugin's reconciler settles
/// its own invoice record from the Receive leg. It cannot prove that BTCPay hears about it — that is
/// <c>SparkSettlementBroadcaster</c> waking a listening session, <c>SparkInvoiceCreditor</c> registering the
/// payment through BTCPay's own gateway, and BTCPay then moving the invoice to <c>Settled</c>. Three
/// components, all of them host-facing, none of them exercised by a test that owns the invoice store.
/// </description></item>
/// <item><description>
/// <b>A payment through the store's Lightning API.</b> This is the payout path a merchant, a pull payment or
/// a Lightning payout processor uses, and it goes through the connection string the provisioner wrote into
/// the store's <c>BTC-LN</c> payment method — so it proves the wiring, the connection-string handler and
/// BTCPay's resolution of a plugin-supplied <c>ILightningClient</c>, not just <c>SparkLightningClient</c>.
/// </description></item>
/// <item><description>
/// <b>A sweep reaching <c>Confirmed</c>.</b> The engine's Sent → Confirmed transition is written by the
/// reconciliation pass running as a hosted service on BTCPay's schedule and reading the store's real
/// settings out of Postgres. Every unit test of it drives the pass by hand.
/// </description></item>
/// </list>
/// <para>
/// <b>Everything is asserted against the counterparty, not against the plugin.</b> Invoice settlement is
/// confirmed by BTCPay's own invoice status and cross-checked against the hash LND paid; the payout is
/// confirmed by LND's copy of its own invoice; the sweep is confirmed by a transaction the fixture's bitcoind
/// mined. A plugin that reported success to itself would pass none of them.
/// </para>
/// <para>
/// <b>Amounts.</b> One store, funded once, and no guaranteed test order — so each test is sized to leave the
/// others enough, and the sweep sizes itself off a live fee quote to leave
/// <see cref="BtcpayE2EStack.ReserveForOtherTestsSats"/> behind rather than draining.
/// </para>
/// <para>Gated on <c>FLINT_BTCPAY_E2E</c>. See <see cref="BtcpayE2EStack"/>.</para>
/// </remarks>
[Trait("Category", "BtcpayE2E")]
[Collection(BtcpayE2EStack.CollectionName)]
public class BtcpayE2ETests
{
    private readonly BtcpayE2EStack _stack;

    public BtcpayE2ETests(BtcpayE2EStack stack) => _stack = stack;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>What the external node pays the store's invoice, in satoshis.</summary>
    private const long ReceiveAmountSats = 2_000;

    /// <summary>What the store pays the external node, in satoshis.</summary>
    private const long SendAmountSats = 3_000;

    /// <summary>
    /// How long an invoice may take to reach <c>Settled</c> after LND reports the payment succeeded.
    /// </summary>
    /// <remarks>
    /// Bounded generously rather than tuned. The path is long — the SDK's event stream, the plugin's
    /// reconciler, the credit gateway, BTCPay's invoice state machine — and the plugin also runs a periodic
    /// reconciliation pass as a backstop for a missed event, which is measured in tens of seconds. Anything
    /// inside this window is a pass; the ceiling exists so a genuinely stuck settlement fails with a message
    /// instead of hanging until the CI job's own timeout kills the run.
    /// </remarks>
    private static readonly TimeSpan SettlementTimeout = TimeSpan.FromMinutes(4);

    /// <summary>How long a sent cooperative exit may take to be recorded as confirmed.</summary>
    private static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromMinutes(6);

    /// <summary>
    /// An invoice created through Greenfield, paid by the fixture's LND, reaching <c>Settled</c> in BTCPay.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The BOLT11 is read off <c>GET /api/v1/stores/{id}/invoices/{id}/payment-methods</c> rather than
    /// constructed: that endpoint's <c>destination</c> is what a checkout page shows a customer, so paying it
    /// is paying what a customer would pay. The invoice is priced in <c>BTC</c> so no rate provider is
    /// involved — a regtest server has no business asking an exchange what a bitcoin is worth, and a rate
    /// fetch that failed would make this a test of network access.
    /// </para>
    /// <para>
    /// <b>The cross-check matters more than the status.</b> An invoice can reach <c>Settled</c> because
    /// something credited it with the wrong payment; so the payment hash BTCPay recorded is compared with the
    /// hash LND says it paid, both normalised to lowercase hex.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_external_node_pays_a_store_invoice_and_BTCPay_settles_it()
    {
        Assert.SkipUnless(BtcpayE2EStack.IsEnabled, BtcpayE2EStack.SkipReason);

        var amountBtc = (ReceiveAmountSats / 100_000_000m).ToString("0.00000000", CultureInfo.InvariantCulture);
        string invoiceId;
        using (var created = await _stack.Api.PostStoreAsync(
                   "/invoices",
                   $$"""
                   {
                     "amount": "{{amountBtc}}",
                     "currency": "BTC",
                     "checkout": { "paymentMethods": ["BTC-LN"], "expirationMinutes": 30 },
                     "metadata": { "itemDesc": "flint btcpay e2e receive" }
                   }
                   """,
                   Ct))
        {
            invoiceId = created.RootElement.GetProperty("id").GetString()!;
            Assert.Equal("New", created.RootElement.GetProperty("status").GetString());
        }

        string bolt11;
        using (var methods = await _stack.Api.GetStoreAsync($"/invoices/{invoiceId}/payment-methods", Ct))
        {
            // Named rather than taken as [0]: the store also has LNURL enabled, and the LNURL method's
            // destination is an LNURL string, which lncli cannot pay.
            var lightning = methods.RootElement.EnumerateArray().FirstOrDefault(
                method => string.Equals(
                    method.GetProperty("paymentMethodId").GetString(), "BTC-LN", StringComparison.Ordinal));

            Assert.True(
                lightning.ValueKind is JsonValueKind.Object,
                "the invoice has no BTC-LN payment method, so the store's Lightning wiring is not in force. "
                + $"Payment methods: {methods.RootElement}");

            bolt11 = lightning.GetProperty("destination").GetString()!;
            Assert.StartsWith("lnbcrt", bolt11, StringComparison.OrdinalIgnoreCase);
        }

        // Decoded by LND rather than parsed here, so the hash being compared below is the one the payer
        // committed to and not one this test derived from the same string twice.
        string paymentHash;
        using (var decoded = await _stack.Control.LncliJsonAsync(["decodepayreq", bolt11], Ct))
        {
            paymentHash = decoded.RootElement.GetProperty("payment_hash").GetString()!.ToLowerInvariant();
            Assert.Equal(
                ReceiveAmountSats,
                RegtestControl.LongOf(decoded.RootElement.GetProperty("num_satoshis")));
        }

        var payment = await _stack.Control.LndPayInvoiceAsync(bolt11, paymentHash, cancellationToken: Ct);
        Assert.True(
            payment.Succeeded,
            $"LND did not pay the store's invoice: status {payment.Status}. The route is LND → its channel "
            + "to the SSP's LDK node → a Spark transfer, so a failure here is the fixture's topology rather "
            + "than the plugin.");

        // Settled and nothing weaker. `Processing` means BTCPay has seen a payment and is waiting on the
        // speed policy; `New` means it has not heard about one at all — a plugin that settled its own record
        // and never told BTCPay leaves the invoice on `New` indefinitely, which is precisely the gap this
        // test exists to close. The last status seen is reported so the two failures are distinguishable.
        var lastStatus = "unknown";
        var settled = await PollAsync(
            SettlementTimeout,
            async () =>
            {
                using var invoice = await _stack.Api.GetStoreAsync($"/invoices/{invoiceId}", Ct);
                lastStatus = invoice.RootElement.GetProperty("status").GetString() ?? "unknown";
                return lastStatus == "Settled" ? lastStatus : null;
            });

        Assert.True(
            settled == "Settled",
            $"invoice {invoiceId} was paid by LND (hash {paymentHash}) but BTCPay still reports "
            + $"{lastStatus} after {SettlementTimeout.TotalMinutes:0} minutes. `New` means the settlement "
            + "never reached BTCPay at all — look at SparkSettlementBroadcaster and the credit gateway in "
            + "the plugin's log; `Processing` means it arrived and the invoice is waiting on the store's "
            + "speed policy.");

        using var paid = await _stack.Api.GetStoreAsync($"/invoices/{invoiceId}/payment-methods", Ct);
        var lnMethod = paid.RootElement.EnumerateArray().First(
            method => string.Equals(
                method.GetProperty("paymentMethodId").GetString(), "BTC-LN", StringComparison.Ordinal));

        var payments = lnMethod.GetProperty("payments").EnumerateArray().ToList();
        Assert.NotEmpty(payments);

        // BTCPay records a Lightning payment's id as the payment hash. Compared with LND's, which is the
        // whole cross-check: a plugin that credited the invoice from the wrong payment would settle it too.
        var recorded = payments
            .Select(p => p.GetProperty("id").GetString()?.ToLowerInvariant())
            .ToList();
        Assert.Contains(paymentHash, recorded);

        Console.WriteLine(
            $"btcpay-e2e: invoice {invoiceId} settled from an external {ReceiveAmountSats:N0} sat payment "
            + $"(hash {paymentHash}, LND fee {payment.FeeSats?.ToString(CultureInfo.InvariantCulture) ?? "?"} "
            + "sat)");
    }

    /// <summary>
    /// The store's Lightning API pays an invoice minted by the fixture's LND, and LND agrees it was paid.
    /// </summary>
    /// <remarks>
    /// <c>POST /api/v1/stores/{id}/lightning/BTC/invoices/pay</c> is the payout path, and it reaches the
    /// plugin only through the connection string the provisioner wrote into the store's <c>BTC-LN</c>
    /// configuration — so a green result here is also proof that <c>SparkConnectionStringHandler</c> claimed
    /// that string and that BTCPay resolved a plugin-supplied client from it. The verdict is read from LND's
    /// own copy of its invoice rather than from BTCPay's response.
    /// </remarks>
    [Fact]
    public async Task The_stores_Lightning_API_pays_an_external_invoice()
    {
        Assert.SkipUnless(BtcpayE2EStack.IsEnabled, BtcpayE2EStack.SkipReason);

        await _stack.RequireBalanceAsync(
            SendAmountSats * 3, "paying an external Lightning invoice", Ct);

        var invoice = await _stack.Control.LndAddInvoiceAsync(
            SendAmountSats, "flint btcpay e2e send", Ct);

        // maxFeePercent generous for a regtest topology whose channel policies nobody tuned; the point of the
        // test is that the payment completes, not what it costs.
        using var response = await _stack.Api.PostStoreAsync(
            "/lightning/BTC/invoices/pay",
            $$"""
            { "BOLT11": "{{invoice.Bolt11}}", "maxFeePercent": "5.0", "sendTimeout": 120 }
            """,
            Ct);

        // A 200 here says BTCPay accepted the send. Whether the money arrived is LND's to say — and on a
        // 0-length route it can answer before BTCPay's response is even read, so this is polled either way.
        var state = await PollAsync(
            SettlementTimeout,
            async () =>
            {
                var current = await _stack.Control.LndLookupInvoiceAsync(invoice.PaymentHash, Ct);
                return current.Settled ? current : null;
            });

        Assert.NotNull(state);
        Assert.Equal("SETTLED", state!.State);
        Assert.Equal(SendAmountSats, state.AmountPaidSats);

        Console.WriteLine(
            $"btcpay-e2e: the store paid LND {SendAmountSats:N0} sats through its Lightning API "
            + $"(hash {invoice.PaymentHash}); BTCPay reported "
            + $"{response.RootElement.ToString()[..Math.Min(160, response.RootElement.ToString().Length)]}");
    }

    /// <summary>
    /// A manual sweep through Greenfield reaches <c>Swept</c>, and the record reaches <c>Confirmed</c> once
    /// the exit is mined.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The sweep is sized off a live quote, not fixed.</b> A cooperative exit's fee does not scale with
    /// what it carries, but on this fixture it scales with the <em>chain</em>: every transaction the fixture
    /// sends specifies <c>fee_rate=100</c>, and bitcoind's estimator eventually believes it, so the same exit
    /// was quoted at a few hundred sats on a fresh chain and at ~20,000 an hour later. A fixed amount would
    /// either be refused by the fee guard on an old chain or make the guard vacuous on a fresh one. So the
    /// test previews first — which reserves no address and writes no record — and sets the store's reserve so
    /// that the fee lands at a comfortable fraction of what the destination receives.
    /// </para>
    /// <para>
    /// <b>The reserve is also what keeps the suite order-independent.</b> Draining would leave the other two
    /// tests with nothing if this one ran first, and xunit guarantees no order within a collection.
    /// </para>
    /// <para>
    /// <b>Confirmation is a claim about the chain.</b> The record's txid is looked up in the fixture's
    /// bitcoind and its confirmations counted, so <c>Confirmed</c> is checked against a mined transaction and
    /// not merely against the plugin's own status field.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_manual_sweep_exits_on_chain_and_is_recorded_as_confirmed()
    {
        Assert.SkipUnless(BtcpayE2EStack.IsEnabled, BtcpayE2EStack.SkipReason);

        // Enough to cover the reserve the sweep leaves behind plus a sweep large enough to clear the fee.
        await _stack.RequireBalanceAsync(
            BtcpayE2EStack.ReserveForOtherTestsSats + 20_000, "a cooperative exit", Ct);

        var settings = await ReadSweepSettingsAsync();
        var destination = Text(settings, "staticAddress")
            ?? throw new InvalidOperationException(
                "the store's sweep configuration has no staticAddress, so this test has no destination to "
                + "check on chain. e2e/btcpay/up.sh sets one; re-run it.");

        // Quote with the reserve at zero, so the quote is for the largest exit the wallet could make and the
        // fee it reports is the flat exit fee this chain is charging right now.
        await WriteSweepSettingsAsync(destination, reserveSats: 0);
        long balance;
        long feeSats;
        using (var preview = await _stack.Api.PostStoreAsync("/spark/sweep", "{\"preview\":true}", Ct))
        {
            Assert.True(
                preview.RootElement.GetProperty("canSweep").GetBoolean(),
                "the sweep preview refused before the reserve was even set: "
                + $"{Text(preview.RootElement, "refusalReason")}");

            balance = preview.RootElement.GetProperty("balanceSats").GetInt64();
            feeSats = preview.RootElement.GetProperty("quote").GetProperty("feeSats").GetInt64();
        }

        Assert.True(feeSats > 0, "the exit quote reported no fee, which no cooperative exit costs.");

        // Sweep everything above the reserve, and then require that the fee is at most a fifth of what the
        // destination receives. Both halves matter. Sweeping the lot keeps the stranded remainder — see
        // BtcpayE2EStack.ReserveForOtherTestsSats — down to the reserve rather than to whatever a fixed
        // amount happened to leave. And a *checked* ratio keeps the fee guard a real assertion: at 20% the
        // 40% ceiling the store is configured with, and the engine's 50% hard backstop, are both comfortably
        // clear, so a sweep that gets through is getting through on its economics rather than on the fee
        // market the fixture happens to be in today.
        var reserve = BtcpayE2EStack.ReserveForOtherTestsSats;
        var sweepable = balance - reserve;
        Assert.True(
            sweepable >= feeSats * 6,
            $"this chain's exit fee ({feeSats:N0} sats) is too large a share of the {balance:N0} sat balance "
            + $"to sweep {sweepable:N0} while leaving {reserve:N0} behind — the plugin's fee guard would "
            + "refuse it, correctly. Re-run e2e/btcpay/up.sh, which funds the store afresh.");
        await WriteSweepSettingsAsync(destination, reserve);

        string txId;
        string idempotencyKey;
        using (var swept = await _stack.Api.PostStoreAsync("/spark/sweep", "{}", Ct))
        {
            var outcome = swept.RootElement.GetProperty("outcome").GetString();
            Assert.True(
                outcome == "Swept",
                $"the sweep was not accepted: outcome {outcome}, refusalCode "
                + $"{Text(swept.RootElement, "refusalCode")}, reason {Text(swept.RootElement, "reason")}. "
                + $"(balance {balance:N0}, reserve {reserve:N0}, quoted fee {feeSats:N0})");

            var record = swept.RootElement.GetProperty("record");
            idempotencyKey = record.GetProperty("idempotencyKey").GetString()!;
            Assert.Equal(destination, record.GetProperty("destinationAddress").GetString());
            Assert.Equal("StaticAddress", record.GetProperty("destinationMode").GetString());
            Assert.Equal("Manual", record.GetProperty("trigger").GetString());
            txId = record.GetProperty("txId").GetString()
                   ?? throw new InvalidOperationException(
                       "the accepted sweep record carries no txId, so there is nothing to mine to.");
        }

        // Mined here rather than waited for: nothing else advances an idle regtest chain, and the plugin's
        // reconciliation pass is what turns confirmations into Confirmed.
        await _stack.Control.MineAsync(6, Ct);

        var confirmed = await PollAsync(
            ConfirmationTimeout,
            async () =>
            {
                await _stack.Control.MineAsync(1, Ct);
                using var configuration = await _stack.Api.GetStoreAsync("/spark/sweep", Ct);
                var record = configuration.RootElement.GetProperty("history").EnumerateArray().FirstOrDefault(
                    entry => string.Equals(
                        entry.GetProperty("idempotencyKey").GetString(), idempotencyKey,
                        StringComparison.Ordinal));

                if (record.ValueKind is not JsonValueKind.Object)
                    return null;

                return record.GetProperty("status").GetString() == "Confirmed"
                    ? record.GetProperty("feeSats").GetInt64().ToString(CultureInfo.InvariantCulture)
                    : null;
            });

        Assert.NotNull(confirmed);

        // And the chain agrees. The plugin's Confirmed is a claim about a transaction; this is the
        // transaction.
        using var tx = await _stack.Control.BitcoinCliJsonAsync(["getrawtransaction", txId, "true"], Ct);
        var confirmations = tx.RootElement.TryGetProperty("confirmations", out var count)
            ? count.GetInt32()
            : 0;
        Assert.True(
            confirmations > 0,
            $"the plugin recorded the sweep {txId} as Confirmed, but the fixture's bitcoind reports "
            + $"{confirmations} confirmations for it.");

        Console.WriteLine(
            $"btcpay-e2e: swept {sweepable:N0} sats to {destination} in {txId} for {confirmed} sats of fee "
            + $"(quoted {feeSats:N0}), confirmed at {confirmations} confirmations, leaving a "
            + $"{reserve:N0} sat reserve");
    }

    private async Task<JsonElement> ReadSweepSettingsAsync()
    {
        using var configuration = await _stack.Api.GetStoreAsync("/spark/sweep", Ct);
        return configuration.RootElement.GetProperty("settings").Clone();
    }

    /// <summary>
    /// Replaces the store's sweep configuration, keeping every field this suite depends on.
    /// </summary>
    /// <remarks>
    /// <c>PUT</c> is a full replacement — a field the body omits takes its default, it does not keep its
    /// current value — so the whole configuration is sent every time. <c>enabled</c> stays false: every sweep
    /// here is a manual trigger, and an automatic pass firing between two tests would move money nothing
    /// asserted on.
    /// </remarks>
    private async Task WriteSweepSettingsAsync(string destination, long reserveSats)
    {
        using var applied = await _stack.Api.PutStoreAsync(
            "/spark/sweep",
            $$"""
            {
              "enabled": false,
              "balanceThresholdSats": 10000,
              "reserveSats": {{reserveSats.ToString(CultureInfo.InvariantCulture)}},
              "minimumSweepSats": 10000,
              "maxFeePercent": 40.0,
              "drainWhenSweeping": true,
              "confirmationSpeed": "Medium",
              "destinationMode": "StaticAddress",
              "staticAddress": "{{destination}}"
            }
            """,
            Ct);

        Assert.Equal(
            reserveSats,
            applied.RootElement.GetProperty("settings").GetProperty("reserveSats").GetInt64());
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Polls <paramref name="probe"/> until it answers non-null, or returns null at the deadline.
    /// </summary>
    /// <remarks>
    /// Returns rather than fails so the caller can assert with its own message: "the invoice never settled"
    /// and "the sweep never confirmed" call for completely different next steps, and a shared helper that
    /// failed here could say neither.
    /// </remarks>
    private static async Task<T?> PollAsync<T>(TimeSpan timeout, Func<Task<T?>> probe) where T : class
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (await probe() is { } answer)
                    return answer;
            }
            catch (BtcpayGreenfieldException ex)
            {
                // A transient 5xx while BTCPay is busy is not a verdict; the deadline is the judge. Anything
                // structural repeats until the deadline and is then reported by the caller's assertion.
                last = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        }

        if (last is not null)
            Console.WriteLine($"btcpay-e2e: the last poll error was {last.Message}");

        return null;
    }
}
