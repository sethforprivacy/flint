using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>
/// The subset of the Breez Spark SDK this plugin uses, expressed in plugin-side types.
/// </summary>
/// <remarks>
/// <para>
/// The seam exists for two reasons. First, testability: the SDK loads a ~200 MB native library and
/// talks to the Lightspark SSP, so none of the money-handling logic above it could otherwise be
/// unit-tested. Second, discipline: the SDK's surface has several traps (BigInteger amounts, opaque
/// payment ids, "not found" reported as an exception, <c>ex.Message</c> being <c>"@v1=…"</c> garbage)
/// and confining them to <see cref="SparkSdkClient"/> keeps them from leaking into the client.
/// </para>
/// <para>
/// <b>Cancellation tokens are accepted and mostly ignored.</b> No <c>IBreezSdk</c> method takes a
/// <c>CancellationToken</c> and there is no way to cancel an in-flight SDK call (spike notes §8).
/// Implementations may use the token to stop waiting, but the underlying call keeps running.
/// </para>
/// </remarks>
public interface ISparkSdkClient : IDisposable
{
    /// <summary>
    /// Wallet identity and balance.
    /// </summary>
    /// <param name="ensureSynced">
    /// False reads the cached value and returns in ~0 ms; true forces a sync, which costs ~2.2 s on
    /// the first call after connect and is coalesced across concurrent callers afterwards. Request
    /// paths should pass false. Note that <c>true</c> is <em>not</em> enough to make the balance
    /// current — see <see cref="SparkNodeInfo"/> and <see cref="SyncWalletAsync"/>.
    /// </param>
    Task<SparkNodeInfo> GetInfoAsync(bool ensureSynced, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forces a wallet sync.
    /// </summary>
    /// <remarks>
    /// The only reliable way to make <see cref="SparkNodeInfo.BalanceSats"/> current: in the funded run
    /// the balance stayed at its stale value for ~20 s after settlement even through
    /// <c>GetInfo(ensureSynced: true)</c>, and only an explicit sync moved it. Costs ~800 ms. Anything
    /// that compares the balance against a threshold — the sweep task — must call this first.
    /// </remarks>
    Task SyncWalletAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Mints a BOLT11 invoice on the SSP.
    /// </summary>
    /// <param name="description">
    /// Must be non-null (the SDK binding throws <c>ArgumentNullException</c> on null) and at most 639
    /// UTF-8 bytes.
    /// </param>
    /// <param name="amountSats">Null for an amountless invoice. Zero is also treated as amountless by the SDK.</param>
    /// <param name="expirySecs">
    /// Must be a positive value. Zero is silently coerced to 24 h and null to 30 days by the SDK, so
    /// callers must always pass an explicit expiry.
    /// </param>
    Task<SparkReceiveResult> ReceiveBolt11Async(
        string description,
        long? amountSats,
        uint expirySecs,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks a payment up by the SDK's own payment id. Returns null when there is no such payment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is <em>not</em> addressable by payment hash. Passing a payment hash returns null (the SDK
    /// throws "Query returned no rows", which this maps to null).
    /// </para>
    /// <para>
    /// This is also the <b>authoritative</b> status of a payment. The status carried on an event is not:
    /// <c>PaymentSucceeded</c> was observed firing twice for one payment on two threads 57 ms apart, and
    /// a completed receive was observed emitting only <c>PaymentPending</c> and never
    /// <c>PaymentSucceeded</c> at all, with the completion visible only from storage afterwards. Re-read
    /// here before crediting anything.
    /// </para>
    /// <para>
    /// For an outgoing payment the id to pass is the <c>idempotencyKey</c> given to
    /// <see cref="SendBolt11Async"/>: the SDK adopts it as <c>Payment.id</c>, so after a crash mid-send
    /// this call answers definitively whether the send happened.
    /// </para>
    /// </remarks>
    Task<SparkPayment?> GetPaymentAsync(string sdkPaymentId, CancellationToken cancellationToken = default);

    /// <summary>Bounded query over settled/pending payment history.</summary>
    Task<IReadOnlyList<SparkPayment>> ListPaymentsAsync(
        SparkListPaymentsQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pays a BOLT11 invoice, giving the caller a veto on the quoted fee before any money moves.
    /// </summary>
    /// <param name="amountSats">
    /// Required for an amountless invoice, ignored otherwise.
    /// </param>
    /// <param name="idempotencyKey">
    /// Must be a UUID string; the SDK rejects anything else with "Invalid TransferId format". Derive it
    /// deterministically so a retry after a crash cannot double-spend. The SDK adopts this value as the
    /// resulting <c>Payment.id</c>, and a replay with the same key returns the original payment without
    /// spending again — verified on cooperative exits, including with a freshly re-quoted prepare.
    /// </param>
    /// <param name="approveQuote">
    /// Called after the SDK has quoted the payment and before it is executed. Return null to proceed,
    /// or a human-readable reason to abort. Must not throw.
    /// </param>
    /// <remarks>
    /// Quotes and execution happen inside this one call on purpose. A prepared response for a bitcoin
    /// address expires after ~60 s, so anything that persists a prepare across a user interaction or a
    /// task boundary is a latent failure; keeping the pair atomic removes the window entirely.
    /// </remarks>
    Task<SparkSendResult> SendBolt11Async(
        string bolt11,
        long? amountSats,
        string idempotencyKey,
        Func<SparkSendQuote, string?> approveQuote,
        TimeSpan? completionTimeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Quotes a cooperative exit without sending anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bare <c>PrepareSendPayment</c>, used to show a merchant what a sweep would cost. It is <b>not</b> free
    /// of side effects the way the BOLT11 prepare is: the bitcoin-address branch reserves leaves first, so it
    /// fails with insufficient funds when the balance cannot cover the amount. It also does not validate that
    /// <c>amount + fee ≤ balance</c> — that arithmetic is the caller's, and getting it wrong surfaces as a late
    /// "insufficient funds" from the send.
    /// </para>
    /// <para>
    /// The returned quote has its own id and a ~60 s expiry and must not be held: every prepare mints a new
    /// quote, and sending against a stale one is rejected by the service provider. Treat this as an estimate and
    /// let <see cref="SendToBitcoinAddressAsync"/> obtain the quote it actually commits to.
    /// </para>
    /// </remarks>
    /// <param name="amountSats">
    /// Required — there is no "send everything" mode. Even <paramref name="feesIncluded"/> needs an explicit
    /// amount; it only changes who absorbs the fee.
    /// </param>
    Task<SparkOnchainFeeQuote> QuoteOnchainSendAsync(
        string address,
        long amountSats,
        bool feesIncluded,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends to a Bitcoin address as a <b>cooperative exit</b>, giving the caller a veto on the quoted fee
    /// before any money moves. This is what auto-sweep does.
    /// </summary>
    /// <param name="feesIncluded">
    /// True to net the fee out of <paramref name="amountSats"/> (the SDK's <c>FeePolicy.FeesIncluded</c>), so the
    /// recipient receives <c>amount − fee</c> and the balance drops by exactly <c>amount</c>. False charges the
    /// fee on top, and the caller is responsible for having checked that the balance covers both.
    /// </param>
    /// <param name="idempotencyKey">
    /// Must be a UUID string. The SDK adopts it as <c>Payment.id</c>, verified on real cooperative exits, so
    /// persisting it <em>before</em> this call is what makes a crash mid-send recoverable:
    /// <see cref="GetPaymentAsync"/> on the same key afterwards answers definitively whether the exit happened.
    /// A replay with the same key returns the original payment without spending again — including with a freshly
    /// re-quoted prepare, since deduplication is keyed on this value alone.
    /// </param>
    /// <param name="approveQuote">
    /// Called with the quote the send is about to commit to, and before it is executed. Return null to proceed or
    /// a human-readable reason to abort. Must not throw. This is where a fee guard belongs: the quote passed here
    /// is the real one, not the estimate a UI displayed.
    /// </param>
    /// <remarks>
    /// Quote and execution are deliberately inside this one call, for the reason given on
    /// <see cref="QuoteOnchainSendAsync"/>: a prepare for a bitcoin address expires in about a minute, so any
    /// design that persists one across a task or request boundary is a latent failure. A caller that still meets
    /// the expiry — the retry window is not zero — should call again with the <em>same</em> idempotency key.
    /// </remarks>
    Task<SparkOnchainSendResult> SendToBitcoinAddressAsync(
        string address,
        long amountSats,
        SparkOnchainSpeed speed,
        bool feesIncluded,
        string idempotencyKey,
        Func<SparkOnchainQuote, string?> approveQuote,
        CancellationToken cancellationToken = default);

    #region On-chain deposits

    /// <summary>
    /// The wallet's static Bitcoin deposit address, for topping the wallet up on-chain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stable across calls and cached by the SDK: the underlying request asks for the existing address, creating
    /// one only if none exists yet. Rotation is available in the SDK and deliberately not exposed here —
    /// previously issued addresses stay monitored, so rotation strands nothing, but a merchant funding a store
    /// wants one address they can save.
    /// </para>
    /// <para>
    /// This is a live service-provider call, not a local derivation, so it costs a round trip and can fail.
    /// </para>
    /// </remarks>
    Task<string> GetBitcoinDepositAddressAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deposits the SDK knows about but has not credited.
    /// </summary>
    /// <remarks>
    /// A local storage read, so it is cheap and works even when the service provider does not. Returns both
    /// not-yet-mature deposits and matured ones whose claim failed; only the latter need anybody
    /// (<see cref="SparkDepositInfo.NeedsAttention"/>).
    /// </remarks>
    Task<IReadOnlyList<SparkDepositInfo>> ListUnclaimedDepositsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims one deposit at an explicit fee ceiling, overriding the wallet's configured cap for this attempt.
    /// </summary>
    /// <param name="maxFee">
    /// The ceiling for this claim. Required: the whole reason to call this by hand is that the configured cap
    /// was too low, so inheriting it would reproduce the failure.
    /// </param>
    Task<SparkClaimDepositResult> ClaimDepositAsync(
        string txId,
        uint vout,
        SparkMaxFee maxFee,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Current mempool fee rates, in sat/vB.
    /// </summary>
    /// <remarks>
    /// Read from a public mempool API rather than a Spark operator. Shown next to the claim-fee policy so a
    /// merchant can see the conditions a stuck deposit is stuck in.
    /// </remarks>
    Task<SparkRecommendedFees> GetRecommendedFeesAsync(CancellationToken cancellationToken = default);

    #endregion

    #region Stable Balance

    /// <summary>Reads the wallet's user settings, including whether stable balance is active.</summary>
    Task<SparkUserSettings> GetUserSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Activates or deactivates stable balance.
    /// </summary>
    /// <param name="activate">
    /// True to activate with <paramref name="label"/>, false to deactivate. An explicit boolean rather than
    /// "null means off", because the SDK's field is a three-state optional-of-enum in which <c>null</c> means
    /// <em>leave unchanged</em> — passing null intending to deactivate would silently do nothing.
    /// </param>
    /// <param name="label">
    /// The plugin-chosen label from the wallet's configured token list, e.g. <c>USDB</c>. This is an
    /// integrator-defined display string with no protocol meaning, <b>not</b> the token identifier. Ignored when
    /// <paramref name="activate"/> is false.
    /// </param>
    /// <remarks>
    /// <b>Both directions move money, and neither is instant.</b> Activation queues a BTC→token conversion and
    /// deactivation queues the reverse, on the SDK's own background worker rather than inline — so the balances
    /// do not move when this returns, and nothing observes the transition, because no SDK event reports one.
    /// </remarks>
    Task SetStableBalanceActiveAsync(
        bool activate,
        string? label,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The service's minimum for a conversion in one direction.
    /// </summary>
    /// <remarks>
    /// Note the unit of the returned minimum follows the <em>from</em> side and so differs between the two
    /// directions; <see cref="SparkConversionLimits"/> carries the direction for exactly that reason.
    /// </remarks>
    Task<SparkConversionLimits> FetchConversionLimitsAsync(
        SparkConversionDirection direction,
        SparkTokenIdentifier token,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Refunds conversions the SDK is holding in <c>RefundNeeded</c>.
    /// </summary>
    /// <remarks>
    /// No request, no response, and no event will tell anyone it worked — the next poll is how the plugin finds
    /// out. Driven from the sweep timer rather than from a loop of its own, because a stuck conversion also
    /// blocks sweeping and the two want to be looked at together.
    /// </remarks>
    Task RefundPendingConversionsAsync(CancellationToken cancellationToken = default);

    #endregion

    #region Cross-chain

    /// <summary>
    /// Routes that can deliver to an address on another chain.
    /// </summary>
    /// <exception cref="SparkCrossChainNotConfiguredException">
    /// When the SDK returns <em>no</em> routes. That is not "nothing reaches this address": with the SDK's
    /// cross-chain configuration unset it returns an empty array and no error at all, so an empty result is a
    /// configuration fault and is raised as one rather than reported as an absent route.
    /// </exception>
    /// <remarks>
    /// The returned routes are unfiltered — Boltz routes appear even though every Boltz prepare currently fails.
    /// <c>CrossChainRouteResolver</c> is where that filtering lives, so the seam stays a faithful report of what
    /// the SDK said.
    /// </remarks>
    Task<IReadOnlyList<SparkCrossChainRoute>> GetCrossChainRoutesAsync(
        string address,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Quotes a cross-chain send without sending anything.
    /// </summary>
    /// <remarks>
    /// Roughly a 60-second life, like every other quote here, and it must not be held across a task or request
    /// boundary — <see cref="SendCrossChainAsync"/> obtains the quote it actually commits to.
    /// </remarks>
    Task<SparkCrossChainQuote> QuoteCrossChainAsync(
        SparkCrossChainRoute route,
        string recipientAddress,
        SparkSendAmount amount,
        uint? maxSlippageBps,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends across chains, giving the caller a veto on the quote before any money moves.
    /// </summary>
    /// <param name="amount">
    /// <b>Carries its own unit</b>, and the unit decides everything else about this call — see
    /// <see cref="SparkSendAmount"/>. The SDK's <c>amount</c> and <c>tokenIdentifier</c> arguments are derived
    /// from this one value together, so they cannot disagree.
    /// </param>
    /// <param name="idempotencyKey">
    /// A UUID, or null.
    /// <para>
    /// <b>Must be null when <paramref name="amount"/> is a token amount.</b> The SDK <em>rejects</em> a key on
    /// any send with a token transfer leg, with <c>InvalidInput</c> — it does not ignore it — so passing one
    /// fails the send. Implementations assert this rather than silently dropping the key, because a caller that
    /// believed it had deduplication and did not is exactly how a sweep gets sent twice.
    /// </para>
    /// <para>
    /// For a bitcoin amount a key is supported and should be passed. Omitting it is also safe on that path —
    /// the SDK then derives a deterministic UUIDv5 from the provider's quote id, so re-sending an identical
    /// prepare deduplicates at the protocol layer — but an explicit key doubles as the sweep record's own key.
    /// </para>
    /// </param>
    /// <param name="approveQuote">
    /// Called with the quote the send is about to commit to and before it is executed. Return null to proceed
    /// or a human-readable reason to abort. Must not throw. The fee and overpay guards belong here.
    /// <para>
    /// <b>Asynchronous, unlike the cooperative-exit equivalent, and that is load-bearing.</b> This callback is
    /// the only point at which the caller sees the quote the send will actually commit to — the prepare that
    /// produced it happens inside this method — and on the token path the caller <em>must persist that quote's
    /// id before the send proceeds</em>, because it is the only thing that will identify the send afterwards.
    /// A synchronous callback would force a blocking database write inside the SDK call.
    /// </para>
    /// </param>
    Task<SparkCrossChainSendResult> SendCrossChainAsync(
        SparkCrossChainRoute route,
        string recipientAddress,
        SparkSendAmount amount,
        uint? maxSlippageBps,
        string? idempotencyKey,
        Func<SparkCrossChainQuote, Task<string?>> approveQuote,
        CancellationToken cancellationToken = default);

    #endregion

    #region Unilateral exit

    /// <summary>
    /// Quotes a unilateral exit — what a forced, non-cooperative withdrawal from the statechain would recover
    /// and cost — without building or signing anything.
    /// </summary>
    /// <param name="feeRateSatPerVbyte">
    /// The rate every transaction in the exit is built at. It is a single rate for the whole tree, so it also
    /// decides which leaves are worth exiting at all, and there is no per-level override.
    /// </param>
    /// <param name="destinationAddress">
    /// Where the final sweep pays. Validated by the SDK, not here; the caller is still expected to have parsed
    /// it for the store's own network first, because a mainnet-shaped address is a valid regtest string.
    /// </param>
    /// <param name="leafIds">
    /// Null or empty selects automatically (the SDK's <c>ExitLeafSelection.Auto</c>): the SDK picks whichever
    /// leaves are worth exiting at this fee rate. Anything else pins the selection to exactly those leaves
    /// (<c>Specific</c>), which is how a resume re-quotes the <em>same</em> exit — see
    /// <see cref="SparkExitLeaf"/>.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>An empty result is a normal answer.</b> With automatic selection the SDK returns no leaves at all when
    /// nothing clears the fee rate, and that must reach the merchant as "nothing worth exiting right now"
    /// rather than as a failure.
    /// </para>
    /// <para>
    /// <b>This still needs the Spark operators to be reachable</b> in the pinned SDK version. Quoting an exit
    /// walks the wallet's tree, which is not held locally, so the one situation a unilateral exit exists for —
    /// operators gone — is the situation in which this call cannot answer. Exiting from local state is a later
    /// SDK feature.
    /// </para>
    /// <para>
    /// Cheap and free of side effects: nothing is reserved, nothing expires, and no quote id is minted. Unlike
    /// <see cref="QuoteOnchainSendAsync"/> it does not touch the service provider's fee-quote machinery at all.
    /// </para>
    /// </remarks>
    Task<SparkExitQuote> PrepareUnilateralExitAsync(
        ulong feeRateSatPerVbyte,
        string destinationAddress,
        IReadOnlyList<string>? leafIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Quotes and then builds a unilateral exit in one call, giving the caller a veto on the quote in between.
    /// Returns signed transactions and <b>broadcasts nothing</b>.
    /// </summary>
    /// <param name="leafIds">
    /// As on <see cref="PrepareUnilateralExitAsync"/>. A build resuming a previously quoted exit passes the ids
    /// that quote returned, because the funding UTXO an operator has already paid for was sized for that leaf
    /// set and automatic selection is free to choose a different one.
    /// </param>
    /// <param name="fundingUtxos">
    /// Confirmed P2WPKH outputs that will pay every fee in the exit. Must be non-empty. The SDK accepts
    /// several and judges their combined value, but the reliable shape for a fresh exit is what
    /// <see cref="SparkExitQuote.SingleUtxoFundingSat"/> quotes: <b>one</b> output of at least that amount,
    /// which the SDK fans out across branches — the service layer passes exactly one for that reason. A
    /// shortfall surfaces as <see cref="SparkExitFundingShortfallException"/>.
    /// </param>
    /// <param name="fundingSecretKey">
    /// The private key for those outputs, used to build a one-shot signer for the CPFP transactions. Held only
    /// for the duration of this call and never logged. The array is the caller's to own and is not cleared here.
    /// </param>
    /// <param name="approveQuote">
    /// Called with the quote this build is about to commit to, and before anything is built. Return null to
    /// proceed or a human-readable refusal, which is raised as <see cref="SparkExitRefusedException"/>. Must not
    /// throw. This is where the "is this still worth doing" guard belongs: the quote passed here is the fresh
    /// one, not whatever a page rendered minutes ago.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Quote and build are one call for the same reason the send paths are</b> — a quote must never be held
    /// across a request or task boundary. The reason differs in kind, though, and is worse here: this quote does
    /// not expire, it goes <em>stale silently</em>. The leaf set is a function of the wallet's tree, which moves
    /// as payments settle, so a build against a quote taken earlier can commit to a different set of leaves than
    /// the operator funded for, with nothing rejecting it.
    /// </para>
    /// <para>
    /// <b>Nothing is broadcast, by the SDK or by this plugin.</b> The returned transactions are signed and
    /// inert; an operator pushes them out by hand, fan-out first and alone, then each tree node packaged with
    /// its CPFP child in dependency order, then the sweep. See <see cref="SparkExitTransaction"/>. That is also
    /// what makes the failure modes here benign: every exception this can throw has moved no coins.
    /// </para>
    /// </remarks>
    /// <exception cref="SparkExitRefusedException"><paramref name="approveQuote"/> returned a refusal.</exception>
    /// <exception cref="SparkExitFundingShortfallException">
    /// The funding outputs do not cover the exit's fees. Carries what the SDK said was needed.
    /// </exception>
    /// <exception cref="SparkExitFundingUtxoConflictException">
    /// One of the funding outputs is already spent by, or committed to, another transaction.
    /// </exception>
    Task<SparkExitResult> UnilateralExitAsync(
        ulong feeRateSatPerVbyte,
        string destinationAddress,
        IReadOnlyList<string>? leafIds,
        IReadOnlyList<SparkExitFundingUtxo> fundingUtxos,
        byte[] fundingSecretKey,
        Func<SparkExitQuote, string?> approveQuote,
        CancellationToken cancellationToken = default);

    #endregion

    /// <summary>
    /// Detaches the event listener and stops the background sync loop.
    /// </summary>
    /// <remarks>
    /// This is <b>not</b> a shutdown. After the SDK's own <c>Disconnect()</c> the instance still
    /// serves the network and still mints live invoices (spike notes §4); only <see cref="IDisposable.Dispose"/>
    /// closes it. Always call both, in this order.
    /// </remarks>
    Task DisconnectAsync();
}
