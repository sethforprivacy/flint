using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Models.StoreViewModels;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Models;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using SparkPaymentDirection = BTCPayServer.Plugins.Flint.Sdk.SparkPaymentDirection;
using SparkSendResult = BTCPayServer.Plugins.Flint.Models.SparkSendResult;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.Flint.Controllers;

/// <summary>
/// The store-facing Spark pages: seed setup, status, and removal.
/// </summary>
/// <remarks>
/// <para>
/// Gated at the class level on <c>CanViewStoreSettings</c>, with everything that writes re-gated on
/// <c>CanModifyStoreSettings</c> — core's own convention for store settings controllers.
/// </para>
/// <para>
/// <b>Every action resolves its store through <see cref="ResolveStore"/> and never trusts a bound
/// parameter.</b> This is not defensive style, it is the fix for a real cross-store hole. BTCPay's
/// authorisation handler reads the store id out of <em>route</em> data, while ASP.NET Core model binding
/// prefers <em>form</em> values over route values — so an action that acted on a bound
/// <c>string storeId</c> could be authorised against the caller's own store and then act on somebody
/// else's, simply by posting <c>storeId=victim</c> in the body. The consequence was another store's
/// Lightning invoices minting into the attacker's wallet. Route binding is pinned with
/// <see cref="FromRouteAttribute"/> <em>and</em> the value is checked against the store BTCPay actually
/// authorised, because either alone would be enough and neither is expensive.
/// </para>
/// <para>
/// <b>The seed is never rendered back from storage.</b> A freshly generated seed passes through core's
/// recovery-seed screen once, on the way in. There is no reveal-seed action, deliberately: the merchant's own
/// backup is the recovery path, and the settings blob is encrypted with keys that live in the BTCPay data
/// directory.
/// </para>
/// <para>
/// The sweep pages are setup page 2. They are reached from the status page and from the setup page through the
/// <c>spark-status-post-body</c> and <c>spark-setup-post-body</c> extension points, and
/// <see cref="SparkStoreProvisioner"/> carries a store's sweep settings across a seed change.
/// </para>
/// <para>
/// <b>Nothing here decides whether a sweep is safe.</b> The sweep actions are a thin shell over
/// <see cref="SparkSweepEngine"/>: the fee ceiling, the economic floor, the dust floor and the destination rules
/// are all enforced inside the engine, against a live quote, on both the automatic and the manual path. The form
/// validation on the settings page is a courtesy to the merchant, not the guard.
/// </para>
/// <para>
/// <b>Nothing here decides anything the API decides differently.</b> Since the Greenfield surface arrived
/// (<see cref="GreenfieldSparkController"/>), every decision the two share lives in a service both call:
/// <see cref="SparkSeedResolver"/> for the seed sources and the hot-wallet policy gate,
/// <see cref="SparkStoreStatusReader"/> for what "status" is, <see cref="SparkSweepSettingsService"/> for what a
/// valid sweep configuration is, <see cref="SparkStoreProvisioner"/> for provisioning and removal, and the engine
/// for sweeping. What is left in this class is rendering and redirecting.
/// </para>
/// </remarks>
// The setup page deliberately re-renders a rejected import with the recovery phrase the person
// just typed (see BuildSetupViewModel's remarks) — right behaviour for the page, but a cached
// response would then keep a mnemonic on browser or proxy machinery outside the session that
// typed it. Every page here is authorised, per-user, and followed by POSTs, so none of it is
// cacheable in principle and the refusal is stated once for the controller rather than per action.
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("plugins/{storeId}/spark")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanViewStoreSettings)]
// Stated rather than inherited. Every POST here changes money-handling configuration, and CSRF protection
// resting on a framework-wide default is protection nobody can see when reading this file.
[AutoValidateAntiforgeryToken]
public class SparkController : Controller
{
    private readonly ISparkStoreSettingsStore _settingsStore;
    private readonly SparkStoreProvisioner _provisioner;
    private readonly SparkLightningWiring _lightningWiring;
    private readonly SparkSeedResolver _seedResolver;
    private readonly SparkStoreStatusReader _statusReader;
    private readonly SparkSweepEngine _sweepEngine;
    private readonly SparkSweepSettingsService _sweepSettings;
    private readonly SparkDepositService _deposits;
    private readonly SparkStableBalanceService _stableBalance;
    private readonly ISparkUnilateralExitService _unilateralExit;
    private readonly ISparkStoreRuntime _storeRuntime;
    private readonly IExitStateBackupStore _exitStateBackupStore;
    private readonly CrossChainCatalog _crossChainCatalog;
    private readonly StablecoinPaymentService _stablecoins;
    private readonly SparkSendPaymentService _sendPayment;
    private readonly IAuthorizationService _authorizationService;
    private readonly ILogger<SparkController> _logger;

    public SparkController(
        ISparkStoreSettingsStore settingsStore,
        SparkStoreProvisioner provisioner,
        SparkLightningWiring lightningWiring,
        SparkSeedResolver seedResolver,
        SparkStoreStatusReader statusReader,
        SparkSweepEngine sweepEngine,
        SparkSweepSettingsService sweepSettings,
        SparkDepositService deposits,
        SparkStableBalanceService stableBalance,
        ISparkUnilateralExitService unilateralExit,
        ISparkStoreRuntime storeRuntime,
        IExitStateBackupStore exitStateBackupStore,
        CrossChainCatalog crossChainCatalog,
        StablecoinPaymentService stablecoins,
        SparkSendPaymentService sendPayment,
        IAuthorizationService authorizationService,
        ILogger<SparkController> logger)
    {
        _settingsStore = settingsStore;
        _provisioner = provisioner;
        _lightningWiring = lightningWiring;
        _seedResolver = seedResolver;
        _statusReader = statusReader;
        _sweepEngine = sweepEngine;
        _sweepSettings = sweepSettings;
        _deposits = deposits;
        _stableBalance = stableBalance;
        _unilateralExit = unilateralExit;
        _storeRuntime = storeRuntime;
        _exitStateBackupStore = exitStateBackupStore;
        _crossChainCatalog = crossChainCatalog;
        _stablecoins = stablecoins;
        _sendPayment = sendPayment;
        _authorizationService = authorizationService;
        _logger = logger;
    }

    /// <summary>
    /// Entry point from the navigation: the status page once configured, the setup page before that.
    /// </summary>
    [HttpGet("")]
    public async Task<IActionResult> Index([FromRoute] string storeId)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        var settings = await _settingsStore.GetAsync(store.Id).ConfigureAwait(false);
        return settings is null
            ? await RedirectToSetupOrDeny(store.Id).ConfigureAwait(false)
            : RedirectToAction(nameof(Status), new { storeId = store.Id });
    }

    #region Setup

    /// <summary>
    /// Setup page 1: choose where the store's Spark seed comes from.
    /// </summary>
    [HttpGet("setup")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> Setup([FromRoute] string storeId)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        return View(await BuildSetupViewModel(store.Id, new SparkSetupViewModel()).ConfigureAwait(false));
    }

    /// <summary>
    /// Provisions the store from the chosen seed source.
    /// </summary>
    /// <remarks>
    /// The generate path provisions <em>before</em> handing the seed to core's backup screen, following the
    /// Boltz pattern: that screen's confirm button is a plain GET of <c>ReturnUrl</c>, so there is no
    /// post-back to provision from afterwards. The consequence is deliberate — the merchant never sees a
    /// recovery phrase for a wallet that failed to start, and a phrase they do see is always the phrase the
    /// server actually stored.
    /// </remarks>
    [HttpPost("setup")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> Setup(
        [FromRoute] string storeId,
        SparkSetupViewModel vm,
        CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        // Shadowed so nothing below can reach the bound parameter by accident.
        storeId = store.Id;

        // Every seed source, and the hot-wallet policy gate that covers all three, decided in the one place the
        // API decides them too.
        var seed = await _seedResolver
            .ResolveAsync(User, storeId, vm.SeedSource, vm.ImportedMnemonic, cancellationToken)
            .ConfigureAwait(false);

        if (!seed.Succeeded)
        {
            if (seed.Rejection is SparkSeedRejection.HotWalletNotAllowed)
            {
                // A server-policy refusal rather than a form error: it is not about anything on the form, and the
                // setup page has already greyed the options out, so the banner is what explains the re-render.
                TempData[WellKnownTempData.ErrorMessage] = seed.Error;
                return RedirectToAction(nameof(Setup), new { storeId });
            }

            ModelState.AddModelError(
                seed.Rejection is SparkSeedRejection.InvalidMnemonic
                    ? nameof(vm.ImportedMnemonic)
                    : nameof(vm.SeedSource),
                seed.Error!);
            return View(await BuildSetupViewModel(storeId, vm).ConfigureAwait(false));
        }

        var mnemonic = seed.Mnemonic!;
        var result = await _provisioner
            .ProvisionAsync(storeId, mnemonic, vm.SeedSource, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // Covers both the seed problems the SDK only reports at connect time and the ones where it declines
            // to start without throwing at all — a seed another store owns, an unsupported chain.
            ModelState.AddModelError(
                vm.SeedSource is SeedSource.Imported ? nameof(vm.ImportedMnemonic) : string.Empty,
                result.Error!);
            return View(await BuildSetupViewModel(storeId, vm).ConfigureAwait(false));
        }

        // Applied after provisioning, never before: SaveAsync needs a configured store, and a sweep
        // configuration written against a wallet that failed to start would be settings for nothing.
        var sweepNotice = vm.EnableSweeping
            ? await TryEnableSweepingAtSetupAsync(storeId, vm, cancellationToken).ConfigureAwait(false)
            : null;

        // Same rule as sweeping: after provisioning, and never able to fail setup. Off mainnet the box is not
        // rendered, and the service refuses anyway.
        if (vm.EnableStablecoins && _stablecoins.Available
            && !await _stablecoins.SetEnabledAsync(storeId, true, cancellationToken).ConfigureAwait(false))
        {
            sweepNotice = string.Join(" ", new[]
            {
                sweepNotice,
                "USDC and USDT payments could not be turned on; use the switch on the Flint page."
            }.Where(part => !string.IsNullOrEmpty(part)));
        }

        if (vm.SeedSource is SeedSource.Generated)
        {
            // Core's screen, so the seed is shown the same way BTCPay shows its own: posted to the page
            // rather than put in a URL, with the "I have written it down" gate.
            return this.RedirectToRecoverySeedBackup(new RecoverySeedBackupViewModel
            {
                Mnemonic = mnemonic,

                // Accurate, and it keeps every field core forwards through its post-redirect form non-null.
                CryptoCode = "BTC",

                // The server does keep this seed, encrypted, which is the warning core's screen prints for
                // IsStored. Saying otherwise would be a lie a merchant might act on.
                IsStored = true,
                ReturnUrl = Url.Action(nameof(Status), new { storeId })
            });
        }

        TempData[WellKnownTempData.SuccessMessage] = sweepNotice is null
            ? "Flint is now set up for this store."
            : $"Flint is now set up for this store. {sweepNotice}";
        return RedirectToAction(nameof(Status), new { storeId });
    }

    /// <summary>
    /// Turns sweeping on as part of setup, returning a sentence for the merchant when it could not be done.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A failure here must not fail setup. The wallet is provisioned and working by this point, and the common
    /// reason sweeping cannot be enabled — the store has no on-chain wallet to sweep into, which
    /// <see cref="SparkSweepSettingsService.SaveAsync"/> checks rather than trusting the view — is a
    /// configuration gap the merchant can close later, not a reason to unwind a working Lightning wallet.
    /// </para>
    /// <para>
    /// Silence would be worse than either, though: a merchant who ticked the box and was told only "Spark is now
    /// set up" would believe their balance is being swept when nothing is. So the reason is carried into the
    /// success message.
    /// </para>
    /// </remarks>
    private async Task<string?> TryEnableSweepingAtSetupAsync(
        string storeId,
        SparkSetupViewModel vm,
        CancellationToken cancellationToken)
    {
        // Everything except these two keeps its default -- destination is the store's own wallet, and the fee
        // limits, minimum and confirmation speed are the ones the sweep page would have offered anyway.
        var input = new SweepSettingsInput
        {
            Enabled = true,
            BalanceThresholdSats = vm.SweepBalanceThresholdSats
        };

        var result = await _sweepSettings.SaveAsync(storeId, input, cancellationToken).ConfigureAwait(false);
        if (result.Status is SparkSweepSettingsSaveStatus.Saved)
            return null;

        var reason = result.Errors.Count > 0
            ? string.Join(" ", result.Errors.Select(e => e.Error))
            : "It could not be saved.";
        _logger.LogWarning(
            "Store {StoreId}: Spark was set up but sweeping could not be enabled from the setup page: {Reason}",
            storeId, reason);
        return $"Sweeping was not turned on: {reason} You can set it up on the Sweeps page.";
    }

    #endregion

    #region Status

    /// <summary>
    /// The default page once configured: wallet, network and Lightning wiring state.
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status([FromRoute] string storeId, CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        var status = await _statusReader.ReadAsync(storeId, cancellationToken).ConfigureAwait(false);
        if (!status.Configured)
            return await RedirectToSetupOrDeny(storeId).ConfigureAwait(false);

        var model = ToViewModel(storeId, status);

        // Read here rather than folded into SparkStoreStatusReader, so a failure to reach the service provider
        // for a deposit address cannot stop the status page rendering the wallet and Lightning state a merchant
        // came for. Both services already degrade internally; this only decides where the degraded values land.
        var deposits = await _deposits.ReadAsync(storeId, cancellationToken).ConfigureAwait(false);
        model.DepositAddress = deposits.Address;
        model.StuckDepositCount = deposits.Stuck.Count;

        model.StablecoinsAvailable = _stablecoins.Available;
        model.StablecoinsEnabled = await _stablecoins.IsEnabledAsync(storeId, cancellationToken).ConfigureAwait(false);

        model.StableBalanceAvailable = _stableBalance.Available;
        if (_stableBalance.Available)
        {
            var stable = await _stableBalance.ReadAsync(storeId, cancellationToken).ConfigureAwait(false);
            model.StableBalanceActive = stable.ActuallyActive;
            model.StableBalanceHolding = stable.Balance?.Describe();
        }

        return View(model);
    }

    /// <summary>
    /// Renders the shared status record as this page's view model. A projection, deliberately with no logic of its
    /// own: anything decided here would be a fact the API could not see.
    /// </summary>
    private SparkStatusViewModel ToViewModel(string storeId, SparkStoreStatus status) => new()
    {
        StoreId = storeId,
        SeedSource = status.SeedSource,
        WalletRunning = status.WalletRunning,
        IdentityPubkey = status.IdentityPubkey,
        BalanceSats = status.BalanceSats,
        WalletError = status.WalletError,
        NetworkStatus = status.NetworkStatus,
        LightningWiring = status.LightningWiring,
        LightningEnabledForCheckout = status.LightningEnabledForCheckout,
        StorageDirectory = status.StorageDirectoryFor(User)
    };

    /// <summary>
    /// Re-points the store's Lightning payment method at its Spark wallet.
    /// </summary>
    /// <remarks>
    /// The repair for a store whose Lightning configuration drifted — a merchant disabled Lightning, or tried
    /// another node — without making them run setup and re-handle a seed.
    /// <para>
    /// It re-reads the current wiring rather than trusting what the page it came from rendered, and refuses to
    /// overwrite another node without <paramref name="confirmed"/>. An LND or Core Lightning connection string
    /// carries macaroon or certificate material that exists nowhere else once it is gone, and a warning
    /// rendered by the previous GET is not consent: the state can have changed since, and the POST is
    /// reachable without that GET.
    /// </para>
    /// </remarks>
    [HttpPost("status/enable-lightning")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> EnableLightning(
        [FromRoute] string storeId,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        var settings = await _settingsStore.GetAsync(storeId).ConfigureAwait(false);
        if (settings?.PaymentKey is not { } paymentKey)
            return await RedirectToSetupOrDeny(storeId).ConfigureAwait(false);

        var wiring = await _lightningWiring
            .InspectAsync(storeId, paymentKey, cancellationToken)
            .ConfigureAwait(false);

        if (!confirmed && wiring.State is SparkLightningWiringState.OtherNode
                or SparkLightningWiringState.InternalNode)
        {
            var what = wiring.State is SparkLightningWiringState.InternalNode
                ? "BTCPay's internal Lightning node"
                : "another Lightning node";

            return View("Confirm", new ConfirmModel(
                "Use Flint for Lightning payments",
                $"This store currently uses <strong>{what}</strong>. Continuing replaces that configuration "
                + "with this store's Spark wallet. A connection string cannot be recovered afterwards — if it "
                + "contains a macaroon, certificate or password you do not have elsewhere, copy it out first.",
                "Replace it")
            {
                ActionName = nameof(EnableLightning),
                ActionValues = new { storeId, confirmed = true },
                ButtonClass = "btn-danger"
            });
        }

        if (await _lightningWiring.EnableAsync(storeId, paymentKey, cancellationToken).ConfigureAwait(false))
        {
            TempData[WellKnownTempData.SuccessMessage] =
                "This store's Lightning payment method now uses its Spark wallet.";
        }
        else
        {
            TempData[WellKnownTempData.ErrorMessage] = "The store's Lightning payment method could not be updated.";
        }

        return RedirectToAction(nameof(Status), new { storeId });
    }

    /// <summary>
    /// The one switch for USDC and USDT at checkout.
    /// </summary>
    /// <remarks>
    /// Both coins together, on purpose: a merchant deciding whether to take stablecoins is making one decision, and
    /// a store that wants only one can still switch the other off in BTCPay's own checkout settings, which this
    /// respects. The state lives in the store's payment methods alone, so this page and checkout cannot disagree.
    /// </remarks>
    [HttpPost("status/stablecoins")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> Stablecoins(
        [FromRoute] string storeId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        if (await _settingsStore.GetAsync(storeId).ConfigureAwait(false) is null)
            return await RedirectToSetupOrDeny(storeId).ConfigureAwait(false);

        if (enabled && !_stablecoins.Available)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                "USDC and USDT payments are only available on Bitcoin mainnet.";
        }
        else if (await _stablecoins.SetEnabledAsync(storeId, enabled, cancellationToken).ConfigureAwait(false))
        {
            TempData[WellKnownTempData.SuccessMessage] = enabled
                ? "Customers can now pay this store in USDC and USDT. Payments arrive in its Spark wallet."
                : "USDC and USDT payments are off for this store.";
        }
        else
        {
            TempData[WellKnownTempData.ErrorMessage] = "The store's payment methods could not be updated.";
        }

        return RedirectToAction(nameof(Status), new { storeId });
    }

    #endregion

    #region Sweep

    /// <summary>
    /// Setup page 2 and the sweep dashboard: configuration, plus the history of what has been swept.
    /// </summary>
    [HttpGet("sweep")]
    public async Task<IActionResult> Sweep(
        [FromRoute] string storeId,
        int skip = 0,
        int count = Constants.SweepHistoryPageSize,
        CancellationToken cancellationToken = default)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        var current = await _sweepSettings.ReadAsync(storeId, cancellationToken).ConfigureAwait(false);
        if (!current.Configured)
            return await RedirectToSetupOrDeny(storeId).ConfigureAwait(false);

        return View(await BuildSweepViewModel(storeId, current.Settings, skip, count, cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Saves the store's sweep configuration.
    /// </summary>
    /// <remarks>
    /// Read-modify-write of the whole settings object, because the sweep settings live inside it alongside the
    /// protected mnemonic — which this page must never see and never rewrite. The seed is left exactly as stored
    /// and only <see cref="SparkSettings.Sweep"/> is touched.
    /// </remarks>
    [HttpPost("sweep")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> Sweep(
        [FromRoute] string storeId,
        SparkSweepViewModel vm,
        CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        var input = vm.Settings ?? new SweepSettingsInput();

        // Validated and written by the service both surfaces share, so the form cannot accept a configuration the
        // API refuses or the other way round.
        var applied = await _sweepSettings.SaveAsync(storeId, input, cancellationToken).ConfigureAwait(false);

        if (applied.Status is SparkSweepSettingsSaveStatus.NotConfigured)
            return await RedirectToSetupOrDeny(storeId).ConfigureAwait(false);

        if (applied.Status is SparkSweepSettingsSaveStatus.Invalid)
        {
            foreach (var error in applied.Errors)
                ModelState.AddModelError($"{nameof(vm.Settings)}.{error.Field}", error.Error);

            return View(await BuildSweepViewModel(storeId, input, vm.Skip, vm.Count, cancellationToken)
                .ConfigureAwait(false));
        }

        if (!applied.WalletRunning)
        {
            // The settings were stored either way — SetAsync persists before it reconciles the instance — so this
            // reports rather than rolls back. A wallet that will not start is a separate problem from the sweep
            // configuration the merchant just saved, and telling them their save failed would be wrong.
            TempData[WellKnownTempData.ErrorMessage] =
                "The sweep settings were saved, but this store's Spark wallet is not running: "
                + (applied.WalletReason ?? "check the server logs for the reason.");
        }
        else
        {
            TempData[WellKnownTempData.SuccessMessage] = "Sweep settings saved.";
        }

        return RedirectToAction(nameof(Sweep), new { storeId });
    }

    /// <summary>
    /// Shows what a manual sweep would do, with a live quote, before anything is sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A POST despite changing nothing on this server. It calls out to the Spark service provider, and a GET would
    /// let a link preview or a browser prefetch do that on a merchant's behalf.
    /// </para>
    /// <para>
    /// The quote shown here expires in about a minute, so it is explicitly an estimate: the confirm step re-quotes
    /// and re-checks the fee limit against the new number. Nothing about the sweep is carried in the form.
    /// </para>
    /// </remarks>
    [HttpPost("sweep/preview")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> SweepPreview(
        [FromRoute] string storeId,
        CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        if (await _settingsStore.GetAsync(storeId).ConfigureAwait(false) is null)
            return await RedirectToSetupOrDeny(storeId).ConfigureAwait(false);

        var preview = await _sweepEngine.PreviewAsync(storeId, cancellationToken).ConfigureAwait(false);
        return View("SweepConfirm", new SparkSweepConfirmViewModel { StoreId = storeId, Preview = preview });
    }

    /// <summary>
    /// Sweeps now, through the same engine the periodic task uses.
    /// </summary>
    /// <remarks>
    /// There is deliberately no separate manual code path. The engine relaxes only the "should I be looking?"
    /// questions for a manual trigger — the automatic switch and the balance threshold — and applies every safety
    /// and economic guard identically, server-side, whatever the confirmation page happened to display.
    /// </remarks>
    [HttpPost("sweep/now")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> SweepNow([FromRoute] string storeId, CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        if (await _settingsStore.GetAsync(storeId).ConfigureAwait(false) is null)
            return await RedirectToSetupOrDeny(storeId).ConfigureAwait(false);

        var result = await _sweepEngine
            .RunAsync(storeId, SweepTrigger.Manual, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        TempData[result.Succeeded ? WellKnownTempData.SuccessMessage : WellKnownTempData.ErrorMessage] =
            result.Reason;

        return RedirectToAction(nameof(Sweep), new { storeId });
    }

    /// <summary>
    /// Fills in everything the sweep page needs beyond what the merchant posted.
    /// </summary>
    /// <remarks>
    /// The balance, the store-wallet availability and the paging bounds all come from
    /// <see cref="SparkSweepSettingsService"/>, so the page and the API report the same numbers with the same
    /// clamping. Only <paramref name="input"/> is taken from the caller — on a re-render that is the merchant's
    /// rejected form, which they need to see rather than have replaced by what is stored.
    /// </remarks>
    private async Task<SparkSweepViewModel> BuildSweepViewModel(
        string storeId,
        SweepSettingsInput input,
        int skip,
        int count,
        CancellationToken cancellationToken)
    {
        var current = await _sweepSettings.ReadAsync(storeId, cancellationToken).ConfigureAwait(false);
        var history = await _sweepSettings
            .ReadHistoryAsync(storeId, skip, count, cancellationToken)
            .ConfigureAwait(false);

        return new SparkSweepViewModel
        {
            StoreId = storeId,
            Settings = input,
            Skip = history.Skip,
            Count = history.Count,
            NetworkName = _sweepSettings.Network.ChainName.ToString(),
            // Read off the cached catalogue, which never blocks on the network: the worst a cold cache does is
            // offer the built-in floor for this one render. Built from the posted form rather than from what is
            // stored, so a re-render after a validation error shows the merchant the destination they chose.
            Picker = _crossChainCatalog.PickerFor(input.EvmChain, input.EvmAsset),
            // The same gate the validator applies, so the page cannot offer an option the save would refuse.
            CrossChainAvailable = _sweepSettings.Network == NBitcoin.Network.Main,
            WalletRunning = current.WalletRunning,
            BalanceSats = current.BalanceSats,
            StoreWalletStatus = current.StoreWalletStatus,
            StoreWalletReason = current.StoreWalletReason,
            HistoryTotal = history.Total,
            History = history.Records,
            RecommendedFees = await _sweepSettings
                .ReadRecommendedFeesAsync(storeId, cancellationToken)
                .ConfigureAwait(false)
        };
    }

    #endregion

    #region Advanced

    /// <summary>
    /// Wallet details, recovery-phrase provenance, the sweep tuning most stores never touch, and removal.
    /// </summary>
    /// <remarks>
    /// A page of its own rather than an accordion on the status page, so a merchant checking their balance
    /// never has to read past any of it.
    /// </remarks>
    [HttpGet("advanced")]
    public async Task<IActionResult> Advanced([FromRoute] string storeId, CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        var status = await _statusReader.ReadAsync(storeId, cancellationToken).ConfigureAwait(false);
        if (!status.Configured)
            return await RedirectToSetupOrDeny(storeId).ConfigureAwait(false);

        return View(await BuildAdvancedViewModel(storeId, status, input: null, cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Saves the two sweep-tuning fields the Advanced page owns: the reserve and the fee policy.
    /// </summary>
    /// <remarks>
    /// Everything else on the sweep configuration is read back from what is stored and carried through
    /// unchanged, so this form cannot alter a threshold or a destination it never displayed — and the whole
    /// merged object still goes through the one validation path both surfaces share.
    /// </remarks>
    [HttpPost("advanced/sweep")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> AdvancedSweep(
        [FromRoute] string storeId,
        SparkAdvancedViewModel vm,
        CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        var settings = await _settingsStore.GetAsync(storeId).ConfigureAwait(false);
        if (settings is null)
            return await RedirectToSetupOrDeny(storeId).ConfigureAwait(false);

        var posted = vm.Settings ?? new SweepSettingsInput();
        var input = SweepSettingsInput.From(settings.Sweep ?? new SweepSettings());
        input.ReserveSats = posted.ReserveSats;
        input.DrainWhenSweeping = posted.DrainWhenSweeping;

        var applied = await _sweepSettings.SaveAsync(storeId, input, cancellationToken).ConfigureAwait(false);

        if (applied.Status is SparkSweepSettingsSaveStatus.NotConfigured)
            return await RedirectToSetupOrDeny(storeId).ConfigureAwait(false);

        if (applied.Status is SparkSweepSettingsSaveStatus.Invalid)
        {
            foreach (var error in applied.Errors)
                ModelState.AddModelError($"{nameof(vm.Settings)}.{error.Field}", error.Error);

            var status = await _statusReader.ReadAsync(storeId, cancellationToken).ConfigureAwait(false);
            return View("Advanced",
                await BuildAdvancedViewModel(storeId, status, input, cancellationToken).ConfigureAwait(false));
        }

        TempData[WellKnownTempData.SuccessMessage] = "Sweep settings saved.";
        return RedirectToAction(nameof(Advanced), new { storeId });
    }

    /// <summary>
    /// Saves the merchant's own Breez API key, or clears it back to the plugin's built-in one.
    /// </summary>
    /// <remarks>
    /// The override exists for revocation resilience: every install shares the plugin's embedded key, and
    /// Breez's own suggestion is to let a merchant hold their own so a revocation of the shared key — never
    /// seen, but possible — costs them nothing. Storing the settings reconciles the running wallet, so the
    /// new key is what the SDK connects with immediately; a key the SDK refuses to start with is rolled back
    /// to the previous settings rather than left stored in front of a dead wallet.
    /// </remarks>
    [HttpPost("advanced/api-key")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> AdvancedApiKey(
        [FromRoute] string storeId,
        SparkAdvancedViewModel vm,
        CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        var previous = await _settingsStore.GetAsync(storeId).ConfigureAwait(false);
        if (previous is null)
            return await RedirectToSetupOrDeny(storeId).ConfigureAwait(false);

        var trimmed = vm.ApiKeyOverride?.Trim();
        string? newKey;
        if (vm.UseBuiltInKey)
        {
            newKey = null;
        }
        else if (string.IsNullOrEmpty(trimmed))
        {
            // The stored key is never displayed, so an empty field is what an untouched form looks like — it
            // cannot be allowed to mean "clear", which is what the explicit button is for.
            ModelState.AddModelError(
                nameof(vm.ApiKeyOverride),
                "Enter a key to save, or use the built-in-key button to remove the override.");

            var current = await _statusReader.ReadAsync(storeId, cancellationToken).ConfigureAwait(false);
            return View("Advanced",
                await BuildAdvancedViewModel(storeId, current, input: null, cancellationToken)
                    .ConfigureAwait(false));
        }
        else
        {
            newKey = trimmed;
        }

        if (string.Equals(newKey, previous.ApiKeyOverride, StringComparison.Ordinal))
        {
            // Nothing changed; do not bounce the wallet for it.
            return RedirectToAction(nameof(Advanced), new { storeId });
        }

        var updated = previous.Clone();
        updated.ApiKeyOverride = newKey;

        var applied = await _settingsStore.SetAsync(storeId, updated).ConfigureAwait(false);
        if (!applied.WalletRunning)
        {
            // The store must not be left holding a key its wallet will not start with. The revert re-applies
            // the previous settings, which brings the previous key's wallet back up.
            await _settingsStore.SetAsync(storeId, previous).ConfigureAwait(false);
            ModelState.AddModelError(
                nameof(vm.ApiKeyOverride),
                "The Spark wallet could not start with this API key"
                + (applied.Reason is { } reason ? $": {reason}" : ".")
                + " The previous key is back in effect.");

            var status = await _statusReader.ReadAsync(storeId, cancellationToken).ConfigureAwait(false);
            var model = await BuildAdvancedViewModel(storeId, status, input: null, cancellationToken)
                .ConfigureAwait(false);
            model.ApiKeyOverride = vm.ApiKeyOverride;
            return View("Advanced", model);
        }

        TempData[WellKnownTempData.SuccessMessage] = newKey is null
            ? "This store now uses the plugin's built-in Breez API key."
            : "This store now uses its own Breez API key.";
        return RedirectToAction(nameof(Advanced), new { storeId });
    }

    /// <summary>
    /// Fills in everything the Advanced page shows. <paramref name="input"/> is the merchant's rejected form
    /// on a re-render, or null to show what is stored.
    /// </summary>
    private async Task<SparkAdvancedViewModel> BuildAdvancedViewModel(
        string storeId,
        SparkStoreStatus status,
        SweepSettingsInput? input,
        CancellationToken cancellationToken)
    {
        if (input is null)
        {
            var current = await _sweepSettings.ReadAsync(storeId, cancellationToken).ConfigureAwait(false);
            input = current.Settings;
        }

        var settings = await _settingsStore.GetAsync(storeId).ConfigureAwait(false);

        return new SparkAdvancedViewModel
        {
            StoreId = storeId,
            SeedSource = status.SeedSource,
            WalletRunning = status.WalletRunning,
            IdentityPubkey = status.IdentityPubkey,
            StorageDirectory = status.StorageDirectoryFor(User),
            Settings = input,
            // Presence only — the key itself never leaves the settings blob for this page. Nobody else should
            // be using a store's key even though Breez does not treat it as a secret, so the page has no
            // business printing it into the DOM.
            HasApiKeyOverride = !string.IsNullOrEmpty(settings?.ApiKeyOverride),
            // When the stored backup was last written. Read from the store rather than the settings blob
            // because the plugin refreshes this file on its own as the wallet's leaves change — this page is
            // showing the operator how current the automation is, not asking whether they want a backup.
            ExitStateBackupTakenAt = await ReadBackupTakenAtAsync(storeId, cancellationToken).ConfigureAwait(false),
            PendingExitStateBackups = await ReadPendingBackupsAsync(storeId, cancellationToken).ConfigureAwait(false)
        };
    }

    /// <summary>
    /// The backups waiting to be imported, or none when that cannot be read. Never throws, for the reason
    /// <see cref="ReadBackupTakenAtAsync"/> gives.
    /// </summary>
    private async Task<IReadOnlyList<PendingExitStateBackup>> ReadPendingBackupsAsync(
        string storeId, CancellationToken cancellationToken)
    {
        try
        {
            return await _exitStateBackupStore.ListPendingAsync(storeId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Store {StoreId}: could not list the exit-state backups waiting to be imported", storeId);
            return [];
        }
    }

    /// <summary>
    /// When the stored exit-state backup was written, or null when none is — or when that cannot be read.
    /// </summary>
    /// <remarks>
    /// Never throws: every Advanced-page action renders through <see cref="BuildAdvancedViewModel"/>, and a
    /// filesystem fault reading one timestamp must not take the page — or, through BTCPay's plugin
    /// exception handler, the plugin — down with it. A read that failed is logged and shown as "none
    /// stored", which the Download action then re-checks for itself.
    /// </remarks>
    private async Task<DateTimeOffset?> ReadBackupTakenAtAsync(string storeId, CancellationToken cancellationToken)
    {
        try
        {
            return await _exitStateBackupStore.TakenAtAsync(storeId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Store {StoreId}: could not read when its exit-state backup was written", storeId);
            return null;
        }
    }

    #endregion

    #region Deposits

    /// <summary>
    /// Funding the store's Spark wallet on-chain: the address, and anything sent to it that has not arrived.
    /// </summary>
    /// <remarks>
    /// A page of its own rather than a section of the status page, because it has a job to do beyond showing an
    /// address: a deposit whose claim fee exceeded the ceiling is <em>never retried</em>, and this is the only
    /// place a merchant can see that and fix it. The status page links here and reports how many deposits are
    /// stuck.
    /// </remarks>
    [HttpGet("deposit")]
    public async Task<IActionResult> Deposit([FromRoute] string storeId, CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        var view = await _deposits.ReadAsync(storeId, cancellationToken).ConfigureAwait(false);
        if (!view.Configured)
            return await RedirectToSetupOrDeny(storeId).ConfigureAwait(false);

        return View(new SparkDepositViewModel { StoreId = storeId, Deposits = view });
    }

    /// <summary>
    /// Claims one stuck deposit, at the fee Spark said it needs or at one the merchant typed.
    /// </summary>
    /// <remarks>
    /// Nothing about the guard lives here. <see cref="SparkDepositService"/> re-reads the deposit, checks the
    /// store's ceiling, and applies the backstop that refuses to spend more than half a deposit on claiming it —
    /// so the page cannot authorise a claim the API would refuse, and a stale page cannot claim a deposit that
    /// has since been claimed.
    /// </remarks>
    [HttpPost("deposit/claim")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> ClaimDeposit(
        [FromRoute] string storeId,
        string txId,
        uint vout,
        long? maxFeeSats,
        CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        var outcome = await _deposits
            .ClaimAsync(storeId, txId, vout, maxFeeSats, cancellationToken)
            .ConfigureAwait(false);

        TempData[outcome.Succeeded ? WellKnownTempData.SuccessMessage : WellKnownTempData.ErrorMessage] =
            outcome.Message;

        return RedirectToAction(nameof(Deposit), new { storeId });
    }

    #endregion

    #region Unilateral exit

    /// <summary>
    /// Forcing this store's Spark balance on-chain without the operators' cooperation: the disclosure, the
    /// quote, the funding instructions, and the signed transactions the merchant broadcasts by hand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These actions decide nothing at all. They read, they relay the service's own refusal
    /// into the status banner, and they redirect back to the page — the same shape as
    /// <see cref="SweepNow"/>. The fee-rate bounds, the disclosure gate, the single-exit-per-store rule, the
    /// funding-sufficiency check and the explorer URL's validation all live in
    /// <see cref="ISparkUnilateralExitService"/>, so nothing a form can carry changes what is allowed.
    /// </para>
    /// </remarks>
    [HttpGet("exit")]
    public async Task<IActionResult> Exit([FromRoute] string storeId, CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        var page = await _unilateralExit.ReadAsync(storeId, cancellationToken).ConfigureAwait(false);
        var settings = await ReadSettingsForExitPageAsync(storeId).ConfigureAwait(false);
        return View(BuildExitViewModel(storeId, page, settings));
    }

    /// <summary>
    /// The store's settings for the exit page's own inputs, or null when they could not be read.
    /// </summary>
    /// <remarks>
    /// Read only to pre-fill the explorer box, so a failure here costs an empty box and nothing else — and a
    /// throw on this GET would cost the whole plugin: BTCPay 2.4 disables a plugin and restarts the server on an
    /// unhandled exception from one of its requests. The service's own read of the same settings is guarded the
    /// same way, and the page it returns already says when the store could not be read.
    /// </remarks>
    private async Task<SparkSettings?> ReadSettingsForExitPageAsync(string storeId)
    {
        try
        {
            return await _settingsStore.GetAsync(storeId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Store {StoreId}: could not read its settings for the exit page", storeId);
            return null;
        }
    }

    /// <summary>
    /// Records the operator's acceptance of the disclosure, which is what unlocks quoting.
    /// </summary>
    /// <remarks>
    /// A POST to its own route rather than a checkbox on the quote form, so the acceptance is a stored fact
    /// with its own moment — the Stable Balance pattern. A merchant who has read the warnings once is not asked
    /// again on every quote, and a quote that arrives without this having happened is refused server-side
    /// whatever any form said.
    /// </remarks>
    [HttpPost("exit/acknowledge")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> AcknowledgeExit([FromRoute] string storeId, CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        var result = await _unilateralExit
            .AcknowledgeDisclosureAsync(storeId, cancellationToken)
            .ConfigureAwait(false);

        RelayExitResult(result, "Acknowledged. You can now quote a unilateral exit for this store.");
        return RedirectToAction(nameof(Exit), new { storeId });
    }

    /// <summary>
    /// Quotes an exit at the requested fee rate and destination, creating the record the operator then funds.
    /// </summary>
    /// <remarks>
    /// The two posted values are handed to the service unexamined. It is the service that decides whether the
    /// rate is sane, whether the address belongs to this server's network, whether anything is worth exiting at
    /// that rate, and whether this store already has an exit in flight — and it says so in words this action
    /// only forwards.
    /// </remarks>
    [HttpPost("exit/quote")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> QuoteExit(
        [FromRoute] string storeId,
        SparkExitViewModel vm,
        CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        var result = await _unilateralExit
            .QuoteAsync(storeId, vm.FeeRateSatPerVbyte, vm.DestinationAddress ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        RelayExitResult(
            result,
            "Exit quoted. Nothing has been signed and nothing has moved — send the funding shown below, then "
            + "build.");
        return RedirectToAction(nameof(Exit), new { storeId });
    }

    /// <summary>
    /// Builds and signs the exit against the funding that has arrived. Broadcasts nothing.
    /// </summary>
    /// <remarks>
    /// Safe to post again after a failure, and the page says so: the service re-quotes the record's own leaves
    /// each time and, once a build has succeeded, hands the SDK the funding it committed to follow alongside
    /// anything new on the address — so a build that failed for want of funding succeeds once more has been
    /// sent, a rebuild never asks for the first funding twice, and steps already confirmed on-chain are skipped
    /// rather than rebuilt.
    /// </remarks>
    [HttpPost("exit/build")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    /// <param name="feeRateSatPerVbyte">
    /// Empty rebuilds at the record's rate; a value re-prices the build — the page's fee bump. Whether it is
    /// allowed is the service's judgement.
    /// </param>
    public async Task<IActionResult> BuildExit(
        [FromRoute] string storeId,
        string recordId,
        long? feeRateSatPerVbyte,
        CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        var result = await _unilateralExit
            .BuildAsync(storeId, recordId, feeRateSatPerVbyte, cancellationToken)
            .ConfigureAwait(false);

        RelayExitResult(
            result,
            "The exit is built and signed. Nothing has been broadcast — the transactions below are yours to "
            + "submit, in the order shown.");
        return RedirectToAction(nameof(Exit), new { storeId });
    }

    /// <summary>
    /// Asks the chain how far the built exit has got, and re-renders the page with the answer and the
    /// refreshed transactions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Renders instead of redirecting, and this is the only action in the region that does.</b> The answer
    /// belongs to this page's own table: a verdict that the stored set can no longer finish is only legible
    /// beside the transactions it is about, and the refreshed statuses have to be on screen in the same
    /// response the operator learns them from. A redirect would drop it into a status banner and show a table
    /// that is still one read behind. The service has already persisted the refreshed set, so the re-read
    /// below is showing what is stored, not a second opinion.
    /// </para>
    /// <para>
    /// The service's refusal, if any, still goes through <see cref="RelayExitResult"/> — a check that could not
    /// run is a banner, not a verdict, and the page must not invent one.
    /// </para>
    /// </remarks>
    [HttpPost("exit/check")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> CheckExit(
        [FromRoute] string storeId,
        string recordId,
        CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        var result = await _unilateralExit
            .CheckAsync(storeId, recordId, cancellationToken)
            .ConfigureAwait(false);

        var page = await _unilateralExit.ReadAsync(storeId, cancellationToken).ConfigureAwait(false);
        var settings = await ReadSettingsForExitPageAsync(storeId).ConfigureAwait(false);
        var model = BuildExitViewModel(storeId, page, settings);

        // Read after the write, so the banner and the table describe the same moment. Verdict is not
        // persisted by the service — it is a snapshot of the chain at the moment of this call — so it is
        // carried on the model for this render and gone on the next read of the page. A check that refused
        // carries no verdict, and the banner below says why rather than the page inventing one.
        model.CheckResult = result.Success ? result.Verdict : null;

        if (!result.Success)
            RelayExitResult(result, string.Empty);

        return View(nameof(Exit), model);
    }

    /// <summary>
    /// Exports this store's exit data and shows it once, for the operator to copy somewhere safe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The blob goes into the view model and never into <c>TempData</c>.</b> It is a live SDK call that can
    /// fail, so it cannot be a GET — but a redirect carrying it in a status message would print it into a
    /// banner that survives navigation and redisplay, and the operator would have no way to tell which of the
    /// store's pages was holding it. Rendered from the model, it exists for exactly one response.
    /// </para>
    /// <para>
    /// Lives under the Advanced page because it is wallet infrastructure rather than one exit's business: it
    /// is something a merchant should collect <em>before</em> they need it, and an exit built from data
    /// collected while Spark was still reachable is the only kind that works with the operators gone.
    /// </para>
    /// <para>
    /// <b>The export and its storing are <see cref="ISparkStoreRuntime.ExportExitStateAsync"/>'s</b>, not this
    /// action's: storing the wallet's own export is the same write the automatic pass makes, with the same
    /// rules — it waits for the connect's restore (which may be reading the file this would replace), it keeps
    /// a different wallet's backup aside instead of overwriting it, and it is bounded. This action renders
    /// what came back, and never throws.
    /// </para>
    /// </remarks>
    [HttpPost("advanced/exit-state/export")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> ExportExitState([FromRoute] string storeId, CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        var status = await _statusReader.ReadAsync(storeId, cancellationToken).ConfigureAwait(false);
        if (!status.Configured)
            return await RedirectToSetupOrDeny(storeId).ConfigureAwait(false);

        var model = await BuildAdvancedViewModel(storeId, status, input: null, cancellationToken)
            .ConfigureAwait(false);

        // Rendering rather than redirecting on every outcome, so the operator stays on the section they
        // pressed the button in and the blob (when there is one) is never re-served by a replay of a
        // redirect target.
        ExitStateExportResult export;
        try
        {
            export = await _storeRuntime.ExportExitStateAsync(storeId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The runtime reports its own failures; this is the backstop an action needs, because an
            // exception out of it disables the plugin.
            _logger.LogWarning(
                "Store {StoreId}: exporting its exit state failed unexpectedly ({ExceptionType})",
                storeId, ex.GetType().Name);
            export = new ExitStateExportResult(null, false,
                "The exit data could not be exported. Check the server log.");
        }

        if (export.Error is { } error)
        {
            TempData[WellKnownTempData.ErrorMessage] = error;
            return View("Advanced", model);
        }

        model.ExportedExitState = export.ExitState;
        if (export.Stored)
            model.ExitStateBackupTakenAt = await ReadBackupTakenAtAsync(storeId, cancellationToken).ConfigureAwait(false);
        else if (export.NotStoredReason is { } notStored)
            TempData[WellKnownTempData.ErrorMessage] = notStored;

        return View("Advanced", model);
    }

    /// <summary>
    /// Downloads the stored exit-state backup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The automatic refresh means an operator never has to remember to take a backup; this is how they
    /// still <i>get</i> one off this server, which is the only thing that makes the automatic copy worth
    /// anything. It serves the automatic backup as stored — the wallet's own latest export — rather than
    /// exporting afresh, so what lands on disk is exactly what the plugin is holding.
    /// </para>
    /// <para>
    /// POST rather than GET. This hands out the most sensitive blob the plugin holds, and a GET would put
    /// the act of taking it into the URL, the access log, and anything that follows a link.
    /// </para>
    /// <para>
    /// <b>Not cacheable, and it inherits that.</b> The response is the wallet's whole exit state, and a
    /// stored copy of it would live on browser or proxy machinery this plugin does not control. The
    /// controller-level <c>[ResponseCache(NoStore = true, Location = None)]</c> is what prevents it: MVC
    /// runs that filter before the action, so the header is on the response whatever the action returns — a
    /// file body included. Per-controller rather than per-action is this class's own convention (see the
    /// note above the attribute); the download's share of it is pinned by a test, since it is the one
    /// action on this controller whose body is a secret rather than a page.
    /// </para>
    /// <para>
    /// <b>Streamed, by design.</b> Nothing on this path reads the content — the point is that the file
    /// the plugin holds and the file the operator keeps are one sequence of bytes — so the action never
    /// holds it either: a string read of a multi-megabyte blob re-encoded to a byte array for a content
    /// result is the secret in memory twice to produce a single pass-through copy, and it is the whole
    /// secret at once rather than a buffer at a time. The stream from
    /// <see cref="IExitStateBackupStore.OpenReadAsync"/> goes to the response as it is, and
    /// <see cref="FileStreamResult"/> disposes the handle when the response pipeline has finished with
    /// it.
    /// </para>
    /// </remarks>
    [HttpPost("advanced/exit-state/download")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> DownloadExitStateBackup(
        [FromRoute] string storeId,
        CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        // Never throws. An unhandled exception out of a plugin action is what BTCPay's plugin exception
        // handler answers by disabling the plugin and restarting the server, and this action's failures
        // are ordinary filesystem ones — a file the process cannot open, a disk error. They become a
        // message on the page the operator pressed the button on.
        Stream? backup;
        DateTimeOffset? takenAt;
        try
        {
            backup = await _exitStateBackupStore.OpenReadAsync(storeId, cancellationToken)
                .ConfigureAwait(false);
            takenAt = backup is null
                ? null
                : await _exitStateBackupStore.TakenAtAsync(storeId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The exception is a filesystem one and names a path, never the content.
            _logger.LogWarning(ex, "Store {StoreId}: the stored exit-state backup could not be opened", storeId);
            TempData[WellKnownTempData.ErrorMessage] =
                "The stored exit-state backup could not be read, so nothing was downloaded. Check the server "
                + "log for the reason; the file itself was left as it was.";
            return RedirectToAction(nameof(Advanced), new { storeId });
        }

        if (backup is null)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                "No exit-state backup is stored for this store yet. One is written automatically as the "
                + "wallet's leaves change; Export takes one now.";
            return RedirectToAction(nameof(Advanced), new { storeId });
        }

        // The length and nothing else, as everywhere this blob is handled — and a file's length now,
        // in bytes, which is exactly the size of the artifact being handed over.
        _logger.LogInformation(
            "Store {StoreId}: served the stored exit-state backup ({Length:N0} bytes, taken {TakenAt:u})",
            storeId, backup.Length, takenAt);

        // The timestamp is in the name because the file is the artifact the operator is storing off-box, and
        // a directory of identical names is one they cannot tell apart a year from now.
        var name = takenAt is { } at
            ? $"exit-state-backup-{storeId}-{at:yyyyMMdd-HHmmss}.txt"
            : $"exit-state-backup-{storeId}.txt";

        return new FileStreamResult(backup, "application/octet-stream") { FileDownloadName = name };
    }

    /// <summary>
    /// Downloads one backup that is waiting to be imported.
    /// </summary>
    /// <remarks>
    /// The same rules as <see cref="DownloadExitStateBackup"/> — POST, not cacheable, streamed, never throws —
    /// for the backups the automatic one is not: a paste that has not imported yet, or a stored backup whose
    /// import failed. While one waits, the operator's own copy may be the only other one, so it can always be
    /// taken off the server. The id comes from the form and is refused by the store unless it is one the
    /// store generated, so it cannot name any other file.
    /// </remarks>
    [HttpPost("advanced/exit-state/pending/download")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> DownloadPendingExitStateBackup(
        [FromRoute] string storeId,
        [FromForm] string? pendingId,
        CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        Stream? backup;
        try
        {
            backup = string.IsNullOrEmpty(pendingId)
                ? null
                : await _exitStateBackupStore.OpenReadPendingAsync(storeId, pendingId, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // Not an id the store ever generated: nothing to serve, and nothing worth a log line.
            backup = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Store {StoreId}: a queued exit-state backup could not be opened", storeId);
            TempData[WellKnownTempData.ErrorMessage] =
                "That backup could not be read, so nothing was downloaded. Check the server log for the reason.";
            return RedirectToAction(nameof(Advanced), new { storeId });
        }

        if (backup is null)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                "That backup is no longer waiting to be imported — it has been imported or cleared since the page "
                + "was loaded.";
            return RedirectToAction(nameof(Advanced), new { storeId });
        }

        _logger.LogInformation(
            "Store {StoreId}: served a queued exit-state backup ({Length:N0} bytes)", storeId, backup.Length);

        return new FileStreamResult(backup, "application/octet-stream")
        {
            FileDownloadName = $"exit-state-backup-{storeId}-pending-{pendingId}.txt"
        };
    }

    /// <summary>
    /// Queues a pasted exit-state blob for import and imports it into the running wallet straight away — or,
    /// with the field empty, clears every stored backup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Imported now, not at the next restart.</b> The deferral was justified as avoiding a race with the
    /// running SDK, but the connect already imports into a running SDK — its import is built to merge into a
    /// live wallet — and a backup left for a restart was one the next automatic pass could replace, with the
    /// operator told it was safe. The paste goes to a queue no automatic pass writes, the import runs at once,
    /// and the banner says what it did, in counts. When the wallet is not running, or the import fails, the
    /// blob stays queued and every connect retries it.
    /// </para>
    /// <para>
    /// Nothing here inspects the string — whether it is a well-formed blob at all is the SDK's judgement at
    /// import time, and a plugin-side shape check would only be a second, weaker parser in front of the real
    /// one.
    /// </para>
    /// </remarks>
    [HttpPost("advanced/exit-state")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    [RequestFormLimits(
        ValueLengthLimit = ExitStateFormLimitBytes,
        MultipartBodyLengthLimit = ExitStateFormLimitBytes,
        Order = BeforeAntiforgery)]
    [RequestSizeLimit(ExitStateFormLimitBytes, Order = BeforeAntiforgery)]
    public async Task<IActionResult> SetExitStateBackup(
        [FromRoute] string storeId,
        SparkAdvancedViewModel vm,
        CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        var (pasted, refusal) = await ReadSubmittedBackupAsync(storeId, vm, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            TempData[WellKnownTempData.ErrorMessage] = refusal;
            return RedirectToAction(nameof(Advanced), new { storeId });
        }

        UnilateralExitOpResult result;
        try
        {
            result = await _unilateralExit
                .SetExitStateBackupAsync(storeId, pasted, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The service reports its own failures as results; this is the backstop for anything it did
            // not, because an exception out of this action disables the plugin. The type only: whatever
            // threw was handed the pasted blob.
            _logger.LogError(
                "Store {StoreId}: saving an exit-state backup failed unexpectedly ({ExceptionType})",
                storeId, ex.GetType().Name);
            result = new UnilateralExitOpResult(false,
                "The exit-state backup could not be saved. Check the server log for the reason.", null);
        }

        if (!result.Success || string.IsNullOrWhiteSpace(pasted))
        {
            RelayExitResult(
                result,
                "No exit-state backup is stored or waiting for this store now. The plugin takes a fresh "
                + "automatic backup on its next pass.");
            return RedirectToAction(nameof(Advanced), new { storeId });
        }

        ExitStateImportReport report;
        try
        {
            report = await _storeRuntime.ImportPendingExitStateAsync(storeId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Store {StoreId}: importing a pasted exit-state backup failed unexpectedly ({ExceptionType})",
                storeId, ex.GetType().Name);
            report = new ExitStateImportReport(ExitStateImportOutcome.Failed, Failed: 1);
        }

        RelayImportReport(report);
        return RedirectToAction(nameof(Advanced), new { storeId });
    }

    /// <summary>
    /// The largest exit-state form this controller accepts, in bytes, whichever way it is posted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the action states its own form limits at all.</b> ASP.NET's defaults cap a single form value at
    /// 4 MiB and a request body at about 30 MB, and the SDK documents a real wallet's export as reaching several
    /// megabytes. A paste past 4 MiB therefore failed as a bare 400 long before the service's own
    /// sixteen-million-character cap could say anything — and it failed inside the antiforgery check, which
    /// reads the whole form before the action runs. A url-encoded post makes it worse: the export is JSON, and
    /// every brace, quote and comma is sent as three characters.
    /// </para>
    /// <para>
    /// Sixty-four MiB covers the service's cap even url-encoded, with room. The page posts multipart (so a
    /// paste is sent as it is) and offers a file input as well. The service's character cap stays the real
    /// bound; this only makes sure the request reaches it. Authorisation still runs first — an anonymous
    /// request is refused before any of its body is read.
    /// </para>
    /// </remarks>
    internal const int ExitStateFormLimitBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Filter order for the form limits: ahead of every antiforgery filter, because antiforgery is what reads
    /// the form first — BTCPay's global UI filter (order 0) and <see cref="AutoValidateAntiforgeryTokenAttribute"/>
    /// on this controller (order 1000).
    /// </summary>
    /// <remarks>
    /// Belt and braces: on this framework both attributes are also endpoint metadata that the form read
    /// honours whichever filter triggers it, so the limits hold at the attributes' default order too. The
    /// explicit order is what keeps them holding if that ever stops being true, and it costs nothing — the
    /// filters only configure the request, and authorisation has run before any of them.
    /// </remarks>
    private const int BeforeAntiforgery = -10_000;

    /// <summary>
    /// The backup a save carries: the pasted text, or the chosen file's content — never both, never a file
    /// that could be mistaken for a clear.
    /// </summary>
    /// <returns>The value to hand the service (null or blank means "clear"), or a refusal for the page.</returns>
    /// <remarks>
    /// The file is read as text exactly as a download wrote it — UTF-8, a byte-order mark dropped, nothing
    /// trimmed — so a backup downloaded here and uploaded again is byte-for-byte the string the wallet
    /// exported. A chosen file that turns out empty is a refusal, not a clear: clearing is what the empty
    /// textarea means, and an operator who picked a file did not ask for everything to be deleted.
    /// </remarks>
    private async Task<(string? Backup, string? Refusal)> ReadSubmittedBackupAsync(
        string storeId, SparkAdvancedViewModel vm, CancellationToken cancellationToken)
    {
        if (vm.ExitStateBackupFile is not { Length: > 0 } file)
        {
            return vm.ExitStateBackupFile is { Length: 0 }
                ? (null, "That file is empty, so nothing was stored.")
                : (vm.ExitStateBackup, null);
        }

        if (!string.IsNullOrWhiteSpace(vm.ExitStateBackup))
            return (null, "Paste the backup or choose its file, not both. Nothing was stored.");

        // Four bytes per character is the most UTF-8 can spend, so a file past this cannot be under the
        // service's character cap; refused before any of it is read into memory.
        if (file.Length > (long)SparkUnilateralExitService.MaxExitStateBackupChars * 4)
        {
            return (null, "That file is far larger than an exit-state backup can be, so it is not one. "
                          + "Nothing was stored.");
        }

        try
        {
            await using var stream = file.OpenReadStream();
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(content)
                ? (null, "That file is empty, so nothing was stored.")
                : (content, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Store {StoreId}: an uploaded exit-state backup could not be read ({ExceptionType})",
                storeId, ex.GetType().Name);
            return (null, "That file could not be read, so nothing was stored.");
        }
    }

    /// <summary>
    /// Turns an import of the queue into the banner the operator reads after a paste.
    /// </summary>
    /// <remarks>
    /// Counts only, never content. "Imported" is not reported as "your backup is in place" when it restored
    /// nothing: that is the answer that looks like success while the wallet is exactly as un-exitable as it
    /// was, and the one an operator most needs to hear plainly.
    /// </remarks>
    private void RelayImportReport(ExitStateImportReport report)
    {
        const string Kept =
            " It is kept, is never replaced by the automatic backup, and is imported again every time this "
            + "store's wallet starts until it succeeds.";

        switch (report.Outcome)
        {
            case ExitStateImportOutcome.Imported when report.RestoredLeaves > 0:
                TempData[WellKnownTempData.SuccessMessage] = string.Format(CultureInfo.InvariantCulture,
                    "Exit-state backup imported into the running wallet: {0:N0} leaves restored ({1:N0} already "
                    + "held, {2:N0} belonging to another wallet, {3:N0} conflicting).",
                    report.RestoredLeaves, report.SkippedChains, report.ForeignLeaves, report.ConflictingLeaves);
                break;

            case ExitStateImportOutcome.Imported when report.ForeignLeaves > 0 && report.ConflictingLeaves == 0:
                TempData[WellKnownTempData.ErrorMessage] = string.Format(CultureInfo.InvariantCulture,
                    "The backup was imported, but none of its {0:N0} leaves belong to this store's wallet, so "
                    + "nothing was restored. It may be another store's or another seed's; it was kept aside in "
                    + "the plugin's exit-state directory rather than deleted.",
                    report.ForeignLeaves);
                break;

            case ExitStateImportOutcome.Imported when report.ConflictingLeaves > 0:
                TempData[WellKnownTempData.ErrorMessage] = string.Format(CultureInfo.InvariantCulture,
                    "The backup was imported, but {0:N0} of its leaves disagree with exit data this wallet already "
                    + "holds, so nothing was restored from them.",
                    report.ConflictingLeaves);
                break;

            case ExitStateImportOutcome.Imported:
                TempData[WellKnownTempData.SuccessMessage] = string.Format(CultureInfo.InvariantCulture,
                    "Exit-state backup imported. This wallet already held everything in it ({0:N0} leaves), so "
                    + "nothing needed restoring.",
                    report.SkippedChains);
                break;

            case ExitStateImportOutcome.WalletNotRunning:
                TempData[WellKnownTempData.SuccessMessage] =
                    "Exit-state backup stored. This store's wallet is not running, so it will be imported when "
                    + "the wallet next starts." + Kept;
                break;

            case ExitStateImportOutcome.Busy:
                TempData[WellKnownTempData.SuccessMessage] =
                    "Exit-state backup stored. Another import into this wallet was still running, so this one "
                    + "waits for the next attempt." + Kept;
                break;

            case ExitStateImportOutcome.NothingPending:
                // Queued and then gone before the import looked: a concurrent clear, or an import already in
                // flight that took it.
                TempData[WellKnownTempData.SuccessMessage] =
                    "Exit-state backup stored; it had already been taken by another import by the time this one "
                    + "looked.";
                break;

            default:
                TempData[WellKnownTempData.ErrorMessage] =
                    "The exit-state backup was stored but could not be imported now"
                    + (report.Reason is { } reason ? ": " + reason.TrimEnd('.') + "." : ".")
                    + Kept + " Check the server log for details.";
                break;
        }
    }

    /// <summary>
    /// Abandons the record so the store can quote again.
    /// </summary>
    /// <remarks>
    /// Moves no money and cancels nothing on-chain: anything already broadcast stays valid and will still
    /// confirm. The page carries that sentence next to the button, because "abandon" is the word a merchant
    /// reaches for when they want to undo a broadcast, and this is not that.
    /// </remarks>
    [HttpPost("exit/abandon")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> AbandonExit(
        [FromRoute] string storeId,
        string recordId,
        CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        var result = await _unilateralExit
            .AbandonAsync(storeId, recordId, cancellationToken)
            .ConfigureAwait(false);

        RelayExitResult(
            result,
            "This exit was abandoned. Anything already broadcast is unaffected and will still confirm.");
        return RedirectToAction(nameof(Exit), new { storeId });
    }

    /// <summary>
    /// Completes the exit once Spark's check confirms it finished, or on the operator's own word when they say
    /// a check cannot answer.
    /// </summary>
    /// <remarks>
    /// It moves no money either way. It exists because without it the only way a finished exit leaves the
    /// active state is "abandon", and telling a merchant to abandon the exit that just succeeded is how a page
    /// teaches somebody to distrust it. Whether a check is needed, and what counts as finished, is the
    /// service's judgement.
    /// </remarks>
    /// <param name="confirmedWithoutVerdict">The page's "I confirmed the sweep myself" box.</param>
    [HttpPost("exit/complete")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> CompleteExit(
        [FromRoute] string storeId,
        string recordId,
        bool confirmedWithoutVerdict,
        CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        var result = await _unilateralExit
            .MarkCompletedAsync(storeId, recordId, confirmedWithoutVerdict, cancellationToken)
            .ConfigureAwait(false);

        RelayExitResult(
            result,
            result.Verdict is SparkExitVerdict.Done
                ? "Recorded as completed: Spark's check confirmed every transaction, the sweep included, is in a "
                  + "block. Nothing was broadcast or moved by this, and this store can quote another exit."
                : "Recorded as completed on your confirmation that the sweep is in a block. Nothing was broadcast "
                  + "or moved by this, and this store can quote another exit.");
        return RedirectToAction(nameof(Exit), new { storeId });
    }

    /// <summary>
    /// Points funding discovery at a different esplora instance, or clears the override.
    /// </summary>
    /// <remarks>
    /// The one piece of real configuration this feature has, and it is settable from the page that reports it
    /// missing: off mainnet there is no sensible default, so an operator who lands on "the explorer could not
    /// be reached" would otherwise have to go looking for a settings screen that does not exist. A blank value
    /// clears the override; whether the string is an acceptable URL is the service's judgement, not this
    /// action's.
    /// <para>
    /// <b>Server administrators only, on top of the store permission.</b> The URL is not a store preference so
    /// much as an instruction to this server to make HTTP requests to a host of the setter's choosing — on every
    /// view of the exit page, by anyone who can view the store — and to relay what came back into the page. For
    /// a store administrator on a shared server that is a blind request forger against the server's own network:
    /// its LAN, its loopback services, whatever a cloud host exposes. The server's administrator is the one
    /// person for whom pointing it at an internal esplora is a legitimate choice, so the setting is theirs.
    /// </para>
    /// </remarks>
    [HttpPost("exit/explorer")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyServerSettings)]
    public async Task<IActionResult> SetExitExplorer(
        [FromRoute] string storeId,
        string? esploraApiUrl,
        CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        var result = await _unilateralExit
            .SetExplorerUrlAsync(storeId, esploraApiUrl, cancellationToken)
            .ConfigureAwait(false);

        RelayExitResult(
            result,
            string.IsNullOrWhiteSpace(esploraApiUrl)
                ? "Explorer override cleared. Funding discovery falls back to the default for this network, "
                  + "which off mainnet means no discovery at all."
                : "Explorer saved. Funding discovery will use it from the next read of this page.");
        return RedirectToAction(nameof(Exit), new { storeId });
    }

    /// <summary>
    /// Puts the service's own outcome in the status banner: its refusal verbatim, or this action's success copy.
    /// </summary>
    /// <remarks>
    /// The result type carries an error but no success message, deliberately — a refusal is the service's
    /// sentence to write, while "what just worked" is a fact about which button was pressed and belongs to the
    /// caller. The fallback exists only so a service that fails without saying why still produces a banner
    /// rather than a silent redirect that looks like success.
    /// </remarks>
    private void RelayExitResult(UnilateralExitOpResult result, string success)
    {
        if (result.Success)
        {
            TempData[WellKnownTempData.SuccessMessage] = success;
            return;
        }

        TempData[WellKnownTempData.ErrorMessage] =
            result.Error ?? "The unilateral exit could not be updated. Check the server logs for the reason.";
    }

    /// <summary>
    /// Projects one service read onto the page. Copies fields; reads nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No deserialisation happens here any more, deliberately.</b> The record's JSON columns are written by
    /// <see cref="ISparkUnilateralExitService"/> and now read back by it too, which is why
    /// <see cref="UnilateralExitPageData"/> arrives typed. A second reader in this class meant two sets of
    /// serialiser options for one format, and the failure mode of them drifting apart was not an exception —
    /// it was an empty transaction table for an exit worth a store's whole balance.
    /// </para>
    /// <para>
    /// The two form fields are pre-filled from the active record so the page shows what was quoted rather than
    /// an empty form beside a live exit. The explorer URL comes off the store's settings instead of the page
    /// data: it is the input's current value, and posting the explorer form with a blank box is how the
    /// override is cleared — so a box that rendered empty while an override was set would clear it by
    /// accident.
    /// </para>
    /// </remarks>
    private SparkExitViewModel BuildExitViewModel(
        string storeId, UnilateralExitPageData page, SparkSettings? settings)
    {
        var model = new SparkExitViewModel
        {
            StoreId = storeId,
            WalletRunning = page.WalletRunning,
            DisclosureAcknowledged = page.DisclosureAcknowledged,
            BalanceSats = page.BalanceSats,
            ActiveRecord = page.ActiveRecord,
            History = page.History,
            FundingReceivedSat = page.FundingReceivedSat,
            FundingLargestOutputSat = page.FundingLargestOutputSat,
            LeafCount = page.LeafCount,
            FundingKeyPath = page.FundingKeyPath,
            Transactions = page.Transactions ?? [],
            TransactionsUnreadable = page.TransactionsUnreadable,
            // Carried as the service reported it, without an empty-list normalisation: the page gates the
            // whole "send these now" section on it being non-null and non-empty, so turning a service that
            // said "nothing is ready" into an empty list here would change nothing — but turning a service
            // that said "I could not tell" into one would put an action block on screen for an unknown set.
            PendingBroadcast = page.PendingBroadcast,
            // Both links coalesced: a stored blob with an explicit "UnilateralExit": null deserialises to a null
            // section whatever the property initialiser says, and a NullReferenceException on this GET disables
            // the plugin and restarts BTCPay.
            EsploraApiUrl = settings?.UnilateralExit?.EsploraApiUrl,
            LoadError = page.LoadError,
            StatusesReadUtc = page.StatusesReadUtc,
            NetworkName = _sweepSettings.Network.ChainName.ToString(),
            IsMainnet = _sweepSettings.Network.ChainName == ChainName.Mainnet
        };

        if (page.ActiveRecord is not { } record)
        {
            // Nothing in flight, so the quote form is what renders and its rate field is otherwise empty. The
            // recommendation when the explorer gave one, and the plugin's own floor when it did not — off mainnet
            // with no override, or an explorer that could not be read. Either way the operator gets a rate to
            // quote at rather than a field they have to fill in blind, and either way they can type another one.
            model.FeeRateSatPerVbyte = page.RecommendedFeeRateSatPerVbyte
                                       ?? SparkUnilateralExitService.DefaultFeeRateSatPerVbyte;
            model.FeeRateSuggested = page.RecommendedFeeRateSatPerVbyte is not null;
            return model;
        }

        // A record wins, and it is not a preference: this field is pre-filled with the rate the exit in front of
        // the operator was actually quoted and funded at. A market rate that has moved since would offer them a
        // number the record's own quote — and any transaction built from it — does not honour.
        model.FeeRateSatPerVbyte = record.FeeRateSatPerVbyte;
        model.DestinationAddress = record.DestinationAddress;

        return model;
    }

    #endregion

    #region Stable Balance

    /// <summary>
    /// Holding the store's balance in a stablecoin between sweeps.
    /// </summary>
    [HttpGet("stable-balance")]
    public async Task<IActionResult> StableBalance([FromRoute] string storeId, CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        var view = await _stableBalance.ReadAsync(storeId, cancellationToken).ConfigureAwait(false);
        if (!view.Configured)
            return await RedirectToSetupOrDeny(storeId).ConfigureAwait(false);

        return View(new SparkStableBalanceViewModel
        {
            StoreId = storeId,
            View = view,
            Settings = StableBalanceInput.From(view.Settings)
        });
    }

    /// <summary>
    /// Saves the Stable Balance configuration and applies it to the wallet.
    /// </summary>
    /// <remarks>
    /// <b>This converts the store's balance.</b> Enabling queues Bitcoin → stablecoin and disabling queues the
    /// reverse, both on Spark's own background worker and both taking a spread. The service is what decides
    /// whether that is allowed — including the disclosure gate, which the API enforces identically.
    /// </remarks>
    [HttpPost("stable-balance")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> StableBalance(
        [FromRoute] string storeId,
        SparkStableBalanceViewModel vm,
        CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        var input = vm.Settings ?? new StableBalanceInput();
        var result = await _stableBalance.SaveAsync(storeId, input, cancellationToken).ConfigureAwait(false);

        if (result.Status is SparkStableBalanceStatus.NotConfigured)
            return await RedirectToSetupOrDeny(storeId).ConfigureAwait(false);

        if (result.Status is SparkStableBalanceStatus.Invalid)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError($"{nameof(vm.Settings)}.{error.Field}", error.Error);

            var view = await _stableBalance.ReadAsync(storeId, cancellationToken).ConfigureAwait(false);
            return View(new SparkStableBalanceViewModel { StoreId = storeId, View = view, Settings = input });
        }

        TempData[result.Succeeded ? WellKnownTempData.SuccessMessage : WellKnownTempData.ErrorMessage] =
            result.Message;

        return RedirectToAction(nameof(StableBalance), new { storeId });
    }

    /// <summary>
    /// Re-applies the stored activation state to a wallet that has drifted from it.
    /// </summary>
    /// <remarks>
    /// The repair for a replaced seed or a fresh storage directory, where the SDK's cached active label starts
    /// empty however the store is configured. Explicit rather than automatic, because applying it converts.
    /// </remarks>
    [HttpPost("stable-balance/reapply")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> ReapplyStableBalance(
        [FromRoute] string storeId,
        CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        var result = await _stableBalance.ReapplyAsync(storeId, cancellationToken).ConfigureAwait(false);
        TempData[result.Succeeded ? WellKnownTempData.SuccessMessage : WellKnownTempData.ErrorMessage] =
            result.Message;

        return RedirectToAction(nameof(StableBalance), new { storeId });
    }

    #endregion

    #region Send Payment

    [HttpGet("send")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> Send([FromRoute] string storeId, CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        if (await _settingsStore.GetAsync(storeId).ConfigureAwait(false) is null)
            return await RedirectToSetupOrDeny(storeId).ConfigureAwait(false);

        return View(new SparkSendViewModel { StoreId = storeId });
    }

    [HttpPost("send")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> Send(
        [FromRoute] string storeId,
        SparkSendViewModel vm,
        CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;
        vm.StoreId = storeId;

        if (await _settingsStore.GetAsync(storeId).ConfigureAwait(false) is null)
            return await RedirectToSetupOrDeny(storeId).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(vm.Destination))
        {
            ModelState.AddModelError(nameof(vm.Destination), "Destination is required.");
            return View(vm);
        }

        var outcome = await _sendPayment.SendAsync(
            storeId, vm.Destination.Trim(), vm.AmountSats, vm.MaxFeeSats, cancellationToken)
            .ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            ModelState.AddModelError(string.Empty, outcome.ErrorMessage ?? outcome.ErrorCode ?? "Payment failed.");
            return View(vm);
        }

        var p = outcome.Payment!;
        vm.Result = new SparkSendResult
        {
            PaymentId  = p.PaymentId,
            PaymentHash = p.PaymentHash,
            AmountSats = p.AmountSats,
            FeeSats    = p.FeeSats,
            PaidAt     = p.PaidAt
        };
        vm.Destination = null;
        vm.AmountSats  = null;
        vm.MaxFeeSats  = null;
        return View(vm);
    }

    [HttpGet("receive")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> Receive([FromRoute] string storeId, CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        if (await _settingsStore.GetAsync(storeId).ConfigureAwait(false) is null)
            return await RedirectToSetupOrDeny(storeId).ConfigureAwait(false);

        return View(new SparkReceiveViewModel { StoreId = storeId });
    }

    [HttpPost("receive")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> Receive(
        [FromRoute] string storeId,
        SparkReceiveViewModel vm,
        CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;
        vm.StoreId = storeId;

        if (await _settingsStore.GetAsync(storeId).ConfigureAwait(false) is null)
            return await RedirectToSetupOrDeny(storeId).ConfigureAwait(false);

        var result = await _sendPayment.ReceiveBolt11Async(
            storeId, vm.Description, vm.AmountSats, cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
        {
            ModelState.AddModelError(string.Empty, "The wallet is not running. Start it and try again.");
            return View(vm);
        }

        vm.Result = result;
        return View(vm);
    }

    [HttpGet("history")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanViewStoreSettings)]
    public async Task<IActionResult> WalletHistory(
        [FromRoute] string storeId,
        [FromQuery] string? direction,
        [FromQuery] int period = 0,
        [FromQuery] int skip = 0,
        CancellationToken cancellationToken = default)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        if (await _settingsStore.GetAsync(storeId).ConfigureAwait(false) is null)
            return await RedirectToSetupOrDeny(storeId).ConfigureAwait(false);

        const int pageSize = 25;
        skip = Math.Max(0, skip);

        SparkPaymentDirection? dir = direction switch
        {
            "send"    => SparkPaymentDirection.Send,
            "receive" => SparkPaymentDirection.Receive,
            _         => null
        };

        DateTimeOffset? from = period > 0
            ? DateTimeOffset.UtcNow.AddDays(-period)
            : null;

        var raw = await _sendPayment.ListAllAsync(
            storeId, dir, from, skip, pageSize + 1, cancellationToken).ConfigureAwait(false);

        var hasMore = raw.Count > pageSize;
        var payments = hasMore ? raw.Take(pageSize).ToList() : raw;

        return View(new SparkWalletHistoryViewModel
        {
            StoreId   = storeId,
            Payments  = payments,
            Direction = dir,
            Period    = period,
            Skip      = skip,
            Count     = pageSize,
            HasMore   = hasMore
        });
    }

    #endregion

    #region Removal

    /// <summary>
    /// Confirmation page for removing the store's Spark wallet.
    /// </summary>
    [HttpGet("remove")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> Remove([FromRoute] string storeId, CancellationToken cancellationToken)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        storeId = store.Id;

        var status = await _statusReader.ReadAsync(storeId, cancellationToken).ConfigureAwait(false);
        if (!status.Configured)
            return RedirectToAction(nameof(Setup), new { storeId });

        return View(ToViewModel(storeId, status));
    }

    /// <summary>
    /// Removes the store's Spark configuration: keys gone from the server, storage directory retained.
    /// </summary>
    [HttpPost("remove")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> RemoveConfirmed([FromRoute] string storeId)
    {
        if (!ResolveStore(storeId, out var store))
            return NotFound();

        await _provisioner.RemoveAsync(store.Id).ConfigureAwait(false);
        TempData[WellKnownTempData.SuccessMessage] =
            "This store's Spark wallet was removed. Its recovery phrase is now the only way to reach any "
            + "remaining funds.";
        return RedirectToAction(nameof(Setup), new { storeId = store.Id });
    }

    #endregion

    /// <summary>
    /// The store BTCPay authorised this request against, or false when the request must not proceed.
    /// </summary>
    /// <param name="routeStoreId">
    /// The <see cref="FromRouteAttribute"/>-bound id. Compared, never used as the working value.
    /// </param>
    /// <remarks>
    /// <para>
    /// Two independent guards, because the cost of being wrong here is one store's Lightning payments being
    /// received into another store's wallet.
    /// </para>
    /// <para>
    /// The first is the item set by BTCPay's authorisation filter: it is the store the caller was actually
    /// authorised for, and it cannot be influenced by the request body. The second is the equality check
    /// against the route value — redundant today, since <see cref="FromRouteAttribute"/> already pins the
    /// binding source and authorisation resolves from the same route data, but it is what fails closed if a
    /// future edit changes either of those. A mismatch is reported as "not found" rather than "forbidden" so
    /// it says nothing about whether the other store exists.
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
                "A Spark request authorised for store {AuthorisedStoreId} carried store id {SuppliedStoreId}; "
                + "refusing it", store.Id, routeStoreId);
            store = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Sends the caller to setup, or refuses if they may only view.
    /// </summary>
    /// <remarks>
    /// The setup page requires <c>CanModifyStoreSettings</c>, so redirecting a view-only user there turns
    /// "this store has not set Spark up" into an access-denied page that reads like a permissions bug. They get
    /// a plain forbid instead.
    /// </remarks>
    private async Task<IActionResult> RedirectToSetupOrDeny(string storeId)
    {
        var canModify = await _authorizationService
            .AuthorizeAsync(User, storeId, Policies.CanModifyStoreSettings)
            .ConfigureAwait(false);

        return canModify.Succeeded
            ? RedirectToAction(nameof(Setup), new { storeId })
            : Forbid();
    }

    /// <summary>
    /// Fills in everything the setup page needs beyond what the merchant posted.
    /// </summary>
    /// <remarks>
    /// Note what this does <em>not</em> do: it does not scrub the submitted recovery phrase. Clearing
    /// <see cref="SparkSetupViewModel.ImportedMnemonic"/> would not achieve that anyway — the textarea is
    /// rendered from <c>ModelState</c>'s attempted value, not from the model — and re-rendering a rejected
    /// phrase back to the person who just typed it is the right behaviour: they need to see the typo. The
    /// phrase reaches nobody else's browser and is never read back out of storage.
    /// </remarks>
    private async Task<SparkSetupViewModel> BuildSetupViewModel(string storeId, SparkSetupViewModel vm)
    {
        vm.StoreId = storeId;
        vm.AlreadyConfigured = await _settingsStore.GetAsync(storeId).ConfigureAwait(false) is not null;
        vm.CanUseHotWallet = await _seedResolver.CanUseHotWalletAsync(User).ConfigureAwait(false);

        // Status and reason only. The result also carries the phrase itself and this page must never see it —
        // rendering it is exactly the defect the existing Spark plugin shipped.
        var seed = await _seedResolver.ReadHotWalletSeedAsync(User, storeId).ConfigureAwait(false);
        vm.HotWalletStatus = seed.Status;
        vm.HotWalletUnavailableReason = seed.Reason;

        return vm;
    }
}
