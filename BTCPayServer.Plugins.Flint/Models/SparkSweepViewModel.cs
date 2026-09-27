using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Services;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace BTCPayServer.Plugins.Flint.Models;

/// <summary>
/// The bound half of the sweep settings page.
/// </summary>
/// <remarks>
/// <para>
/// A separate type from <see cref="SparkSweepViewModel"/> so the form's inbound surface is exactly these ten
/// fields. Everything the page also needs — the balance, the history, whether the store even has an on-chain
/// wallet — is a server-side fact, and a posted value that quietly replaced one of those would be a form deciding
/// what it is allowed to do.
/// </para>
/// <para>
/// The validation here is a courtesy, not the enforcement. <see cref="SparkSweepEngine"/> re-derives every
/// economic and safety decision from the stored settings and the live fee quote, so a settings blob that arrives
/// by some other route is still refused at the point money would move.
/// </para>
/// </remarks>
public class SweepSettingsInput
{
    [Display(Name = "Sweep automatically")]
    public bool Enabled { get; set; }

    [Display(Name = "Sweep when the balance passes")]
    public long BalanceThresholdSats { get; set; } = SweepSettings.DefaultBalanceThresholdSats;

    [Display(Name = "Leave behind")]
    public long ReserveSats { get; set; }

    [Display(Name = "Never sweep less than")]
    public long MinimumSweepSats { get; set; } = SweepSettings.DefaultMinimumSweepSats;

    /// <remarks>
    /// The <c>StringEnumConverter</c> is for the Greenfield surface, which uses this very type as the body of
    /// <c>PUT .../spark/sweep</c> and its own response. BTCPay's MVC JSON settings carry no global enum converter,
    /// so without it the API would emit <c>1</c> where it documents <c>"Medium"</c> — core annotates its own
    /// Greenfield enums the same way. Form binding is unaffected: it goes through model binders, not Newtonsoft.
    /// </remarks>
    [Display(Name = "Confirmation speed")]
    [JsonConverter(typeof(StringEnumConverter))]
    public SweepConfirmationSpeed ConfirmationSpeed { get; set; } = SweepConfirmationSpeed.Medium;

    [Display(Name = "Maximum fee, as a percentage of the amount delivered")]
    public double MaxFeePercent { get; set; } = SweepSettings.DefaultMaxFeePercent;

    [Display(Name = "Maximum fee in sats (optional)")]
    public long? MaxFeeFlatSats { get; set; }

    [Display(Name = "Take the exit fee out of the swept amount")]
    public bool DrainWhenSweeping { get; set; } = true;

    [Display(Name = "Send sweeps to")]
    [JsonConverter(typeof(StringEnumConverter))]
    public SweepDestinationMode DestinationMode { get; set; } = SweepDestinationMode.StoreWallet;

    [Display(Name = "Fixed Bitcoin address")]
    public string? StaticAddress { get; set; }

    [Display(Name = "EVM address")]
    public string? EvmAddress { get; set; }

    [Display(Name = "Chain")]
    public string? EvmChain { get; set; }

    [Display(Name = "Asset")]
    public string? EvmAsset { get; set; }

    [Display(Name = "Cross-chain slippage, in basis points")]
    public uint? CrossChainSlippageBps { get; set; }

    [Display(Name = "Never sweep less than, in whole stablecoin units")]
    public long CrossChainMinimumStableUnits { get; set; } =
        SweepSettings.DefaultCrossChainMinimumStableUnits;

    [Display(Name = "Sweep webhook URL (optional)")]
    public string? SweepWebhookUrl { get; set; }

    public static SweepSettingsInput From(SweepSettings settings) => new()
    {
        EvmAddress = settings.EvmAddress,
        EvmChain = settings.EffectiveCrossChainChain,
        EvmAsset = settings.EffectiveCrossChainAsset,
        CrossChainSlippageBps = settings.CrossChainSlippageBps,
        CrossChainMinimumStableUnits = settings.EffectiveCrossChainMinimumStableUnits,
        Enabled = settings.Enabled,
        // The effective value, not the raw one: a blob written before this wave carries an explicit zero, and
        // pre-filling zero would show the merchant a threshold they never chose as though they had.
        BalanceThresholdSats = settings.EffectiveBalanceThresholdSats,
        ReserveSats = settings.ReserveSats,
        MinimumSweepSats = settings.MinimumSweepSats,
        ConfirmationSpeed = settings.ConfirmationSpeed,
        MaxFeePercent = settings.MaxFeePercent,
        MaxFeeFlatSats = settings.MaxFeeFlatSats,
        DrainWhenSweeping = settings.DrainWhenSweeping,
        DestinationMode = settings.DestinationMode,
        StaticAddress = settings.StaticAddress,
        SweepWebhookUrl = settings.SweepWebhookUrl
    };

    /// <summary>
    /// Applies this form onto a store's settings, leaving anything not on the form alone.
    /// </summary>
    /// <remarks>
    /// Mutates a caller-supplied instance rather than constructing one, so a property added to
    /// <see cref="SweepSettings"/> that this form does not expose is preserved rather than silently reset to its
    /// default on every save.
    /// </remarks>
    public void ApplyTo(SweepSettings settings)
    {
        settings.Enabled = Enabled;
        settings.BalanceThresholdSats = BalanceThresholdSats;
        settings.ReserveSats = ReserveSats;
        settings.MinimumSweepSats = MinimumSweepSats;
        settings.ConfirmationSpeed = ConfirmationSpeed;
        settings.MaxFeePercent = MaxFeePercent;
        settings.MaxFeeFlatSats = MaxFeeFlatSats;
        settings.DrainWhenSweeping = DrainWhenSweeping;
        settings.DestinationMode = DestinationMode;
        settings.StaticAddress = DestinationMode is SweepDestinationMode.StaticAddress
            ? StaticAddress?.Trim()
            // Cleared rather than kept when the merchant switches back to their store wallet. A leftover address
            // is a destination they have stopped intending to use, and leaving it in the settings is how it gets
            // used again by accident later.
            : null;

        // Same reasoning, and it matters more here: a leftover EVM address is a destination on a chain this
        // plugin cannot claw anything back from.
        var crossChain = DestinationMode is SweepDestinationMode.EvmAddress;
        settings.EvmAddress = crossChain ? EvmAddress?.Trim() : null;
        settings.EvmChain = crossChain ? EvmChain?.Trim() : null;
        settings.EvmAsset = crossChain ? EvmAsset?.Trim() : null;
        settings.CrossChainSlippageBps = crossChain ? CrossChainSlippageBps : null;
        settings.CrossChainMinimumStableUnits = CrossChainMinimumStableUnits;
        settings.SweepWebhookUrl = string.IsNullOrWhiteSpace(SweepWebhookUrl) ? null : SweepWebhookUrl.Trim();
    }

    /// <summary>
    /// Every reason this configuration would be rejected, keyed by the field to attach it to.
    /// </summary>
    /// <remarks>
    /// Returned rather than written straight into <c>ModelState</c> so the rules are unit-testable without a
    /// controller. The two cross-field checks are the ones worth having: a configuration whose threshold can
    /// never clear its own minimum, and a fee-on-top policy with no reserve to charge the fee against, both look
    /// perfectly reasonable field by field and then never sweep anything.
    /// </remarks>
    public IReadOnlyList<(string Field, string Error)> Validate(Network network)
    {
        var errors = new List<(string, string)>();

        if (BalanceThresholdSats < 0)
            errors.Add((nameof(BalanceThresholdSats), "The threshold cannot be negative."));

        if (ReserveSats < 0)
            errors.Add((nameof(ReserveSats), "The reserve cannot be negative."));

        if (MinimumSweepSats < Constants.MinimumOnchainSendSats)
        {
            errors.Add((nameof(MinimumSweepSats), string.Format(
                CultureInfo.InvariantCulture,
                "A Bitcoin transaction cannot carry less than {0:N0} sat, so the minimum cannot be below it.",
                Constants.MinimumOnchainSendSats)));
        }

        if (MaxFeePercent < 0 || MaxFeePercent > 100)
            errors.Add((nameof(MaxFeePercent), "The percentage must be between 0 and 100."));

        if (MaxFeeFlatSats is { } flat)
        {
            if (flat < 0)
            {
                errors.Add((nameof(MaxFeeFlatSats), "The fee limit cannot be negative."));
            }
            else if (flat > MinimumSweepSats)
            {
                // Otherwise clearing the percentage and typing a large number here is a fee guard switched off: the
                // engine had nothing left to take a minimum against. It now applies a hard backstop regardless, but
                // a configuration that only that backstop stands between and an absurd fee should not be saveable.
                errors.Add((nameof(MaxFeeFlatSats), string.Format(
                    CultureInfo.InvariantCulture,
                    "A fee limit of {0:N0} sat is above the {1:N0} sat smallest sweep you allow, so it would permit "
                    + "a sweep costing more than it delivers. Lower it, or raise the minimum sweep.",
                    flat, MinimumSweepSats)));
            }
        }

        if (MaxFeePercent <= 0 && MaxFeeFlatSats is null)
        {
            errors.Add((nameof(MaxFeePercent),
                "Set a fee limit. Sweeping is automatic, so there has to be a number above which the plugin "
                + "refuses to pay — leave the percentage in place, or set a limit in sats instead."));
        }

        if (DestinationMode is SweepDestinationMode.StaticAddress &&
            !SweepDestinationResolver.TryParse(StaticAddress, network, out var addressError))
        {
            errors.Add((nameof(StaticAddress), $"Enter a Bitcoin address to sweep to — {addressError}."));
        }

        if (Enabled && !DrainWhenSweeping && ReserveSats <= 0)
        {
            errors.Add((nameof(ReserveSats), string.Format(
                CultureInfo.InvariantCulture,
                "With the exit fee charged on top of the swept amount, the reserve is what pays it — and a "
                + "reserve of zero cannot. Either leave around {0:N0} sat behind, or switch to taking the fee "
                + "out of the swept amount.",
                Constants.IndicativeCoopExitFeeSats)));
        }

        if (Enabled && BalanceThresholdSats - ReserveSats < MinimumSweepSats)
        {
            errors.Add((nameof(BalanceThresholdSats), string.Format(
                CultureInfo.InvariantCulture,
                "At this threshold only {0:N0} sat would ever be sweepable, which is below the {1:N0} sat "
                + "minimum — so no automatic sweep could ever run. Raise the threshold or lower the minimum.",
                BalanceThresholdSats - ReserveSats, MinimumSweepSats)));
        }

        errors.AddRange(ValidateCrossChain(network));

        return errors;
    }

    /// <summary>
    /// The rules that only apply to a cross-chain destination.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Split out because they are conditional on the mode and because two of them are network facts rather than
    /// field checks. The most important is the first: <b>cross-chain sending is hard-gated to mainnet by the
    /// SDK</b>, and a connect carrying a cross-chain configuration on regtest throws outright. Storing an EVM
    /// destination on a regtest server would therefore not merely fail to sweep — it would produce a
    /// configuration the plugin refuses to build, so the refusal belongs here where the merchant can see it.
    /// </para>
    /// <para>
    /// The slippage bounds are the SDK's own (10–500), enforced at <c>Connect</c>. Rejecting an out-of-range
    /// value here rather than clamping it silently means the merchant learns that 1000 bps is not a thing rather
    /// than believing they configured it.
    /// </para>
    /// </remarks>
    private IEnumerable<(string Field, string Error)> ValidateCrossChain(Network network)
    {
        if (DestinationMode is not SweepDestinationMode.EvmAddress)
            yield break;

        if (network != Network.Main)
        {
            yield return (nameof(DestinationMode),
                $"Cross-chain sweeps only work on Bitcoin mainnet, and this server runs on "
                + $"{network.ChainName}. Spark refuses to start a wallet that is configured for cross-chain "
                + "sending on any other network, so this cannot be saved here.");
        }

        if (!SweepDestinationResolver.TryParseEvm(EvmAddress, out _, out var addressError))
            yield return (nameof(EvmAddress), $"Enter the address to sweep to — {addressError}.");

        if (string.IsNullOrWhiteSpace(EvmChain))
            yield return (nameof(EvmChain), "Choose the chain to deliver on, for example arbitrum.");

        if (string.IsNullOrWhiteSpace(EvmAsset))
            yield return (nameof(EvmAsset), "Choose the asset to deliver, for example USDT.");

        if (CrossChainSlippageBps is { } bps &&
            (bps < SweepSettings.MinCrossChainSlippageBps || bps > SweepSettings.MaxCrossChainSlippageBps))
        {
            yield return (nameof(CrossChainSlippageBps), string.Format(
                CultureInfo.InvariantCulture,
                "Spark accepts cross-chain slippage between {0} and {1} basis points.",
                SweepSettings.MinCrossChainSlippageBps, SweepSettings.MaxCrossChainSlippageBps));
        }

        if (CrossChainMinimumStableUnits < 0)
            yield return (nameof(CrossChainMinimumStableUnits), "The minimum cannot be negative.");

        if (Enabled && MinimumSweepSats < SweepSettings.DefaultCrossChainMinimumSweepSats)
        {
            // A warning shaped as a refusal, because the cost curve here is genuinely punishing at the bottom:
            // the provider's fee has a fixed ~$0.025 component, so the smallest viable send costs about 3.3% and
            // a 50,000-sat one about 0.34%. The protocol would allow far less; the economics should not.
            yield return (nameof(MinimumSweepSats), string.Format(
                CultureInfo.InvariantCulture,
                "A cross-chain send has a fixed fee component, so small ones are very poor value — about 3% at "
                + "the protocol minimum against about 0.3% at {0:N0} sat. Raise the minimum to at least "
                + "{0:N0} sat.",
                SweepSettings.DefaultCrossChainMinimumSweepSats));
        }
    }
}

/// <summary>
/// The sweep settings page: configuration, what it would do next, and what it has done.
/// </summary>
public class SparkSweepViewModel
{
    /// <summary>Always set by the controller from the store BTCPay authorised. Never bound.</summary>
    [BindNever]
    public string StoreId { get; set; } = string.Empty;

    /// <summary>The only inbound part of this page.</summary>
    [ValidateNever]
    public SweepSettingsInput Settings { get; set; } = new();

    [BindNever]
    public bool WalletRunning { get; set; }

    /// <summary>Spark balance in satoshi, or null when it could not be read. Indicative, as everywhere.</summary>
    [BindNever]
    public long? BalanceSats { get; set; }

    /// <summary>
    /// Whether the store has an on-chain wallet to sweep into, so the page can disable the option rather than
    /// offering a mode that would be refused.
    /// </summary>
    [BindNever]
    public SweepAddressStatus StoreWalletStatus { get; set; } = SweepAddressStatus.Unavailable;

    /// <summary>Merchant-facing explanation when the store has no usable on-chain wallet.</summary>
    [BindNever]
    public string? StoreWalletReason { get; set; }

    /// <summary>Name of the chain a fixed address is validated against, for the field's help text.</summary>
    [BindNever]
    public string NetworkName { get; set; } = string.Empty;

    /// <summary>
    /// Mempool fee rates, so the confirmation-speed tiers can show roughly what each pays right now. Null when
    /// they could not be read; the select renders without them.
    /// </summary>
    [BindNever]
    public Sdk.SparkRecommendedFees? RecommendedFees { get; set; }

    [BindNever]
    public IReadOnlyList<SweepRecord> History { get; set; } = [];

    [BindNever]
    public int HistoryTotal { get; set; }

    [BindNever]
    public int Skip { get; set; }

    [BindNever]
    public int Count { get; set; } = Constants.SweepHistoryPageSize;

    public bool StoreWalletAvailable => StoreWalletStatus is SweepAddressStatus.Available;

    /// <summary>
    /// Whether a cross-chain destination can be offered at all on this server.
    /// </summary>
    /// <remarks>
    /// Mainnet only, and this is a hard gate rather than a preference: the SDK throws at <c>Connect</c> if a
    /// wallet is configured for cross-chain sending on any other network, so storing such a destination would
    /// stop the store's wallet starting. The option is disabled rather than hidden so a merchant reading the
    /// page can see the feature exists and why it is unavailable.
    /// </remarks>
    [BindNever]
    public bool CrossChainAvailable { get; set; }

    /// <summary>
    /// The two destination pickers: what they offer, and what they open on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Handed in by the controller rather than computed here, because the catalogue behind it is a fetched and
    /// cached service and a view model that reached for one would be a view model that could make a network
    /// call. It is built from the form rather than from the stored settings: on a re-render the form is the
    /// merchant's rejected post, and the value they are looking at is the one the picker has to be able to show.
    /// </para>
    /// <para>
    /// Defaulted to the offline picker rather than left null, so a code path that forgets to set it renders the
    /// built-in floor rather than two empty selects. An empty select posts a blank chain and a blank asset, and
    /// a blank asset is stored and then silently resolved to the default at the point of sending.
    /// </para>
    /// </remarks>
    [BindNever]
    public CrossChainPicker Picker { get; set; } = CrossChainPicker.Offline;

    /// <summary>The chains the picker offers, plus whichever one this store already has if it is not among them.</summary>
    public IReadOnlyList<CrossChainDestination> ChainOptions => Picker.Chains;

    /// <summary>The assets the picker offers on <see cref="SelectedChain"/>. Never empty.</summary>
    public IReadOnlyList<CrossChainAsset> AssetOptions => Picker.Assets;

    /// <summary>
    /// The chain option the picker must open on.
    /// </summary>
    /// <remarks>
    /// Neither the raw field nor merely its trimmed form. With nothing stored,
    /// <see cref="SweepSettings.DefaultCrossChainChain"/> is what a sweep would use, so an unselected picker
    /// would say something untrue; and an <c>option</c> is selected by exact value where the route table matches
    /// case-insensitively, so this is the catalogue's spelling of what the store has.
    /// </remarks>
    public string SelectedChain => Picker.SelectedChain;

    /// <inheritdoc cref="SelectedChain"/>
    public string SelectedAsset => Picker.SelectedAsset;
}

/// <summary>
/// The confirmation step of a manual sweep: what is about to happen, before it happens.
/// </summary>
/// <remarks>
/// Nothing on this page is bound. The confirm button posts only the store id and the antiforgery token; the
/// engine re-derives the amount, the destination and the fee from the stored settings and a fresh quote. That is
/// deliberate — a form that carried the amount or the destination would be a form that could change them.
/// <see cref="StoreId"/> is marked <see cref="BindNeverAttribute"/> for consistency with every sibling model
/// rather than because this one is reachable: no action binds it. Consistency is the guard — the rule "a store
/// id on a view model is never inbound" is checkable, and "this particular model happens not to be bound today"
/// is not.
/// </remarks>
public class SparkSweepConfirmViewModel
{
    [BindNever]
    public string StoreId { get; set; } = string.Empty;

    public SweepPreview Preview { get; set; } = null!;
}
