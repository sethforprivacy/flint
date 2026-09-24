using System.ComponentModel.DataAnnotations;
using BTCPayServer.Plugins.Flint.Services;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace BTCPayServer.Plugins.Flint.Models;

/// <summary>
/// Page 1 of setup: where this store's Spark seed comes from.
/// </summary>
/// <remarks>
/// <para>
/// Only two properties are inbound — <see cref="SeedSource"/> and <see cref="ImportedMnemonic"/>. Everything
/// else is <see cref="BindNeverAttribute"/>, because it is a server-side fact about the store or the caller's
/// permissions, and a posted value that quietly replaced one of those would be a form deciding what it is
/// allowed to do.
/// </para>
/// <para>
/// There is deliberately no property here that could hold the <em>stored</em> mnemonic: nothing reads the seed
/// back out of settings, in this wave or any other. A rejected <see cref="ImportedMnemonic"/> does reappear in
/// the re-rendered form — that is <c>ModelState</c>'s attempted value, and it is the right behaviour, since the
/// person who has to find the typo is the person who typed it.
/// </para>
/// <para>
/// Wave 4 adds page 2 (sweep settings). It hangs off the <c>spark-setup-post-body</c> extension point in
/// <c>Setup.cshtml</c> and the status page's <c>spark-status-post-body</c>, so it needs no change to this
/// model.
/// </para>
/// </remarks>
public class SparkSetupViewModel
{
    /// <summary>
    /// The store being configured, always set by the controller from the store BTCPay authorised.
    /// </summary>
    /// <remarks>
    /// Never bound. The view builds its form action from this, so a value that could arrive from the request
    /// body would be a form that decides which store it posts to.
    /// </remarks>
    [BindNever]
    public string StoreId { get; set; } = string.Empty;

    /// <summary>Which of the three seed sources the merchant picked. Generate is the default.</summary>
    public SeedSource SeedSource { get; set; } = SeedSource.Generated;

    /// <summary>
    /// The recovery phrase typed into the import box. Inbound only — never populated from storage.
    /// </summary>
    [Display(Name = "Recovery phrase")]
    public string? ImportedMnemonic { get; set; }

    /// <summary>
    /// Turn auto-sweeping on as part of setup, rather than sending the merchant to a second page for it.
    /// </summary>
    /// <remarks>
    /// Step 2 of setup used to be a paragraph explaining sweeping and a promise you could configure it later,
    /// which meant the safest configuration — not leaving a growing balance on a second layer — was the one that
    /// needed an extra trip. Only the two settings that decide whether sweeping happens at all are here; the fee
    /// limits, destination and confirmation speed keep their defaults and live on the sweep page.
    /// </remarks>
    [Display(Name = "Sweep automatically to this store's on-chain wallet")]
    public bool EnableSweeping { get; set; }

    [Display(Name = "Sweep when the balance passes")]
    public long SweepBalanceThresholdSats { get; set; } = SweepSettings.DefaultBalanceThresholdSats;

    /// <summary>
    /// Offer USDC and USDT at checkout as well, as part of setup. The one question the feature asks; the status
    /// page carries the same switch afterwards. Ignored off mainnet, where the page does not render it.
    /// </summary>
    [Display(Name = "Also accept USDC and USDT, received as bitcoin")]
    public bool EnableStablecoins { get; set; }

    /// <summary>
    /// True when this store already has a Spark wallet, so the page can frame itself as replacing a seed
    /// rather than as first-time setup.
    /// </summary>
    /// <remarks>
    /// <c>[BindNever]</c> attaches to the declaration that follows it, comments notwithstanding — this
    /// attribute once drifted onto <see cref="EnableSweeping"/> when this property was moved without it,
    /// which both let a poster set this flag and silently discarded the setup page's sweeping opt-in.
    /// <c>SparkSetupViewModelBindingTests</c> pins the placement.
    /// </remarks>
    [BindNever]
    public bool AlreadyConfigured { get; set; }

    /// <summary>
    /// Whether the merchant may create or import a hot wallet at all:
    /// <c>AllowHotWalletForAll || user is a server admin</c>, the same gate BTCPay puts on its own on-chain
    /// hot wallets. False disables every option on the page.
    /// </summary>
    /// <remarks>
    /// Rendering only. The controller re-checks the policy server-side before it provisions anything, so this
    /// value never decides whether the request is allowed.
    /// </remarks>
    [BindNever]
    public bool CanUseHotWallet { get; set; }

    /// <summary>Whether the store's on-chain hot-wallet seed can be reused, and if not, why not.</summary>
    [BindNever]
    public HotWalletSeedStatus HotWalletStatus { get; set; } = HotWalletSeedStatus.Unavailable;

    /// <summary>Merchant-facing explanation shown under a disabled "reuse" option.</summary>
    [BindNever]
    public string? HotWalletUnavailableReason { get; set; }

    public bool HotWalletAvailable => HotWalletStatus is HotWalletSeedStatus.Available;
}
