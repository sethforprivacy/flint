using Breez.Sdk.Spark;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;
using SdkNetwork = Breez.Sdk.Spark.Network;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The setup flow's decisions: what is accepted as a seed, what gets written, in what order, and what is put
/// back when a step fails.
/// </summary>
public class SparkStoreProvisionerTests
{
    private const string StoreId = "store-1";

    /// <summary>The BIP39 test vector. Valid, and famous enough that nobody will mistake it for a real seed.</summary>
    private const string ValidMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    private static SparkMnemonicProtector Protector() => new(new EphemeralDataProtectionProvider());

    private sealed record Harness(
        SparkStoreProvisioner Provisioner,
        FakeSparkStoreSettingsStore Settings,
        FakeStoreLightningConfigStore Config,
        SparkMnemonicProtector Protector,
        WriteLog Writes,
        CapturingLogger<SparkStoreProvisioner> Log);

    private static Harness Create(bool storeExists = true)
    {
        // One shared log across both fakes, because the ordering between them is the invariant that matters and
        // two separate call counters cannot express it.
        var writes = new WriteLog();
        var config = new FakeStoreLightningConfigStore(writes);
        if (storeExists)
            config.Add(StoreId);

        var wiring = new SparkLightningWiring(config, NullLogger<SparkLightningWiring>.Instance);

        // Wired to the wiring so a removal clears the Lightning configuration, as SparkService.Set(null) does.
        var settings = new FakeSparkStoreSettingsStore(wiring, writes);
        var protector = Protector();
        var log = new CapturingLogger<SparkStoreProvisioner>();

        return new Harness(
            new SparkStoreProvisioner(settings, wiring, protector, log),
            settings, config, protector, writes, log);
    }

    #region Mnemonic normalisation

    [Theory]
    // Ragged whitespace, which is what a paste out of a PDF or a password manager actually looks like.
    [InlineData("  abandon   abandon\tabandon abandon abandon abandon abandon abandon abandon abandon abandon about  ")]
    // Casing, which BIP39 wordlists do not use but humans type.
    [InlineData("Abandon ABANDON abandon abandon abandon abandon abandon abandon abandon abandon abandon About")]
    // A newline mid-phrase, from a wrapped textarea.
    [InlineData("abandon abandon abandon abandon abandon abandon\nabandon abandon abandon abandon abandon about")]
    public void Normalisation_canonicalises_a_valid_phrase(string messy)
    {
        Assert.True(SparkStoreProvisioner.TryNormalizeMnemonic(messy, out var normalized, out var error));
        Assert.Null(error);
        Assert.Equal(ValidMnemonic, normalized);
    }

    [Theory]
    [InlineData("  Abandon  abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon ABOUT ")]
    [InlineData("abandon abandon abandon abandon abandon abandon\tabandon abandon abandon abandon abandon about")]
    [InlineData(ValidMnemonic)]
    public void Normalisation_produces_the_exact_spelling_the_wallet_guard_hashes(string messy)
    {
        // Comparing two DeriveWalletKey results would prove nothing: it canonicalises whatever it is handed, so
        // two spellings hash the same even if this method normalised nothing at all. The checkable invariant is
        // stronger — what gets stored must already be a fixed point of that canonicalisation, so the stored seed
        // and the "one live instance per wallet" key agree with no second normalisation step in between. If they
        // ever diverge, two SDK instances can run against one SQLite file and corrupt it.
        Assert.True(SparkStoreProvisioner.TryNormalizeMnemonic(messy, out var normalized, out _));
        Assert.Equal(normalized, SparkService.CanonicaliseMnemonic(normalized));

        // And it reaches that fixed point through the parse path, not the guard's unparseable-input fallback.
        Assert.True(new Mnemonic(normalized).IsValidChecksum);
    }

    [Fact]
    public void An_unnormalised_phrase_is_not_a_fixed_point_of_the_wallet_guard()
    {
        // The control for the test above: without normalisation the invariant genuinely fails, so that test is
        // capable of failing.
        const string messy = "  Abandon  abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon ABOUT ";
        Assert.NotEqual(messy, SparkService.CanonicaliseMnemonic(messy));
    }

    [Fact]
    public void Normalisation_produces_a_phrase_NBitcoin_round_trips()
    {
        var generated = SparkStoreProvisioner.GenerateMnemonic();

        Assert.True(SparkStoreProvisioner.TryNormalizeMnemonic(generated, out var normalized, out _));
        Assert.Equal(generated, normalized);
        Assert.Equal(12, normalized.Split(' ').Length);
        Assert.True(new Mnemonic(normalized).IsValidChecksum);
    }

    [Theory]
    // Nothing typed.
    [InlineData(null, "Enter your recovery phrase")]
    [InlineData("", "Enter your recovery phrase")]
    [InlineData("   ", "Enter your recovery phrase")]
    // Eleven words: not a valid BIP39 length. The count is named, because that is what the merchant can act on.
    [InlineData(
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon",
        "has 11")]
    // Three words. NBitcoin reports this as an undetectable word list ("Unknown"); a word count is the truth
    // the merchant needs.
    [InlineData("not a mnemonic", "has 3")]
    // Right length, no real words. NBitcoin cannot even guess the language here and throws the bare string
    // "Unknown", which is why this message is written locally.
    [InlineData("aaa bbb ccc ddd eee fff ggg hhh iii jjj kkk lll", "not in the BIP39 word list")]
    // Eleven valid words and one that is not. This is the branch whose NBitcoin message names the bad word.
    [InlineData(
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon zzzzsecret1",
        "not in the BIP39 word list")]
    // Twelve real words in the wrong combination: a bad checksum, which only a checksum check catches.
    [InlineData(
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon",
        "invalid checksum")]
    public void Normalisation_rejects_a_phrase_no_wallet_could_use(string? input, string expectedInMessage)
    {
        Assert.False(SparkStoreProvisioner.TryNormalizeMnemonic(input, out var normalized, out var error));
        Assert.Null(normalized);

        // Asserting on content, not merely on non-null: a message that named the wrong problem — or said
        // "Unknown" — would satisfy a null check and tell the merchant nothing.
        Assert.Contains(expectedInMessage, error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // The unknown-word branch. NBitcoin's own text here is "Word zzzzsecret1 is not in the wordlist for this
    // language, cannot continue to rebuild entropy from wordlist" (verified against 8.0.11) — relaying it put a
    // word of the merchant's phrase into ModelState and then into a rendered validation summary. A phrase that
    // fails on one word can be one typo away from a funded wallet.
    [InlineData(
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon zzzzsecret1",
        "zzzzsecret1")]
    // The checksum branch, which never interpolated anything — kept so both are covered by name.
    [InlineData("zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo", "zoo")]
    // A phrase of real words where the count is wrong: the word-count message must not quote them either.
    [InlineData("abandon abandon abandon zzzzsecret1", "zzzzsecret1")]
    public void Normalisation_never_echoes_a_submitted_word_back_in_its_error(string input, string secretWord)
    {
        Assert.False(SparkStoreProvisioner.TryNormalizeMnemonic(input, out _, out var error));
        Assert.DoesNotContain(secretWord, error, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Provisioning

    [Fact]
    public async Task Provision_stores_the_seed_encrypted_and_wires_lightning_up()
    {
        var h = Create();

        var result = await h.Provisioner.ProvisionAsync(StoreId, ValidMnemonic, SeedSource.Imported);

        Assert.True(result.Succeeded);
        Assert.Null(result.Error);

        var stored = Assert.IsType<SparkSettings>(h.Settings.Settings[StoreId]);
        Assert.Equal(SeedSource.Imported, stored.SeedSource);
        Assert.NotNull(stored.PaymentKey);

        // The settings blob lives in BTCPay's database as plain JSON, so this is the assertion that matters.
        Assert.NotNull(stored.ProtectedMnemonic);
        Assert.DoesNotContain("abandon", stored.ProtectedMnemonic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ValidMnemonic, h.Protector.TryUnprotect(stored.ProtectedMnemonic));

        Assert.Equal(
            SparkConnectionString.Format(StoreId, stored.PaymentKey!),
            h.Config.Stores[StoreId].ConnectionString);
    }

    [Fact]
    public async Task Provision_normalises_before_storing()
    {
        var h = Create();

        Assert.True((await h.Provisioner.ProvisionAsync(
            StoreId,
            "  abandon   abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon ABOUT ",
            SeedSource.Imported)).Succeeded);

        Assert.Equal(ValidMnemonic, h.Protector.TryUnprotect(h.Settings.Settings[StoreId]!.ProtectedMnemonic));
    }

    [Fact]
    public async Task Provision_rejects_an_invalid_seed_before_writing_anything()
    {
        var h = Create();

        var result = await h.Provisioner.ProvisionAsync(StoreId, "not a mnemonic", SeedSource.Imported);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        Assert.Empty(h.Settings.Writes);
        Assert.Empty(h.Config.Writes);
    }

    [Fact]
    public async Task Provision_wires_lightning_up_only_after_the_wallet_starts()
    {
        // A store must never advertise a Lightning wallet that failed to start, so the settings write — which is
        // what starts the wallet — has to happen before the Lightning write. Asserted against a log shared by
        // both fakes: two independent Assert.Single counters are satisfied just as well by the reverse order,
        // which is exactly the bug this is meant to catch.
        var h = Create();

        Assert.True((await h.Provisioner.ProvisionAsync(StoreId, ValidMnemonic, SeedSource.Generated)).Succeeded);

        Assert.Equal(
            [$"settings:{StoreId}:stored", $"lightning:{StoreId}:set"],
            h.Writes.Entries);
    }

    [Fact]
    public async Task Provision_rolls_the_settings_back_when_the_wallet_will_not_start()
    {
        // The seed the SDK rejects but NBitcoin accepts — a valid phrase for a wallet the SDK cannot open.
        // The real settings store persists first and only then connects, so a failure leaves settings behind
        // unless they are undone.
        var h = Create();
        h.Settings.FailNextSetWith = new SdkException.Generic("the mnemonic has an invalid checksum");

        var result = await h.Provisioner.ProvisionAsync(StoreId, ValidMnemonic, SeedSource.Imported);

        Assert.False(result.Succeeded);
        Assert.Contains("invalid checksum", result.Error);
        Assert.Null(h.Settings.Settings[StoreId]);
        Assert.Empty(h.Config.Writes);
    }

    [Fact]
    public async Task Provision_rolls_back_when_the_store_disappeared()
    {
        var h = Create(storeExists: false);

        var result = await h.Provisioner.ProvisionAsync(StoreId, ValidMnemonic, SeedSource.Generated);

        Assert.False(result.Succeeded);
        Assert.Null(h.Settings.Settings[StoreId]);
    }

    [Fact]
    public async Task Provision_restores_the_previous_configuration_when_a_replacement_fails()
    {
        // A merchant replacing a seed must not lose the wallet they already had because the new phrase was
        // rejected.
        var h = Create();
        Assert.True((await h.Provisioner.ProvisionAsync(StoreId, ValidMnemonic, SeedSource.Generated)).Succeeded);
        var original = h.Settings.Settings[StoreId];

        h.Settings.FailNextSetWith = new SdkException.Generic("mnemonic contains an unknown word (word 0)");
        var replacement = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();

        Assert.False((await h.Provisioner.ProvisionAsync(StoreId, replacement, SeedSource.Imported)).Succeeded);
        Assert.Same(original, h.Settings.Settings[StoreId]);
    }

    [Fact]
    public async Task Provision_keeps_the_payment_key_and_every_settings_block_across_a_seed_change()
    {
        // The payment key is a store-binding token, not a secret that ages, and rotating it would invalidate a
        // Lightning configuration that is already live. The settings blocks are the merchant's, and changing a
        // seed is not a request to lose them — every nested block has to be carried, and the one that goes
        // missing when a new block is added is the one nobody asserted.
        var h = Create();
        Assert.True((await h.Provisioner.ProvisionAsync(StoreId, ValidMnemonic, SeedSource.Generated)).Succeeded);

        var first = h.Settings.Settings[StoreId]!;
        first.Sweep.Enabled = true;
        first.Sweep.BalanceThresholdSats = 100_000;
        first.Deposits.ClaimFeeLeewaySatPerVbyte = 9;
        first.StableBalance.DisclosureAcknowledged = true;
        // Infrastructure configuration, which has nothing to do with which seed the store runs on — and off
        // mainnet losing it means the next unilateral exit refuses for want of a block explorer.
        first.UnilateralExit.DisclosureAcknowledged = true;
        first.UnilateralExit.EsploraApiUrl = "https://explorer.test/api";
        first.ApiKeyOverride = "merchant-key";

        var replacement = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();
        Assert.True((await h.Provisioner.ProvisionAsync(StoreId, replacement, SeedSource.Imported)).Succeeded);

        var second = h.Settings.Settings[StoreId]!;
        Assert.Equal(first.PaymentKey, second.PaymentKey);
        Assert.True(second.Sweep.Enabled);
        Assert.Equal(100_000, second.Sweep.BalanceThresholdSats);
        Assert.Equal(9, second.Deposits.ClaimFeeLeewaySatPerVbyte);
        Assert.True(second.StableBalance.DisclosureAcknowledged);
        Assert.True(second.UnilateralExit.DisclosureAcknowledged);
        Assert.Equal("https://explorer.test/api", second.UnilateralExit.EsploraApiUrl);
        Assert.Equal("merchant-key", second.ApiKeyOverride);
        Assert.Equal(SeedSource.Imported, second.SeedSource);

        // Copied, not aliased. Sharing a block with the object the caller still holds would make a later edit to
        // one silently edit the other — including the copy a failed attempt is supposed to roll back to.
        Assert.NotSame(first.Sweep, second.Sweep);
        Assert.NotSame(first.Deposits, second.Deposits);
        Assert.NotSame(first.StableBalance, second.StableBalance);
        Assert.NotSame(first.UnilateralExit, second.UnilateralExit);
    }

    [Fact]
    public async Task Provision_gives_different_stores_different_payment_keys()
    {
        var h = Create();
        h.Config.Add("store-2");

        Assert.True((await h.Provisioner.ProvisionAsync(StoreId, ValidMnemonic, SeedSource.Generated)).Succeeded);
        Assert.True((await h.Provisioner.ProvisionAsync(
            "store-2", SparkStoreProvisioner.GenerateMnemonic(), SeedSource.Generated)).Succeeded);

        Assert.NotEqual(h.Settings.Settings[StoreId]!.PaymentKey, h.Settings.Settings["store-2"]!.PaymentKey);
    }

    [Fact]
    public async Task Provision_fails_and_rolls_back_when_a_second_store_is_given_the_same_seed()
    {
        // The production scenario this whole outcome-reporting change exists for. A merchant with two stores on
        // one server reuses the same hot-wallet seed for both; SparkService's wallet-uniqueness guard refuses the
        // second — two SDK instances on one wallet corrupt its SQLite file — and it refuses by *returning*, with
        // no exception to catch. Before this, the second store got "Spark is now set up", an enabled Lightning
        // payment method, and a checkout that failed every single time.
        var h = Create();
        h.Config.Add("store-2");

        Assert.True((await h.Provisioner.ProvisionAsync(StoreId, ValidMnemonic, SeedSource.HotWallet)).Succeeded);

        h.Settings.AlwaysDeclineWith =
            "Another store on this server already uses this recovery phrase.";

        var second = await h.Provisioner.ProvisionAsync("store-2", ValidMnemonic, SeedSource.HotWallet);

        Assert.False(second.Succeeded);
        Assert.Contains("already uses this recovery phrase", second.Error);

        // Rolled back, and — the part that actually broke checkout — no Lightning payment method written.
        Assert.Null(h.Settings.Settings["store-2"]);
        Assert.Null(h.Config.Stores["store-2"].ConnectionString);
        Assert.DoesNotContain(h.Config.Writes, write => write.StoreId == "store-2");
    }

    [Fact]
    public async Task Provision_reports_the_reason_when_the_chain_is_unsupported()
    {
        // Same quiet-failure shape, different cause: a BTCPay running on testnet or signet, which the SDK has no
        // network for at all.
        var h = Create();
        h.Settings.NextSetDeclinesWith = "The Spark SDK supports mainnet and regtest only; this server runs on testnet.";

        var result = await h.Provisioner.ProvisionAsync(StoreId, ValidMnemonic, SeedSource.Generated);

        Assert.False(result.Succeeded);
        Assert.Contains("mainnet and regtest only", result.Error);
        Assert.Null(h.Settings.Settings[StoreId]);
        Assert.Empty(h.Config.Writes);
    }

    [Fact]
    public async Task Provision_does_not_alias_the_previous_sweep_settings()
    {
        // Settings objects are handed out by reference from the service's cache. Aliasing the sweep block across
        // a seed change would make a later edit to the new configuration silently edit the old one — including
        // the copy a failed attempt is supposed to roll back to.
        var h = Create();
        Assert.True((await h.Provisioner.ProvisionAsync(StoreId, ValidMnemonic, SeedSource.Generated)).Succeeded);

        var first = h.Settings.Settings[StoreId]!;
        first.Sweep.BalanceThresholdSats = 100_000;

        Assert.True((await h.Provisioner.ProvisionAsync(
            StoreId, SparkStoreProvisioner.GenerateMnemonic(), SeedSource.Imported)).Succeeded);

        var second = h.Settings.Settings[StoreId]!;
        Assert.Equal(100_000, second.Sweep.BalanceThresholdSats);
        Assert.NotSame(first.Sweep, second.Sweep);

        second.Sweep.BalanceThresholdSats = 7;
        Assert.Equal(100_000, first.Sweep.BalanceThresholdSats);
    }

    [Fact]
    public async Task Provision_never_logs_a_word_of_the_seed()
    {
        // Unfalsifiable with NullLogger, which is what every other test injects: a line that printed the
        // merchant's phrase would pass the whole suite. Includes the path that catches an SDK exception, because
        // that exception came out of a call whose argument was the phrase.
        var h = Create();
        h.Settings.FailNextSetWith = new SdkException.Generic(
            "Word zzzzsecret1 is not in the wordlist for this language");

        await h.Provisioner.ProvisionAsync(StoreId, ValidMnemonic, SeedSource.Imported);
        await h.Provisioner.ProvisionAsync(StoreId, ValidMnemonic, SeedSource.Imported);
        await h.Provisioner.RemoveAsync(StoreId);

        Assert.NotEmpty(h.Log.Lines);
        Assert.DoesNotContain("abandon", h.Log.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("about", h.Log.AllText, StringComparison.OrdinalIgnoreCase);

        // Nor the text of the exception it caught, which is not this plugin's to vouch for.
        Assert.DoesNotContain("zzzzsecret1", h.Log.AllText, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Removal

    [Fact]
    public async Task Remove_clears_the_settings()
    {
        var h = Create();
        Assert.True((await h.Provisioner.ProvisionAsync(StoreId, ValidMnemonic, SeedSource.Generated)).Succeeded);

        await h.Provisioner.RemoveAsync(StoreId);

        Assert.Null(h.Settings.Settings[StoreId]);
        Assert.Null(h.Settings.Writes[^1].Settings);
    }

    [Fact]
    public async Task Remove_clears_the_lightning_payment_method_it_wrote()
    {
        // RemoveAsync does not clear the Lightning configuration itself — SparkService.Set(null) does, because
        // that is the single choke point for "this store no longer has a Spark wallet". This pins that coupling
        // end to end, so removing the clearing from either side fails a test.
        var h = Create();
        Assert.True((await h.Provisioner.ProvisionAsync(StoreId, ValidMnemonic, SeedSource.Generated)).Succeeded);
        Assert.NotNull(h.Config.Stores[StoreId].ConnectionString);

        await h.Provisioner.RemoveAsync(StoreId);

        Assert.Null(h.Config.Stores[StoreId].ConnectionString);
        Assert.Equal(
            [
                $"settings:{StoreId}:stored", $"lightning:{StoreId}:set",
                $"settings:{StoreId}:removed", $"lightning:{StoreId}:cleared"
            ],
            h.Writes.Entries);
    }

    [Fact]
    public async Task Remove_leaves_another_nodes_configuration_alone()
    {
        var h = Create();
        Assert.True((await h.Provisioner.ProvisionAsync(StoreId, ValidMnemonic, SeedSource.Generated)).Succeeded);

        // The merchant moved to their own node after setting Spark up.
        const string ownNode = "type=lnd-rest;server=https://127.0.0.1:8080/;macaroon=abcdef";
        h.Config.Add(StoreId, ownNode);

        await h.Provisioner.RemoveAsync(StoreId);

        Assert.Equal(ownNode, h.Config.Stores[StoreId].ConnectionString);
    }

    #endregion
}
