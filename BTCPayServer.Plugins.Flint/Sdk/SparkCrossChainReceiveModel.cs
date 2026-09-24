using System;
using System.Numerics;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>
/// The amount bounds a provider publishes for moving one route with one Spark-side asset.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every bound is optional, and an absent one means "not published", never zero.</b> A route can publish a
/// base-unit floor, a USD band, both or neither. Reading a missing maximum as zero would hide every network from
/// checkout; reading it as "anything goes" is what the SDK itself does, and the prepare call is the authority
/// either way — a route can enforce a tighter bound than it publishes.
/// </para>
/// <para>
/// On a receive, <see cref="MinAmount"/> and <see cref="MaxAmount"/> are in the <em>external</em> asset's base
/// units (what the payer sends), per the route's decimals. The USD band bounds the order's value and reads the
/// same in both directions.
/// </para>
/// </remarks>
public sealed record SparkCrossChainLimits(
    BigInteger? MinAmount,
    BigInteger? MaxAmount,
    ulong? MinUsdCents,
    ulong? MaxUsdCents)
{
    /// <summary>
    /// Whether a USD-par amount sits inside the published USD band. Base-unit bounds are left to the prepare call,
    /// which sees the deposit it actually sized.
    /// </summary>
    public bool AdmitsUsd(decimal usd)
    {
        if (usd <= 0m)
            return false;

        var cents = usd * 100m;
        if (MinUsdCents is { } min && cents < min)
            return false;
        if (MaxUsdCents is { } max && cents > max)
            return false;
        return true;
    }
}

/// <summary>
/// One place a payer can send USDC or USDT from, into this wallet: a chain, an asset on it, and how it lands.
/// </summary>
/// <remarks>
/// <para>
/// The receive-direction counterpart of <see cref="SparkCrossChainRoute"/>, and kept apart from it on purpose.
/// That record describes a <em>destination</em> a sweep delivers to, with what the send can be funded from; this
/// one describes a <em>source</em> a payer delivers from, with what the receipt can land as. The chain and asset
/// fields look the same and mean opposite ends of the transfer.
/// </para>
/// <para>
/// <b>Pass the instance the SDK gave you back to it.</b> Exactly as for sends: the SDK's receive takes a whole
/// <c>CrossChainRoutePair</c>, carried here opaquely in <see cref="Handle"/> and never rebuilt.
/// </para>
/// <para>
/// <see cref="Decimals"/> is read, never assumed: USDC and USDT are 6 decimals on most chains, 18 on BSC and 8 on
/// HyperCore, and a receive amount is in these base units.
/// </para>
/// </remarks>
/// <param name="BitcoinLimits">
/// The published bounds for landing this route as sats, or null when none are published. Only meaningful when
/// <see cref="LandsAsBitcoin"/>.
/// </param>
public sealed record SparkCrossChainReceiveRoute(
    SparkCrossChainProvider Provider,
    string Chain,
    string? ChainId,
    string Asset,
    string? ContractAddress,
    uint Decimals,
    bool LandsAsBitcoin,
    bool LandsAsToken,
    SparkCrossChainLimits? BitcoinLimits = null,
    object? Handle = null)
{
    /// <summary>How a route reads in a log line: <c>USDC on base</c>.</summary>
    public string Describe() => $"{Asset} on {Chain}";
}

/// <summary>
/// A live cross-chain receive quote: where the payer sends, how much, and what the wallet should end up with.
/// </summary>
/// <remarks>
/// <para>
/// <b>The SDK returns no quote id.</b> Its <c>CrossChainReceiveInfo</c> carries the deposit address and the
/// amounts but not the provider's quote identifier, and the payment that eventually arrives carries the quote id
/// but not the deposit address — so nothing the SDK hands back at quote time can be looked up directly at
/// completion. What survives both ends unchanged is the quote-time data the provider row freezes:
/// <see cref="ExpectedReceivedAmount"/> (the payment's <c>estimatedOut</c>) and <see cref="ServiceFeeAmount"/>
/// (its <c>serviceFeeAmount</c>), on this route. The stablecoin service keeps those two unique per route among
/// the quotes that can still complete, which is what makes a completed receive attributable to exactly one
/// invoice. See <c>StablecoinQuoteMatcher</c>.
/// </para>
/// </remarks>
/// <param name="DepositAmount">
/// What the payer must send, in the route's base units. Sized by the SDK to cover the provider's fee and an
/// overpay buffer on top of the amount asked for.
/// </param>
/// <param name="ExpectedReceivedAmount">
/// What the wallet should receive, net of fees, in the destination's base units: sats when
/// <see cref="DestinationAsset"/> is <c>BTC</c>, token base units otherwise.
/// </param>
/// <param name="ServiceFeeAmount">In <see cref="ServiceFeeAsset"/> units — not the route's and not the destination's.</param>
/// <param name="ExpiresAt">
/// The quote's expiry. The SDK does not gate on it: the provider reprices a late deposit at the live rate, and the
/// SDK keeps probing for one for 24 hours past this instant.
/// </param>
/// <param name="PaymentRequest">
/// The SDK's own payment URI: EIP-681 for an EVM route, the bare deposit address for Solana and Tron.
/// </param>
public sealed record SparkCrossChainReceiveQuote(
    SparkCrossChainReceiveRoute Route,
    string DepositAddress,
    BigInteger DepositAmount,
    BigInteger ExpectedReceivedAmount,
    string DestinationAsset,
    string? TokenIdentifier,
    BigInteger ServiceFeeAmount,
    string? ServiceFeeAsset,
    DateTimeOffset ExpiresAt,
    string PaymentRequest);
