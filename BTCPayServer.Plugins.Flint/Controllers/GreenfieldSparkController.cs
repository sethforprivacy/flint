using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Models;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Serialization;

namespace BTCPayServer.Plugins.Flint.Controllers;

/// <summary>
/// The Greenfield (API-key authenticated) Spark API: set a store up, read its state, configure sweeping, sweep.
/// </summary>
/// <remarks>
/// <para>
/// This exists so the plugin can be driven without a browser: headless mainnet validation, host-level end-to-end
/// tests in CI, and merchants scripting their own setup. It is a second <em>surface</em>, deliberately not a second
/// <em>implementation</em>. Every decision it makes is made by a service the cookie-authenticated
/// <see cref="SparkController"/> calls as well — <see cref="SparkSeedResolver"/> for seed sources and the
/// hot-wallet policy gate, <see cref="SparkStoreProvisioner"/> for provisioning and removal,
/// <see cref="SparkStoreStatusReader"/> for status, <see cref="SparkSweepSettingsService"/> for the sweep
/// configuration, and <see cref="SparkSweepEngine"/> for sweeping. If the two surfaces ever behave differently,
/// that is a bug in one of them and not a feature of either.
/// </para>
/// <para>
/// <b>Store scoping, and why it is written this way.</b> A cross-store hole was found in the MVC controller in
/// review: BTCPay resolves the store a request is authorised for from <em>route</em> data, while ASP.NET Core model
/// binding preferred a <em>form</em> value of the same name, so an action that acted on its bound <c>storeId</c>
/// could be authorised against the caller's own store and then act on somebody else's — another store's Lightning
/// invoices minting into the attacker's wallet. Greenfield's authorisation model is the same model: core's
/// <c>BuiltInPermissionScopeProvider</c> resolves the scope from route data, then the query string, then the form,
/// and <b>never from a JSON body</b> — which the model binder is nevertheless happy to bind from. So the shape of
/// the hole survives into JSON, and the same two guards are applied here: the route value is pinned with
/// <see cref="FromRouteAttribute"/>, and it is checked against the store BTCPay actually authorised, which is the
/// only value in the request that a body cannot influence. No request model in this API carries a store id at all
/// (see <see cref="SparkApiModels"/>), so there is nothing for a body to name.
/// </para>
/// <para>
/// <b>Secrets.</b> A generated recovery phrase is returned exactly once, in the response to the
/// <see cref="Provision"/> call that generated it, and is documented as such on
/// <see cref="SparkProvisionResponse.Mnemonic"/>. There is no endpoint that reads a seed back, no status field that
/// could contain one, and no path that echoes a submitted phrase — including validation errors, which describe what
/// is wrong with a phrase without quoting it.
/// </para>
/// <para>
/// <b>Exit paths.</b> Every sweep this API can cause is a cooperative exit, by owner decision. There is no
/// unilateral-exit endpoint, no parameter that selects one, and no way to
/// reach one; "drain" in the sweep settings means the SDK's <c>FeesIncluded</c> fee policy and nothing else.
/// The plugin's experimental unilateral-exit flow is deliberately UI-only and stays unreachable from here.
/// </para>
/// <para>
/// <b>Not on the graph BTCPay builds at startup.</b> A controller is constructed per request, so nothing here
/// participates in the startup-graph cycle that deadlocked BTCPay in PR #6 — see <c>SparkPlugin</c>'s
/// registrations. It takes no dependency core does not already hand this plugin's MVC controller.
/// </para>
/// </remarks>
[ApiController]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
[EnableCors(CorsPolicies.All)]
public class GreenfieldSparkController : ControllerBase
{
    /// <summary>Returned when the store has never set Spark up and the call needs it to have.</summary>
    private const string NotConfiguredCode = "spark-not-configured";

    /// <summary>Returned when the server does not let this caller create hot wallets.</summary>
    private const string HotWalletNotAllowedCode = "hot-wallet-not-allowed";

    private readonly ISparkStoreSettingsStore _settingsStore;
    private readonly SparkStoreProvisioner _provisioner;
    private readonly SparkSeedResolver _seedResolver;
    private readonly SparkStoreStatusReader _statusReader;
    private readonly SparkSweepSettingsService _sweepSettings;
    private readonly SparkSweepEngine _sweepEngine;
    private readonly SparkDepositService _deposits;
    private readonly SparkStableBalanceService _stableBalance;
    private readonly ILogger<GreenfieldSparkController> _logger;

    public GreenfieldSparkController(
        ISparkStoreSettingsStore settingsStore,
        SparkStoreProvisioner provisioner,
        SparkSeedResolver seedResolver,
        SparkStoreStatusReader statusReader,
        SparkSweepSettingsService sweepSettings,
        SparkSweepEngine sweepEngine,
        SparkDepositService deposits,
        SparkStableBalanceService stableBalance,
        ILogger<GreenfieldSparkController> logger)
    {
        _settingsStore = settingsStore;
        _provisioner = provisioner;
        _seedResolver = seedResolver;
        _statusReader = statusReader;
        _sweepSettings = sweepSettings;
        _sweepEngine = sweepEngine;
        _deposits = deposits;
        _stableBalance = stableBalance;
        _logger = logger;
    }

    #region Status

    /// <summary>
    /// Reports whether the store has Spark configured and what state its wallet is in.
    /// </summary>
    /// <remarks>
    /// Answers <c>200</c> with <see cref="SparkStatusData.Configured"/> false for a store that has never set Spark
    /// up, rather than <c>404</c>: "is this store configured" is the question the endpoint exists to answer, and
    /// making the answer an error status would force every caller to treat a normal state as a failure.
    /// </remarks>
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    [HttpGet("~/api/v1/stores/{storeId}/spark")]
    public async Task<IActionResult> GetStatus([FromRoute] string storeId, CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return StoreNotFound();

        var status = await _statusReader.ReadAsync(store.Id, cancellationToken).ConfigureAwait(false);
        return Ok(ToModel(status));
    }

    #endregion

    #region Provisioning

    /// <summary>
    /// Configures the store's Spark wallet from a chosen seed source and points its Lightning payment method at it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Calling this on a store that already has Spark configured replaces its seed. The store's sweep configuration
    /// and any API-key override are carried across; the previous wallet's storage directory is kept.
    /// </para>
    /// <para>
    /// The order of operations is the provisioner's and is load-bearing: the seed is validated before anything is
    /// written, the settings are stored (which is what starts the wallet), <b>the wallet is confirmed running</b>,
    /// and only then is the store's Lightning payment method pointed at it. A wallet that will not start rolls the
    /// settings back rather than leaving a store that looks configured and takes no payments.
    /// </para>
    /// </remarks>
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    [HttpPost("~/api/v1/stores/{storeId}/spark")]
    public async Task<IActionResult> Provision(
        [FromRoute] string storeId,
        // Empty body allowed so the refusal comes from this plugin's own seed-source message rather than from the
        // framework's "a non-empty request body is required".
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] SparkProvisionRequest? request,
        CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return StoreNotFound();

        request ??= new SparkProvisionRequest();

        if (!SparkProvisionRequest.TryParseSeedSource(request.SeedSource, out var seedSource))
        {
            // Parsed here rather than bound as an enum so this message is the plugin's own; see the remarks on
            // SparkProvisionRequest.SeedSource for why that matters next to a mnemonic field.
            ModelState.AddModelError(
                JsonName(nameof(request.SeedSource)),
                "Set seedSource to one of: generate (a new phrase, returned once in this response), import (supply "
                + "it in mnemonic), or hotWallet (reuse this store's on-chain BTCPay wallet seed).");
            return this.CreateValidationError(ModelState);
        }

        var seed = await _seedResolver
            .ResolveAsync(User, store.Id, seedSource, request.Mnemonic, cancellationToken)
            .ConfigureAwait(false);

        if (!seed.Succeeded)
        {
            switch (seed.Rejection)
            {
                case SparkSeedRejection.HotWalletNotAllowed:
                    // A server policy, not a bad request, and it covers all three seed sources — reusing the
                    // store's existing on-chain seed included, because that copies key material into a second
                    // wallet, which is the capability the policy controls.
                    return this.CreateAPIError(403, HotWalletNotAllowedCode, seed.Error!);

                case SparkSeedRejection.InvalidMnemonic:
                    // Attached to the mnemonic field. The message never quotes the phrase: NBitcoin's own wording
                    // for an unrecognised word contains that word, which is why none of its messages are relayed.
                    ModelState.AddModelError(JsonName(nameof(request.Mnemonic)), seed.Error!);
                    return this.CreateValidationError(ModelState);

                default:
                    ModelState.AddModelError(JsonName(nameof(request.SeedSource)), seed.Error!);
                    return this.CreateValidationError(ModelState);
            }
        }

        var result = await _provisioner
            .ProvisionAsync(store.Id, seed.Mnemonic, seedSource, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // 422: the request was well formed and the server understood it, and provisioning was attempted and
            // rolled back. Attached to the field the caller can act on — the phrase, when they supplied one.
            ModelState.AddModelError(
                JsonName(seedSource is SeedSource.Imported
                    ? nameof(request.Mnemonic)
                    : nameof(request.SeedSource)),
                result.Error!);
            return this.CreateValidationError(ModelState);
        }

        var status = await _statusReader.ReadAsync(store.Id, cancellationToken).ConfigureAwait(false);

        return Ok(new SparkProvisionResponse
        {
            // The one and only disclosure. Null for import (the caller already holds it) and for hotWallet (it is
            // the store's on-chain seed, which this API will not hand out).
            Mnemonic = seedSource is SeedSource.Generated ? seed.Mnemonic : null,
            Status = ToModel(status)
        });
    }

    /// <summary>
    /// Removes the store's Spark configuration: the keys are destroyed on this server.
    /// </summary>
    /// <remarks>
    /// The same path as the setup page's remove button, including clearing the store's Lightning payment-method
    /// configuration — but <b>only when it still points at this store's Spark wallet</b>. Another node's connection
    /// string, or another store's Spark wallet, is left exactly as it was; a macaroon or certificate that exists
    /// nowhere else is not this plugin's to discard.
    /// <para>
    /// The SDK storage directory is deliberately kept: it holds the record of settled payments for a wallet whose
    /// recovery phrase the merchant still controls, and after this call that phrase is the only way to reach any
    /// remaining funds.
    /// </para>
    /// </remarks>
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    [HttpDelete("~/api/v1/stores/{storeId}/spark")]
    public async Task<IActionResult> Remove([FromRoute] string storeId, CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return StoreNotFound();

        // A settings lookup rather than a full status read: tearing a store down has no business probing Spark's
        // published network status or reading a balance it is about to make unreachable.
        if (await _settingsStore.GetAsync(store.Id).ConfigureAwait(false) is null)
            return NotConfigured();

        await _provisioner.RemoveAsync(store.Id).ConfigureAwait(false);
        return Ok();
    }

    #endregion

    #region Sweep

    /// <summary>
    /// Reports the store's sweep configuration and a page of its sweep history.
    /// </summary>
    /// <remarks>
    /// The history includes sweeps that did not happen. A <c>Refused</c> record carries the stable
    /// <c>refusalCode</c>, the sentence a merchant would read, and an <c>attemptCount</c> — a recurring automatic
    /// refusal is folded onto one row per cause per day rather than filed once per pass, so a store parked on a
    /// refusal shows one row that keeps incrementing instead of hundreds.
    /// </remarks>
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    [HttpGet("~/api/v1/stores/{storeId}/spark/sweep")]
    public async Task<IActionResult> GetSweepConfiguration(
        [FromRoute] string storeId,
        int skip = 0,
        int count = Constants.SweepHistoryPageSize,
        CancellationToken cancellationToken = default)
    {
        if (!ResolveStore(storeId, out var store))
            return StoreNotFound();

        var current = await _sweepSettings.ReadAsync(store.Id, cancellationToken).ConfigureAwait(false);
        if (!current.Configured)
            return NotConfigured();

        var history = await _sweepSettings
            .ReadHistoryAsync(store.Id, skip, count, cancellationToken)
            .ConfigureAwait(false);

        return Ok(ToModel(current, history));
    }

    /// <summary>
    /// Replaces the store's sweep configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A full replacement, as <c>PUT</c> implies: a field the body omits takes its default, it does not keep its
    /// current value. Read the configuration first and send it back modified.
    /// </para>
    /// <para>
    /// Validated by exactly the rules the settings form is validated by, in the same service, including the two
    /// that look reasonable field by field and then never sweep anything: a threshold that cannot clear its own
    /// minimum, and a fee-on-top policy with no reserve to charge the fee against. <b>There is no configuration
    /// this endpoint accepts that switches the fee guard off</b> — clearing the percentage without setting a flat
    /// ceiling is refused here, and whatever is stored, the engine applies a hard backstop no configuration can
    /// lift at the point money would move.
    /// </para>
    /// </remarks>
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    [HttpPut("~/api/v1/stores/{storeId}/spark/sweep")]
    public async Task<IActionResult> UpdateSweepConfiguration(
        [FromRoute] string storeId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] SweepSettingsInput? request,
        CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return StoreNotFound();

        // A body of `null` or `{}` is a full replacement with defaults, which is what PUT means. It is still
        // validated, and the defaults do not pass validation for an enabled configuration by accident.
        request ??= new SweepSettingsInput();

        var applied = await _sweepSettings.SaveAsync(store.Id, request, cancellationToken).ConfigureAwait(false);

        switch (applied.Status)
        {
            case SparkSweepSettingsSaveStatus.NotConfigured:
                return NotConfigured();

            case SparkSweepSettingsSaveStatus.Invalid:
                foreach (var error in applied.Errors)
                    ModelState.AddModelError(JsonName(error.Field), error.Error);
                return this.CreateValidationError(ModelState);
        }

        // Re-read rather than echoed back, so the response is what is stored and what a subsequent GET will say.
        var current = await _sweepSettings.ReadAsync(store.Id, cancellationToken).ConfigureAwait(false);
        var history = await _sweepSettings
            .ReadHistoryAsync(store.Id, 0, Constants.SweepHistoryPageSize, cancellationToken)
            .ConfigureAwait(false);

        return Ok(ToModel(current, history));
    }

    /// <summary>
    /// Sweeps the store's Spark balance on-chain now, or quotes what a sweep would do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs through <see cref="SparkSweepEngine"/> — the same method the periodic task calls, with the trigger set
    /// to manual. A manual trigger relaxes exactly two things, both of which answer "should I be looking?": the
    /// automatic-sweep switch and the balance threshold. Every guard that answers "is this safe and economic?" —
    /// the minimum sweep, the fee ceiling, the dust floor, the destination rules, the single-flight lock, and the
    /// refusal to send while an earlier sweep's fate is unknown — applies identically. The sweep is recorded with
    /// its idempotency key <em>before</em> the send, which is what makes a crashed sweep resolvable rather than a
    /// guess.
    /// </para>
    /// <para>
    /// With <c>preview: true</c> nothing is sent, no address is reserved from the store's wallet, and no record is
    /// written; the response is the live quote and the destination a sweep would use. The quote expires in about a
    /// minute and the engine re-quotes on a real sweep, so it is an estimate.
    /// </para>
    /// <para>
    /// <b>Always a cooperative exit.</b> There is no parameter here or anywhere else in this API that selects a
    /// unilateral exit.
    /// </para>
    /// <para>
    /// <b>A <c>200</c> means the engine reached a decision, not that money moved.</b> Read <c>outcome</c>: only
    /// <c>Swept</c> means a cooperative exit was accepted. See <see cref="SparkSweepResultData"/> for why refusals
    /// are not reported as an error status.
    /// </para>
    /// </remarks>
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    [HttpPost("~/api/v1/stores/{storeId}/spark/sweep")]
    public async Task<IActionResult> Sweep(
        [FromRoute] string storeId,
        // Empty body allowed: "sweep now" is the common call and should not require one.
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] SparkSweepRequest? request,
        CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return StoreNotFound();

        // A settings lookup rather than a configuration read: the engine re-reads everything it needs, syncs the
        // wallet itself, and an extra balance read here would be a second, staler number in the same request.
        if (await _settingsStore.GetAsync(store.Id).ConfigureAwait(false) is null)
            return NotConfigured();

        if (request?.Preview is true)
        {
            var preview = await _sweepEngine.PreviewAsync(store.Id, cancellationToken).ConfigureAwait(false);
            return Ok(ToModel(preview));
        }

        var result = await _sweepEngine
            .RunAsync(store.Id, SweepTrigger.Manual, cancellationToken)
            .ConfigureAwait(false);

        return Ok(SparkSweepResultData.From(result));
    }

    #endregion

    #region Deposits

    /// <summary>
    /// The store's on-chain deposit address, and anything sent to it that has not been credited.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The address is static and safe to save. Sending to it credits the store's Spark balance once the deposit
    /// has three confirmations and the SDK has claimed it — <b>budget half an hour or more</b>, not seconds.
    /// </para>
    /// <para>
    /// <b>Read <c>deposits</c> as well as <c>address</c>.</b> A claim is capped by a fee ceiling, and a deposit
    /// whose claim needs more than the ceiling allows is not retried at a lower price — it sits unclaimed
    /// indefinitely and is visible only here. That is the failure this endpoint exists to make visible, and the
    /// claim endpoint is how it is fixed.
    /// </para>
    /// </remarks>
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    [HttpGet("~/api/v1/stores/{storeId}/spark/deposit")]
    public async Task<IActionResult> GetDeposits([FromRoute] string storeId, CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return StoreNotFound();

        var view = await _deposits.ReadAsync(store.Id, cancellationToken).ConfigureAwait(false);
        if (!view.Configured)
            return NotConfigured();

        return Ok(SparkDepositData.From(view));
    }

    /// <summary>
    /// Claims one deposit the SDK could not claim on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Omit <c>maxFeeSats</c> for the ordinary case: the fee the claim actually needs is reported by the SDK on
    /// the very error that stranded the deposit, and that is what will be used.
    /// </para>
    /// <para>
    /// <b>Guarded, and not only by the store's configured ceiling.</b> Whatever that ceiling says, the plugin
    /// refuses to spend more than half a deposit on claiming it — a rate-based cap knows nothing about the size
    /// of the deposit it is paid out of, and this is the only place the two are compared.
    /// </para>
    /// <para>
    /// A <c>200</c> means the plugin reached a decision. Read <c>status</c>: only <c>Claimed</c> means the claim
    /// was broadcast. A refusal spends nothing.
    /// </para>
    /// </remarks>
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    [HttpPost("~/api/v1/stores/{storeId}/spark/deposit/claim")]
    public async Task<IActionResult> ClaimDeposit(
        [FromRoute] string storeId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] SparkClaimDepositRequest? request,
        CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return StoreNotFound();

        if (await _settingsStore.GetAsync(store.Id).ConfigureAwait(false) is null)
            return NotConfigured();

        request ??= new SparkClaimDepositRequest();

        if (string.IsNullOrWhiteSpace(request.TxId))
        {
            ModelState.AddModelError(
                JsonName(nameof(request.TxId)),
                "Name the deposit to claim, by the transaction id and output index reported on "
                + "GET /api/v1/stores/{storeId}/spark/deposit.");
            return this.CreateValidationError(ModelState);
        }

        var outcome = await _deposits
            .ClaimAsync(store.Id, request.TxId!, request.Vout, request.MaxFeeSats, cancellationToken)
            .ConfigureAwait(false);

        return Ok(new SparkClaimDepositResponse
        {
            Status = outcome.Status,
            Succeeded = outcome.Succeeded,
            Message = outcome.Message,
            FeeSats = outcome.FeeSats
        });
    }

    #endregion

    #region Stable Balance

    /// <summary>
    /// The store's Stable Balance configuration, and what its wallet is actually doing.
    /// </summary>
    /// <remarks>
    /// <c>desiredActive</c> is the setting and <c>activeLabel</c> is the wallet. They can legitimately disagree
    /// — the SDK caches the active label per wallet, so a replaced seed starts deactivated — and the plugin
    /// reports the disagreement rather than reconciling it, because reconciling means converting a merchant's
    /// balance without being asked. <c>PUT</c> or the re-apply call is what converges them.
    /// </remarks>
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    [HttpGet("~/api/v1/stores/{storeId}/spark/stable-balance")]
    public async Task<IActionResult> GetStableBalance(
        [FromRoute] string storeId,
        CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return StoreNotFound();

        var view = await _stableBalance.ReadAsync(store.Id, cancellationToken).ConfigureAwait(false);
        if (!view.Configured)
            return NotConfigured();

        return Ok(SparkStableBalanceData.From(view));
    }

    /// <summary>
    /// Replaces the store's Stable Balance configuration and applies it to the wallet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This moves money.</b> Enabling queues a conversion of the store's Bitcoin balance into the configured
    /// stablecoin and disabling queues the reverse; both run on Spark's own background worker, take a spread,
    /// and report nothing when they finish — <b>no SDK event exists for a conversion</b>, so the only way to
    /// observe the result is to read this endpoint again later.
    /// </para>
    /// <para>
    /// <c>disclosureAcknowledged</c> must be true to enable. Holding a store's balance in USDB adds a regulated
    /// issuer who can freeze it, on top of the Spark operators; the API enforces the same acknowledgement the
    /// settings page does, because a disclosure one surface skips is a disclosure with a documented bypass.
    /// </para>
    /// <para>
    /// Mainnet only. On any other network this is refused rather than stored, because the SDK would accept the
    /// configuration and then silently never convert anything.
    /// </para>
    /// <para>
    /// <b>An empty body is refused rather than treated as a replacement with defaults</b>, which is how every
    /// other <c>PUT</c> in this API behaves. The default here is <c>enabled: false</c>, and applying it converts
    /// the store's whole stablecoin balance back to Bitcoin at the conversion spread — a money movement is not
    /// something a caller who sent no body should be able to trigger.
    /// </para>
    /// </remarks>
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    [HttpPut("~/api/v1/stores/{storeId}/spark/stable-balance")]
    public async Task<IActionResult> UpdateStableBalance(
        [FromRoute] string storeId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] StableBalanceInput? request,
        CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return StoreNotFound();

        // NOT defaulted to "off".
        //
        // Everywhere else in this API an omitted body means "replace with defaults", and that is right for a
        // configuration. It is wrong here, because the default is `enabled: false` and applying it *converts
        // the store's entire stablecoin balance back to Bitcoin* at the DEX spread — a money movement, queued
        // by a caller who sent no body at all and passed no disclosure gate. An accidental `PUT` with an empty
        // body is not consent to convert.
        if (request is null)
        {
            ModelState.AddModelError(
                JsonName(nameof(StableBalanceInput.Enabled)),
                "Send an explicit body. This endpoint replaces the whole configuration, and defaulting an "
                + "omitted one would mean 'off' — which converts any stablecoin balance back to Bitcoin at the "
                + "conversion spread. Send {\"enabled\": false} if that is what you want.");
            return this.CreateValidationError(ModelState);
        }

        var result = await _stableBalance.SaveAsync(store.Id, request, cancellationToken).ConfigureAwait(false);

        switch (result.Status)
        {
            case SparkStableBalanceStatus.NotConfigured:
                return NotConfigured();

            case SparkStableBalanceStatus.Invalid:
                foreach (var error in result.Errors)
                    ModelState.AddModelError(JsonName(error.Field), error.Error);
                return this.CreateValidationError(ModelState);

            case SparkStableBalanceStatus.Unavailable:
            {
                // The store's real state, not a bare error.
                //
                // This returned a plain API error, so a caller saw nulls for desiredActive, activeLabel and
                // needsReapply — on the one response where those are the facts that matter. The setting *was*
                // stored and the wallet *was not* changed, which is a disagreement the caller needs to see and
                // act on; hiding it behind an error code made a mainnet failure hard to read from the API alone.
                var failed = await _stableBalance.ReadAsync(store.Id, cancellationToken).ConfigureAwait(false);
                return new ObjectResult(SparkStableBalanceData.From(failed, result.Message))
                {
                    StatusCode = StatusCodes.Status422UnprocessableEntity
                };
            }
        }

        var view = await _stableBalance.ReadAsync(store.Id, cancellationToken).ConfigureAwait(false);
        return Ok(SparkStableBalanceData.From(view, result.Message));
    }

    #endregion

    #region Mapping

    private SparkStatusData ToModel(SparkStoreStatus status) => new()
    {
        Configured = status.Configured,
        SeedSource = status.SeedSource,
        WalletRunning = status.WalletRunning,
        IdentityPubkey = status.IdentityPubkey,
        BalanceSats = status.BalanceSats,
        WalletError = status.WalletError,
        NetworkStatus = SparkNetworkStatusData.From(status.NetworkStatus),
        LightningWiring = status.LightningWiring,
        LightningEnabledForCheckout = status.LightningEnabledForCheckout,
        StorageDirectory = status.StorageDirectoryFor(User)
    };

    private SparkSweepConfigurationData ToModel(SparkSweepSettingsView view, SparkSweepHistoryPage history) => new()
    {
        Settings = view.Settings,
        WalletRunning = view.WalletRunning,
        BalanceSats = view.BalanceSats,
        StoreWalletStatus = view.StoreWalletStatus,
        StoreWalletReason = view.StoreWalletReason,
        Network = _sweepSettings.Network.ChainName.ToString(),
        Total = history.Total,
        Skip = history.Skip,
        Count = history.Count,
        History = history.Records.Select(SparkSweepRecordData.From).ToList()
    };

    private static SparkSweepPreviewData ToModel(SweepPreview preview) => new()
    {
        CanSweep = preview.CanSweep,
        RefusalReason = preview.RefusalReason,
        BalanceSats = preview.BalanceSats,
        SweepableSats = preview.SweepableSats,
        // Null on a token-funded cross-chain sweep, deliberately: the amount is not in satoshi there, and
        // reporting a sats figure that happens to be the sats balance would be a number a caller could act on
        // and be wrong. `amount` carries the unit.
        AmountSats = (preview.Amount as SparkSendAmount.Bitcoin)?.Sats ?? preview.Quote?.AmountSats,
        Amount = preview.Amount?.ToString(),
        CrossChainQuote = preview.CrossChainQuote is { } crossChain
            ? SparkCrossChainQuoteData.From(crossChain)
            : null,
        RecipientAmountSats = preview.Quote?.RecipientAmountSats,
        Destination = preview.Destination is { } destination
            ? new SparkSweepDestinationData(destination.Address, destination.Mode, destination.Rotates)
            : null,
        Quote = preview.Quote is { } quote ? SparkSweepQuoteData.From(quote) : null,
        Settings = SweepSettingsInput.From(preview.Settings)
    };

    #endregion

    /// <summary>
    /// The store BTCPay authorised this request against, or false when the request must not proceed.
    /// </summary>
    /// <param name="routeStoreId">
    /// The <see cref="FromRouteAttribute"/>-bound id. Compared, never used as the working value.
    /// </param>
    /// <remarks>
    /// <para>
    /// Two independent guards, mirroring <c>SparkController.ResolveStore</c>, because the cost of being wrong here
    /// is one store's money moving on another store's authority.
    /// </para>
    /// <para>
    /// The first is the store BTCPay's authorisation filter recorded: it is the store the caller was actually
    /// authorised for, and no part of the request body can influence it. The second is the equality check against
    /// the route value — redundant while <see cref="FromRouteAttribute"/> pins the binding source and core resolves
    /// the scope from the same route data, and kept because it is what fails closed if a future edit changes either
    /// of those. A mismatch is reported as "not found" rather than "forbidden" so it says nothing about whether the
    /// other store exists.
    /// </para>
    /// </remarks>
    private bool ResolveStore(string? routeStoreId, [NotNullWhen(true)] out StoreData? store)
    {
        store = HttpContext.GetStoreDataOrNull();
        if (store is null)
            return false;

        if (!string.Equals(store.Id, routeStoreId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "A Spark API request authorised for store {AuthorisedStoreId} carried store id {SuppliedStoreId}; "
                + "refusing it", store.Id, routeStoreId);
            store = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// The JSON member name a CLR property serialises to, for a validation error's <c>path</c>.
    /// </summary>
    /// <remarks>
    /// BTCPay serialises Greenfield responses through a <c>DefaultContractResolver</c> carrying a
    /// <c>CamelCaseNamingStrategy</c>, so the wire name of <c>MaxFeePercent</c> is <c>maxFeePercent</c>. A
    /// validation error whose <c>path</c> said <c>MaxFeePercent</c> would name a field the caller never sent, so the
    /// same strategy object is asked rather than the first character being lower-cased by hand — that shortcut
    /// happens to be right for every field this API has today and is wrong for the first acronym-initial one
    /// somebody adds.
    /// </remarks>
    private static string JsonName(string clrPropertyName) =>
        NamingStrategy.GetPropertyName(clrPropertyName, hasSpecifiedName: false);

    private static readonly CamelCaseNamingStrategy NamingStrategy = new();

    /// <summary>
    /// The response to a request whose store could not be established. Says nothing about whether the store exists.
    /// </summary>
    private IActionResult StoreNotFound() =>
        this.CreateAPIError(404, "store-not-found", "The store was not found.");

    private IActionResult NotConfigured() =>
        this.CreateAPIError(
            404, NotConfiguredCode,
            "Flint is not set up for this store. POST to /api/v1/stores/{storeId}/spark to set it up.");
}
