using System;
using System.Numerics;
using Breez.Sdk.Spark;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>
/// Maps the SDK's <c>Payment</c> record onto <see cref="SparkPayment"/>.
/// </summary>
/// <remarks>
/// Pure and static so it can be unit-tested by constructing SDK record instances directly — no
/// native calls involved. Everything subtle about the SDK's payment shape lives here.
/// </remarks>
public static class SparkPaymentMapper
{
    /// <summary>
    /// Timestamps above this are interpreted as milliseconds rather than seconds. The SDK documents
    /// neither unit; the guard is here because misreading it by 1000× would put every payment in the
    /// year 56000 (or 1970) and silently break the reconciliation window.
    /// </summary>
    private const ulong MillisecondThreshold = 100_000_000_000UL;

    /// <param name="bolt11Parser">
    /// Used only as the fallback source of the payment hash: <c>PaymentDetails.Lightning</c> has no
    /// <c>paymentHash</c> property, the hash lives in the nullable <c>htlcDetails</c>, and when that is
    /// absent the invoice string is the only remaining source (spike notes §6).
    /// </param>
    public static SparkPayment Map(Payment payment, IBolt11Parser bolt11Parser)
    {
        ArgumentNullException.ThrowIfNull(payment);
        ArgumentNullException.ThrowIfNull(bolt11Parser);

        string? paymentHash = null;
        string? bolt11 = null;
        string? preimage = null;
        string? description = null;
        string? txId = null;
        ConversionInfo? conversionInfo = null;

        switch (payment.details)
        {
            case PaymentDetails.Deposit deposit:
                // An auto-claimed on-chain static deposit. No payment hash exists and none is expected;
                // the funding outpoint is the only identifier.
                txId = NullIfBlank(deposit.txId);
                description = $"on-chain deposit {deposit.txId}:{deposit.vout}";
                break;
            case PaymentDetails.Withdraw withdraw:
                // A cooperative exit. The L1 txid is present from the first Pending event onwards, which is
                // what lets a sweep record its txid without waiting for completion.
                txId = NullIfBlank(withdraw.txId);
                description = $"on-chain withdrawal {withdraw.txId}";
                break;
            case PaymentDetails.Lightning lightning:
                bolt11 = NullIfBlank(lightning.invoice);
                description = NullIfBlank(lightning.description);
                paymentHash = NullIfBlank(lightning.htlcDetails?.paymentHash);
                preimage = NullIfBlank(lightning.htlcDetails?.preimage);
                conversionInfo = lightning.conversionInfo;
                break;
            case PaymentDetails.Spark spark:
                // A BOLT11 receive can settle as a direct Spark transfer, which Breez's own docs warn
                // "cannot be linked back to the invoice". When it does, the HTLC details are the only thing
                // tying it back to one; there is no invoice string to fall back on.
                paymentHash = NullIfBlank(spark.htlcDetails?.paymentHash);
                preimage = NullIfBlank(spark.htlcDetails?.preimage);
                // Also where a cross-chain send funded from the sats balance shows up: the SDK models it as
                // an ordinary Spark transfer to the provider's deposit address, with the provider state nested
                // here. There is no cross-chain PaymentMethod and no cross-chain event.
                conversionInfo = spark.conversionInfo;
                break;
            case PaymentDetails.Token token:
                // A cross-chain send funded from a token balance — the Stable Balance path. Same shape, one
                // layer along, and the leg on which an idempotency key is rejected.
                description = NullIfBlank(token.metadata?.ticker);
                conversionInfo = token.conversionInfo;
                break;
        }

        if (paymentHash is null && bolt11 is not null)
            paymentHash = bolt11Parser.Parse(bolt11)?.PaymentHash;

        return new SparkPayment(
            SdkPaymentId: payment.id,
            Direction: payment.paymentType is PaymentType.Send
                ? SparkPaymentDirection.Send
                : SparkPaymentDirection.Receive,
            Status: payment.status switch
            {
                Breez.Sdk.Spark.PaymentStatus.Completed => SparkPaymentStatus.Completed,
                Breez.Sdk.Spark.PaymentStatus.Failed => SparkPaymentStatus.Failed,
                _ => SparkPaymentStatus.Pending
            },
            Method: payment.method switch
            {
                Breez.Sdk.Spark.PaymentMethod.Lightning => SparkPaymentMethod.Lightning,
                Breez.Sdk.Spark.PaymentMethod.Spark => SparkPaymentMethod.Spark,
                Breez.Sdk.Spark.PaymentMethod.Token => SparkPaymentMethod.Token,
                Breez.Sdk.Spark.PaymentMethod.Deposit => SparkPaymentMethod.Deposit,
                Breez.Sdk.Spark.PaymentMethod.Withdraw => SparkPaymentMethod.Withdraw,
                _ => SparkPaymentMethod.Unknown
            },
            AmountSats: ToSats(payment.amount),
            FeeSats: ToSats(payment.fees),
            Timestamp: ToTimestamp(payment.timestamp),
            PaymentHash: paymentHash?.ToLowerInvariant(),
            Bolt11: bolt11,
            Preimage: preimage?.ToLowerInvariant(),
            Description: description,
            TxId: txId?.ToLowerInvariant(),
            Conversion: MapConversion(conversionInfo, payment.conversionDetails));
    }

    /// <summary>
    /// Flattens the SDK's two parallel conversion representations into one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The per-leg <c>ConversionInfo</c> inside <c>PaymentDetails</c> is preferred, because it is the only one
    /// that carries the provider's <c>quoteId</c> and <c>deliveredAmount</c> — and the quote id is the token
    /// path's entire crash-recovery story, while the delivered amount is the authoritative settled figure.
    /// </para>
    /// <para>
    /// <c>Payment.conversionDetails</c> is the BTC↔token view and is read as a fallback so a payment carrying
    /// only that (an auto-conversion with no cross-chain leg) still reports a status rather than looking like a
    /// payment with no conversion at all.
    /// </para>
    /// </remarks>
    internal static SparkConversionState? MapConversion(
        ConversionInfo? info,
        ConversionDetails? details)
    {
        switch (info)
        {
            case ConversionInfo.Orchestra orchestra:
                // The last six fields are what a cross-chain *receive* is attributed by: estimatedOut and
                // serviceFeeAmount are frozen at quote time and identify the quote, assetAmountIn is what the
                // payer really sent, and the external hash is the payer's own transaction. A send ignores them.
                return new SparkConversionState(
                    SparkCrossChainProvider.Orchestra,
                    MapConversionStatus(orchestra.status),
                    orchestra.quoteId,
                    orchestra.orderId,
                    orchestra.deliveredAmount,
                    orchestra.recipientAddress,
                    orchestra.chain,
                    orchestra.asset,
                    orchestra.assetDecimals,
                    NullIfBlank(orchestra.chainId),
                    NullIfBlank(orchestra.assetContract),
                    orchestra.assetAmountIn,
                    orchestra.estimatedOut,
                    orchestra.serviceFeeAmount,
                    NullIfBlank(orchestra.externalTxHash));

            case ConversionInfo.Boltz boltz:
                // Recorded even though no Boltz route currently prepares, so a payment that somehow took one is
                // legible rather than reported as having no conversion. Boltz's own identifier is a swap id
                // rather than a quote id; it goes in the same slot because it plays the same role.
                return new SparkConversionState(
                    SparkCrossChainProvider.Boltz,
                    MapConversionStatus(boltz.status),
                    boltz.swapId,
                    boltz.bridgeRef,
                    boltz.deliveredAmount,
                    boltz.recipientAddress,
                    boltz.chain,
                    boltz.asset,
                    boltz.assetDecimals);

            case ConversionInfo.Amm amm:
                // The Flashnet BTC↔USDB leg. No destination and no delivered amount — it is a swap inside the
                // wallet, not a delivery — but its status is what a stuck Stable Balance conversion shows up as.
                return new SparkConversionState(
                    SparkCrossChainProvider.Unknown,
                    MapConversionStatus(amm.status),
                    ProviderOrderId: amm.conversionId);
        }

        if (details is null)
            return null;

        return new SparkConversionState(SparkCrossChainProvider.Unknown, MapConversionStatus(details.status));
    }

    private static SparkConversionStatus MapConversionStatus(ConversionStatus status) => status switch
    {
        ConversionStatus.Completed => SparkConversionStatus.Completed,
        ConversionStatus.Failed => SparkConversionStatus.Failed,
        ConversionStatus.RefundNeeded => SparkConversionStatus.RefundNeeded,
        ConversionStatus.Refunded => SparkConversionStatus.Refunded,
        ConversionStatus.Pending => SparkConversionStatus.Pending,
        _ => SparkConversionStatus.Unknown
    };

    /// <summary>
    /// Narrows a UniFFI <c>u128</c> amount to <see cref="long"/> satoshi.
    /// </summary>
    /// <remarks>
    /// Saturates rather than throwing. A payment amount that does not fit in a signed 64-bit satoshi
    /// value cannot exist (21e14 sats is the entire supply), so an out-of-range value means the SDK
    /// returned something nonsensical; clamping keeps a single bad row from taking down the event
    /// consumer loop, and the clamped value is still obviously wrong to anyone reading the logs.
    /// </remarks>
    internal static long ToSats(BigInteger value)
    {
        if (value <= BigInteger.Zero)
            return 0;
        return value > long.MaxValue ? long.MaxValue : (long)value;
    }

    internal static DateTimeOffset ToTimestamp(ulong timestamp)
    {
        if (timestamp == 0)
            return DateTimeOffset.UtcNow;
        return timestamp >= MillisecondThreshold
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Min(timestamp, long.MaxValue))
            : DateTimeOffset.FromUnixTimeSeconds((long)timestamp);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
