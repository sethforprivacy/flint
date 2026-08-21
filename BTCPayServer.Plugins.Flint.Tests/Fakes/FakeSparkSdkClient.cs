using System.Numerics;
using Breez.Sdk.Spark;
using BTCPayServer.Plugins.Flint.Sdk;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// In-memory <see cref="ISparkSdkClient"/> that models the SDK's hazards rather than an idealised SDK.
/// </summary>
/// <remarks>
/// <para>
/// A fake that only ever behaves well would let through the bugs it is meant to catch. So this one: honours
/// <c>From</c>, <c>Offset</c> and <c>Limit</c> on payment queries (a single unpaged page hides the case where
/// the target payment has been pushed off page one); indexes sends under their idempotency key the way the
/// real SDK does by adopting the key as the payment id; can hold both the Receive and the Send leg of one
/// payment hash; and can be told to fail a send after the quote was approved.
/// </para>
/// <para>
/// The cooperative-exit surface models five hazards the funded run found, all of which the sweep engine has to
/// survive and none of which a cooperative fake would ever produce:
/// </para>
/// <list type="bullet">
/// <item><description><b>The quote does not check the balance.</b>
/// <see cref="QuoteOnchainSendAsync"/> succeeds whenever the amount alone fits, exactly as
/// <c>PrepareSendPayment</c> does; the <c>amount + fee &lt;= balance</c> check happens only at send. That is how
/// "insufficient funds at send despite a clean quote" arises here rather than being stipulated.</description></item>
/// <item><description><b>Quotes expire.</b> <see cref="ExpireNextQuotes"/> makes the next N sends fail the way
/// the service provider does, so a caller that reuses a stale prepare — or forgets to re-quote — is
/// caught.</description></item>
/// <item><description><b>The dust floor is enforced locally, at quote time.</b></description></item>
/// <item><description><b>A send can return still-pending and never complete.</b> The default
/// <see cref="NextOnchainSendStatus"/> is <c>Pending</c>, and nothing in this fake ever promotes it: a caller
/// that treats "sent" as "confirmed" fails.</description></item>
/// <item><description><b>An idempotency-key replay returns the original payment and spends nothing.</b> The
/// balance is untouched and <see cref="OnchainSendCalls"/> still records the attempt, so a test can prove both
/// halves.</description></item>
/// </list>
/// </remarks>
public sealed class FakeSparkSdkClient : ISparkSdkClient
{
    /// <summary>The SDK's script-type-dependent floor for a P2WPKH destination, enforced at quote time.</summary>
    public const long DustFloorSats = 294;

    private readonly WriteLog? _writeLog;

    public FakeSparkSdkClient(WriteLog? writeLog = null)
    {
        _writeLog = writeLog;
    }

    public long BalanceSats { get; set; } = 12_345;
    public string IdentityPubkey { get; set; } = "02aafff7";

    /// <summary>Token balances reported by <see cref="GetInfoAsync"/>. Empty is the normal state.</summary>
    public List<SparkTokenBalance> TokenBalances { get; } = [];

    /// <summary>Thrown by every method when set, to exercise the failure paths.</summary>
    public Exception? FailWith { get; set; }

    /// <summary>Thrown only by <see cref="SendBolt11Async"/>, after the quote has been approved.</summary>
    public Exception? FailSendWith { get; set; }

    public List<ReceiveCall> ReceiveCalls { get; } = [];
    public List<SparkListPaymentsQuery> ListQueries { get; } = [];
    public List<string> GetPaymentCalls { get; } = [];
    public List<SendCall> SendCalls { get; } = [];

    /// <summary>Invoice returned by the next <see cref="ReceiveBolt11Async"/>.</summary>
    public string NextPaymentRequest { get; set; } = "lnbcrt-fake";

    /// <summary>
    /// Payments addressable by id. The real SDK adopts a send's idempotency key as its payment id, so
    /// <see cref="SendBolt11Async"/> registers its result here under that key.
    /// </summary>
    public Dictionary<string, SparkPayment> PaymentsById { get; } = [];

    /// <summary>Payment history, oldest first. Queries return it newest-first, as the SDK does.</summary>
    public List<SparkPayment> Payments { get; } = [];

    /// <summary>Quote handed to the caller's fee-approval callback.</summary>
    public SparkSendQuote NextQuote { get; set; } = new(1000, 4, null);

    /// <summary>Status the next successful send resolves to.</summary>
    public SparkPaymentStatus NextSendStatus { get; set; } = SparkPaymentStatus.Completed;

    /// <summary>Payment returned by a send that was not vetoed. Overrides <see cref="NextSendStatus"/>.</summary>
    public SparkPayment? NextSendResult { get; set; }

    public bool Disconnected { get; private set; }
    public bool Disposed { get; private set; }
    public int SyncCount { get; private set; }

    public Task<SparkNodeInfo> GetInfoAsync(bool ensureSynced, CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        // Logged with its argument, because the sweep engine's invariant is an *ordering* — sync, then read — and a
        // call count cannot express one.
        _writeLog?.Record(ensureSynced ? "sdk:getinfo:synced" : "sdk:getinfo:cached");
        return Task.FromResult(new SparkNodeInfo(IdentityPubkey, BalanceSats, TokenBalances.ToList()));
    }

    public Task SyncWalletAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        SyncCount++;
        _writeLog?.Record("sdk:sync");
        return Task.CompletedTask;
    }

    public Task<SparkReceiveResult> ReceiveBolt11Async(
        string description,
        long? amountSats,
        uint expirySecs,
        CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        ReceiveCalls.Add(new ReceiveCall(description, amountSats, expirySecs));
        return Task.FromResult(new SparkReceiveResult(NextPaymentRequest, 0));
    }

    /// <summary>
    /// When set, <see cref="GetPaymentAsync"/> returns a task that never completes.
    /// </summary>
    /// <remarks>
    /// Deliberately ignores the cancellation token, for the same reason
    /// <see cref="FakeSparkSdkClientFactory"/>'s hanging connect does: no <c>IBreezSdk</c> method takes one and
    /// none can be aborted, so a fake that honoured the token would let a caller "fix" a hang by passing a
    /// cancellation it does not really have. Anything that must survive a hung SDK read has to bound its own
    /// wait, and this is what proves it does.
    /// </remarks>
    public bool HangGetPayment { get; set; }

    public Task<SparkPayment?> GetPaymentAsync(string sdkPaymentId, CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        GetPaymentCalls.Add(sdkPaymentId);
        if (HangGetPayment)
            return new TaskCompletionSource<SparkPayment?>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        return Task.FromResult(PaymentsById.GetValueOrDefault(sdkPaymentId));
    }

    public Task<IReadOnlyList<SparkPayment>> ListPaymentsAsync(
        SparkListPaymentsQuery query,
        CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        ListQueries.Add(query);

        // Newest-first, then the window, then the page — the same order the SDK applies them, which is what
        // makes an unpaged caller's blind spot reproducible here.
        IEnumerable<SparkPayment> results = Enumerable.Reverse(Payments);
        if (query.Direction is { } direction)
            results = results.Where(p => p.Direction == direction);
        if (query.CompletedOnly)
            results = results.Where(p => p.Status is SparkPaymentStatus.Completed);
        if (query.From is { } from)
            results = results.Where(p => p.Timestamp >= from);

        // Honoured, because a caller that pages in the wrong direction walks away from what it is looking for
        // and a fake that ignored the flag would let that pass.
        if (query.Ascending)
            results = results.Reverse();

        return Task.FromResult<IReadOnlyList<SparkPayment>>(
            results.Skip(query.Offset).Take(query.Limit).ToList());
    }

    /// <summary>
    /// Completed by <see cref="SendBolt11Async"/> the moment it is entered, so a test can know a send is in
    /// flight rather than guessing with a sleep.
    /// </summary>
    public TaskCompletionSource SendEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// When set, <see cref="SendBolt11Async"/> waits on it before producing a result — holding one send open so a
    /// second concurrent caller can be observed arriving (or, once serialised, observed not arriving).
    /// </summary>
    public TaskCompletionSource? HoldSendUntil { get; set; }

    public async Task<SparkSendResult> SendBolt11Async(
        string bolt11,
        long? amountSats,
        string idempotencyKey,
        Func<SparkSendQuote, string?> approveQuote,
        TimeSpan? completionTimeout,
        CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        SendCalls.Add(new SendCall(bolt11, amountSats, idempotencyKey, completionTimeout));
        SendEntered.TrySetResult();
        if (HoldSendUntil is not null)
            await HoldSendUntil.Task.ConfigureAwait(false);

        var rejection = approveQuote(NextQuote);
        if (rejection is not null)
            return new SparkSendResult(null, NextQuote, rejection);

        if (FailSendWith is not null)
            throw FailSendWith;

        var payment = NextSendResult ?? new SparkPayment(
            // The real SDK adopts the idempotency key as the payment id.
            idempotencyKey,
            SparkPaymentDirection.Send,
            NextSendStatus,
            SparkPaymentMethod.Lightning,
            NextQuote.AmountSats,
            NextQuote.FeeSats,
            DateTimeOffset.UtcNow,
            NextQuote.PaymentHash,
            bolt11,
            null,
            null);

        PaymentsById[payment.SdkPaymentId] = payment;
        Payments.Add(payment);
        return new SparkSendResult(payment, NextQuote, null);
    }

    #region Cooperative exits

    /// <summary>
    /// Tier fees returned by every quote. Defaults to the values measured in the funded regtest run:
    /// <c>userFeeSat</c> 750 plus <c>l1BroadcastFeeSat</c> 1,200/1,440/1,680.
    /// </summary>
    /// <remarks>
    /// Flat on purpose — the same numbers whatever the amount, which is what the real SDK does and what the whole
    /// economics of sweeping turns on.
    /// </remarks>
    public SparkOnchainFeeQuote OnchainTiers { get; set; } = new(
        "SparkCoopExitFeeQuote:019fccd4-f72e-510c-0000-5126aade792a",
        DateTimeOffset.UnixEpoch.AddSeconds(1785847996),
        SlowFeeSats: 1950,
        MediumFeeSats: 2190,
        FastFeeSats: 2430);

    /// <summary>
    /// Tier fees the <em>send</em> commits to, when they differ from what a preceding quote showed.
    /// </summary>
    /// <remarks>
    /// The hazard: a cooperative-exit quote lives about a minute, and the fee it names is not a promise. A caller
    /// that checks its fee limit only against the earlier quote would commit to whatever the send came back with —
    /// so this is what makes the guard inside the approval callback the enforcement point rather than a duplicate
    /// of the pre-flight one.
    /// </remarks>
    public SparkOnchainFeeQuote? OnchainTiersAtSend { get; set; }

    /// <summary>Thrown by <see cref="QuoteOnchainSendAsync"/> when set, in place of quoting.</summary>
    public Exception? FailQuoteWith { get; set; }

    /// <summary>Thrown by <see cref="SendToBitcoinAddressAsync"/> when set, after the quote is approved.</summary>
    public Exception? FailOnchainSendWith { get; set; }

    /// <summary>
    /// The balance the <em>send</em> sees, when it differs from what the quote saw.
    /// </summary>
    /// <remarks>
    /// Set this to reproduce the hazard rather than stipulate it: the quote does not check the balance, exactly as
    /// <c>PrepareSendPayment</c> does not, so a balance that shrinks in between produces the real
    /// "insufficient funds" the service provider would — a receive that has not settled, or a concurrent spend.
    /// </remarks>
    public long? BalanceOnSend { get; set; }

    /// <summary>
    /// How many of the next sends fail with the service provider's expired-quote error before one succeeds.
    /// </summary>
    public int ExpireNextQuotes { get; set; }

    /// <summary>Status the next successful cooperative exit resolves to. Pending is the observed norm.</summary>
    public SparkPaymentStatus NextOnchainSendStatus { get; set; } = SparkPaymentStatus.Pending;

    /// <summary>Txid reported on the exit. Present from the first pending event in the funded run.</summary>
    public string? NextOnchainTxId { get; set; } =
        "8808985e78ad465c25727d5ad749f60a5787855d4f1ddffebfc4afb4dbde1b37";

    public List<OnchainQuoteCall> OnchainQuoteCalls { get; } = [];
    public List<OnchainSendCall> OnchainSendCalls { get; } = [];

    /// <summary>
    /// Quotes handed to the caller's approval callback, in order — so a test can prove the guard was offered the
    /// committed quote rather than the earlier estimate.
    /// </summary>
    public List<SparkOnchainQuote> ApprovedQuotes { get; } = [];

    public Task<SparkOnchainFeeQuote> QuoteOnchainSendAsync(
        string address,
        long amountSats,
        bool feesIncluded,
        CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        OnchainQuoteCalls.Add(new OnchainQuoteCall(address, amountSats, feesIncluded));
        _writeLog?.Record("sdk:quote");

        if (FailQuoteWith is not null)
            throw FailQuoteWith;

        // Local, 0 ms, and on the requested amount rather than on what the recipient nets — which is what the real
        // SDK checks, and why the engine has to do the recipient-side arithmetic itself.
        if (amountSats < DustFloorSats)
        {
            throw new SdkException.InvalidInput(
                $"@v1=Amount is below the minimum of {DustFloorSats} sats required for this address");
        }

        // Deliberately no balance check. PrepareSendPayment does not validate amount + fee <= balance; only
        // amount > balance fails, and even that only at send.
        return Task.FromResult(OnchainTiers);
    }

    public Task<SparkOnchainSendResult> SendToBitcoinAddressAsync(
        string address,
        long amountSats,
        SparkOnchainSpeed speed,
        bool feesIncluded,
        string idempotencyKey,
        Func<SparkOnchainQuote, string?> approveQuote,
        CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        OnchainSendCalls.Add(new OnchainSendCall(address, amountSats, speed, feesIncluded, idempotencyKey));
        _writeLog?.Record($"sdk:send:{idempotencyKey}");

        // No dust check here. The real SDK enforces the floor when the payment is prepared, which
        // QuoteOnchainSendAsync models, and duplicating it on this path only ever produced an unreachable branch.

        var tiers = OnchainTiersAtSend ?? OnchainTiers;
        var quote = new SparkOnchainQuote(amountSats, tiers.FeeFor(speed), feesIncluded, tiers);
        ApprovedQuotes.Add(quote);

        var rejection = approveQuote(quote);
        if (rejection is not null)
            return Task.FromResult(new SparkOnchainSendResult(null, quote, rejection));

        // Deduplication is keyed on the idempotency key alone — not on the prepare — and returns the original
        // payment without spending again. Checked before every failure mode below, because a replay of a send that
        // already went through must not be able to fail.
        if (PaymentsById.TryGetValue(idempotencyKey, out var existing))
            return Task.FromResult(new SparkOnchainSendResult(existing, quote, null));

        if (ExpireNextQuotes > 0)
        {
            ExpireNextQuotes--;
            throw new SdkException.SparkException(
                "@v1=Service error: service provider error: graphql error: The coop exit fee quote has expired, "
                + "please request a new quote.");
        }

        if (FailOnchainSendWith is not null)
            throw FailOnchainSendWith;

        // The check the quote skipped. One message covers "no funds at all" and "not enough for amount + fee"; the
        // real SDK cannot distinguish them either. BalanceOnSend is how a test makes the balance move between the
        // quote and the send, which is the only way this branch is reachable through the engine — the engine's own
        // arithmetic keeps it unreachable otherwise, which is the point of that arithmetic.
        if (BalanceOnSend is { } atSend)
            BalanceSats = atSend;

        var debited = feesIncluded ? amountSats : amountSats + quote.FeeSats;
        if (debited > BalanceSats)
            throw new SdkException.SparkException("@v1=Tree service error: insufficient funds");

        BalanceSats -= debited;

        var payment = new SparkPayment(
            // The SDK adopts the idempotency key as the payment id, verified on real cooperative exits.
            idempotencyKey,
            SparkPaymentDirection.Send,
            NextOnchainSendStatus,
            SparkPaymentMethod.Withdraw,
            // The payment's amount is what the destination gets, NOT what was asked for: under FeesIncluded the
            // SDK has already netted the fee out by the time it hands back a Payment, so amount + fees == debited
            // under both policies. The funded run measured a 72,733 sat drain coming back as amount 70,783 with
            // fees 1,950, and a mainnet sweep of 62,000 at a 1,710 sat fee paid the destination exactly 60,290.
            // A fake that echoed the requested amount here would be idealised in precisely the way that let a
            // double-subtraction of the fee ship in the merchant-facing sweep message.
            quote.RecipientAmountSats,
            quote.FeeSats,
            DateTimeOffset.UtcNow,
            PaymentHash: null,
            Bolt11: null,
            Preimage: null,
            Description: $"on-chain withdrawal {NextOnchainTxId}",
            TxId: NextOnchainTxId);

        PaymentsById[payment.SdkPaymentId] = payment;
        Payments.Add(payment);
        return Task.FromResult(new SparkOnchainSendResult(payment, quote, null));
    }

    #endregion

    #region On-chain deposits

    /// <summary>The static deposit address. Constant across calls, as the real one is.</summary>
    public string DepositAddress { get; set; } = "bc1pfake0staticdeposit0address0000000000000000000000000000000000";

    /// <summary>Thrown by <see cref="GetBitcoinDepositAddressAsync"/> when set.</summary>
    /// <remarks>
    /// A real possibility rather than a contrived one: minting the address is a live service-provider call,
    /// unlike listing unclaimed deposits, which is a local storage read. A surface that let a failure here stop
    /// it from showing the unclaimed list would be hiding the thing a merchant came to look at.
    /// </remarks>
    public Exception? FailDepositAddressWith { get; set; }

    /// <summary>Thrown by <see cref="ListUnclaimedDepositsAsync"/> when set.</summary>
    public Exception? FailUnclaimedDepositsWith { get; set; }

    /// <summary>Thrown by <see cref="GetRecommendedFeesAsync"/> when set.</summary>
    public Exception? FailRecommendedFeesWith { get; set; }

    /// <summary>Deposits the SDK knows about and has not credited.</summary>
    public List<SparkDepositInfo> UnclaimedDeposits { get; } = [];

    /// <summary>
    /// Fee rates reported to callers, defaulted to the live mainnet sample the spike took.
    /// </summary>
    /// <remarks>
    /// Deliberately the <em>cheap</em> market that was observed, not an expensive one. It is the case that makes
    /// the point: even at <c>fastestFee=3</c> the SDK's default 1 sat/vB claim ceiling is already below the
    /// half-hour rate, so a fake that modelled an expensive mempool would make the default look wrong only
    /// under stress rather than essentially always.
    /// </remarks>
    public SparkRecommendedFees RecommendedFees { get; set; } = new(3, 3, 2, 2, 1);

    /// <summary>
    /// Returned by <see cref="ClaimDepositAsync"/> in place of a payment, when set.
    /// </summary>
    /// <remarks>
    /// A returned failure, not a thrown one — matching the real client, which catches and reports, because a
    /// claim that failed broadcast nothing and must leave the deposit claimable again.
    /// </remarks>
    public string? ClaimFailsWith { get; set; }

    public List<ClaimCall> ClaimCalls { get; } = [];

    public Task<string> GetBitcoinDepositAddressAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        _writeLog?.Record("sdk:deposit-address");
        if (FailDepositAddressWith is not null)
            throw FailDepositAddressWith;
        return Task.FromResult(DepositAddress);
    }

    public Task<IReadOnlyList<SparkDepositInfo>> ListUnclaimedDepositsAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        if (FailUnclaimedDepositsWith is not null)
            throw FailUnclaimedDepositsWith;
        return Task.FromResult<IReadOnlyList<SparkDepositInfo>>(UnclaimedDeposits.ToList());
    }

    public Task<SparkClaimDepositResult> ClaimDepositAsync(
        string txId,
        uint vout,
        SparkMaxFee maxFee,
        CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        ClaimCalls.Add(new ClaimCall(txId, vout, maxFee));
        _writeLog?.Record($"sdk:claim:{txId}:{vout}");

        if (ClaimFailsWith is not null)
            return Task.FromResult(new SparkClaimDepositResult(null, ClaimFailsWith));

        var deposit = UnclaimedDeposits.FirstOrDefault(
            candidate => candidate.TxId == txId && candidate.Vout == vout);

        // The real SDK will not claim a deposit it does not have, and a fake that invented one would let a
        // caller "claim" a deposit that had already been claimed between the page render and the button press.
        if (deposit is null)
            return Task.FromResult(new SparkClaimDepositResult(null, "No such unclaimed deposit."));

        // The fee actually charged is capped by, not equal to, the ceiling — so a caller that reported the
        // ceiling as the fee paid would be overstating it.
        var chargedFee = maxFee switch
        {
            SparkMaxFee.Fixed fixedFee => Math.Min(fixedFee.Sats, deposit.ClaimError?.RequiredFeeSats ?? fixedFee.Sats),
            _ => deposit.ClaimError?.RequiredFeeSats ?? 0
        };

        UnclaimedDeposits.Remove(deposit);
        BalanceSats += deposit.AmountSats - chargedFee;

        var payment = new SparkPayment(
            Guid.NewGuid().ToString(),
            SparkPaymentDirection.Receive,
            SparkPaymentStatus.Completed,
            SparkPaymentMethod.Deposit,
            deposit.AmountSats - chargedFee,
            chargedFee,
            DateTimeOffset.UtcNow,
            PaymentHash: null,
            Bolt11: null,
            Preimage: null,
            Description: $"on-chain deposit {txId}:{vout}",
            TxId: txId);

        PaymentsById[payment.SdkPaymentId] = payment;
        Payments.Add(payment);
        return Task.FromResult(new SparkClaimDepositResult(payment, null));
    }

    public Task<SparkRecommendedFees> GetRecommendedFeesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        if (FailRecommendedFeesWith is not null)
            throw FailRecommendedFeesWith;
        return Task.FromResult(RecommendedFees);
    }

    public sealed record ClaimCall(string TxId, uint Vout, SparkMaxFee MaxFee);

    #endregion

    #region Stable Balance

    /// <summary>The active stable-balance label the wallet reports. Null is deactivated.</summary>
    public string? StableBalanceActiveLabel { get; set; }

    /// <summary>
    /// Whether this wallet was connected with a stable-balance configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The hazard that hid the deactivation bug.</b> The SDK separates which tokens a wallet has
    /// <em>available</em> — declared in the connect config — from which one is <em>active</em>. Ask a wallet
    /// with no config to deactivate and it throws <c>Stable balance is not configured</c>; a fake that let the
    /// call succeed regardless made "enabled but never disableable" invisible, in the same way a deterministic
    /// quote id hid F1 and a handle that was never disposed hid F2.
    /// </para>
    /// <para>
    /// Set by <see cref="FakeSparkStoreSettingsStore"/> on every reconnect, from the <em>production</em> rule
    /// for building that config — so narrowing the rule breaks these tests rather than passing them.
    /// </para>
    /// </remarks>
    public bool StableBalanceConfigured { get; set; } = true;

    /// <summary>Thrown by <see cref="GetUserSettingsAsync"/> when set.</summary>
    public Exception? FailUserSettingsWith { get; set; }

    /// <summary>Thrown by <see cref="SetStableBalanceActiveAsync"/> when set.</summary>
    public Exception? FailStableBalanceWith { get; set; }

    /// <summary>
    /// Token identifiers <see cref="FetchConversionLimitsAsync"/> will answer for.
    /// </summary>
    /// <remarks>
    /// Anything else is rejected, which is what the real SDK does with an identifier it does not know — and is
    /// the check the plugin relies on to refuse a mistyped token <em>before</em> storing it. A fake that
    /// answered for every string would let a store be configured for a token that does not exist.
    /// </remarks>
    public HashSet<string> KnownTokens { get; } = [StableBalanceSettings.DefaultTokenIdentifier];

    /// <summary>The BTC→token conversion floor, in satoshi, as measured live.</summary>
    public BigInteger? ConversionMinimumFromBitcoinSats { get; set; } = 800;

    public List<StableBalanceCall> StableBalanceCalls { get; } = [];
    public int RefundPendingConversionsCalls { get; private set; }

    public Task<SparkUserSettings> GetUserSettingsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        if (FailUserSettingsWith is not null)
            throw FailUserSettingsWith;
        return Task.FromResult(new SparkUserSettings(false, StableBalanceActiveLabel));
    }

    public Task SetStableBalanceActiveAsync(
        bool activate,
        string? label,
        CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        StableBalanceCalls.Add(new StableBalanceCall(activate, label));
        _writeLog?.Record($"sdk:stable:{(activate ? "on" : "off")}");

        if (FailStableBalanceWith is not null)
            throw FailStableBalanceWith;

        // What the real SDK does with a wallet that has no stable-balance config: it refuses, in both
        // directions. Deactivation is the one that matters, because it is the direction a merchant needs when
        // their money is already in the token.
        if (!StableBalanceConfigured)
            throw new SdkException.Generic("@v1=Stable balance is not configured");

        // Only the label changes. The *balance* deliberately does not: the real conversion runs on a background
        // worker and no event reports it, so a fake that moved the balance here would let a caller believe the
        // money had converted by the time this returned.
        StableBalanceActiveLabel = activate ? label : null;
        return Task.CompletedTask;
    }

    public Task<SparkConversionLimits> FetchConversionLimitsAsync(
        SparkConversionDirection direction,
        SparkTokenIdentifier token,
        CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();

        if (!KnownTokens.Contains(token.Value))
            throw new SdkException.InvalidInput($"@v1=Unknown token identifier: {token.Value}");

        // The unit of the returned minimum follows the *from* side, so the same field means satoshi in one
        // direction and token base units in the other. Modelled, because a caller that compared them would be
        // comparing a dollar figure with a sats figure.
        return Task.FromResult(direction is SparkConversionDirection.FromBitcoin
            ? new SparkConversionLimits(direction, ConversionMinimumFromBitcoinSats, null)
            : new SparkConversionLimits(direction, 500_000, null));
    }

    public Task RefundPendingConversionsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        RefundPendingConversionsCalls++;
        _writeLog?.Record("sdk:refund-conversions");
        return Task.CompletedTask;
    }

    public sealed record StableBalanceCall(bool Activate, string? Label);

    #endregion

    #region Cross-chain

    /// <summary>The mainnet USDB identifier, for tests that need a stable-balance token.</summary>
    public static SparkTokenIdentifier Usdb => new(StableBalanceSettings.DefaultTokenIdentifier);

    /// <summary>
    /// Whether the SDK's cross-chain configuration is set.
    /// </summary>
    /// <remarks>
    /// <b>The trap this fake exists to reproduce.</b> With it false the real SDK's route query returns an empty
    /// array and <em>no error at all</em> — the spike watched the identical call go from 0 routes to 54 purely
    /// by setting the config. The client turns that into
    /// <see cref="SparkCrossChainNotConfiguredException"/>, and this is what lets a test prove it does.
    /// </remarks>
    public bool CrossChainConfigured { get; set; } = true;

    /// <summary>
    /// The route table, defaulted to the shape observed live.
    /// </summary>
    /// <remarks>
    /// Three routes, all of which matter. Orchestra carries real USDT on arbitrum and can be funded from either
    /// balance. Boltz carries USDT on the same chain and <b>only from Bitcoin</b> — it cannot do token sends in
    /// v1 — and it carries <c>USDT0</c> elsewhere, which is the LayerZero token and a different asset a
    /// merchant expecting Tether will not accept. Every one of those is a way for a naive filter to pick the
    /// wrong route.
    /// </remarks>
    public List<SparkCrossChainRoute> CrossChainRoutes { get; } =
    [
        new(SparkCrossChainProvider.Orchestra, "arbitrum", "42161", "USDT",
            "0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9", 6,
            [SparkCrossChainSource.Bitcoin, SparkCrossChainSource.Token], "route:orchestra:arbitrum:USDT"),
        new(SparkCrossChainProvider.Boltz, "arbitrum", "42161", "USDT",
            "0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9", 6,
            [SparkCrossChainSource.Bitcoin], "route:boltz:arbitrum:USDT"),
        new(SparkCrossChainProvider.Boltz, "polygon", "137", "USDT0",
            "0xc2132d05d31c914a87c6611c10748aeb04b58e8f", 6,
            [SparkCrossChainSource.Bitcoin], "route:boltz:polygon:USDT0")
    ];

    /// <summary>Thrown by <see cref="GetCrossChainRoutesAsync"/> when set, in place of answering.</summary>
    public Exception? FailCrossChainRoutesWith { get; set; }

    /// <summary>Thrown by a cross-chain prepare when set, after the route has been chosen.</summary>
    public Exception? FailCrossChainQuoteWith { get; set; }

    /// <summary>
    /// Run after each prepare, so a test can move the market between the pre-flight quote and the send's.
    /// </summary>
    /// <remarks>
    /// The hazard it exists for: a cross-chain quote lives about a minute and the spread it names is not a
    /// promise. A caller that checked its fee ceiling only against the earlier quote would commit to whatever
    /// the send came back with, and nothing else in this fake can reproduce that.
    /// </remarks>
    public Action? WhenQuoted { get; set; }

    /// <summary>Thrown by <see cref="SendCrossChainAsync"/> when set, after the quote is approved.</summary>
    public Exception? FailCrossChainSendWith { get; set; }

    /// <summary>How many of the next cross-chain sends fail with the provider's expired-quote error.</summary>
    public int ExpireNextCrossChainQuotes { get; set; }

    /// <summary>
    /// The provider's own floor, in satoshi, below which a prepare is refused.
    /// </summary>
    /// <remarks>
    /// 1,500, the lowest amount that succeeded when the spike binary-searched it — the SDK exposes no getter,
    /// so this is discoverable only by attempting a prepare, and arrives as a <c>NetworkException</c> rather
    /// than as a typed "too small".
    /// </remarks>
    public long CrossChainMinimumSats { get; set; } = 1_500;

    /// <summary>
    /// Destination-asset base units per satoshi, as a rational, from the live quote the spike captured.
    /// </summary>
    /// <remarks>
    /// 35,721,666 USDT base units for 55,277 sats. Kept as a ratio rather than a rounded rate so the fake's
    /// arithmetic stays integral and a test can assert exact figures.
    /// </remarks>
    public (BigInteger Numerator, BigInteger Denominator) CrossChainRate { get; set; } = (35_721_666, 55_277);

    /// <summary>The provider's spread, in basis points of the gross destination amount.</summary>
    public long CrossChainFeeBps { get; set; } = 34;

    /// <summary>
    /// How far the source leg is overpaid, in basis points, floored at 50 satoshi.
    /// </summary>
    /// <remarks>
    /// Defaulted to the <c>max(50 bps, ~50 sats)</c> that was measured live. Adjustable because the pad is
    /// <b>not derivable from any setting</b> — it absorbs the provider's fee as well as slippage, so it moves
    /// with the market — and a caller that sized a sweep against the default and never re-checked the quote
    /// would be caught out the first time it widened.
    /// </remarks>
    public long CrossChainOverpayBps { get; set; } = 50;

    /// <summary>
    /// How many prepares return a provider quote id before the rest come back without one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null — the default — means every prepare carries an id, which is what every observed Orchestra quote
    /// did. The field is nullable on <c>SparkCrossChainQuote</c> all the same, and an external audit (H-2)
    /// found that a missing one silently disabled the only thing that makes a token-funded send recoverable:
    /// the caller wrote the id when there was one and sent anyway when there was not.
    /// </para>
    /// <para>
    /// Two settings, two shapes of the same hazard. <c>0</c> is a provider that never names its quotes at all.
    /// <c>1</c> is the worse one and the one the audit describes: the pre-flight quote has an id, so the row is
    /// written with a plausible-looking handle, and then the prepare the send actually commits to has none —
    /// so the recorded id belongs to a quote that was never used and can never match.
    /// </para>
    /// </remarks>
    public int? NullProviderQuoteIdAfterPrepares { get; set; }

    private int _prepareCount;

    public List<CrossChainRouteQuery> CrossChainRouteQueries { get; } = [];
    public List<CrossChainCall> CrossChainQuoteCalls { get; } = [];
    public List<CrossChainCall> CrossChainSendCalls { get; } = [];

    /// <summary>Quotes handed to the caller's approval callback, in order.</summary>
    public List<SparkCrossChainQuote> ApprovedCrossChainQuotes { get; } = [];

    public Task<IReadOnlyList<SparkCrossChainRoute>> GetCrossChainRoutesAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        CrossChainRouteQueries.Add(new CrossChainRouteQuery(address));
        _writeLog?.Record("sdk:cc-routes");

        if (FailCrossChainRoutesWith is not null)
            throw FailCrossChainRoutesWith;

        // The real client raises this from the same condition — an empty array — rather than the SDK raising
        // anything. Reproduced at this seam because the client is what is under test above it.
        if (!CrossChainConfigured)
            throw new SparkCrossChainNotConfiguredException(address);

        return Task.FromResult<IReadOnlyList<SparkCrossChainRoute>>(CrossChainRoutes.ToList());
    }

    public Task<SparkCrossChainQuote> QuoteCrossChainAsync(
        SparkCrossChainRoute route,
        string recipientAddress,
        SparkSendAmount amount,
        uint? maxSlippageBps,
        CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        CrossChainQuoteCalls.Add(new CrossChainCall(route, recipientAddress, amount, maxSlippageBps, null));
        _writeLog?.Record("sdk:cc-quote");
        return Task.FromResult(Prepare(route, recipientAddress, amount));
    }

    public async Task<SparkCrossChainSendResult> SendCrossChainAsync(
        SparkCrossChainRoute route,
        string recipientAddress,
        SparkSendAmount amount,
        uint? maxSlippageBps,
        string? idempotencyKey,
        Func<SparkCrossChainQuote, Task<string?>> approveQuote,
        CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        CrossChainSendCalls.Add(
            new CrossChainCall(route, recipientAddress, amount, maxSlippageBps, idempotencyKey));
        _writeLog?.Record($"sdk:cc-send:{idempotencyKey ?? "no-key"}");

        // The rejection the spike could not verify live but that three sources agree on, and the reason the
        // token path needs a different crash-safety story at all. Checked first, exactly as the real SDK's
        // validation is: it fails the send outright rather than ignoring the key.
        if (idempotencyKey is not null && amount is SparkSendAmount.Token)
        {
            throw new SdkException.InvalidInput(
                "@v1=Idempotency key is not supported for payments with a token transfer leg (direct token send "
                + "or AMM conversion).");
        }

        // A second prepare, deliberately: this is what the real client does, and it is why the committed quote
        // id differs from the pre-flight one.
        var quote = Prepare(route, recipientAddress, amount);
        ApprovedCrossChainQuotes.Add(quote);

        var rejection = await approveQuote(quote).ConfigureAwait(false);
        if (rejection is not null)
            return new SparkCrossChainSendResult(null, quote, rejection);

        // Deduplication, but only where the SDK offers it. A keyed replay returns the original payment and
        // spends nothing; a keyless one has nothing to deduplicate on and would genuinely send twice, which is
        // exactly why the engine must never retry that path.
        if (idempotencyKey is not null && PaymentsById.TryGetValue(idempotencyKey, out var existing))
            return new SparkCrossChainSendResult(existing, quote, null);

        if (ExpireNextCrossChainQuotes > 0)
        {
            ExpireNextCrossChainQuotes--;
            throw new SdkException.SparkException(
                "@v1=Service error: Cross-chain quote has expired. Please re-prepare.");
        }

        if (FailCrossChainSendWith is not null)
            throw FailCrossChainSendWith;

        if (amount is SparkSendAmount.Bitcoin bitcoin)
        {
            // Debited by amountIn, not by the amount asked for. A fake that debited the requested amount would
            // never reproduce the case where a sweep sized to the whole balance cannot be funded.
            if (quote.AmountInSats > BalanceSats)
                throw new SdkException.SparkException("@v1=Tree service error: insufficient funds");
            BalanceSats -= quote.AmountInSats;
        }

        var conversion = new SparkConversionState(
            route.Provider,
            SparkConversionStatus.Pending,
            quote.ProviderQuoteId,
            $"order-{quote.ProviderQuoteId}",
            // Null: the provider has not delivered yet, and there is no event that will say when it does.
            DeliveredAmount: null,
            recipientAddress,
            route.Chain,
            route.Asset,
            route.Decimals);

        var payment = new SparkPayment(
            // Keyed sends adopt the key as the payment id; keyless ones get an id the caller has never seen,
            // which is the whole reason the provider quote id has to be persisted before the send.
            idempotencyKey ?? $"sdk-{Guid.NewGuid()}",
            SparkPaymentDirection.Send,
            SparkPaymentStatus.Pending,
            // No cross-chain payment method exists. A sats-funded send is a Spark transfer to the provider's
            // deposit address; a token-funded one is a Token transfer.
            amount is SparkSendAmount.Token ? SparkPaymentMethod.Token : SparkPaymentMethod.Spark,
            amount is SparkSendAmount.Bitcoin sats ? sats.Sats : 0,
            0,
            DateTimeOffset.UtcNow,
            PaymentHash: null,
            Bolt11: null,
            Preimage: null,
            Description: $"cross-chain to {recipientAddress}",
            TxId: null,
            Conversion: conversion);

        PaymentsById[payment.SdkPaymentId] = payment;
        Payments.Add(payment);
        return new SparkCrossChainSendResult(payment, quote, null);
    }

    /// <summary>
    /// One cross-chain prepare, modelling the arithmetic and the two ways it refuses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Boltz always fails here</b>, with the exact message every one of the spike's six prepare attempts
    /// returned across three chains and three amounts. That is not a contrivance: the provider is unusable
    /// today, its routes nevertheless appear in the route table, and a fake that let Boltz prepare would let a
    /// route filter that trusts the table pass its tests and then fail on mainnet.
    /// </para>
    /// <para>
    /// <b><c>amountIn</c> for a token-funded send is deliberately absurd.</b> The field's meaning was only ever
    /// observed for a sats-funded send, and it is a satoshi figure; what it contains for a token source is
    /// unverified. Returning a number larger than any balance means a caller that applies the sats-side balance
    /// check on the token path fails immediately, rather than passing here and being wrong on mainnet.
    /// </para>
    /// </remarks>
    private SparkCrossChainQuote Prepare(
        SparkCrossChainRoute route,
        string recipientAddress,
        SparkSendAmount amount)
    {
        if (route.Provider is SparkCrossChainProvider.Boltz)
        {
            throw new SdkException.NetworkException(
                "@v1=Boltz API: BTC/TBTC pair not found. Is referral header configured?");
        }

        if (amount is SparkSendAmount.Token && !route.SupportsToken)
            throw new SdkException.InvalidInput("@v1=This route does not support token sends.");

        if (FailCrossChainQuoteWith is not null)
            throw FailCrossChainQuoteWith;

        BigInteger assetAmountIn;
        long amountInSats;

        switch (amount)
        {
            case SparkSendAmount.Bitcoin bitcoin:
                if (bitcoin.Sats < CrossChainMinimumSats)
                {
                    // The provider's own floor, enforced server-side and discoverable only by asking. Arrives
                    // as a network exception carrying the provider's prose, with no typed variant and no code.
                    throw new SdkException.NetworkException("@v1=Amount too small (code: 400)");
                }

                // The source leg is overpaid to absorb the fee and slippage: max(50 bps, ~50 sats), as measured.
                amountInSats = bitcoin.Sats + Math.Max(50, bitcoin.Sats * CrossChainOverpayBps / 10_000);
                assetAmountIn = new BigInteger(amountInSats) * CrossChainRate.Numerator / CrossChainRate.Denominator;
                break;

            case SparkSendAmount.Token token:
                assetAmountIn = token.BaseUnits;
                // See the remarks: unverified for a token source, so made unmistakably unusable.
                amountInSats = long.MaxValue;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(amount));
        }

        var fee = assetAmountIn * CrossChainFeeBps / 10_000;
        var estimatedOut = assetAmountIn - fee;

        // Rounded down to whole cents, as every observed quote was.
        var cent = BigInteger.Pow(10, (int)Math.Max(0, route.Decimals - 2));
        if (cent > BigInteger.One)
            estimatedOut = estimatedOut / cent * cent;

        WhenQuoted?.Invoke();

        // Counted before the id is chosen, so "the first prepare keeps its id" reads the way it is written.
        var prepareOrdinal = ++_prepareCount;
        var quoteId = NullProviderQuoteIdAfterPrepares is { } keepFirst && prepareOrdinal > keepFirst
            ? null
            : $"q_{route.Chain}_{Guid.NewGuid():N}";

        return new SparkCrossChainQuote(
            route,
            recipientAddress,
            amountInSats,
            assetAmountIn,
            estimatedOut,
            fee,
            // Two thirds of the fee, billed in a different asset — as observed, where a USDT route's service
            // fee came back denominated in USDC.
            fee * 2 / 3,
            "USDC",
            0,
            DateTimeOffset.UtcNow.AddSeconds(60),
            // A FRESH id per prepare, which is what Orchestra does — a quote is a per-request object.
            //
            // This is the single most important cooperative-fake fix in this file. It previously returned a
            // deterministic $"q_{chain}_{amount}", so a pre-flight quote and the send's own prepare produced the
            // same id by accident, and a caller that persisted the wrong one still matched on recovery. That is
            // exactly the shape of bug this project has shipped twice: the fake was more forgiving than the SDK,
            // so the suite could not see it.
            //
            // Or no id at all — see NullProviderQuoteIdAfterPrepares. The field is nullable on the SDK's own
            // quote type and a fake that always filled it was, again, more forgiving than the type it models.
            quoteId,
            "spark1pgssfakedepositaddress");
    }

    public sealed record CrossChainRouteQuery(string Address);

    public sealed record CrossChainCall(
        SparkCrossChainRoute Route,
        string RecipientAddress,
        SparkSendAmount Amount,
        uint? MaxSlippageBps,
        string? IdempotencyKey);

    #endregion

    #region Unilateral exit

    /// <summary>
    /// The leaves an automatic selection would pick, and their values.
    /// </summary>
    /// <remarks>
    /// Empty by default is <b>not</b> laziness. A unilateral-exit quote with <c>Auto</c> selection returns no
    /// leaves whenever nothing clears the requested fee rate, and that is a normal answer the caller has to
    /// report as "nothing worth exiting" rather than as a fault — so the fake's default state is the one that
    /// catches a caller treating an empty quote as success.
    /// </remarks>
    public List<SparkExitLeaf> ExitLeaves { get; } = [];

    /// <summary>Total fee the quote reports, in satoshi.</summary>
    public long ExitTotalFeeSat { get; set; } = 3_000;

    /// <summary>The fan-out's share of <see cref="ExitTotalFeeSat"/>.</summary>
    public long ExitFanoutFeeSat { get; set; } = 500;

    /// <summary>
    /// The single confirmed output the exit must be funded with, in satoshi.
    /// </summary>
    /// <remarks>
    /// Deliberately larger than <see cref="ExitTotalFeeSat"/>, as the real quote's is: the funding UTXO has to
    /// cover every fee plus the fan-out's own outputs, so a caller that funds against the fee total alone is
    /// under-funded and this default is what catches it.
    /// </remarks>
    public long ExitSingleUtxoFundingSat { get; set; } = 4_200;

    /// <summary>Every prepare this fake has been asked for, in order.</summary>
    public List<ExitQuoteCall> ExitQuoteCalls { get; } = [];

    /// <summary>Every build this fake has been asked for, in order.</summary>
    public List<ExitBuildCall> ExitBuildCalls { get; } = [];

    /// <summary>Thrown by a prepare when set, before any quote is produced.</summary>
    public Exception? FailExitQuoteWith { get; set; }

    /// <summary>Thrown by a build when set, <em>after</em> the quote has been approved.</summary>
    public Exception? FailExitBuildWith { get; set; }

    /// <summary>
    /// Run after each prepare, so a test can move the wallet's tree between the quote a page showed and the
    /// quote a build commits to.
    /// </summary>
    /// <remarks>
    /// The hazard this exists for is the sharpest one on the exit surface, and it is <em>not</em> the
    /// cooperative-exit one. A unilateral-exit quote never expires and carries no id, so a stale one is not
    /// rejected by anything — it simply describes a different set of leaves than the wallet now has, and a build
    /// against it commits to leaves the operator did not fund for. Mutating <see cref="ExitLeaves"/> from here
    /// is how a test proves the caller re-quotes inside the build.
    /// </remarks>
    public Action? WhenExitQuoted { get; set; }

    public Task<SparkExitQuote> PrepareUnilateralExitAsync(
        ulong feeRateSatPerVbyte,
        string destinationAddress,
        IReadOnlyList<string>? leafIds,
        CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        ExitQuoteCalls.Add(new ExitQuoteCall(feeRateSatPerVbyte, destinationAddress, leafIds?.ToList()));

        if (FailExitQuoteWith is not null)
            throw FailExitQuoteWith;

        var quote = BuildExitQuote(feeRateSatPerVbyte, destinationAddress, leafIds);
        WhenExitQuoted?.Invoke();
        return Task.FromResult(quote);
    }

    public Task<SparkExitResult> UnilateralExitAsync(
        ulong feeRateSatPerVbyte,
        string destinationAddress,
        IReadOnlyList<string>? leafIds,
        IReadOnlyList<SparkExitFundingUtxo> fundingUtxos,
        byte[] fundingSecretKey,
        Func<SparkExitQuote, string?> approveQuote,
        CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        ArgumentNullException.ThrowIfNull(fundingUtxos);
        ArgumentNullException.ThrowIfNull(approveQuote);

        // Quoted inside the build, exactly as the real client does, so the veto sees the fresh quote rather than
        // whatever the caller last looked at.
        ExitQuoteCalls.Add(new ExitQuoteCall(feeRateSatPerVbyte, destinationAddress, leafIds?.ToList()));
        if (FailExitQuoteWith is not null)
            throw FailExitQuoteWith;

        var quote = BuildExitQuote(feeRateSatPerVbyte, destinationAddress, leafIds);
        WhenExitQuoted?.Invoke();

        var rejection = approveQuote(quote);
        ExitBuildCalls.Add(new ExitBuildCall(
            feeRateSatPerVbyte,
            destinationAddress,
            leafIds?.ToList(),
            fundingUtxos.ToList(),
            fundingSecretKey?.Length ?? 0,
            rejection));

        if (rejection is not null)
            throw new SparkExitRefusedException(rejection);

        if (FailExitBuildWith is not null)
            throw FailExitBuildWith;

        // The funding check the real SDK makes, reproduced rather than stipulated: the shortfall is discovered at
        // build time and names the amount that would have worked.
        var funded = fundingUtxos.Sum(utxo => utxo.ValueSat);
        if (funded < ExitSingleUtxoFundingSat)
            throw new SparkExitFundingShortfallException(ExitSingleUtxoFundingSat);

        // Signed and inert. Nothing in this fake, and nothing in the real SDK, broadcasts any of it.
        var sweepDependsOn = quote.Leaves.Select(leaf => $"txid:node:{leaf.LeafId}").ToList();
        var transactions = new List<SparkExitTransaction>
        {
            new(SparkExitTxKind.Fanout, null, "txid:fanout", "0200fanout", null, null, [],
                SparkExitTxStatus.Unconfirmed)
        };

        transactions.AddRange(quote.Leaves.Select(leaf => new SparkExitTransaction(
            SparkExitTxKind.TreeNode,
            $"node:{leaf.LeafId}",
            $"txid:node:{leaf.LeafId}",
            $"0200node{leaf.LeafId}",
            // A CPFP child, because a tree node pays no fee of its own and must go out as a package. A fake
            // that left this null would let a caller ship single-transaction broadcast instructions.
            $"0200cpfp{leaf.LeafId}",
            1_008,
            ["txid:fanout"],
            SparkExitTxStatus.Unconfirmed)));

        transactions.Add(new SparkExitTransaction(
            SparkExitTxKind.Sweep, null, "txid:sweep", "0200sweep", null, null, sweepDependsOn,
            SparkExitTxStatus.Unconfirmed));

        return Task.FromResult(new SparkExitResult(
            quote.RecoverableValueSat, quote.TotalFeeSat, transactions, quote.Leaves));
    }

    /// <remarks>
    /// A pinned selection is honoured by filtering, and an id that is no longer in the tree simply does not come
    /// back — which is how a test reproduces the case a resume has to survive: the operator funded for a leaf
    /// set that has since changed under them.
    /// </remarks>
    private SparkExitQuote BuildExitQuote(
        ulong feeRateSatPerVbyte,
        string destinationAddress,
        IReadOnlyList<string>? leafIds)
    {
        var selected = leafIds is null || leafIds.Count == 0
            ? ExitLeaves.ToList()
            : ExitLeaves.Where(leaf => leafIds.Contains(leaf.LeafId)).ToList();

        return new SparkExitQuote(
            selected.Sum(leaf => leaf.ValueSat),
            selected.Count == 0 ? 0 : ExitTotalFeeSat,
            selected.Count == 0 ? 0 : ExitSingleUtxoFundingSat,
            selected,
            selected.Count == 0 ? 0 : ExitFanoutFeeSat,
            selected
                .Select(leaf => new SparkExitBranchFunding(leaf.LeafId, ExitSingleUtxoFundingSat / selected.Count))
                .ToList(),
            feeRateSatPerVbyte,
            destinationAddress);
    }

    public sealed record ExitQuoteCall(
        ulong FeeRateSatPerVbyte,
        string DestinationAddress,
        List<string>? LeafIds);

    public sealed record ExitBuildCall(
        ulong FeeRateSatPerVbyte,
        string DestinationAddress,
        List<string>? LeafIds,
        List<SparkExitFundingUtxo> FundingUtxos,
        int FundingSecretKeyLength,
        string? Rejection);

    #endregion

    public Task DisconnectAsync()
    {
        Disconnected = true;
        return Task.CompletedTask;
    }

    public void Dispose() => Disposed = true;

    /// <summary>
    /// A fresh handle onto the same wallet, as a reconnect produces.
    /// </summary>
    /// <remarks>
    /// State that belongs to the <em>wallet</em> is carried across — balances, payment history, the active
    /// stable-balance label, unclaimed deposits — because a reconnect does not change any of it. State that
    /// belongs to the <em>handle</em>, notably the recorded call lists, deliberately is not: a test asserting
    /// on calls made through a handle that has since been disposed should see them on that handle.
    /// </remarks>
    public FakeSparkSdkClient Reconnected()
    {
        var replacement = new FakeSparkSdkClient(_writeLog)
        {
            BalanceSats = BalanceSats,
            IdentityPubkey = IdentityPubkey,
            StableBalanceActiveLabel = StableBalanceActiveLabel,
            StableBalanceConfigured = StableBalanceConfigured,
            DepositAddress = DepositAddress,
            RecommendedFees = RecommendedFees,
            CrossChainConfigured = CrossChainConfigured,
            CrossChainFeeBps = CrossChainFeeBps,
            CrossChainOverpayBps = CrossChainOverpayBps,
            CrossChainMinimumSats = CrossChainMinimumSats,
            CrossChainRate = CrossChainRate,
            NullProviderQuoteIdAfterPrepares = NullProviderQuoteIdAfterPrepares,
            ConversionMinimumFromBitcoinSats = ConversionMinimumFromBitcoinSats,
            FailStableBalanceWith = FailStableBalanceWith,
            FailUserSettingsWith = FailUserSettingsWith
        };

        replacement.TokenBalances.AddRange(TokenBalances);
        replacement.UnclaimedDeposits.AddRange(UnclaimedDeposits);
        replacement.Payments.AddRange(Payments);
        foreach (var (key, value) in PaymentsById)
            replacement.PaymentsById[key] = value;

        return replacement;
    }

    /// <summary>Registers a payment so it is reachable both by id and through history queries.</summary>
    public FakeSparkSdkClient Seed(SparkPayment payment)
    {
        PaymentsById[payment.SdkPaymentId] = payment;
        Payments.Add(payment);
        return this;
    }

    private void ThrowIfConfigured()
    {
        if (FailWith is not null)
            throw FailWith;
    }

    public sealed record ReceiveCall(string Description, long? AmountSats, uint ExpirySecs);

    public sealed record SendCall(string Bolt11, long? AmountSats, string IdempotencyKey, TimeSpan? Timeout);

    public sealed record OnchainQuoteCall(string Address, long AmountSats, bool FeesIncluded);

    public sealed record OnchainSendCall(
        string Address,
        long AmountSats,
        SparkOnchainSpeed Speed,
        bool FeesIncluded,
        string IdempotencyKey);
}
