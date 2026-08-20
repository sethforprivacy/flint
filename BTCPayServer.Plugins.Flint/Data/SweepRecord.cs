using System;
using System.Globalization;
using System.Numerics;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;

namespace BTCPayServer.Plugins.Flint.Data;

/// <summary>
/// Durable record of one auto-sweep attempt from a store's Spark balance to an on-chain address.
/// </summary>
/// <remarks>
/// <para>
/// The row is written <em>before</em> the SDK send is issued, with the idempotency key already
/// populated. That ordering is the whole point of the table: the SDK adopts the key as its own
/// <c>Payment.id</c>, so after a crash mid-send <c>GetPayment(key)</c> answers definitively whether the
/// exit happened, and a retry with the same key cannot double-spend. The key-becomes-<c>Payment.id</c>
/// behaviour is not documented by the SDK; it was verified against coop exits on a funded regtest run.
/// </para>
/// <para>
/// Every sweep this records is a <b>cooperative exit</b>. A unilateral exit is never recorded here — the
/// experimental unilateral-exit flow keeps its own records (<c>UnilateralExitRecord</c>).
/// </para>
/// <para>
/// The row is also the merchant's explanation of a sweep that did <em>not</em> happen: a refusal writes a
/// <see cref="SweepRecordStatus.Refused"/> row with its reason, because a merchant whose sweeps have silently
/// stopped needs to be able to see why on the history page rather than in the server log.
/// </para>
/// </remarks>
public class SweepRecord
{
    /// <summary>
    /// UUID used as the SDK's idempotency key and as this row's primary key.
    /// </summary>
    public string IdempotencyKey { get; set; } = null!;

    /// <summary>Store this sweep belongs to. Indexed.</summary>
    public string StoreId { get; set; } = null!;

    /// <summary>
    /// Destination address. A fresh, labelled address reserved from the store's BTC derivation
    /// scheme and rotated per sweep, or the merchant's configured static address.
    /// </summary>
    /// <remarks>
    /// Empty for a refusal that never got as far as resolving one — a store in
    /// <see cref="SweepDestinationMode.StoreWallet"/> mode with no on-chain wallet, most obviously.
    /// </remarks>
    public string DestinationAddress { get; set; } = string.Empty;

    /// <summary>Which destination rule produced <see cref="DestinationAddress"/>.</summary>
    public SweepDestinationMode DestinationMode { get; set; }

    /// <summary>
    /// Amount asked of the SDK, in satoshi. Not necessarily what the destination receives — see
    /// <see cref="FeesIncluded"/> and <see cref="RecipientAmountSats"/>.
    /// </summary>
    public long AmountSats { get; set; }

    /// <summary>
    /// True when the fee was netted out of <see cref="AmountSats"/> (the SDK's <c>FeePolicy.FeesIncluded</c>).
    /// </summary>
    public bool FeesIncluded { get; set; }

    /// <summary>Fee tier requested.</summary>
    public SweepConfirmationSpeed ConfirmationSpeed { get; set; }

    /// <summary>
    /// Fee the quote promised at the moment the row was written, in satoshi.
    /// </summary>
    /// <remarks>
    /// Kept alongside <see cref="FeeSats"/> rather than overwritten by it, so a divergence between what was
    /// quoted and what was charged is visible after the fact instead of being smoothed over. They agreed
    /// exactly on every observed exit, which is a claim worth being able to check on real funds.
    /// </remarks>
    public long QuotedFeeSats { get; set; }

    /// <summary>Fee actually paid, in satoshi; null until the SDK reports the payment.</summary>
    public long? FeeSats { get; set; }

    /// <summary>Spark balance, in satoshi, that this sweep was decided from.</summary>
    /// <remarks>
    /// Read after an explicit <c>SyncWallet</c>, which is the only way to make it current. Recorded because it
    /// is the input to every guard on the row, so a surprising sweep can be explained without re-deriving it.
    /// </remarks>
    public long BalanceAtDecisionSats { get; set; }

    /// <summary>On-chain txid of the cooperative exit; null until the SDK reports one.</summary>
    /// <remarks>
    /// Available from the first <c>Pending</c> event, so it is usually populated as soon as the send returns.
    /// </remarks>
    public string? TxId { get; set; }

    /// <summary>Whether a merchant asked for this sweep or the periodic task decided on it.</summary>
    public SweepTrigger Trigger { get; set; }

    public SweepRecordStatus Status { get; set; } = SweepRecordStatus.Pending;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Why this sweep was refused or failed, in words fit for a merchant. Never contains secrets.
    /// </summary>
    /// <remarks>
    /// <b>Not an identity.</b> The text embeds live figures — the amount, the fee, the percentage — so two
    /// refusals for the same underlying reason almost never have the same <see cref="Error"/>. Use
    /// <see cref="RefusalCode"/> to decide whether two refusals are the same refusal.
    /// </remarks>
    public string? Error { get; set; }

    /// <summary>
    /// The stable identity of a refusal, for recognising a repeat of one.
    /// </summary>
    /// <remarks>
    /// This column exists because the first version of the de-duplication compared the rendered
    /// <see cref="Error"/> sentence, and that sentence carries the balance — which drifts by a few sats around the
    /// SDK's background leaf optimisation. Consecutive refusals for one unchanged cause therefore never matched,
    /// the de-duplication never fired, and a store parked on a refusal accumulated a row every couple of minutes
    /// forever with no cleanup path. That is not an edge case: with mainnet broadcast fees an order of magnitude
    /// above the regtest levels these defaults were calibrated against, a default-configured store sits
    /// permanently on <see cref="SweepRefusalCode.FeeAboveLimit"/>.
    /// </remarks>
    public SweepRefusalCode RefusalCode { get; set; } = SweepRefusalCode.None;

    /// <summary>
    /// When this refusal was last reached, for a row that stands for an ongoing condition rather than one moment.
    /// Null while it has only happened once.
    /// </summary>
    /// <remarks>
    /// Without it a de-duplicated row is indistinguishable from a one-off: <see cref="CreatedAt"/> is never
    /// touched, so a merchant looking at the history cannot tell a refusal from three days ago that has since
    /// resolved itself from one that happened thirty seconds ago and is still happening.
    /// </remarks>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>
    /// How many times this row's outcome has been reached. One for a sweep; more for a recurring refusal.
    /// </summary>
    public int AttemptCount { get; set; } = 1;

    /// <summary>When this row's outcome was last observed — <see cref="LastSeenAt"/> if it recurred.</summary>
    public DateTimeOffset LastActivityAt => LastSeenAt ?? CompletedAt ?? CreatedAt;

    /// <summary>
    /// What the destination receives, in satoshi.
    /// </summary>
    /// <remarks>
    /// Derived rather than stored so it cannot disagree with the amount and fee it is computed from. Uses the
    /// actual fee once known and the quoted one before that.
    /// </remarks>
    public long RecipientAmountSats =>
        FeesIncluded ? Math.Max(0, AmountSats - (FeeSats ?? QuotedFeeSats)) : AmountSats;

    /// <summary>
    /// The fee as a percentage of what the destination receives — the number that makes flat exit fees honest.
    /// </summary>
    public double FeePercent
    {
        get
        {
            var recipient = RecipientAmountSats;
            return recipient <= 0 ? 0d : (FeeSats ?? QuotedFeeSats) * 100d / recipient;
        }
    }

    #region Cross-chain

    /// <summary>
    /// Which rail this sweep used. <see cref="SweepDestinationKind.BitcoinAddress"/> for every row written
    /// before Wave 7, which is also the enum's zero value.
    /// </summary>
    /// <remarks>
    /// Stored rather than derived from <see cref="DestinationMode"/> because the reconciliation strategy turns
    /// on it: a Bitcoin-rail sweep is resolved by <c>GetPayment(idempotencyKey)</c>, and a cross-chain one may
    /// have no idempotency key at all.
    /// </remarks>
    public SweepDestinationKind DestinationKind { get; set; } = SweepDestinationKind.BitcoinAddress;

    /// <summary>Destination chain for a cross-chain sweep, e.g. <c>arbitrum</c>.</summary>
    public string? DestinationChain { get; set; }

    /// <summary>Destination asset for a cross-chain sweep, e.g. <c>USDT</c>.</summary>
    public string? DestinationAsset { get; set; }

    /// <summary>Decimals of <see cref="DestinationAsset"/>, read from the route rather than assumed.</summary>
    /// <remarks>
    /// Six on every Orchestra USDT route except BSC, which is eighteen. Stored per row so a historical sweep
    /// still renders correctly if a route's decimals ever change under it.
    /// </remarks>
    public int DestinationAssetDecimals { get; set; }

    /// <summary>
    /// Which bridge provider carried this sweep. Null for a cooperative exit.
    /// </summary>
    /// <remarks>
    /// Recorded because provider availability is not stable: every Boltz route currently fails at prepare, so
    /// "which provider actually carried it" is a question a merchant and an operator will both ask.
    /// </remarks>
    public SparkCrossChainProvider? Provider { get; set; }

    /// <summary>
    /// The provider's own quote id, persisted <b>before</b> the send.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what replaces the idempotency key as the crash-safety primitive on the token path.</b>
    /// The ordinary primitive rests on the SDK adopting our UUID as <c>Payment.id</c>, so
    /// <c>GetPayment(key)</c> answers "did it send?" definitively. That holds only while the send's first leg is
    /// a sats transfer. Turn Stable Balance on and the first leg becomes a token transfer, on which the SDK
    /// <em>rejects</em> an idempotency key outright — so there is no key, and no id to look up.
    /// </para>
    /// <para>
    /// What is left is the provider's quote id, which the SDK hands back at prepare and then carries on the
    /// resulting payment's conversion info. Writing it here before the send means a crashed token sweep is
    /// resolved by <em>scanning</em> payments for this value rather than by re-sending — and a token sweep is
    /// never re-sent, because nothing would deduplicate it.
    /// </para>
    /// <para>
    /// Also populated on the Bitcoin-source cross-chain path, where it is a second, independent way to identify
    /// the same send. On that path the idempotency key still works.
    /// </para>
    /// </remarks>
    public string? ProviderQuoteId { get; set; }

    /// <summary>The provider's order id, when it reports one. Diagnostic; not used for matching.</summary>
    public string? ProviderOrderId { get; set; }

    /// <summary>
    /// Whether the send carried an SDK idempotency key, and therefore whether
    /// <see cref="IdempotencyKey"/> is a payment id the SDK will answer to.
    /// </summary>
    /// <remarks>
    /// True for every cooperative exit and for a cross-chain sweep funded from the sats balance. False for one
    /// funded from a token balance, where <see cref="IdempotencyKey"/> is only this row's primary key and
    /// looking a payment up by it would return nothing — which, read as evidence, would say the sweep never
    /// happened.
    /// </remarks>
    public bool IdempotencyKeyAccepted { get; set; } = true;

    /// <summary>
    /// The token this sweep was funded from, when it was not funded from the sats balance.
    /// </summary>
    /// <remarks>
    /// <b>When this is set, <see cref="AmountSats"/> is not the amount</b> — <see cref="SourceAmountBaseUnits"/>
    /// is, in this token's base units. The two are never both meaningful, which is the same distinction
    /// <c>SparkSendAmount</c> enforces in the code that produced them.
    /// </remarks>
    public string? SourceTokenIdentifier { get; set; }

    /// <summary>
    /// The amount sent, in <see cref="SourceTokenIdentifier"/>'s base units. Null for a sats-funded sweep.
    /// </summary>
    /// <remarks>
    /// A string because the SDK's amounts are <c>u128</c> and an 18-decimal token overflows every fixed-width
    /// column for ordinary sums. Parsed back through <see cref="System.Numerics.BigInteger"/>.
    /// </remarks>
    public string? SourceAmountBaseUnits { get; set; }

    /// <summary>Decimals of <see cref="SourceTokenIdentifier"/>.</summary>
    public int SourceTokenDecimals { get; set; }

    /// <summary>What the quote said would arrive, in destination-asset base units.</summary>
    public string? EstimatedOutBaseUnits { get; set; }

    /// <summary>
    /// What actually arrived, in destination-asset base units. Null until the provider reports delivery.
    /// </summary>
    /// <remarks>
    /// The authoritative settled figure, and it arrives through <b>no event whatsoever</b> — nothing in the
    /// SDK's nine event variants concerns a conversion or a delivery. It appears only when something polls.
    /// </remarks>
    public string? DeliveredAmountBaseUnits { get; set; }

    /// <summary>
    /// How far the provider has got. Null for a cooperative exit, which has no conversion.
    /// </summary>
    /// <remarks>
    /// <see cref="SparkConversionStatus.RefundNeeded"/> is the one that needs a human: the SDK is holding funds
    /// it could not convert, and it will keep holding them until <c>RefundPendingConversions</c> is called.
    /// </remarks>
    public SparkConversionStatus? ConversionStatus { get; set; }

    /// <summary>True when this row is a cross-chain sweep rather than a cooperative exit.</summary>
    public bool IsCrossChain => DestinationKind is SweepDestinationKind.EvmAddress;

    /// <summary>
    /// The amount sent, rendered with its own unit — satoshi or a token quantity.
    /// </summary>
    /// <remarks>
    /// The single place a surface should read an amount off this row, so no view has to decide which of the two
    /// fields means anything.
    /// </remarks>
    public string DescribeAmount() =>
        SourceTokenIdentifier is not null && SourceAmountBaseUnits is { } baseUnits
            ? SparkSendAmount.FormatBaseUnits(ParseBaseUnits(baseUnits), (uint)Math.Max(0, SourceTokenDecimals))
            : AmountSats.ToString("N0", CultureInfo.InvariantCulture) + " sat";

    /// <summary>What arrived, or what is expected to, with its asset. Null for a cooperative exit.</summary>
    public string? DescribeDelivered()
    {
        var amount = DeliveredAmountBaseUnits ?? EstimatedOutBaseUnits;
        if (amount is null || DestinationAsset is null)
            return null;

        var quantity = SparkSendAmount.FormatBaseUnits(
            ParseBaseUnits(amount), (uint)Math.Max(0, DestinationAssetDecimals));
        return DeliveredAmountBaseUnits is null
            ? $"about {quantity} {DestinationAsset}"
            : $"{quantity} {DestinationAsset}";
    }

    /// <summary>
    /// A stored base-units string as a number. Zero for anything unparseable.
    /// </summary>
    /// <remarks>
    /// Tolerant on purpose: this is display arithmetic over a nullable text column, and a row that cannot be
    /// rendered must not throw out of a history page.
    /// </remarks>
    internal static BigInteger ParseBaseUnits(string? value) =>
        BigInteger.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : BigInteger.Zero;

    #endregion

    /// <summary>True while this row is still the authority on an exit whose outcome is unknown.</summary>
    public bool IsInFlight => Status is SweepRecordStatus.Pending;
}

/// <summary>
/// The stable identity of a reason the plugin declined to sweep.
/// </summary>
/// <remarks>
/// <b>Stable</b> is the whole point. Every merchant-facing refusal sentence embeds live figures — the balance,
/// the sweepable amount, the fee, the percentage, or the SDK's own wording — so comparing sentences cannot tell
/// "the same problem, again" from "a different problem". These codes can, and they are what the recurring-refusal
/// de-duplication keys on. New members may be appended; existing values must never be renumbered, because they are
/// persisted.
/// </remarks>
public enum SweepRefusalCode
{
    /// <summary>Not a refusal.</summary>
    None = 0,

    /// <summary>The store has no running Spark wallet.</summary>
    WalletNotRunning = 1,

    /// <summary>The Spark balance could not be read.</summary>
    BalanceUnreadable = 2,

    /// <summary>The balance is not above the configured reserve.</summary>
    NothingAboveReserve = 3,

    /// <summary>Less is sweepable than the store's economic floor allows.</summary>
    BelowMinimumSweep = 4,

    /// <summary>No usable destination — most often store-wallet mode with no on-chain wallet.</summary>
    NoDestination = 5,

    /// <summary>Spark could not quote the exit, for a reason other than funds.</summary>
    QuoteFailed = 6,

    /// <summary>Spark reported insufficient funds.</summary>
    InsufficientFunds = 7,

    /// <summary>What the destination would receive is below the on-chain dust floor.</summary>
    BelowDustFloor = 8,

    /// <summary>The fee is charged on top and the reserve cannot cover it.</summary>
    ReserveBelowFee = 9,

    /// <summary>The quoted fee exceeds the store's fee ceiling.</summary>
    FeeAboveLimit = 10,

    /// <summary>
    /// Cross-chain sending is not available at all — the SDK returned no routes whatsoever, or the route
    /// lookup failed. A plugin or network fault, not a destination the merchant should change.
    /// </summary>
    CrossChainUnavailable = 11,

    /// <summary>
    /// No usable route to the configured chain and asset. Distinct from
    /// <see cref="CrossChainUnavailable"/>: routing works, and this particular destination has nothing
    /// carrying it that can also be funded the way this store's sweeps are funded.
    /// </summary>
    NoCrossChainRoute = 12,

    /// <summary>
    /// The cross-chain quote debits more than is sweepable. The quote overpays the source leg to absorb the
    /// provider fee and slippage, so the amount asked for is not the amount that has to be available.
    /// </summary>
    CrossChainOverpayExceedsBalance = 13,

    /// <summary>
    /// Stable Balance is holding the store's funds, so there is nothing on the rail this destination uses.
    /// </summary>
    /// <remarks>
    /// The interaction that would otherwise look like a broken sweep: with Stable Balance active the sats
    /// balance is converted away, so a cooperative exit finds nothing to send and refuses on the economic
    /// floor without ever mentioning the stablecoin sitting next to it.
    /// </remarks>
    StableBalanceHoldsTheFunds = 14,

    /// <summary>
    /// The cross-chain quote could not be read as a quote — nothing would arrive, or it claims to deliver at
    /// least as much as it takes in. Refused rather than interpreted, because computing a fee from it would
    /// produce a negative number that passes every ceiling.
    /// </summary>
    CrossChainQuoteUnusable = 15,

    /// <summary>
    /// What the destination would receive is not worth what would leave the wallet, or its value could not be
    /// established at all.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="FeeAboveLimit"/> because it is a different question. That one bounds the spread
    /// the provider states; this one bounds the <em>rate</em> it applies, which nothing inside the quote can
    /// check — a quote offering $100 of USDT for $320 of satoshi states a 0.34% spread.
    /// </remarks>
    CrossChainValueUnverifiable = 16
}

/// <summary>What set a sweep in motion.</summary>
public enum SweepTrigger
{
    /// <summary>The periodic task's threshold comparison.</summary>
    Automatic,

    /// <summary>A merchant pressing "sweep now". Runs the identical engine path.</summary>
    Manual
}

public enum SweepRecordStatus
{
    /// <summary>
    /// Row written, outcome not yet known. The send may or may not have reached the service provider; the next
    /// pass resolves it with <c>GetPayment(idempotencyKey)</c> rather than by retrying blind.
    /// </summary>
    Pending,

    /// <summary>The service provider accepted the exit. An on-chain txid is usually known.</summary>
    Sent,

    /// <summary>The SDK reports the cooperative exit as completed.</summary>
    Confirmed,

    /// <summary>The attempt failed after it was committed to; see <see cref="SweepRecord.Error"/>.</summary>
    Failed,

    /// <summary>
    /// The plugin declined to send, and <b>nothing was sent</b> — an economic floor, a fee guard, a missing
    /// destination. <see cref="SweepRecord.Error"/> says which.
    /// </summary>
    Refused
}
