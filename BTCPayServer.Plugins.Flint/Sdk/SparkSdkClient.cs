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
            null,
            // receiverIdentityPublicKey: null since the SDK 0.23.0 binding change. The new field routes a
            // minted BOLT11 to another Spark identity; passing null (the documented "absent" encoding)
            // preserves the pre-0.23.0 behaviour of crediting the connected wallet, which is what every
            // LNURL prompt here needs. A connected-wallet invoice whose key we set would be a hold invoice
            // nobody can claim.
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
        // Saturating, never a raw cast: these are provider-supplied u64s, and the fee guard downstream is a
        // `<=` against a ceiling — a value that wrapped negative would sail under any ceiling, which is the
        // exact number the guard exists to stop. Saturated to long.MaxValue, the same value fails every guard.
        var feeSats = prepared.paymentMethod switch
        {
            SendPaymentMethod.Bolt11Invoice bolt11Method => SaturatingAdd(
                ToSatsSaturating(bolt11Method.lightningFeeSats),
                ToSatsSaturating(bolt11Method.sparkTransferFeeSats ?? 0)),
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
        // fee; neither half is meaningful on its own. Clamping each half and then adding them raw would still
        // wrap for two near-max halves, so the addition saturates too — see the fee-quote note above.
        static long Total(SendOnchainSpeedFeeQuote tier) =>
            SaturatingAdd(ToSatsSaturating(tier.userFeeSat), ToSatsSaturating(tier.l1BroadcastFeeSat));
    }

    /// <summary>A provider u64 as satoshi, saturated at <see cref="long.MaxValue"/> instead of cast raw.</summary>
    /// <remarks>
    /// A raw <c>(long)</c> of a u64 past <c>long.MaxValue</c> wraps negative, and every fee guard in this plugin
    /// is a <c>&lt;=</c> against a ceiling — a negative fee passes all of them. Saturated, the same absurd value
    /// fails all of them, which is the direction a guard on provider-supplied numbers must fail in.
    /// </remarks>
    internal static long ToSatsSaturating(ulong value) =>
        value > long.MaxValue ? long.MaxValue : (long)value;

    /// <summary>Adds two non-negative fee components without wrapping past <see cref="long.MaxValue"/>.</summary>
    internal static long SaturatingAdd(long a, long b) =>
        a > long.MaxValue - b ? long.MaxValue : a + b;

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
            : response.deposits.Where(IsUnclaimed).Select(MapDeposit).ToList();
    }

    /// <summary>
    /// False for a deposit the SDK has already credited but still lists.
    /// </summary>
    /// <remarks>
    /// Since 0.26 the SDK can claim a deposit before it matures, and such a deposit stays in
    /// <c>ListUnclaimedDeposits</c> with <c>InstantClaimStatus.Claimed</c> until the service provider spends its
    /// output, some time after the credit. Breez's guidance is to treat that status as settled. Shown as
    /// unclaimed, it would read as money still on its way — or, once it matured, as a stuck deposit inviting a
    /// second claim of funds already in the balance.
    /// </remarks>
    internal static bool IsUnclaimed(DepositInfo deposit) =>
        deposit.instantClaimStatus is not InstantClaimStatus.Claimed;

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

            return MapClaimOutcome(response.outcome, _bolt11Parser);
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
    /// <summary>
    /// What a manual claim did, from the SDK's three-way outcome.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Since 0.26 a claim no longer simply returns its payment. <c>Settled</c> still does; <c>Submitted</c> is a
    /// claim made before maturity that settles asynchronously, and the credit arrives later as an ordinary deposit
    /// payment; <c>Deferred</c> claimed nothing, and the SDK keeps the ceiling it was given on the deposit and
    /// retries under it by itself. The plugin only ever claims a matured deposit by hand, so the last two are
    /// unusual here — but a deferral is reported as a failure with its reason rather than as a success, because
    /// the merchant pressed a button that did not move the money.
    /// </para>
    /// </remarks>
    internal static SparkClaimDepositResult MapClaimOutcome(ClaimDepositOutcome? outcome, IBolt11Parser bolt11Parser) =>
        outcome switch
    {
        ClaimDepositOutcome.Settled settled =>
            new SparkClaimDepositResult(SparkPaymentMapper.Map(settled.payment, bolt11Parser), null),
        ClaimDepositOutcome.Submitted => new SparkClaimDepositResult(null, null, Submitted: true),
        ClaimDepositOutcome.Deferred deferred => new SparkClaimDepositResult(null, DescribeDeferral(deferred.reason)),
        _ => new SparkClaimDepositResult(null, "Spark did not say what the claim did.")
    };

    internal static string DescribeDeferral(ClaimDeferredReason? reason) => reason switch
    {
        ClaimDeferredReason.MaxFeeExceeded exceeded => string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "Nothing was claimed: claiming this deposit now costs {0:N0} sat, above the {1:N0} sat allowed. "
            + "Spark keeps that ceiling on the deposit and claims it by itself once the cost fits.",
            exceeded.requiredFeeSats, exceeded.maxFeeSats),
        ClaimDeferredReason.NoEarlyClaimAvailable =>
            "Nothing was claimed yet: the service provider will not credit this deposit early at its current "
            + "depth. Spark claims it by itself, usually within a confirmation or two.",
        ClaimDeferredReason.ProviderDeclined declined =>
            $"Nothing was claimed: the service provider declined ({Describe(declined.message)}).",
        _ => "Nothing was claimed yet, and Spark did not say why."
    };

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

        // The prepared payment's recipient is an echo from the provider, and every guard downstream of here is
        // amount-shaped — none of them would notice the money going to the right chain at the wrong address.
        // Case-insensitive because the request may carry an EIP-55 mixed-case address and the echo a lowercased
        // one: those are the same account, and a checksum-only difference is not a redirection.
        if (!string.Equals(crossChain.recipientAddress, recipientAddress, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Spark prepared this cross-chain send for {crossChain.recipientAddress}, which is not the "
                + "destination that was requested; refusing to send.");
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
        // acceptedAssets (0.26) is the Spark side of the route: what a send can be funded from, and what a
        // receive can land as. The per-asset amount limits riding on it are read by the receive path; a send
        // only needs to know which of the two balances can fund it.
        pair.acceptedAssets is null
            ? []
            : pair.acceptedAssets
                .Select(accepted => accepted.asset is SparkAsset.Token
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
