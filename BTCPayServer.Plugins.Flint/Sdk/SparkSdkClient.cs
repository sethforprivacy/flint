using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Breez.Sdk.Spark;
using Microsoft.Extensions.Logging;
// BTCPayServer has its own PaymentRequest type (the payment-requests feature) and this file lives under
// the BTCPayServer.* root namespace, so the SDK's must be aliased.
using SdkPaymentRequest = Breez.Sdk.Spark.PaymentRequest;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>
/// <see cref="ISparkSdkClient"/> over a live <c>BreezSdk</c> instance.
/// </summary>
/// <remarks>
/// This class owns the native handle. It is the only place in the plugin that touches
/// <c>Breez.Sdk.Spark</c>'s request/response types.
/// </remarks>
public sealed class SparkSdkClient : ISparkSdkClient
{
    private readonly BreezSdk _sdk;
    private readonly IBolt11Parser _bolt11Parser;
    private readonly ILogger _logger;
    private readonly string _storeId;
    private string? _eventListenerId;
    private bool _disposed;

    internal SparkSdkClient(
        string storeId,
        BreezSdk sdk,
        string? eventListenerId,
        IBolt11Parser bolt11Parser,
        ILogger logger)
    {
        _storeId = storeId;
        _sdk = sdk;
        _eventListenerId = eventListenerId;
        _bolt11Parser = bolt11Parser;
        _logger = logger;
    }

    public async Task<SparkNodeInfo> GetInfoAsync(bool ensureSynced, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var response = await _sdk.GetInfo(new GetInfoRequest(ensureSynced)).ConfigureAwait(false);
        return new SparkNodeInfo(
            response.identityPubkey,
            SparkPaymentMapper.ToSats(response.balanceSats),
            MapTokenBalances(response.tokenBalances));
    }

    /// <summary>
    /// The SDK's token-balance dictionary, flattened.
    /// </summary>
    /// <remarks>
    /// The dictionary key is the token identifier and so is <c>tokenMetadata.identifier</c>; the metadata's own
    /// value is preferred, because it is the one the rest of the metadata describes. Empty rather than null on
    /// a wallet with no tokens, which is what the SDK returns and what every wallet returns until Stable
    /// Balance has converted something.
    /// </remarks>
    private static IReadOnlyList<SparkTokenBalance> MapTokenBalances(
        Dictionary<string, Breez.Sdk.Spark.TokenBalance>? balances)
    {
        if (balances is null || balances.Count == 0)
            return [];

        var mapped = new List<SparkTokenBalance>(balances.Count);
        foreach (var (key, balance) in balances)
        {
            var metadata = balance.tokenMetadata;
            var identifier = string.IsNullOrWhiteSpace(metadata?.identifier) ? key : metadata!.identifier;
            if (string.IsNullOrWhiteSpace(identifier))
                continue;

            mapped.Add(new SparkTokenBalance(
                new SparkTokenIdentifier(identifier),
                balance.balance,
                metadata?.ticker ?? "?",
                metadata?.name ?? identifier,
                metadata?.decimals ?? 0,
                metadata?.isFreezable ?? false));
        }

        return mapped;
    }

    public async Task SyncWalletAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _sdk.SyncWallet(new SyncWalletRequest()).ConfigureAwait(false);
    }

    public async Task<SparkReceiveResult> ReceiveBolt11Async(
        string description,
        long? amountSats,
        uint expirySecs,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // The binding throws ArgumentNullException — not an SdkException — on a null description, and
        // an expiry of 0 is silently coerced to 24 h. Both are the caller's contract, asserted here so
        // a violation fails loudly rather than minting a subtly wrong invoice.
        ArgumentNullException.ThrowIfNull(description);
        ArgumentOutOfRangeException.ThrowIfZero(expirySecs);

        var method = new ReceivePaymentMethod.Bolt11Invoice(
            description,
            // A negative or zero amount would be turned into an amountless invoice by the SSP; pass
            // null explicitly so that intent is visible rather than incidental.
            amountSats is > 0 ? (ulong)amountSats.Value : null,
            expirySecs,
            // paymentHash: null lets the SSP pick, which means it owns the preimage and claims the
            // HTLC for us. Supplying our own would make this a hold invoice we must claim manually.
            null);

        var response = await _sdk.ReceivePayment(new ReceivePaymentRequest(method)).ConfigureAwait(false);
        return new SparkReceiveResult(response.paymentRequest, SparkPaymentMapper.ToSats(response.fee));
    }

    public async Task<SparkPayment?> GetPaymentAsync(
        string sdkPaymentId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(sdkPaymentId);

        try
        {
            var response = await _sdk.GetPayment(new GetPaymentRequest(sdkPaymentId)).ConfigureAwait(false);
            return SparkPaymentMapper.Map(response.payment, _bolt11Parser);
        }
        catch (Exception ex) when (SparkErrors.IsNotFound(ex))
        {
            // "Not found" is an exception in this SDK, not a null. Translated here so no caller has to
            // know that.
            return null;
        }
    }

    public async Task<IReadOnlyList<SparkPayment>> ListPaymentsAsync(
        SparkListPaymentsQuery query,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(query);

        var request = new ListPaymentsRequest(
            typeFilter: query.Direction switch
            {
                SparkPaymentDirection.Receive => [PaymentType.Receive],
                SparkPaymentDirection.Send => [PaymentType.Send],
                _ => null
            },
            statusFilter: query.CompletedOnly ? [Breez.Sdk.Spark.PaymentStatus.Completed] : null,
            assetFilter: null,
            paymentDetailsFilter: null,
            fromTimestamp: query.From is null ? null : (ulong)Math.Max(0, query.From.Value.ToUnixTimeSeconds()),
            toTimestamp: null,
            offset: (uint)Math.Max(0, query.Offset),
            limit: (uint)Math.Max(1, query.Limit),
            // Newest-first suits the settlement reconciler, which is looking for something that just happened.
            // A quote-id scan is looking for the oldest row in its window — the send that was in flight when the
            // process died — so paging newest-first would walk away from it.
            sortAscending: query.Ascending);

        var response = await _sdk.ListPayments(request).ConfigureAwait(false);
        return response.payments is null
            ? []
            : response.payments.Select(p => SparkPaymentMapper.Map(p, _bolt11Parser)).ToList();
    }

    public async Task<SparkSendResult> SendBolt11Async(
        string bolt11,
        long? amountSats,
        string idempotencyKey,
        Func<SparkSendQuote, string?> approveQuote,
        TimeSpan? completionTimeout,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(bolt11);
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);
        ArgumentNullException.ThrowIfNull(approveQuote);

        // Wrapped so the caller can tell a prepare failure from a send failure. Nothing has been spent at this
        // point, and reporting it as an unknown outcome gets the payout cancelled ten minutes later over a blip
        // that moved no money. See SparkPreSendException.
        PrepareSendPaymentResponse prepared;
        try
        {
            prepared = await _sdk.PrepareSendPayment(new PrepareSendPaymentRequest(
                    new SdkPaymentRequest.Input(bolt11),
                    amount: amountSats is > 0 ? new BigInteger(amountSats.Value) : null))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new SparkPreSendException(ex);
        }

        // Prepare succeeds even with a zero balance — the funds check happens in SendPayment — so this
        // quote is free to obtain and must be checked before committing.
        var feeSats = prepared.paymentMethod switch
        {
            SendPaymentMethod.Bolt11Invoice bolt11Method =>
                (long)bolt11Method.lightningFeeSats + (long)(bolt11Method.sparkTransferFeeSats ?? 0),
            SendPaymentMethod.SparkAddress sparkAddress => SparkPaymentMapper.ToSats(sparkAddress.fee),
            _ => 0
        };
        var paymentHash = (prepared.paymentMethod as SendPaymentMethod.Bolt11Invoice)?
            .invoiceDetails?.paymentHash?.ToLowerInvariant();

        var quote = new SparkSendQuote(SparkPaymentMapper.ToSats(prepared.amount), feeSats, paymentHash);
        var rejection = approveQuote(quote);
        if (rejection is not null)
        {
            _logger.LogInformation(
                "Store {StoreId}: refused to send {AmountSats} sat over Lightning: {Reason}",
                _storeId, quote.AmountSats, rejection);
            return new SparkSendResult(null, quote, rejection);
        }

        var options = new SendPaymentOptions.Bolt11Invoice(
            // preferSpark: false keeps the payment on the Lightning rail. A Spark-rail send to a
            // Lightning destination cannot be linked back to the invoice, which would break payout
            // bookkeeping.
            preferSpark: false,
            completionTimeoutSecs: completionTimeout is { TotalSeconds: > 0 } timeout
                ? (uint)Math.Min(timeout.TotalSeconds, uint.MaxValue)
                : null);

        var response = await _sdk
            .SendPayment(new SendPaymentRequest(prepared, options, idempotencyKey))
            .ConfigureAwait(false);
        return new SparkSendResult(SparkPaymentMapper.Map(response.payment, _bolt11Parser), quote, null);
    }

    public async Task<SparkOnchainFeeQuote> QuoteOnchainSendAsync(
        string address,
        long amountSats,
        bool feesIncluded,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var (_, quote) = await PrepareOnchainAsync(address, amountSats, feesIncluded).ConfigureAwait(false);
        return quote;
    }

    public async Task<SparkOnchainSendResult> SendToBitcoinAddressAsync(
        string address,
        long amountSats,
        SparkOnchainSpeed speed,
        bool feesIncluded,
        string idempotencyKey,
        Func<SparkOnchainQuote, string?> approveQuote,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);
        ArgumentNullException.ThrowIfNull(approveQuote);

        var (prepared, tiers) = await PrepareOnchainAsync(address, amountSats, feesIncluded)
            .ConfigureAwait(false);

        var quote = new SparkOnchainQuote(
            SparkPaymentMapper.ToSats(prepared.amount), tiers.FeeFor(speed), feesIncluded, tiers);

        var rejection = approveQuote(quote);
        if (rejection is not null)
        {
            _logger.LogInformation(
                "Store {StoreId}: refused a {AmountSats} sat cooperative exit quoted at {FeeSats} sat: {Reason}",
                _storeId, quote.AmountSats, quote.FeeSats, rejection);
            return new SparkOnchainSendResult(null, quote, rejection);
        }

        var response = await _sdk
            .SendPayment(new SendPaymentRequest(
                prepared,
                new SendPaymentOptions.BitcoinAddress(ToSdkSpeed(speed)),
                idempotencyKey))
            .ConfigureAwait(false);

        return new SparkOnchainSendResult(
            SparkPaymentMapper.Map(response.payment, _bolt11Parser), quote, null);
    }

    /// <summary>
    /// One <c>PrepareSendPayment</c> against a bitcoin address, with the response's own claims checked.
    /// </summary>
    /// <remarks>
    /// The two assertions are not paranoia. <c>PrepareSendPaymentResponse.feePolicy</c> echoes back the policy
    /// that <em>will</em> be applied, and if it ever disagreed with what was asked for, the difference is whether
    /// the fee comes out of the swept amount or out of the merchant's reserve — silently. And the
    /// <c>paymentMethod</c> cast is what distinguishes an address the SDK understood as a bitcoin address from
    /// one it resolved as something else entirely (a BIP21 URI, a Spark address, a Lightning address); sending on
    /// the wrong rail is money in the wrong place.
    /// </remarks>
    private async Task<(PrepareSendPaymentResponse Prepared, SparkOnchainFeeQuote Quote)> PrepareOnchainAsync(
        string address,
        long amountSats,
        bool feesIncluded)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amountSats);

        var requestedPolicy = feesIncluded ? FeePolicy.FeesIncluded : FeePolicy.FeesExcluded;
        var prepared = await _sdk.PrepareSendPayment(new PrepareSendPaymentRequest(
                new SdkPaymentRequest.Input(address),
                amount: new BigInteger(amountSats),
                feePolicy: requestedPolicy))
            .ConfigureAwait(false);

        if (prepared.paymentMethod is not SendPaymentMethod.BitcoinAddress bitcoinAddress)
        {
            throw new InvalidOperationException(
                $"Spark resolved the sweep destination as {prepared.paymentMethod.GetType().Name} rather than a "
                + "Bitcoin address; refusing to send.");
        }

        if (prepared.feePolicy != requestedPolicy)
        {
            throw new InvalidOperationException(
                $"Spark quoted the sweep with fee policy {prepared.feePolicy} rather than the requested "
                + $"{requestedPolicy}; refusing to send.");
        }

        var feeQuote = bitcoinAddress.feeQuote;
        return (prepared, new SparkOnchainFeeQuote(
            feeQuote.id,
            DateTimeOffset.FromUnixTimeSeconds((long)Math.Min(feeQuote.expiresAt, long.MaxValue)),
            SlowFeeSats: Total(feeQuote.speedSlow),
            MediumFeeSats: Total(feeQuote.speedMedium),
            FastFeeSats: Total(feeQuote.speedFast)));

        // The executed payment's `fees` equalled this sum exactly on every observed coop exit, so the sum is the
        // fee; neither half is meaningful on its own.
        static long Total(SendOnchainSpeedFeeQuote tier) =>
            (long)Math.Min(tier.userFeeSat, long.MaxValue) + (long)Math.Min(tier.l1BroadcastFeeSat, long.MaxValue);
    }

    /// <remarks>
    /// Mapped explicitly rather than cast. The SDK's enum is ordered <c>Fast = 0, Medium = 1, Slow = 2</c> and
    /// the plugin's is ordered slowest-first, so a numeric cast would quietly buy the most expensive tier for
    /// every merchant who asked for the cheapest.
    /// </remarks>
    private static OnchainConfirmationSpeed ToSdkSpeed(SparkOnchainSpeed speed) => speed switch
    {
        SparkOnchainSpeed.Slow => OnchainConfirmationSpeed.Slow,
        SparkOnchainSpeed.Fast => OnchainConfirmationSpeed.Fast,
        SparkOnchainSpeed.Medium => OnchainConfirmationSpeed.Medium,
        _ => throw new ArgumentOutOfRangeException(nameof(speed), speed, "Unknown confirmation speed.")
    };

    #region On-chain deposits

    public async Task<string> GetBitcoinDepositAddressAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // newAddress: null asks for the existing address, minting one only if the wallet has none. Rotation is
        // available and deliberately not offered: previously issued addresses stay monitored so nothing would be
        // stranded, but a merchant funding a store wants an address they can save once.
        var response = await _sdk
            .ReceivePayment(new ReceivePaymentRequest(new ReceivePaymentMethod.BitcoinAddress(null)))
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(response.paymentRequest))
        {
            throw new InvalidOperationException(
                "Spark returned an empty Bitcoin deposit address. Sending to it would lose the funds.");
        }

        return response.paymentRequest;
    }

    public async Task<IReadOnlyList<SparkDepositInfo>> ListUnclaimedDepositsAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var response = await _sdk
            .ListUnclaimedDeposits(new ListUnclaimedDepositsRequest())
            .ConfigureAwait(false);

        return response.deposits is null
            ? []
            : response.deposits.Select(MapDeposit).ToList();
    }

    public async Task<SparkClaimDepositResult> ClaimDepositAsync(
        string txId,
        uint vout,
        SparkMaxFee maxFee,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(txId);
        ArgumentNullException.ThrowIfNull(maxFee);

        try
        {
            var response = await _sdk
                .ClaimDeposit(new ClaimDepositRequest(txId, vout, ToSdkMaxFee(maxFee)))
                .ConfigureAwait(false);

            return new SparkClaimDepositResult(
                SparkPaymentMapper.Map(response.payment, _bolt11Parser), null);
        }
        catch (Exception ex)
        {
            // A failed claim spends nothing — the SDK either broadcasts the claim transaction or it does not —
            // so this is reported rather than thrown. The deposit stays in the unclaimed list and the merchant
            // can try again at a different ceiling.
            _logger.LogWarning(ex,
                "Store {StoreId}: claiming deposit {TxId}:{Vout} at {MaxFee} failed ({Reason})",
                _storeId, txId, vout, maxFee, SparkErrors.Describe(ex));
            return new SparkClaimDepositResult(null, SparkErrors.Describe(ex));
        }
    }

    public async Task<SparkRecommendedFees> GetRecommendedFeesAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var fees = await _sdk.RecommendedFees().ConfigureAwait(false);
        return new SparkRecommendedFees(
            ToLong(fees.fastestFee),
            ToLong(fees.halfHourFee),
            ToLong(fees.hourFee),
            ToLong(fees.economyFee),
            ToLong(fees.minimumFee));
    }

    /// <remarks>
    /// The claim error is flattened into a kind plus the numbers an operator can act on. Only
    /// <c>MaxDepositClaimFeeExceeded</c> carries a required fee, and that value is exactly what a one-click
    /// manual claim uses as its ceiling — which is why it is lifted out rather than left inside a message.
    /// </remarks>
    private static SparkDepositInfo MapDeposit(DepositInfo deposit) => new(
        deposit.txid,
        deposit.vout,
        ToLong(deposit.amountSats),
        deposit.isMature,
        deposit.claimError switch
        {
            DepositClaimError.MaxDepositClaimFeeExceeded exceeded => new SparkDepositClaimFailure(
                SparkDepositClaimFailureKind.MaxFeeExceeded,
                "The fee needed to claim this deposit is above the limit this store allows.",
                ToLong(exceeded.requiredFeeSats),
                ToLong(exceeded.requiredFeeRateSatPerVbyte)),
            DepositClaimError.MissingUtxo => new SparkDepositClaimFailure(
                SparkDepositClaimFailureKind.MissingUtxo,
                "The transaction output this deposit refers to no longer exists on-chain. It was most likely "
                + "replaced or reorganised away, in which case nothing was ever received."),
            DepositClaimError.Generic generic => new SparkDepositClaimFailure(
                SparkDepositClaimFailureKind.Other, Describe(generic.message)),
            null => null,
            var other => new SparkDepositClaimFailure(
                SparkDepositClaimFailureKind.Other, Describe(other.ToString()))
        },
        deposit.refundTxId);

    private static string Describe(string? message) =>
        string.IsNullOrWhiteSpace(message) ? "Spark did not say why." : message;

    /// <remarks>
    /// Three different fee types live within one call of each other in this SDK — <c>MaxFee</c> on the claim
    /// request, <c>Fee</c> on the claim error, and <c>Fee</c> again on a refund request with a different variant
    /// set. Mapping in exactly one direction, here, is what keeps them from being confused at a call site.
    /// </remarks>
    private static MaxFee ToSdkMaxFee(SparkMaxFee maxFee) => maxFee switch
    {
        SparkMaxFee.Fixed fixedFee => new MaxFee.Fixed(ToUlong(fixedFee.Sats)),
        SparkMaxFee.Rate rate => new MaxFee.Rate(ToUlong(rate.SatPerVbyte)),
        SparkMaxFee.NetworkRecommended recommended =>
            new MaxFee.NetworkRecommended(ToUlong(recommended.LeewaySatPerVbyte)),
        _ => throw new ArgumentOutOfRangeException(nameof(maxFee), maxFee, "Unknown deposit claim fee policy.")
    };

    #endregion

    #region Stable Balance

    public async Task<SparkUserSettings> GetUserSettingsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var settings = await _sdk.GetUserSettings().ConfigureAwait(false);
        return new SparkUserSettings(settings.sparkPrivateModeEnabled, settings.stableBalanceActiveLabel);
    }

    public async Task SetStableBalanceActiveAsync(
        bool activate,
        string? label,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (activate && string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException(
                "Activating stable balance needs the label of a token in the wallet's configured token list.",
                nameof(label));
        }

        // sparkPrivateModeEnabled: null leaves that setting alone. The activation field is a three-state
        // optional-of-enum where null also means "unchanged", so deactivation must be an explicit Unset — this
        // is why the method takes a boolean rather than a nullable label.
        StableBalanceActiveLabel activeLabel = activate
            ? new StableBalanceActiveLabel.Set(label!.Trim())
            : new StableBalanceActiveLabel.Unset();

        await _sdk
            .UpdateUserSettings(new UpdateUserSettingsRequest(null, activeLabel))
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Store {StoreId}: stable balance {Action}{Label}. The conversion runs on the SDK's own background "
            + "worker, so balances do not move immediately and no event will report when they do",
            _storeId, activate ? "activated" : "deactivated", activate ? $" as {label}" : string.Empty);
    }

    public async Task<SparkConversionLimits> FetchConversionLimitsAsync(
        SparkConversionDirection direction,
        SparkTokenIdentifier token,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // The token is required in both directions, though it reaches the SDK differently: as the request's own
        // tokenIdentifier for FromBitcoin (which throws without one) and inside the conversion type for
        // ToBitcoin.
        ConversionType conversionType = direction is SparkConversionDirection.ToBitcoin
            ? new ConversionType.ToBitcoin(token.Value)
            : new ConversionType.FromBitcoin();

        var response = await _sdk
            .FetchConversionLimits(new FetchConversionLimitsRequest(conversionType, token.Value))
            .ConfigureAwait(false);

        return new SparkConversionLimits(direction, response.minFromAmount, response.minToAmount);
    }

    public async Task RefundPendingConversionsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _sdk.RefundPendingConversions().ConfigureAwait(false);
    }

    #endregion

    #region Cross-chain

    public async Task<IReadOnlyList<SparkCrossChainRoute>> GetCrossChainRoutesAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(address);

        var details = await ParseCrossChainAddressAsync(address).ConfigureAwait(false);
        var routes = await _sdk
            .GetCrossChainRoutes(new CrossChainRouteFilter.Send(details))
            .ConfigureAwait(false);

        RequireRoutes(routes?.Length ?? 0, address);
        return routes!.Select(MapRoute).ToList();
    }

    /// <summary>
    /// Refuses an empty route table, because an empty route table is not an answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Split out as a static so the rule is testable without a live SDK — everything else on this class needs
    /// the native handle, and this is the one decision here that is pure policy rather than translation.
    /// </para>
    /// <para>
    /// With <c>Config.crossChainConfig</c> unset the SDK answers every route query with an empty array and
    /// <em>no error</em>; the spike watched the identical call go from 0 routes to 54 purely by setting it. The
    /// plugin sets that config on every mainnet connect, so an empty result means something upstream of the
    /// merchant is wrong — and reporting it as "no routes available" would send them off changing chains to fix
    /// a configuration fault.
    /// </para>
    /// </remarks>
    internal static void RequireRoutes(int count, string address)
    {
        if (count == 0)
            throw new SparkCrossChainNotConfiguredException(address);
    }

    public async Task<SparkCrossChainQuote> QuoteCrossChainAsync(
        SparkCrossChainRoute route,
        string recipientAddress,
        SparkSendAmount amount,
        uint? maxSlippageBps,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var (_, quote) = await PrepareCrossChainAsync(route, recipientAddress, amount, maxSlippageBps)
            .ConfigureAwait(false);
        return quote;
    }

    public async Task<SparkCrossChainSendResult> SendCrossChainAsync(
        SparkCrossChainRoute route,
        string recipientAddress,
        SparkSendAmount amount,
        uint? maxSlippageBps,
        string? idempotencyKey,
        Func<SparkCrossChainQuote, Task<string?>> approveQuote,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentNullException.ThrowIfNull(approveQuote);

        // Asserted, not silently corrected. The SDK *rejects* an idempotency key on any send with a token
        // transfer leg — "Idempotency key is not supported for payments with a token transfer leg", raised as
        // InvalidInput — so dropping the key here would turn a caller's false belief that it has deduplication
        // into a successful send it thinks is replayable. Failing loudly, before anything moves, is the only
        // safe reading: the caller has to have chosen the token-path recovery strategy instead.
        if (idempotencyKey is not null && !amount.SupportsIdempotencyKey)
        {
            throw new InvalidOperationException(
                "A cross-chain send funded from a token balance cannot carry an idempotency key — the SDK "
                + "rejects one outright. Persist the provider quote id before sending and reconcile against it "
                + "instead; never re-send blind.");
        }

        var (prepared, quote) = await PrepareCrossChainAsync(route, recipientAddress, amount, maxSlippageBps)
            .ConfigureAwait(false);

        // Awaited before the send: the caller persists the committed quote id here, and on the token path that
        // write is the whole crash-recovery story.
        var rejection = await approveQuote(quote).ConfigureAwait(false);
        if (rejection is not null)
        {
            _logger.LogInformation(
                "Store {StoreId}: refused a cross-chain send of {Amount} to {Address} on {Chain}: {Reason}",
                _storeId, amount, recipientAddress, route.Chain, rejection);
            return new SparkCrossChainSendResult(null, quote, rejection);
        }

        // options: null — SendPaymentOptions has no cross-chain variant at all (only BitcoinAddress,
        // Bolt11Invoice and SparkAddress), so there is nothing to pass and nothing to choose.
        var response = await _sdk
            .SendPayment(new SendPaymentRequest(prepared, null, idempotencyKey))
            .ConfigureAwait(false);

        return new SparkCrossChainSendResult(
            SparkPaymentMapper.Map(response.payment, _bolt11Parser), quote, null);
    }

    /// <summary>
    /// One <c>PrepareSendPayment</c> against a cross-chain route, with the response's own claims checked.
    /// </summary>
    /// <remarks>
    /// The cast on <c>paymentMethod</c> is the same assertion the cooperative-exit path makes and for the same
    /// reason: it is what distinguishes a destination the SDK resolved as a cross-chain address from one it
    /// resolved as something else, and sending on the wrong rail puts money in the wrong place. No fee policy is
    /// requested — the documented behaviour is that <c>feePolicy</c> is ignored on cross-chain conversion sends,
    /// so asking for one and then asserting it came back would assert a promise the SDK never makes.
    /// </remarks>
    private async Task<(PrepareSendPaymentResponse Prepared, SparkCrossChainQuote Quote)> PrepareCrossChainAsync(
        SparkCrossChainRoute route,
        string recipientAddress,
        SparkSendAmount amount,
        uint? maxSlippageBps)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentException.ThrowIfNullOrEmpty(recipientAddress);
        ArgumentNullException.ThrowIfNull(amount);

        if (route.Handle is not CrossChainRoutePair pair)
        {
            throw new InvalidOperationException(
                "This cross-chain route did not come from the SDK's own route table. The SDK is given back the "
                + "route object it produced, because a reconstructed one is not what the provider quoted.");
        }

        var (sdkAmount, tokenIdentifier) = ToSdkAmount(amount);

        var prepared = await _sdk.PrepareSendPayment(new PrepareSendPaymentRequest(
                new SdkPaymentRequest.CrossChain(recipientAddress, pair, maxSlippageBps, null),
                amount: sdkAmount,
                tokenIdentifier: tokenIdentifier))
            .ConfigureAwait(false);

        if (prepared.paymentMethod is not SendPaymentMethod.CrossChainAddress crossChain)
        {
            throw new InvalidOperationException(
                $"Spark resolved the cross-chain destination as {prepared.paymentMethod.GetType().Name} rather "
                + "than a cross-chain address; refusing to send.");
        }

        var context = crossChain.providerContext as CrossChainProviderContext.Orchestra;

        return (prepared, new SparkCrossChainQuote(
            MapRoute(crossChain.route),
            crossChain.recipientAddress,
            SparkPaymentMapper.ToSats(crossChain.amountIn),
            crossChain.assetAmountIn,
            crossChain.estimatedOut,
            crossChain.feeAmount,
            crossChain.serviceFeeAmount,
            crossChain.serviceFeeAsset,
            ToLong(crossChain.sourceTransferFeeSats),
            ParseExpiry(crossChain.expiresAt),
            context?.quoteId,
            context?.depositAddress));
    }

    /// <summary>
    /// The single place the SDK's <c>amount</c> and <c>tokenIdentifier</c> arguments are produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This method is the whole mitigation for the unit trap.</b> The SDK takes the two as independent
    /// positional parameters and silently reinterprets the first according to the second — satoshi with no token
    /// identifier, token base units with one — so the only structural defence is to make it impossible to set
    /// one without the other. Both come out of this one switch over
    /// <see cref="SparkSendAmount"/>'s two cases, so there is no expression anywhere in the plugin that can
    /// produce a mismatched pair.
    /// </para>
    /// <para>
    /// Nothing above this line ever sees a bare number for either.
    /// </para>
    /// </remarks>
    internal static (BigInteger Amount, string? TokenIdentifier) ToSdkAmount(SparkSendAmount amount) =>
        amount switch
        {
            SparkSendAmount.Bitcoin bitcoin => (new BigInteger(bitcoin.Sats), (string?)null),
            SparkSendAmount.Token token => (token.BaseUnits, token.Identifier.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(amount), amount, "Unknown send amount unit.")
        };

    /// <summary>
    /// Resolves an address into the SDK's own cross-chain address details.
    /// </summary>
    /// <remarks>
    /// <c>Parse</c> is offline and needs no operator, so this is a free validity check as well as a conversion.
    /// A bare EVM address carries <b>no chain</b> — <c>chainId</c> and <c>contractAddress</c> both come back
    /// null — which is why the destination chain is a separate setting rather than something inferred from the
    /// address.
    /// </remarks>
    private async Task<CrossChainAddressDetails> ParseCrossChainAddressAsync(string address)
    {
        var parsed = await _sdk.Parse(address).ConfigureAwait(false);
        if (parsed is InputType.CrossChainAddress crossChain)
            return crossChain.v1;

        throw new SdkException.InvalidInput(
            $"@v1=Spark read this as {parsed.GetType().Name} rather than an address on another chain.");
    }

    private static SparkCrossChainRoute MapRoute(CrossChainRoutePair pair) => new(
        pair.provider switch
        {
            CrossChainProvider.Orchestra => SparkCrossChainProvider.Orchestra,
            CrossChainProvider.Boltz => SparkCrossChainProvider.Boltz,
            _ => SparkCrossChainProvider.Unknown
        },
        pair.chain,
        pair.chainId,
        pair.asset,
        pair.contractAddress,
        pair.decimals,
        pair.supportedSources is null
            ? []
            : pair.supportedSources
                .Select(source => source is SourceAsset.Token
                    ? SparkCrossChainSource.Token
                    : SparkCrossChainSource.Bitcoin)
                .Distinct()
                .ToList(),
        pair);

    /// <remarks>
    /// The cross-chain quote reports its expiry as an ISO-8601 <em>string</em>, where the cooperative-exit quote
    /// reports its own as a Unix <c>ulong</c>. An unparseable value is treated as already expired rather than as
    /// never expiring: the failure mode of the first is one wasted re-quote, and of the second is committing to
    /// a quote the provider has already dropped.
    /// </remarks>
    private static DateTimeOffset ParseExpiry(string? expiresAt) =>
        DateTimeOffset.TryParse(
            expiresAt,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    #endregion

    #region Unilateral exit

    public async Task<SparkExitQuote> PrepareUnilateralExitAsync(
        ulong feeRateSatPerVbyte,
        string destinationAddress,
        IReadOnlyList<string>? leafIds,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var prepared = await PrepareExitAsync(feeRateSatPerVbyte, destinationAddress, leafIds)
            .ConfigureAwait(false);
        return MapExitQuote(prepared);
    }

    public async Task<SparkExitResult> UnilateralExitAsync(
        ulong feeRateSatPerVbyte,
        string destinationAddress,
        IReadOnlyList<string>? leafIds,
        IReadOnlyList<SparkExitFundingUtxo> fundingUtxos,
        byte[] fundingSecretKey,
        Func<SparkExitQuote, string?> approveQuote,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(fundingUtxos);
        ArgumentNullException.ThrowIfNull(approveQuote);

        // Asserted rather than left to the SDK. An empty funding list would be quoted and then fail somewhere
        // inside the build with no indication that the caller simply never found a UTXO, and a zero-length key
        // produces a signer that signs nothing.
        if (fundingUtxos.Count == 0)
        {
            throw new ArgumentException(
                "A unilateral exit needs at least one confirmed funding output to pay its on-chain fees.",
                nameof(fundingUtxos));
        }

        if (fundingSecretKey is null || fundingSecretKey.Length == 0)
        {
            throw new ArgumentException(
                "A unilateral exit needs the private key for its funding outputs so the CPFP transactions can "
                + "be signed.",
                nameof(fundingSecretKey));
        }

        var inputs = fundingUtxos.Select(ToSdkFundingInput).ToArray();

        // Re-quoted here rather than accepted from the caller. See ISparkSdkClient.UnilateralExitAsync: this
        // quote does not expire, it goes stale silently, so the only safe quote is one taken inside the call
        // that consumes it.
        var prepared = await PrepareExitAsync(feeRateSatPerVbyte, destinationAddress, leafIds)
            .ConfigureAwait(false);
        var quote = MapExitQuote(prepared);

        var rejection = approveQuote(quote);
        if (rejection is not null)
        {
            _logger.LogInformation(
                "Store {StoreId}: refused to build a unilateral exit recovering {RecoverableSat} sat for "
                + "{FeeSat} sat in fees: {Reason}",
                _storeId, quote.RecoverableValueSat, quote.TotalFeeSat, rejection);
            throw new SparkExitRefusedException(rejection);
        }

        // A one-shot signer over the funding key. Created after the veto so a refused exit never materialises
        // key material, and disposed in a finally because the binding's implementation owns a native handle.
        var signer = BreezSdkSparkMethods.SingleKeyCpfpSigner(fundingSecretKey);
        try
        {
            UnilateralExitResponse response;
            try
            {
                response = await _sdk
                    .UnilateralExit(new UnilateralExitRequest(prepared, inputs), signer)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (SparkErrors.TranslateUnilateralExit(ex) is { } typed)
            {
                // Both translated failures mean the funding outputs were wrong, not that the exit is
                // impossible, and neither built or broadcast anything. Raised as typed exceptions so the
                // service above can put the SDK's own numbers in front of an operator.
                throw typed;
            }

            var transactions = MapExitTransactions(response.transactions);
            _logger.LogInformation(
                "Store {StoreId}: built a unilateral exit over {LeafCount} leaves recovering {RecoverableSat} "
                + "sat for {FeeSat} sat in fees, as {TxCount} signed transactions. Nothing has been broadcast",
                _storeId, response.leaves?.Length ?? 0, ToLong(response.recoverableValueSat),
                ToLong(response.totalFeeSat), transactions.Count);

            return new SparkExitResult(
                ToLong(response.recoverableValueSat),
                ToLong(response.totalFeeSat),
                transactions,
                MapExitLeaves(response.leaves));
        }
        finally
        {
            // The interface the binding exposes is not IDisposable; its generated implementation is.
            if (signer is IDisposable disposable)
                disposable.Dispose();
        }
    }

    /// <summary>
    /// One <c>PrepareUnilateralExit</c>, with the response's own echo of the request checked.
    /// </summary>
    /// <remarks>
    /// The check is the same discipline <see cref="PrepareOnchainAsync"/> applies to <c>feePolicy</c>, and it
    /// matters more here: the prepared response is handed straight back to <c>UnilateralExit</c>, which builds
    /// and signs the sweep against <em>its</em> destination and rate rather than against the arguments passed
    /// here. If those ever disagreed, the operator would be handed signed transactions paying somewhere else.
    /// </remarks>
    private async Task<PrepareUnilateralExitResponse> PrepareExitAsync(
        ulong feeRateSatPerVbyte,
        string destinationAddress,
        IReadOnlyList<string>? leafIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationAddress);
        ArgumentOutOfRangeException.ThrowIfZero(feeRateSatPerVbyte);

        var prepared = await _sdk.PrepareUnilateralExit(new PrepareUnilateralExitRequest(
                feeRateSatPerVbyte,
                // P2WPKH is the only funding kind offered. The SDK also accepts P2TR and an arbitrary script,
                // and neither is a choice a merchant makes: the funding key is derived on one fixed path, and a
                // funding kind that disagrees with the input supplied later produces an invalid witness.
                new CpfpFundingKind.P2wpkh(),
                destinationAddress,
                ToSdkLeafSelection(leafIds)))
            .ConfigureAwait(false);

        RequireQuoteEchoesRequest(prepared, feeRateSatPerVbyte, destinationAddress);
        return prepared;
    }

    /// <summary>
    /// Refuses a quote that does not describe the exit that was asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Split out as a static so the rule is testable without a live SDK, like
    /// <see cref="RequireRoutes"/>.
    /// </para>
    /// <para>
    /// The destination comparison ignores case because bech32 and bech32m are case-insensitive and an
    /// operator's address may be pasted in either form, while still catching the failure this exists for: a
    /// response describing a <em>different</em> address. The fee rate is a plain integer echo with no
    /// normalisation possible, so it is compared exactly.
    /// </para>
    /// </remarks>
    internal static void RequireQuoteEchoesRequest(
        PrepareUnilateralExitResponse prepared,
        ulong feeRateSatPerVbyte,
        string destinationAddress)
    {
        ArgumentNullException.ThrowIfNull(prepared);

        if (!string.Equals(prepared.destination, destinationAddress, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Spark quoted the unilateral exit against a different destination address than the one "
                + "requested. The sweep transaction is signed against the quote, so this would pay somewhere "
                + "else; refusing to build.");
        }

        if (prepared.feeRateSatPerVbyte != feeRateSatPerVbyte)
        {
            throw new InvalidOperationException(
                $"Spark quoted the unilateral exit at {prepared.feeRateSatPerVbyte} sat/vB rather than the "
                + $"requested {feeRateSatPerVbyte} sat/vB. Every fee and the funding amount follow from the "
                + "rate, so refusing to build rather than funding against the wrong figure.");
        }
    }

    /// <remarks>
    /// Null and empty are both automatic selection, because a caller that has no leaf ids and a caller that has
    /// an empty list mean the same thing, and <c>Specific([])</c> would be a request to exit nothing. Blank ids
    /// are rejected rather than filtered: a hole in a persisted leaf list means the resume would silently pin a
    /// smaller exit than the one the operator funded.
    /// </remarks>
    internal static ExitLeafSelection ToSdkLeafSelection(IReadOnlyList<string>? leafIds)
    {
        if (leafIds is null || leafIds.Count == 0)
            return new ExitLeafSelection.Auto();

        if (leafIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "A pinned leaf selection contains a blank leaf id, which would quote a different exit than the "
                + "one it is resuming.",
                nameof(leafIds));
        }

        return new ExitLeafSelection.Specific(leafIds.ToArray());
    }

    /// <remarks>
    /// P2WPKH only, matching the funding kind the prepare asks for. The value is clamped rather than wrapped on
    /// the way to the SDK's <c>u64</c>; a negative one is a caller bug and is refused, because an output the
    /// SDK believes is worth zero would be signed for a fee it cannot pay.
    /// </remarks>
    internal static CpfpInput ToSdkFundingInput(SparkExitFundingUtxo utxo)
    {
        ArgumentNullException.ThrowIfNull(utxo);
        ArgumentException.ThrowIfNullOrWhiteSpace(utxo.Txid);
        ArgumentException.ThrowIfNullOrWhiteSpace(utxo.PubkeyHex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(utxo.ValueSat);

        return new CpfpInput.P2wpkh(utxo.Txid, utxo.Vout, ToUlong(utxo.ValueSat), utxo.PubkeyHex);
    }

    internal static SparkExitQuote MapExitQuote(PrepareUnilateralExitResponse prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);

        return new SparkExitQuote(
            ToLong(prepared.recoverableValueSat),
            ToLong(prepared.totalFeeSat),
            ToLong(prepared.singleUtxoFundingSat),
            MapExitLeaves(prepared.leaves),
            ToLong(prepared.fanoutFeeSat),
            prepared.perBranchFunding is null
                ? []
                : prepared.perBranchFunding
                    .Select(branch => new SparkExitBranchFunding(branch.leafId, ToLong(branch.fundingSat)))
                    .ToList(),
            prepared.feeRateSatPerVbyte,
            prepared.destination);
    }

    private static IReadOnlyList<SparkExitLeaf> MapExitLeaves(UnilateralExitLeaf[]? leaves) =>
        leaves is null
            ? []
            : leaves.Select(leaf => new SparkExitLeaf(leaf.leafId, ToLong(leaf.value))).ToList();

    private static IReadOnlyList<SparkExitTransaction> MapExitTransactions(
        UnilateralExitTransaction[]? transactions) =>
        transactions is null ? [] : transactions.Select(MapExitTransaction).ToList();

    internal static SparkExitTransaction MapExitTransaction(UnilateralExitTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return new SparkExitTransaction(
            MapExitTxKind(transaction.kind),
            transaction.nodeId,
            transaction.txid,
            transaction.txHex,
            transaction.cpfpTxHex,
            transaction.csvTimelockBlocks,
            transaction.dependsOn is null ? [] : transaction.dependsOn.ToList(),
            MapExitTxStatus(transaction.status));
    }

    /// <remarks>
    /// Mapped by name, and an unknown variant is a hard failure rather than a fallback. What a transaction is
    /// decides how it may be broadcast — alone, or packaged with a CPFP child — so a kind this plugin does not
    /// understand cannot be given broadcast instructions, and guessing would be instructions to lose money.
    /// Failing here costs nothing: the SDK has broadcast none of it.
    /// </remarks>
    internal static SparkExitTxKind MapExitTxKind(UnilateralExitTxKind kind) => kind switch
    {
        UnilateralExitTxKind.FanOut => SparkExitTxKind.Fanout,
        UnilateralExitTxKind.Node => SparkExitTxKind.TreeNode,
        UnilateralExitTxKind.Refund => SparkExitTxKind.Refund,
        UnilateralExitTxKind.Sweep => SparkExitTxKind.Sweep,
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind,
            "Spark returned a unilateral-exit transaction of a kind this plugin does not know how to broadcast.")
    };

    /// <remarks>
    /// Mapped explicitly rather than cast, for the reason given on <see cref="SparkExitTxStatus"/>: the SDK
    /// orders its enum <c>Confirmed = 0, Unconfirmed = 1</c> and the plugin's is the other way round, so a
    /// numeric cast would report every unmined transaction as confirmed and every confirmed one as pending.
    /// </remarks>
    internal static SparkExitTxStatus MapExitTxStatus(ConfirmationStatus status) => status switch
    {
        ConfirmationStatus.Confirmed => SparkExitTxStatus.Confirmed,
        ConfirmationStatus.Unconfirmed => SparkExitTxStatus.Unconfirmed,
        ConfirmationStatus.Unverified => SparkExitTxStatus.Unverified,
        _ => throw new ArgumentOutOfRangeException(
            nameof(status), status, "Unknown Spark confirmation status.")
    };

    #endregion

    private static long ToLong(ulong value) => (long)Math.Min(value, long.MaxValue);

    private static ulong ToUlong(long value) => value < 0 ? 0UL : (ulong)value;

    public async Task DisconnectAsync()
    {
        if (_disposed)
            return;

        var listenerId = Interlocked.Exchange(ref _eventListenerId, null);
        if (listenerId is not null)
        {
            try
            {
                await _sdk.RemoveEventListener(listenerId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Non-fatal: Dispose below frees the handle, after which no listener can be invoked.
                _logger.LogWarning(ex, "Store {StoreId}: could not remove the Spark event listener", _storeId);
            }
        }

        try
        {
            await _sdk.Disconnect().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Store {StoreId}: Spark disconnect failed", _storeId);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            // Disconnect alone leaves the wallet live and still able to mint invoices; only Dispose
            // frees the native handle. Double-disposing is safe, and disposing without disconnecting
            // first is safe too.
            _sdk.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Store {StoreId}: disposing the Spark SDK instance failed", _storeId);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SparkSdkClient),
                $"The Spark wallet for store {_storeId} has been shut down.");
    }
}
