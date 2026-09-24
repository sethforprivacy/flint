using System;
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>What <see cref="StablecoinQuoteMatcher.Match"/> concluded about one completed receive.</summary>
public enum StablecoinMatchKind
{
    /// <summary>Exactly one quote is this receive's.</summary>
    Matched,

    /// <summary>
    /// The receive's quote-time fingerprint names none of this store's open quotes: it is not one of this
    /// plugin's, or its quote has left the window.
    /// </summary>
    NoMatch,

    /// <summary>More than one open quote fits, and nothing the payment carries tells them apart.</summary>
    Ambiguous
}

public sealed record StablecoinMatch(StablecoinMatchKind Kind, StablecoinQuote? Quote = null, int Candidates = 0);

/// <summary>
/// Decides which of a store's open quotes a completed USDC/USDT receive came from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why there is a decision to make at all.</b> The SDK returns no quote id when it quotes a receive, and the
/// payment that arrives carries no deposit address, so there is no identifier common to both ends. What the
/// completed payment does carry, frozen from the quote by the provider row, is the route plus two amounts —
/// <c>estimatedOut</c> (the quote's <c>expectedReceivedAmount</c>) and <c>serviceFeeAmount</c> — and, live, what
/// the payer actually deposited (<c>assetAmountIn</c>).
/// </para>
/// <para>
/// <b>The rules, in order.</b>
/// </para>
/// <list type="number">
/// <item><description>Only quotes on the same route are candidates: chain, asset and contract all equal.</description></item>
/// <item><description>When the payment carries its fingerprint, candidates must match it. One match is the answer,
/// whatever the payer sent — the fingerprint is quote-time data, so a payer who sent the wrong amount still paid
/// <em>that</em> quote's address. A fingerprint matching nothing is <see cref="StablecoinMatchKind.NoMatch"/>
/// rather than a reason to guess by amount: it means a quote this plugin did not make, such as the same recovery
/// phrase receiving in a mobile wallet.</description></item>
/// <item><description>Several quotes sharing one fingerprint — equal invoices quoted close together, most often
/// landing as USDB, whose estimate rounds to the cent — are told apart by the deposit, which the plugin keeps
/// unique per route (<see cref="StablecoinAmounts.UniqueAsk"/>). One exact match is the answer; anything else is
/// <see cref="StablecoinMatchKind.Ambiguous"/>, and is left for a human rather than credited to whichever invoice
/// came first.</description></item>
/// <item><description>A payment with no fingerprint at all (an SDK that stopped reporting one) falls back to the
/// exact deposit alone.</description></item>
/// </list>
/// <para>
/// Pure and static, so every one of these is tested without an SDK, a database or BTCPay.
/// </para>
/// </remarks>
public static class StablecoinQuoteMatcher
{
    public static StablecoinMatch Match(IReadOnlyList<StablecoinQuote> openQuotes, SparkConversionState conversion)
    {
        ArgumentNullException.ThrowIfNull(openQuotes);
        ArgumentNullException.ThrowIfNull(conversion);

        var onRoute = openQuotes
            .Where(quote => StablecoinPayments.Same(quote.Chain, conversion.Chain)
                            && StablecoinPayments.Same(quote.Asset, conversion.Asset)
                            && StablecoinPayments.Same(quote.ContractAddress, conversion.AssetContract))
            .ToList();

        if (onRoute.Count == 0)
            return new StablecoinMatch(StablecoinMatchKind.NoMatch);

        if (conversion.EstimatedOut is { } estimatedOut && conversion.ServiceFeeAmount is { } serviceFee)
        {
            var fingerprinted = onRoute
                .Where(quote => quote.ExpectedReceived == estimatedOut && quote.ServiceFee == serviceFee)
                .ToList();

            if (fingerprinted.Count == 0)
                return new StablecoinMatch(StablecoinMatchKind.NoMatch);
            if (fingerprinted.Count == 1)
                return new StablecoinMatch(StablecoinMatchKind.Matched, fingerprinted[0], 1);

            // Several quotes froze the same estimate and fee. The deposit is the one thing that can still tell
            // them apart, and failing that the answer is "several", not the first of them.
            return ByExactDeposit(fingerprinted, conversion)
                   ?? new StablecoinMatch(StablecoinMatchKind.Ambiguous, Candidates: fingerprinted.Count);
        }

        // No fingerprint to go on. Only an exact deposit is evidence enough, and without one this receive is not
        // attributable — even to the only quote on the route, which a foreign receive could equally be taken for.
        return ByExactDeposit(onRoute, conversion) ?? new StablecoinMatch(StablecoinMatchKind.NoMatch);
    }

    private static StablecoinMatch? ByExactDeposit(
        IReadOnlyList<StablecoinQuote> candidates,
        SparkConversionState conversion)
    {
        if (conversion.AssetAmountIn is not { } paid)
            return null;

        var exact = candidates.Where(quote => quote.Asked == paid).ToList();
        return exact.Count == 1
            ? new StablecoinMatch(StablecoinMatchKind.Matched, exact[0], candidates.Count)
            : null;
    }
}
