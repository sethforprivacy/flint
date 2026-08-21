using System.Linq;
using Newtonsoft.Json.Linq;
using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Flint.Models;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// Provisioning a store through the API: the seed sources, the policy gate, and what may leave the server.
/// </summary>
public class GreenfieldSparkProvisioningTests
{
    private const string Store = SparkSurfaceHarness.AttackerStore;

    [Fact]
    public async Task Generating_returns_the_phrase_once_and_stores_exactly_that_phrase()
    {
        var h = SparkSurfaceHarness.Create();

        var response = AssertOk<SparkProvisionResponse>(await h.Api.Provision(
            Store, new SparkProvisionRequest { SeedSource = "generate" }, CancellationToken.None));

        // A real BIP39 phrase, not a placeholder: the merchant's funds depend on it, so a test that only asserted
        // "not null" would pass against a stub.
        Assert.NotNull(response.Mnemonic);
        var mnemonic = new Mnemonic(response.Mnemonic);
        Assert.True(mnemonic.IsValidChecksum);
        Assert.Equal(12, mnemonic.Words.Length);

        // And it is the phrase the server actually stored, not a phrase it showed and then discarded.
        Assert.Equal(response.Mnemonic, h.StoredMnemonic(Store));
        Assert.True(response.Status.Configured);
        Assert.True(response.Status.WalletRunning);
    }

    [Fact]
    public async Task The_generated_phrase_never_appears_again_on_any_read_endpoint()
    {
        // The single-disclosure rule, asserted against the serialised bodies rather than against a property, so a
        // future field that happened to carry the seed would fail here too.
        var h = SparkSurfaceHarness.Create();

        var provisioned = AssertOk<SparkProvisionResponse>(await h.Api.Provision(
            Store, new SparkProvisionRequest { SeedSource = "generate" }, CancellationToken.None));
        var mnemonic = provisioned.Mnemonic!;

        var status = AssertOk<SparkStatusData>(await h.Api.GetStatus(Store, CancellationToken.None));
        var sweep = AssertOk<SparkSweepConfigurationData>(
            await h.Api.GetSweepConfiguration(Store, 0, 25, CancellationToken.None));

        foreach (var body in new object[] { status, sweep })
        {
            var json = ApiJson.Serialize(body);

            Assert.DoesNotContain(mnemonic, json, StringComparison.OrdinalIgnoreCase);

            // Word by word as well, because a leak that reordered or truncated the phrase is still a leak, and the
            // first four words are enough to brute-force the rest of a 12-word phrase far too cheaply.
            //
            // Against the *values* only, never the property names. Matching `"word"` in the raw JSON also matched
            // keys, and BIP39 is ordinary English: a generated phrase containing "history" collided with
            // SparkSweepConfigurationData's own `"history"` property and failed a test with no leak in it at all.
            // "index", "amount", "network", "select", "process" and "total" are the same accident waiting to
            // happen. A security guard that fires at random is worse than no guard, because the next real failure
            // gets re-run and dismissed.
            AssertNoSeedMaterialInValues(mnemonic, json);

            // The protected form must not travel either: it is decryptable by this server.
            Assert.DoesNotContain(
                h.Settings.Settings[Store]!.ProtectedMnemonic!, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Importing_normalises_the_phrase_before_storing_it()
    {
        // Normalisation is not cosmetic: SparkService derives a store's wallet key from the canonicalised phrase to
        // decide whether two stores are on the same wallet, and that guard is what stops two live SDK instances
        // corrupting one storage directory. A phrase stored with stray whitespace or capitals would defeat it.
        var h = SparkSurfaceHarness.Create();

        var messy = "  Abandon\tabandon   ABANDON abandon abandon abandon abandon abandon abandon abandon "
                    + "abandon About  ";

        var response = AssertOk<SparkProvisionResponse>(await h.Api.Provision(
            Store, new SparkProvisionRequest { SeedSource = "import", Mnemonic = messy }, CancellationToken.None));

        // Never returned for an import: the caller supplied it.
        Assert.Null(response.Mnemonic);
        Assert.Equal(SparkSurfaceHarness.ValidMnemonic, h.StoredMnemonic(Store));
        Assert.Equal(SeedSource.Imported, response.Status.SeedSource);
    }

    [Theory]
    // An unknown word, alongside eleven real ones. NBitcoin's own message for this case names the offending word.
    [InlineData(
        "zzzzsecret1 abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about",
        "zzzzsecret1")]
    // Right words, wrong checksum — a reordering or a typo within the wordlist.
    [InlineData(
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon",
        null)]
    // Wrong length.
    [InlineData("abandon abandon about", null)]
    // Empty.
    [InlineData("   ", null)]
    public async Task Importing_an_unusable_phrase_is_refused_without_quoting_it(string phrase, string? secretWord)
    {
        var h = SparkSurfaceHarness.Create();

        var errors = AssertValidationError(await h.Api.Provision(
            Store, new SparkProvisionRequest { SeedSource = "import", Mnemonic = phrase }, CancellationToken.None));

        var error = Assert.Single(errors);
        Assert.Equal("mnemonic", error.Path);
        Assert.NotEmpty(error.Message);

        // Plugin-authored, not relayed. NBitcoin's messages for these cases are either unfit to show a merchant
        // ("Unknown") or contain a word of their phrase, so none of them may reach a response body.
        Assert.DoesNotContain("wordlist for this language", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("Unknown", error.Message);

        if (secretWord is not null)
            Assert.DoesNotContain(secretWord, error.Message, StringComparison.OrdinalIgnoreCase);

        // No word of the submitted phrase is echoed, whether or not NBitcoin would have named one.
        foreach (var word in phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            Assert.DoesNotContain(word, error.Message, StringComparison.OrdinalIgnoreCase);

        // And nothing was written on the way to refusing.
        Assert.Empty(h.Settings.Writes);
        Assert.Empty(h.Lightning.Writes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wibble")]
    // Named on purpose: the API offers no unilateral-exit path, so asking for one is simply an
    // unrecognised seed source rather than something the API quietly interprets.
    [InlineData("unilateral-exit")]
    public async Task An_unrecognised_seed_source_is_refused_with_the_plugins_own_message(string? value)
    {
        var h = SparkSurfaceHarness.Create();

        var errors = AssertValidationError(await h.Api.Provision(
            Store, new SparkProvisionRequest { SeedSource = value }, CancellationToken.None));

        var error = Assert.Single(errors);
        Assert.Equal("seedSource", error.Path);
        Assert.Contains("generate", error.Message, StringComparison.Ordinal);
        Assert.Contains("hotWallet", error.Message, StringComparison.Ordinal);

        Assert.Empty(h.Settings.Writes);
    }

    [Theory]
    [InlineData("generate")]
    [InlineData("import")]
    [InlineData("hotWallet")]
    public async Task Every_seed_source_is_behind_the_servers_hot_wallet_policy(string seedSource)
    {
        // Including seed reuse, which is the one it is tempting to exempt: it copies key material the server already
        // holds into a second wallet, which is exactly the capability the policy exists to control.
        var h = SparkSurfaceHarness.Create(
            allowHotWalletForAll: false,
            hotWalletSeed: HotWalletSeedResult.Found(SparkSurfaceHarness.ValidMnemonic));

        var result = await h.Api.Provision(
            Store,
            new SparkProvisionRequest { SeedSource = seedSource, Mnemonic = SparkSurfaceHarness.ValidMnemonic },
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        var error = Assert.IsType<GreenfieldAPIError>(objectResult.Value);
        Assert.Equal("hot-wallet-not-allowed", error.Code);

        Assert.Empty(h.Settings.Writes);
        Assert.Empty(h.Lightning.Writes);

        // The gate runs before anything else, so the store's on-chain seed was never even read.
        Assert.Empty(h.SeedReader.Reads);
    }

    [Fact]
    public async Task The_policy_gate_is_the_same_gate_the_setup_page_applies()
    {
        // Parity, asserted rather than assumed: a policy enforced on one surface and not the other is advisory.
        var h = SparkSurfaceHarness.Create(allowHotWalletForAll: false);

        var page = await h.Mvc.Setup(
            Store,
            new SparkSetupViewModel { SeedSource = SeedSource.Generated },
            CancellationToken.None);
        var api = await h.Api.Provision(
            Store, new SparkProvisionRequest { SeedSource = "generate" }, CancellationToken.None);

        // The page redirects with an error banner; the API answers 403. Different reporting, same refusal — and
        // neither wrote anything.
        Assert.IsType<RedirectToActionResult>(page);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(api).StatusCode);
        Assert.Empty(h.Settings.Writes);
        Assert.Null(h.Settings.Settings.GetValueOrDefault(Store));
    }

    [Theory]
    [InlineData(HotWalletSeedStatus.NotAHotWallet, "This store's Bitcoin wallet is watch-only.")]
    [InlineData(HotWalletSeedStatus.NoSeedStored, "This wallet was imported from an extended private key.")]
    [InlineData(HotWalletSeedStatus.Unavailable, "This server does not expose hot-wallet seeds to plugins.")]
    public async Task Reusing_an_unavailable_on_chain_seed_is_refused_with_the_readers_reason(
        HotWalletSeedStatus status,
        string reason)
    {
        var h = SparkSurfaceHarness.Create(
            hotWalletSeed: HotWalletSeedResult.NotAvailable(status, reason));

        var errors = AssertValidationError(await h.Api.Provision(
            Store, new SparkProvisionRequest { SeedSource = "hotWallet" }, CancellationToken.None));

        var error = Assert.Single(errors);
        Assert.Equal("seedSource", error.Path);
        Assert.Equal(reason, error.Message);

        Assert.Empty(h.Settings.Writes);
        Assert.Empty(h.Lightning.Writes);
    }

    [Fact]
    public async Task Reusing_an_available_on_chain_seed_provisions_without_handing_the_seed_back()
    {
        var h = SparkSurfaceHarness.Create(
            hotWalletSeed: HotWalletSeedResult.Found(SparkSurfaceHarness.ValidMnemonic));

        var response = AssertOk<SparkProvisionResponse>(await h.Api.Provision(
            Store, new SparkProvisionRequest { SeedSource = "hotWallet" }, CancellationToken.None));

        // The store is set up from its on-chain seed, and that seed does not leave the server: it is not the
        // plugin's to disclose, and a caller holding an API key is not necessarily holding the wallet's backup.
        Assert.Null(response.Mnemonic);
        Assert.Equal(SeedSource.HotWallet, response.Status.SeedSource);
        Assert.Equal(SparkSurfaceHarness.ValidMnemonic, h.StoredMnemonic(Store));
        Assert.DoesNotContain(
            SparkSurfaceHarness.ValidMnemonic,
            ApiJson.Serialize(response.Status),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_wallet_is_confirmed_running_before_the_stores_Lightning_config_is_written()
    {
        // The ordering invariant, asserted on the shared monotonic write log rather than as two independent call
        // counts — which would pass just as happily with the writes reversed. A store must never advertise a
        // Lightning wallet that has not started.
        var h = SparkSurfaceHarness.Create();

        AssertOk<SparkProvisionResponse>(await h.Api.Provision(
            Store, new SparkProvisionRequest { SeedSource = "generate" }, CancellationToken.None));

        var settingsWrite = h.WriteLog.Entries.IndexOf($"settings:{Store}:stored");
        var lightningWrite = h.WriteLog.Entries.IndexOf($"lightning:{Store}:set");

        Assert.True(settingsWrite >= 0, "the settings were never stored");
        Assert.True(lightningWrite >= 0, "the Lightning payment method was never written");
        Assert.True(
            settingsWrite < lightningWrite,
            "the store's Lightning payment method was written before the wallet was confirmed running: "
            + string.Join(" -> ", h.WriteLog.Entries));
    }

    [Fact]
    public async Task A_wallet_that_declines_to_start_rolls_the_settings_back_and_writes_no_Lightning_config()
    {
        // The quiet failure: a seed another store already owns, an unsupported chain, a seed this server can no
        // longer decrypt. None of them throws, and treating "it returned" as "it is running" is what once told a
        // merchant Spark was ready while every checkout failed.
        var h = SparkSurfaceHarness.Create();
        h.Settings.AlwaysDeclineWith = "Another store is already using this wallet.";

        var errors = AssertValidationError(await h.Api.Provision(
            Store, new SparkProvisionRequest { SeedSource = "generate" }, CancellationToken.None));

        Assert.Equal("Another store is already using this wallet.", Assert.Single(errors).Message);

        // Rolled back to unconfigured, and the store's Lightning payment method never touched.
        Assert.Null(h.Settings.Settings.GetValueOrDefault(Store));
        Assert.DoesNotContain($"lightning:{Store}:set", h.WriteLog.Entries);
    }

    [Fact]
    public async Task A_seed_the_SDK_rejects_outright_rolls_back_and_never_logs_the_phrase()
    {
        var h = SparkSurfaceHarness.Create();
        h.Settings.FailNextSetWith = new InvalidOperationException("unknown word (word 0)");

        var errors = AssertValidationError(await h.Api.Provision(
            Store,
            new SparkProvisionRequest { SeedSource = "import", Mnemonic = SparkSurfaceHarness.ValidMnemonic },
            CancellationToken.None));

        Assert.Equal("mnemonic", Assert.Single(errors).Path);
        Assert.Null(h.Settings.Settings.GetValueOrDefault(Store));
        Assert.DoesNotContain($"lightning:{Store}:set", h.WriteLog.Entries);

        // The operator log gets the store and the exception type, never the phrase — the failing call's argument
        // was the merchant's recovery phrase, and the SDK's wording for a rejected seed is not this plugin's to
        // vouch for.
        Assert.DoesNotContain("abandon", h.ProvisionerLog.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("about", h.ProvisionerLog.AllText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Nothing_on_the_provisioning_path_logs_seed_material()
    {
        var h = SparkSurfaceHarness.Create();

        var response = AssertOk<SparkProvisionResponse>(await h.Api.Provision(
            Store, new SparkProvisionRequest { SeedSource = "generate" }, CancellationToken.None));

        var log = h.ProvisionerLog.AllText;
        Assert.NotEmpty(log);
        Assert.DoesNotContain(response.Mnemonic!, log, StringComparison.OrdinalIgnoreCase);

        // Consecutive words, never single ones. The same collision the JSON guard above was rewritten for, one
        // assertion along: this matched `" {word} "` against the operator log, and the log is English prose the
        // plugin wrote. Its success line ends "...configured from a generate seed", and "seed" is a BIP39 word,
        // so roughly one provisioning in a hundred and seventy failed a test with no leak in it.
        AssertNoSeedMaterialInLog(new Mnemonic(response.Mnemonic).Words, log);

        Assert.DoesNotContain(h.Settings.Settings[Store]!.ProtectedMnemonic!, log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Re_provisioning_replaces_the_seed_and_carries_the_sweep_configuration_across()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true);
        h.Settings.Settings[Store]!.Sweep = new SweepSettings
        {
            Enabled = true,
            BalanceThresholdSats = 400_000,
            MaxFeePercent = 1.25
        };

        var response = AssertOk<SparkProvisionResponse>(await h.Api.Provision(
            Store,
            new SparkProvisionRequest { SeedSource = "import", Mnemonic = SparkSurfaceHarness.ValidMnemonic },
            CancellationToken.None));

        Assert.Null(response.Mnemonic);
        Assert.Equal(SparkSurfaceHarness.ValidMnemonic, h.StoredMnemonic(Store));

        // A merchant changing their seed has not asked to lose their sweep configuration.
        var sweep = h.Settings.Settings[Store]!.Sweep;
        Assert.True(sweep.Enabled);
        Assert.Equal(400_000, sweep.BalanceThresholdSats);
        Assert.Equal(1.25, sweep.MaxFeePercent);

        // And the payment key is kept rather than rotated, so a Lightning configuration already written stays valid.
        Assert.Equal(SparkSurfaceHarness.VictimPaymentKey, h.Settings.Settings[Store]!.PaymentKey);
    }

    [Fact]
    public async Task Removing_an_unconfigured_store_says_so_rather_than_pretending_to_succeed()
    {
        var h = SparkSurfaceHarness.Create();

        var result = await h.Api.Remove(Store, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, objectResult.StatusCode);
        Assert.Equal("spark-not-configured", Assert.IsType<GreenfieldAPIError>(objectResult.Value).Code);
        Assert.Empty(h.Settings.Writes);
    }

    [Fact]
    public async Task Removal_leaves_another_Lightning_node_alone()
    {
        // The store experimented with Spark and then configured an LND node. Removing Spark must not discard a
        // connection string carrying macaroon material that exists nowhere else.
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true);
        h.Lightning.Add(Store, "type=lnd-rest;server=https://127.0.0.1:8080/;macaroon=abcdef");

        Assert.IsType<OkResult>(await h.Api.Remove(Store, CancellationToken.None));

        Assert.Null(h.Settings.Settings.GetValueOrDefault(Store));
        Assert.Equal(
            "type=lnd-rest;server=https://127.0.0.1:8080/;macaroon=abcdef",
            h.Lightning.Stores[Store].ConnectionString);
    }

    [Fact]
    public async Task Status_reports_an_unconfigured_store_as_unconfigured_rather_than_as_an_error()
    {
        var h = SparkSurfaceHarness.Create();

        var status = AssertOk<SparkStatusData>(await h.Api.GetStatus(Store, CancellationToken.None));

        Assert.False(status.Configured);
        Assert.False(status.WalletRunning);
        Assert.Null(status.BalanceSats);
        Assert.Null(status.IdentityPubkey);
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<T>(ok.Value);
    }

    private static List<GreenfieldValidationError> AssertValidationError(IActionResult result)
    {
        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, unprocessable.StatusCode);
        return Assert.IsType<List<GreenfieldValidationError>>(unprocessable.Value);
    }

    /// <summary>
    /// The flake this guard was rewritten to remove, pinned so it cannot come back.
    /// </summary>
    /// <remarks>
    /// A real phrase, chosen because it contains "history", which is also a property on
    /// <c>SparkSweepConfigurationData</c>. The previous guard matched <c>"history"</c> in the raw JSON, could not
    /// tell a key from a value, and so failed at random depending on what the generator happened to produce.
    /// </remarks>
    [Fact]
    public void The_seed_leak_guard_ignores_a_property_name_that_is_also_a_seed_word()
    {
        const string colliding = "cereal little razor excess large spread pill all used green history fit";
        var json = ApiJson.Serialize(new SparkSweepConfigurationData());

        // The property is really there, or this test proves nothing about the collision.
        Assert.Contains("\"history\"", json, StringComparison.OrdinalIgnoreCase);

        AssertNoSeedMaterialInValues(colliding, json);
    }

    /// <summary>
    /// And the guard still fails when the seed really is in a value — otherwise it would pass forever.
    /// </summary>
    [Theory]
    [InlineData("history")]
    [InlineData("green history")]
    [InlineData("leaked: used green history fit")]
    public void The_seed_leak_guard_fails_when_a_value_carries_seed_material(string leaked)
    {
        const string colliding = "cereal little razor excess large spread pill all used green history fit";
        var json = ApiJson.Serialize(new SparkStatusData { WalletError = leaked });

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => AssertNoSeedMaterialInValues(colliding, json));
    }

    /// <summary>
    /// Fails if any string <em>value</em> in <paramref name="json"/> carries part of <paramref name="mnemonic"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two checks, because a seed can leak in two shapes and each needs a different test. A field holding the
    /// words as separate entries — <c>["abandon", "abandon", …]</c> — is caught by comparing whole values against
    /// whole words. A field holding some of the phrase inside a longer string is caught by looking for any two
    /// <em>consecutive</em> words, which also covers the reordered and truncated leaks the caller cares about.
    /// </para>
    /// <para>
    /// Why pairs rather than single words for the substring check: a lone common word appears in ordinary prose —
    /// an error message mentioning a "network" would trip it — so a single-word substring rule would reintroduce
    /// the false positive this method exists to remove, just one layer down. Two consecutive BIP39 words in that
    /// order do not occur by accident, and no real leak of a phrase can avoid producing at least one such pair.
    /// </para>
    /// </remarks>
    private static void AssertNoSeedMaterialInValues(string mnemonic, string json)
    {
        var words = new Mnemonic(mnemonic).Words;
        // A response body is always an object here, but rooting the walk at JContainer keeps this honest if one
        // is ever an array or a bare value.
        var root = JToken.Parse(json);
        var tokens = root is JContainer container
            ? container.DescendantsAndSelf()
            : new[] { root }.AsEnumerable();
        var values = tokens
            .OfType<JValue>()
            .Where(v => v.Type is JTokenType.String)
            .Select(v => (string?)v.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .ToList();

        foreach (var value in values)
        {
            foreach (var word in words)
            {
                Assert.False(
                    string.Equals(value, word, StringComparison.OrdinalIgnoreCase),
                    $"a JSON value is exactly the seed word '{word}'");
            }

            for (var i = 0; i + 1 < words.Length; i++)
            {
                var pair = $"{words[i]} {words[i + 1]}";
                Assert.False(
                    value!.Contains(pair, StringComparison.OrdinalIgnoreCase),
                    $"a JSON value contains the consecutive seed words '{pair}'");
            }
        }
    }

    /// <summary>
    /// The log flake, pinned. Proves its own premise first, because the whole point is that the collision is
    /// real and not hypothetical.
    /// </summary>
    /// <remarks>
    /// The provisioner's success line is "Store {StoreId}: Spark configured from a {SeedSource} seed", and
    /// "seed" is in the BIP39 English wordlist. The old assertion looked for <c>" {word} "</c> in the log, so a
    /// generated phrase containing "seed" matched the log's own last word and failed a test with nothing wrong
    /// in it — about one provisioning in a hundred and seventy, which is exactly often enough to be dismissed
    /// as noise and not often enough to be found.
    /// </remarks>
    [Fact]
    public async Task The_log_seed_guard_ignores_a_seed_word_the_log_prose_itself_uses()
    {
        var h = SparkSurfaceHarness.Create();
        AssertOk<SparkProvisionResponse>(await h.Api.Provision(
            Store, new SparkProvisionRequest { SeedSource = "generate" }, CancellationToken.None));
        var log = h.ProvisionerLog.AllText;

        // Both halves of the collision, asserted rather than assumed: a future reword of that log line, or a
        // wordlist that never had "seed" in it, should retire this test rather than let it pass vacuously.
        Assert.True(
            Wordlist.English.WordExists("seed", out _),
            "'seed' is no longer a BIP39 word, so this regression test no longer pins anything.");
        Assert.Contains("seed", log, StringComparison.OrdinalIgnoreCase);

        // A phrase whose eleventh word is the one the log prose uses. The guard must not care.
        string[] colliding =
            ["cereal", "little", "razor", "excess", "large", "spread", "pill", "all", "used", "green", "seed", "fit"];

        AssertNoSeedMaterialInLog(colliding, log);
    }

    /// <summary>
    /// And it still fails when the log really does carry seed material — otherwise it would pass forever.
    /// </summary>
    [Theory]
    [InlineData("used green")]
    [InlineData("Store x: setup failed for all used green seed fit")]
    [InlineData("cereal little razor excess large spread pill all used green seed fit")]
    public void The_log_seed_guard_still_fails_when_the_log_carries_consecutive_seed_words(string leaked)
    {
        string[] colliding =
            ["cereal", "little", "razor", "excess", "large", "spread", "pill", "all", "used", "green", "seed", "fit"];

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNoSeedMaterialInLog(colliding, leaked));
    }

    /// <summary>
    /// Fails if <paramref name="log"/> carries any two <em>consecutive</em> words of <paramref name="words"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The log counterpart of <see cref="AssertNoSeedMaterialInValues"/>, and it keeps only that method's second
    /// rule. There is no key-versus-value distinction to draw here — a log is prose, all of it "value" — so the
    /// pair rule is doing all the work, and the whole-value-equals-a-word rule has no meaning against free text.
    /// </para>
    /// <para>
    /// Pairs, for the reason that method gives and one more: the plugin's log lines are written in English about
    /// wallets, networks and seeds, so single BIP39 words are not merely possible in them, they are expected.
    /// Two consecutive words of a phrase, in order, are not — and no leak of a recovery phrase, reordered or
    /// truncated, can avoid producing at least one such pair.
    /// </para>
    /// </remarks>
    private static void AssertNoSeedMaterialInLog(string[] words, string log)
    {
        for (var i = 0; i + 1 < words.Length; i++)
        {
            var pair = $"{words[i]} {words[i + 1]}";
            Assert.False(
                log.Contains(pair, StringComparison.OrdinalIgnoreCase),
                $"the operator log contains the consecutive seed words '{pair}'");
        }
    }
}
