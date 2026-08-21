using System;
using System.Globalization;
using Breez.Sdk.Spark;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>
/// Turns SDK exceptions into text fit for a merchant, and classifies the ones we act on.
/// </summary>
/// <remarks>
/// <para>
/// The SDK's exceptions are UniFFI-generated: the payload sits in a public field named <c>v1</c> and
/// <c>Message</c> is synthesised as <c>"@v1=Tree service error: insufficient funds"</c>, prefix and
/// all. Never surface <c>ex.Message</c> directly (spike notes §12).
/// </para>
/// <para>
/// Note also that not every failure is an <c>SdkException</c>: the C# binding layer itself throws
/// <c>ArgumentNullException</c> for a null description, so callers must catch <c>Exception</c> at the
/// <c>ILightningClient</c> boundary rather than <c>SdkException</c>.
/// </para>
/// </remarks>
public static class SparkErrors
{
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            SdkException.InsufficientFunds => "Insufficient Spark balance.",
            SdkException.SparkException spark => Strip(spark.v1),
            SdkException.InvalidInput invalid => Strip(invalid.v1),
            SdkException.NetworkException network => $"Spark network error: {Strip(network.v1)}",
            SdkException.StorageException storage => $"Spark storage error: {Strip(storage.v1)}",
            SdkException.ChainServiceException chain => $"Bitcoin chain service error: {Strip(chain.v1)}",
            SdkException.LnurlException lnurl => $"LNURL error: {Strip(lnurl.v1)}",
            SdkException.Signer signer => $"Spark signer error: {Strip(signer.v1)}",
            SdkException.InvalidUuid uuid => $"Invalid identifier: {Strip(uuid.v1)}",
            SdkException.Generic generic => Strip(generic.v1),
            SdkException.InsufficientCpfpFunds shortfall => DescribeCpfpShortfall(ToSats(shortfall.requiredSat)),
            SdkException.FundingUtxoConflict conflict => DescribeUtxoConflict(conflict.txid, conflict.vout),
            // MissingUtxo and MaxDepositClaimFeeExceeded carry several named fields rather than a
            // single v1, so there is nothing better to do than strip the synthesised prefix.
            SdkException => Strip(exception.Message),
            ObjectDisposedException => "The Spark wallet for this store is no longer running.",
            _ => Strip(exception.Message)
        };
    }

    /// <summary>
    /// True when the failure means "not enough sats".
    /// </summary>
    /// <remarks>
    /// The typed <c>SdkException.InsufficientFunds</c> variant exists but was never observed being
    /// thrown: an unfunded send surfaces as <c>SparkException: @v1=Tree service error: insufficient
    /// funds</c>. Both are matched, the second one by substring, because there is no alternative.
    /// </remarks>
    public static bool IsInsufficientFunds(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is SdkException.InsufficientFunds ||
               (exception is SdkException.SparkException spark &&
                spark.v1?.Contains("insufficient funds", StringComparison.OrdinalIgnoreCase) is true);
    }

    /// <summary>
    /// True when the SDK rejected the request locally, before doing anything.
    /// </summary>
    /// <remarks>
    /// These are client-side validations that cost 0 ms and definitively mean nothing was sent — an amount
    /// below the 294-sat dust floor for the destination's script type, a malformed address, an unsupported
    /// payment method. Distinguishing them from a network failure is what lets a caller say "safe to retry
    /// differently" instead of "state unknown".
    /// </remarks>
    public static bool IsInvalidInput(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is SdkException.InvalidInput;
    }

    /// <summary>
    /// True when a cooperative-exit fee quote has expired and the send must be re-prepared.
    /// </summary>
    /// <remarks>
    /// A prepared bitcoin-address response is valid for only ~60 seconds, so this is a normal condition
    /// rather than a failure to report: the caller re-prepares and tries again. It arrives as prose inside
    /// a <c>SparkException</c> ("The coop exit fee quote has expired, please request a new quote"), so
    /// there is no typed variant to match on. Used by the sweep path.
    /// </remarks>
    public static bool IsExpiredFeeQuote(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is SdkException.SparkException spark &&
               spark.v1?.Contains("fee quote has expired", StringComparison.OrdinalIgnoreCase) is true;
    }

    /// <summary>
    /// True when a bridge provider refused an amount as below its own minimum.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A normal outcome, not a fault.</b> The cross-chain minimum is enforced server-side by the provider and
    /// the SDK exposes no getter for it — the spike had to binary-search it, finding a floor somewhere between
    /// 1,000 and 1,500 satoshi — so "too small" is only discoverable by attempting a prepare, and arrives as a
    /// <c>NetworkException</c> carrying the provider's own prose (<c>Amount too small (code: 400)</c>).
    /// </para>
    /// <para>
    /// Matched by substring because there is no typed variant and no error code on the C# side. A caller that
    /// treated this as a network failure would report the provider as unreachable to a merchant whose only
    /// problem is a small balance.
    /// </para>
    /// </remarks>
    public static bool IsAmountTooSmall(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is SdkException.NetworkException network &&
               network.v1?.Contains("amount too small", StringComparison.OrdinalIgnoreCase) is true;
    }

    /// <summary>
    /// True when the SDK reported "no such row", which is how it reports "not found".
    /// </summary>
    /// <remarks>
    /// <c>GetPayment</c> on an unknown id throws <c>StorageException: @v1=Underlying implementation
    /// error: Query returned no rows</c> rather than returning null (spike notes §6).
    /// </remarks>
    public static bool IsNotFound(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is SdkException.StorageException storage &&
               storage.v1?.Contains("no rows", StringComparison.OrdinalIgnoreCase) is true;
    }

    /// <summary>
    /// Turns the two unilateral-exit-specific SDK errors into typed plugin exceptions, or returns null when the
    /// failure is something else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These two are lifted out of the generic error path because a caller has to <em>act</em> differently on
    /// them, and the action needs the numbers. <c>InsufficientCpfpFunds</c> names the amount that would have
    /// worked, which is exactly the figure to put in front of an operator who has to top up a funding address;
    /// <c>FundingUtxoConflict</c> names the output that is already committed elsewhere, which is what
    /// distinguishes "your funding UTXO was spent" from "the exit is impossible". Neither reads as anything
    /// useful through <see cref="Describe"/> alone, and neither can be matched on without touching SDK types —
    /// which above this seam nothing may do.
    /// </para>
    /// <para>
    /// Returns null rather than the original exception so a call site can use it as an exception filter and let
    /// everything else escape unchanged, with its original stack.
    /// </para>
    /// </remarks>
    public static Exception? TranslateUnilateralExit(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            SdkException.InsufficientCpfpFunds shortfall =>
                new SparkExitFundingShortfallException(ToSats(shortfall.requiredSat), shortfall),
            SdkException.FundingUtxoConflict conflict =>
                new SparkExitFundingUtxoConflictException(conflict.txid, conflict.vout, conflict),
            _ => null
        };
    }

    internal static string DescribeCpfpShortfall(long requiredSat) => string.Format(
        CultureInfo.InvariantCulture,
        "There is not enough confirmed Bitcoin on the exit funding address to pay the exit's on-chain fees. "
        + "Spark needs at least {0:N0} sat available there, as a single confirmed output.",
        requiredSat);

    internal static string DescribeUtxoConflict(string? txid, uint vout) => string.Format(
        CultureInfo.InvariantCulture,
        "The funding output {0}:{1} is already spent or committed to another transaction, so it cannot pay for "
        + "this exit. Send fresh funds to the funding address and try again once they confirm.",
        string.IsNullOrWhiteSpace(txid) ? "(unknown)" : txid,
        vout);

    /// <remarks>
    /// Every amount on the exit surface is a <c>u64</c> of satoshi — no tokens, no base units, no
    /// <c>BigInteger</c> — so the only conversion hazard is the width, and it is clamped rather than wrapped:
    /// an absurd value must not come out the other side as a negative fee.
    /// </remarks>
    private static long ToSats(ulong value) => (long)Math.Min(value, long.MaxValue);

    private static string Strip(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return "Unknown Spark error.";
        return message.StartsWith("@v1=", StringComparison.Ordinal) ? message[4..] : message;
    }
}

/// <summary>
/// Raised when a unilateral exit could not be built because its funding outputs do not cover the fees.
/// </summary>
/// <remarks>
/// <para>
/// <b>Recoverable, and the fix is a number.</b> A unilateral exit pays every one of its own on-chain fees from a
/// separate confirmed UTXO the operator supplies, because the coins being recovered are locked behind timelocks
/// and cannot pay for their own release. Under-funding it therefore fails the build rather than producing a
/// cheaper exit — and the SDK says what would have been enough, which is carried here so an operator is told
/// how much to add instead of being told to guess.
/// </para>
/// <para>
/// Nothing was built, signed or broadcast, so retrying after topping the address up is safe.
/// </para>
/// </remarks>
public sealed class SparkExitFundingShortfallException : InvalidOperationException
{
    public SparkExitFundingShortfallException(long requiredSat, Exception? innerException = null)
        : base(SparkErrors.DescribeCpfpShortfall(requiredSat), innerException)
    {
        RequiredSat = requiredSat;
    }

    /// <summary>What the SDK said the exit needs, in satoshi, as a single confirmed output.</summary>
    public long RequiredSat { get; }
}

/// <summary>
/// Raised when a funding output offered to a unilateral exit is already spent or otherwise committed.
/// </summary>
/// <remarks>
/// <para>
/// Almost always means the discovery step raced the chain: the output was unspent when the plugin listed the
/// funding address and is not by the time the SDK builds against it. It can also mean the same funding UTXO is
/// being used by a second exit attempt, which is why the outpoint is carried rather than folded into prose —
/// an operator comparing it against a previous attempt's record is how that gets diagnosed.
/// </para>
/// <para>
/// Nothing was built, signed or broadcast. Re-discovering the funding outputs and trying again is safe.
/// </para>
/// </remarks>
public sealed class SparkExitFundingUtxoConflictException : InvalidOperationException
{
    public SparkExitFundingUtxoConflictException(string? txid, uint vout, Exception? innerException = null)
        : base(SparkErrors.DescribeUtxoConflict(txid, vout), innerException)
    {
        Txid = txid;
        Vout = vout;
    }

    public string? Txid { get; }

    public uint Vout { get; }

    /// <summary>The conflicting output as <c>txid:vout</c>, for comparison against a persisted record.</summary>
    public string OutPoint =>
        $"{Txid ?? "(unknown)"}:{Vout.ToString(CultureInfo.InvariantCulture)}";
}
