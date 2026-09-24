using System;
using System.Globalization;
using System.Numerics;

namespace BTCPayServer.Plugins.Flint.Data;

/// <summary>
/// One USDC/USDT deposit address this plugin showed a payer: the quote behind it, the invoice it is for, and —
/// once the money arrives — the Spark payment that settled it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the plugin keeps its own copy of a quote the SDK already persists.</b> The SDK's cross-chain receive
/// returns no quote id, and the payment that eventually arrives carries no deposit address, so nothing the SDK
/// offers at either end can be looked up at the other. This row is the join: it records, at quote time, the
/// values the provider freezes onto the completed payment — the route, <see cref="ExpectedReceivedBaseUnits"/>
/// and <see cref="ServiceFeeBaseUnits"/> — plus the exact amount the payer was asked for, which the plugin keeps
/// unique per route among the quotes that can still complete. <c>StablecoinQuoteMatcher</c> holds the rules.
/// </para>
/// <para>
/// Amounts in base units are strings, like <see cref="SweepRecord"/>'s: an 18-decimal route (USDC and USDT on
/// BSC) exceeds a 64-bit integer at a few dollars, and the values are only ever compared for equality and parsed
/// back through <see cref="BigInteger"/>.
/// </para>
/// </remarks>
public class StablecoinQuote
{
    /// <summary>The plugin's own id for the quote. A GUID string; the SDK's quote id is never available here.</summary>
    public string Id { get; set; } = null!;

    public string StoreId { get; set; } = null!;

    /// <summary>The BTCPay invoice the quote was made for.</summary>
    public string InvoiceId { get; set; } = null!;

    /// <summary><c>USDC-FLINT</c> or <c>USDT-FLINT</c>: the prompt the credit lands on.</summary>
    public string PaymentMethodId { get; set; } = null!;

    /// <summary>The source chain, as the provider spells it (<c>base</c>, <c>solana</c>, <c>tron</c>).</summary>
    public string Chain { get; set; } = null!;

    public string? ChainId { get; set; }

    /// <summary>The source asset symbol, <c>USDC</c> or <c>USDT</c>.</summary>
    public string Asset { get; set; } = null!;

    /// <summary>The token contract or mint on <see cref="Chain"/> the payer must send.</summary>
    public string? ContractAddress { get; set; }

    /// <summary>The route's decimals: 6 on most chains, 18 on BSC, 8 on HyperCore.</summary>
    public int Decimals { get; set; }

    /// <summary>The provider-controlled address on <see cref="Chain"/> the payer sends to.</summary>
    public string DepositAddress { get; set; } = null!;

    /// <summary>What the SDK sized the deposit at, in route base units.</summary>
    public string DepositBaseUnits { get; set; } = null!;

    /// <summary>
    /// What the payer was asked to send, in route base units: the deposit rounded up to the payment method's six
    /// decimals, plus the few millionths that keep it unique on this route. Never below
    /// <see cref="DepositBaseUnits"/>.
    /// </summary>
    public string AskedBaseUnits { get; set; } = null!;

    /// <summary>What the payer's wallet is given: an EIP-681 URI on an EVM chain, the bare address elsewhere.</summary>
    public string PaymentRequest { get; set; } = null!;

    /// <summary>The invoice's net due when the quote was made, in the prompt currency (USDC or USDT).</summary>
    public decimal DueAmount { get; set; }

    /// <summary>
    /// What paying through this route costs on top of <see cref="DueAmount"/>, in the prompt currency: the asked
    /// amount less the due. Recorded as the credited payment's fee, so a payer who sends exactly the asked amount
    /// settles exactly the due.
    /// </summary>
    public decimal FeeAmount { get; set; }

    /// <summary>
    /// The quote's delivery estimate, in the landing asset's base units (sats, or USDB base units). Frozen by the
    /// provider row and reported back as the payment's <c>estimatedOut</c>: half of the quote's fingerprint.
    /// </summary>
    public string ExpectedReceivedBaseUnits { get; set; } = null!;

    /// <summary><c>BTC</c> or the landing token's symbol (<c>USDB</c>).</summary>
    public string DestinationAsset { get; set; } = null!;

    /// <summary>The provider's quoted fee, in its own asset's units: the other half of the fingerprint.</summary>
    public string ServiceFeeBaseUnits { get; set; } = null!;

    public string? ServiceFeeAsset { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The quote's own expiry. Not the end of its life: the provider reprices a late deposit, and the SDK keeps
    /// probing for one for a day past this — see <see cref="StablecoinQuoteStoreExtensions.MatchWindow"/>.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>The inbound Spark payment this quote was matched to. Unique: one payment settles one quote.</summary>
    public string? SdkPaymentId { get; set; }

    /// <summary>What the payer actually deposited, in route base units, as the provider reported it.</summary>
    public string? PaidBaseUnits { get; set; }

    /// <summary>What reached the wallet, in the landing asset's base units.</summary>
    public string? DeliveredBaseUnits { get; set; }

    /// <summary>The payer's own transaction on <see cref="Chain"/>.</summary>
    public string? ExternalTxHash { get; set; }

    public string? ProviderOrderId { get; set; }

    public string? ProviderQuoteId { get; set; }

    /// <summary>When the quote was matched to <see cref="SdkPaymentId"/>.</summary>
    public DateTimeOffset? SettledAt { get; set; }

    /// <summary>When the payment was recorded on the BTCPay invoice. Retried until set.</summary>
    public DateTimeOffset? CreditedAt { get; set; }

    /// <summary>The asked amount as a number.</summary>
    public BigInteger Asked => ParseBaseUnits(AskedBaseUnits);

    public BigInteger ExpectedReceived => ParseBaseUnits(ExpectedReceivedBaseUnits);

    public BigInteger ServiceFee => ParseBaseUnits(ServiceFeeBaseUnits);

    /// <summary>A base-unit string back to a number. A value nothing could have written reads as zero.</summary>
    internal static BigInteger ParseBaseUnits(string? value) =>
        BigInteger.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : BigInteger.Zero;

    internal static string FormatBaseUnits(BigInteger value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>What the provider reported when a quote's money arrived.</summary>
public sealed record StablecoinSettlement(
    string SdkPaymentId,
    BigInteger? PaidBaseUnits,
    BigInteger? DeliveredBaseUnits,
    string? ExternalTxHash,
    string? ProviderOrderId,
    string? ProviderQuoteId,
    DateTimeOffset SettledAt);
