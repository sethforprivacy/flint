using System;
using System.ComponentModel.DataAnnotations;
using BTCPayServer.Plugins.Flint.Services;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace BTCPayServer.Plugins.Flint.Models;

/// <summary>
/// The Advanced page: wallet details, recovery-phrase provenance, the sweep tuning most stores never touch,
/// and removal.
/// </summary>
/// <remarks>
/// Everything here used to live on the status page — a merchant opening the plugin to check their balance had
/// to read past all of it. Holds no seed material, by construction, same as
/// <see cref="SparkStatusViewModel"/>.
/// </remarks>
public class SparkAdvancedViewModel
{
    [BindNever]
    public string StoreId { get; set; } = string.Empty;

    /// <summary>Where the seed came from, for the security-posture note.</summary>
    public SeedSource SeedSource { get; set; }

    public bool WalletRunning { get; set; }

    /// <summary>The wallet's Spark identity public key. Null when not running or not synced yet.</summary>
    public string? IdentityPubkey { get; set; }

    /// <summary>SDK storage path. <b>Null for anyone but a server admin</b> — a fact about the host, not the store.</summary>
    public string? StorageDirectory { get; set; }

    /// <summary>
    /// The sweep configuration, of which only the reserve and the fee policy are editable here. The rest is
    /// carried so the save can go through the same whole-object validation the sweep page uses.
    /// </summary>
    [ValidateNever]
    public SweepSettingsInput Settings { get; set; } = new();

    /// <summary>
    /// A replacement Breez API key, inbound only — <b>the stored key is never written into this model</b>.
    /// Not a secret in Breez's model, but nobody else should be using this store's key either, so the page
    /// has no reason to hand it out: the form shows only whether an override is set, an empty field to
    /// replace it, and a button to return to the built-in key.
    /// </summary>
    [Display(Name = "New Breez API key")]
    public string? ApiKeyOverride { get; set; }

    /// <summary>Whether this store has its own key set. Display only; the key itself stays server-side.</summary>
    [BindNever]
    public bool HasApiKeyOverride { get; set; }

    /// <summary>
    /// True when the merchant pressed the "use built-in key" button, which is the only way to clear the
    /// override: with the stored key never displayed, an empty field cannot be allowed to mean "clear" — it
    /// is what the form looks like when nothing was touched.
    /// </summary>
    public bool UseBuiltInKey { get; set; }

    /// <summary>
    /// When the stored exit-state backup was last written, or null when none is stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A timestamp rather than a flag, because the interesting question stopped being "is there one" and
    /// became "how stale is it". The plugin refreshes this backup on its own after the wallet's leaves
    /// change, so an operator reading this page is checking that the automation is working, not deciding
    /// whether to do it by hand.
    /// </para>
    /// <para>
    /// <b>The stored blob is never rendered back into this page.</b> It carries every leaf and
    /// its transactions for the wallet, so it discloses the balance, how that balance is split and the
    /// wallet's history — it is the one blob in the plugin that is worth more to a reader than the account it
    /// describes. An operator who wants a copy downloads it.
    /// </para>
    /// </remarks>
    [BindNever]
    public DateTimeOffset? ExitStateBackupTakenAt { get; set; }

    /// <summary>
    /// A blob to store, inbound only. The stored blob is never written into this model.
    /// </summary>
    /// <remarks>
    /// Outbound it is bound but never populated; empty posted to the set action clears the stored backup,
    /// matching the explorer override's pattern. This is typed as string rather than annotated with
    /// <c>[DataType]</c> because it is opaque to this page by design: nothing validates its shape here, since
    /// whether it is a usable blob is settled by the SDK when it is imported.
    /// </remarks>
    [Display(Name = "Exit-state backup")]
    public string? ExitStateBackup { get; set; }

    /// <summary>
    /// A freshly exported blob, for one render, so the operator can copy it.
    /// </summary>
    /// <remarks>
    /// Its own field beside <see cref="ExitStateBackupTakenAt"/> so a render cannot confuse "a backup exists"
    /// with "here is that backup": only the export action sets this, and what it sets is what the SDK just
    /// returned.
    /// </remarks>
    [BindNever]
    public string? ExportedExitState { get; set; }
}
