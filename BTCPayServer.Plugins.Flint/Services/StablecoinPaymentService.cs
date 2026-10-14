using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Payments;
using BTCPayServer.Plugins.Flint.Sdk;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>What asking for a quote produced: a quote to show, or a sentence for the payer.</summary>
public sealed record StablecoinQuoteResult(StablecoinActiveQuote? Quote, string? Error, bool NotFound = false)
{
    public static StablecoinQuoteResult Refused(string error) => new(null, error);
}

/// <summary>What handing a completed receive to the stablecoin path did.</summary>
public enum StablecoinReceiveOutcome
{
    /// <summary>Not a cross-chain receive; the Lightning path's business.</summary>
    NotStablecoin,

    /// <summary>Not finished yet — the Spark claim or the provider's leg is still pending.</summary>
    Pending,

    /// <summary>Recorded on its BTCPay invoice by this call.</summary>
    Credited,

    /// <summary>Already on its BTCPay invoice.</summary>
    AlreadyCredited,

    /// <summary>Matched to a quote, but the BTCPay credit did not land; the reconciliation pass retries it.</summary>
    CreditFailed,

    /// <summary>Matched to no quote, or to several. Reported once and left in the balance.</summary>
    Unattributed
}

/// <summary>
/// Accepting USDC and USDT at checkout: which networks an invoice offers, the quote a payer is shown, and the
/// crediting of what arrives.
/// </summary>
/// <remarks>
/// <para>
/// <b>The flow.</b> An invoice's USDC and USDT prompts are created with the networks the provider serves and no
/// address (<see cref="SelectNetworks"/>). A payer picks a network and the checkout asks for a quote
/// (<see cref="QuoteAsync"/>): the SDK sizes a deposit above the invoice's due, the plugin records the quote and
/// points the prompt at its address with the route's cost as the network fee. The payer sends; the provider
/// bridges the funds to the store's Spark wallet — as sats, or as the store's Stable Balance token — and the SDK
/// reports the arrival with the provider's conversion details. <see cref="TryCreditAsync"/> matches that arrival
/// to its quote and records it on the invoice. <see cref="ReconcileAsync"/> does the same for anything the event
/// stream dropped, and retries credits that did not land.
/// </para>
/// <para>
/// <b>Why every credit is by quote, never by invoice.</b> The provider pays into the one static Spark address of
/// the wallet, so the arrival carries nothing but the quote-time data the provider row froze onto it. See
/// <see cref="StablecoinQuoteMatcher"/> for the rules, and <see cref="QuoteAsync"/> for what keeps that data unique
/// to one quote.
/// </para>
/// </remarks>
public sealed class StablecoinPaymentService
{
    /// <summary>
    /// The largest share of the due a route's cost may be before that route is refused for the invoice.
    /// </summary>
    /// <remarks>
    /// The provider's fee has a fixed part — on Ethereum it includes moving the deposit, which can be dollars — so
    /// on a small invoice some networks cost more to use than they are worth. Refusing is kinder than showing a
    /// payer a quote that doubles the price; they can pick a cheaper network, and the checkout says so.
    /// </remarks>
    internal const decimal MaxFeeShareOfDue = 0.5m;

    /// <summary>
    /// A quote is reused only while at least this long remains of the time it is offered for
    /// (<see cref="StablecoinPayments.OfferedUntil"/>).
    /// </summary>
    internal static readonly TimeSpan ReuseMargin = TimeSpan.FromMinutes(2);

    /// <summary>How far before the oldest open quote the reconciliation scan starts, for clock skew.</summary>
    private static readonly TimeSpan ScanSlack = TimeSpan.FromMinutes(10);

    internal const int ScanPageSize = 50;
    /// <summary>Oldest-first pages the reconciliation sweep reads per rail per pass, after each rail's newest page.</summary>
    internal const int MaxScanPages = 5;
    private const int MaxCreditsPerStorePerPass = 100;
    private const int MaxStoresPerPass = 500;

    /// <summary>How long a settled credit keeps being retried before it is left for a human.</summary>
    private static readonly TimeSpan CreditRetryHorizon = TimeSpan.FromDays(7);

    /// <summary>
    /// How long a finished quote is kept: one credited this long ago, or one that expired unpaid this long ago.
    /// </summary>
    /// <remarks>
    /// Well past anything that reads them — the match window is two days and the credit retry a week — so the table
    /// stays bounded the way the plugin's other tables are, and a quote settled but never credited is kept for
    /// whoever reconciles it by hand.
    /// </remarks>
    internal static readonly TimeSpan QuoteRetention = TimeSpan.FromDays(30);

    private static readonly TimeSpan RetentionInterval = TimeSpan.FromHours(1);
    private long _nextRetentionTicks;

    private static readonly Regex EvmAddress = new("^0x[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant);
    private static readonly Regex Digits = new("^[0-9]+$", RegexOptions.CultureInvariant);

    private readonly IStablecoinQuoteStore _quotes;
    private readonly IStablecoinInvoiceGateway _invoices;
    private readonly IStablecoinStoreConfig _storeConfig;
    private readonly ISparkStoreRuntime _runtime;
    private readonly StablecoinRouteCache _routes;
    private readonly ICrossChainValueOracle _valueOracle;
    private readonly TimeProvider _time;
    private readonly ILogger<StablecoinPaymentService> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _storeGates = new(StringComparer.Ordinal);

    /// <summary>
    /// Arrivals already reported as unattributable, so each is reported once per process rather than on every
    /// reconciliation pass that sees it again. Cleared wholesale if it ever grows large; the cost is a repeated line.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _reportedUnattributed = new(StringComparer.Ordinal);

    public StablecoinPaymentService(
        IStablecoinQuoteStore quotes,
        IStablecoinInvoiceGateway invoices,
        IStablecoinStoreConfig storeConfig,
        ISparkStoreRuntime runtime,
        StablecoinRouteCache routes,
        ICrossChainValueOracle valueOracle,
        TimeProvider time,
        bool available,
        ILogger<StablecoinPaymentService> logger)
    {
        _valueOracle = valueOracle;
        _quotes = quotes;
        _invoices = invoices;
        _storeConfig = storeConfig;
        _runtime = runtime;
        _routes = routes;
        _time = time;
        Available = available;
        _logger = logger;
    }

    /// <summary>
    /// Whether this server can offer USDC and USDT at all: Bitcoin mainnet only, because the SDK refuses a
    /// cross-chain configuration on any other network and there is no test deployment of the provider.
    /// </summary>
    public bool Available { get; }

    #region The store's switch

    /// <summary>
    /// Whether the store offers USDC and USDT at checkout. False for a store that does not exist — and for one whose
    /// configuration cannot be read right now, which is logged.
    /// </summary>
    /// <remarks>
    /// Never throws, because the Flint pages call it inside BTCPay's request, where an exception from plugin code
    /// disables the plugin and restarts the server. A caller that must tell "off" from "could not tell" uses
    /// <see cref="TryReadEnabledAsync"/>.
    /// </remarks>
    public async Task<bool> IsEnabledAsync(string storeId, CancellationToken cancellationToken = default) =>
        await TryReadEnabledAsync(storeId, cancellationToken).ConfigureAwait(false) is true;

    /// <summary>Whether the store offers USDC and USDT at checkout, or null when that cannot be read right now.</summary>
    public async Task<bool?> TryReadEnabledAsync(string storeId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _storeConfig.IsEnabledAsync(storeId, cancellationToken).ConfigureAwait(false) is true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Store {StoreId}: could not read whether USDC and USDT payments are on", storeId);
            return null;
        }
    }

    /// <summary>
    /// Turns USDC and USDT on or off for a store. Turning them on is refused off mainnet, where no invoice could
    /// ever offer them; turning them off always goes through. False when nothing was changed, including when the
    /// store's configuration could not be written, which is logged rather than thrown (see <see cref="IsEnabledAsync"/>).
    /// </summary>
    public async Task<bool> SetEnabledAsync(string storeId, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        if (enabled && !Available)
            return false;

        bool updated;
        try
        {
            updated = await _storeConfig.SetEnabledAsync(storeId, enabled, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Store {StoreId}: could not turn USDC and USDT payments {State}",
                storeId, enabled ? "on" : "off");
            return false;
        }

        if (updated)
        {
            _logger.LogInformation(
                "Store {StoreId}: USDC and USDT payments turned {State}", storeId, enabled ? "on" : "off");
        }

        return updated;
    }

    #endregion

    #region Invoice creation

    /// <summary>
    /// The networks a new invoice's prompt should offer for <paramref name="asset"/>, or the reason it can offer
    /// none. Never throws for an unavailable wallet or provider — that is a reason, not a fault.
    /// </summary>
    public async Task<(IReadOnlyList<StablecoinNetworkOption> Networks, string? Unavailable)> GetNetworksAsync(
        string storeId,
        StablecoinAsset asset,
        decimal due,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        ArgumentNullException.ThrowIfNull(asset);

        if (!Available)
            return ([], "USDC and USDT payments are only available on Bitcoin mainnet.");

        var sdk = await _runtime.GetSdkClientAsync(storeId).ConfigureAwait(false);
        if (sdk is null)
            return ([], "This store's Spark wallet is not running.");

        var routes = await _routes
            .GetAsync(storeId, sdk, StablecoinPayments.RouteFetchDeadline, cancellationToken)
            .ConfigureAwait(false);
        if (routes is null)
            return ([], "Spark could not list the networks it can receive from.");

        var networks = SelectNetworks(routes, asset, due);
        return networks.Count == 0
            ? ([], $"No network takes a {asset.Symbol} payment of {due.ToString(CultureInfo.InvariantCulture)} right now.")
            : (networks, null);
    }

    /// <summary>
    /// The routes a prompt offers: Orchestra's, for exactly this asset, on a network the plugin can show, that can
    /// land as sats, and whose published bounds admit the due — once per network, in checkout order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only routes that can land as sats.</b> Where the store holds a Stable Balance the SDK lands the token
    /// instead when the route can; but a route that can <em>only</em> land a token would be refused for every store
    /// without one, so it is not offered to any.
    /// </para>
    /// <para>
    /// <b>Only networks with an icon</b> (<see cref="StablecoinPayments.NetworkIcon"/>): the icon is how a payer
    /// checks they are sending on the right network, so a network without one is not offered.
    /// </para>
    /// <para>
    /// A published bound that excludes the due hides the network — the USD band, and the floor and ceiling in the
    /// payer's token. The deposit is the due plus the route's cost, so the due is the conservative side of a floor.
    /// An absent bound hides nothing: the quote itself is the authority, and says which bound an amount missed when
    /// a route enforces one it does not publish — as Tron's did for a three-dollar invoice on mainnet.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<StablecoinNetworkOption> SelectNetworks(
        IReadOnlyList<SparkCrossChainReceiveRoute> routes,
        StablecoinAsset asset,
        decimal due) =>
        routes
            .Where(route => route.Provider is SparkCrossChainProvider.Orchestra
                            && string.Equals(route.Asset, asset.Symbol, StringComparison.OrdinalIgnoreCase)
                            && route.LandsAsBitcoin
                            && !string.IsNullOrWhiteSpace(route.Chain)
                            && StablecoinPayments.NetworkIcon(route.Chain) is not null
                            && (route.BitcoinLimits is not { } limits
                                || (limits.AdmitsUsd(due)
                                    && limits.AdmitsAmount(StablecoinAmounts.ToBaseUnits(due, (int)route.Decimals)))))
            .GroupBy(route => route.Chain.ToLowerInvariant())
            .Select(group => group.First())
            .OrderBy(route => StablecoinPayments.ChainOrder(route.Chain).Rank)
            .ThenBy(route => StablecoinPayments.ChainOrder(route.Chain).Name, StringComparer.OrdinalIgnoreCase)
            .Select(route => new StablecoinNetworkOption
            {
                Chain = route.Chain,
                Name = StablecoinPayments.ChainName(route.Chain),
                ChainId = route.ChainId,
                ContractAddress = route.ContractAddress
            })
            .ToList();

    #endregion

    #region Quoting

    /// <summary>
    /// What a payer reads when a network will not take this amount. The provider's own words ("Increase the input
    /// amount") are written for the integrator, not for someone who cannot change the price; this points at the fix
    /// the payer does have, which is another network, and names the bound when the bound is the reason.
    /// </summary>
    /// <remarks>
    /// <b>A published bound is named only when it explains the refusal</b> — a floor above what was asked, a ceiling
    /// below it. The SDK warns that a route can enforce a tighter bound than it publishes, and on mainnet USDT on
    /// Tron refused a three-dollar invoice while publishing an eighty-cent floor: "the smallest payment Tron takes is
    /// $0.80" would have been false in front of the payer it was meant to help.
    /// </remarks>
    internal static string OutOfRange(
        StablecoinAsset asset,
        string network,
        SparkAmountOutOfRange range,
        int decimals,
        BigInteger requested)
    {
        var requestedCents = StablecoinAmounts.FromBaseUnits(requested, decimals, StablecoinPayments.Divisibility) * 100m;
        bool Explains(decimal boundCents) => range.TooSmall ? boundCents > requestedCents : boundCents < requestedCents;

        string? bound = null;
        if (range.BoundAmount is { } units && units > 0 && (range.TooSmall ? units > requested : units < requested))
            bound = $"{StablecoinAmounts.Format(units, decimals)} {asset.Symbol}";
        else if (range.BoundUsdCents is { } cents && cents > 0 && Explains(cents))
            bound = $"${(cents / 100m).ToString("#,0.00", CultureInfo.InvariantCulture)}";

        if (range.TooSmall)
        {
            return bound is null
                ? $"This payment is too small for {asset.Symbol} on {network}. Choose another network."
                : $"The smallest {asset.Symbol} payment {network} takes is {bound}. Choose another network.";
        }

        return bound is null
            ? $"This payment is too large for {asset.Symbol} on {network}. Choose another network, or pay another way."
            : $"The largest {asset.Symbol} payment {network} takes is {bound}. Choose another network, or pay another way.";
    }

    /// <summary>
    /// How many quotes one request asks the provider for, each with its target nudged, before telling the payer to
    /// try again — see <see cref="QuoteAsync"/> on why a quote can need another.
    /// </summary>
    internal const int MaxFingerprintAttempts = 4;

    /// <summary>
    /// A quote for paying <paramref name="invoiceId"/>'s <paramref name="paymentMethodId"/> prompt from
    /// <paramref name="chain"/>, shown on the prompt — reusing a live one for the same network and due.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every recorded quote has a fingerprint no other open quote on its route shares.</b> The fingerprint —
    /// the quote's <c>expectedReceivedAmount</c> and <c>serviceFeeAmount</c> — is the only thing the arriving payment
    /// carries that says which quote it paid (<see cref="StablecoinQuoteMatcher"/>). Equal dues on one route produce
    /// equal fingerprints: landing as USDB every time, because the SDK's sizing is deterministic at dollar parity and
    /// the estimate is floored to the cent; landing as sats whenever the provider's price and the SDK's fiat rate
    /// have not moved between two quotes. So a quote whose fingerprint is taken is not recorded, and the provider is
    /// asked again for a target nudged up. The quote that lost is never shown to anyone, so nothing is sent to its
    /// address; the SDK polls its row for a day like any other unpaid quote.
    /// </para>
    /// <para>
    /// <b>Why nudging the target works, and by how much.</b> The SDK sizes the deposit from the target, and the
    /// provider's quote is exact-in: it forward-computes the estimate from that deposit, and its total fee includes
    /// the sub-cent remainder the estimate was floored by (<c>service_fee_from_quote</c>). Between them the two
    /// halves pin down the deposit, so any change in the deposit changes the fingerprint. A target a millionth of the
    /// token higher is enough when the quote lands as USDB (the SDK rescales the target to USDB's six decimals); a
    /// quote landing as sats needs a sat's worth, because the SDK converts the target to whole sats first. The first
    /// attempt starts past the open quotes with the same due on the route — the twins it would most likely
    /// collide with — so the common case costs one provider round trip and a few millionths of a dollar.
    /// </para>
    /// <para>
    /// <b>The per-store gate covers the check and the write, never the provider.</b> Uniqueness is a read of the
    /// open quotes followed by a write of the new one, and two requests interleaved there could both take the same
    /// fingerprint. The provider call — up to <see cref="StablecoinPayments.QuoteDeadline"/> — happens outside it,
    /// so one slow quote does not queue every payer at the store behind it; the fingerprint it produced is checked
    /// against a fresh read once the gate is held, and a request that lost the race asks again.
    /// </para>
    /// </remarks>
    public async Task<StablecoinQuoteResult> QuoteAsync(
        string invoiceId,
        PaymentMethodId paymentMethodId,
        string? chain,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(invoiceId);
        ArgumentNullException.ThrowIfNull(paymentMethodId);

        if (StablecoinPayments.For(paymentMethodId) is not { } asset)
            return new StablecoinQuoteResult(null, "This invoice has no such payment method.", NotFound: true);
        if (!Available)
            return StablecoinQuoteResult.Refused("USDC and USDT payments are only available on Bitcoin mainnet.");
        if (string.IsNullOrWhiteSpace(chain))
            return StablecoinQuoteResult.Refused("Choose the network you are paying from.");

        var invoice = await _invoices.GetInvoiceAsync(invoiceId, paymentMethodId, cancellationToken).ConfigureAwait(false);
        if (invoice?.Details is null)
            return new StablecoinQuoteResult(null, "This invoice has no such payment method.", NotFound: true);
        if (!invoice.Payable)
            return StablecoinQuoteResult.Refused("This invoice can no longer be paid.");
        if (invoice.Due <= 0m)
            return StablecoinQuoteResult.Refused("Nothing is left to pay on this invoice.");

        // A network without an icon is not offered (see SelectNetworks), including on an invoice listed before that
        // rule: its checkout no longer shows it, and a request naming it anyway is refused like any other.
        var network = invoice.Details.Networks.FirstOrDefault(n => StablecoinPayments.Same(n.Chain, chain));
        if (network is null || StablecoinPayments.NetworkIcon(network.Chain) is null)
            return StablecoinQuoteResult.Refused($"This invoice does not take {asset.Symbol} on that network.");

        var sdk = await _runtime.GetSdkClientAsync(invoice.StoreId).ConfigureAwait(false);
        if (sdk is null)
            return StablecoinQuoteResult.Refused($"{asset.Symbol} payments are unavailable right now. Please pay another way.");

        var now = _time.GetUtcNow();
        var existing = await _quotes.ListForInvoiceAsync(invoice.InvoiceId, cancellationToken).ConfigureAwait(false);
        if (Reusable(existing, invoice, asset, network, sdk, now) is { } reusable)
            return await ShowAsync(invoice, asset, reusable, sdk, cancellationToken).ConfigureAwait(false);
        if (existing.Count >= StablecoinPayments.MaxQuotesPerInvoice)
            return TooManyForInvoice();

        var open = await _quotes
            .ListOpenAsync(invoice.StoreId, now - StablecoinPayments.MatchWindow, cancellationToken)
            .ConfigureAwait(false);
        if (StoreBusy(invoice, asset, open, now) is { } busy)
            return busy;

        var routes = await _routes
            .GetAsync(invoice.StoreId, sdk, StablecoinPayments.RouteFetchDeadline, cancellationToken)
            .ConfigureAwait(false);
        var route = routes?.FirstOrDefault(r =>
            r.Provider is SparkCrossChainProvider.Orchestra
            && StablecoinPayments.Same(r.Chain, network.Chain)
            && StablecoinPayments.Same(r.Asset, asset.Symbol)
            && StablecoinPayments.Same(r.ContractAddress, network.ContractAddress));
        if (route is null)
        {
            return StablecoinQuoteResult.Refused(
                $"{asset.Symbol} on {network.Name} is not available right now. Try another network.");
        }

        var decimals = (int)route.Decimals;
        var due = StablecoinAmounts.ToBaseUnits(invoice.Due, decimals);
        var step = StablecoinAmounts.Pow10(Math.Max(0, decimals - StablecoinPayments.Divisibility));
        var twins = open.Where(q => OnRoute(q, route) && q.DueAmount == invoice.Due).ToList();
        BigInteger nudge = twins.Count;
        // Twins that landed as sats say what a sat's worth is before the provider is asked at all.
        if (twins.LastOrDefault(q => q.DestinationAsset == "BTC" && q.ExpectedReceived > BigInteger.Zero) is { } sats)
            step = BigInteger.Max(step, SatsWorth(due, sats.ExpectedReceived));

        for (var attempt = 0; attempt < MaxFingerprintAttempts; attempt++)
        {
            var amount = due + step * nudge;
            var (quote, refusal) = await RequestQuoteAsync(invoice, asset, network, route, sdk, amount, cancellationToken)
                .ConfigureAwait(false);
            if (quote is null)
                return refusal!;

            // What the payer is asked for: the SDK's deposit at the prompt's precision.
            var ask = StablecoinAmounts.RoundUpToDivisibility(quote.DepositAmount, decimals, StablecoinPayments.Divisibility);
            var asked = StablecoinAmounts.FromBaseUnits(ask, decimals, StablecoinPayments.Divisibility);
            var fee = Math.Max(0m, asked - invoice.Due);
            if (fee > invoice.Due * MaxFeeShareOfDue)
            {
                return StablecoinQuoteResult.Refused(string.Format(
                    CultureInfo.InvariantCulture,
                    "Paying from {0} would add {1} {2} in network costs to this payment. Choose a cheaper network.",
                    network.Name, fee, asset.Symbol));
            }

            StablecoinQuote? recorded = null;
            StablecoinQuote? raced = null;
            StablecoinQuoteResult? refused = null;
            var gate = _storeGates.GetOrAdd(invoice.StoreId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                now = _time.GetUtcNow();
                var current = await _quotes.ListForInvoiceAsync(invoice.InvoiceId, cancellationToken).ConfigureAwait(false);
                raced = Reusable(current, invoice, asset, network, sdk, now);
                if (raced is null)
                {
                    var fresh = await _quotes
                        .ListOpenAsync(invoice.StoreId, now - StablecoinPayments.MatchWindow, cancellationToken)
                        .ConfigureAwait(false);
                    if (current.Count >= StablecoinPayments.MaxQuotesPerInvoice)
                    {
                        refused = TooManyForInvoice();
                    }
                    else if (StoreBusy(invoice, asset, fresh, now) is { } stillBusy)
                    {
                        refused = stillBusy;
                    }
                    else if (!StablecoinQuoteMatcher.FingerprintTaken(
                                 fresh, route, quote.ExpectedReceivedAmount, quote.ServiceFeeAmount))
                    {
                        recorded = NewRecord(invoice, asset, route, quote, ask, fee, now);
                        // Recorded before anyone is shown the address: the row is the only thing that will
                        // attribute a payment to it, so an address shown without one is money that arrives
                        // unattributable.
                        await _quotes.AddAsync(recorded, cancellationToken).ConfigureAwait(false);
                        RecordMinted(sdk, recorded.Id);
                    }
                }
            }
            finally
            {
                gate.Release();
            }

            if (raced is not null)
                return await ShowAsync(invoice, asset, raced, sdk, cancellationToken).ConfigureAwait(false);
            if (refused is not null)
                return refused;
            if (recorded is not null)
            {
                _logger.LogInformation(
                    "Store {StoreId}: quoted {Amount} {Asset} on {Chain} for invoice {InvoiceId} (due {Due}, landing "
                    + "as {Destination})",
                    invoice.StoreId, StablecoinAmounts.Format(ask, decimals), asset.Symbol, route.Chain,
                    invoice.InvoiceId, invoice.Due, quote.DestinationAsset);
                return await ShowAsync(invoice, asset, recorded, sdk, cancellationToken).ConfigureAwait(false);
            }

            // Taken. Landing as sats, a millionth of a dollar is a hundredth of a sat and moves nothing, so from
            // here on each step is at least a sat's worth at the rate this quote implies.
            if (quote.TokenIdentifier is null && quote.ExpectedReceivedAmount > BigInteger.Zero)
                step = BigInteger.Max(step, SatsWorth(amount, quote.ExpectedReceivedAmount));

            nudge += BigInteger.One;
            _logger.LogDebug(
                "Store {StoreId}: a {Asset} quote on {Chain} for invoice {InvoiceId} matched another open quote's "
                + "fingerprint; asking again with the target nudged",
                invoice.StoreId, asset.Symbol, route.Chain, invoice.InvoiceId);
        }

        _logger.LogWarning(
            "Store {StoreId}: {Attempts} {Asset} quotes on {Chain} for invoice {InvoiceId} each matched another open "
            + "quote's fingerprint; refusing rather than showing an address its payment could not be attributed from",
            invoice.StoreId, MaxFingerprintAttempts, asset.Symbol, route.Chain, invoice.InvoiceId);
        return StablecoinQuoteResult.Refused(
            $"{asset.Symbol} payments are busy right now. Please try again shortly, or pay another way.");
    }

    /// <summary>Route base units per sat, rounded up, at the rate a quote of <paramref name="sats"/> for <paramref name="amount"/> implies.</summary>
    private static BigInteger SatsWorth(BigInteger amount, BigInteger sats) =>
        BigInteger.Divide(amount + sats - BigInteger.One, sats);

    /// <summary>The quotes each running wallet minted, by the SDK handle it runs as.</summary>
    /// <remarks>
    /// Weak on the handle, so a wallet that is torn down takes its entry with it. See <see cref="Reusable"/>.
    /// </remarks>
    private readonly ConditionalWeakTable<ISparkSdkClient, ConcurrentDictionary<string, byte>> _mintedBy = new();

    private bool MintedBy(ISparkSdkClient sdk, string quoteId) =>
        _mintedBy.TryGetValue(sdk, out var minted) && minted.ContainsKey(quoteId);

    private void RecordMinted(ISparkSdkClient sdk, string quoteId) =>
        _mintedBy.GetValue(sdk, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal))[quoteId] = 0;

    /// <summary>
    /// The live quote this invoice already holds for the network and due, if any: a payer clicking back and forth
    /// costs nothing.
    /// </summary>
    /// <remarks>
    /// <b>Only a quote the running wallet made.</b> A quote's address pays whichever wallet quoted it, and after a
    /// store's recovery phrase is replaced that is the old one: reused, the checkout kept handing payers an address
    /// into a wallet the store had moved away from, for the hour it is offered. The table records no wallet, so the
    /// running wallet's own quotes are tracked here, by the SDK handle that minted them. After a restart nothing is
    /// known to be the running wallet's, and the next request for each network mints once — a provider row, against
    /// sending a payer to a wallet that may no longer be the store's.
    /// </remarks>
    private StablecoinQuote? Reusable(
        IReadOnlyList<StablecoinQuote> invoiceQuotes,
        StablecoinInvoice invoice,
        StablecoinAsset asset,
        StablecoinNetworkOption network,
        ISparkSdkClient sdk,
        DateTimeOffset now) =>
        invoiceQuotes.FirstOrDefault(quote =>
            MintedBy(sdk, quote.Id)
            && quote.PaymentMethodId == asset.PaymentMethodId.ToString()
            && StablecoinPayments.Same(quote.Chain, network.Chain)
            && StablecoinPayments.Same(quote.ContractAddress, network.ContractAddress)
            && quote.SdkPaymentId is null
            && quote.DueAmount == invoice.Due
            && StablecoinPayments.OfferedUntil(quote.ExpiresAt) > now + ReuseMargin);

    private static StablecoinQuoteResult TooManyForInvoice() =>
        StablecoinQuoteResult.Refused(
            "This invoice has already asked for as many quotes as it may. Use an address it has already "
            + "shown, or pay another way.");

    /// <summary>
    /// A refusal when the store already holds <see cref="StablecoinPayments.MaxOpenQuotesPerStore"/> quotes still on
    /// offer to a payer; null otherwise.
    /// </summary>
    /// <remarks>
    /// <b>Only quotes still on offer count.</b> The cap bounds the provider rows the SDK polls hard, and the SDK polls
    /// a quote on every tick only until its provider expiry; after that, once per ten minutes
    /// (<c>RECEIVE_EXPIRED_PROBE_SECS</c>) until it gives up a day later. A quote past
    /// <see cref="StablecoinPayments.OfferedUntil"/> is also no longer shown to anyone. Counting every quote in the
    /// two-day match window let an anonymous caller — fifty invoices, ten quotes each — switch a store's USDC and
    /// USDT off for two days; now holding the cap takes minting quotes continuously against the checkout's rate
    /// limit. Quotes past their offer still count for attribution: the fingerprint check reads the whole window.
    /// </remarks>
    private StablecoinQuoteResult? StoreBusy(
        StablecoinInvoice invoice,
        StablecoinAsset asset,
        IReadOnlyList<StablecoinQuote> open,
        DateTimeOffset now)
    {
        var offered = open.Count(q => StablecoinPayments.OfferedUntil(q.ExpiresAt) > now);
        if (offered < StablecoinPayments.MaxOpenQuotesPerStore)
            return null;

        _logger.LogWarning(
            "Store {StoreId}: refusing a new {Asset} quote for invoice {InvoiceId}; the store already has {Offered} "
            + "unsettled quotes on offer to payers",
            invoice.StoreId, asset.Symbol, invoice.InvoiceId, offered);
        return StablecoinQuoteResult.Refused(
            $"{asset.Symbol} payments are busy right now. Please try again shortly, or pay another way.");
    }

    private static bool OnRoute(StablecoinQuote quote, SparkCrossChainReceiveRoute route) =>
        StablecoinPayments.Same(quote.Chain, route.Chain)
        && StablecoinPayments.Same(quote.Asset, route.Asset)
        && StablecoinPayments.Same(quote.ContractAddress, route.ContractAddress);

    /// <summary>One provider quote for <paramref name="amount"/>, or the sentence the payer is shown instead.</summary>
    private async Task<(SparkCrossChainReceiveQuote? Quote, StablecoinQuoteResult? Refusal)> RequestQuoteAsync(
        StablecoinInvoice invoice,
        StablecoinAsset asset,
        StablecoinNetworkOption network,
        SparkCrossChainReceiveRoute route,
        ISparkSdkClient sdk,
        BigInteger amount,
        CancellationToken cancellationToken)
    {
        try
        {
            var quote = await sdk
                .ReceiveCrossChainAsync(route, amount, StablecoinPayments.MaxSlippageBps, cancellationToken)
                .WaitAsync(StablecoinPayments.QuoteDeadline, _time, cancellationToken)
                .ConfigureAwait(false);
            return (quote, null);
        }
        catch (TimeoutException)
        {
            return (null, StablecoinQuoteResult.Refused(
                $"Spark took too long to quote {asset.Symbol} on {network.Name}. Try again, or pick another network."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var reason = SparkErrors.Describe(ex);
            _logger.LogInformation(
                "Store {StoreId}: could not quote {Asset} on {Chain} for invoice {InvoiceId} ({Reason})",
                invoice.StoreId, asset.Symbol, route.Chain, invoice.InvoiceId, reason);
            // The payer only ever reads the plugin's own sentences; the provider's words are in the log line above.
            // They are written for the integrator, and relaying even a scrubbed error to an anonymous checkout hands
            // whoever holds an invoice link the provider's and the SDK's internals.
            return (null, StablecoinQuoteResult.Refused(
                SparkErrors.AmountOutOfRange(ex) is { } range
                    ? OutOfRange(asset, network.Name, range, (int)route.Decimals, amount)
                    : SparkErrors.RouteUnavailable(ex) is { } temporary
                        ? temporary
                            ? $"{asset.Symbol} on {network.Name} is unavailable right now. Try again shortly, or choose another network."
                            : $"{asset.Symbol} on {network.Name} can't take this payment. Choose another network."
                        : $"{asset.Symbol} on {network.Name} cannot take this payment right now. Try again shortly, or choose another network."));
        }
    }

    private static StablecoinQuote NewRecord(
        StablecoinInvoice invoice,
        StablecoinAsset asset,
        SparkCrossChainReceiveRoute route,
        SparkCrossChainReceiveQuote quote,
        BigInteger ask,
        decimal fee,
        DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        StoreId = invoice.StoreId,
        InvoiceId = invoice.InvoiceId,
        PaymentMethodId = asset.PaymentMethodId.ToString(),
        Chain = route.Chain,
        ChainId = route.ChainId,
        Asset = route.Asset,
        ContractAddress = route.ContractAddress,
        Decimals = (int)route.Decimals,
        DepositAddress = quote.DepositAddress,
        DepositBaseUnits = StablecoinQuote.FormatBaseUnits(quote.DepositAmount),
        AskedBaseUnits = StablecoinQuote.FormatBaseUnits(ask),
        PaymentRequest = PaymentRequestFor(route, quote.DepositAddress, ask),
        DueAmount = invoice.Due,
        FeeAmount = fee,
        ExpectedReceivedBaseUnits = StablecoinQuote.FormatBaseUnits(quote.ExpectedReceivedAmount),
        DestinationAsset = quote.DestinationAsset,
        ServiceFeeBaseUnits = StablecoinQuote.FormatBaseUnits(quote.ServiceFeeAmount),
        ServiceFeeAsset = quote.ServiceFeeAsset,
        CreatedAt = now,
        ExpiresAt = quote.ExpiresAt
    };

    /// <remarks>
    /// <b>Shown with the wallet it pays.</b> A quote's address pays the wallet that minted it, through
    /// <paramref name="sdk"/>, so the prompt records that wallet's tag and the checkout and payment link stop offering
    /// the quote once the store runs another (see <c>StablecoinCheckoutModelExtension.IsCurrent</c>). A handle that is
    /// no longer the store's by now is a wallet replaced mid-request, and its quote is not shown at all.
    /// </remarks>
    private async Task<StablecoinQuoteResult> ShowAsync(
        StablecoinInvoice invoice,
        StablecoinAsset asset,
        StablecoinQuote quote,
        ISparkSdkClient sdk,
        CancellationToken cancellationToken)
    {
        if (_runtime.GetWalletTag(invoice.StoreId, sdk) is not { } walletTag)
            return StablecoinQuoteResult.Refused($"{asset.Symbol} payments are unavailable right now. Please try again.");

        var shown = ToActiveQuote(quote, walletTag);
        await _invoices.ShowQuoteAsync(invoice.InvoiceId, asset.PaymentMethodId, shown, cancellationToken)
            .ConfigureAwait(false);
        return new StablecoinQuoteResult(shown, null);
    }

    /// <summary>
    /// What the payer's wallet is handed by link: an EIP-681 token transfer on an EVM chain, the bare address
    /// elsewhere. The QR code is the bare address everywhere — a scanner that does not read EIP-681 takes its first
    /// address, the token contract, as the recipient.
    /// </summary>
    /// <remarks>
    /// Built here rather than taken from the SDK, because the SDK's URI carries the deposit it sized and the payer
    /// is asked for <paramref name="ask"/>, rounded up to the prompt's precision, and a wallet handed the unrounded
    /// 18-decimal deposit would send a hair less than the prompt says. Solana and Tron wallets do not honour their schemes'
    /// parameters reliably (the SDK's own reasoning), so those get the address alone and the amount is shown
    /// beside it.
    /// </remarks>
    internal static string PaymentRequestFor(SparkCrossChainReceiveRoute route, string depositAddress, BigInteger ask)
    {
        if (EvmAddress.IsMatch(depositAddress)
            && route.ContractAddress is { } contract && EvmAddress.IsMatch(contract)
            && route.ChainId is { } chainId && Digits.IsMatch(chainId))
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"ethereum:{contract}@{chainId}/transfer?address={depositAddress}&uint256={ask}");
        }

        return depositAddress;
    }

    private static StablecoinActiveQuote ToActiveQuote(StablecoinQuote quote, string walletTag) => new()
    {
        WalletTag = walletTag,
        QuoteId = quote.Id,
        Chain = quote.Chain,
        ChainName = StablecoinPayments.ChainName(quote.Chain),
        DepositAddress = quote.DepositAddress,
        Amount = StablecoinAmounts.Format(quote.Asked, quote.Decimals),
        PaymentRequest = quote.PaymentRequest,
        ContractAddress = quote.ContractAddress,
        ExpiresAt = quote.ExpiresAt,
        Due = quote.DueAmount,
        Fee = quote.FeeAmount
    };

    #endregion

    #region Crediting

    /// <summary>The tag of the wallet the store is running now, or null; see <see cref="ISparkStoreRuntime.GetWalletTag"/>.</summary>
    public string? RunningWalletTag(string storeId) => _runtime.GetWalletTag(storeId);

    /// <summary>Whether a store has quotes a receive arriving now could belong to.</summary>
    public async Task<bool> HasOpenQuotesAsync(string storeId, CancellationToken cancellationToken = default)
    {
        if (!Available)
            return false;
        var open = await _quotes
            .ListOpenAsync(storeId, _time.GetUtcNow() - StablecoinPayments.MatchWindow, cancellationToken)
            .ConfigureAwait(false);
        return open.Count > 0;
    }

    /// <summary>
    /// The expiry every quote an arrival at <paramref name="arrivedAt"/> could have paid is after: the match window,
    /// measured back from the arrival — or from now, for an arrival stamped in the future.
    /// </summary>
    private DateTimeOffset CandidatesExpiringAfter(DateTimeOffset arrivedAt)
    {
        var now = _time.GetUtcNow();
        var from = arrivedAt < now ? arrivedAt : now;
        return from < DateTimeOffset.MinValue + StablecoinPayments.MatchWindow
            ? DateTimeOffset.MinValue
            : from - StablecoinPayments.MatchWindow;
    }

    /// <summary>
    /// Settles and credits one inbound payment if it is a completed cross-chain receive this plugin quoted.
    /// Safe to call any number of times for the same payment, from any path.
    /// </summary>
    public Task<StablecoinReceiveOutcome> TryCreditAsync(
        string storeId,
        SparkPayment payment,
        CancellationToken cancellationToken = default) =>
        TryCreditAsync(storeId, payment, openQuotes: null, cancellationToken);

    private async Task<StablecoinReceiveOutcome> TryCreditAsync(
        string storeId,
        SparkPayment payment,
        List<StablecoinQuote>? openQuotes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        ArgumentNullException.ThrowIfNull(payment);

        if (payment.Direction is not SparkPaymentDirection.Receive
            || payment.Conversion is not { Provider: SparkCrossChainProvider.Orchestra } conversion)
        {
            return StablecoinReceiveOutcome.NotStablecoin;
        }

        if (payment.Status is not SparkPaymentStatus.Completed || conversion.Status is not SparkConversionStatus.Completed)
            return StablecoinReceiveOutcome.Pending;

        var settled = await _quotes
            .FindBySdkPaymentIdAsync(storeId, payment.SdkPaymentId, cancellationToken)
            .ConfigureAwait(false);
        if (settled is not null)
        {
            return settled.CreditedAt is null
                ? await CreditAsync(settled, cancellationToken).ConfigureAwait(false)
                : StablecoinReceiveOutcome.AlreadyCredited;
        }

        // Re-evaluated every time rather than remembered as lost, so a verdict reached before the quote row was
        // readable is not final; the window is the arrival's own (see StablecoinQuoteMatcher), so the passing of
        // time cannot turn an ambiguous arrival into a match. Only the report is remembered, so the operator hears
        // about each one once.
        openQuotes ??= (await _quotes
                .ListOpenAsync(storeId, CandidatesExpiringAfter(payment.Timestamp), cancellationToken)
                .ConfigureAwait(false))
            .ToList();

        var match = StablecoinQuoteMatcher.Match(openQuotes, conversion, payment.Timestamp);
        if (match is not { Kind: StablecoinMatchKind.Matched, Quote: { } quote })
        {
            ReportUnattributed(storeId, payment, conversion, match);
            return StablecoinReceiveOutcome.Unattributed;
        }

        var settlement = new StablecoinSettlement(
            payment.SdkPaymentId,
            conversion.AssetAmountIn,
            conversion.DeliveredAmount,
            conversion.ExternalTxHash,
            conversion.ProviderOrderId,
            conversion.ProviderQuoteId,
            payment.Timestamp);

        if (!await _quotes.TrySettleAsync(quote.Id, settlement, cancellationToken).ConfigureAwait(false))
        {
            // Lost a race to another path settling the same arrival — or the quote was settled by a different
            // payment, which is a second deposit to one address and is not this payment's quote after all.
            var winner = await _quotes
                .FindBySdkPaymentIdAsync(storeId, payment.SdkPaymentId, cancellationToken)
                .ConfigureAwait(false);
            if (winner is null)
            {
                ReportUnattributed(storeId, payment, conversion, match with { Kind = StablecoinMatchKind.Ambiguous });
                return StablecoinReceiveOutcome.Unattributed;
            }

            return winner.CreditedAt is null
                ? await CreditAsync(winner, cancellationToken).ConfigureAwait(false)
                : StablecoinReceiveOutcome.AlreadyCredited;
        }

        openQuotes.Remove(quote);
        ForgetUnattributed(storeId, payment.SdkPaymentId);

        _logger.LogInformation(
            "Store {StoreId}: {Asset} payment on {Chain} (Spark payment {SdkPaymentId}) settled quote {QuoteId} "
            + "for invoice {InvoiceId}",
            storeId, quote.Asset, quote.Chain, payment.SdkPaymentId, quote.Id, quote.InvoiceId);

        var fresh = await _quotes
            .FindBySdkPaymentIdAsync(storeId, payment.SdkPaymentId, cancellationToken)
            .ConfigureAwait(false);
        return await CreditAsync(fresh ?? quote, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// How far short of its quote's due a sats delivery may be worth, at the live price, and still read as the quote
    /// paid in full — when the provider has not said what was deposited (see <see cref="CreditedBaseUnits"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Covers the provider's spread over the store's own rate source and the minutes between the conversion and the
    /// credit — Breez's own receive tests allow 200 bps on sats destinations — and nothing else. A price move is not
    /// in it: the delivery is valued at today's price, so a deposit the provider repriced is judged by what it is
    /// worth now, however far the price moved.
    /// </para>
    /// <para>
    /// It was a flat 10% of the quote's sat estimate until 1.3.1, sized for a day of price drift, and so let a payer
    /// whose deposit went unmeasured send up to a tenth less than asked and settle the invoice in full.
    /// </para>
    /// </remarks>
    internal const int RepricedDeliveryToleranceBps = 200;

    /// <summary>
    /// What a settled quote's payer is taken to have deposited, in route base units — the amount its invoice is
    /// credited with.
    /// </summary>
    /// <param name="quote">The settled quote.</param>
    /// <param name="usdPerSat">
    /// The live value of a sat, for valuing a sats delivery the quote's own price does not account for; null when no
    /// rate could be had, or none was asked for (see <see cref="NeedsLivePrice"/>).
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>The deposit the SDK reports is not always the deposit.</b> <c>assetAmountIn</c> is the provider order's
    /// <c>amountIn</c> when the order carries one, and the <em>quote-time</em> deposit when it does not
    /// (<c>build_orchestra_receive_conversion_info</c> falls back to the row's own <c>amount_in</c>; the SDK's
    /// completed-receive fixture has no <c>amountIn</c> at all). So a reported amount equal to
    /// <see cref="StablecoinQuote.DepositBaseUnits"/> is indistinguishable from no report, and is read as one.
    /// Crediting it as sent recorded a hair less than the payer was asked for on an 18-decimal route, where the ask is
    /// the deposit rounded <em>up</em> to six decimals, and a correctly paid BSC invoice read as partly paid.
    /// </para>
    /// <para>
    /// <b>The rules.</b> A reported amount that is not the quote-time deposit is the provider's word on what arrived,
    /// and is credited as it is. Otherwise the delivery is the only evidence of what was sent:
    /// </para>
    /// <list type="bullet">
    /// <item>within the quote's slippage budget of its estimate, the quote was paid as asked;</item>
    /// <item>a token lands at par, so short of that it is credited in proportion to what was delivered;</item>
    /// <item>
    /// sats short of the estimate may be a short deposit or a late one the provider repriced, and only the price can
    /// tell them apart: worth the quote's due at the live price (less <see cref="RepricedDeliveryToleranceBps"/>),
    /// it was paid as asked; worth less, it is credited in proportion to that worth. With no live price, in
    /// proportion to the estimate, as a token is — a payer the price moved against reads as part-paid, which the
    /// merchant can see and settle, rather than a short payer reading as paid, which they cannot.
    /// </item>
    /// </list>
    /// <para>Credits are rounded down and never exceed the ask.</para>
    /// </remarks>
    internal static BigInteger CreditedBaseUnits(StablecoinQuote quote, decimal? usdPerSat = null)
    {
        ArgumentNullException.ThrowIfNull(quote);

        var asked = quote.Asked;
        if (Measured(quote) is { } paid)
            return paid;

        var expected = quote.ExpectedReceived;
        if (quote.DeliveredBaseUnits is null || expected <= BigInteger.Zero)
            return asked;

        var delivered = StablecoinQuote.ParseBaseUnits(quote.DeliveredBaseUnits);
        if (delivered * 10_000 >= expected * (10_000 - StablecoinPayments.MaxSlippageBps))
            return asked;

        if (LandsAsSats(quote) && usdPerSat is > 0m && quote.DueAmount > 0m)
        {
            var worth = (decimal)delivered * usdPerSat.Value;
            if (worth * 10_000m >= quote.DueAmount * (10_000m - RepricedDeliveryToleranceBps))
                return asked;

            var ppm = new BigInteger(Math.Floor(worth / quote.DueAmount * 1_000_000m));
            return BigInteger.Min(asked, asked * ppm / 1_000_000);
        }

        return BigInteger.Min(asked, asked * delivered / expected);
    }

    /// <summary>
    /// Whether crediting <paramref name="quote"/> turns on the live price of a sat: an unmeasured sats delivery short
    /// of its estimate by more than the quote's own slippage.
    /// </summary>
    internal static bool NeedsLivePrice(StablecoinQuote quote) =>
        LandsAsSats(quote)
        && Measured(quote) is null
        && quote.DeliveredBaseUnits is not null
        && quote.ExpectedReceived > BigInteger.Zero
        && StablecoinQuote.ParseBaseUnits(quote.DeliveredBaseUnits) * 10_000
           < quote.ExpectedReceived * (10_000 - StablecoinPayments.MaxSlippageBps);

    /// <summary>The deposit the provider measured, or null when what it reported is only the quote-time deposit.</summary>
    private static BigInteger? Measured(StablecoinQuote quote) =>
        quote.PaidBaseUnits is { } reported
        && StablecoinQuote.ParseBaseUnits(reported) is var paid
        && paid != StablecoinQuote.ParseBaseUnits(quote.DepositBaseUnits)
            ? paid
            : null;

    private static bool LandsAsSats(StablecoinQuote quote) =>
        string.Equals(quote.DestinationAsset, "BTC", StringComparison.OrdinalIgnoreCase);

    /// <summary>Records a settled quote's payment on its BTCPay invoice, and marks it recorded.</summary>
    private async Task<StablecoinReceiveOutcome> CreditAsync(StablecoinQuote quote, CancellationToken cancellationToken)
    {
        if (quote.SdkPaymentId is not { } paymentId)
            return StablecoinReceiveOutcome.CreditFailed;

        decimal? usdPerSat = null;
        if (NeedsLivePrice(quote))
        {
            // USD for the coin: the plugin prices USDC and USDT at par with it (see StablecoinRateRules).
            usdPerSat = await _valueOracle
                .TryGetSatoshiValueAsync(quote.StoreId, "USD", cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "Store {StoreId}: {Asset} payment {SdkPaymentId} for invoice {InvoiceId} delivered {Delivered} sat "
                + "against an estimate of {Expected} and no measured deposit; valuing it at {Price}",
                quote.StoreId, quote.Asset, paymentId, quote.InvoiceId, quote.DeliveredBaseUnits,
                quote.ExpectedReceivedBaseUnits,
                usdPerSat is { } price ? $"{price * 100_000_000m:0.##} USD/BTC" : "the quote's own price (no live rate)");
        }

        var paid = CreditedBaseUnits(quote, usdPerSat);
        var value = StablecoinAmounts.FromBaseUnits(paid, quote.Decimals, StablecoinPayments.Divisibility);
        // The quote's own network cost, so a payer who sent exactly what was asked settles exactly the due. Capped
        // at what arrived: a deposit below the cost is still a payment, of nothing net.
        var fee = Math.Min(quote.FeeAmount, value);

        var details = new StablecoinPaymentDetails
        {
            QuoteId = quote.Id,
            Chain = quote.Chain,
            ChainId = quote.ChainId,
            Asset = quote.Asset,
            ContractAddress = quote.ContractAddress,
            DepositAddress = quote.DepositAddress,
            ExternalTxHash = quote.ExternalTxHash,
            ProviderOrderId = quote.ProviderOrderId,
            DestinationAsset = quote.DestinationAsset,
            DeliveredAmount = quote.DeliveredBaseUnits,
            SdkPaymentId = paymentId
        };

        StablecoinCreditOutcome outcome;
        try
        {
            outcome = await _invoices
                .AddPaymentAsync(
                    new StablecoinCreditRequest(quote, value, fee, details, quote.SettledAt ?? _time.GetUtcNow()),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Log(CreditFailureLevel(quote.Id), ex,
                "Store {StoreId}: could not record the {Asset} payment {SdkPaymentId} on invoice {InvoiceId} yet; "
                + "it is retried on the next reconciliation pass",
                quote.StoreId, quote.Asset, paymentId, quote.InvoiceId);
            return StablecoinReceiveOutcome.CreditFailed;
        }

        if (outcome is StablecoinCreditOutcome.CreditedNow or StablecoinCreditOutcome.AlreadyRecorded)
            _creditFailureWarned.TryRemove(quote.Id, out _);

        switch (outcome)
        {
            case StablecoinCreditOutcome.CreditedNow:
                _logger.LogInformation(
                    "Store {StoreId}: recorded {Value} {Asset} (network cost {Fee}) on invoice {InvoiceId}",
                    quote.StoreId, value, quote.Asset, fee, quote.InvoiceId);
                await _quotes.TryMarkCreditedAsync(quote.Id, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                return StablecoinReceiveOutcome.Credited;

            case StablecoinCreditOutcome.AlreadyRecorded:
                await _quotes.TryMarkCreditedAsync(quote.Id, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                return StablecoinReceiveOutcome.AlreadyCredited;

            default:
                _logger.Log(CreditFailureLevel(quote.Id),
                    "Store {StoreId}: the {Asset} payment {SdkPaymentId} ({Value}) matched invoice {InvoiceId}, but it "
                    + "could not be recorded there ({Outcome}). The money is in the Spark wallet; the plugin keeps "
                    + "retrying for {Days} days",
                    quote.StoreId, quote.Asset, paymentId, value, quote.InvoiceId, outcome,
                    CreditRetryHorizon.TotalDays);
                return StablecoinReceiveOutcome.CreditFailed;
        }
    }

    /// <summary>How often one quote's failing credit is reported at warning level; in between, at debug.</summary>
    internal static readonly TimeSpan CreditFailureWarningInterval = TimeSpan.FromHours(6);

    /// <summary>When each failing credit was last reported at warning level.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _creditFailureWarned = new(StringComparer.Ordinal);

    /// <summary>
    /// Warning the first time a quote's credit fails and every <see cref="CreditFailureWarningInterval"/> after,
    /// debug otherwise. The retry runs every pass for a week, and a credit that cannot land (the invoice deleted, its
    /// prompt gone) would otherwise write a warning a minute for all of it — per quote.
    /// </summary>
    private LogLevel CreditFailureLevel(string quoteId)
    {
        var now = _time.GetUtcNow();
        if (_creditFailureWarned.Count > 10_000)
            _creditFailureWarned.Clear();
        if (_creditFailureWarned.TryGetValue(quoteId, out var last) && now - last < CreditFailureWarningInterval)
            return LogLevel.Debug;
        _creditFailureWarned[quoteId] = now;
        return LogLevel.Warning;
    }

    private void ReportUnattributed(
        string storeId,
        SparkPayment payment,
        SparkConversionState conversion,
        StablecoinMatch match)
    {
        if (_reportedUnattributed.Count > 10_000)
            _reportedUnattributed.Clear();
        if (!_reportedUnattributed.TryAdd(payment.SdkPaymentId, 0))
            return;

        var amount = conversion.AssetAmountIn is { } units
            ? StablecoinAmounts.Format(units, (int)conversion.AssetDecimals)
            : null;
        RememberUnattributed(storeId, new StablecoinUnattributedArrival(
            payment.SdkPaymentId,
            conversion.Asset ?? "stablecoin",
            conversion.Chain is { Length: > 0 } chain ? StablecoinPayments.ChainName(chain) : "an unknown network",
            amount,
            conversion.ExternalTxHash,
            match.Kind is StablecoinMatchKind.Ambiguous ? match.Candidates : 0,
            payment.Timestamp));

        var paid = amount ?? "an unreported amount of";
        _logger.LogWarning(
            "Store {StoreId}: received {Paid} {Asset} on {Chain} (Spark payment {SdkPaymentId}, payer transaction "
            + "{ExternalTxHash}) that {Reason}. It is in the Spark wallet and has not been recorded on any invoice; "
            + "match it by hand",
            storeId, paid, conversion.Asset ?? "stablecoin", conversion.Chain ?? "an unknown network",
            payment.SdkPaymentId, conversion.ExternalTxHash ?? "unknown",
            match.Kind is StablecoinMatchKind.Ambiguous
                ? $"fits {match.Candidates} open quotes equally well"
                : "matches none of this store's open quotes");
    }

    #endregion

    #region What the store owner should know

    /// <summary>The arrivals each store's page lists, newest last, at most <see cref="MaxRememberedPerStore"/> each.</summary>
    private readonly Dictionary<string, List<StablecoinUnattributedArrival>> _unattributed = new(StringComparer.Ordinal);

    internal const int MaxRememberedPerStore = 20;

    /// <summary>How long after settling a credit that has not landed is shown to the store, rather than left to the retry.</summary>
    internal static readonly TimeSpan UncreditedNoticeAfter = TimeSpan.FromMinutes(15);

    private void RememberUnattributed(string storeId, StablecoinUnattributedArrival arrival)
    {
        lock (_unattributed)
        {
            if (!_unattributed.TryGetValue(storeId, out var list))
            {
                if (_unattributed.Count >= MaxStoresPerPass * 2)
                    _unattributed.Clear();
                _unattributed[storeId] = list = [];
            }

            list.RemoveAll(a => a.SdkPaymentId == arrival.SdkPaymentId);
            list.Add(arrival);
            if (list.Count > MaxRememberedPerStore)
                list.RemoveRange(0, list.Count - MaxRememberedPerStore);
        }
    }

    private void ForgetUnattributed(string storeId, string sdkPaymentId)
    {
        lock (_unattributed)
        {
            if (_unattributed.TryGetValue(storeId, out var list))
                list.RemoveAll(a => a.SdkPaymentId == sdkPaymentId);
        }
    }

    /// <summary>
    /// The USDC/USDT money in a store's wallet that is on no invoice: arrivals nothing could attribute, and
    /// payments that matched an invoice but could not be recorded on it. Never throws.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the store needs to hear this.</b> Both are real money a human has to place by hand, and until now they
    /// were a warning in the server log — which on a shared server the store owner never sees. The Flint status page
    /// shows them (<c>Views/Shared/Spark/StablecoinAttention.cshtml</c>), once each, without anything being sent.
    /// </para>
    /// <para>
    /// <b>Arrivals are remembered in memory, bounded per store.</b> Nothing stores an arrival that matched no quote,
    /// and adding a table for a notice is more than it is worth: after a restart the reconciliation pass reports each
    /// one again for as long as it is inside an open quote's window, which is where it can still be matched by hand
    /// from the invoice it belongs to. Uncredited payments are read from the quote table, so they survive restarts
    /// and are counted however old.
    /// </para>
    /// </remarks>
    public async Task<StablecoinAttention> GetAttentionAsync(string storeId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(storeId) || !Available)
            return StablecoinAttention.None;

        IReadOnlyList<StablecoinUnattributedArrival> arrivals;
        lock (_unattributed)
            arrivals = _unattributed.TryGetValue(storeId, out var list) ? [.. list] : [];

        try
        {
            var count = await _quotes.CountUncreditedAsync(storeId, cancellationToken).ConfigureAwait(false);
            if (count == 0)
                return new StablecoinAttention(arrivals, [], 0);

            var noticeBefore = _time.GetUtcNow() - UncreditedNoticeAfter;
            var uncredited = (await _quotes
                    .ListUncreditedAsync(storeId, DateTimeOffset.MinValue, MaxRememberedPerStore, cancellationToken)
                    .ConfigureAwait(false))
                .Where(q => q.SettledAt is { } at && at < noticeBefore)
                .Select(q => new StablecoinUncreditedPayment(
                    q.InvoiceId,
                    q.Asset,
                    StablecoinPayments.ChainName(q.Chain),
                    StablecoinAmounts.Format(CreditedBaseUnits(q), q.Decimals),
                    q.SdkPaymentId!,
                    q.SettledAt!.Value))
                .ToList();
            // The count covers what the list, bounded, leaves out; below the bound the list is the count.
            return new StablecoinAttention(arrivals, uncredited, count > MaxRememberedPerStore ? count : uncredited.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Store {StoreId}: could not read the USDC/USDT payments awaiting a credit", storeId);
            return new StablecoinAttention(arrivals, [], 0);
        }
    }

    #endregion

    #region Reconciliation

    /// <summary>
    /// One pass: retries credits that did not land, and credits completed receives the event stream dropped.
    /// Returns how many payments this pass recorded on invoices.
    /// </summary>
    public async Task<int> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        if (!Available)
            return 0;

        var now = _time.GetUtcNow();
        var openStores = (await _quotes
                .ListStoresWithOpenQuotesAsync(now - StablecoinPayments.MatchWindow, cancellationToken)
                .ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);
        var creditStores = await _quotes
            .ListStoresAwaitingCreditAsync(now - CreditRetryHorizon, cancellationToken)
            .ConfigureAwait(false);

        await PruneAsync(now, cancellationToken).ConfigureAwait(false);

        var credited = 0;
        foreach (var storeId in creditStores.Union(openStores, StringComparer.Ordinal).Take(MaxStoresPerPass))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                credited += await RetryCreditsAsync(storeId, now, cancellationToken).ConfigureAwait(false);
                if (openStores.Contains(storeId))
                    credited += await ScanStoreAsync(storeId, now, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Store {StoreId}: the USDC/USDT reconciliation pass failed; it runs again shortly", storeId);
            }
        }

        return credited;
    }

    /// <summary>Drops finished quotes past <see cref="QuoteRetention"/>, at most once an hour.</summary>
    private async Task PruneAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var due = Interlocked.Read(ref _nextRetentionTicks);
        if (now.UtcTicks < due
            || Interlocked.CompareExchange(ref _nextRetentionTicks, (now + RetentionInterval).UtcTicks, due) != due)
        {
            return;
        }

        try
        {
            var deleted = await _quotes.DeleteFinishedAsync(now - QuoteRetention, cancellationToken).ConfigureAwait(false);
            if (deleted > 0)
                _logger.LogDebug("Removed {Deleted} USDC/USDT quote(s) older than {Days} days", deleted, QuoteRetention.TotalDays);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not prune old USDC/USDT quotes; the next hour's pass tries again");
        }
    }

    private async Task<int> RetryCreditsAsync(string storeId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var credited = 0;
        var pending = await _quotes
            .ListUncreditedAsync(storeId, now - CreditRetryHorizon, MaxCreditsPerStorePerPass, cancellationToken)
            .ConfigureAwait(false);
        foreach (var quote in pending)
        {
            if (await CreditAsync(quote, cancellationToken).ConfigureAwait(false) is StablecoinReceiveOutcome.Credited)
                credited++;
        }

        return credited;
    }

    /// <summary>
    /// The two kinds of payment a cross-chain receive arrives as: a Spark transfer of sats, or a token transfer of
    /// the store's Stable Balance token. Listed separately because the SDK can filter storage by either but not by
    /// "has cross-chain details", and not by both at once.
    /// </summary>
    private static readonly SparkPaymentMethod[] ScanRails = [SparkPaymentMethod.Spark, SparkPaymentMethod.Token];

    /// <summary>Where each store's sweep of each rail resumes: the window it was walking, and how far it got.</summary>
    private readonly ConcurrentDictionary<(string StoreId, SparkPaymentMethod Rail), (DateTimeOffset From, int Offset)>
        _scanResume = new();

    /// <summary>
    /// Credits what the event stream dropped: every completed cross-chain receive in the window of the store's
    /// open quotes, bounded per pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only the rails a cross-chain receive can arrive on.</b> The window reaches back to the oldest open quote —
    /// two days and more — and a store's Lightning receives in that time can be thousands. Reading them all to find
    /// the few cross-chain ones pushed a dropped event past the page limit on a busy store for up to two days, so the
    /// scan asks the SDK for Spark transfers and token payments only.
    /// </para>
    /// <para>
    /// <b>Newest first, then a resumable sweep.</b> Each rail's newest page is read on every pass, because a dropped
    /// event is most likely a recent one. Then the sweep walks the window oldest first for at most
    /// <see cref="MaxScanPages"/> pages and remembers where it stopped, so a window longer than one pass's budget is
    /// covered over several passes instead of re-reading its first pages forever. Seeing a payment twice costs a
    /// lookup; crediting is idempotent.
    /// </para>
    /// </remarks>
    private async Task<int> ScanStoreAsync(string storeId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sdk = await _runtime.GetSdkClientAsync(storeId).ConfigureAwait(false);
        if (sdk is null)
            return 0;

        var open = await _quotes
            .ListOpenAsync(storeId, now - StablecoinPayments.MatchWindow, cancellationToken)
            .ConfigureAwait(false);
        if (open.Count == 0)
        {
            foreach (var rail in ScanRails)
                _scanResume.TryRemove((storeId, rail), out _);
            return 0;
        }

        // From the oldest quote that could still be paid: nothing earlier can be one of them. Matched against every
        // quote an arrival that early could have paid, which reaches further back than the quotes open now; see
        // StablecoinQuoteMatcher on why the window is the arrival's.
        var from = open.Min(q => q.CreatedAt) - ScanSlack;
        var candidates = (await _quotes
                .ListOpenAsync(storeId, CandidatesExpiringAfter(from), cancellationToken)
                .ConfigureAwait(false))
            .ToList();

        var credited = 0;
        foreach (var rail in ScanRails)
        {
            if (candidates.Count == 0)
                break;

            var (recent, _) = await ScanPageAsync(
                    storeId, sdk, rail, from, 0, ascending: false, candidates, cancellationToken)
                .ConfigureAwait(false);
            credited += recent;

            var key = (storeId, rail);
            var offset = _scanResume.TryGetValue(key, out var resume) && resume.From == from ? resume.Offset : 0;
            var finished = false;
            for (var page = 0; page < MaxScanPages && candidates.Count > 0; page++)
            {
                var (count, read) = await ScanPageAsync(
                        storeId, sdk, rail, from, offset, ascending: true, candidates, cancellationToken)
                    .ConfigureAwait(false);
                credited += count;
                offset += read;
                if (read < ScanPageSize)
                {
                    finished = true;
                    break;
                }
            }

            if (finished)
                _scanResume.TryRemove(key, out _);
            else
                _scanResume[key] = (from, offset);
        }

        return credited;
    }

    /// <summary>One page of one rail: how many it credited, and how many payments it read.</summary>
    private async Task<(int Credited, int Read)> ScanPageAsync(
        string storeId,
        ISparkSdkClient sdk,
        SparkPaymentMethod rail,
        DateTimeOffset from,
        int offset,
        bool ascending,
        List<StablecoinQuote> candidates,
        CancellationToken cancellationToken)
    {
        var payments = await sdk
            .ListPaymentsAsync(
                new SparkListPaymentsQuery(
                    SparkPaymentDirection.Receive,
                    CompletedOnly: true,
                    From: from,
                    Offset: offset,
                    Limit: ScanPageSize,
                    Ascending: ascending,
                    Method: rail),
                cancellationToken)
            .ConfigureAwait(false);

        var credited = 0;
        foreach (var payment in payments)
        {
            if (candidates.Count == 0)
                break;
            if (payment.Conversion is not { Provider: SparkCrossChainProvider.Orchestra, Status: SparkConversionStatus.Completed })
                continue;
            if (await TryCreditAsync(storeId, payment, candidates, cancellationToken).ConfigureAwait(false)
                is StablecoinReceiveOutcome.Credited)
            {
                credited++;
            }
        }

        return (credited, payments.Count);
    }

    #endregion
}
