using System;
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>One stablecoin a store can accept at checkout, and the BTCPay payment method it is offered as.</summary>
/// <param name="Symbol">
/// The currency code the prompt is denominated in and the asset matched against a route, exactly: <c>USDT0</c>
/// is a different token from <c>USDT</c>, and a prefix match would silently take it.
/// </param>
public sealed record StablecoinAsset(string Symbol, string Name, PaymentMethodId PaymentMethodId);

/// <summary>
/// Accepting USDC and USDT at checkout, landing in the store's Spark wallet as bitcoin — the policy constants and
/// the two payment methods.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two payment methods, many networks.</b> A payer picks USDC or USDT the way they pick Lightning or on-chain,
/// and then picks the network they hold it on. One payment method per asset keeps checkout to two extra buttons
/// where one per (asset, chain) would be two dozen, and a prompt has one currency, so USDC and USDT cannot share
/// one. The network is chosen inside the payment method, and each choice mints its own quote.
/// </para>
/// <para>
/// <b>The payer pays for the route.</b> A quote is asked for the invoice's due and the SDK sizes the deposit above
/// it (<c>FeesExcluded</c>); the difference is shown as the payment method's network cost and recorded as the
/// credited payment's fee. That is BTCPay's own model for an on-chain network fee, so an invoice paid exactly
/// settles exactly, and the merchant receives the invoice's value.
/// </para>
/// </remarks>
public static class StablecoinPayments
{
    /// <summary>The payment type half of both payment method ids: <c>USDC-FLINT</c>, <c>USDT-FLINT</c>.</summary>
    /// <remarks>
    /// Named for the plugin rather than for a chain or a rail, because the method spans every network the
    /// provider serves and because another plugin accepting the same coin (a Tron-only USDT plugin, say) must not
    /// collide with it.
    /// </remarks>
    public const string PaymentType = "FLINT";

    public static readonly StablecoinAsset Usdc = new("USDC", "USD Coin", new PaymentMethodId($"USDC-{PaymentType}"));

    public static readonly StablecoinAsset Usdt = new("USDT", "Tether", new PaymentMethodId($"USDT-{PaymentType}"));

    public static IReadOnlyList<StablecoinAsset> Assets { get; } = [Usdc, Usdt];

    /// <summary>The asset a payment method id names, or null for anyone else's.</summary>
    public static StablecoinAsset? For(PaymentMethodId? paymentMethodId) =>
        paymentMethodId is null ? null : Assets.FirstOrDefault(asset => asset.PaymentMethodId == paymentMethodId);

    /// <inheritdoc cref="For(PaymentMethodId?)" />
    public static StablecoinAsset? For(string? paymentMethodId) =>
        PaymentMethodId.TryParse(paymentMethodId, out var parsed) ? For(parsed) : null;

    /// <summary>
    /// Decimals of both prompts. USDC and USDT are six-decimal on almost every chain; the 18-decimal BSC tokens and
    /// the 8-decimal HyperCore one are asked for at six, rounded up, which costs a payer under a millionth.
    /// </summary>
    public const int Divisibility = 6;

    /// <summary>
    /// How far a delivery may fall short of its quote, in basis points.
    /// </summary>
    /// <remarks>
    /// 1%. Tighter than the SDK's receive default would be pointless and looser would be the merchant's loss: the
    /// invoice is credited with what the payer <em>sent</em>, so any shortfall in what reaches the wallet is the
    /// merchant's. At 50 bps — the sweep default — small invoices fail at quote time, because a cent of provider
    /// rounding is already 20 bps of a five-dollar sale; Breez's own receive tests widen to 200 bps for exactly
    /// that reason on sats destinations.
    /// </remarks>
    public const uint MaxSlippageBps = 100;

    /// <summary>
    /// How long after a quote's expiry a receive can still arrive for it, and so how long the quote stays a
    /// matching candidate.
    /// </summary>
    /// <remarks>
    /// The SDK keeps probing an unfunded quote for 24 hours past its expiry (the provider reprices a late deposit
    /// at the live rate), and a funded one can take a while longer to deliver. 48 hours covers both with room; a
    /// quote past it is one the SDK has stopped watching.
    /// </remarks>
    public static readonly TimeSpan MatchWindow = TimeSpan.FromHours(48);

    /// <summary>
    /// How long past the provider's own expiry a quote's address stays on the checkout, and is reused for the same
    /// network and due.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The provider's expiry is the life of its <em>price</em> — about two minutes, measured on mainnet — not of its
    /// address. The SDK calls it "not authoritative for the receive lifecycle": the provider reprices a deposit that
    /// arrives later, and the SDK keeps watching an unpaid quote for a day past it. Treating those two minutes as
    /// the address's life told payers to fetch a new address before most wallets could have sent to the first, and
    /// minted a provider row each time they did.
    /// </para>
    /// <para>
    /// A reprice changes nothing for the payer, who sends the amount the quote asked for, and nothing for the
    /// invoice, which is credited with what was sent; it moves how much bitcoin reaches the wallet, the same
    /// exposure any crypto invoice carries between its rate and its payment. An hour keeps an address on screen
    /// while a payer funds a wallet or waits on an exchange withdrawal, and ends 23 hours before the SDK stops
    /// watching it.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan OfferedPastExpiry = TimeSpan.FromHours(1);

    /// <summary>Until when a quote the provider says expires at <paramref name="expiresAt"/> is offered to payers.</summary>
    public static DateTimeOffset OfferedUntil(DateTimeOffset expiresAt) => expiresAt + OfferedPastExpiry;

    /// <summary>Quotes one invoice may request across every network, so a checkout page cannot mint without bound.</summary>
    /// <remarks>
    /// Every quote is a provider row the SDK polls for a day, whether or not it is paid. Ten covers a payer
    /// trying several networks and coming back after a quote expired; past that the page says to use what it
    /// already has.
    /// </remarks>
    public const int MaxQuotesPerInvoice = 10;

    /// <summary>Unsettled quotes one store may hold inside <see cref="MatchWindow"/> before new ones are refused.</summary>
    /// <remarks>
    /// The server-wide bound on the polling above, and on the checkout endpoint being used to load somebody
    /// else's provider through this server. Far above what a real store's customers produce in two days.
    /// </remarks>
    public const int MaxOpenQuotesPerStore = 500;

    /// <summary>How long a store's list of receive networks is reused before it is read from the provider again.</summary>
    public static readonly TimeSpan RouteCacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>How long invoice creation waits for that list when it is not cached, before offering no stablecoin.</summary>
    public static readonly TimeSpan RouteFetchDeadline = TimeSpan.FromSeconds(8);

    /// <summary>How long the checkout's quote request waits on the SDK.</summary>
    public static readonly TimeSpan QuoteDeadline = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How the provider's chain identifiers read. Anything missing is shown capitalised; only the networks in
    /// <see cref="NetworkIcons"/> are offered at checkout, but a payment recorded on another one still needs a name.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ChainNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["arbitrum"] = "Arbitrum",
            ["arc"] = "Arc",
            ["avalanche"] = "Avalanche",
            ["base"] = "Base",
            ["berachain"] = "Berachain",
            ["bsc"] = "BNB Chain",
            ["ethereum"] = "Ethereum",
            ["hypercore"] = "HyperCore",
            ["hyperevm"] = "HyperEVM",
            ["ink"] = "Ink",
            ["linea"] = "Linea",
            ["mantle"] = "Mantle",
            ["monad"] = "Monad",
            ["optimism"] = "Optimism",
            ["plasma"] = "Plasma",
            ["polygon"] = "Polygon",
            ["sei"] = "Sei",
            ["solana"] = "Solana",
            ["sonic"] = "Sonic",
            ["tempo"] = "Tempo",
            ["ton"] = "TON",
            ["tron"] = "Tron",
            ["unichain"] = "Unichain"
        };

    /// <summary>
    /// The order networks are offered in: the ones most payers hold stablecoins on first, then the rest
    /// alphabetically.
    /// </summary>
    private static readonly string[] PreferredChainOrder =
        ["tron", "ethereum", "solana", "base", "arbitrum", "polygon", "bsc", "optimism", "avalanche"];

    /// <summary>
    /// The networks the plugin ships an icon for, by the provider's chain identifier, and the file under
    /// <c>Resources/img/networks/</c>.
    /// </summary>
    /// <remarks>
    /// <b>Only these networks are offered.</b> The icon is the payer's visual check that the network they are
    /// sending on is the one this QR code and address are for — the mistake that loses a payment outright — so a
    /// network the plugin cannot show is a network it does not offer. The provider serves more; adding one is its
    /// icon from the network's own brand assets, a line here, and a NOTICE entry for wherever the artwork came from.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> NetworkIcons =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["arbitrum"] = "arbitrum.svg",
            ["avalanche"] = "avalanche.svg",
            ["base"] = "base.svg",
            ["bsc"] = "bsc.svg",
            ["ethereum"] = "ethereum.svg",
            ["polygon"] = "polygon.svg",
            ["solana"] = "solana.svg",
            ["tron"] = "tron.svg"
        };

    /// <summary>
    /// The web path of a network's icon, relative to the application root, or null when the plugin has none — which
    /// also means the network is not offered.
    /// </summary>
    /// <remarks>
    /// BTCPay serves a plugin's embedded <c>Resources/**</c> from its web root, under the same path.
    /// </remarks>
    public static string? NetworkIcon(string? chain) =>
        chain is not null && NetworkIcons.TryGetValue(chain.Trim(), out var file)
            ? $"Resources/img/networks/{file}"
            : null;

    /// <summary>Every network icon, keyed by the lower-case chain identifier, for the checkout to look up.</summary>
    public static IReadOnlyDictionary<string, string> NetworkIconPaths { get; } =
        NetworkIcons.ToDictionary(pair => pair.Key.ToLowerInvariant(), pair => $"Resources/img/networks/{pair.Value}");

    /// <summary>The web path of a coin's icon, relative to the application root.</summary>
    public static string TokenIcon(StablecoinAsset asset) =>
        $"Resources/img/tokens/{asset.Symbol.ToLowerInvariant()}.svg";

    public static string ChainName(string chain)
    {
        ArgumentException.ThrowIfNullOrEmpty(chain);
        if (ChainNames.TryGetValue(chain, out var name))
            return name;
        return char.ToUpperInvariant(chain[0]) + chain[1..];
    }

    /// <summary>A sort key for <see cref="PreferredChainOrder"/>, then the display name.</summary>
    public static (int Rank, string Name) ChainOrder(string chain)
    {
        var rank = Array.FindIndex(PreferredChainOrder, c => string.Equals(c, chain, StringComparison.OrdinalIgnoreCase));
        return (rank < 0 ? PreferredChainOrder.Length : rank, ChainName(chain));
    }

    /// <summary>Whether two provider identifiers (a chain, an asset, a contract) name the same thing.</summary>
    /// <remarks>
    /// Case-insensitive, because EVM contract addresses are written checksummed by one party and lowercase by
    /// another. Both sides of every comparison are the provider's own spelling of one route, read back from it, so
    /// the base58 identifiers of Solana and Tron are never compared against a hand-typed value here.
    /// </remarks>
    public static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}
