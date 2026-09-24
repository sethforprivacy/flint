using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace BTCPayServer.Plugins.Flint.Models;

/// <summary>
/// The store's Spark status page.
/// </summary>
/// <remarks>
/// Holds no seed material, by construction: the identity pubkey is public by design, and the mnemonic is
/// never read back out of settings. Nothing here is inbound either — no action binds this model — but the
/// store id carries <see cref="BindNeverAttribute"/> all the same, because the guard against a form-bound
/// store id is only as good as the fact that every one of these models has it. See
/// <c>SparkControllerStoreScopeTests</c>, which asserts that across all of them rather than model by model.
/// </remarks>
public class SparkStatusViewModel
{
    [BindNever]
    public string StoreId { get; set; } = string.Empty;

    /// <summary>Where the seed came from, for the security-posture note on the page.</summary>
    public SeedSource SeedSource { get; set; }

    /// <summary>
    /// False when the settings exist but no SDK instance is running — a seed that cannot be decrypted, a
    /// second store on the same wallet, or an unsupported network. The page then says so instead of
    /// rendering an empty balance.
    /// </summary>
    public bool WalletRunning { get; set; }

    /// <summary>
    /// The wallet's Spark identity public key. Null when the wallet is not running or has not synced yet.
    /// </summary>
    public string? IdentityPubkey { get; set; }

    /// <summary>
    /// Spark balance in satoshi, or null when it could not be read.
    /// </summary>
    /// <remarks>
    /// Display only. It lagged settlement by ~20 s in the funded run and drifts by a few sats around the
    /// SDK's background leaf optimisation, so the page labels it as indicative and nothing derives an
    /// accounting figure from it.
    /// </remarks>
    public long? BalanceSats { get; set; }

    /// <summary>Set when the wallet is running but could not be read, for the merchant to see.</summary>
    public string? WalletError { get; set; }

    /// <summary>The Spark network's published status. Null renders as "unknown".</summary>
    public SparkNetworkStatus? NetworkStatus { get; set; }

    /// <summary>What the store's Lightning payment method currently points at.</summary>
    public SparkLightningWiringState LightningWiring { get; set; }

    /// <summary>False when Lightning is configured but excluded from checkout.</summary>
    public bool LightningEnabledForCheckout { get; set; }

    /// <summary>
    /// Absolute path of this store's SDK storage directory. <b>Null for anyone but a server admin.</b>
    /// </summary>
    /// <remarks>
    /// It is a fact about the host's filesystem layout, and a store manager is not a server operator: every
    /// role that can view store settings could read it, on a page that is otherwise entirely about the store.
    /// The pages render the row only when it is present.
    /// </remarks>
    public string? StorageDirectory { get; set; }

    /// <summary>
    /// The wallet's static Bitcoin deposit address, or null when it could not be read.
    /// </summary>
    /// <remarks>
    /// Shown here as well as on its own page because a merchant looking for "where do I send Bitcoin" starts on
    /// the status page. It is a live service-provider read behind a per-wallet cache, so a null here means the
    /// read failed rather than that the wallet has no address.
    /// </remarks>
    public string? DepositAddress { get; set; }

    /// <summary>
    /// How many on-chain deposits have matured and failed to be claimed.
    /// </summary>
    /// <remarks>
    /// <b>The number that must never be invisible.</b> Spark will not retry a claim it refused on fee grounds,
    /// so from the merchant's side an on-chain top-up simply never arrives. Surfaced on the status page rather
    /// than only on the deposit page, because nothing would send them to the deposit page.
    /// </remarks>
    public int StuckDepositCount { get; set; }

    /// <summary>False on any network but mainnet, where Stable Balance cannot work at all.</summary>
    public bool StableBalanceAvailable { get; set; }

    /// <summary>Whether the wallet reports a stable balance as active — the fact, not the setting.</summary>
    public bool StableBalanceActive { get; set; }

    /// <summary>The stablecoin balance as a readable quantity, or null when the store holds none.</summary>
    public string? StableBalanceHolding { get; set; }

    /// <summary>False on any network but mainnet, where the provider that bridges USDC and USDT does not operate.</summary>
    public bool StablecoinsAvailable { get; set; }

    /// <summary>Whether checkout offers USDC and USDT: either payment method configured and not switched off.</summary>
    public bool StablecoinsEnabled { get; set; }
}
