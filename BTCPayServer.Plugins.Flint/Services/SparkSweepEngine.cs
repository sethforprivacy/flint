using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>What one pass of the sweep engine did.</summary>
public enum SweepOutcomeKind
{
    /// <summary>A cooperative exit was accepted by the service provider.</summary>
    Swept,

    /// <summary>
    /// An earlier sweep has not resolved yet, so this pass deliberately did nothing. Not an error.
    /// </summary>
    InFlight,

    /// <summary>
    /// Nothing to do: sweeping is off, the balance is below the threshold, or another pass is already running.
    /// Normal, and not written to the history.
    /// </summary>
    Skipped,

    /// <summary>
    /// The plugin declined to send, and <b>nothing was sent</b>. Always has a reason, always recorded.
    /// </summary>
    Refused,

    /// <summary>The exit was attempted and failed. Nothing was sent, or the send was rejected.</summary>
    Failed,

    /// <summary>
    /// The send's outcome is unknown — it may or may not have reached the service provider. The record stays
    /// <see cref="SweepRecordStatus.Pending"/> and the next pass resolves it by asking the SDK.
    /// </summary>
    Unresolved
}

/// <param name="Reason">Always set, always fit for a merchant to read.</param>
/// <param name="Record">The sweep record this pass created or resolved, when there is one.</param>
public sealed record SweepRunResult(SweepOutcomeKind Kind, string Reason, SweepRecord? Record = null)
{
    public bool Succeeded => Kind is SweepOutcomeKind.Swept;
}

/// <summary>
/// What a sweep would do right now, for the confirmation step of a manual sweep.
/// </summary>
/// <param name="RefusalReason">
/// Set when the engine would refuse. The confirmation page shows this instead of a confirm button, so a
/// merchant is never invited to press a button that is going to be declined.
/// </param>
/// <param name="Destination">Where it would go, when one could be resolved without consuming an address.</param>
/// <param name="Quote">
/// The quoted amounts and per-tier fees. <b>An estimate.</b> A cooperative-exit quote lives about a minute, so
/// the engine re-quotes on confirm and re-checks the fee guard against the new number.
/// </param>
/// <param name="CrossChainQuote">
/// The live cross-chain quote, for a store sweeping to an EVM address. Mutually exclusive with
/// <paramref name="Quote"/> — a sweep is on one rail or the other — and set instead of it so a surface cannot
/// read a cooperative-exit fee off a cross-chain sweep.
/// </param>
/// <param name="Amount">
/// What would be sent, carrying its unit. Satoshi for a cooperative exit or a Bitcoin-funded cross-chain sweep;
/// token base units when Stable Balance is funding it. Null when there would be no sweep.
/// </param>
public sealed record SweepPreview(
    string? RefusalReason,
    SweepDestination? Destination,
    long BalanceSats,
    long SweepableSats,
    SparkOnchainQuote? Quote,
    SweepSettings Settings,
    SparkCrossChainQuote? CrossChainQuote = null,
    SparkSendAmount? Amount = null)
{
    public bool CanSweep =>
        RefusalReason is null && Destination is not null && (Quote is not null || CrossChainQuote is not null);
}

/// <summary>
/// The sweep engine: turns a store's Spark balance into a cooperative exit to its own on-chain destination.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every sweep here is a cooperative exit</b> — <c>PrepareSendPayment</c>/<c>SendPayment</c> to a Bitcoin
/// address through the service provider. There is no unilateral-exit path in this class or anywhere sweeping
/// can reach; the plugin's only unilateral path is the experimental, gated flow on the Advanced page, entirely
/// separate from this engine. "Drain" means the SDK's
/// <c>FeePolicy.FeesIncluded</c> and nothing else.
/// </para>
/// <para>
/// <b>One path for automatic and manual sweeps.</b> <see cref="RunAsync"/> is the only way a sweep happens; the
/// "sweep now" button calls it with <see cref="SweepTrigger.Manual"/>. A manual trigger relaxes exactly two
/// things — the <see cref="SweepSettings.Enabled"/> switch and the balance threshold, both of which answer
/// "should I be looking?", a question the merchant has just answered by pressing the button. Every guard that
/// answers "is this safe and economic?" — the minimum sweep, the fee ceiling, the dust floor, the destination
/// rules — applies identically, and is enforced here rather than in the view.
/// </para>
/// <para><b>The sequence, and why it is in this order.</b></para>
/// <list type="number">
/// <item><description><b>Single-flight per store.</b> The scheduler makes no promise that a pass finishes before
/// the next tick, and a merchant can press "sweep now" during one. Two overlapping passes would each read the
/// same balance and each send it.</description></item>
/// <item><description><b>Resolve anything unresolved, before considering a new sweep.</b> A record left
/// <see cref="SweepRecordStatus.Pending"/> is a sweep whose fate is unknown, and the SDK adopts the idempotency
/// key as its own <c>Payment.id</c> — so <c>GetPayment(key)</c> answers definitively whether it happened. Until
/// it does, this pass sends nothing. Nothing is ever retried blind.</description></item>
/// <item><description><b><c>SyncWallet</c>, then read the balance.</b> The balance lagged a settled payment by
/// ~20 s in the funded run and stayed stale even through <c>GetInfo(ensureSynced: true)</c>; only an explicit
/// sync moved it. A threshold comparison against a stale balance is a sweep that either does not happen or is
/// the wrong size.</description></item>
/// <item><description><b>Economics before addresses.</b> The minimum-sweep floor is checked before a destination
/// is resolved, so a store that is not worth sweeping does not burn an address from its wallet on every
/// pass.</description></item>
/// <item><description><b>Quote, then check the guards.</b> Coop-exit fees are flat — 1,950–2,430 sats on regtest
/// regardless of amount — so the fee ceiling is the difference between a 1% sweep and a 40% one.</description></item>
/// <item><description><b>Persist the record, with its idempotency key, before the send.</b> This is the
/// crash-safety primitive. If the process dies anywhere after this line, step 2 of the next pass can find out
/// what happened.</description></item>
/// <item><description><b>Send, with the fee guard re-checked against the quote actually being committed
/// to.</b> The pre-flight quote from step 5 is an estimate that has since expired; the guard that matters runs
/// inside the send, on the real number, and vetoing it means nothing was sent.</description></item>
/// </list>
/// <para>
/// <b>Why this cannot disturb settlement.</b> The reconciliation task settles invoices from <c>Payment</c> rows,
/// never from balances or balance deltas — which is exactly why the funded run's balance drift is tolerable. This
/// engine reads a balance and writes only its own table, so a sweep cannot mask, consume or race a receive. The
/// converse also holds: a receive that has not been credited yet is simply not in the balance, so it is not swept
/// this pass.
/// </para>
/// </remarks>
public sealed class SparkSweepEngine
{
    /// <summary>
    /// How long a <see cref="SweepRecordStatus.Pending"/> record is given to become visible to the SDK before it
    /// is written off.
    /// </summary>
    /// <remarks>
    /// <c>GetPayment(key)</c> returning nothing is strong evidence the send never reached the service provider,
    /// but not instant evidence: the send may be in flight right now, on a call that cannot be cancelled and took
    /// 5–7 s to return in the funded run. So the record holds the store's sweeps back for this long, and only
    /// then is marked failed. The cost of being patient is a few minutes of L2 exposure; the cost of being hasty
    /// is a second sweep decided while the first is still moving.
    /// </remarks>
    internal static readonly TimeSpan UnresolvedGrace = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How far back the crash-recovery walk looks.
    /// </summary>
    /// <remarks>
    /// A <see cref="SweepRecordStatus.Sent"/> exit is already on-chain with a recorded txid; re-reading it
    /// forever to upgrade a label to "Confirmed" would be a growing cost for no benefit. A
    /// <see cref="SweepRecordStatus.Pending"/> row cannot reach this age in normal operation, because
    /// <see cref="UnresolvedGrace"/> resolves it within minutes.
    /// </remarks>
    internal static readonly TimeSpan ResolutionWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// How long an automatic refusal keeps folding onto one row before a fresh sighting starts a new one.
    /// </summary>
    /// <remarks>
    /// Bounds history growth for a store parked on a refusal to one row per day per reason — instead of one per
    /// pass, which is 720 a day — while still showing a condition that stopped and came back as two episodes rather
    /// than one endlessly-incrementing tally.
    /// </remarks>
    internal static readonly TimeSpan RefusalCoalescingWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// How much of the available balance a cross-chain sweep leaves behind for the provider's overpay, in
    /// basis points.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Without this, a cross-chain sweep can never succeed with the default configuration.</b> A cross-chain
    /// quote debits <em>more</em> than the amount it is asked for — the source leg is overpaid to absorb the
    /// provider's fee and slippage, measured at roughly <c>max(50 bps, ~50 sats)</c> — and the engine's own
    /// arithmetic asks for exactly the sweepable balance. The two are incompatible: a store with the default
    /// zero reserve would sit permanently on
    /// <see cref="SweepRefusalCode.CrossChainOverpayExceedsBalance"/>, having done nothing wrong.
    /// </para>
    /// <para>
    /// 100 bps, which is double the observed proportional pad and, at the 50,000-sat cross-chain floor, 500
    /// sats against a ~50-sat fixed one. The remainder stays on the balance and is swept next time.
    /// </para>
    /// <para>
    /// It is a <em>margin</em>, not the check. The quote's own <c>amountIn</c> is still compared against what is
    /// sweepable on the sats path, because a pad wider than this margin has to be refused rather than
    /// discovered at the send. On the token path that comparison is not available — the quote's sats-side
    /// fields were only ever observed for a sats-funded send, and guessing at their meaning for a token-funded
    /// one is the kind of assumption this wave exists to avoid — so there the margin is the whole defence.
    /// </para>
    /// </remarks>
    internal const long CrossChainHeadroomBps = 100;

    private readonly ISparkStoreSettingsStore _settingsStore;
    private readonly ISparkStoreRuntime _runtime;
    private readonly ISweepRecordStore _records;
    private readonly SweepDestinationResolver _destinations;
    private readonly CrossChainRouteResolver _routes;
    private readonly ICrossChainValueOracle _valueOracle;
    private readonly TimeProvider _timeProvider;
    private readonly ISweepTransactionLabeler _transactionLabeler;
    private readonly ILogger<SparkSweepEngine> _logger;

    /// <summary>
    /// Stores with a pass in progress. Membership is the lock; there is deliberately no queueing, because a pass
    /// that waits for another to finish would then act on a balance that other pass has just spent.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _running = new();

    public SparkSweepEngine(
        ISparkStoreSettingsStore settingsStore,
        ISparkStoreRuntime runtime,
        ISweepRecordStore records,
        SweepDestinationResolver destinations,
        CrossChainRouteResolver routes,
        ICrossChainValueOracle valueOracle,
        ISweepTransactionLabeler transactionLabeler,
        TimeProvider timeProvider,
        ILogger<SparkSweepEngine> logger)
    {
        _settingsStore = settingsStore;
        _runtime = runtime;
        _records = records;
        _destinations = destinations;
        _routes = routes;
        _valueOracle = valueOracle;
        _transactionLabeler = transactionLabeler;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Runs one sweep pass for a store. The only path by which a sweep happens.
    /// </summary>
    public async Task<SweepRunResult> RunAsync(
        string storeId,
        SweepTrigger trigger,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        if (!_running.TryAdd(storeId, 0))
        {
            // Not an error, and deliberately not queued: see the field's remarks.
            _logger.LogDebug("Store {StoreId}: a Spark sweep is already running; skipping this pass", storeId);
            return new SweepRunResult(
                SweepOutcomeKind.Skipped, "A sweep for this store is already running. Try again in a moment.");
        }

        try
        {
            return await RunGuardedAsync(storeId, trigger, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _running.TryRemove(storeId, out _);
        }
    }

    /// <summary>
    /// What a sweep would do right now, without consuming an address or sending anything.
    /// </summary>
    /// <remarks>
    /// Shares the decision rules with <see cref="RunAsync"/> rather than reimplementing them, so the
    /// confirmation page cannot promise something the engine would then refuse. It is still only a preview: the
    /// authoritative checks run inside the send.
    /// </remarks>
    public async Task<SweepPreview> PreviewAsync(string storeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        var stored = await _settingsStore.GetAsync(storeId).ConfigureAwait(false);
        var settings = stored?.Sweep ?? new SweepSettings();

        var sdk = await _runtime.GetSdkClientAsync(storeId).ConfigureAwait(false);
        if (sdk is null)
            return new SweepPreview(WalletNotRunning, null, 0, 0, null, settings);

        long balance;
        SparkNodeInfo info;
        try
        {
            // Synced here too. A preview that showed a stale balance would offer a sweep of the wrong size, and
            // the merchant would have no way to know.
            await sdk.SyncWalletAsync(cancellationToken).ConfigureAwait(false);
            info = await sdk.GetInfoAsync(ensureSynced: true, cancellationToken).ConfigureAwait(false);
            balance = info.BalanceSats;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Store {StoreId}: could not read its Spark balance to preview a sweep", storeId);
            return new SweepPreview(
                $"This store's Spark balance could not be read: {SparkErrors.Describe(ex)}",
                null, 0, 0, null, settings);
        }

        if (settings.DestinationMode is SweepDestinationMode.EvmAddress)
        {
            return await PreviewCrossChainAsync(
                    storeId, sdk, settings, balance,
                    ResolveStableToken(stored ?? new SparkSettings(), info), cancellationToken)
                .ConfigureAwait(false);
        }

        var plan = PlanSweep(settings, balance);
        if (plan.Refusal is { } planRefusal)
            return new SweepPreview(planRefusal.Message, null, balance, plan.SweepableSats, null, settings);

        // reserve: false — a merchant looking at a quote must not consume an address from their wallet.
        var resolution = await _destinations
            .ResolveAsync(storeId, settings, reserve: false, cancellationToken)
            .ConfigureAwait(false);
        if (resolution.Destination is not { } destination)
        {
            return new SweepPreview(
                resolution.RefusalReason, null, balance, plan.SweepableSats, null, settings);
        }

        try
        {
            var tiers = await sdk
                .QuoteOnchainSendAsync(
                    destination.Address, plan.AmountSats, settings.DrainWhenSweeping, cancellationToken)
                .ConfigureAwait(false);

            var quote = new SparkOnchainQuote(
                plan.AmountSats, tiers.FeeFor(ToSdkSpeed(settings.ConfirmationSpeed)),
                settings.DrainWhenSweeping, tiers);

            return new SweepPreview(
                ApproveQuote(settings, quote)?.Message, destination, balance, plan.SweepableSats, quote, settings,
                Amount: SparkSendAmount.FromSats(plan.AmountSats));
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex,
                "Store {StoreId}: could not quote a {AmountSats} sat cooperative exit for a preview",
                storeId, plan.AmountSats);
            // Shares DescribeQuoteFailure with the run, so a preview cannot describe the same failure differently.
            return new SweepPreview(
                DescribeQuoteFailure(ex).Message, destination, balance, plan.SweepableSats, null, settings);
        }
    }

    /// <summary>
    /// What a cross-chain sweep would do right now, without sending anything.
    /// </summary>
    /// <remarks>
    /// Every decision is taken by the same methods the run takes them with —
    /// <see cref="PlanCrossChainFunding"/>, <see cref="CrossChainRouteResolver"/> and
    /// <see cref="ApproveCrossChainQuote"/> — so the confirmation page cannot promise something the engine would
    /// then refuse, and cannot describe a refusal differently from the way it will be recorded.
    /// </remarks>
    private async Task<SweepPreview> PreviewCrossChainAsync(
        string storeId,
        ISparkSdkClient sdk,
        SweepSettings settings,
        long balance,
        SparkTokenBalance? stableToken,
        CancellationToken cancellationToken)
    {
        var resolution = await _destinations
            .ResolveAsync(storeId, settings, reserve: false, cancellationToken)
            .ConfigureAwait(false);

        if (resolution.Destination is not { } destination)
            return new SweepPreview(resolution.RefusalReason, null, balance, 0, null, settings);

        // Trigger.Manual: a preview is a merchant asking, so the balance threshold — which only answers "should
        // I be looking?" — must not turn into "there is nothing to show you".
        var funding = PlanCrossChainFunding(settings, balance, stableToken, SweepTrigger.Manual);
        if (funding.Refusal is { } refusal)
            return new SweepPreview(refusal.Message, destination, balance, funding.SweepableSats, null, settings);

        if (funding.Amount is not { } amount)
            return new SweepPreview(funding.Skip, destination, balance, funding.SweepableSats, null, settings);

        var route = await _routes
            .ResolveAsync(sdk, destination, settings, (amount as SparkSendAmount.Token)?.Identifier, cancellationToken)
            .ConfigureAwait(false);

        if (route.Route is not { } chosen)
        {
            return new SweepPreview(
                route.Refusal?.Message, destination, balance, funding.SweepableSats, null, settings,
                Amount: amount);
        }

        try
        {
            var quote = await sdk
                .QuoteCrossChainAsync(
                    chosen, destination.Address, amount, settings.EffectiveCrossChainSlippageBps, cancellationToken)
                .ConfigureAwait(false);

            return new SweepPreview(
                ApproveCrossChainQuote(settings, amount, quote, funding.SweepableSats)?.Message,
                destination, balance, funding.SweepableSats, null, settings, quote, amount);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex,
                "Store {StoreId}: could not quote a cross-chain sweep of {Amount} for a preview", storeId, amount);
            return new SweepPreview(
                DescribeCrossChainQuoteFailure(ex).Message, destination, balance, funding.SweepableSats, null,
                settings, Amount: amount);
        }
    }

    private async Task<SweepRunResult> RunGuardedAsync(
        string storeId,
        SweepTrigger trigger,
        CancellationToken cancellationToken)
    {
        var settings = await _settingsStore.GetAsync(storeId).ConfigureAwait(false);
        if (settings is null)
        {
            return new SweepRunResult(
                SweepOutcomeKind.Skipped, "Spark is not configured for this store, so there is nothing to sweep.");
        }

        // Coalesced, not dereferenced. The property initialiser only covers a settings blob with no "Sweep" key at
        // all; a blob containing an explicit `"Sweep": null` — which any hand edit, older serializer or restored
        // backup can produce — deserialises to null, and dereferencing it threw a NullReferenceException out of a
        // scheduler pass. PreviewAsync always coalesced; this is the asymmetry closed.
        var sweep = settings.Sweep ?? new SweepSettings();

        var sdk = await _runtime.GetSdkClientAsync(storeId).ConfigureAwait(false);
        if (sdk is null)
        {
            // A refusal rather than a skip: for a manual sweep the merchant is waiting for an answer, and for an
            // automatic one a wallet that will not start is a problem they need to see.
            return await RefuseAsync(
                    storeId, trigger, sweep,
                    new SweepRefusal(SweepRefusalCode.WalletNotRunning, WalletNotRunning),
                    null, 0, 0, 0, cancellationToken)
                .ConfigureAwait(false);
        }

        // Step 2: nothing new may be sent while an earlier sweep's fate is unknown.
        var unresolved = await ResolveUnresolvedAsync(storeId, sdk, cancellationToken).ConfigureAwait(false);
        if (unresolved is { } blocking)
        {
            _logger.LogInformation(
                "Store {StoreId}: sweep {IdempotencyKey} is still unresolved; not starting another",
                storeId, blocking.IdempotencyKey);
            return new SweepRunResult(
                SweepOutcomeKind.InFlight,
                "An earlier sweep has not resolved yet. Nothing new will be sent until Spark says whether it "
                + "went through.",
                blocking);
        }

        if (trigger is SweepTrigger.Automatic && !sweep.Enabled)
        {
            return new SweepRunResult(
                SweepOutcomeKind.Skipped, "Automatic sweeping is switched off for this store.");
        }

        long balance;
        SparkNodeInfo info;
        try
        {
            // Step 3. The sync is what makes the number current; see the class remarks.
            await sdk.SyncWalletAsync(cancellationToken).ConfigureAwait(false);
            info = await sdk.GetInfoAsync(ensureSynced: true, cancellationToken).ConfigureAwait(false);
            balance = info.BalanceSats;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Store {StoreId}: could not read its Spark balance for a sweep ({Reason})",
                storeId, SparkErrors.Describe(ex));
            return await RefuseAsync(
                    storeId, trigger, sweep,
                    new SweepRefusal(
                        SweepRefusalCode.BalanceUnreadable,
                        $"This store's Spark balance could not be read: {SparkErrors.Describe(ex)}"),
                    null, 0, 0, 0, cancellationToken)
                .ConfigureAwait(false);
        }

        // The stable-balance token this store holds, if any. Read here rather than at the send, because it
        // decides which rail the sweep runs on and therefore which unit everything below is in.
        var stableToken = ResolveStableToken(settings, info);

        if (sweep.DestinationMode is SweepDestinationMode.EvmAddress)
        {
            // A different rail with different arithmetic and, when funded from a token, a different unit and no
            // idempotency key. Branched before the sats plan is made rather than after, because a sats plan is
            // not merely unnecessary on that path — it is denominated in the wrong thing.
            return await RunCrossChainAsync(
                    storeId, sdk, trigger, sweep, balance, stableToken, cancellationToken)
                .ConfigureAwait(false);
        }

        // EffectiveBalanceThresholdSats, not the raw property: a settings blob written before this wave carries an
        // explicit zero, which would make every pass "above the threshold" and rely on the economic floor to say no.
        var threshold = sweep.EffectiveBalanceThresholdSats;
        if (trigger is SweepTrigger.Automatic && balance <= threshold)
        {
            return new SweepRunResult(
                SweepOutcomeKind.Skipped,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The Spark balance of {0:N0} sat has not passed the {1:N0} sat sweep threshold.",
                    balance, threshold));
        }

        // Step 4.
        var plan = PlanSweep(sweep, balance);
        if (plan.Refusal is { } planRefusal)
        {
            // The one interaction between the two post-MVP features that would otherwise look like a bug. With
            // Stable Balance active the sats balance has been converted away, so a cooperative exit finds
            // nothing to send and refuses on the economic floor — truthfully, and without ever mentioning the
            // stablecoin sitting next to it. A merchant reading "only 400 sat is sweepable" while holding $300
            // of USDB has no way to connect the two.
            if (stableToken is { } holding && holding.BaseUnits > BigInteger.Zero)
            {
                planRefusal = new SweepRefusal(
                    SweepRefusalCode.StableBalanceHoldsTheFunds,
                    $"This store holds {holding.Describe()} in Stable Balance, and only "
                    + string.Format(CultureInfo.InvariantCulture, "{0:N0} sat", plan.SweepableSats)
                    + " on the Bitcoin balance — which is below the sweep minimum. Sweeps to a Bitcoin "
                    + "destination can only send the Bitcoin balance. Either switch the sweep destination to a "
                    + "cross-chain address, or turn Stable Balance off to convert back to Bitcoin.");
            }

            return await RefuseAsync(
                    storeId, trigger, sweep, planRefusal, null, balance, plan.AmountSats, 0, cancellationToken)
                .ConfigureAwait(false);
        }

        // reserve: true — this is a real sweep, so the address is reserved and labelled, and therefore rotated.
        var resolution = await _destinations
            .ResolveAsync(storeId, sweep, reserve: true, cancellationToken)
            .ConfigureAwait(false);
        if (resolution.Destination is not { } destination)
        {
            return await RefuseAsync(
                    storeId, trigger, sweep,
                    new SweepRefusal(
                        SweepRefusalCode.NoDestination,
                        resolution.RefusalReason ?? "This store has no usable sweep destination."),
                    null, balance, plan.AmountSats, 0, cancellationToken)
                .ConfigureAwait(false);
        }

        // Step 5. A pre-flight quote, so an economic or dust refusal is decided before a record exists — which is
        // what lets refusals be de-duplicated instead of accumulating a row every couple of minutes. It costs a
        // second `PrepareSendPayment`; the quote the send commits to is its own, obtained moments later.
        SparkOnchainQuote quote;
        try
        {
            var tiers = await sdk
                .QuoteOnchainSendAsync(
                    destination.Address, plan.AmountSats, sweep.DrainWhenSweeping, cancellationToken)
                .ConfigureAwait(false);
            quote = new SparkOnchainQuote(
                plan.AmountSats, tiers.FeeFor(ToSdkSpeed(sweep.ConfirmationSpeed)),
                sweep.DrainWhenSweeping, tiers);
        }
        catch (Exception ex)
        {
            var refusal = DescribeQuoteFailure(ex);

            _logger.LogWarning(ex,
                "Store {StoreId}: could not quote a {AmountSats} sat cooperative exit ({Reason})",
                storeId, plan.AmountSats, SparkErrors.Describe(ex));
            return await RefuseAsync(
                    storeId, trigger, sweep, refusal, destination, balance, plan.AmountSats, 0, cancellationToken)
                .ConfigureAwait(false);
        }

        if (ApproveQuote(sweep, quote) is { } guardRefusal)
        {
            return await RefuseAsync(
                    storeId, trigger, sweep, guardRefusal, destination, balance, plan.AmountSats, quote.FeeSats,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // Step 6, and the point of no return: from here on the sweep may have happened, so it must be on record
        // with the key that can prove it either way.
        var now = _timeProvider.GetUtcNow();
        var record = new SweepRecord
        {
            IdempotencyKey = Guid.NewGuid().ToString(),
            StoreId = storeId,
            DestinationAddress = destination.Address,
            DestinationMode = destination.Mode,
            AmountSats = plan.AmountSats,
            FeesIncluded = sweep.DrainWhenSweeping,
            ConfirmationSpeed = sweep.ConfirmationSpeed,
            QuotedFeeSats = quote.FeeSats,
            BalanceAtDecisionSats = balance,
            Trigger = trigger,
            Status = SweepRecordStatus.Pending,
            CreatedAt = now,
            AttemptCount = 1
        };

        await _records.AddAsync(record, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Store {StoreId}: sweeping {AmountSats} sat to {Destination} by cooperative exit "
            + "(quoted fee {FeeSats} sat, {Speed}, feesIncluded={FeesIncluded}, key {IdempotencyKey})",
            storeId, plan.AmountSats, destination.Address, quote.FeeSats, sweep.ConfirmationSpeed,
            sweep.DrainWhenSweeping, record.IdempotencyKey);

        return await SendAsync(storeId, sdk, sweep, record, cancellationToken).ConfigureAwait(false);
    }

    #region Cross-chain

    /// <summary>
    /// One cross-chain sweep pass, from the balance read onwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same sequence as the cooperative-exit path, with three things that are genuinely different.</b>
    /// Everything before the send is shared in spirit — single-flight, resolve-before-sending, sync before
    /// reading, economics before addresses, quote then guard, persist then send — and is re-stated here rather
    /// than shared in code because each step's <em>arithmetic</em> differs:
    /// </para>
    /// <list type="number">
    /// <item><description><b>The unit.</b> With Stable Balance active the sweep is funded from a token balance
    /// and its amount is in token base units; otherwise it is satoshi. The two are carried in
    /// <see cref="SparkSendAmount"/> so nothing downstream can confuse them, and the economic floor is chosen
    /// to match — a sats floor is meaningless against a token amount and there is no local exchange rate to
    /// convert one into the other.</description></item>
    /// <item><description><b>The quote debits more than it is asked for.</b> The provider overpays the source
    /// leg to absorb its fee and slippage, so the balance check is against <c>amountIn</c> and not against the
    /// amount. A sweep sized to exactly the sweepable balance fails.</description></item>
    /// <item><description><b>The crash-safety primitive changes.</b> A sats-funded send still carries an
    /// idempotency key. A token-funded one <em>cannot</em> — the SDK rejects the key outright — so the
    /// provider's quote id is persisted before the send instead, and recovery scans for it rather than
    /// re-sending. See <see cref="ResolveByQuoteIdAsync"/>.</description></item>
    /// </list>
    /// </remarks>
    private async Task<SweepRunResult> RunCrossChainAsync(
        string storeId,
        ISparkSdkClient sdk,
        SweepTrigger trigger,
        SweepSettings sweep,
        long balanceSats,
        SparkTokenBalance? stableToken,
        CancellationToken cancellationToken)
    {
        // Nothing is reserved or consumed by resolving an EVM destination — it is one address the merchant
        // configured — so this runs before the economics rather than after, and gives a clearer refusal for a
        // store that has switched mode without filling the address in.
        var resolution = await _destinations
            .ResolveAsync(storeId, sweep, reserve: false, cancellationToken)
            .ConfigureAwait(false);

        if (resolution.Destination is not { } destination)
        {
            return await RefuseAsync(
                    storeId, trigger, sweep,
                    new SweepRefusal(
                        SweepRefusalCode.NoDestination,
                        resolution.RefusalReason ?? "This store has no usable cross-chain sweep destination."),
                    null, balanceSats, 0, 0, cancellationToken)
                .ConfigureAwait(false);
        }

        var funding = PlanCrossChainFunding(sweep, balanceSats, stableToken, trigger);
        if (funding.Refusal is { } fundingRefusal)
        {
            return await RefuseAsync(
                    storeId, trigger, sweep, fundingRefusal, destination, balanceSats, 0, 0, cancellationToken)
                .ConfigureAwait(false);
        }

        if (funding.Skip is { } skipReason)
            return new SweepRunResult(SweepOutcomeKind.Skipped, skipReason);

        var amount = funding.Amount!;

        var route = await _routes
            .ResolveAsync(sdk, destination, sweep, (amount as SparkSendAmount.Token)?.Identifier, cancellationToken)
            .ConfigureAwait(false);

        if (route.Route is not { } chosen)
        {
            return await RefuseAsync(
                    storeId, trigger, sweep,
                    route.Refusal ?? new SweepRefusal(
                        SweepRefusalCode.NoCrossChainRoute, "No cross-chain route is available."),
                    destination, balanceSats, 0, 0, cancellationToken)
                .ConfigureAwait(false);
        }

        // A pre-flight quote, for the same reason the exit path has one: it lets an economic refusal be decided
        // and de-duplicated before a record exists.
        SparkCrossChainQuote quote;
        try
        {
            quote = await sdk
                .QuoteCrossChainAsync(
                    chosen, destination.Address, amount, sweep.EffectiveCrossChainSlippageBps, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var refusal = DescribeCrossChainQuoteFailure(ex);
            _logger.LogWarning(ex,
                "Store {StoreId}: could not quote a cross-chain sweep of {Amount} to {Chain} ({Reason})",
                storeId, amount, chosen.Chain, SparkErrors.Describe(ex));

            return await RefuseAsync(
                    storeId, trigger, sweep, refusal, destination, balanceSats, 0, 0, cancellationToken)
                .ConfigureAwait(false);
        }

        var valueRefusal = await ApproveCrossChainValueAsync(storeId, amount, quote, cancellationToken)
            .ConfigureAwait(false);
        if (valueRefusal is null)
            valueRefusal = ApproveCrossChainQuote(sweep, amount, quote, funding.SweepableSats);

        if (valueRefusal is { } guardRefusal)
        {
            return await RefuseAsync(
                    storeId, trigger, sweep, guardRefusal, destination, balanceSats,
                    amount is SparkSendAmount.Bitcoin bitcoinAmount ? bitcoinAmount.Sats : 0,
                    0, cancellationToken)
                .ConfigureAwait(false);
        }

        // The point of no return, and the one place the token path's recovery story is established: the
        // provider's quote id goes on the row BEFORE the send, because on that path it is the only thing that
        // will identify this send afterwards.
        var now = _timeProvider.GetUtcNow();
        var tokenAmount = amount as SparkSendAmount.Token;
        var record = new SweepRecord
        {
            IdempotencyKey = Guid.NewGuid().ToString(),
            StoreId = storeId,
            DestinationAddress = destination.Address,
            DestinationMode = SweepDestinationMode.EvmAddress,
            DestinationKind = SweepDestinationKind.EvmAddress,
            DestinationChain = chosen.Chain,
            DestinationAsset = chosen.Asset,
            DestinationAssetDecimals = (int)chosen.Decimals,
            Provider = chosen.Provider,
            ProviderQuoteId = quote.ProviderQuoteId,
            IdempotencyKeyAccepted = amount.SupportsIdempotencyKey,
            AmountSats = amount is SparkSendAmount.Bitcoin sats ? sats.Sats : 0,
            SourceTokenIdentifier = tokenAmount?.Identifier.Value,
            SourceAmountBaseUnits = tokenAmount?.BaseUnits.ToString(CultureInfo.InvariantCulture),
            SourceTokenDecimals = (int)(tokenAmount?.Decimals ?? 0),
            EstimatedOutBaseUnits = quote.EstimatedOut.ToString(CultureInfo.InvariantCulture),
            ConversionStatus = SparkConversionStatus.Pending,
            // The sats-side cost of a sats-funded sweep is the overpay: what left the wallet beyond what was
            // asked for. Zero for a token-funded one, where there is no verified sats figure to record.
            QuotedFeeSats = amount is SparkSendAmount.Bitcoin requested
                ? Math.Max(0, quote.AmountInSats - requested.Sats)
                : 0,
            // The fee policy is not applicable: it is documented as ignored on cross-chain conversion sends.
            FeesIncluded = false,
            ConfirmationSpeed = sweep.ConfirmationSpeed,
            BalanceAtDecisionSats = balanceSats,
            Trigger = trigger,
            Status = SweepRecordStatus.Pending,
            CreatedAt = now,
            AttemptCount = 1
        };

        await _records.AddAsync(record, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Store {StoreId}: sweeping {Amount} to {Address} as {Asset} on {Chain} via {Provider} "
            + "(quote {QuoteId}, about {EstimatedOut} expected, idempotency key {KeyState}, record {Key})",
            storeId, amount, destination.Address, chosen.Asset, chosen.Chain, chosen.Provider,
            quote.ProviderQuoteId ?? "unknown", quote.DescribeEstimatedOut(),
            amount.SupportsIdempotencyKey ? "accepted" : "rejected by the SDK on a token leg", record.IdempotencyKey);

        return await SendCrossChainAsync(
                storeId, sdk, sweep, record, chosen, amount, funding.SweepableSats, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Issues the cross-chain send for a record that is already persisted, and records what came back.
    /// </summary>
    /// <remarks>
    /// <b>There is no retry loop here, unlike the cooperative-exit send.</b> That loop exists because an expired
    /// exit quote can be re-sent safely under the same idempotency key. A token-funded cross-chain send has no
    /// key, so a second attempt is a second payment rather than a replay of the first — the SDK's own guidance
    /// on that path is to debounce at the application layer until it returns a payment or a terminal error. So
    /// an expired quote is reported and the next pass starts over from a fresh quote, having first resolved this
    /// record. Re-quoting inside the send would defeat the record's purpose: it would produce a second provider
    /// quote id that nothing had written down.
    /// </remarks>
    private async Task<SweepRunResult> SendCrossChainAsync(
        string storeId,
        ISparkSdkClient sdk,
        SweepSettings settings,
        SweepRecord record,
        SparkCrossChainRoute route,
        SparkSendAmount amount,
        long sweepableSats,
        CancellationToken cancellationToken)
    {
        SweepRefusal? committedRefusal = null;

        async Task<string?> Approve(SparkCrossChainQuote quote)
        {
            // Re-evaluated against the quote the send is actually committing to, exactly as on the exit path:
            // the pre-flight quote is by now seconds old and has its own ~60 s expiry, and this callback is the
            // enforcement point.
            committedRefusal =
                await ApproveCrossChainValueAsync(storeId, amount, quote, cancellationToken).ConfigureAwait(false)
                ?? ApproveCrossChainQuote(settings, amount, quote, sweepableSats);

            if (committedRefusal is not null)
                return committedRefusal.Message;

            // The committed quote id, written before the send proceeds.
            //
            // This is not belt and braces, it is the whole crash-recovery story on the token path. The id put on
            // the row at insert time came from the *pre-flight* quote; the SDK prepares again inside the send,
            // and Orchestra mints a fresh quote id per prepare. Recovery scans payments for this row's id, so
            // leaving the pre-flight one there means the scan can never match — and a sweep that delivered USDT
            // would be written off as never sent, then dropped from the unresolved list so its delivery was
            // never polled either.
            //
            // Inside the approval callback because this is the only moment both facts hold: the committed quote
            // exists, and nothing has been sent yet.
            var committedQuoteId = quote.ProviderQuoteId;

            // No quote id at all, on the rail where the quote id is the only recovery handle there is.
            //
            // Raised by an external audit (H-2). The block below only fires when there *is* an id to write, so a
            // committed prepare that came back without one skipped the whole thing and the send went out anyway
            // — irreversible, cross-chain, and with nothing to match it against afterwards. Recovery would then
            // log "whether it sent cannot be established" and close the row without re-sending, which is the
            // correct thing to do with no handle and a terrible thing to have arranged on purpose.
            //
            // The refusal below is the same doctrine as the one for a failed write, applied to the case where
            // there is nothing to write: a payment nothing has a record of is worse than no payment. The code
            // honoured it in one of those two cases and not the other.
            //
            // **Only on the token rail.** A sats-funded cross-chain send carries an idempotency key, the SDK
            // deduplicates on it, and recovery looks the payment up by it directly — a missing provider quote id
            // there costs a little reporting detail and nothing else, so refusing on it would turn a working
            // send into a refused one for no gain. `SupportsIdempotencyKey` is the same predicate that decides
            // whether a key is passed to the SDK at all, a few lines below, so the two cannot disagree about
            // which rail this is.
            if (string.IsNullOrEmpty(committedQuoteId) && !amount.SupportsIdempotencyKey)
            {
                _logger.LogError(
                    "Store {StoreId}: the committed cross-chain quote for sweep {Key} carries no provider quote "
                    + "id, and a token-funded send accepts no idempotency key, so nothing would identify the "
                    + "payment afterwards; refusing the send rather than issuing one that cannot be recovered",
                    storeId, record.IdempotencyKey);

                committedRefusal = new SweepRefusal(
                    SweepRefusalCode.QuoteFailed,
                    "Spark committed to a quote that carries no reference of its own, and a sweep funded from "
                    + "a stablecoin balance cannot be identified any other way. It was not sent, because a "
                    + "payment that could never be traced is worse than one that never happened. Nothing "
                    + "moved.");
                return committedRefusal.Message;
            }

            if (!string.IsNullOrEmpty(committedQuoteId) &&
                !string.Equals(committedQuoteId, record.ProviderQuoteId, StringComparison.Ordinal))
            {
                if (await _records
                        .TryRecordProviderQuoteAsync(storeId, record.IdempotencyKey, committedQuoteId, cancellationToken)
                        .ConfigureAwait(false))
                {
                    record.ProviderQuoteId = committedQuoteId;
                }
                else
                {
                    // The row is no longer pending, so something else has resolved it and this send must not
                    // proceed — sending now would produce a payment nothing has a record of.
                    _logger.LogError(
                        "Store {StoreId}: could not record the committed provider quote for sweep {Key}; "
                        + "refusing the send rather than issuing one that cannot be recovered",
                        storeId, record.IdempotencyKey);

                    committedRefusal = new SweepRefusal(
                        SweepRefusalCode.QuoteFailed,
                        "This sweep could not be recorded against the quote it was about to commit to, so it "
                        + "was not sent. Nothing moved.");
                    return committedRefusal.Message;
                }
            }

            return null;
        }

        try
        {
            var result = await sdk
                .SendCrossChainAsync(
                    route,
                    record.DestinationAddress,
                    amount,
                    settings.EffectiveCrossChainSlippageBps,
                    // Null on the token path, and this is not an omission: the SDK rejects a key on any send
                    // with a token transfer leg, so passing one fails the send outright. The client asserts the
                    // pairing rather than quietly dropping it.
                    amount.SupportsIdempotencyKey ? record.IdempotencyKey : null,
                    Approve,
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.RejectedReason is { } rejected)
            {
                return await ResolveAsync(
                        record, SweepOutcomeKind.Refused, SweepRecordStatus.Refused,
                        feeSats: null, txId: null, error: rejected, reason: rejected, cancellationToken,
                        refusalCode: committedRefusal?.Code ?? SweepRefusalCode.FeeAboveLimit)
                    .ConfigureAwait(false);
            }

            if (result.Payment is not { } payment)
            {
                _logger.LogError(
                    "Store {StoreId}: cross-chain sweep {Key} returned neither a payment nor a refusal",
                    storeId, record.IdempotencyKey);
                return new SweepRunResult(
                    SweepOutcomeKind.Unresolved,
                    "Spark gave no answer for this cross-chain sweep. It will be checked again shortly.",
                    record);
            }

            var conversion = payment.Conversion;
            var status = payment.Status switch
            {
                SparkPaymentStatus.Failed => SweepRecordStatus.Failed,
                // Deliberately not Confirmed on a Completed payment. The *payment* completing means the source
                // leg reached the provider; delivery on the destination chain is a separate thing the provider
                // reports later, through no event, in deliveredAmount. Calling that confirmed would tell a
                // merchant their USDT had arrived when it may not have left Arbitrum's queue.
                _ => SweepRecordStatus.Sent
            };

            var reason = status is SweepRecordStatus.Failed
                ? "Spark reported this cross-chain send as failed."
                  // No leading "About": DescribeDelivered already says "about …" while the amount is an
                  // estimate, and saying it twice read as "About about 38.16 USDT" on the first mainnet sweep.
                : $"Cross-chain sweep of {amount} accepted via {route.Provider}. Expecting "
                  + $"{FormatEstimate(record)} at {record.DestinationAddress} on {route.Chain}. "
                  + "Delivery is reported by the provider rather than by an event, so it is checked on the next "
                  + "pass.";

            return await ResolveAsync(
                    record,
                    status is SweepRecordStatus.Failed ? SweepOutcomeKind.Failed : SweepOutcomeKind.Swept,
                    status,
                    feeSats: null,
                    txId: null,
                    error: status is SweepRecordStatus.Failed ? reason : null,
                    reason,
                    cancellationToken,
                    conversionStatus: conversion?.Status ?? SparkConversionStatus.Pending,
                    deliveredBaseUnits: conversion?.DeliveredAmount?.ToString(CultureInfo.InvariantCulture),
                    // The SDK's own payment id for a token-leg send is not our key, so it is captured here as
                    // the second thing recovery can match on.
                    providerOrderId: conversion?.ProviderOrderId)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (SparkErrors.IsInsufficientFunds(ex) || IsProvablyNotSent(ex))
        {
            var reason = SparkErrors.IsInsufficientFunds(ex)
                ? "Spark reported insufficient funds for this cross-chain sweep. A cross-chain quote debits "
                  + "more than the amount it is asked for, to absorb the provider's fee and slippage, so the "
                  + "balance has to cover the larger figure."
                : $"Spark rejected this cross-chain sweep: {SparkErrors.Describe(ex)}";

            _logger.LogWarning(ex,
                "Store {StoreId}: cross-chain sweep {Key} was rejected before sending ({Reason})",
                storeId, record.IdempotencyKey, SparkErrors.Describe(ex));

            return await ResolveAsync(
                    record, SweepOutcomeKind.Failed, SweepRecordStatus.Failed, null, null, reason, reason,
                    cancellationToken, conversionStatus: SparkConversionStatus.Failed)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Left Pending on purpose, and — on the token path — this is the case the whole quote-id scheme
            // exists for. The record already carries the provider quote id, so the next pass can find out
            // whether the send happened by scanning payments for it, rather than by re-sending something that
            // nothing would deduplicate.
            _logger.LogError(ex,
                "Store {StoreId}: cross-chain sweep {Key} failed with an unknown outcome ({Reason}). It will be "
                + "resolved against {Matcher} on the next pass",
                storeId, record.IdempotencyKey, SparkErrors.Describe(ex),
                record.IdempotencyKeyAccepted
                    ? "the payment under its idempotency key"
                    : $"provider quote {record.ProviderQuoteId ?? "(none recorded)"}");

            return new SweepRunResult(
                SweepOutcomeKind.Unresolved,
                "This cross-chain sweep's outcome is not yet known. Nothing further will be sent for this store "
                + "until Spark answers — a cross-chain send funded from a stablecoin balance cannot be retried "
                + "safely, so it is never re-sent blind.",
                record);
        }
    }

    /// <summary>
    /// What a cross-chain sweep is funded from and how much of it.
    /// </summary>
    /// <param name="Amount">The amount, carrying its unit. Null when there is nothing to sweep.</param>
    /// <param name="SweepableSats">
    /// The sats available to a sats-funded sweep, for the overpay check. Zero on the token path, where the
    /// sats-side arithmetic does not apply.
    /// </param>
    /// <param name="Skip">Set when this pass should quietly do nothing — a threshold, not a problem.</param>
    private sealed record CrossChainFunding(
        SparkSendAmount? Amount,
        long SweepableSats,
        SweepRefusal? Refusal = null,
        string? Skip = null);

    /// <summary>
    /// Decides which balance funds a cross-chain sweep, and how much of it, in that balance's own unit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Stable Balance decides the rail, not the size of the balance.</b> If the store holds the configured
    /// stable token, the sweep is funded from it. There is deliberately no fallback to the sats balance when the
    /// token balance is small: the sats sitting alongside it are there to be converted, and sweeping them
    /// instead would move money the merchant has asked to be held in dollars — and would do it in a different
    /// unit, which is the mistake this whole design is arranged to prevent.
    /// </para>
    /// <para>
    /// The reserve is a satoshi concept — it exists so the wallet can keep receiving cheaply — and is not
    /// applied to a token balance, which has no such role.
    /// </para>
    /// </remarks>
    private static CrossChainFunding PlanCrossChainFunding(
        SweepSettings settings,
        long balanceSats,
        SparkTokenBalance? stableToken,
        SweepTrigger trigger)
    {
        if (stableToken is { } token && token.BaseUnits > BigInteger.Zero)
        {
            var decimals = token.Decimals;
            var floor = BigInteger.Multiply(
                settings.EffectiveCrossChainMinimumStableUnits,
                BigInteger.Pow(10, (int)Math.Min(decimals, 30)));

            if (token.BaseUnits < floor)
            {
                return new CrossChainFunding(null, 0, new SweepRefusal(
                    SweepRefusalCode.BelowMinimumSweep,
                    $"Only {token.Describe()} is held in Stable Balance, below this store's minimum of "
                    + $"{settings.EffectiveCrossChainMinimumStableUnits:N0} {token.Ticker}. A cross-chain send "
                    + "has a fixed fee component, so sending less is poor value."));
            }

            // Slightly less than everything: the provider overpays the source leg and there is no verified
            // sats-side figure to size that against on this path. See CrossChainHeadroomBps.
            var requested = token.BaseUnits * (10_000 - CrossChainHeadroomBps) / 10_000;
            if (requested <= BigInteger.Zero)
            {
                return new CrossChainFunding(null, 0, new SweepRefusal(
                    SweepRefusalCode.BelowMinimumSweep,
                    $"There is not enough of {token.Ticker} to send after leaving room for the provider's fee."));
            }

            return new CrossChainFunding(
                SparkSendAmount.FromTokenBaseUnits(token.Identifier, requested, decimals), 0);
        }

        // Sats-funded: the same threshold and reserve rules as a cooperative exit, with the cross-chain floor.
        var threshold = settings.EffectiveBalanceThresholdSats;
        if (trigger is SweepTrigger.Automatic && balanceSats <= threshold)
        {
            return new CrossChainFunding(null, 0, Skip: string.Format(
                CultureInfo.InvariantCulture,
                "The Spark balance of {0:N0} sat has not passed the {1:N0} sat sweep threshold.",
                balanceSats, threshold));
        }

        var plan = PlanSweep(settings, balanceSats);
        if (plan.Refusal is { } refusal)
            return new CrossChainFunding(null, plan.SweepableSats, refusal);

        // The same margin as the token path, and needed for the same reason: the amount a cooperative exit
        // would ask for is the whole sweepable balance, and a cross-chain quote debits more than it is asked
        // for. Asking for all of it is asking for a refusal. Note the guard still runs against the quote — this
        // only stops the ordinary case from tripping it.
        var requestedSats = plan.AmountSats * (10_000 - CrossChainHeadroomBps) / 10_000;
        if (requestedSats <= 0)
        {
            return new CrossChainFunding(null, plan.SweepableSats, new SweepRefusal(
                SweepRefusalCode.BelowMinimumSweep,
                "There is not enough to send after leaving room for the provider's fee."));
        }

        return new CrossChainFunding(SparkSendAmount.FromSats(requestedSats), plan.SweepableSats);
    }

    /// <summary>
    /// The cross-chain fee and balance guards. Returns null to proceed, or the reason to refuse.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The fee is measured as a ratio inside the destination asset</b>, as
    /// <c>(assetAmountIn − estimatedOut) / assetAmountIn</c>. That is deliberate: the quote reports its fee in
    /// destination-asset base units and its service fee in a third asset again (<c>USDC</c> was observed on a
    /// <c>USDT</c> route), so adding the two figures would be adding different currencies. A ratio of gross to
    /// net is unit-free, is the number a merchant would compute themselves, and works identically whichever
    /// balance funded the sweep — which is what lets one guard cover both units.
    /// </para>
    /// <para>
    /// The store's existing <see cref="SweepSettings.MaxFeePercent"/> is reused rather than a new setting
    /// added, because it answers the same question, and the same
    /// <see cref="SweepSettings.HardMaxFeePercent"/> backstop applies for the same reason: there must be no
    /// configuration in which the plugin will pay an unbounded fee.
    /// </para>
    /// <para>
    /// The two sats-side checks — that the debit fits, and the flat ceiling — apply only to a sats-funded
    /// sweep. On the token path <c>amountIn</c>'s meaning was never verified for a token source, and asserting
    /// against a number whose unit is a guess would be worse than not asserting.
    /// </para>
    /// </remarks>
    internal static SweepRefusal? ApproveCrossChainQuote(
        SweepSettings settings,
        SparkSendAmount amount,
        SparkCrossChainQuote quote,
        long sweepableSats)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentNullException.ThrowIfNull(quote);

        if (quote.EstimatedOut <= BigInteger.Zero)
        {
            return new SweepRefusal(
                SweepRefusalCode.CrossChainQuoteUnusable,
                "The cross-chain quote says nothing would arrive at the destination. Nothing was sent.");
        }

        if (quote.AssetAmountIn <= BigInteger.Zero || quote.EstimatedOut > quote.AssetAmountIn)
        {
            // Gross below net is not a cheap quote, it is a quote that cannot be read — and reading it anyway
            // would compute a negative fee and pass every ceiling.
            return new SweepRefusal(
                SweepRefusalCode.CrossChainQuoteUnusable,
                "The cross-chain quote does not make sense: it reports delivering at least as much as it takes "
                + "in. Nothing was sent.");
        }

        if (amount is SparkSendAmount.Bitcoin bitcoin)
        {
            // The check a sweep sized to the whole balance would fail on. The quote debits amountIn, not the
            // amount asked for, because the source leg is overpaid to absorb the fee and slippage.
            if (quote.AmountInSats > sweepableSats)
            {
                return new SweepRefusal(SweepRefusalCode.CrossChainOverpayExceedsBalance, string.Format(
                    CultureInfo.InvariantCulture,
                    "This sweep would debit {0:N0} sat — more than the {1:N0} sat asked for, because the "
                    + "provider overpays the source to cover its fee and slippage — but only {2:N0} sat is "
                    + "sweepable. Nothing was sent.",
                    quote.AmountInSats, bitcoin.Sats, sweepableSats));
            }

            var overpaySats = Math.Max(0, quote.AmountInSats - bitcoin.Sats);
            if (settings.MaxFeeFlatSats is { } flat && flat >= 0 && overpaySats > flat)
            {
                return new SweepRefusal(SweepRefusalCode.FeeAboveLimit, string.Format(
                    CultureInfo.InvariantCulture,
                    "This sweep would cost {0:N0} sat over the {1:N0} sat being sent, above the {2:N0} sat "
                    + "limit this store allows.",
                    overpaySats, bitcoin.Sats, flat));
            }
        }

        var feePercent = (double)(quote.AssetAmountIn - quote.EstimatedOut) * 100d / (double)quote.AssetAmountIn;

        var maxPercent = settings.MaxFeePercent > 0
            ? settings.MaxFeePercent
            : SweepSettings.DefaultMaxFeePercent;

        // The backstop no configuration can lift, matching the cooperative-exit guard.
        maxPercent = Math.Min(maxPercent, SweepSettings.HardMaxFeePercent);

        if (feePercent <= maxPercent)
            return null;

        return new SweepRefusal(SweepRefusalCode.FeeAboveLimit, string.Format(
            CultureInfo.InvariantCulture,
            "The cross-chain fee is {0:0.##}% of this sweep, above the {1:0.##}% limit this store allows. "
            + "Sweep a larger amount or raise the limit.",
            feePercent, maxPercent));
    }

    /// <summary>
    /// Checks that what the destination receives is worth roughly what leaves the wallet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The guard <see cref="ApproveCrossChainQuote"/> structurally cannot be.</b> That one compares
    /// <c>assetAmountIn</c> against <c>estimatedOut</c> — both from the same quote and in the same asset — so it
    /// bounds the spread the provider <em>states</em> and never the rate it applies. A quote offering $100 of
    /// USDT for 500,000 satoshi reports a 0.34% spread, clears every ceiling, and loses about 68% of the
    /// merchant's money. Only a price from outside the quote can see that.
    /// </para>
    /// <para>
    /// The two funding rails need different work. A <b>token</b>-funded sweep is USD-pegged on both sides, so
    /// the check is rate-free arithmetic. A <b>satoshi</b>-funded one needs a BTC price, which comes from the
    /// store's own rate rules — the price the merchant already trusts for their invoices.
    /// </para>
    /// <para>
    /// <b>No rate means no sweep.</b> Allowing one through unchecked would make the guard bypassable by any
    /// rate-provider hiccup, which is precisely when a bad quote is hardest to notice. Refusing costs nothing:
    /// sweeping is automatic, a refusal is a designed steady state, and the next pass tries again.
    /// </para>
    /// <para>
    /// The band is deliberately wide. This is a check against catastrophic mispricing, not an economic tuner —
    /// the spread guard already does that job with the merchant's own ceiling — and it has to absorb the peg
    /// drift, exchange spread and rate staleness that are all entirely normal.
    /// </para>
    /// </remarks>
    private async Task<SweepRefusal?> ApproveCrossChainValueAsync(
        string storeId,
        SparkSendAmount amount,
        SparkCrossChainQuote quote,
        CancellationToken cancellationToken)
    {
        var asset = quote.Route.Asset;
        if (!UsdStablecoins.Contains(asset))
        {
            // Refused rather than skipped. The plugin only advertises USD stablecoins, and an asset whose value
            // it cannot establish is one whose quote it cannot sanity-check — which is the one thing standing
            // between a merchant and a catastrophically mispriced quote.
            return new SweepRefusal(
                SweepRefusalCode.CrossChainValueUnverifiable, string.Format(
                    CultureInfo.InvariantCulture,
                    "This plugin cannot establish what {0} is worth, so it cannot check that this sweep is "
                    + "priced sensibly and will not send it. It supports {1}.",
                    asset, string.Join(", ", UsdStablecoins)));
        }

        // What arrives, in dollars.
        var deliveredUsd = ToDecimal(quote.EstimatedOut, quote.Route.Decimals);
        if (deliveredUsd <= 0)
            return null; // Already refused by the quote-shape check.

        decimal debitedUsd;
        switch (amount)
        {
            case SparkSendAmount.Token token:
                // Both sides are USD-pegged, so no price is needed and none is fetched.
                debitedUsd = ToDecimal(token.BaseUnits, token.Decimals);
                break;

            case SparkSendAmount.Bitcoin:
            {
                var perSatoshi = await _valueOracle
                    .TryGetSatoshiValueAsync(storeId, UsdCurrency, cancellationToken)
                    .ConfigureAwait(false);

                if (perSatoshi is not { } rate || rate <= 0)
                {
                    return new SweepRefusal(
                        SweepRefusalCode.CrossChainValueUnverifiable,
                        "No exchange rate was available to check that this cross-chain quote is priced "
                        + "sensibly, so nothing was sent. This resolves itself once rates are available again.");
                }

                // amountIn, not the amount asked for: what actually leaves the wallet.
                debitedUsd = quote.AmountInSats * rate;
                break;
            }

            default:
                return null;
        }

        if (debitedUsd <= 0)
            return null;

        var lossPercent = (double)((debitedUsd - deliveredUsd) / debitedUsd) * 100d;
        if (lossPercent <= MaxCrossChainValueLossPercent)
            return null;

        return new SweepRefusal(SweepRefusalCode.CrossChainValueUnverifiable, string.Format(
            CultureInfo.InvariantCulture,
            "This sweep would take about {0:C} out of the wallet and deliver about {1:C} — a loss of {2:0.#}%, "
            + "far beyond the {3:0.#}% this plugin will accept as a spread. The quote is priced wrongly and "
            + "nothing was sent.",
            debitedUsd, deliveredUsd, lossPercent, MaxCrossChainValueLossPercent));
    }

    private static decimal ToDecimal(BigInteger baseUnits, uint decimals)
    {
        var scale = BigInteger.Pow(10, (int)Math.Min(decimals, 28));
        var whole = BigInteger.DivRem(baseUnits, scale, out var fraction);

        // Guarded against decimal overflow, which an 18-decimal token reaches for quite ordinary sums. A value
        // that does not fit is not a value this can check, and returning zero makes the caller refuse.
        if (whole > new BigInteger(decimal.MaxValue / 2))
            return 0m;

        return (decimal)whole + ((decimal)fraction / (decimal)scale);
    }

    /// <summary>
    /// Assets whose value this plugin is prepared to assume, for the quote sanity check.
    /// </summary>
    /// <remarks>
    /// Every one is a USD stablecoin, which is the entire basis on which the check treats one unit as one
    /// dollar. Anything else is refused rather than guessed at.
    /// </remarks>
    private static readonly HashSet<string> UsdStablecoins =
        new(StringComparer.OrdinalIgnoreCase) { "USDT", "USDC", "USDB" };

    private const string UsdCurrency = "USD";

    /// <summary>
    /// The share of a sweep's value that may go missing between the wallet and the destination.
    /// </summary>
    /// <remarks>
    /// 10%, and deliberately loose. It is not an economic ceiling — <see cref="SweepSettings.MaxFeePercent"/> is,
    /// and it is far tighter — but a backstop against a quote that is mispriced by an order of magnitude. It has
    /// to absorb peg drift, exchange spread and a rate up to a minute stale without producing false refusals.
    /// </remarks>
    public const double MaxCrossChainValueLossPercent = 10.0;

    private static SweepRefusal DescribeCrossChainQuoteFailure(Exception exception)
    {
        if (SparkErrors.IsInsufficientFunds(exception))
        {
            return new SweepRefusal(
                SweepRefusalCode.InsufficientFunds,
                "Spark reported insufficient funds when quoting this cross-chain sweep.");
        }

        if (SparkErrors.IsAmountTooSmall(exception))
        {
            // A normal outcome rather than an error. The provider's own floor is enforced server-side and is
            // discoverable only by attempting a prepare — the SDK exposes no getter for it — so "below the
            // provider's minimum" arrives as a network exception and must not be reported as a fault.
            return new SweepRefusal(
                SweepRefusalCode.BelowMinimumSweep,
                "This amount is below the bridge provider's own minimum, which is enforced on their side and "
                + "is only discoverable by asking. Wait for a larger balance.");
        }

        return new SweepRefusal(
            SweepRefusalCode.QuoteFailed,
            $"Spark could not quote this cross-chain sweep: {SparkErrors.Describe(exception)}");
    }

    /// <summary>
    /// The stable-balance token this store is configured for and actually holds, or null.
    /// </summary>
    /// <remarks>
    /// Both halves matter. A store that has not enabled Stable Balance is on the sats rail whatever its wallet
    /// happens to hold, and a store that has enabled it but holds nothing yet is too — the conversion runs on
    /// the SDK's own background worker and has not got there. Returning the live balance rather than the
    /// setting is what keeps the two in step without the plugin guessing.
    /// </remarks>
    private static SparkTokenBalance? ResolveStableToken(SparkSettings settings, SparkNodeInfo info)
    {
        var stable = settings.StableBalance ?? new StableBalanceSettings();
        if (!stable.Enabled || stable.Token() is not { } token)
            return null;

        return info.TokenBalance(token);
    }

    private static string FormatEstimate(SweepRecord record) =>
        record.DescribeDelivered() ?? "the quoted amount";

    #endregion

    /// <summary>
    /// Issues the cooperative exit for a record that is already persisted, and records what came back.
    /// </summary>
    private async Task<SweepRunResult> SendAsync(
        string storeId,
        ISparkSdkClient sdk,
        SweepSettings settings,
        SweepRecord record,
        CancellationToken cancellationToken)
    {
        // The fee guard, re-evaluated against the quote the send is actually committing to. The pre-flight quote
        // is by now seconds old and has its own expiry; this callback is the enforcement point, and it runs
        // server-side on every sweep whatever the UI showed.
        SweepRefusal? committedRefusal = null;
        SparkOnchainQuote? committedQuote = null;

        // The SDK seam wants a sentence; the record wants the code as well, so both are captured here.
        string? Approve(SparkOnchainQuote quote)
        {
            committedQuote = quote;
            committedRefusal = ApproveQuote(settings, quote);
            return committedRefusal?.Message;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var result = await sdk
                    .SendToBitcoinAddressAsync(
                        record.DestinationAddress,
                        record.AmountSats,
                        ToSdkSpeed(record.ConfirmationSpeed),
                        record.FeesIncluded,
                        record.IdempotencyKey,
                        Approve,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (result.RejectedReason is { } rejected)
                {
                    // Vetoed by the guard above, which means nothing was sent. Rare — it needs the fee to have
                    // moved between the pre-flight quote and this one — but it is the case where a merchant most
                    // needs to see the number that was refused.
                    return await ResolveAsync(
                            record, SweepOutcomeKind.Refused, SweepRecordStatus.Refused,
                            feeSats: committedQuote?.FeeSats, txId: null, error: rejected, reason: rejected,
                            cancellationToken,
                            refusalCode: committedRefusal?.Code ?? SweepRefusalCode.FeeAboveLimit)
                        .ConfigureAwait(false);
                }

                if (result.Payment is not { } payment)
                {
                    // Neither a payment nor a rejection: the SDK contract says this cannot happen, so treat it as
                    // an unknown outcome rather than as either success or failure.
                    _logger.LogError(
                        "Store {StoreId}: sweep {IdempotencyKey} returned neither a payment nor a refusal",
                        storeId, record.IdempotencyKey);
                    return new SweepRunResult(
                        SweepOutcomeKind.Unresolved,
                        "Spark gave no answer for this sweep. It will be checked again shortly.",
                        record);
                }

                var status = payment.Status switch
                {
                    // Completed already — the funded run saw this arrive ~16–32 s after the send returned, so it
                    // is unusual but not impossible.
                    SparkPaymentStatus.Completed => SweepRecordStatus.Confirmed,
                    SparkPaymentStatus.Failed => SweepRecordStatus.Failed,
                    // The normal case: the send returned while still pending, with the L1 txid already present.
                    _ => SweepRecordStatus.Sent
                };

                var kind = status is SweepRecordStatus.Failed
                    ? SweepOutcomeKind.Failed
                    : SweepOutcomeKind.Swept;

                // What the destination actually receives, and therefore the only number worth showing a merchant.
                //
                // It is `payment.AmountSats` under *both* fee policies, and it is a mistake to net the fee out of
                // it again. The SDK's two amount fields do not mean the same thing:
                //
                //   • On the *quote* (PrepareSendPaymentResponse) `amount` echoes back what was asked for and is
                //     not adjusted for FeesIncluded — which is why SparkCoopExitQuote has to
                //     derive RecipientAmountSats itself.
                //   • On the *payment* (the send result) `amount` is already what leaves for the destination, with
                //     the fee reported separately alongside it, so total debited is amount + fees. Under
                //     FeesIncluded the SDK has already done the netting: a 62,000 sat drain at a 1,710 sat fee
                //     comes back as amount 60,290, fees 1,710. Under FeesExcluded nothing was netted in the first
                //     place: amount is the requested sum, the fee rides on top, and it is again what the
                //     destination gets. The Lightning path reads the same field the same way (see
                //     SparkLightningClient's PayDetails.TotalAmount = amount + fee).
                //
                // Subtracting the fee for FeesIncluded understated a real mainnet sweep by exactly one fee: the
                // chain paid the store 60,290 sat and the message claimed 58,580. Prefer this over the record's
                // RecipientAmountSats, which at this point still derives from the *quoted* fee — the record's own
                // FeeSats is only written by the ResolveAsync below — and so goes stale if the fee moved between
                // quote and send.
                var recipientAmountSats = payment.AmountSats;

                var reason = status switch
                {
                    SweepRecordStatus.Failed => "Spark reported this cooperative exit as failed.",
                    SweepRecordStatus.Confirmed => string.Format(
                        CultureInfo.InvariantCulture,
                        "Swept {0:N0} sat on-chain for a {1:N0} sat fee.",
                        recipientAmountSats,
                        payment.FeeSats),
                    _ => string.Format(
                        CultureInfo.InvariantCulture,
                        "Sweep of {0:N0} sat accepted for a {1:N0} sat fee. It confirms on-chain shortly.",
                        recipientAmountSats,
                        payment.FeeSats)
                };

                return await ResolveAsync(
                        record, kind, status, payment.FeeSats, payment.TxId,
                        error: status is SweepRecordStatus.Failed ? reason : null, reason, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (SparkErrors.IsExpiredFeeQuote(ex) && attempt == 1)
            {
                // A normal condition, not a failure: a bitcoin-address prepare is valid for about a minute. The
                // retry reuses the same idempotency key — safe by construction, since deduplication is keyed on
                // the key alone and this send provably did not go through — and gets a fresh quote, which the
                // guard above re-checks.
                _logger.LogInformation(
                    "Store {StoreId}: the fee quote for sweep {IdempotencyKey} expired; re-quoting once",
                    storeId, record.IdempotencyKey);
            }
            catch (Exception ex) when (SparkErrors.IsInsufficientFunds(ex) || IsProvablyNotSent(ex))
            {
                // Every case here is decided before anything leaves the wallet — insufficient funds is checked by
                // the service provider at send, invalid input is a local 0 ms validation, and the client's own
                // pre-send assertions and argument guards throw before SendPayment is reached at all. So this is a
                // clean failure and the store is free to try again next pass.
                //
                // Getting that classification wrong is not cosmetic: falling into the generic branch below would
                // leave the row Pending and block every sweep for this store until the grace period expired, over a
                // failure that provably sent nothing.
                var reason = SparkErrors.IsInsufficientFunds(ex)
                    ? "Spark reported insufficient funds for this sweep. The fee is charged on top of the swept "
                      + "amount unless the sweep is set to take the fee out of it."
                    : $"Spark rejected this sweep: {SparkErrors.Describe(ex)}";

                _logger.LogWarning(ex,
                    "Store {StoreId}: sweep {IdempotencyKey} was rejected before sending ({Reason})",
                    storeId, record.IdempotencyKey, SparkErrors.Describe(ex));

                return await ResolveAsync(
                        record, SweepOutcomeKind.Failed, SweepRecordStatus.Failed, null, null, reason, reason,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Anything else — a network failure, a timeout, a disposed handle — leaves the outcome genuinely
                // unknown. The record deliberately stays Pending: the next pass asks the SDK whether the exit
                // happened rather than guessing, and no new sweep starts until it knows.
                _logger.LogError(ex,
                    "Store {StoreId}: sweep {IdempotencyKey} failed with an unknown outcome ({Reason}). It will "
                    + "be resolved against the Spark wallet on the next pass",
                    storeId, record.IdempotencyKey, SparkErrors.Describe(ex));

                return new SweepRunResult(
                    SweepOutcomeKind.Unresolved,
                    "This sweep's outcome is not yet known. Spark will be asked again shortly; nothing further "
                    + "will be sent until it answers.",
                    record);
            }
        }
    }

    /// <summary>
    /// Resolves every unsettled record for a store against the SDK.
    /// </summary>
    /// <returns>
    /// The record that still blocks new sweeps, or null when none does. Only a
    /// <see cref="SweepRecordStatus.Pending"/> row blocks: a <see cref="SweepRecordStatus.Sent"/> exit is already
    /// on-chain and its funds are no longer in the balance a new sweep would read.
    /// </returns>
    private async Task<SweepRecord?> ResolveUnresolvedAsync(
        string storeId,
        ISparkSdkClient sdk,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var records = await _records
            .ListUnresolvedAsync(storeId, now - ResolutionWindow, cancellationToken)
            .ConfigureAwait(false);

        SweepRecord? blocking = null;
        var refundNeeded = false;
        foreach (var record in records)
        {
            SparkPayment? payment;
            try
            {
                payment = record.IdempotencyKeyAccepted
                    // The idempotency key is the SDK's payment id — verified on real cooperative exits — so this
                    // is a point lookup and a definitive answer, not a heuristic.
                    ? await sdk.GetPaymentAsync(record.IdempotencyKey, cancellationToken).ConfigureAwait(false)
                    // ...except when the send could not carry a key at all. Then there is no payment id to look
                    // up and the provider's quote id is what identifies the send.
                    : await ResolveByQuoteIdAsync(storeId, sdk, record, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Store {StoreId}: could not read sweep {IdempotencyKey} back from the Spark wallet",
                    storeId, record.IdempotencyKey);
                // Unknown, so an in-flight record keeps blocking. Refusing to sweep is always the safe direction.
                if (record.IsInFlight)
                    blocking ??= record;
                continue;
            }

            if (payment is null)
            {
                if (!record.IsInFlight)
                    continue;

                if (now - record.CreatedAt < UnresolvedGrace)
                {
                    blocking ??= record;
                    continue;
                }

                // Worded as what is known rather than as a conclusion. That the SDK would replay a service-provider
                // -accepted transfer into its own storage after a crash and reconnect is an assumption, and the
                // spike lists it as unverified — so "Spark has no record of this" is honest and "it was never sent"
                // would not be. The balance and the store's history are the checks a merchant should make.
                //
                // On the token path the caveat is stronger still: what was searched is the payment history for
                // this record's provider quote id, and a send whose persistence step never completed might not
                // be in that history at all. So the row is closed and the store is unblocked, but the sentence
                // does not claim the money is safe — and, crucially, nothing re-sends it.
                var reason = record.IdempotencyKeyAccepted
                    ? "Spark has no record of this sweep. Most likely nothing was sent and the balance is still "
                      + "on the Spark wallet, in which case the next pass will try again — but check the Spark "
                      + "balance against your sweep history before assuming so."
                    : "Spark has no payment carrying this sweep's provider quote id. A cross-chain send funded "
                      + "from a stablecoin balance cannot carry an idempotency key, so this is the strongest "
                      + "evidence available and it is not proof: check the stablecoin balance and the "
                      + "destination address before assuming nothing was sent. It will not be retried "
                      + "automatically.";

                if (await _records
                        .TryResolveAsync(
                            storeId, record.IdempotencyKey, [SweepRecordStatus.Pending],
                            new SweepResolution(SweepRecordStatus.Failed, null, null, reason, now),
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    _logger.LogWarning(
                        "Store {StoreId}: sweep {IdempotencyKey} was written {Age} ago and the Spark wallet has "
                        + "no payment under that key; recording it as never sent",
                        storeId, record.IdempotencyKey, now - record.CreatedAt);
                }

                continue;
            }

            // What shape of payment this row's send should have produced. A cooperative exit is a Withdraw; a
            // cross-chain send is an ordinary Spark transfer (sats-funded) or a Token transfer (token-funded),
            // because the SDK has no cross-chain payment method at all — the destination lives in the nested
            // conversion info instead.
            var expectedMethods = record.IsCrossChain
                ? new[] { SparkPaymentMethod.Spark, SparkPaymentMethod.Token }
                : [SparkPaymentMethod.Withdraw];

            if (payment.Direction is not SparkPaymentDirection.Send ||
                Array.IndexOf(expectedMethods, payment.Method) < 0)
            {
                // The key is a UUID this plugin minted, so a payment under it that is not an outgoing cooperative
                // exit should be impossible — but writing a receive's fee and txid onto a sweep row would be a lie
                // about where a merchant's money went, and the settlement reconciler makes the mirror-image check
                // before crediting an invoice. Treated as unreadable: the row keeps blocking, which is safe.
                _logger.LogError(
                    "Store {StoreId}: the payment matched to sweep {IdempotencyKey} is a {Direction} on "
                    + "{Method}, which is not what this sweep would have produced; refusing to record it",
                    storeId, record.IdempotencyKey, payment.Direction, payment.Method);
                if (record.IsInFlight)
                    blocking ??= record;
                continue;
            }

            if (payment.Conversion is { NeedsRefund: true })
                refundNeeded = true;

            if (record.IsCrossChain)
            {
                await PromoteCrossChainAsync(storeId, record, payment, now, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            switch (payment.Status)
            {
                case SparkPaymentStatus.Completed:
                    await PromoteAsync(
                            storeId, record, SweepRecordStatus.Confirmed, payment, now, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case SparkPaymentStatus.Failed:
                    await PromoteAsync(
                            storeId, record, SweepRecordStatus.Failed, payment, now, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                default:
                    // Still pending at the service provider, but the exit definitively happened: it has an id and,
                    // from the very first event, an L1 txid. Recording that is what turns an unknown outcome into
                    // a sweep the merchant can look up on-chain — and it stops blocking new sweeps, because those
                    // funds have already left the balance.
                    await PromoteAsync(storeId, record, SweepRecordStatus.Sent, payment, now, cancellationToken)
                        .ConfigureAwait(false);
                    break;
            }
        }

        if (refundNeeded)
        {
            // Driven from here rather than from a loop of its own, which is the point: this plugin already
            // requires a reconciliation pass of its own because the SDK's events are unreliable (PaymentSucceeded
            // was observed firing twice for one payment, and in one case never), and
            // for conversions the situation is worse — there is no event to be unreliable. Extending the walk
            // that already runs before every sweep keeps one mechanism rather than two, and a conversion the SDK
            // is holding in RefundNeeded is also a conversion blocking this store's sweeps.
            try
            {
                // Called only when a conversion has actually been observed in RefundNeeded, never speculatively.
                //
                // UNVERIFIED, and the reason for the caution: the SDK's own storage queries suggest this call
                // also picks up conversions filtered as OrchestraPending and BoltzPending — which are *healthy*
                // in-flight conversions, not stuck ones. If that is what it does, calling it on a timer would
                // interfere with sweeps that are simply still travelling. Nobody could confirm the behaviour
                // from outside the SDK, so the plugin treats it as a targeted recovery action rather than
                // routine hygiene: one call, on the pass that saw a genuinely stuck conversion, and never
                // otherwise. Confirm on the first funded mainnet run before making it any more eager.
                await sdk.RefundPendingConversionsAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "Store {StoreId}: a conversion needed refunding and a refund was requested. Nothing reports "
                    + "when it completes, so it is re-checked on the next pass. Note this SDK call may also "
                    + "touch healthy in-flight conversions; it is issued only because one was observed stuck",
                    storeId);
            }
            catch (Exception ex)
            {
                // Non-fatal: the condition is already recorded on the row and will be seen again next pass.
                _logger.LogError(ex,
                    "Store {StoreId}: could not refund a stuck conversion ({Reason})",
                    storeId, SparkErrors.Describe(ex));
            }
        }

        return blocking;
    }

    /// <summary>
    /// Finds the payment a keyless cross-chain send produced, by the provider quote id recorded before it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the replacement for the idempotency-key primitive on the token path, and it is deliberately a
    /// search rather than a retry.</b> A token-leg send cannot carry an idempotency key — the SDK rejects one — so
    /// there is no id to look a payment up by and nothing that would deduplicate a second attempt. The
    /// provider's quote id is written to the row before the send and appears on the resulting payment's
    /// conversion info, so scanning is the only honest way to answer "did it send?".
    /// </para>
    /// <para>
    /// The scan is bounded the same way the settlement reconciler's is: anchored to the record's creation time
    /// with a little slack for clock skew, filtered to sends, and paged. A record with no recorded quote id
    /// cannot be matched at all and is reported as not found, which puts it on the same grace-period path as
    /// any other unanswerable send.
    /// </para>
    /// </remarks>
    private async Task<SparkPayment?> ResolveByQuoteIdAsync(
        string storeId,
        ISparkSdkClient sdk,
        SweepRecord record,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(record.ProviderQuoteId))
        {
            // Nothing to match on. The row goes down the grace-period path and is closed as unknown rather than
            // retried — which is the safe direction, because a retry here would be a second payment.
            _logger.LogError(
                "Store {StoreId}: cross-chain sweep {Key} has no provider quote id recorded, so whether it sent "
                + "cannot be established. It will not be retried",
                storeId, record.IdempotencyKey);
            return null;
        }

        var from = record.CreatedAt - QuoteScanSlack;
        for (var offset = 0; offset < QuoteScanMaxRows; offset += QuoteScanPageSize)
        {
            var page = await sdk
                .ListPaymentsAsync(
                    new SparkListPaymentsQuery(
                        SparkPaymentDirection.Send, CompletedOnly: false, from, offset, QuoteScanPageSize,
                        // Oldest first. The target is the send that was in flight when the process died, which
                        // is the oldest row in a window anchored to the record's own creation time — paging
                        // newest-first would spend the whole budget walking away from it on a busy store.
                        Ascending: true),
                    cancellationToken)
                .ConfigureAwait(false);

            if (page.Count == 0)
                return null;

            foreach (var payment in page)
            {
                if (payment.Conversion is { ProviderQuoteId: { } quoteId } &&
                    string.Equals(quoteId, record.ProviderQuoteId, StringComparison.Ordinal))
                {
                    return payment;
                }
            }

            if (page.Count < QuoteScanPageSize)
                return null;
        }

        _logger.LogWarning(
            "Store {StoreId}: scanned {Rows} payments without finding provider quote {QuoteId} for sweep {Key}",
            storeId, QuoteScanMaxRows, record.ProviderQuoteId, record.IdempotencyKey);
        return null;
    }

    /// <summary>
    /// Applies what a poll learned about a cross-chain sweep.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Confirmed means delivered, not sent.</b> The payment completing says the source leg reached the
    /// provider; the money arriving on the destination chain is a separate fact the provider reports later in
    /// <c>deliveredAmount</c>, through no event. Promoting on the payment's status alone would tell a merchant
    /// their USDT had arrived while it was still in the provider's queue — so the conversion status is what
    /// decides, and the payment's status only decides failure.
    /// </para>
    /// <para>
    /// <see cref="SparkConversionStatus.RefundNeeded"/> is left as <c>Sent</c> rather than failed: the money has
    /// left, the SDK is holding it, and a refund has been requested. Marking it failed would invite a retry of a
    /// send whose funds are not back yet.
    /// </para>
    /// </remarks>
    private async Task PromoteCrossChainAsync(
        string storeId,
        SweepRecord record,
        SparkPayment payment,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var conversion = payment.Conversion;
        var conversionStatus = conversion?.Status ?? SparkConversionStatus.Unknown;

        var status = payment.Status is SparkPaymentStatus.Failed
            ? SweepRecordStatus.Failed
            : conversionStatus switch
            {
                SparkConversionStatus.Completed => SweepRecordStatus.Confirmed,
                SparkConversionStatus.Failed => SweepRecordStatus.Failed,
                SparkConversionStatus.Refunded => SweepRecordStatus.Failed,
                _ => SweepRecordStatus.Sent
            };

        if (record.Status == status &&
            record.ConversionStatus == conversionStatus &&
            (conversion?.DeliveredAmount is null ||
             record.DeliveredAmountBaseUnits == conversion.DeliveredAmount.Value.ToString(CultureInfo.InvariantCulture)))
        {
            return;
        }

        // Both Pending and Sent, for every target status — unlike the cooperative-exit path, where Sent is
        // terminal enough that it may only be promoted. A cross-chain row legitimately moves Sent -> Sent as the
        // delivered amount arrives, and Sent -> Confirmed when the provider finishes, so there is nothing to
        // narrow. Written as one array rather than a ternary whose branches were identical.
        SweepRecordStatus[] allowedFrom = [SweepRecordStatus.Pending, SweepRecordStatus.Sent];

        var error = status switch
        {
            SweepRecordStatus.Failed when conversionStatus is SparkConversionStatus.Refunded =>
                "The bridge provider could not deliver this sweep and it was refunded. The funds are back on the "
                + "Spark wallet.",
            SweepRecordStatus.Failed =>
                "Spark reported this cross-chain send as failed. Nothing arrived at the destination.",
            _ when conversionStatus is SparkConversionStatus.RefundNeeded =>
                "The bridge provider could not complete this sweep and the funds need refunding. A refund has "
                + "been requested; nothing reports when it completes, so this is re-checked every pass.",
            _ => null
        };

        if (await _records
                .TryResolveAsync(
                    storeId, record.IdempotencyKey, allowedFrom,
                    new SweepResolution(
                        status, null, null, error, now, SweepRefusalCode.None, conversionStatus,
                        conversion?.DeliveredAmount?.ToString(CultureInfo.InvariantCulture)),
                    cancellationToken)
                .ConfigureAwait(false))
        {
            _logger.LogInformation(
                "Store {StoreId}: cross-chain sweep {Key} is {Status} ({Conversion}), delivered {Delivered}",
                storeId, record.IdempotencyKey, status, conversionStatus,
                conversion?.DescribeDelivered() ?? "not yet reported");
        }
    }

    /// <summary>Clock skew allowance when anchoring a quote-id scan to a record's creation time.</summary>
    private static readonly TimeSpan QuoteScanSlack = TimeSpan.FromMinutes(10);

    private const int QuoteScanPageSize = 50;

    /// <summary>
    /// How many payments a quote-id scan will read before giving up.
    /// </summary>
    /// <remarks>
    /// Bounded because this runs on every sweep pass for every unresolved keyless row, and an unbounded scan
    /// would become O(all history). Five pages back from the record's own timestamp is far more than a busy
    /// store produces in the seconds between a send being issued and a crash.
    /// </remarks>
    private const int QuoteScanMaxRows = 250;

    private async Task PromoteAsync(
        string storeId,
        SweepRecord record,
        SweepRecordStatus status,
        SparkPayment payment,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (record.Status == status)
            return;

        // Sent may be promoted to Confirmed or Failed, but never demoted back to Sent.
        SweepRecordStatus[] allowedFrom = status is SweepRecordStatus.Sent
            ? [SweepRecordStatus.Pending]
            : [SweepRecordStatus.Pending, SweepRecordStatus.Sent];

        var error = status is SweepRecordStatus.Failed
            ? "Spark reported this cooperative exit as failed. Nothing arrived at the destination."
            : null;

        if (await _records
                .TryResolveAsync(
                    storeId, record.IdempotencyKey, allowedFrom,
                    new SweepResolution(status, payment.FeeSats, payment.TxId, error, now),
                    cancellationToken)
                .ConfigureAwait(false))
        {
            _logger.LogInformation(
                "Store {StoreId}: sweep {IdempotencyKey} resolved as {Status} "
                + "(fee {FeeSats} sat, txid {TxId})",
                storeId, record.IdempotencyKey, status, payment.FeeSats, payment.TxId ?? "unknown");

            // The reconciliation path learns the txid from the SDK's payment rather than from the row, so this
            // covers a sweep whose send raced a crash — the label lands when the fate is finally known. The
            // write is idempotent, so a Sent row later promoted to Confirmed labels once.
            if (status is SweepRecordStatus.Sent or SweepRecordStatus.Confirmed && payment.TxId is not null)
                await _transactionLabeler.LabelAsync(record, payment.TxId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies an outcome to a record that has already been sent for, and reports it.
    /// </summary>
    /// <param name="error">Recorded on the row. Null for a success, which has nothing to explain.</param>
    /// <param name="reason">Reported to the caller. Always set.</param>
    private async Task<SweepRunResult> ResolveAsync(
        SweepRecord record,
        SweepOutcomeKind kind,
        SweepRecordStatus status,
        long? feeSats,
        string? txId,
        string? error,
        string reason,
        CancellationToken cancellationToken,
        SweepRefusalCode refusalCode = SweepRefusalCode.None,
        SparkConversionStatus? conversionStatus = null,
        string? deliveredBaseUnits = null,
        string? providerOrderId = null)
    {
        var now = _timeProvider.GetUtcNow();
        if (!await _records
                .TryResolveAsync(
                    record.StoreId, record.IdempotencyKey, [SweepRecordStatus.Pending],
                    new SweepResolution(
                        status, feeSats, txId, error, now, refusalCode, conversionStatus, deliveredBaseUnits),
                    cancellationToken)
                .ConfigureAwait(false))
        {
            // Should be unreachable: the row was created moments ago by this very pass, and the single-flight guard
            // means no other pass for this store can be resolving it. Logged rather than ignored because if it ever
            // does happen, the row on disk disagrees with what the caller is about to be told.
            _logger.LogError(
                "Store {StoreId}: sweep {IdempotencyKey} could not be moved to {Status}; it is no longer pending",
                record.StoreId, record.IdempotencyKey, status);
        }

        // Mirrored onto the in-memory copy so the caller — a controller rendering a result page — sees the row it
        // just caused rather than the pre-send snapshot.
        record.Status = status;
        record.FeeSats = feeSats ?? record.FeeSats;
        record.TxId = txId ?? record.TxId;
        record.Error = error ?? record.Error;
        if (refusalCode is not SweepRefusalCode.None)
            record.RefusalCode = refusalCode;
        record.ConversionStatus = conversionStatus ?? record.ConversionStatus;
        record.DeliveredAmountBaseUnits = deliveredBaseUnits ?? record.DeliveredAmountBaseUnits;
        record.ProviderOrderId = providerOrderId ?? record.ProviderOrderId;
        record.CompletedAt = now;

        // After the resolution is durable, never before: the label decorates a transaction that happened, and a
        // labelling failure is logged and swallowed inside the labeler rather than allowed to disturb a sweep
        // that has already moved money.
        if (status is SweepRecordStatus.Sent or SweepRecordStatus.Confirmed && txId is not null)
            await _transactionLabeler.LabelAsync(record, txId, cancellationToken).ConfigureAwait(false);

        return new SweepRunResult(kind, reason, record);
    }

    /// <summary>
    /// Records a refusal and reports it, folding a recurring automatic one onto the row that already stands for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Keyed on the refusal's code, never on its rendered sentence.</b> Every message here embeds live figures —
    /// the balance, the sweepable amount, the fee, the percentage, the SDK's own wording — and the balance itself
    /// drifts by a few sats around the SDK's background leaf optimisation. An earlier revision compared sentences,
    /// so consecutive refusals for one unchanged cause essentially never matched, and a store parked on a refusal
    /// wrote a row every couple of minutes forever with no cleanup path. That is not a corner case: with mainnet
    /// broadcast fees an order of magnitude above the regtest levels these defaults were calibrated against, a
    /// default-configured store sits permanently on <see cref="SweepRefusalCode.FeeAboveLimit"/>.
    /// </para>
    /// <para>
    /// It also searches by reason rather than asking "is the newest row this refusal?". Any intervening row — a
    /// manual refusal, one successful sweep — would defeat the latter, and an ongoing condition does not stop being
    /// ongoing because something else happened once in the middle of it.
    /// </para>
    /// <para>
    /// A manual refusal always files its own row: a merchant who pressed the button wants to find that press
    /// afterwards, distinct from whatever the scheduler has been doing.
    /// </para>
    /// </remarks>
    private async Task<SweepRunResult> RefuseAsync(
        string storeId,
        SweepTrigger trigger,
        SweepSettings settings,
        SweepRefusal refusal,
        SweepDestination? destination,
        long balanceSats,
        long amountSats,
        long quotedFeeSats,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var mode = destination?.Mode ?? settings.DestinationMode;

        if (trigger is SweepTrigger.Automatic)
        {
            var open = await _records
                .FindOpenRefusalAsync(storeId, refusal.Code, mode, now - RefusalCoalescingWindow, cancellationToken)
                .ConfigureAwait(false);

            if (open is not null &&
                await _records
                    .TryRecordRepeatRefusalAsync(
                        storeId, open.IdempotencyKey, refusal.Message, now, cancellationToken)
                    .ConfigureAwait(false))
            {
                // Debug, not warning: the condition was already reported at warning level when the row was filed,
                // and repeating it every couple of minutes is how an operator's log becomes unreadable.
                _logger.LogDebug(
                    "Store {StoreId}: still refusing to sweep ({Code}, seen {AttemptCount} times) — {Reason}",
                    storeId, refusal.Code, open.AttemptCount + 1, refusal.Message);

                // Mirrored onto the copy so the caller reports the current tally rather than the stale one.
                open.AttemptCount += 1;
                open.LastSeenAt = now;
                open.Error = refusal.Message;
                return new SweepRunResult(SweepOutcomeKind.Refused, refusal.Message, open);
            }
        }

        _logger.LogWarning(
            "Store {StoreId}: refusing to sweep ({Code}) — {Reason}", storeId, refusal.Code, refusal.Message);

        var record = new SweepRecord
        {
            IdempotencyKey = Guid.NewGuid().ToString(),
            StoreId = storeId,
            DestinationAddress = destination?.Address ?? string.Empty,
            DestinationMode = mode,
            AmountSats = amountSats,
            FeesIncluded = settings.DrainWhenSweeping,
            ConfirmationSpeed = settings.ConfirmationSpeed,
            QuotedFeeSats = quotedFeeSats,
            BalanceAtDecisionSats = balanceSats,
            Trigger = trigger,
            Status = SweepRecordStatus.Refused,
            RefusalCode = refusal.Code,
            CreatedAt = now,
            CompletedAt = now,
            AttemptCount = 1,
            Error = refusal.Message
        };

        try
        {
            await _records.AddAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The refusal itself is what matters and it is already logged and returned; failing to file it must
            // not turn a safe refusal into an exception on a merchant's page.
            _logger.LogError(ex, "Store {StoreId}: could not record a refused sweep", storeId);
        }

        return new SweepRunResult(SweepOutcomeKind.Refused, refusal.Message, record);
    }

    /// <summary>
    /// How a failed cooperative-exit quote is described, and which refusal it counts as.
    /// </summary>
    /// <remarks>
    /// Shared by the run and the preview so the two cannot describe one failure differently. The
    /// insufficient-funds split matters to a merchant: it means "your balance moved", not "Spark is broken".
    /// </remarks>
    private static SweepRefusal DescribeQuoteFailure(Exception exception) =>
        SparkErrors.IsInsufficientFunds(exception)
            ? new SweepRefusal(
                SweepRefusalCode.InsufficientFunds,
                "Spark reported insufficient funds when quoting this sweep. The balance may have been spent or may "
                + "not have finished settling.")
            : new SweepRefusal(
                SweepRefusalCode.QuoteFailed,
                $"Spark could not quote this sweep: {SparkErrors.Describe(exception)}");

    /// <summary>
    /// The amount to sweep, or the reason there is not one worth sweeping.
    /// </summary>
    /// <remarks>
    /// Pure and internal so the economics are directly testable, and shared by the run and the preview so the two
    /// cannot disagree.
    /// <para>
    /// <b>The two fee policies mean different things and are checked differently.</b> With
    /// <see cref="SweepSettings.DrainWhenSweeping"/> on, the fee is netted out of the swept amount, so the balance
    /// lands on exactly the reserve and no separate arithmetic is needed. With it off, the fee is charged on top,
    /// so it comes out of the reserve — and a reserve smaller than the fee is refused rather than allowed to
    /// overdraw, because <c>PrepareSendPayment</c> does not check <c>amount + fee ≤ balance</c> and would let the
    /// send fail late instead.
    /// </para>
    /// </remarks>
    internal static SweepPlan PlanSweep(SweepSettings settings, long balanceSats)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var reserve = Math.Max(0, settings.ReserveSats);
        var sweepable = balanceSats - reserve;

        if (sweepable <= 0)
        {
            return SweepPlan.Refuse(sweepable, new SweepRefusal(
                SweepRefusalCode.NothingAboveReserve,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The Spark balance of {0:N0} sat is not above the {1:N0} sat reserve, so there is nothing to "
                    + "sweep.",
                    balanceSats, reserve)));
        }

        // EffectiveMinimumSweepSats, not the raw property. It takes the higher of the merchant's own minimum and
        // the cross-chain floor when the destination is an EVM address, because the two floors answer different
        // questions: a cooperative exit's fee is flat, so its floor is about clearing a fixed cost, while a
        // bridge's is about staying off the punishing bottom of a curve (~3.3% at the protocol minimum against
        // ~0.34% at 50,000 sats). Reading the raw property left that floor as dead code enforced only by form
        // validation — which this project's own doctrine calls a courtesy, not a guard.
        var floor = Math.Max(
            Math.Max(0, settings.EffectiveMinimumSweepSats), Constants.MinimumOnchainSendSats);
        if (sweepable < floor)
        {
            return SweepPlan.Refuse(sweepable, new SweepRefusal(
                SweepRefusalCode.BelowMinimumSweep,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Only {0:N0} sat is sweepable, below this store's {1:N0} sat minimum. A cooperative exit "
                    + "costs the same flat fee whatever it carries, so sweeping less is poor value.",
                    sweepable, floor)));
        }

        return SweepPlan.Sweep(sweepable);
    }

    /// <summary>
    /// The fee guard. Returns null to proceed, or the reason to refuse.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured against what the <em>destination receives</em>, not against the gross amount. That is the number
    /// a merchant would compute themselves, and with the fee netted out of the swept amount it is also the
    /// slightly more conservative of the two.
    /// </para>
    /// <para>
    /// <b>There is no configuration that switches the guard off</b>, and that claim is now enforced rather than
    /// asserted. Two things make it true. A percentage of zero or less with no flat ceiling falls back to the
    /// default percentage rather than allowing anything. And whatever the merchant configures, the fee may never
    /// exceed <see cref="SweepSettings.HardMaxFeePercent"/> of what the destination receives — so clearing the
    /// percentage and typing a very large number into the flat field, which the form used to accept and which left
    /// nothing to take a minimum against, no longer authorises an unbounded fee.
    /// </para>
    /// <para>
    /// Also refuses a sweep whose recipient amount is below the on-chain dust floor, and — for the fee-on-top
    /// policy — one whose reserve cannot cover the fee.
    /// </para>
    /// </remarks>
    internal static SweepRefusal? ApproveQuote(SweepSettings settings, SparkOnchainQuote quote)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(quote);

        var recipient = quote.RecipientAmountSats;

        if (recipient < Constants.MinimumOnchainSendSats)
        {
            return new SweepRefusal(SweepRefusalCode.BelowDustFloor, string.Format(
                CultureInfo.InvariantCulture,
                "After a {0:N0} sat exit fee the destination would receive {1:N0} sat, below the {2:N0} sat "
                + "on-chain minimum. Wait for a larger balance.",
                quote.FeeSats, recipient, Constants.MinimumOnchainSendSats));
        }

        if (!quote.FeesIncluded)
        {
            var reserve = Math.Max(0, settings.ReserveSats);
            if (quote.FeeSats > reserve)
            {
                return new SweepRefusal(SweepRefusalCode.ReserveBelowFee, string.Format(
                    CultureInfo.InvariantCulture,
                    "The {0:N0} sat exit fee is charged on top of the swept amount, but this store's reserve is "
                    + "only {1:N0} sat. Either raise the reserve above the fee, or set sweeps to take the fee out "
                    + "of the swept amount.",
                    quote.FeeSats, reserve));
            }
        }

        long? maxFeeSats = null;
        if (settings.MaxFeePercent > 0)
            maxFeeSats = (long)Math.Floor(recipient * settings.MaxFeePercent / 100d);

        if (settings.MaxFeeFlatSats is { } flat && flat >= 0)
            maxFeeSats = maxFeeSats is null ? flat : Math.Min(maxFeeSats.Value, flat);

        maxFeeSats ??= (long)Math.Floor(recipient * SweepSettings.DefaultMaxFeePercent / 100d);

        // The backstop no configuration can lift. Without it, clearing the percentage and entering a very large
        // flat ceiling left `maxFeeSats` with nothing to take a minimum against, which is a fee guard switched off
        // by a form the plugin accepted.
        maxFeeSats = Math.Min(
            maxFeeSats.Value, (long)Math.Floor(recipient * SweepSettings.HardMaxFeePercent / 100d));

        if (quote.FeeSats <= maxFeeSats)
            return null;

        return new SweepRefusal(SweepRefusalCode.FeeAboveLimit, string.Format(
            CultureInfo.InvariantCulture,
            "The {0:N0} sat exit fee is {1:0.##}% of the {2:N0} sat this sweep would deliver, above the "
            + "{3:N0} sat limit this store allows. Sweep a larger amount, choose a slower confirmation speed, or "
            + "raise the limit.",
            quote.FeeSats, quote.FeePercentOfRecipientAmount, recipient, maxFeeSats.Value));
    }

    /// <summary>
    /// Whether a failure happened before <c>SendPayment</c> could have been reached, so nothing was sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Covers the SDK's own local validation and the client's pre-send assertions: a destination the SDK resolved as
    /// something other than a Bitcoin address, a fee policy it echoed back changed, and the argument guards. All of
    /// those throw on the way <em>to</em> the send, so the safe classification is a clean failure rather than an
    /// unknown outcome — and treating them as unknown would block the store's sweeps for the grace period over a
    /// configuration problem.
    /// </para>
    /// <para>
    /// <b><see cref="ObjectDisposedException"/> is excluded, and the exclusion is load-bearing.</b> It derives from
    /// <see cref="InvalidOperationException"/>, so without the first clause it matches the list below and a sweep
    /// whose SDK handle was disposed <em>mid-send</em> would resolve <c>Failed</c> — unblocking the store to sweep
    /// the same balance again on the next pass, while the first send may well have left the wallet. Disposal races
    /// a send on every reconfigure and shutdown, which is exactly when sweeps are in flight. The generic catch in
    /// <c>SendAsync</c> already names "a disposed handle" as a genuinely-unknown outcome; this clause is what
    /// actually routes it there. Yes, a handle disposed *before* the send truly sent nothing — but nothing here can
    /// tell the two apart, and paying a grace period is cheaper than paying twice.
    /// </para>
    /// </remarks>
    private static bool IsProvablyNotSent(Exception exception) =>
        exception is not ObjectDisposedException
        && (SparkErrors.IsInvalidInput(exception)
            || exception is InvalidOperationException or ArgumentException or ArgumentNullException);

    internal static SparkOnchainSpeed ToSdkSpeed(SweepConfirmationSpeed speed) => speed switch
    {
        SweepConfirmationSpeed.Slow => SparkOnchainSpeed.Slow,
        SweepConfirmationSpeed.Fast => SparkOnchainSpeed.Fast,
        _ => SparkOnchainSpeed.Medium
    };

    private const string WalletNotRunning =
        "This store's Spark wallet is not running, so nothing can be swept. Check the Flint status page.";
}

/// <param name="AmountSats">Amount to ask the SDK for. Zero when there is nothing to sweep.</param>
/// <param name="SweepableSats">Balance minus reserve, whether or not it is worth sweeping.</param>
/// <param name="Refusal">Set exactly when no sweep should be attempted.</param>
public sealed record SweepPlan(long AmountSats, long SweepableSats, SweepRefusal? Refusal)
{
    internal static SweepPlan Sweep(long sweepable) => new(sweepable, sweepable, null);

    internal static SweepPlan Refuse(long sweepable, SweepRefusal refusal) => new(0, sweepable, refusal);
}

/// <summary>
/// A reason the plugin declined to sweep: the stable identity, and the sentence a merchant reads.
/// </summary>
/// <param name="Code">
/// What kind of refusal this is. The de-duplication keys on this and never on <paramref name="Message"/>.
/// </param>
/// <param name="Message">
/// Written for a merchant, and therefore full of live figures — which is exactly why it cannot be an identity.
/// </param>
public sealed record SweepRefusal(SweepRefusalCode Code, string Message);
