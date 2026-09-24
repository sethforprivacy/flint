using System;
using System.Globalization;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>
/// A cap on what the SDK may spend claiming an on-chain static deposit.
/// </summary>
/// <remarks>
/// <para>
/// <b>A cap, not a bid.</b> When the fee actually required exceeds it the claim does not happen at a lower
/// price — it does not happen at all, and the deposit sits unclaimed indefinitely, surfacing only through
/// <c>ListUnclaimedDeposits</c> and the <c>UnclaimedDeposits</c> event (spike §4.4).
/// </para>
/// <para>
/// <b><c>null</c> is not "use a sensible default" — it disables automatic claiming entirely.</b> That is why
/// this type is used non-nullably wherever the plugin configures the SDK, and why
/// <c>SparkDepositSettings.ToMaxFee</c> can never produce a null.
/// </para>
/// </remarks>
public abstract record SparkMaxFee
{
    private SparkMaxFee()
    {
    }

    /// <summary>An absolute ceiling in satoshi.</summary>
    public sealed record Fixed(long Sats) : SparkMaxFee
    {
        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "at most {0:N0} sat", Sats);
    }

    /// <summary>
    /// A ceiling expressed as a fee rate.
    /// </summary>
    /// <remarks>
    /// The SDK's default is <c>Rate(1)</c>, which is <b>below the mainnet floor essentially always</b> — even at
    /// the unusually cheap 3 sat/vB measured during the spike. The plugin never configures this variant for
    /// automatic claiming; it exists because the SDK reports required rates in the same unit.
    /// </remarks>
    public sealed record Rate(long SatPerVbyte) : SparkMaxFee
    {
        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "at most {0:N0} sat/vB", SatPerVbyte);
    }

    /// <summary>
    /// A ceiling that tracks the mempool: the network-recommended rate plus a fixed leeway.
    /// </summary>
    /// <remarks>
    /// The only variant that moves with real fee conditions, and therefore the only defensible one for
    /// unattended claiming. See <c>SparkDepositSettings</c> for the leeway the plugin defaults to and why.
    /// </remarks>
    public sealed record NetworkRecommended(long LeewaySatPerVbyte) : SparkMaxFee
    {
        public override string ToString() => string.Format(
            CultureInfo.InvariantCulture,
            "the network-recommended rate plus {0:N0} sat/vB", LeewaySatPerVbyte);
    }
}

/// <summary>Why the SDK could not claim a matured deposit.</summary>
public enum SparkDepositClaimFailureKind
{
    /// <summary>
    /// The required fee exceeded the configured cap. The one that matters: it is recoverable, and the SDK tells
    /// us exactly what it would have cost.
    /// </summary>
    MaxFeeExceeded,

    /// <summary>The UTXO the deposit refers to is gone — most plausibly a reorg or an RBF replacement.</summary>
    MissingUtxo,

    /// <summary>Anything else the SDK reported, verbatim.</summary>
    Other
}

/// <param name="RequiredFeeSats">
/// What the claim would actually have cost. Present only for <see cref="SparkDepositClaimFailureKind.MaxFeeExceeded"/>,
/// and the number a one-click manual claim uses as its ceiling.
/// </param>
public sealed record SparkDepositClaimFailure(
    SparkDepositClaimFailureKind Kind,
    string Message,
    long? RequiredFeeSats = null,
    long? RequiredFeeRateSatPerVbyte = null);

/// <summary>
/// One on-chain deposit the SDK knows about but has not credited to the balance.
/// </summary>
/// <remarks>
/// <c>ListUnclaimedDeposits</c> returns <b>both</b> not-yet-mature deposits and matured-but-failed ones
/// (spike §4.3). They need completely different treatment and are told apart by <see cref="IsMature"/>: an
/// immature deposit is simply waiting for its third confirmation and needs nobody, while a matured one with a
/// <see cref="ClaimError"/> is money that will never arrive unless an operator acts.
/// </remarks>
public sealed record SparkDepositInfo(
    string TxId,
    uint Vout,
    long AmountSats,
    bool IsMature,
    SparkDepositClaimFailure? ClaimError = null,
    string? RefundTxId = null)
{
    /// <summary>
    /// True when this deposit is stuck: matured, so the SDK has already tried, and it did not work.
    /// </summary>
    /// <remarks>
    /// The condition that must never be invisible. A merchant who sent money to the deposit address and sees
    /// nothing in the balance has no other way to find out what happened.
    /// </remarks>
    public bool NeedsAttention => IsMature && ClaimError is not null;

    /// <summary>A stable key for one deposit, for a form post and for de-duplication.</summary>
    public string OutPoint => $"{TxId}:{Vout.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>
/// Mempool fee rates, in sat/vB, as the SDK reports them.
/// </summary>
/// <remarks>
/// Surfaced so a merchant looking at a stuck deposit can see the conditions that stranded it, and so the
/// configured claim policy can be shown against a real number instead of asserted. Read from a public mempool
/// API rather than from a Spark operator, which is why it was reachable during the spike when almost nothing
/// else was.
/// </remarks>
public sealed record SparkRecommendedFees(
    long FastestFeeSatPerVbyte,
    long HalfHourFeeSatPerVbyte,
    long HourFeeSatPerVbyte,
    long EconomyFeeSatPerVbyte,
    long MinimumFeeSatPerVbyte);

/// <summary>
/// Outcome of a manual claim. At most one of <see cref="Payment"/> and <see cref="Error"/> is set, and exactly
/// one of <see cref="Payment"/>, <see cref="Error"/> and <see cref="Submitted"/> says what happened.
/// </summary>
/// <param name="Submitted">
/// The SDK accepted the claim but settles it asynchronously, so there is no payment to hand back yet — its
/// <c>ClaimDepositOutcome.Submitted</c>. A success: the credit arrives as an ordinary deposit payment on the event
/// stream, and nothing more is needed from anybody.
/// </param>
public sealed record SparkClaimDepositResult(SparkPayment? Payment, string? Error, bool Submitted = false)
{
    public bool Succeeded => Payment is not null || Submitted;
}
