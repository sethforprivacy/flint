using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
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
/// <see cref="StablecoinQuoteMatcher"/> for the rules, and <see cref="StablecoinAmounts.UniqueAsk"/> for what keeps
/// an exactly-paid quote attributable.
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

    /// <summary>A quote is reused only while at least this long remains before it expires.</summary>
    internal static readonly TimeSpan ReuseMargin = TimeSpan.FromMinutes(2);

    /// <summary>How far before the oldest open quote the reconciliation scan starts, for clock skew.</summary>
    private static readonly TimeSpan ScanSlack = TimeSpan.FromMinutes(10);

    private const int ScanPageSize = 50;
    private const int MaxScanPages = 10;
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
        TimeProvider time,
        bool available,
        ILogger<StablecoinPaymentService> logger)
    {
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

    /// <summary>Whether the store offers USDC and USDT at checkout. False for a store that does not exist.</summary>
    public async Task<bool> IsEnabledAsync(string storeId, CancellationToken cancellationToken = default) =>
        await _storeConfig.IsEnabledAsync(storeId, cancellationToken).ConfigureAwait(false) is true;

    /// <summary>
    /// Turns USDC and USDT on or off for a store. Turning them on is refused off mainnet, where no invoice could
    /// ever offer them; turning them off always goes through.
    /// </summary>
    public async Task<bool> SetEnabledAsync(string storeId, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        if (enabled && !Available)
            return false;

        var updated = await _storeConfig.SetEnabledAsync(storeId, enabled, cancellationToken).ConfigureAwait(false);
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
    /// The routes a prompt offers: Orchestra's, for exactly this asset, that can land as sats, and whose published
    /// USD band admits the due — once per network, in checkout order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only routes that can land as sats.</b> Where the store holds a Stable Balance the SDK lands the token
    /// instead when the route can; but a route that can <em>only</em> land a token would be refused for every store
    /// without one, so it is not offered to any.
    /// </para>
    /// <para>
    /// A published band that excludes the due hides the network; an absent one hides nothing — the quote itself is
    /// the authority, and it says so in words when an amount does not fit.
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
                            && (route.BitcoinLimits is not { } limits || limits.AdmitsUsd(due)))
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
    /// A quote for paying <paramref name="invoiceId"/>'s <paramref name="paymentMethodId"/> prompt from
    /// <paramref name="chain"/>, shown on the prompt — reusing a live one for the same network and due.
    /// </summary>
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

        var network = invoice.Details.Networks.FirstOrDefault(n => StablecoinPayments.Same(n.Chain, chain));
        if (network is null)
            return StablecoinQuoteResult.Refused($"This invoice does not take {asset.Symbol} on that network.");

        var sdk = await _runtime.GetSdkClientAsync(invoice.StoreId).ConfigureAwait(false);
        if (sdk is null)
            return StablecoinQuoteResult.Refused($"{asset.Symbol} payments are unavailable right now. Please pay another way.");

        // One quote at a time per store: the uniqueness of every live ask on a route is decided by reading the
        // open quotes and then writing a new one, and two quotes interleaved there could both take the same ask.
        var gate = _storeGates.GetOrAdd(invoice.StoreId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await QuoteLockedAsync(invoice, asset, network, sdk, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<StablecoinQuoteResult> QuoteLockedAsync(
        StablecoinInvoice invoice,
        StablecoinAsset asset,
        StablecoinNetworkOption network,
        ISparkSdkClient sdk,
        CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var paymentMethodId = asset.PaymentMethodId;

        var existing = await _quotes.ListForInvoiceAsync(invoice.InvoiceId, cancellationToken).ConfigureAwait(false);
        var reusable = existing.FirstOrDefault(quote =>
            quote.PaymentMethodId == paymentMethodId.ToString()
            && StablecoinPayments.Same(quote.Chain, network.Chain)
            && StablecoinPayments.Same(quote.ContractAddress, network.ContractAddress)
            && quote.SdkPaymentId is null
            && quote.DueAmount == invoice.Due
            && quote.ExpiresAt > now + ReuseMargin);
        if (reusable is not null)
        {
            var shown = ToActiveQuote(reusable);
            await _invoices.ShowQuoteAsync(invoice.InvoiceId, paymentMethodId, shown, cancellationToken)
                .ConfigureAwait(false);
            return new StablecoinQuoteResult(shown, null);
        }

        if (existing.Count >= StablecoinPayments.MaxQuotesPerInvoice)
        {
            return StablecoinQuoteResult.Refused(
                "This invoice has already asked for as many quotes as it may. Use an address it has already "
                + "shown, or pay another way.");
        }

        var open = await _quotes
            .ListOpenAsync(invoice.StoreId, now - StablecoinPayments.MatchWindow, cancellationToken)
            .ConfigureAwait(false);
        if (open.Count >= StablecoinPayments.MaxOpenQuotesPerStore)
        {
            _logger.LogWarning(
                "Store {StoreId}: refusing a new {Asset} quote for invoice {InvoiceId}; the store already holds "
                + "{Open} unsettled quotes from the last {Hours} hours",
                invoice.StoreId, asset.Symbol, invoice.InvoiceId, open.Count,
                StablecoinPayments.MatchWindow.TotalHours);
            return StablecoinQuoteResult.Refused(
                $"{asset.Symbol} payments are busy right now. Please try again shortly, or pay another way.");
        }

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
        var amount = StablecoinAmounts.ToBaseUnits(invoice.Due, decimals);

        SparkCrossChainReceiveQuote quote;
        try
        {
            quote = await sdk
                .ReceiveCrossChainAsync(route, amount, StablecoinPayments.MaxSlippageBps, cancellationToken)
                .WaitAsync(StablecoinPayments.QuoteDeadline, _time, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return StablecoinQuoteResult.Refused(
                $"Spark took too long to quote {asset.Symbol} on {network.Name}. Try again, or pick another network.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var reason = SparkErrors.Describe(ex);
            _logger.LogInformation(
                "Store {StoreId}: could not quote {Asset} on {Chain} for invoice {InvoiceId} ({Reason})",
                invoice.StoreId, asset.Symbol, route.Chain, invoice.InvoiceId, reason);
            return StablecoinQuoteResult.Refused(
                $"{asset.Symbol} on {network.Name} cannot take this payment right now: {reason}");
        }

        // What the payer is asked for: the SDK's deposit at the prompt's precision, nudged by millionths when
        // another live quote on this route already asks exactly that.
        var rounded = StablecoinAmounts.RoundUpToDivisibility(quote.DepositAmount, decimals, StablecoinPayments.Divisibility);
        var taken = open
            .Where(q => StablecoinPayments.Same(q.Chain, route.Chain)
                        && StablecoinPayments.Same(q.Asset, route.Asset)
                        && StablecoinPayments.Same(q.ContractAddress, route.ContractAddress))
            .Select(q => q.Asked)
            .ToHashSet();
        if (StablecoinAmounts.UniqueAsk(rounded, decimals, StablecoinPayments.Divisibility, taken) is not { } ask)
        {
            _logger.LogWarning(
                "Store {StoreId}: every amount near {Amount} {Asset} on {Chain} is already asked for by a live "
                + "quote; refusing another for invoice {InvoiceId}",
                invoice.StoreId, StablecoinAmounts.Format(rounded, decimals), asset.Symbol, route.Chain,
                invoice.InvoiceId);
            return StablecoinQuoteResult.Refused(
                $"{asset.Symbol} payments are busy right now. Please try again shortly, or pay another way.");
        }

        var asked = StablecoinAmounts.FromBaseUnits(ask, decimals, StablecoinPayments.Divisibility);
        var fee = Math.Max(0m, asked - invoice.Due);
        if (fee > invoice.Due * MaxFeeShareOfDue)
        {
            return StablecoinQuoteResult.Refused(string.Format(
                CultureInfo.InvariantCulture,
                "Paying from {0} would add {1} {2} in network costs to this payment. Choose a cheaper network.",
                network.Name, fee, asset.Symbol));
        }

        var record = new StablecoinQuote
        {
            Id = Guid.NewGuid().ToString("N"),
            StoreId = invoice.StoreId,
            InvoiceId = invoice.InvoiceId,
            PaymentMethodId = paymentMethodId.ToString(),
            Chain = route.Chain,
            ChainId = route.ChainId,
            Asset = route.Asset,
            ContractAddress = route.ContractAddress,
            Decimals = decimals,
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

        // Recorded before anyone is shown the address: the row is the only thing that will attribute a payment to
        // it, so an address shown without one is money that arrives unattributable.
        await _quotes.AddAsync(record, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Store {StoreId}: quoted {Amount} {Asset} on {Chain} for invoice {InvoiceId} (due {Due}, landing as {Destination})",
            invoice.StoreId, StablecoinAmounts.Format(ask, decimals), asset.Symbol, route.Chain, invoice.InvoiceId,
            invoice.Due, quote.DestinationAsset);

        var active = ToActiveQuote(record);
        await _invoices.ShowQuoteAsync(invoice.InvoiceId, paymentMethodId, active, cancellationToken)
            .ConfigureAwait(false);
        return new StablecoinQuoteResult(active, null);
    }

    /// <summary>
    /// What the payer's wallet is handed: an EIP-681 token transfer on an EVM chain, the bare address elsewhere.
    /// </summary>
    /// <remarks>
    /// Built here rather than taken from the SDK, because the SDK's URI carries the deposit it sized and the payer
    /// is asked for <paramref name="ask"/> — rounded to the prompt's precision and possibly nudged — which is the
    /// amount that keeps an exact payment attributable. Solana and Tron wallets do not honour their schemes'
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

    private static StablecoinActiveQuote ToActiveQuote(StablecoinQuote quote) => new()
    {
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

        // Re-evaluated every time rather than remembered as lost: an arrival that fitted two quotes equally well
        // becomes attributable once the other of them is settled by its own exact payment. Only the report is
        // remembered, so the operator hears about each one once.
        openQuotes ??= (await _quotes
                .ListOpenAsync(storeId, _time.GetUtcNow() - StablecoinPayments.MatchWindow, cancellationToken)
                .ConfigureAwait(false))
            .ToList();

        var match = StablecoinQuoteMatcher.Match(openQuotes, conversion);
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

        _logger.LogInformation(
            "Store {StoreId}: {Asset} payment on {Chain} (Spark payment {SdkPaymentId}) settled quote {QuoteId} "
            + "for invoice {InvoiceId}",
            storeId, quote.Asset, quote.Chain, payment.SdkPaymentId, quote.Id, quote.InvoiceId);

        var fresh = await _quotes
            .FindBySdkPaymentIdAsync(storeId, payment.SdkPaymentId, cancellationToken)
            .ConfigureAwait(false);
        return await CreditAsync(fresh ?? quote, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records a settled quote's payment on its BTCPay invoice, and marks it recorded.</summary>
    private async Task<StablecoinReceiveOutcome> CreditAsync(StablecoinQuote quote, CancellationToken cancellationToken)
    {
        if (quote.SdkPaymentId is not { } paymentId)
            return StablecoinReceiveOutcome.CreditFailed;

        var paid = quote.PaidBaseUnits is null ? quote.Asked : StablecoinQuote.ParseBaseUnits(quote.PaidBaseUnits);
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
            _logger.LogWarning(ex,
                "Store {StoreId}: could not record the {Asset} payment {SdkPaymentId} on invoice {InvoiceId} yet; "
                + "it is retried on the next reconciliation pass",
                quote.StoreId, quote.Asset, paymentId, quote.InvoiceId);
            return StablecoinReceiveOutcome.CreditFailed;
        }

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
                _logger.LogWarning(
                    "Store {StoreId}: the {Asset} payment {SdkPaymentId} ({Value}) matched invoice {InvoiceId}, but it "
                    + "could not be recorded there ({Outcome}). The money is in the Spark wallet; the plugin keeps "
                    + "retrying for {Days} days",
                    quote.StoreId, quote.Asset, paymentId, value, quote.InvoiceId, outcome,
                    CreditRetryHorizon.TotalDays);
                return StablecoinReceiveOutcome.CreditFailed;
        }
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

        var paid = conversion.AssetAmountIn is { } amount
            ? StablecoinAmounts.Format(amount, (int)conversion.AssetDecimals)
            : "an unreported amount of";
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
        var creditStores = await _quotes.ListStoresAwaitingCreditAsync(cancellationToken).ConfigureAwait(false);

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
            .ListUncreditedAsync(storeId, MaxCreditsPerStorePerPass, cancellationToken)
            .ConfigureAwait(false);
        foreach (var quote in pending.Where(q => q.SettledAt is not { } at || now - at <= CreditRetryHorizon))
        {
            if (await CreditAsync(quote, cancellationToken).ConfigureAwait(false) is StablecoinReceiveOutcome.Credited)
                credited++;
        }

        return credited;
    }

    private async Task<int> ScanStoreAsync(string storeId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sdk = await _runtime.GetSdkClientAsync(storeId).ConfigureAwait(false);
        if (sdk is null)
            return 0;

        var open = (await _quotes
                .ListOpenAsync(storeId, now - StablecoinPayments.MatchWindow, cancellationToken)
                .ConfigureAwait(false))
            .ToList();
        if (open.Count == 0)
            return 0;

        // Oldest first from the oldest quote that could still be paid: nothing earlier can be one of them.
        var from = open.Min(q => q.CreatedAt) - ScanSlack;
        var credited = 0;
        for (var page = 0; page < MaxScanPages && open.Count > 0; page++)
        {
            var payments = await sdk
                .ListPaymentsAsync(
                    new SparkListPaymentsQuery(
                        SparkPaymentDirection.Receive,
                        CompletedOnly: true,
                        From: from,
                        Offset: page * ScanPageSize,
                        Limit: ScanPageSize,
                        Ascending: true),
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (var payment in payments)
            {
                if (payment.Conversion is not { Provider: SparkCrossChainProvider.Orchestra, Status: SparkConversionStatus.Completed })
                    continue;
                if (await TryCreditAsync(storeId, payment, open, cancellationToken).ConfigureAwait(false)
                    is StablecoinReceiveOutcome.Credited)
                {
                    credited++;
                }
            }

            if (payments.Count < ScanPageSize)
                break;
        }

        return credited;
    }

    #endregion
}
