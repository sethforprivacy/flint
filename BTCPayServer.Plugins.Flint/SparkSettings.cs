using System;
using BTCPayServer.Plugins.Flint.Sdk;

namespace BTCPayServer.Plugins.Flint;

/// <summary>
/// Per-store plugin settings, persisted through
/// <c>StoreRepository.UpdateSetting&lt;SparkSettings&gt;(storeId, Constants.StoreSettingsKey, …)</c>.
/// </summary>
/// <remarks>
/// The mnemonic is never stored in plaintext: <c>SparkMnemonicProtector</c> wraps it with
/// <c>IDataProtector</c> before it reaches <see cref="ProtectedMnemonic"/>, and only <c>SparkService</c>
/// unwraps it. It must never be echoed back into a view model or HTML.
/// </remarks>
public class SparkSettings
{
    /// <summary>
    /// <c>IDataProtector</c>-protected BIP39 mnemonic for this store's Spark wallet.
    /// </summary>
    public string? ProtectedMnemonic { get; set; }

    /// <summary>
    /// Where the mnemonic came from. Surfaced in the UI so the merchant understands the
    /// security posture: with <see cref="SeedSource.HotWallet"/> one compromise drains both the Spark
    /// balance and the store's on-chain wallet, and the Spark identity pubkey publicly links the two.
    /// </summary>
    public SeedSource SeedSource { get; set; } = SeedSource.Generated;

    /// <summary>
    /// Random secret embedded in the store's Lightning connection string. The handler checks that
    /// this value matches the settings of the store named in the same connection string, which is
    /// what makes the credential non-transferable between stores.
    /// </summary>
    public string? PaymentKey { get; set; }

    /// <summary>
    /// Optional per-merchant Breez API key. When null the plugin-wide
    /// <see cref="Constants.BreezApiKey"/> is used.
    /// </summary>
    public string? ApiKeyOverride { get; set; }

    /// <summary>
    /// Auto-sweep configuration. Off until the merchant opts in on the sweep settings page.
    /// </summary>
    /// <remarks>
    /// <b>The initialiser is not a guarantee.</b> It covers a stored blob with no <c>Sweep</c> key, but a blob
    /// containing an explicit <c>"Sweep": null</c> — a hand edit, a restored backup, an older serializer —
    /// deserialises to null and the compiler cannot see it. Dereferencing it unguarded threw a
    /// <c>NullReferenceException</c> out of a scheduler pass, so every reader coalesces. The type is left
    /// non-nullable because null is a corrupt blob rather than a meaningful state, and making it nullable would
    /// push a question onto every call site that only has one right answer.
    /// </remarks>
    public SweepSettings Sweep { get; set; } = new();

    /// <summary>
    /// How on-chain deposits to the wallet's static address are claimed. Coalesce before use, as with
    /// <see cref="Sweep"/>.
    /// </summary>
    public SparkDepositSettings Deposits { get; set; } = new();

    /// <summary>
    /// Stable Balance (USDB) configuration. Off until the merchant opts in. Coalesce before use, as with
    /// <see cref="Sweep"/>.
    /// </summary>
    public StableBalanceSettings StableBalance { get; set; } = new();

    /// <summary>
    /// Experimental unilateral-exit configuration. Inert on any host that has not set
    /// <c>FLINT_EXPERIMENTAL_UNILATERAL_EXIT</c> (<see cref="Constants.UnilateralExitEnabled"/>). Coalesce
    /// before use, as with <see cref="Sweep"/>.
    /// </summary>
    /// <remarks>
    /// Present on every settings blob written from this version on, whether or not the host has the gate set,
    /// because the alternative — writing the section only when the feature is enabled — would mean a store's
    /// acknowledgement silently disappearing from the blob the first time an operator saved settings with the
    /// gate off. The section existing is not the feature being available; see
    /// <see cref="UnilateralExitSettings"/>.
    /// </remarks>
    public UnilateralExitSettings UnilateralExit { get; set; } = new();

    /// <summary>
    /// An independent copy, nested settings included. Every property added to this class must be added here too.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what stops the settings cache being an aliasing hazard.</b> <c>SparkService</c> keeps one of
    /// these per store and hands it to every reader; before this existed it handed out the cached instance
    /// itself, so a caller that applied an edit and then failed to persist it left the cache — and therefore
    /// the sweep engine, which reads through the same cache — acting on a configuration that was never stored.
    /// The service now clones on the way out and on the way in, so the only object anyone can mutate is their
    /// own.
    /// </para>
    /// <para>
    /// Deep for the four nested objects, because a shallow copy would defeat the whole point: the edits that
    /// matter all land on <see cref="Sweep"/>, <see cref="Deposits"/>, <see cref="StableBalance"/> or
    /// <see cref="UnilateralExit"/> rather than on the scalars here. Each is coalesced, because an explicit
    /// <c>null</c> in a stored blob defeats the property initialiser — the same hazard every reader of these
    /// four has to handle.
    /// </para>
    /// </remarks>
    public SparkSettings Clone() => new()
    {
        ProtectedMnemonic = ProtectedMnemonic,
        SeedSource = SeedSource,
        PaymentKey = PaymentKey,
        ApiKeyOverride = ApiKeyOverride,
        Sweep = (Sweep ?? new SweepSettings()).Clone(),
        Deposits = (Deposits ?? new SparkDepositSettings()).Clone(),
        StableBalance = (StableBalance ?? new StableBalanceSettings()).Clone(),
        UnilateralExit = (UnilateralExit ?? new UnilateralExitSettings()).Clone()
    };
}

/// <summary>
/// The claim-fee policy for on-chain deposits into the store's Spark wallet.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> The SDK's default <c>maxDepositClaimFee</c> is <c>Rate(1 sat/vB)</c> — on
/// <em>both</em> networks — and it is a <b>cap, not a bid</b>. When the required rate exceeds it the claim does
/// not happen more cheaply; it does not happen, the deposit sits unclaimed indefinitely, and the only trace is a
/// row in <c>ListUnclaimedDeposits</c>. To a merchant that is indistinguishable from money vanishing.
/// </para>
/// <para>
/// The spike observed auto-claim working under that default — but it was measured on <b>regtest</b>, where
/// the mempool is empty. On mainnet 1 sat/vB is below the floor essentially always: the spike sampled a market
/// at an unusually cheap <c>fastestFee=3, economyFee=2, minimumFee=1</c> and even there the default is under the
/// half-hour rate. Breez's own documentation concedes the default "requires manual claiming when fees exceed
/// this threshold".
/// </para>
/// <para>
/// <b>The policy chosen, and why.</b> <c>MaxFee.NetworkRecommended(leeway)</c>, never
/// <c>Rate</c> and never <c>null</c>:
/// </para>
/// <list type="bullet">
/// <item><description><c>Rate(n)</c> is a number picked today against a market that moves. Any value low enough
/// to be prudent now strands deposits in the next fee spike, and any value high enough to survive one is not a
/// cap in a quiet market.</description></item>
/// <item><description><c>NetworkRecommended(leeway)</c> is the only variant that tracks the mempool, and it
/// exists precisely for this. The leeway is headroom over the recommendation, so a claim still succeeds when
/// rates move between the estimate and the broadcast.</description></item>
/// <item><description><c>null</c> is not a default — it <b>disables automatic claiming entirely</b>, which is
/// the failure this setting exists to prevent. <see cref="ToMaxFee"/> can never return one.</description></item>
/// </list>
/// <para>
/// A rate cap alone is still not enough, because the fee is paid out of a deposit whose size the cap knows
/// nothing about: at 200 sat/vB a perfectly reasonable rate policy would spend more claiming a 5,000-sat deposit
/// than the deposit is worth. That guard cannot be expressed in the SDK's config, so it lives on the manual
/// claim path (<see cref="MaxManualClaimFeeSats"/> and <see cref="HardMaxClaimFeePercent"/>) and in the
/// dashboard that surfaces what was stranded.
/// </para>
/// <para>
/// <b>Say plainly what that leaves uncovered.</b> Automatic claiming is done by the SDK's own background
/// worker, with no per-deposit hook and no callback the plugin could refuse from; the whole of the plugin's
/// influence over it is the single <c>maxDepositClaimFee</c> handed over at connect, and the SDK's
/// <c>MaxFee</c> has exactly three shapes — a flat satoshi cap, a flat rate, and the recommendation plus a
/// leeway — none of which can be relative to the amount being claimed. So an <em>automatic</em> claim is
/// bounded by rate alone, and a small deposit maturing in a fee spike can still lose a large share of itself.
/// The bound that does exist is <see cref="MaxClaimFeeLeewaySatPerVbyte"/>, which is why
/// <see cref="EffectiveClaimFeeLeewaySatPerVbyte"/> clamps to it unconditionally. Closing the gap properly
/// would mean taking automatic claiming away from the SDK — configuring a ceiling it can never meet and
/// claiming every matured deposit from the plugin instead, through the same
/// <c>SparkDepositService.Guard</c> — which is a real design but not one to ship without a mainnet run behind
/// it, since a mistake there strands every deposit rather than merely overpaying on one.
/// </para>
/// <para>
/// <b>Neither property here has a write path, and that is deliberate.</b> Nothing binds them from the MVC
/// forms or from Greenfield; like <c>ApiKeyOverride</c> they are operator-level overrides, reachable only by
/// editing the store's settings blob. The defaults are the policy, both surfaces show what is in force
/// (deposit page and <c>GET .../spark/deposits</c>), and adding a form would put a fee ceiling that moves
/// money in front of every merchant who does not need one. The consequence is that <b>every value here has to
/// be safe against a blob nobody validated</b> — hence the clamping on the effective properties rather than at
/// an input boundary that does not exist.
/// </para>
/// </remarks>
public class SparkDepositSettings
{
    /// <summary>
    /// Default headroom over the network-recommended fee rate, in sat/vB.
    /// </summary>
    /// <remarks>
    /// 2 sat/vB. The spike's live sample spanned <c>minimumFee=1</c> to <c>fastestFee=3</c>, so two is roughly a
    /// doubling of a cheap market's whole range — enough that a claim prepared against one estimate still
    /// broadcasts against the next, and small enough that it is not a blank cheque. The SDK's recommendation is
    /// the moving part; this is only the margin around it.
    /// </remarks>
    public const long DefaultClaimFeeLeewaySatPerVbyte = 2;

    /// <summary>
    /// Default ceiling on a manual claim, in satoshi.
    /// </summary>
    /// <remarks>
    /// 25,000 sats. A P2TR static-deposit claim is on the order of 150–450 sats at 3 sat/vB, so this clears a
    /// ~50× fee spike before it bites. It is not a tuned economic figure and is not meant to be: it is the line
    /// past which "claim this deposit" should be a decision somebody makes deliberately rather than a button
    /// press.
    /// </remarks>
    public const long DefaultMaxManualClaimFeeSats = 25_000;

    /// <summary>
    /// The share of a deposit that no configuration may authorise spending on claiming it.
    /// </summary>
    /// <remarks>
    /// A backstop, not a default, mirroring <see cref="SweepSettings.HardMaxFeePercent"/>. Whatever
    /// <see cref="MaxManualClaimFeeSats"/> says, a claim that costs more than half the deposit is refused —
    /// there is no configuration in which spending most of a deposit to receive the rest is what the merchant
    /// meant. 50% is deliberately absurd; it is a line, not a policy.
    /// </remarks>
    public const double HardMaxClaimFeePercent = 50.0;

    /// <summary>Largest leeway any configuration may put into force, in sat/vB.</summary>
    /// <remarks>
    /// <para>
    /// 100 sat/vB over the recommendation is already far outside any market this plugin will meet. The cap
    /// exists so a mistyped or corrupted figure cannot turn the rate ceiling into no ceiling.
    /// </para>
    /// <para>
    /// <b>Applied in <see cref="EffectiveClaimFeeLeewaySatPerVbyte"/>, not at a form boundary.</b> It used to be
    /// described as the largest leeway "the settings form" would store — but there is no deposit-settings form
    /// and no Greenfield write path either (see the class remarks), so the only way this value ever changes is a
    /// hand-edited settings blob, which is precisely the route a form check would not cover. A ceiling enforced
    /// only where nothing writes is not a ceiling.
    /// </para>
    /// </remarks>
    public const long MaxClaimFeeLeewaySatPerVbyte = 100;

    /// <summary>
    /// Headroom over the network-recommended rate for automatic claims, in sat/vB. Read
    /// <see cref="EffectiveClaimFeeLeewaySatPerVbyte"/> rather than this property.
    /// </summary>
    public long ClaimFeeLeewaySatPerVbyte { get; set; } = DefaultClaimFeeLeewaySatPerVbyte;

    /// <summary>
    /// The leeway actually in force: at least the default, never more than
    /// <see cref="MaxClaimFeeLeewaySatPerVbyte"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zero is treated as unset rather than as "no headroom", for the same migration reason
    /// <see cref="SweepSettings.EffectiveBalanceThresholdSats"/> exists: every settings blob written before this
    /// wave has no <c>Deposits</c> key at all, and the ones written by a future partial update could carry an
    /// explicit zero, which wins over a property initialiser on deserialize. A store reading zero would be back
    /// on a bare recommendation with no margin — the condition that strands deposits.
    /// </para>
    /// <para>
    /// The upper clamp matters more than it looks. This is the <em>only</em> bound on what an unattended claim
    /// may spend — the 50%-of-deposit backstop is on the manual path and the SDK's configuration cannot express
    /// it — so an unclamped blob carrying a leeway of 100,000 would hand the SDK a rate ceiling three orders of
    /// magnitude above any real market and let a fee spike consume a small deposit whole. Clamped here rather
    /// than at any write path, because there is no write path.
    /// </para>
    /// </remarks>
    public long EffectiveClaimFeeLeewaySatPerVbyte =>
        ClaimFeeLeewaySatPerVbyte > 0
            ? Math.Min(ClaimFeeLeewaySatPerVbyte, MaxClaimFeeLeewaySatPerVbyte)
            : DefaultClaimFeeLeewaySatPerVbyte;

    /// <summary>Ceiling on a manual claim, in satoshi.</summary>
    public long MaxManualClaimFeeSats { get; set; } = DefaultMaxManualClaimFeeSats;

    /// <summary>The ceiling actually in force. Zero or less is treated as unset.</summary>
    public long EffectiveMaxManualClaimFeeSats =>
        MaxManualClaimFeeSats > 0 ? MaxManualClaimFeeSats : DefaultMaxManualClaimFeeSats;

    /// <summary>
    /// The SDK configuration this policy produces. <b>Never null</b>, because null disables auto-claim.
    /// </summary>
    public SparkMaxFee ToMaxFee() =>
        new SparkMaxFee.NetworkRecommended(EffectiveClaimFeeLeewaySatPerVbyte);

    /// <summary>An independent copy. Every property added to this class must be added here too.</summary>
    public SparkDepositSettings Clone() => new()
    {
        ClaimFeeLeewaySatPerVbyte = ClaimFeeLeewaySatPerVbyte,
        MaxManualClaimFeeSats = MaxManualClaimFeeSats
    };
}

/// <summary>
/// Stable Balance: holding the store's Spark balance in a USD stablecoin between sweeps.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off by default, and a real trust decision rather than a preference.</b> USDB is issued by a regulated US
/// stablecoin issuer and its token metadata reports <c>isFreezable: true</c> — the issuer can freeze the
/// balance. Turning this on adds that counterparty on top of the Spark statechain operators the merchant is
/// already trusting, so <see cref="DisclosureAcknowledged"/> gates activation on the merchant
/// having been shown it.
/// </para>
/// <para>
/// <b>It is not a prerequisite for anything.</b> BTC → USDT cross-chain is a single Orchestra conversion;
/// BTC → USDB → USDT is two, with two spreads. Stable Balance earns its place only when the merchant wants to
/// <em>hold</em> dollars between sweeps, and the settings page says so.
/// </para>
/// <para>
/// <b>Mainnet only.</b> The SDK accepts a stable-balance configuration on regtest and it simply never converts,
/// because USDB does not exist there — which is worse than a refusal, so the plugin does not configure it off
/// mainnet at all.
/// </para>
/// <para>
/// <b>Turning this on changes the unit of a cross-chain sweep amount</b>, from satoshi to token base units, and
/// removes the sweep engine's idempotency-key crash-safety primitive because the send then has a token first
/// leg. Neither is a setting; both are consequences. <see cref="Sdk.SparkSendAmount"/> is what keeps the first
/// from being a bug, and <c>SweepRecord.ProviderQuoteId</c> is what replaces the second.
/// </para>
/// </remarks>
public class StableBalanceSettings
{
    /// <summary>
    /// The mainnet USDB token identifier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The SDK ships no default for this and cannot supply one</b> — <c>StableBalanceConfig.tokens</c> is
    /// entirely application-supplied, and <c>FetchConversionLimits</c> rejects a null identifier outright. So the
    /// plugin has to carry it.
    /// </para>
    /// <para>
    /// Recovered live from Orchestra's route table during the spike and corroborated by Breez's own published
    /// sample: ticker <c>USDB</c>, name "Bitcoin USD", 6 decimals, issued by Brale, <b>freezable</b>.
    /// </para>
    /// <para>
    /// A default rather than a constant, deliberately. It is a piece of mainnet state hard-coded into a
    /// distributed binary, and if it ever changes an operator must be able to correct it without waiting for a
    /// plugin release. <c>SparkStableBalanceService</c> verifies whatever is configured against the SDK before
    /// activating.
    /// </para>
    /// </remarks>
    public const string DefaultTokenIdentifier =
        "btkn1xgrvjwey5ngcagvap2dzzvsy4uk8ua9x69k82dwvt5e7ef9drm9qztux87";

    /// <summary>
    /// The label the plugin gives its configured token.
    /// </summary>
    /// <remarks>
    /// An integrator-defined display string with <b>no protocol meaning</b> — it is not a canonical ticker and
    /// the SDK does not interpret it. It is nevertheless the value <c>UpdateUserSettings</c> activates by, so it
    /// has to be stable and it has to match the token list the wallet was configured with.
    /// </remarks>
    public const string DefaultLabel = "USDB";

    /// <summary>USDB's decimals. Read from token metadata where available; this is the documented value.</summary>
    public const uint DefaultDecimals = 6;

    /// <summary>
    /// Default slippage tolerance for a BTC↔token conversion, in basis points.
    /// </summary>
    /// <remarks>
    /// 10 bps, which is also what the SDK falls back to. <b>Not to be confused with the cross-chain slippage
    /// default</b>, a different field on a different config whose fallback is 100 bps — 10× looser. The two are
    /// deliberately kept as separate settings here so neither inherits the other's number.
    /// </remarks>
    public const uint DefaultMaxSlippageBps = 10;

    /// <summary>Largest slippage the SDK accepts on this field, in basis points.</summary>
    public const uint MaxSlippageBpsLimit = 10_000;

    /// <summary>
    /// The floor under a BTC→token conversion, in satoshi, as measured live.
    /// </summary>
    /// <remarks>
    /// Informational only. The service's own minimum is authoritative and is fetched at runtime; this exists so
    /// the settings page can warn about a threshold that can never convert before the merchant finds out by it
    /// never converting. The SDK clamps a configured threshold <em>upward</em> to this floor, so setting less
    /// does not do what it looks like it does.
    /// </remarks>
    public const long IndicativeMinimumConversionSats = 800;

    /// <summary>
    /// Whether the merchant wants stable balance active.
    /// </summary>
    /// <remarks>
    /// The <em>desired</em> state. The authority on the actual state is the wallet's own user settings, read
    /// through <c>GetUserSettings</c>, and the two can legitimately disagree — the SDK caches the active label
    /// per wallet, so a restored wallet or a new storage directory starts deactivated whatever this says.
    /// <c>SparkStableBalanceService</c> reports both rather than quietly reconciling them, because reconciling
    /// means converting a merchant's balance without them asking.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// The merchant has been shown the freezable-issuer disclosure. Activation is refused without it.
    /// </summary>
    /// <remarks>
    /// Stored rather than treated as a form-only checkbox so the API cannot activate without it either. It is
    /// not consent theatre: the difference between Spark's 2-of-3 operator set and a regulated issuer who can
    /// freeze the balance is the kind of thing a merchant should have to tick.
    /// </remarks>
    public bool DisclosureAcknowledged { get; set; }

    /// <summary>The token identifier. Defaults to mainnet USDB; overridable by the operator.</summary>
    public string TokenIdentifier { get; set; } = DefaultTokenIdentifier;

    /// <summary>The plugin-chosen label for that token.</summary>
    public string Label { get; set; } = DefaultLabel;

    /// <summary>
    /// The token's decimals, for display arithmetic.
    /// </summary>
    /// <remarks>
    /// Stored so a non-default token can be configured coherently, but the live value from token metadata wins
    /// wherever one is available. Nothing computes a money figure from this without having tried the metadata
    /// first.
    /// </remarks>
    public uint Decimals { get; set; } = DefaultDecimals;

    /// <summary>Conversion slippage tolerance, in basis points.</summary>
    public uint MaxSlippageBps { get; set; } = DefaultMaxSlippageBps;

    /// <summary>
    /// Balance at which the SDK auto-converts, in satoshi. Null leaves the service's own minimum in force.
    /// </summary>
    /// <remarks>
    /// Clamped <em>upward</em> by the SDK to the conversion minimum, so a value below that floor is silently
    /// raised rather than honoured.
    /// </remarks>
    public long? AutoConvertThresholdSats { get; set; }

    /// <summary>The configured token as a typed identifier, or null when it is blank.</summary>
    public Sdk.SparkTokenIdentifier? Token() =>
        string.IsNullOrWhiteSpace(TokenIdentifier)
            ? null
            : new Sdk.SparkTokenIdentifier(TokenIdentifier);

    /// <summary>
    /// The label actually in force. Falls back to the default rather than allowing a blank one.
    /// </summary>
    /// <remarks>
    /// A blank label is not a valid configuration — the SDK requires labels to be unique and non-empty within
    /// the token list, and activation addresses the token by label — so an empty stored value is treated as
    /// unset rather than passed through into a connect that would fail.
    /// </remarks>
    public string EffectiveLabel =>
        string.IsNullOrWhiteSpace(Label) ? DefaultLabel : Label.Trim();

    /// <summary>The decimals actually in force. Zero is treated as unset.</summary>
    public uint EffectiveDecimals => Decimals > 0 ? Decimals : DefaultDecimals;

    /// <summary>An independent copy. Every property added to this class must be added here too.</summary>
    public StableBalanceSettings Clone() => new()
    {
        Enabled = Enabled,
        DisclosureAcknowledged = DisclosureAcknowledged,
        TokenIdentifier = TokenIdentifier,
        Label = Label,
        Decimals = Decimals,
        MaxSlippageBps = MaxSlippageBps,
        AutoConvertThresholdSats = AutoConvertThresholdSats
    };
}

/// <summary>
/// Experimental unilateral exit: recovering the store's Spark balance on-chain without the operators
/// cooperating on an exit transaction.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing in this section is reachable unless the host sets
/// <c>FLINT_EXPERIMENTAL_UNILATERAL_EXIT</c></b> — see <see cref="Constants.UnilateralExitEnabled"/>. With the
/// gate off the Advanced page shows no entry point and every controller action returns 404, so a blob carrying
/// an acknowledgement is inert rather than dangerous. The section is still written and still cloned, because a
/// setting that only exists while a feature flag is on is a setting that vanishes the first time somebody saves
/// with the flag off.
/// </para>
/// <para>
/// <b>What it is, stated plainly, because the word oversells it.</b> A cooperative exit — every sweep this
/// plugin makes — asks the operators to build and broadcast one Bitcoin transaction, and it lands in seconds
/// for a flat fee. A unilateral exit walks the store's own leaves out through the statechain's timelocked
/// transaction tree: the SDK builds and signs, the plugin <em>never</em> broadcasts, and an operator has to
/// push the transactions by hand in dependency order, waiting on confirmations and on CSV timelocks measured
/// in days. It is a last resort for the case the exit path this plugin actually uses stops working, not a
/// privacy or cost option, and the copy on the page says so.
/// </para>
/// <para>
/// <b>Three traps that are the reason this is experimental rather than a feature.</b> First, on the pinned SDK
/// (Breez.Sdk.Spark 0.22.0) preparing an exit <em>still requires the operators to be reachable</em>: the
/// scenario a merchant most wants this for — operators gone — is the one it cannot serve until the SDK ships
/// exit-from-local-state. Second, the tree transactions cannot pay their own fees, so the exit is funded by
/// CPFP from an on-chain UTXO the operator has to send to a plugin-derived native-SegWit address first (see
/// <see cref="Constants.UnilateralExitFundingAccount"/>); too little there and the build refuses. Third, the
/// funds are not spendable when the transactions are built — they are spendable when the last timelock
/// expires.
/// </para>
/// <para>
/// Deliberately two properties. Everything else about an exit — fee rate, destination, which leaves — belongs
/// to one attempt and lives on the exit record, not in the store's configuration: a persisted quote is a stale
/// quote, and a persisted destination is an address nobody re-read before money moved.
/// </para>
/// </remarks>
public class UnilateralExitSettings
{
    /// <summary>
    /// The operator has been shown what a unilateral exit costs them in time and attention, and accepted it.
    /// Quoting and building are refused without it.
    /// </summary>
    /// <remarks>
    /// Stored rather than treated as a form-only checkbox, for the same reason
    /// <see cref="StableBalanceSettings.DisclosureAcknowledged"/> is: a checkbox enforced in a view is enforced
    /// nowhere. The service re-reads this before every operation, so the acknowledgement is a server-side gate
    /// on an action that produces signed transactions spending the store's balance — and one an operator has to
    /// have made deliberately, because the alternative is discovering the multi-day timelocks after starting.
    /// </remarks>
    public bool DisclosureAcknowledged { get; set; }

    /// <summary>
    /// Base URL of the esplora-compatible API used to discover the funding UTXO. Null uses the default for the
    /// store's network.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The plugin has to see the funding UTXO before it can build anything, and it cannot ask the SDK: the
    /// funding output is an ordinary on-chain UTXO on an address derived outside Spark's key tree, so nothing in
    /// the wallet knows about it. BTCPay's own NBXplorer does not either, because the address is not in any of
    /// the store's derivation schemes. That leaves a block explorer, and an override so an operator can point at
    /// their own instance rather than a third party that learns which address is funding their exit.
    /// </para>
    /// <para>
    /// <b>There is no usable default off mainnet.</b> mempool.space has no regtest, so a regtest exit without
    /// this set is refused with a message saying to set it, rather than silently reporting no funding found —
    /// which reads identically to "your UTXO has not confirmed yet" and would have an operator waiting on a
    /// confirmation that already happened.
    /// </para>
    /// <para>
    /// Nothing here is trusted with a decision. A wrong or hostile explorer can make the build refuse (no UTXO
    /// found) or fail at broadcast (a UTXO that does not exist), which is why the operator sees the funding
    /// figure the explorer reported before pressing Build; it cannot redirect money, because the destination is
    /// in the transactions the SDK signs.
    /// </para>
    /// </remarks>
    public string? EsploraApiUrl { get; set; }

    /// <summary>An independent copy. Every property added to this class must be added here too.</summary>
    public UnilateralExitSettings Clone() => new()
    {
        DisclosureAcknowledged = DisclosureAcknowledged,
        EsploraApiUrl = EsploraApiUrl
    };
}

/// <summary>
/// Origin of the store's Spark wallet seed — the three sources the setup wizard offers, and the only three.
/// </summary>
/// <remarks>
/// <see cref="Generated"/> is the default on purpose. <see cref="HotWallet"/> is offered as the convenience
/// option rather than the recommended one, because reusing the store's BTCPay hot-wallet seed means one
/// compromise drains both balances; it is also simply unavailable to a watch-only store, and to a wallet with
/// a BIP39 passphrase, since NBXplorer does not store passphrases. Key derivation is not the concern: Spark
/// derives at a hardened <c>m/8797555'/…</c> path, disjoint from BIP84/86.
/// </remarks>
public enum SeedSource
{
    /// <summary>Freshly generated by the plugin and shown once via BTCPay's recovery-seed screen.</summary>
    Generated,

    /// <summary>Imported from a mnemonic the merchant supplied.</summary>
    Imported,

    /// <summary>Reused from the store's BTCPay hot wallet via core's <c>HotwalletSafe</c>.</summary>
    HotWallet
}

/// <summary>
/// Where a sweep sends to.
/// </summary>
/// <remarks>
/// An enum rather than "static address, or null for the store wallet", because the two are different
/// intentions and the difference has to be visible in the UI and in a refusal. A store in
/// <see cref="StoreWallet"/> mode with no on-chain wallet must be refused loudly, not quietly demoted to
/// whatever address happens to be left in <see cref="SweepSettings.StaticAddress"/> from an earlier
/// configuration.
/// <para>
/// Phase 2 adds destinations that are not Bitcoin addresses at all (a stablecoin balance, an EVM address via
/// the SDK's cross-chain send). Those arrive as new members here plus a new
/// <c>SweepDestinationKind</c>; nothing in the engine's sequence changes.
/// </para>
/// </remarks>
public enum SweepDestinationMode
{
    /// <summary>
    /// A fresh, labelled address from the store's BTC derivation scheme, rotated every sweep. The default,
    /// and the proven Boltz pattern.
    /// </summary>
    StoreWallet,

    /// <summary>
    /// One fixed Bitcoin address the merchant supplied. For stores with no on-chain BTCPay wallet — a
    /// watch-only store, or one that keeps its cold storage entirely outside BTCPay.
    /// </summary>
    StaticAddress,

    /// <summary>
    /// A stablecoin delivered to an address on an EVM chain, through the SDK's cross-chain send.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a Bitcoin address and not a cooperative exit: the wallet transfers to the bridge provider's Spark
    /// deposit address and the provider settles on the destination chain. It is still an ordinary Spark
    /// transfer at the point money leaves this wallet, so the exit-path policy is untouched — no exit of
    /// either kind happens here. A unilateral exit is reachable only from the experimental, env-gated flow on
    /// the Advanced page (<see cref="UnilateralExitSettings"/>), never from a sweep.
    /// </para>
    /// <para>
    /// <b>Mainnet only</b>, hard-gated by the SDK: a connect that carries a cross-chain configuration on
    /// regtest throws outright.
    /// </para>
    /// </remarks>
    EvmAddress
}

/// <summary>
/// Confirmation-speed tier for a cooperative exit, mapped onto the SDK's
/// <c>OnchainConfirmationSpeed</c>.
/// </summary>
/// <remarks>
/// Declared here rather than reusing the SDK enum so the persisted settings blob does not depend on a
/// pre-1.0 SDK type — and because the SDK's ordering is <c>Fast = 0, Medium = 1, Slow = 2</c>, which would
/// make <c>default</c> mean "Fast" (the most expensive tier) for every store that never touched the setting.
/// </remarks>
public enum SweepConfirmationSpeed
{
    Slow,
    Medium,
    Fast
}

/// <summary>
/// Auto-sweep settings. Off by default: a merchant must opt in.
/// </summary>
/// <remarks>
/// <para>
/// <b>The economics these defaults encode.</b> A cooperative exit costs a <em>flat</em> fee, independent of
/// the amount swept: the funded regtest run measured <c>userFeeSat = 750</c> plus an
/// <c>l1BroadcastFeeSat</c> of 1,200/1,440/1,680 by tier — 1,950/2,190/2,430 sats total — identically for
/// every amount from 294 to 99,901 sats. So the only thing that decides whether a sweep is a good deal is
/// how large it is. Sweeping 5,000 sats burns ~39% of it; sweeping 500,000 burns ~0.4%.
/// </para>
/// <para>
/// Every one of these numbers was measured on <b>regtest</b>, where <c>l1BroadcastFeeSat</c> tracks a
/// 1 sat/vB chain. Mainnet will differ, and the defaults below should be re-measured before the plugin is
/// listed.
/// </para>
/// </remarks>
public class SweepSettings
{
    /// <summary>
    /// Default balance at which a sweep is considered.
    /// </summary>
    /// <remarks>
    /// 200,000 sats, so a default-configured sweep pays roughly 1% in exit fees — the level the spike
    /// notes identify as the point below which a merchant should be warned rather than silently charged.
    /// </remarks>
    public const long DefaultBalanceThresholdSats = 200_000;

    /// <summary>
    /// Default economic floor under a single sweep.
    /// </summary>
    /// <remarks>
    /// 100,000 sats. At the tier fees above that is a 2.0–2.4% cost, which is the worst deal this plugin
    /// will make without the merchant explicitly lowering the floor. It sits below
    /// <see cref="DefaultBalanceThresholdSats"/> on purpose: the threshold decides <em>when</em> to look, the
    /// floor decides whether what is actually sweepable — balance minus reserve, after a partial spend or a
    /// reserve change — is still worth sending.
    /// </remarks>
    public const long DefaultMinimumSweepSats = 100_000;

    /// <summary>
    /// Default ceiling on the exit fee, as a percentage of the swept amount.
    /// </summary>
    /// <remarks>
    /// The guard is defaulted in <em>percentage</em> form rather than as flat sats precisely because the fee
    /// is flat: a flat-sats default would either be so high it never bites or would silently stop every sweep
    /// the first time mainnet broadcast fees rose. 3% clears the 2,430-sat worst-case tier fee at the
    /// 100,000-sat floor (3,000 sats allowed) with room for mainnet fee levels, while still refusing the
    /// pathological case where an exit quote comes back an order of magnitude out.
    /// </remarks>
    public const double DefaultMaxFeePercent = 3.0;

    /// <summary>
    /// The share of the delivered amount that no configuration may authorise paying in fees.
    /// </summary>
    /// <remarks>
    /// A backstop, not a default. Clearing <see cref="MaxFeePercent"/> and entering a very large
    /// <see cref="MaxFeeFlatSats"/> used to leave nothing for the engine to take a minimum against — a fee guard
    /// switched off through a form the plugin accepted. 50% is deliberately absurd: it is not a policy anyone should
    /// operate at, it is the line past which the plugin refuses regardless of what it was told.
    /// </remarks>
    public const double HardMaxFeePercent = 50.0;

    public bool Enabled { get; set; }

    /// <summary>
    /// Sweep is considered once the Spark balance exceeds this value. Read
    /// <see cref="EffectiveBalanceThresholdSats"/> rather than this property.
    /// </summary>
    public long BalanceThresholdSats { get; set; } = DefaultBalanceThresholdSats;

    /// <summary>
    /// The threshold actually in force: <see cref="BalanceThresholdSats"/>, or the default when it is not set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A migration concern, not a preference. Wave 3 shipped <see cref="BalanceThresholdSats"/> with no initialiser,
    /// so every settings blob written before this wave contains an explicit <c>"BalanceThresholdSats": 0</c> — and an
    /// explicit zero wins over a property initialiser on deserialize. Such a store would read as "sweep at any
    /// balance", relying on the economic floor to say no, and its settings page would pre-fill zero as though the
    /// merchant had chosen it.
    /// </para>
    /// <para>
    /// Nothing is lost by treating zero as unset: <see cref="MinimumSweepSats"/> is at least the on-chain dust floor
    /// and is the real economic gate, so a threshold of zero is only ever equivalent to a threshold of the minimum.
    /// The form also refuses to save an enabled configuration whose threshold cannot clear its own minimum, so zero
    /// is unreachable for a store a merchant has actually configured.
    /// </para>
    /// </remarks>
    public long EffectiveBalanceThresholdSats =>
        BalanceThresholdSats > 0 ? BalanceThresholdSats : DefaultBalanceThresholdSats;

    /// <summary>
    /// Amount left behind on Spark after a sweep, to keep receiving cheaply.
    /// </summary>
    /// <remarks>
    /// With <see cref="DrainWhenSweeping"/> off the exit fee is charged on top of the swept amount, so the
    /// reserve is also what pays it — a reserve smaller than the fee is refused rather than allowed to
    /// overdraw.
    /// </remarks>
    public long ReserveSats { get; set; }

    /// <summary>
    /// Smallest amount worth sweeping, in satoshi. See the class remarks for why this exists.
    /// </summary>
    public long MinimumSweepSats { get; set; } = DefaultMinimumSweepSats;

    /// <summary>Which of the SDK's three fee tiers to use.</summary>
    public SweepConfirmationSpeed ConfirmationSpeed { get; set; } = SweepConfirmationSpeed.Medium;

    /// <summary>
    /// Ceiling on the exit fee as a percentage of the swept amount. Zero or less disables the percentage
    /// guard, in which case <see cref="MaxFeeFlatSats"/> is the only limit.
    /// </summary>
    public double MaxFeePercent { get; set; } = DefaultMaxFeePercent;

    /// <summary>
    /// Optional absolute ceiling on the exit fee, in satoshi. When both guards are set the stricter one
    /// wins.
    /// </summary>
    public long? MaxFeeFlatSats { get; set; }

    /// <summary>
    /// Take the exit fee out of the swept amount (the SDK's <c>FeePolicy.FeesIncluded</c>) rather than
    /// charging it on top.
    /// </summary>
    /// <remarks>
    /// <b>On by default, and this is a cooperative exit either way.</b> "Drain" here means only that the fee
    /// is netted out of the amount, so the balance lands on exactly <see cref="ReserveSats"/>; it has nothing
    /// to do with a unilateral exit. Sweeping — automatic or manual — is cooperative, always; the only
    /// unilateral-exit path in the plugin is the experimental, env-gated, manually-broadcast one on the
    /// Advanced page (<see cref="UnilateralExitSettings"/>), which no sweep setting can reach. It
    /// defaults on because the default <see cref="ReserveSats"/> is zero, and with
    /// the fee charged on top a zero reserve leaves nothing to charge it against.
    /// </remarks>
    public bool DrainWhenSweeping { get; set; } = true;

    /// <summary>Where sweeps go.</summary>
    public SweepDestinationMode DestinationMode { get; set; } = SweepDestinationMode.StoreWallet;

    /// <summary>
    /// The fixed destination used in <see cref="SweepDestinationMode.StaticAddress"/> mode. Validated
    /// against this server's Bitcoin network on save <em>and</em> again before every send.
    /// </summary>
    public string? StaticAddress { get; set; }

    #region Cross-chain

    /// <summary>
    /// Default economic floor under a cross-chain sweep, in satoshi.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 50,000 sats, and it is set by economics rather than by the protocol minimum — which is the whole point.
    /// Orchestra's own floor is somewhere between 1,000 and 1,500 sats, so a merchant could sweep far below
    /// this. The cost curve says not to: the fee has a fixed component of roughly $0.025 plus about 0.29%, so
    /// the smallest viable send (~1,500 sats, ~$1.00) costs <b>3.3%</b> while a 55,000-sat sweep costs
    /// <b>0.34%</b>. 50,000 sats keeps the all-in under about 0.4%.
    /// </para>
    /// <para>
    /// Deliberately higher than <see cref="DefaultMinimumSweepSats"/>: a cooperative exit's fee is flat, so its
    /// floor is about clearing a fixed cost, while this one is about staying on the flat part of a curve that
    /// is brutal at the bottom.
    /// </para>
    /// </remarks>
    public const long DefaultCrossChainMinimumSweepSats = 50_000;

    /// <summary>
    /// Default slippage tolerance for a cross-chain send, in basis points.
    /// </summary>
    /// <remarks>
    /// 50 bps, matching Breez's own sample. <b>Set explicitly rather than inherited</b>: leaving the SDK's
    /// cross-chain slippage unset falls back to 100 bps, which is 10× the Stable Balance default and, on a $35
    /// sweep, up to $0.35 of tolerated slippage. The SDK accepts 10–500 on this field.
    /// </remarks>
    public const uint DefaultCrossChainSlippageBps = 50;

    /// <summary>Bounds the SDK enforces on cross-chain slippage, in basis points.</summary>
    public const uint MinCrossChainSlippageBps = 10;

    /// <inheritdoc cref="MinCrossChainSlippageBps"/>
    public const uint MaxCrossChainSlippageBps = 500;

    /// <summary>Default destination asset for a cross-chain sweep.</summary>
    /// <remarks>
    /// <c>USDT</c>. Matched case-insensitively and <b>exactly</b> against the route's asset, which matters:
    /// Boltz mostly offers <c>USDT0</c>, the LayerZero omnichain token, which is a different asset a merchant
    /// expecting Tether will not accept. A prefix match would silently take it.
    /// </remarks>
    public const string DefaultCrossChainAsset = "USDT";

    /// <summary>Default destination chain for a cross-chain sweep.</summary>
    /// <remarks>
    /// Arbitrum. It is Orchestra's native settlement chain, so it has the shortest path and the least to go
    /// wrong, and live quotes for arbitrum, optimism and polygon were identical in fee terms anyway — the
    /// recipient never pays gas, because settlement is the provider's problem and its cost is already inside
    /// the quoted fee.
    /// </remarks>
    public const string DefaultCrossChainChain = "arbitrum";

    /// <summary>
    /// The EVM address a cross-chain sweep delivers to, in <see cref="SweepDestinationMode.EvmAddress"/> mode.
    /// </summary>
    /// <remarks>
    /// Validated on save and again before every send, by asking the SDK to parse it rather than by a local
    /// regex — the SDK is the authority on what it will accept, and it also tells us the address family.
    /// </remarks>
    public string? EvmAddress { get; set; }

    /// <summary>The destination chain, e.g. <c>arbitrum</c>. Matched against the live route table.</summary>
    public string? EvmChain { get; set; }

    /// <summary>The destination asset, e.g. <c>USDT</c>. Matched exactly, never by prefix.</summary>
    public string? EvmAsset { get; set; }

    /// <summary>Cross-chain slippage tolerance, in basis points. Null uses the plugin default.</summary>
    public uint? CrossChainSlippageBps { get; set; }

    /// <summary>
    /// Default economic floor under a cross-chain sweep funded from a stablecoin balance, in whole units of
    /// that stablecoin.
    /// </summary>
    /// <remarks>
    /// 20 — twenty dollars, for USDB. Whole units rather than base units because base units are the trap this
    /// wave exists to fence off, and because "never sweep less than $20" is a sentence a merchant can check.
    /// The figure is the same economic judgement as
    /// <see cref="DefaultCrossChainMinimumSweepSats"/>: below roughly this size the fixed ~$0.025 component of
    /// the provider fee starts to dominate.
    /// </remarks>
    public const long DefaultCrossChainMinimumStableUnits = 20;

    /// <summary>
    /// Smallest cross-chain sweep to make from a stablecoin balance, in <b>whole units</b> of the configured
    /// stable token.
    /// </summary>
    /// <remarks>
    /// Applies only when Stable Balance is active, because only then is a cross-chain sweep funded from a token
    /// rather than from satoshi. <see cref="MinimumSweepSats"/> is meaningless on that path — the amount is not
    /// in satoshi and there is no exchange rate available locally to convert one floor into the other.
    /// </remarks>
    public long CrossChainMinimumStableUnits { get; set; } = DefaultCrossChainMinimumStableUnits;

    /// <summary>The stablecoin floor actually in force. Zero or less is treated as unset.</summary>
    public long EffectiveCrossChainMinimumStableUnits =>
        CrossChainMinimumStableUnits > 0
            ? CrossChainMinimumStableUnits
            : DefaultCrossChainMinimumStableUnits;

    /// <summary>The chain actually in force.</summary>
    public string EffectiveCrossChainChain =>
        string.IsNullOrWhiteSpace(EvmChain) ? DefaultCrossChainChain : EvmChain.Trim();

    /// <summary>The asset actually in force.</summary>
    public string EffectiveCrossChainAsset =>
        string.IsNullOrWhiteSpace(EvmAsset) ? DefaultCrossChainAsset : EvmAsset.Trim();

    /// <summary>The slippage actually in force, clamped into the range the SDK accepts.</summary>
    /// <remarks>
    /// Clamped rather than passed through because an out-of-range value is rejected at <em>prepare</em>, which
    /// is after a sweep record exists. The settings form refuses to store one, so reaching the clamp means a
    /// blob that arrived by another route.
    /// </remarks>
    public uint EffectiveCrossChainSlippageBps =>
        CrossChainSlippageBps is { } bps
            ? Math.Clamp(bps, MinCrossChainSlippageBps, MaxCrossChainSlippageBps)
            : DefaultCrossChainSlippageBps;

    /// <summary>
    /// The economic floor in force for this destination mode.
    /// </summary>
    /// <remarks>
    /// A cross-chain sweep takes the higher of the merchant's own minimum and the cross-chain default, because
    /// the two floors answer different questions and a store that switched destination mode should not carry a
    /// coop-exit-shaped floor onto a curve it does not fit.
    /// </remarks>
    public long EffectiveMinimumSweepSats =>
        DestinationMode is SweepDestinationMode.EvmAddress
            ? Math.Max(MinimumSweepSats, DefaultCrossChainMinimumSweepSats)
            : MinimumSweepSats;

    #endregion

    /// <summary>
    /// An independent copy, for carrying a store's sweep configuration onto a new settings object.
    /// </summary>
    /// <remarks>
    /// Mutable settings objects are handed out by reference from the service's cache, so aliasing this one
    /// across a seed change would make an edit to the new configuration silently edit the old — including the
    /// copy a failed provisioning attempt is supposed to roll back to. <b>Every property added to this class
    /// must be added here too</b>; a field left out is a setting that silently stops surviving a seed change.
    /// <c>SweepSettingsTests.Clone_copies_every_property</c> enumerates the properties by reflection and
    /// fails when one is missed, so this cannot rot silently.
    /// </remarks>
    public SweepSettings Clone() => new()
    {
        Enabled = Enabled,
        BalanceThresholdSats = BalanceThresholdSats,
        ReserveSats = ReserveSats,
        MinimumSweepSats = MinimumSweepSats,
        ConfirmationSpeed = ConfirmationSpeed,
        MaxFeePercent = MaxFeePercent,
        MaxFeeFlatSats = MaxFeeFlatSats,
        DrainWhenSweeping = DrainWhenSweeping,
        DestinationMode = DestinationMode,
        StaticAddress = StaticAddress,
        EvmAddress = EvmAddress,
        EvmChain = EvmChain,
        EvmAsset = EvmAsset,
        CrossChainSlippageBps = CrossChainSlippageBps,
        CrossChainMinimumStableUnits = CrossChainMinimumStableUnits
    };
}
