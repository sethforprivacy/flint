using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Sdk;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// Outcome of a provisioning attempt. <see cref="Error"/> is set exactly when it failed, and is written for a
/// merchant to read on the setup page.
/// </summary>
public sealed record SparkProvisionResult(bool Succeeded, string? Error)
{
    public static readonly SparkProvisionResult Ok = new(true, null);
    public static SparkProvisionResult Failed(string error) => new(false, error);
}

/// <summary>
/// Turns "this store should use this seed" into a configured, running store.
/// </summary>
/// <remarks>
/// <para>
/// Everything the setup flow decides lives here rather than in the controller, so it can be tested against
/// fakes: the controller's job is reduced to picking a seed source, calling one method, and rendering the
/// result. That was the biggest coverage gap the Wave 2 reviews flagged.
/// </para>
/// <para><b>Order of operations, and why.</b> The seed is normalised and validated first, because everything
/// after it is a write. Then the settings are stored, which is what connects the SDK — and the SDK is the only
/// thing that can definitively reject a seed. Only once a wallet is <em>confirmed running</em> does the store's
/// Lightning payment method get pointed at it, so a store never advertises a Lightning wallet that failed to
/// start. If the wallet cannot start, the settings are rolled back rather than left behind, because a persisted
/// seed with no instance is a store that looks configured, takes no payments, and explains itself only in the
/// server log.
/// </para>
/// <para>
/// "Confirmed running" is load-bearing and was once assumed. Not every refusal throws: the wallet-uniqueness
/// guard declining a seed another store already owns, an unsupported chain, a seed this server can no longer
/// decrypt — all of those return normally. Treating a returned <see cref="ISparkStoreSettingsStore.SetAsync"/>
/// as success is what told a merchant with one hot-wallet seed and two stores that both were ready, while every
/// checkout on the second failed. <see cref="SparkSettingsApplied.WalletRunning"/> is the answer, and it must be
/// checked.
/// </para>
/// </remarks>
public sealed class SparkStoreProvisioner
{
    /// <summary>Word count for a freshly generated seed. 12 words is BTCPay's own default.</summary>
    private const WordCount GeneratedWordCount = WordCount.Twelve;

    /// <summary>BIP39's permitted lengths. The SDK accepts all five (verified on regtest).</summary>
    private static readonly int[] ValidWordCounts = [12, 15, 18, 21, 24];

    private readonly ISparkStoreSettingsStore _settingsStore;
    private readonly SparkLightningWiring _lightningWiring;
    private readonly SparkMnemonicProtector _mnemonicProtector;
    private readonly ILogger<SparkStoreProvisioner> _logger;

    public SparkStoreProvisioner(
        ISparkStoreSettingsStore settingsStore,
        SparkLightningWiring lightningWiring,
        SparkMnemonicProtector mnemonicProtector,
        ILogger<SparkStoreProvisioner> logger)
    {
        _settingsStore = settingsStore;
        _lightningWiring = lightningWiring;
        _mnemonicProtector = mnemonicProtector;
        _logger = logger;
    }

    /// <summary>
    /// A fresh BIP39 mnemonic, in the canonical spacing <see cref="TryNormalizeMnemonic"/> produces.
    /// </summary>
    public static string GenerateMnemonic() =>
        new Mnemonic(Wordlist.English, GeneratedWordCount).ToString();

    /// <summary>
    /// Canonicalises a mnemonic and rejects one no wallet could use, with a message fit for a merchant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Normalisation is not cosmetic. <c>SparkService.DeriveWalletKey</c> canonicalises the same way to decide
    /// whether two stores are on the same wallet, and its guard is what prevents two live SDK instances
    /// corrupting one SQLite file. A seed stored with a double space, a tab or mixed casing would defeat that
    /// guard if it were not normalised on the way in.
    /// </para>
    /// <para>
    /// Validation duplicates something the SDK also does — <c>Connect</c> rejects bad word counts, unknown
    /// words and bad checksums. Doing it here as well is worth it because it happens before any write, so a
    /// typo costs a re-render rather than a persisted-then-rolled-back store.
    /// </para>
    /// <para>
    /// <b>Every message below is written here, and none relays NBitcoin's.</b> Two of NBitcoin's are unfit to
    /// show: an unknown word produces <c>"Word zzzzsecret1 is not in the wordlist for this language…"</c>,
    /// which puts a word of the merchant's phrase — possibly one word away from a funded wallet — into
    /// <c>ModelState</c> and then into a rendered validation summary; and a phrase whose language it cannot
    /// guess produces the bare word <c>"Unknown"</c>, which tells nobody anything. Both were verified against
    /// NBitcoin 8.0.11.
    /// </para>
    /// </remarks>
    public static bool TryNormalizeMnemonic(
        string? input,
        [NotNullWhen(true)] out string? normalized,
        [NotNullWhen(false)] out string? error)
    {
        normalized = null;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Enter your recovery phrase.";
            return false;
        }

        // Case and whitespace are flattened before NBitcoin sees the phrase. BIP39 wordlists are lower case
        // and NBitcoin matches them exactly, so "Abandon" is an unknown word to it — but it is what a merchant
        // gets from a phone keyboard's auto-capitalisation or a paste out of a formatted document, and
        // rejecting it would be a puzzle rather than an error. Invariant lower-casing on purpose: a Turkish
        // locale would otherwise map "I" to a dotless "ı" and break otherwise-valid English phrases.
        var words = input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var flattened = string.Join(' ', words).ToLowerInvariant();

        // Counted before parsing, so a wrong length is reported as a wrong length. NBitcoin reaches its own
        // word-count check only after it has managed to guess the language, so a three-word phrase reports an
        // undetectable word list instead — technically true, and useless to the person holding the phrase.
        if (!ValidWordCounts.Contains(words.Length))
        {
            error = $"A recovery phrase has 12, 15, 18, 21 or 24 words. This one has {words.Length}.";
            return false;
        }

        Mnemonic mnemonic;
        try
        {
            mnemonic = new Mnemonic(flattened);
        }
        catch (Exception)
        {
            // Deliberately swallowed whole. Past the word-count check, everything NBitcoin can throw here means
            // the same thing to a merchant — a word it does not recognise, whether it says so or merely fails to
            // identify the language — and its text for the first case contains the offending word.
            error = "One or more of those words are not in the BIP39 word list. Check for typos, and note that "
                    + "the phrase must be in a single language.";
            return false;
        }

        if (!mnemonic.IsValidChecksum)
        {
            // A phrase can be all-real words in the right count and still not be a mnemonic. Caught here
            // because the SDK's version of this message ("the mnemonic has an invalid checksum") arrives only
            // after the settings have been written.
            error = "That recovery phrase has an invalid checksum. Check for mistyped or reordered words.";
            return false;
        }

        // Lower case by construction, matching how SparkService.DeriveWalletKey canonicalises before hashing,
        // so the same seed cannot look like two wallets to the single-instance-per-wallet guard.
        normalized = string.Join(' ', mnemonic.Words);
        return true;
    }

    /// <summary>
    /// Configures a store to use <paramref name="mnemonic"/>, starts its wallet, and points the store's
    /// Lightning payment method at it.
    /// </summary>
    /// <param name="seedSource">Recorded in the settings so the status page can explain the security posture.</param>
    public async Task<SparkProvisionResult> ProvisionAsync(
        string storeId,
        string? mnemonic,
        SeedSource seedSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        if (!TryNormalizeMnemonic(mnemonic, out var normalized, out var mnemonicError))
            return SparkProvisionResult.Failed(mnemonicError);

        var existing = await _settingsStore.GetAsync(storeId).ConfigureAwait(false);

        // An existing payment key is kept rather than rotated. The key is not a secret that ages — it is a
        // store-binding token — and rotating it would invalidate a Lightning configuration that is already
        // written and working for the window between storing the settings and rewriting that configuration.
        var paymentKey = existing?.PaymentKey ?? SparkConnectionString.GeneratePaymentKey();

        var settings = new SparkSettings
        {
            ProtectedMnemonic = _mnemonicProtector.Protect(normalized),
            PaymentKey = paymentKey,
            SeedSource = seedSource,

            // Carried over, not reset: a merchant changing their seed has not asked to lose their sweep
            // configuration or their API key override. Wave 4's sweep settings ride on this.
            //
            // Copied rather than aliased. Sharing the instance with the settings object still held by the
            // caller — and by the service's own cache — means a later edit to one silently edits the other,
            // and the rollback below would restore a "previous" object that had already been mutated.
            ApiKeyOverride = existing?.ApiKeyOverride,
            Sweep = existing?.Sweep is { } previousSweep ? previousSweep.Clone() : new SweepSettings(),
            Deposits = existing?.Deposits is { } previousDeposits
                ? previousDeposits.Clone()
                : new SparkDepositSettings(),

            // Carried across like the rest, and the copy matters just as much: this object decides whether the
            // new wallet is configured for Stable Balance at all. Note that carrying the *setting* across does
            // not carry the wallet's *state* — a new seed is a new wallet with no cached active label, so the
            // Stable Balance page will report the two as disagreeing until the merchant re-applies. That is the
            // honest behaviour: re-activating silently would convert the new wallet's balance without being
            // asked.
            StableBalance = existing?.StableBalance is { } previousStable
                ? previousStable.Clone()
                : new StableBalanceSettings(),

            // Carried across like the rest, and both halves earn it. The explorer override is a piece of
            // infrastructure configuration that has nothing to do with which seed the store runs on, and losing
            // it on a regtest server means the next exit refuses with "no block explorer is configured". The
            // acknowledgement is the operator's statement that they have read what a unilateral exit costs them,
            // which a seed change does not un-read.
            //
            // Note what this does not carry: any exit already recorded. Those rows name a funding address
            // derived from the *old* seed, and the build re-derives and refuses when the two disagree — which is
            // the honest outcome, because the plugin can no longer sign for what was sent there.
            UnilateralExit = existing?.UnilateralExit is { } previousExit
                ? previousExit.Clone()
                : new UnilateralExitSettings()
        };

        SparkSettingsApplied applied;
        try
        {
            applied = await _settingsStore.SetAsync(storeId, settings).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The exception object is deliberately not attached to this log line. It came out of a call whose
            // argument was the merchant's recovery phrase, and its text is not this plugin's to vouch for — the
            // SDK's own wording for a rejected seed is currently an index ("unknown word (word 0)") but nothing
            // guarantees a future version will not name the word. The merchant gets the description below,
            // because they typed the phrase; the operator log gets the type and the store, and the SDK's own log
            // bridge records its side independently.
            _logger.LogError(
                "Store {StoreId}: could not start a Spark wallet for the supplied seed ({ExceptionType})",
                storeId, ex.GetType().Name);
            await RollBackAsync(storeId, existing).ConfigureAwait(false);
            return SparkProvisionResult.Failed(
                $"The Spark wallet could not be started: {SparkErrors.Describe(ex)}");
        }

        if (!applied.WalletRunning)
        {
            // The quiet failures: a seed another store already owns, a chain the SDK does not support, a seed
            // this server can no longer decrypt. None of them throws, and treating "it returned" as "it is
            // running" is what told merchants Spark was ready while every checkout failed.
            await RollBackAsync(storeId, existing).ConfigureAwait(false);
            return SparkProvisionResult.Failed(
                applied.Reason ?? "The Spark wallet did not start. Check the server logs for the reason.");
        }

        if (!await _lightningWiring.EnableAsync(storeId, paymentKey, cancellationToken).ConfigureAwait(false))
        {
            await RollBackAsync(storeId, existing).ConfigureAwait(false);
            return SparkProvisionResult.Failed(
                "The store could not be updated. It may have been deleted while you were setting Spark up.");
        }

        _logger.LogInformation(
            "Store {StoreId}: Spark configured from a {SeedSource} seed", storeId, seedSource);
        return SparkProvisionResult.Ok;
    }

    /// <summary>
    /// Removes a store's Spark configuration, shutting its wallet down and clearing the Lightning payment
    /// method it wrote.
    /// </summary>
    /// <remarks>
    /// The clearing happens inside <see cref="ISparkStoreSettingsStore.SetAsync"/>, which is the single choke
    /// point for "this store no longer has a Spark wallet" and therefore covers store deletion and any future
    /// API caller too. The keys are destroyed; the SDK storage directory is deliberately kept, since it holds
    /// the record of settled payments for a wallet whose seed the merchant still controls.
    /// </remarks>
    public async Task RemoveAsync(string storeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        await _settingsStore.SetAsync(storeId, null).ConfigureAwait(false);
        _logger.LogInformation("Store {StoreId}: Spark configuration removed", storeId);
    }

    /// <summary>
    /// Puts the store back the way it was after a failed provisioning attempt.
    /// </summary>
    /// <remarks>
    /// Best effort by necessity: this runs because something already failed, so it may fail too. A failure is
    /// logged and swallowed, because the merchant needs the real error from the caller and not this one.
    /// </remarks>
    private async Task RollBackAsync(string storeId, SparkSettings? previous)
    {
        try
        {
            await _settingsStore.SetAsync(storeId, previous).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Store {StoreId}: could not restore the previous Spark settings after a failed setup attempt. "
                + "The store's Spark configuration may need to be re-entered", storeId);
        }
    }
}
