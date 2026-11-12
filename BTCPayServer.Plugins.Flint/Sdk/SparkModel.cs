using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>
/// Plugin-side view of a Spark payment, mapped from the SDK's <c>Payment</c> record.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not the SDK type. The SDK's <c>Payment</c> uses <c>BigInteger</c> amounts (UniFFI
/// <c>u128</c>), lower-camel-case fields, and hides the payment hash inside
/// <c>details.Lightning.htlcDetails</c> — which is nullable. Normalising all of that once, in
/// <see cref="SparkPaymentMapper"/>, keeps every consumer honest about units and nullability.
/// </para>
/// <para>
/// Amounts are in <b>satoshi</b>, matching the SDK. BTCPay works in millisatoshi; convert at the
/// boundary and remember the SDK cannot express sub-satoshi amounts for a receive.
/// </para>
/// </remarks>
/// <param name="SdkPaymentId">
/// The SDK's opaque payment id (an SSP transfer id). The only key <c>GetPayment</c> accepts. For
/// outgoing payments this value changes from provisional to final, so never use it as a durable key
/// for a send.
/// </param>
/// <param name="PaymentHash">
/// Lower-case hex payment hash, or null when neither the HTLC details nor the BOLT11 yielded one.
/// </param>
/// <param name="AmountSats">
/// What moved to the counterparty, with <paramref name="FeeSats"/> reported separately alongside it, so a send
/// debits <c>AmountSats + FeeSats</c> in total.
/// <para>
/// <b>Already net of the fee under <c>FeePolicy.FeesIncluded</c></b> — the SDK does that netting itself on the
/// payment, and a 62,000 sat drain quoted at 1,710 sat comes back as amount 60,290, fees 1,710. Do not subtract
/// the fee again; a sweep message that did understated a real mainnet sweep by exactly one fee. This is the
/// opposite of <see cref="SparkOnchainQuote.AmountSats"/> on the <em>quote</em>, which is not adjusted for the
/// fee policy at all and needs <see cref="SparkOnchainQuote.RecipientAmountSats"/>.
/// </para>
/// </param>
/// <param name="TxId">
/// Bitcoin transaction id, for the two on-chain payment shapes only: the cooperative exit a sweep produces
/// (<c>PaymentDetails.Withdraw</c>) and an inbound static-deposit claim (<c>PaymentDetails.Deposit</c>). Null
/// for everything else.
/// <para>
/// Present from the <em>first</em> <c>Pending</c> event on a coop exit, so a sweep's txid can be recorded and
/// shown without waiting for completion. There is no confirmation count anywhere in the SDK's shape; showing
/// one would need the plugin's own chain lookup.
/// </para>
/// </param>
/// <param name="Conversion">
/// The conversion or cross-chain leg riding on this payment, when there is one. Null for every ordinary
/// Lightning, Spark or cooperative-exit payment.
/// <para>
/// This is how a cross-chain send is observed at all. <c>PaymentMethod</c> has no cross-chain member — such a
/// send appears as <c>Spark</c> (from a sats balance) or <c>Token</c> (from a token balance) with the provider
/// state nested inside — and <b>no SDK event reports a conversion or a delivery</b>, so the state here changes
/// only between polls. See <see cref="SparkConversionState"/>.
/// </para>
/// </param>
public sealed record SparkPayment(
    string SdkPaymentId,
    SparkPaymentDirection Direction,
    SparkPaymentStatus Status,
    SparkPaymentMethod Method,
    long AmountSats,
    long FeeSats,
    DateTimeOffset Timestamp,
    string? PaymentHash,
    string? Bolt11,
    string? Preimage,
    string? Description,
    string? TxId = null,
    SparkConversionState? Conversion = null)
{
    /// <summary>Received/sent amount in millisatoshi.</summary>
    public long AmountMsat => AmountSats * 1000L;
}

/// <summary>
/// Direction of a payment.
/// </summary>
/// <remarks>
/// This is not cosmetic. One payment hash produces <b>two</b> <c>Payment</c> rows when a wallet both
/// sends and receives the same invoice — a <c>Receive</c> leg and a <c>Send</c> leg, with different ids,
/// the same hash and the same invoice, but different fees (0 on the receive, 3 on the send in the funded
/// run). Any hash-based reconciliation must filter on direction or it can credit the wrong leg.
/// </remarks>
public enum SparkPaymentDirection
{
    Receive,
    Send
}

/// <summary>
/// Rail a payment travelled on.
/// </summary>
/// <remarks>
/// Needed to tell an unattributable Lightning receive (a real problem worth warning about) from an
/// on-chain static-deposit claim or a cooperative exit, neither of which has a payment hash and both of
/// which are entirely normal.
/// </remarks>
public enum SparkPaymentMethod
{
    Lightning,
    Spark,
    Token,

    /// <summary>An inbound on-chain static deposit, auto-claimed by the SDK.</summary>
    Deposit,

    /// <summary>An outbound cooperative exit — what auto-sweep produces.</summary>
    Withdraw,
    Unknown
}

public enum SparkPaymentStatus
{
    Pending,
    Completed,
    Failed
}

/// <summary>Result of minting a receive request (BOLT11 invoice) on the SSP.</summary>
public sealed record SparkReceiveResult(string PaymentRequest, long FeeSats, string PaymentHash);

/// <summary>
/// Wallet-level information, from the SDK's <c>GetInfo</c>.
/// </summary>
/// <remarks>
/// <b><see cref="BalanceSats"/> is for display only.</b> In the funded run it still read 0 for ~20 s after
/// the settlement event had fired — including through <c>GetInfo(ensureSynced: true)</c>, which took
/// 3.8 s and returned the stale value — and it drifted by 3–8 sats around the SDK's background leaf
/// optimisation, once moving <em>up</em> between sessions. Never derive a settlement, an amount, or any
/// accounting figure from a balance or a balance delta; reconcile from <see cref="SparkPayment"/> rows.
/// A caller that must act on the balance (the sweep task) has to <c>SyncWallet</c> first.
/// </remarks>
/// <param name="TokenBalances">
/// Token balances the wallet holds, keyed by nothing — the identifier is on each entry. Empty on a wallet with
/// no tokens, which is every wallet until Stable Balance is activated. <b>These are base units, not
/// satoshi</b>; see <see cref="SparkTokenBalance"/>.
/// </param>
public sealed record SparkNodeInfo(
    string IdentityPubkey,
    long BalanceSats,
    IReadOnlyList<SparkTokenBalance>? TokenBalances = null)
{
    /// <summary>Token balances, never null.</summary>
    public IReadOnlyList<SparkTokenBalance> Tokens => TokenBalances ?? [];

    /// <summary>The balance of one token, or null when the wallet holds none of it.</summary>
    public SparkTokenBalance? TokenBalance(SparkTokenIdentifier identifier)
    {
        foreach (var balance in Tokens)
        {
            if (balance.Identifier == identifier)
                return balance;
        }

        return null;
    }
}

/// <summary>
/// Bounded query over the SDK's payment history.
/// </summary>
/// <remarks>
/// <see cref="From"/> and <see cref="Limit"/> are not optional in spirit: the reconciliation task calls this
/// per pending invoice on every pass, and an unbounded scan would become O(all history). Anchor
/// <see cref="From"/> to the invoice's creation time and page with <see cref="Offset"/>.
/// </remarks>
/// <param name="Ascending">
/// Oldest first. Defaults to false, which is what a reconciler wants — it is looking for something that just
/// happened. A scan anchored to a record's creation time wants the opposite: its target is the oldest row in
/// the window, so paging newest-first walks away from it.
/// </param>
/// <param name="Method">
/// Only payments of this kind: <see cref="SparkPaymentMethod.Spark"/> (plain Spark transfers — no Lightning, no
/// deposits) or <see cref="SparkPaymentMethod.Token"/> (token payments). Null for every kind. These are the two the
/// SDK can filter by in storage, and the two a cross-chain receive arrives as: sats, or the Stable Balance token.
/// </param>
public sealed record SparkListPaymentsQuery(
    SparkPaymentDirection? Direction = null,
    bool CompletedOnly = false,
    DateTimeOffset? From = null,
    int Offset = 0,
    int Limit = 50,
    bool Ascending = false,
    SparkPaymentMethod? Method = null);

/// <summary>
/// What the SDK quoted for a send, before it is executed. Surfaced so the caller can enforce its own
/// maximum-fee policy before any money moves.
/// </summary>
public sealed record SparkSendQuote(long AmountSats, long FeeSats, string? PaymentHash);

/// <summary>
/// Outcome of a send. Exactly one of <see cref="Payment"/> and <see cref="RejectedReason"/> is set:
/// a rejection means the caller's fee policy vetoed the quote and <b>nothing was sent</b>.
/// </summary>
public sealed record SparkSendResult(SparkPayment? Payment, SparkSendQuote? Quote, string? RejectedReason);

/// <summary>
/// Confirmation-speed tier for a cooperative exit, mapped onto the SDK's <c>OnchainConfirmationSpeed</c>.
/// </summary>
public enum SparkOnchainSpeed
{
    Slow,
    Medium,
    Fast
}

/// <summary>
/// The SDK's cooperative-exit fee quote: one fee per confirmation-speed tier, with an expiry.
/// </summary>
/// <remarks>
/// <para>
/// The fee for a tier is <c>userFeeSat + l1BroadcastFeeSat</c>, and the executed payment's <c>fees</c> equalled
/// that sum exactly in the funded run. Only the sum is exposed here; splitting it out would invite a caller to
/// display or compare half of it.
/// </para>
/// <para>
/// <b>These fees do not depend on the amount.</b> 294 sats and 99,901 sats produced identical quotes. That is
/// the whole reason <see cref="SweepSettings.MinimumSweepSats"/> exists.
/// </para>
/// <para>
/// <see cref="ExpiresAt"/> is roughly 60 seconds out, and the service provider enforces it: sending against an
/// expired prepare fails with "The coop exit fee quote has expired". A quote is therefore safe to <em>show</em>
/// but never to <em>hold</em> across a user interaction — re-quote and re-check the guard on confirm.
/// </para>
/// </remarks>
public sealed record SparkOnchainFeeQuote(
    string QuoteId,
    DateTimeOffset ExpiresAt,
    long SlowFeeSats,
    long MediumFeeSats,
    long FastFeeSats)
{
    public long FeeFor(SparkOnchainSpeed speed) => speed switch
    {
        SparkOnchainSpeed.Slow => SlowFeeSats,
        SparkOnchainSpeed.Fast => FastFeeSats,
        _ => MediumFeeSats
    };
}

/// <summary>
/// What a cooperative exit will cost at one chosen tier, before it is executed.
/// </summary>
/// <param name="AmountSats">The amount asked of the SDK — <em>not</em> necessarily what the recipient gets.</param>
/// <param name="FeeSats">Total fee for the chosen tier.</param>
/// <param name="FeesIncluded">
/// True when the fee is netted out of <paramref name="AmountSats"/> (the SDK's <c>FeePolicy.FeesIncluded</c>).
/// </param>
/// <remarks>
/// The two derived amounts are computed here rather than read off the SDK because
/// <c>PrepareSendPaymentResponse.amount</c> is <b>not</b> adjusted for <c>FeesIncluded</c> — it echoes back what
/// was requested. Only the <c>Payment</c> returned by the send shows the netted figure, which is far too late to
/// tell a merchant what their destination will receive.
/// </remarks>
public sealed record SparkOnchainQuote(
    long AmountSats,
    long FeeSats,
    bool FeesIncluded,
    SparkOnchainFeeQuote Tiers)
{
    /// <summary>What the destination address actually receives.</summary>
    public long RecipientAmountSats => FeesIncluded ? Math.Max(0, AmountSats - FeeSats) : AmountSats;

    /// <summary>What leaves the Spark balance in total.</summary>
    public long TotalDebitedSats => FeesIncluded ? AmountSats : AmountSats + FeeSats;

    /// <summary>The fee as a percentage of what the destination receives, for the honesty warning in the UI.</summary>
    public double FeePercentOfRecipientAmount =>
        RecipientAmountSats <= 0 ? 100d : FeeSats * 100d / RecipientAmountSats;
}

/// <summary>
/// Outcome of a cooperative exit. Exactly one of <see cref="Payment"/> and <see cref="RejectedReason"/> is set;
/// a rejection means the caller's fee policy vetoed the quote and <b>nothing was sent</b>.
/// </summary>
public sealed record SparkOnchainSendResult(
    SparkPayment? Payment,
    SparkOnchainQuote? Quote,
    string? RejectedReason);
