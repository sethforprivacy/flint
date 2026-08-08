using System;

namespace BTCPayServer.Plugins.Flint;

/// <summary>
/// Compile-time constants shared across the plugin.
/// </summary>
public static class Constants
{
    /// <summary>
    /// Plugin identifier, must match the assembly name so BTCPay's plugin loader and the
    /// plugin-builder registry agree on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This was <c>BTCPayServer.Plugins.Spark</c> before the plugin became Flint. It changed because a third party had
    /// already registered that identifier on the official plugin registry (slug <c>spark</c>, from
    /// <c>github.com/p-i-g-g-y/btcpay-spark</c>), and BTCPay joins an installed plugin to a registry
    /// entry on identifier alone: it took the author, source and documentation links shown for this
    /// plugin from theirs, and would have offered their build as an "update" the moment their version
    /// passed ours. It also meant this plugin could never be listed under its own name.
    /// </para>
    /// <para>
    /// <b>Nothing else in this file moved with it, and nothing else may.</b> Five constants below
    /// contain "Spark" or this exact string and are load-bearing for data that already exists:
    /// <see cref="DatabaseSchema"/>, <see cref="StoreSettingsKey"/>, <see cref="WorkDirName"/>,
    /// <see cref="ConnectionStringType"/>, and above all <see cref="DataProtectionPurpose"/>, which is
    /// the purpose string every stored mnemonic is encrypted under — changing that one makes every
    /// merchant's recovery phrase permanently undecryptable. The XOR mask for the obfuscated Breez key
    /// is a sixth. They are separate constants that happen to share a string, which is exactly what
    /// makes a repository-wide find-and-replace of the old identifier destructive.
    /// <c>SparkBrandingTests</c> pins every one of them.
    /// </para>
    /// <para>
    /// Because BTCPay keys an install by this string, upgrading across it is an uninstall of the old
    /// plugin and an install of the new one. Store settings, the Postgres schema and the SDK storage
    /// directory all survive, because each is keyed by one of the constants that did not change.
    /// </para>
    /// </remarks>
    public const string PluginIdentifier = "BTCPayServer.Plugins.Flint";

    /// <summary>
    /// Oldest BTCPay Server version this plugin declares support for. Becomes the plugin's
    /// <c>PluginDependency</c> condition, and BTCPay refuses to load the plugin on anything older.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is deliberately not the same as <see cref="BuiltAgainstBTCPayServerVersion"/>, and it must not be
    /// bumped in lockstep with it.</b> The plugin is compiled against the newest pinned submodule tag but declares
    /// support for the oldest release it actually works on — "build against newer, declare support for older".
    /// Raising the floor is a <em>support decision</em>, not a consequence of a submodule bump: it drops every host
    /// below the new value. <c>.github/workflows/btcpayserver-update.yml</c> therefore bumps the submodule and
    /// <see cref="BuiltAgainstBTCPayServerVersion"/> only, and leaves this constant alone on purpose.
    /// </para>
    /// <para>
    /// <b>Lowering it is a different and much more dangerous operation, and "it compiles and the suite is green"
    /// is not evidence for it.</b> This floor was once dropped to 2.4.0 on exactly that evidence and the plugin
    /// broke on every 2.4.0 host. Five views use <c>&lt;vc:title-header /&gt;</c>; <c>TitleHeader</c> was added in
    /// v2.4.1 (upstream <c>7d206d935</c>, "Rename &lt;vc:breadcrumb&gt;"), so on 2.4.0 it did not exist. A Razor
    /// view component is resolved <em>by name, at render time</em>: the C# compiler emits a string, the unit suite
    /// never renders a view, and so nothing between the edit and the merchant's browser could see it. The first
    /// request to any Spark page threw <c>InvalidOperationException: A view component named 'TitleHeader' could not
    /// be found</c>, BTCPay auto-disabled the plugin and restarted, and every plugin route — MVC and Greenfield —
    /// 404'd until an operator re-enabled it by hand.
    /// </para>
    /// <para>
    /// So: lower this only with proof of <em>runtime</em> compatibility against the candidate floor — at minimum a
    /// real BTCPay of that version with the packaged plugin installed and every view actually rendered, plus a
    /// pass over anything else resolved by name rather than by the compiler (partials, tag helpers, injected
    /// types, route names). <c>ViewComponentCompatibilityTests</c> mechanises the view-component half of that
    /// check against the pinned submodule; it cannot check a version the submodule is not pinned to.
    /// </para>
    /// </remarks>
    public const string MinBTCPayServerVersion = "2.4.1";

    /// <summary>
    /// The BTCPay Server release this plugin is compiled against. Must always equal the <c>btcpayserver</c>
    /// submodule tag; the update workflow keeps the two in step.
    /// </summary>
    /// <remarks>
    /// Informational — nothing at runtime reads it. It exists so the update automation has a version to bump that
    /// is not the support floor, and so a reader can see at a glance which release the assembly was built against.
    /// It currently equals <see cref="MinBTCPayServerVersion"/>, which is the safe state, not a redundancy: the two
    /// stay separate constants so a submodule bump can move this one without silently moving the floor.
    /// </remarks>
    public const string BuiltAgainstBTCPayServerVersion = "2.4.2";

    /// <summary>
    /// The <c>type=</c> discriminator of our Lightning connection string.
    /// </summary>
    /// <remarks>
    /// Was <c>breezspark</c>, which named the backing SDK specifically to leave no room for a
    /// collision with another plugin. <c>flint</c> is a more generic word and reopens that risk in
    /// principle; it was taken knowingly, after checking that nothing claims it — not BTCPay's six
    /// built-in handlers (<c>clightning</c>, <c>eclair</c>, <c>phoenixd</c>, <c>lnd-rest</c>,
    /// <c>lnd-grpc</c>, <c>lndhub</c>), not the known plugin handlers (<c>breez</c>, <c>micro</c>,
    /// <c>app</c>), and nothing in public GitHub code search. A collision here is silent and would
    /// misroute a merchant's connection string, so if another plugin ever claims <c>flint</c>, the fix
    /// is to move back to a vendor-specific discriminator rather than to race them for it.
    /// </remarks>
    public const string ConnectionStringType = "flint";

    /// <summary>
    /// Key under which per-store settings are persisted via <c>StoreRepository.UpdateSetting</c>.
    /// </summary>
    public const string StoreSettingsKey = "Flint";

    /// <summary>
    /// Postgres schema and EF migrations-history table name for the plugin's DbContext.
    /// </summary>
    /// <remarks>
    /// Changed once, deliberately, when the plugin became Flint, together with the four constants
    /// around it. That was not free and was not a rename: it orphaned the old schema's tables, so the
    /// payment, payout and sweep history recorded under the previous name is not visible to this
    /// version. Operators were told to re-import their recovery phrase, which restores the wallet
    /// from the network but not the local history. Stable from here: changing it again orphans
    /// everything a second time, and there is no reason left to.
    /// </remarks>
    public const string DatabaseSchema = "BTCPayServer.Plugins.Flint";

    /// <summary>
    /// Sub-directory of the BTCPay data directory holding per-store SDK storage
    /// (<c>&lt;DataDir&gt;/Plugins/Spark/&lt;storeId&gt;</c>).
    /// </summary>
    public const string WorkDirName = "Flint";

    /// <summary>
    /// <c>IDataProtector</c> purpose string for the store mnemonic.
    /// </summary>
    /// <remarks>
    /// <b>The most dangerous constant in this file.</b> Changing it does not orphan data that could be
    /// migrated later — it makes every stored seed permanently undecryptable, recoverable only from the
    /// merchant's own backup of the phrase. It was changed exactly once, when the plugin became Flint,
    /// as a deliberate break with a documented re-import procedure and after confirming the phrases
    /// were backed up. Do not change it again. If it ever must move, the migration is to decrypt with
    /// the old purpose and re-encrypt with the new one on startup, never a bare edit.
    /// </remarks>
    public const string DataProtectionPurpose = "BTCPayServer.Plugins.Flint.Mnemonic";

    /// <summary>
    /// Maximum size of a BOLT11 description, in <b>bytes</b> not characters. The Lightspark service
    /// provider rejects anything longer with "Description is too long: Max length 639 bytes", and only
    /// after a network round trip, so descriptions are truncated locally.
    /// </summary>
    public const int MaxBolt11DescriptionBytes = 639;

    /// <summary>
    /// Smallest amount a cooperative exit will send, in satoshi, for a P2WPKH destination.
    /// </summary>
    /// <remarks>
    /// The SDK rejects anything below this locally, with "Amount is below the minimum of 294 sats required
    /// for this address". The message says <em>for this address</em>: the floor is script-type dependent, and
    /// a Taproot address reserved from the store's wallet may differ. The check is free (client-side, 0 ms),
    /// so the sweep path should probe per destination rather than trust this constant. Kept here as the known
    /// lower bound for validation messages.
    /// </remarks>
    public const long MinimumOnchainSendSats = 294;

    /// <summary>
    /// A representative cooperative-exit fee, in satoshi, used only to warn a merchant about a configuration
    /// that cannot work.
    /// </summary>
    /// <remarks>
    /// The worst tier measured in the funded regtest run: <c>userFeeSat</c> 750 plus <c>l1BroadcastFeeSat</c>
    /// 1,680 for the fast tier. <b>Never used to decide anything</b> — every real decision uses the live quote,
    /// because this number is a regtest observation of a chain at 1 sat/vB and mainnet will differ. It exists so
    /// the settings page can say "a reserve this small will not cover the fee" before the merchant finds out from
    /// a refused sweep.
    /// </remarks>
    public const long IndicativeCoopExitFeeSats = 2_430;

    /// <summary>Rows per page on the sweep history table.</summary>
    public const int SweepHistoryPageSize = 25;

    /// <summary>
    /// Default ceiling on a Lightning send fee, as a percentage of the amount, when the caller sets none.
    /// </summary>
    /// <remarks>
    /// BTCPay's automated payout processor calls <c>Pay</c> with a <c>PayInvoiceParams</c> that carries only an
    /// amount, so without a default there would be no protection at all against a pathological route on an
    /// automated payout. Observed Lightning send fees were 3 sat on 500 and 3–4 sat on 1 000 (under 1%), so
    /// this is deliberately generous: it exists to catch the absurd, not to tune economics.
    /// </remarks>
    public const double DefaultMaxFeePercent = 3.0;

    /// <summary>
    /// Floor under <see cref="DefaultMaxFeePercent"/>, in satoshi, so small payments are not blocked by
    /// percentage rounding.
    /// </summary>
    public const long DefaultMaxFeeFloorSats = 25;

    /// <summary>
    /// Deadline applied to a single SDK call made from a background loop.
    /// </summary>
    /// <remarks>
    /// No SDK method takes a <c>CancellationToken</c> and none can be cancelled, so a hung service-provider
    /// request would otherwise stall the loop that is awaiting it indefinitely. The deadline abandons the
    /// <em>wait</em>, not the call — the call keeps running — which is enough to keep a queue moving.
    /// </remarks>
    public static readonly TimeSpan SdkCallDeadline = TimeSpan.FromSeconds(30);

    /// <summary>How often <c>SparkReconciliationTask</c> runs, measured from the end of the previous pass.</summary>
    public static readonly TimeSpan ReconciliationInterval = TimeSpan.FromMinutes(1);

    /// <summary>How often <c>SweepTask</c> runs, measured from the end of the previous pass.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Wall clock a single reconciliation pass may spend before it stops starting new stores.
    /// </summary>
    /// <remarks>
    /// See <c>SparkStorePassScheduler</c> for why a pass needs a bound at all. Half the interval, so that in the
    /// worst case — the budget spent, plus the one store still running under
    /// <see cref="ReconciliationStoreDeadline"/> — a pass costs one full interval and the task's duty cycle on
    /// one of BTCPay's three shared scheduled-task workers is capped at 50%. In the ordinary case, where a store
    /// costs about a second, a pass finishes long inside the budget and the duty cycle is a fraction of a
    /// percent. Nothing is dropped when the budget runs out: the next pass resumes at the next store.
    /// </remarks>
    public static readonly TimeSpan ReconciliationPassBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Wall clock one store's reconciliation may take before the pass stops waiting on it.
    /// </summary>
    /// <remarks>
    /// Generous next to the per-call <see cref="SdkCallDeadline"/> that already bounds each SDK read inside a
    /// store's walk, because a legitimate walk over a backlog makes many of them. It exists for the case those
    /// per-call deadlines cannot bound: a store with enough pending invoices to spend hours inside one pass.
    /// </remarks>
    public static readonly TimeSpan ReconciliationStoreDeadline = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Wall clock a single sweep pass may spend before it stops starting new stores.
    /// </summary>
    /// <remarks>
    /// Half the sweep interval, for the reason given on <see cref="ReconciliationPassBudget"/>.
    /// </remarks>
    public static readonly TimeSpan SweepPassBudget = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Wall clock one store's sweep pass may take before the pass stops waiting on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only bound on a sweep pass, and unlike reconciliation there is nothing underneath it:
    /// <c>SparkSweepEngine</c> makes no deadline-bounded SDK call, so without this one wallet whose
    /// <c>SyncWallet</c> never returns holds one of BTCPay's three shared scheduled-task workers for the life of
    /// the process.
    /// </para>
    /// <para>
    /// Larger than <see cref="SdkCallDeadline"/> because a real sweep is several SDK calls end to end — a sync,
    /// an info read, a recovery lookup, a pre-flight quote, and the send itself — and abandoning the wait on a
    /// sweep that is merely slow buys nothing: the engine's own single-flight guard means the pass that stopped
    /// waiting cannot be replaced by another one anyway. Abandoning it is safe rather than merely tolerable,
    /// because a sweep's crash-safety primitive is a persisted idempotency key and not the caller's attention.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan SweepStoreDeadline = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Log filter for the SDK's internal Rust logging, in <c>env_logger</c> syntax.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Breez's "moving to production" checklist asks for <c>debug</c>. That is deliberately not the
    /// default here: it is very chatty, and these lines are forwarded into the operator's BTCPay log
    /// where they compete with everything else. Raise it to <c>debug</c> when diagnosing a payment problem.
    /// </para>
    /// <para>
    /// <b>Raising it to <c>trace</c> is a different matter, and <c>SparkLogging</c> refuses to.</b> What the
    /// SDK emits at each level was read line by line against a throwaway regtest wallet. At <c>info</c> and
    /// <c>debug</c> nothing secret appears — migration DDL, gRPC request structs carrying the wallet's public
    /// identity key, TLS handshake details, sync summaries. At <c>trace</c> the service provider's GraphQL
    /// <c>session_token</c> is logged in full inside raw response bodies, and that is a live bearer credential
    /// for the merchant's wallet, written to <c>&lt;DataDir&gt;/Plugins/Spark/logs/sdk.log</c> by the Rust side
    /// where no C# scrubbing can reach it.
    /// </para>
    /// <para>
    /// One gap in that audit, stated rather than papered over: the probe wallet was unfunded, so the lines a
    /// completed payment produces — the ones that could carry a preimage — were never emitted and are
    /// unaudited. <c>SparkLogScrubber</c> redacts by name against exactly that gap.
    /// </para>
    /// </remarks>
    public const string SdkLogFilter = "info";

    /// <summary>
    /// Breez SDK API key baked into the plugin, stored obfuscated rather than as a plain literal.
    /// <para>
    /// Breez API keys are <b>per-application</b> identifiers, not per-user credentials — the same
    /// model a first-party mobile app uses. Shipping one plugin-wide key keeps merchant setup friction at zero
    /// and matches the Boltz <c>referralId="btcpay"</c> precedent. A merchant may still supply
    /// their own via <c>SparkSettings.ApiKeyOverride</c>.
    /// </para>
    /// <para>
    /// <b>The obfuscation is not encryption and is not security.</b> The mask is in this repository
    /// and in <c>scripts/obfuscate-api-key.py</c>; recovering the key takes a minute. Its only
    /// purpose is that the key is not a plain string in a public repository, so it is not picked up
    /// by GitHub code search, automated secret scanners, or <c>strings</c> over the shipped
    /// assembly. It deters casual reuse; it defeats nobody who is trying.
    /// </para>
    /// <para>
    /// It is stored this way rather than injected at build time, the approach an app that builds its own releases would take, because
    /// the BTCPay plugin registry builds plugins <em>from source</em>: it clones the repository and
    /// runs <c>dotnet publish</c>. A CI-only secret would leave every registry-installed copy with
    /// no key at all.
    /// </para>
    /// </summary>
    public static string BreezApiKey { get; } = Deobfuscate(ObfuscatedBreezApiKey);

    /// <summary>
    /// The obfuscated form. Regenerate with <c>scripts/obfuscate-api-key.py</c> if the key changes.
    /// </summary>
    private const string ObfuscatedBreezApiKey =
        "Dx0KEgcDECYzIiIVbyclNyYOJzt+dSUHGDAnBCwqID8RAj8EPypvJykxIiYjMlkBLVg7MQMsDhYwFxkJKC4KBWY4DzsqAzcEYQItWSMgB2QNFCQBBA0ROCgIdycjMSYRIydrdiItKwwVPgIoLCsCEjcRPDZ4AT0+Ih4aN3cRGAUnMiY8IRcZFTchNywoME0XLUQyLC8LYxc5WzhEIxcBFwNKGgInPi8CSj0qHwI9LwJjBzkuLw02OCARLBESKzEyXBFYNiUxEAoBGmoNJzAKIHsWNj4pNQBXXTkQOVQGVDs0WT0JXxBdWgFABR0OGSY/HiRGMSRDezQoAiIrQQR/Ay0eJzItEAIdIx4dMzokKDBvNlQwJgMvMmMEXC4vRRcwBzcwLhEnJhcqE34lNBgTJSonZBAaQkUiGxYqARNANC0xIiQUbDciIy86IzZpAi0+CTYWMTIkACoFEz04UEF9AyoDHyQaGm0/DRAHLSMuAjwjHh0zOiQgN2Y6LRYAOx4JdB4+BiVGCGQaYyMRNjAwHDwlWjwIRyEaDDR4diAEIAIgAAIWIx4eFyguJDZ/AS4NVw8sOwENPSEkODMBMGERGGsmRDMOHXY3WloBX0E4SAUuECccCQ0hCSxIASZZQVMLY2IDDCwPIwYZBQsFRUQ1IC8ELi0fMhcMHCBCFRU5HlEKFHsD";

    /// <summary>XOR mask for <see cref="ObfuscatedBreezApiKey"/>. Deliberately not a secret.</summary>
    private const string ApiKeyMask = "BTCPayServer.Plugins.Flint";

    private static string Deobfuscate(string encoded)
    {
        var blob = Convert.FromBase64String(encoded);
        var mask = System.Text.Encoding.UTF8.GetBytes(ApiKeyMask);
        var outBytes = new byte[blob.Length];
        for (var i = 0; i < blob.Length; i++)
            outBytes[i] = (byte)(blob[i] ^ mask[i % mask.Length]);
        return System.Text.Encoding.UTF8.GetString(outBytes);
    }
}
