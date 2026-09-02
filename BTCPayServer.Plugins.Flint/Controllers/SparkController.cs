using System;
using System.Diagnostics.CodeAnalysis;
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
using BTCPayServer.Plugins.Flint.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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
    private readonly CrossChainCatalog _crossChainCatalog;
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
        CrossChainCatalog crossChainCatalog,
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
        _crossChainCatalog = crossChainCatalog;
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
            HasApiKeyOverride = !string.IsNullOrEmpty(settings?.ApiKeyOverride)
        };
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
