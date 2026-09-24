using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>Which bridge provider settles a cross-chain send.</summary>
/// <remarks>
/// <b>Only <see cref="Orchestra"/> works today.</b> Every one of the spike's six Boltz prepare attempts, across
/// three chains and three amounts, failed with <c>Boltz API: BTC/TBTC pair not found. Is referral header
/// configured?</c> — and <c>api.boltz.exchange</c> was reachable at the time, so it is not the IP block that
/// masked so much else. Boltz routes nevertheless keep appearing in <c>GetCrossChainRoutes</c>, which is why
/// <c>CrossChainRouteResolver</c> filters on this rather than trusting the route list.
/// <para>
/// Boltz also cannot send from a token balance at all (<c>Boltz does not support token sends in v1</c>), so it
/// would be unusable for a Stable-Balance store even if the pair lookup were fixed.
/// </para>
/// </remarks>
public enum SparkCrossChainProvider
{
    Orchestra,
    Boltz,
    Unknown
}

/// <summary>What a route can be funded from.</summary>
public enum SparkCrossChainSource
{
    /// <summary>The wallet's sats balance. Every provider supports this.</summary>
    Bitcoin,

    /// <summary>A token balance. Orchestra only.</summary>
    Token
}

/// <summary>
/// One destination the SDK can deliver to: a provider, a chain, and an asset on that chain.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pass the instance the SDK gave you back to it.</b> <c>PaymentRequest.CrossChain</c> takes a whole
/// <c>CrossChainRoutePair</c>, and a reconstructed one is not the same object the provider quoted against. The
/// plugin therefore carries the SDK's own value in <see cref="Handle"/> and never rebuilds it; every other
/// member here exists so the plugin can filter, display and validate without reaching into SDK types outside
/// <see cref="SparkSdkClient"/>.
/// </para>
/// <para>
/// <see cref="Decimals"/> is <c>byte</c> on the SDK's route (a <c>uint</c> everywhere else) and must be read
/// rather than assumed: it is 6 for USDT on Arbitrum, Optimism, Polygon, Plasma and Ethereum, and <b>18</b> on
/// BSC.
/// </para>
/// </remarks>
/// <param name="Handle">
/// The SDK's own <c>CrossChainRoutePair</c>, opaque above the client. Never null in production; the fake
/// supplies its own stand-in.
/// </param>
public sealed record SparkCrossChainRoute(
    SparkCrossChainProvider Provider,
    string Chain,
    string? ChainId,
    string Asset,
    string? ContractAddress,
    uint Decimals,
    IReadOnlyList<SparkCrossChainSource> SupportedSources,
    object? Handle = null)
{
    /// <summary>True when this route can be funded from the given token balance.</summary>
    public bool SupportsToken =>
        SupportedSources.Contains(SparkCrossChainSource.Token);

    public bool SupportsBitcoin =>
        SupportedSources.Contains(SparkCrossChainSource.Bitcoin);

    /// <summary>How a route reads on a settings page: <c>USDT on arbitrum (Orchestra)</c>.</summary>
    public string Describe() => $"{Asset} on {Chain} ({Provider})";
}

/// <summary>
/// A live cross-chain quote: what leaves, what arrives, and what it costs.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="AmountInSats"/> is larger than the amount that was asked for</b>, and that is the number a
/// balance check has to clear. The SDK overpays the source leg to absorb the provider fee and slippage — the
/// spike measured roughly <c>max(50 bps, ~50 sats)</c> — so a sweep that drains to exactly the requested amount
/// fails. The pad is not derivable from <c>targetOverpayBps</c> alone; it must be read from the quote.
/// </para>
/// <para>
/// <see cref="FeeAmount"/> and <see cref="ServiceFeeAmount"/> are in <b>destination-asset base units</b>, not
/// satoshi, and <see cref="ServiceFeeAsset"/> was observed as <c>USDC</c> on a <c>USDT</c> route — so it may not
/// match <see cref="SparkCrossChainRoute.Asset"/> and must not be assumed to.
/// </para>
/// <para>
/// There is <b>no arrival estimate anywhere on the SDK's quote</b>. The plan for this feature assumed one; it
/// does not exist, so no surface may show one.
/// </para>
/// </remarks>
/// <param name="ExpiresAt">
/// Roughly 60 seconds out. Carried as a real instant even though the SDK reports this one as an ISO-8601
/// <em>string</em> while the cooperative-exit quote reports its own as a Unix <c>ulong</c>.
/// </param>
/// <param name="ProviderQuoteId">
/// The provider's own quote id. <b>This is the token path's crash-safety primitive</b> — see
/// <c>SweepRecord.ProviderQuoteId</c>.
/// </param>
public sealed record SparkCrossChainQuote(
    SparkCrossChainRoute Route,
    string RecipientAddress,
    long AmountInSats,
    BigInteger AssetAmountIn,
    BigInteger EstimatedOut,
    BigInteger FeeAmount,
    BigInteger ServiceFeeAmount,
    string? ServiceFeeAsset,
    long SourceTransferFeeSats,
    DateTimeOffset ExpiresAt,
    string? ProviderQuoteId,
    string? ProviderDepositAddress)
{
    /// <summary>What arrives at the EVM address, as a decimal quantity in the destination asset.</summary>
    public string DescribeEstimatedOut() =>
        $"{SparkSendAmount.FormatBaseUnits(EstimatedOut, Route.Decimals)} {Route.Asset}";

    /// <summary>
    /// The all-in cost as a percentage of what is debited, computed on the <em>sats</em> side.
    /// </summary>
    /// <remarks>
    /// Deliberately not computed from <see cref="FeeAmount"/>: that figure is in destination-asset units and
    /// <see cref="ServiceFeeAmount"/> may be in a third asset again, so adding them is meaningless. The honest
    /// comparable number for a merchant is how much more left the wallet than the sweep asked for, which is the
    /// overpay pad — and the provider fee is inside it.
    /// </remarks>
    public double OverpayPercent(long requestedSats) =>
        requestedSats <= 0 ? 0d : (AmountInSats - requestedSats) * 100d / requestedSats;
}

/// <summary>How far a conversion or a cross-chain delivery has got.</summary>
/// <remarks>
/// There is <b>no SDK event for any transition here</b> — none of the nine <c>SdkEvent</c> variants concerns
/// conversions or cross-chain sends (spike §5). Every value below is discovered by polling.
/// </remarks>
public enum SparkConversionStatus
{
    Pending,
    Completed,
    Failed,

    /// <summary>
    /// Stuck and recoverable: the SDK holds funds it could not convert and needs
    /// <c>RefundPendingConversions</c> called. Surfaced to the merchant rather than retried silently.
    /// </summary>
    RefundNeeded,

    Refunded,
    Unknown
}

/// <summary>
/// The conversion or cross-chain leg attached to a payment, flattened from the SDK's two parallel shapes.
/// </summary>
/// <remarks>
/// The SDK carries this twice: <c>Payment.conversionDetails</c> is the BTC↔token view and
/// <c>PaymentDetails.*.conversionInfo</c> is the per-leg view. Both are read and merged here so no caller has to
/// know that, and so <see cref="ProviderQuoteId"/> is populated from whichever one has it.
/// </remarks>
/// <param name="ProviderQuoteId">
/// Orchestra's quote id, when this leg went through Orchestra. The value a token-leg sweep is reconciled by.
/// </param>
/// <param name="DeliveredAmount">
/// The authoritative settled amount at the destination, in destination-asset base units. Null until the
/// provider reports delivery — which, again, arrives through no event.
/// </param>
/// <param name="AssetAmountIn">
/// On a <b>receive</b>, what the payer actually deposited on <paramref name="Chain"/>, in the external asset's
/// base units (<paramref name="AssetDecimals"/>). On a send, the Spark amount expressed in the external asset.
/// </param>
/// <param name="EstimatedOut">
/// Frozen at quote time. On a receive this is in Spark-side units (sats, or token base units) and equals the
/// quote's <c>expectedReceivedAmount</c> — which is why it can identify the quote a receive came from.
/// </param>
/// <param name="ServiceFeeAmount">Frozen at quote time, in the service-fee asset's own units.</param>
/// <param name="ExternalTxHash">
/// The transaction on the non-Spark chain: the payer's funding deposit on a receive, the delivery on a send.
/// </param>
public sealed record SparkConversionState(
    SparkCrossChainProvider Provider,
    SparkConversionStatus Status,
    string? ProviderQuoteId = null,
    string? ProviderOrderId = null,
    BigInteger? DeliveredAmount = null,
    string? RecipientAddress = null,
    string? Chain = null,
    string? Asset = null,
    uint AssetDecimals = 0,
    string? ChainId = null,
    string? AssetContract = null,
    BigInteger? AssetAmountIn = null,
    BigInteger? EstimatedOut = null,
    BigInteger? ServiceFeeAmount = null,
    string? ExternalTxHash = null)
{
    /// <summary>True while the provider has neither delivered nor given up.</summary>
    public bool IsInFlight => Status is SparkConversionStatus.Pending or SparkConversionStatus.Unknown;

    /// <summary>True when an operator has to act: the SDK is holding funds it could not convert.</summary>
    public bool NeedsRefund => Status is SparkConversionStatus.RefundNeeded;

    public string? DescribeDelivered() =>
        DeliveredAmount is { } delivered
            ? $"{SparkSendAmount.FormatBaseUnits(delivered, AssetDecimals)} {Asset ?? "tokens"}"
            : null;
}

/// <summary>
/// Outcome of a cross-chain send. Exactly one of <see cref="Payment"/> and <see cref="RejectedReason"/> is set;
/// a rejection means the caller's guard vetoed the quote and <b>nothing was sent</b>.
/// </summary>
public sealed record SparkCrossChainSendResult(
    SparkPayment? Payment,
    SparkCrossChainQuote? Quote,
    string? RejectedReason);

/// <summary>
/// Raised when the SDK returns no cross-chain routes at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>An exception rather than an empty list, because an empty list is not an answer.</b> With
/// <c>Config.crossChainConfig</c> left null — which is what <c>DefaultConfig</c> does — the SDK's
/// <c>GetCrossChainRoutes</c> returns a zero-length array and <em>no error</em>. The spike verified the same
/// call going from 0 routes to 54 purely by setting <c>new CrossChainConfig(null, null)</c>. Reported as
/// "no routes available" it looks like a destination problem the merchant should fix by choosing another chain;
/// it is in fact a plugin configuration bug, and conflating the two costs whoever hits it the same hours it cost
/// the spike.
/// </para>
/// <para>
/// The plugin sets <c>crossChainConfig</c> on every mainnet connect, so reaching this means something has gone
/// wrong upstream of the merchant.
/// </para>
/// </remarks>
public sealed class SparkCrossChainNotConfiguredException : InvalidOperationException
{
    public SparkCrossChainNotConfiguredException(string address)
        : base(
            $"Spark returned no cross-chain routes at all for {address}. That is not 'no route to this chain' — "
            + "with the SDK's cross-chain configuration unset it returns an empty list and no error, so this is a "
            + "plugin or network configuration fault rather than something to fix by choosing another chain. "
            + "Cross-chain sends are also mainnet-only.")
    {
        Address = address;
    }

    public string Address { get; }
}
