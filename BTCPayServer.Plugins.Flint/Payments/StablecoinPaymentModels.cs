using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.Flint.Payments;

/// <summary>
/// The store-level configuration of a USDC or USDT payment method. Empty on purpose.
/// </summary>
/// <remarks>
/// There is nothing to configure per store: the wallet is the store's Spark wallet, the networks are whatever the
/// provider serves, and the fee is the payer's. BTCPay still needs a configuration object to exist for a payment
/// method to be offered, so its presence <em>is</em> the setting — written by the Flint status page's toggle and
/// removed by it.
/// </remarks>
public class StablecoinPaymentMethodConfig
{
}

/// <summary>
/// What an invoice's USDC or USDT prompt carries: the networks it offers and the quote currently shown.
/// </summary>
/// <remarks>
/// The networks are fixed when the invoice is created, so the checkout page never waits on the provider to render
/// and every poll of it is a read of this blob. A quote replaces <see cref="Quote"/> each time a payer picks a
/// network; the earlier ones stay payable and live on in the plugin's own quote table, which is what settles them.
/// </remarks>
public class StablecoinPromptDetails
{
    public List<StablecoinNetworkOption> Networks { get; set; } = [];

    public StablecoinActiveQuote? Quote { get; set; }
}

/// <summary>One network a payer can send from.</summary>
public class StablecoinNetworkOption
{
    /// <summary>The provider's chain identifier, which is what the quote request names.</summary>
    public string Chain { get; set; } = null!;

    /// <summary>How the network reads at checkout.</summary>
    public string Name { get; set; } = null!;

    public string? ChainId { get; set; }

    /// <summary>The token contract or mint the payer must send, shown so they can check it is the right token.</summary>
    public string? ContractAddress { get; set; }
}

/// <summary>The quote a checkout is showing: where to send, how much, and until when.</summary>
public class StablecoinActiveQuote
{
    /// <summary>The plugin's own id for the quote.</summary>
    public string QuoteId { get; set; } = null!;

    public string Chain { get; set; } = null!;

    public string ChainName { get; set; } = null!;

    public string DepositAddress { get; set; } = null!;

    /// <summary>The exact amount to send, as a decimal string in the token's own units.</summary>
    public string Amount { get; set; } = null!;

    /// <summary>EIP-681 on an EVM chain, the bare address elsewhere — what the QR code encodes.</summary>
    public string PaymentRequest { get; set; } = null!;

    public string? ContractAddress { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>The invoice due this quote was made for; a later due change means a fresh quote.</summary>
    public decimal Due { get; set; }

    /// <summary>What this network adds to the due.</summary>
    public decimal Fee { get; set; }
}

/// <summary>What a credited USDC or USDT payment records on the BTCPay invoice.</summary>
public class StablecoinPaymentDetails
{
    /// <summary>The plugin's quote this payment settled.</summary>
    public string QuoteId { get; set; } = null!;

    public string Chain { get; set; } = null!;

    public string? ChainId { get; set; }

    public string Asset { get; set; } = null!;

    public string? ContractAddress { get; set; }

    /// <summary>The provider address on <see cref="Chain"/> the payer sent to.</summary>
    public string DepositAddress { get; set; } = null!;

    /// <summary>The payer's own transaction on <see cref="Chain"/>, when the provider reported it.</summary>
    public string? ExternalTxHash { get; set; }

    public string? ProviderOrderId { get; set; }

    /// <summary><c>BTC</c>, or the Stable Balance token the store holds, as the wallet received it.</summary>
    public string DestinationAsset { get; set; } = null!;

    /// <summary>What reached the Spark wallet, in <see cref="DestinationAsset"/>'s base units.</summary>
    public string? DeliveredAmount { get; set; }

    /// <summary>The inbound Spark payment, which is also this BTCPay payment's id.</summary>
    public string SdkPaymentId { get; set; } = null!;
}
