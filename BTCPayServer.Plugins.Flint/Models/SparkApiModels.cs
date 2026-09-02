using System;
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace BTCPayServer.Plugins.Flint.Models;

/// <summary>
/// The request and response shapes of the Greenfield Spark API.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not one of these types carries a store id.</b> That is a deliberate, load-bearing omission rather than
/// tidiness. BTCPay resolves the store a Greenfield request is authorised for from route data — and, failing that,
/// from the query string and then the form — but never from a JSON body, while the model binder is perfectly happy
/// to populate a <c>StoreId</c> property from that body. A request model with such a property is therefore the
/// exact shape of the cross-store hole that was found in the MVC controller: authorised against the caller's own
/// store, acting on somebody else's. The store always comes from
/// <c>HttpContext.GetStoreDataOrNull()</c>; see <see cref="Controllers.GreenfieldSparkController"/>.
/// </para>
/// <para>
/// <b>No response type has a mnemonic on it except <see cref="SparkProvisionResponse"/></b>, and that one is
/// documented as the single disclosure of a freshly generated phrase. Nothing reads a stored seed back.
/// </para>
/// </remarks>
public class SparkStatusData
{
    /// <summary>False when this store has never set Spark up. Everything below is then meaningless.</summary>
    public bool Configured { get; set; }

    /// <summary>Where this store's seed came from: <c>Generated</c>, <c>Imported</c> or <c>HotWallet</c>.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public SeedSource SeedSource { get; set; }

    /// <summary>
    /// Whether a Spark wallet is live for this store right now.
    /// </summary>
    /// <remarks>
    /// False with <see cref="Configured"/> true is the case worth automating against: a seed the server can no
    /// longer decrypt, a second store on the same wallet, or a chain Spark does not support. The store looks set up
    /// and takes no payments.
    /// </remarks>
    public bool WalletRunning { get; set; }

    /// <summary>The wallet's Spark identity public key, or null when it is not running or has not synced.</summary>
    /// <remarks>Public by design. It does, however, publicly link this wallet to whatever else shares its seed.</remarks>
    public string? IdentityPubkey { get; set; }

    /// <summary>
    /// Spark balance in satoshi, or null when it could not be read.
    /// </summary>
    /// <remarks>
    /// <b>Indicative only.</b> Read from the SDK's cache without forcing a sync, because this is a request thread.
    /// It lagged settlement by ~20 s in the funded regtest run and drifts by a few sats around the SDK's background
    /// leaf optimisation. Do not reconcile against it, do not derive an accounting figure from it, and do not use
    /// it to decide whether a payment arrived — the invoice is the authority on that.
    /// </remarks>
    public long? BalanceSats { get; set; }

    /// <summary>Set when the wallet is running but could not be read.</summary>
    public string? WalletError { get; set; }

    /// <summary>Spark's own published service status, or null when it could not be read.</summary>
    public SparkNetworkStatusData? NetworkStatus { get; set; }

    /// <summary>What this store's Lightning payment method currently points at.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public SparkLightningWiringState LightningWiring { get; set; }

    /// <summary>False when a Lightning payment method exists but is excluded from checkout.</summary>
    public bool LightningEnabledForCheckout { get; set; }

    /// <summary>
    /// Absolute path of this store's SDK storage directory on the server. <b>Null unless the caller is a
    /// server admin</b>: it describes the host's filesystem, not the store, and every role that can view
    /// store settings can reach this endpoint.
    /// </summary>
    public string? StorageDirectory { get; set; }
}

/// <param name="Status">
/// <c>Operational</c>, <c>Degraded</c>, <c>Partial</c>, <c>Major</c> or <c>Unknown</c>, as published by the Spark
/// operators. <c>Unknown</c> is not healthy.
/// </param>
public sealed record SparkNetworkStatusData(string Status, DateTimeOffset LastUpdated, bool IsOperational)
{
    public static SparkNetworkStatusData? From(SparkNetworkStatus? status) =>
        status is null ? null : new SparkNetworkStatusData(status.Status, status.LastUpdated, status.IsOperational);
}

/// <summary>
/// Body of <c>POST /api/v1/stores/{storeId}/spark</c>.
/// </summary>
public class SparkProvisionRequest
{
    /// <summary>
    /// Where the seed comes from: <c>generate</c>, <c>import</c> or <c>hotWallet</c>.
    /// </summary>
    /// <remarks>
    /// A string rather than a bound enum on purpose. A value the enum cannot parse would otherwise be rejected by
    /// Newtonsoft with its own wording ("Error converting value …") before this plugin sees the request, and the
    /// one thing every message on this path must be is plugin-authored — the neighbouring
    /// <see cref="Mnemonic"/> field means a relayed parser message is a plausible place for a word of somebody's
    /// recovery phrase to surface. Parsed by <see cref="TryParseSeedSource"/>, which is also where the accepted
    /// spellings are defined.
    /// </remarks>
    public string? SeedSource { get; set; }

    /// <summary>
    /// The BIP39 recovery phrase, for <c>import</c> only. Ignored for the other two sources.
    /// </summary>
    /// <remarks>
    /// <b>Never echoed back.</b> Not in a success response, not in a validation error, not in a log line. A
    /// rejected phrase produces a message about what is wrong with it and never quotes it.
    /// </remarks>
    public string? Mnemonic { get; set; }

    /// <summary>
    /// Maps the API's seed-source spellings onto the plugin's enum.
    /// </summary>
    /// <remarks>
    /// The wire names differ from the enum names (<c>generate</c> rather than <c>Generated</c>) because they read
    /// as instructions to the API, which is what they are. Case-insensitive, and both spellings are accepted for
    /// each source so a caller who guesses the enum name is not punished for it.
    /// </remarks>
    public static bool TryParseSeedSource(string? value, out SeedSource seedSource)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "generate" or "generated":
                seedSource = Flint.SeedSource.Generated;
                return true;
            case "import" or "imported":
                seedSource = Flint.SeedSource.Imported;
                return true;
            case "hotwallet" or "hot-wallet" or "hot_wallet":
                seedSource = Flint.SeedSource.HotWallet;
                return true;
            default:
                seedSource = Flint.SeedSource.Generated;
                return false;
        }
    }
}

/// <summary>
/// Response to a successful provisioning call.
/// </summary>
public class SparkProvisionResponse
{
    /// <summary>
    /// The generated recovery phrase — <b>returned exactly once, here, and never again.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Populated only when the request asked for <c>generate</c>. It is null for <c>import</c> (the caller already
    /// has it) and for <c>hotWallet</c> (it is the store's on-chain seed, which this API will not hand out).
    /// </para>
    /// <para>
    /// <b>This is the only response body in the whole API that can contain seed material.</b> There is no
    /// reveal-seed endpoint, deliberately: the phrase is stored encrypted with keys in the BTCPay data directory
    /// and is never read back into a response. If the caller does not persist this value now, the store's funds
    /// depend entirely on that server's data directory surviving. Do not log the response, and do not send it
    /// anywhere the store's own credentials are not already trusted.
    /// </para>
    /// </remarks>
    public string? Mnemonic { get; set; }

    /// <summary>The store's status immediately after provisioning, as <c>GET .../spark</c> would report it.</summary>
    public SparkStatusData Status { get; set; } = new();
}

/// <summary>
/// Response to <c>GET /api/v1/stores/{storeId}/spark/sweep</c>: the configuration, plus what has been swept.
/// </summary>
public class SparkSweepConfigurationData
{
    /// <summary>The sweep configuration in force. The same shape <c>PUT</c> accepts.</summary>
    public SweepSettingsInput Settings { get; set; } = new();

    /// <summary>Whether a Spark wallet is live for this store.</summary>
    public bool WalletRunning { get; set; }

    /// <summary>Spark balance in satoshi. Indicative — see <see cref="SparkStatusData.BalanceSats"/>.</summary>
    public long? BalanceSats { get; set; }

    /// <summary>
    /// Whether the store has an on-chain BTCPay wallet to sweep into: <c>Available</c>, <c>NoOnchainWallet</c> or
    /// <c>Unavailable</c>.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public SweepAddressStatus StoreWalletStatus { get; set; }

    /// <summary>Why the store wallet is not usable as a destination, when it is not.</summary>
    public string? StoreWalletReason { get; set; }

    /// <summary>The chain a static destination address is validated against, e.g. <c>Mainnet</c>.</summary>
    public string Network { get; set; } = string.Empty;

    /// <summary>Total number of sweep records for this store, for paging.</summary>
    public int Total { get; set; }

    public int Skip { get; set; }

    public int Count { get; set; }

    /// <summary>The requested page of sweep history, newest first.</summary>
    public IReadOnlyList<SparkSweepRecordData> History { get; set; } = [];

    /// <summary>
    /// Advisory warnings about the current sweep configuration.
    /// Empty when there is nothing worth flagging. On mainnet, low thresholds or fee ceilings
    /// produce entries here; the defaults were measured on regtest and may need adjustment.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; set; } = [];
}

/// <summary>
/// Response to <c>POST /api/v1/stores/{storeId}/spark/sync</c>: the balance after a forced wallet sync.
/// </summary>
public class SparkBalanceSyncData
{
    /// <summary>
    /// Spark balance in satoshi after forcing a wallet sync. Zero when the wallet is not running.
    /// </summary>
    public long BalanceSats { get; set; }

    /// <summary>Whether a Spark wallet is live for this store.</summary>
    public bool WalletRunning { get; set; }

    /// <summary>When the sync was performed, UTC.</summary>
    public DateTimeOffset SyncedAt { get; set; }
}

/// <summary>
/// One sweep attempt, including the ones that were refused.
/// </summary>
/// <remarks>
/// A projection of the stored row rather than the row itself, so a database column can be renamed without breaking
/// a caller. <see cref="RefusalCode"/> is the stable identity of a refusal — <see cref="Error"/> embeds live
/// figures and is not comparable between two occurrences of the same cause.
/// </remarks>
public class SparkSweepRecordData
{
    /// <summary>
    /// The UUID this sweep was sent under, which the SDK adopts as its own payment id.
    /// </summary>
    /// <remarks>
    /// Written before the send, which is what makes a crashed sweep resolvable rather than a guess. Also this
    /// record's primary key.
    /// </remarks>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>Where it went. Empty for a refusal that never resolved a destination.</summary>
    public string DestinationAddress { get; set; } = string.Empty;

    [JsonConverter(typeof(StringEnumConverter))]
    public SweepDestinationMode DestinationMode { get; set; }

    /// <summary>Amount asked of the SDK. Not necessarily what the destination receives.</summary>
    public long AmountSats { get; set; }

    /// <summary>What the destination receives, after the fee when <see cref="FeesIncluded"/> is true.</summary>
    public long RecipientAmountSats { get; set; }

    /// <summary>True when the exit fee was netted out of <see cref="AmountSats"/> rather than charged on top.</summary>
    public bool FeesIncluded { get; set; }

    [JsonConverter(typeof(StringEnumConverter))]
    public SweepConfirmationSpeed ConfirmationSpeed { get; set; }

    /// <summary>Fee the quote promised when the row was written.</summary>
    public long QuotedFeeSats { get; set; }

    /// <summary>Fee actually paid, or null until Spark reports the payment.</summary>
    public long? FeeSats { get; set; }

    /// <summary>The fee as a percentage of what the destination receives.</summary>
    public double FeePercent { get; set; }

    /// <summary>The Spark balance this sweep was decided from.</summary>
    public long BalanceAtDecisionSats { get; set; }

    /// <summary>On-chain txid of the cooperative exit, or null until Spark reports one.</summary>
    public string? TxId { get; set; }

    /// <summary><c>Automatic</c> or <c>Manual</c>. Both run the identical engine path.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public SweepTrigger Trigger { get; set; }

    /// <summary><c>Pending</c>, <c>Sent</c>, <c>Confirmed</c>, <c>Failed</c> or <c>Refused</c>.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public SweepRecordStatus Status { get; set; }

    /// <summary>The stable identity of a refusal, or <c>None</c>.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public SweepRefusalCode RefusalCode { get; set; }

    /// <summary>Why this sweep was refused or failed, in words fit for a merchant. Never contains secrets.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// How many times this row's outcome has been reached. One for a sweep; more for a recurring refusal, which is
    /// folded onto a single row per cause per day rather than filed once per pass.
    /// </summary>
    public int AttemptCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>When a recurring refusal was last reached. Null while it has only happened once.</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary><c>BitcoinAddress</c> for a cooperative exit, <c>EvmAddress</c> for a cross-chain sweep.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public SweepDestinationKind DestinationKind { get; set; }

    /// <summary>
    /// The amount sent, rendered with its own unit — satoshi, or a stablecoin quantity.
    /// </summary>
    /// <remarks>
    /// Provided because <see cref="AmountSats"/> is <b>zero and meaningless</b> on a sweep funded from a
    /// stablecoin balance: the amount is then in that token's base units, in
    /// <see cref="SourceAmountBaseUnits"/>. Read this rather than deciding which field applies.
    /// </remarks>
    public string Amount { get; set; } = string.Empty;

    /// <summary>Destination chain for a cross-chain sweep, e.g. <c>arbitrum</c>.</summary>
    public string? DestinationChain { get; set; }

    /// <summary>Destination asset for a cross-chain sweep, e.g. <c>USDT</c>.</summary>
    public string? DestinationAsset { get; set; }

    /// <summary>
    /// Which bridge provider carried it, e.g. <c>Orchestra</c>. Null for a cooperative exit.
    /// </summary>
    /// <remarks>
    /// Recorded because provider availability is not stable — every Boltz route currently fails at prepare — so
    /// which provider actually carried a sweep is a question worth being able to answer after the fact.
    /// </remarks>
    public string? Provider { get; set; }

    /// <summary>
    /// The bridge provider's quote id, written before the send.
    /// </summary>
    /// <remarks>
    /// On a sweep funded from a stablecoin balance this is the <b>only</b> thing that identifies the send: the
    /// SDK rejects an idempotency key on any transfer with a token leg, so <c>idempotencyKey</c> is this row's
    /// primary key and nothing more. <see cref="IdempotencyKeyAccepted"/> says which case a row is.
    /// </remarks>
    public string? ProviderQuoteId { get; set; }

    /// <summary>
    /// Whether <see cref="IdempotencyKey"/> is also a Spark payment id.
    /// </summary>
    /// <remarks>
    /// True for every cooperative exit and for a cross-chain sweep funded from satoshi. False when the send had
    /// a token leg — looking a payment up by the key would then return nothing, which read as evidence would
    /// say the sweep never happened.
    /// </remarks>
    public bool IdempotencyKeyAccepted { get; set; }

    /// <summary>The token this sweep was funded from, when it was not funded from satoshi.</summary>
    public string? SourceTokenIdentifier { get; set; }

    /// <summary>The amount sent in that token's base units, as a decimal string.</summary>
    public string? SourceAmountBaseUnits { get; set; }

    /// <summary>What the quote said would arrive, in destination-asset base units.</summary>
    public string? EstimatedOutBaseUnits { get; set; }

    /// <summary>
    /// What actually arrived, in destination-asset base units. Null until the provider reports delivery.
    /// </summary>
    /// <remarks>
    /// The authoritative settled figure. It arrives through <b>no event</b> — nothing in the SDK's event set
    /// concerns a delivery — so it appears only after the plugin's own polling has seen it.
    /// </remarks>
    public string? DeliveredAmountBaseUnits { get; set; }

    /// <summary>What arrived, or is expected to, as a readable quantity with its asset.</summary>
    public string? Delivered { get; set; }

    /// <summary>
    /// <c>Pending</c>, <c>Completed</c>, <c>Failed</c>, <c>RefundNeeded</c> or <c>Refunded</c>. Null for a
    /// cooperative exit.
    /// </summary>
    /// <remarks>
    /// <c>RefundNeeded</c> is the one that needs a human: Spark is holding funds it could not convert. The
    /// plugin requests a refund automatically on the next sweep pass, and nothing reports when that completes.
    /// <para>
    /// Note that a <c>Sent</c> status with a <c>Pending</c> conversion is the normal steady state for a minute
    /// or two: the payment reaching the provider and the money arriving on the destination chain are two
    /// different facts.
    /// </para>
    /// </remarks>
    [JsonConverter(typeof(StringEnumConverter))]
    public SparkConversionStatus? ConversionStatus { get; set; }

    public static SparkSweepRecordData From(SweepRecord record) => new()
    {
        DestinationKind = record.DestinationKind,
        Amount = record.DescribeAmount(),
        DestinationChain = record.DestinationChain,
        DestinationAsset = record.DestinationAsset,
        Provider = record.Provider?.ToString(),
        ProviderQuoteId = record.ProviderQuoteId,
        IdempotencyKeyAccepted = record.IdempotencyKeyAccepted,
        SourceTokenIdentifier = record.SourceTokenIdentifier,
        SourceAmountBaseUnits = record.SourceAmountBaseUnits,
        EstimatedOutBaseUnits = record.EstimatedOutBaseUnits,
        DeliveredAmountBaseUnits = record.DeliveredAmountBaseUnits,
        Delivered = record.DescribeDelivered(),
        ConversionStatus = record.ConversionStatus,
        IdempotencyKey = record.IdempotencyKey,
        DestinationAddress = record.DestinationAddress,
        DestinationMode = record.DestinationMode,
        AmountSats = record.AmountSats,
        RecipientAmountSats = record.RecipientAmountSats,
        FeesIncluded = record.FeesIncluded,
        ConfirmationSpeed = record.ConfirmationSpeed,
        QuotedFeeSats = record.QuotedFeeSats,
        FeeSats = record.FeeSats,
        FeePercent = record.FeePercent,
        BalanceAtDecisionSats = record.BalanceAtDecisionSats,
        TxId = record.TxId,
        Trigger = record.Trigger,
        Status = record.Status,
        RefusalCode = record.RefusalCode,
        Error = record.Error,
        AttemptCount = record.AttemptCount,
        CreatedAt = record.CreatedAt,
        CompletedAt = record.CompletedAt,
        LastSeenAt = record.LastSeenAt
    };
}

/// <summary>
/// Body of <c>POST /api/v1/stores/{storeId}/spark/sweep</c>.
/// </summary>
/// <remarks>
/// Deliberately does not carry an amount, a destination or a fee tier. Those are re-derived by the engine from the
/// store's stored configuration and a live quote; a request that could name them would be a request that could
/// change where a merchant's money goes.
/// </remarks>
public class SparkSweepRequest
{
    /// <summary>
    /// True to quote the sweep without sending it.
    /// </summary>
    /// <remarks>
    /// A dry run reserves no address from the store's wallet and writes no sweep record. Nothing about a preview
    /// is carried into a subsequent real sweep: the quote lives about a minute, and the engine re-quotes and
    /// re-checks the fee ceiling against the number it actually commits to.
    /// </remarks>
    public bool Preview { get; set; }

    /// <summary>
    /// When true, sweeps whatever is above the reserve even if it is below the configured minimum sweep amount.
    /// The absolute protocol minimum (<c>Constants.MinimumOnchainSendSats</c>) is still enforced.
    /// Ignored when <see cref="Preview"/> is true.
    /// </summary>
    public bool Force { get; set; }

    /// <summary>
    /// A Bitcoin address to sweep to instead of the store's configured destination.
    /// Validated against the current network. Ignored when <see cref="Preview"/> is true.
    /// Not compatible with stores configured for EVM cross-chain sweeps.
    /// </summary>
    public string? DestinationAddress { get; set; }
}

/// <summary>
/// What a sweep would do right now.
/// </summary>
public class SparkSweepPreviewData
{
    /// <summary>True when the engine would proceed. False means <see cref="RefusalReason"/> is set.</summary>
    public bool CanSweep { get; set; }

    /// <summary>Why the engine would refuse, in words fit for a merchant.</summary>
    public string? RefusalReason { get; set; }

    /// <summary>Spark balance the plan was made from, after an explicit sync.</summary>
    public long BalanceSats { get; set; }

    /// <summary>Balance minus the configured reserve, whether or not it is worth sweeping.</summary>
    public long SweepableSats { get; set; }

    /// <summary>Amount that would be asked of the SDK. Null when there would be no sweep.</summary>
    public long? AmountSats { get; set; }

    /// <summary>What the destination would receive at the store's chosen tier.</summary>
    public long? RecipientAmountSats { get; set; }

    /// <summary>Where it would go, when a destination could be resolved without consuming an address.</summary>
    public SparkSweepDestinationData? Destination { get; set; }

    /// <summary>
    /// The live quote: the fee at each tier and the one the store's configuration selects.
    /// </summary>
    /// <remarks>
    /// <b>An estimate.</b> A cooperative-exit quote expires in about a minute and the fees are flat rather than
    /// proportional, so the same numbers will not necessarily apply to a sweep issued later.
    /// </remarks>
    public SparkSweepQuoteData? Quote { get; set; }

    /// <summary>
    /// The live cross-chain quote, for a store sweeping to an EVM address.
    /// </summary>
    /// <remarks>
    /// Set instead of <see cref="Quote"/>, never alongside it: a sweep is on one rail or the other, and the two
    /// quotes are not comparable — a cooperative exit's fee is flat satoshi, a cross-chain quote's is a spread
    /// inside the destination asset.
    /// </remarks>
    public SparkCrossChainQuoteData? CrossChainQuote { get; set; }

    /// <summary>
    /// What would be sent, rendered with its own unit.
    /// </summary>
    /// <remarks>
    /// Read this rather than <see cref="AmountSats"/> when Stable Balance may be on: the amount is then in the
    /// stablecoin's base units and <see cref="AmountSats"/> is null.
    /// </remarks>
    public string? Amount { get; set; }

    /// <summary>The configuration this preview was computed against.</summary>
    public SweepSettingsInput Settings { get; set; } = new();
}

/// <param name="Mode">Which destination rule produced <paramref name="Address"/>.</param>
/// <param name="Rotates">True when a real sweep would reserve a fresh address rather than reuse this one.</param>
public sealed record SparkSweepDestinationData(
    string Address,
    [property: JsonConverter(typeof(StringEnumConverter))] SweepDestinationMode Mode,
    bool Rotates);

/// <param name="FeeSats">The fee at the tier the store's configuration selects.</param>
/// <param name="FeesIncluded">True when the fee is netted out of the amount rather than charged on top.</param>
/// <param name="TotalDebitedSats">What would leave the Spark balance in total.</param>
/// <param name="FeePercentOfRecipientAmount">
/// The fee as a percentage of what the destination receives — the number that makes a flat exit fee honest.
/// </param>
public sealed record SparkSweepQuoteData(
    long FeeSats,
    bool FeesIncluded,
    long TotalDebitedSats,
    double FeePercentOfRecipientAmount,
    long SlowFeeSats,
    long MediumFeeSats,
    long FastFeeSats,
    DateTimeOffset ExpiresAt)
{
    public static SparkSweepQuoteData From(SparkOnchainQuote quote) => new(
        quote.FeeSats,
        quote.FeesIncluded,
        quote.TotalDebitedSats,
        quote.FeePercentOfRecipientAmount,
        quote.Tiers.SlowFeeSats,
        quote.Tiers.MediumFeeSats,
        quote.Tiers.FastFeeSats,
        quote.Tiers.ExpiresAt);
}

/// <summary>
/// The outcome of a manual sweep.
/// </summary>
/// <remarks>
/// <b>A <c>200</c> means the engine ran and reached a decision, not that money moved.</b> Read
/// <see cref="Outcome"/>: only <c>Swept</c> means a cooperative exit was accepted. <c>Refused</c>, <c>Skipped</c>
/// and <c>InFlight</c> are routine, expected steady states of an engine designed to decline — a store whose fee
/// ceiling is below the current exit fee sits on <c>Refused</c> indefinitely and nothing is wrong. Mapping those
/// onto a <c>4xx</c> would make normal operation look like a client error and would discard
/// <see cref="RefusalCode"/> and <see cref="Record"/>.
/// </remarks>
public class SparkSweepResultData
{
    /// <summary>
    /// <c>Swept</c>, <c>InFlight</c>, <c>Skipped</c>, <c>Refused</c>, <c>Failed</c> or <c>Unresolved</c>.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public SweepOutcomeKind Outcome { get; set; }

    /// <summary>True exactly when <see cref="Outcome"/> is <c>Swept</c>.</summary>
    public bool Succeeded { get; set; }

    /// <summary>Always set, always fit for a merchant to read.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>The stable identity of a refusal, or <c>None</c>.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public SweepRefusalCode RefusalCode { get; set; }

    /// <summary>
    /// The record this pass created or resolved, when there is one. <c>Skipped</c> outcomes have none, by design:
    /// nothing happened and nothing is written.
    /// </summary>
    public SparkSweepRecordData? Record { get; set; }

    public static SparkSweepResultData From(SweepRunResult result) => new()
    {
        Outcome = result.Kind,
        Succeeded = result.Succeeded,
        Reason = result.Reason,
        RefusalCode = result.Record?.RefusalCode ?? SweepRefusalCode.None,
        Record = result.Record is null ? null : SparkSweepRecordData.From(result.Record)
    };
}

#region Deposits

/// <summary>
/// Response to <c>GET /api/v1/stores/{storeId}/spark/deposit</c>.
/// </summary>
public class SparkDepositData
{
    /// <summary>
    /// The wallet's static Bitcoin deposit address, or null when it could not be read.
    /// </summary>
    /// <remarks>
    /// Stable across calls and safe to save. Also the string to render as a QR code; the plugin does not
    /// produce an image.
    /// </remarks>
    public string? Address { get; set; }

    /// <summary>Why the address could not be read, when it could not.</summary>
    public string? AddressError { get; set; }

    public bool WalletRunning { get; set; }

    /// <summary>
    /// Everything sent to the address that has not been credited yet.
    /// </summary>
    /// <remarks>
    /// Two quite different things share this list. A deposit with <c>isMature</c> false is simply waiting for
    /// its third confirmation and needs nobody. A deposit with <c>isMature</c> true and a <c>claimError</c> is
    /// <b>stuck</b>: the SDK tried, the fee it needed was above the ceiling, and it will never retry at a lower
    /// price. That one needs a claim call.
    /// </remarks>
    public IReadOnlyList<SparkDepositEntryData> Deposits { get; set; } = [];

    /// <summary>Current mempool fee rates in sat/vB, so the claim ceiling can be judged against a real market.</summary>
    public SparkRecommendedFeesData? RecommendedFees { get; set; }

    /// <summary>
    /// Headroom over the network-recommended rate that automatic claims are allowed, in sat/vB.
    /// </summary>
    /// <remarks>
    /// The plugin configures Spark's claim ceiling as "network-recommended plus this", never as a fixed rate:
    /// a fixed rate low enough to be prudent today strands deposits in the next fee spike. Spark's own default
    /// is a fixed 1 sat/vB, which is below the mainnet floor essentially always.
    /// </remarks>
    public long ClaimFeeLeewaySatPerVbyte { get; set; }

    /// <summary>Ceiling on a manual claim, in satoshi.</summary>
    public long MaxManualClaimFeeSats { get; set; }

    /// <summary>True when the configured ceiling already looks too low for the current fee market.</summary>
    public bool ClaimPolicyLooksTooLow { get; set; }

    public static SparkDepositData From(SparkDepositView view) => new()
    {
        Address = view.Address,
        AddressError = view.AddressError,
        WalletRunning = view.WalletRunning,
        Deposits = view.Deposits.Select(SparkDepositEntryData.From).ToList(),
        RecommendedFees = view.RecommendedFees is { } fees
            ? new SparkRecommendedFeesData(
                fees.FastestFeeSatPerVbyte, fees.HalfHourFeeSatPerVbyte, fees.HourFeeSatPerVbyte,
                fees.EconomyFeeSatPerVbyte, fees.MinimumFeeSatPerVbyte)
            : null,
        ClaimFeeLeewaySatPerVbyte = view.Settings.EffectiveClaimFeeLeewaySatPerVbyte,
        MaxManualClaimFeeSats = view.Settings.EffectiveMaxManualClaimFeeSats,
        ClaimPolicyLooksTooLow = view.ClaimPolicyLooksTooLow
    };
}

/// <param name="RequiredFeeSats">
/// What the claim would actually cost. Present only when the claim failed on the fee ceiling, and the value a
/// claim call uses when none is supplied.
/// </param>
public sealed record SparkDepositEntryData(
    string TxId,
    uint Vout,
    long AmountSats,
    bool IsMature,
    bool NeedsAttention,
    string? ClaimError,
    long? RequiredFeeSats,
    long? RequiredFeeRateSatPerVbyte)
{
    public static SparkDepositEntryData From(SparkDepositInfo deposit) => new(
        deposit.TxId,
        deposit.Vout,
        deposit.AmountSats,
        deposit.IsMature,
        deposit.NeedsAttention,
        deposit.ClaimError?.Message,
        deposit.ClaimError?.RequiredFeeSats,
        deposit.ClaimError?.RequiredFeeRateSatPerVbyte);
}

public sealed record SparkRecommendedFeesData(
    long FastestFee,
    long HalfHourFee,
    long HourFee,
    long EconomyFee,
    long MinimumFee);

/// <summary>
/// Body of <c>POST /api/v1/stores/{storeId}/spark/deposit/claim</c>.
/// </summary>
public class SparkClaimDepositRequest
{
    /// <summary>Transaction id of the deposit, as reported by the deposit endpoint.</summary>
    public string? TxId { get; set; }

    /// <summary>Output index of the deposit.</summary>
    public uint Vout { get; set; }

    /// <summary>
    /// Ceiling for this claim, in satoshi. Omit to use the fee Spark said the claim needs.
    /// </summary>
    /// <remarks>
    /// Omitting it is the right answer almost always. Supplying a larger value does not make the claim more
    /// likely to succeed if the store's own limit is lower, and no value lifts the backstop that refuses to
    /// spend more than half a deposit on claiming it.
    /// </remarks>
    public long? MaxFeeSats { get; set; }
}

public class SparkClaimDepositResponse
{
    /// <summary><c>Claimed</c>, <c>Refused</c>, <c>Failed</c> or <c>Unavailable</c>.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public SparkClaimStatus Status { get; set; }

    /// <summary>True exactly when <see cref="Status"/> is <c>Claimed</c>.</summary>
    public bool Succeeded { get; set; }

    /// <summary>Always set, always fit for a merchant to read.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>The ceiling the claim was issued at, when one was.</summary>
    public long? FeeSats { get; set; }
}

#endregion

#region Stable Balance

/// <summary>
/// Response to the Stable Balance endpoints.
/// </summary>
public class SparkStableBalanceData
{
    /// <summary>What the store asked for.</summary>
    public bool DesiredActive { get; set; }

    /// <summary>
    /// The label the wallet reports as active, or null when stable balance is off there.
    /// </summary>
    /// <remarks>
    /// <b>This, not <see cref="DesiredActive"/>, is what the wallet is doing.</b> The two can disagree: the SDK
    /// caches the active label per wallet, so a store whose seed was replaced starts deactivated whatever its
    /// setting says. The plugin reports the disagreement rather than silently converting to fix it.
    /// </remarks>
    public string? ActiveLabel { get; set; }

    /// <summary>
    /// True when the setting and the wallet disagree, or cannot be shown to agree, and a re-apply would
    /// converge them.
    /// </summary>
    /// <remarks>
    /// Three conditions, and the two beyond the obvious one are the ones that used to read as success: a wallet
    /// whose state could not be read, and a wallet holding a stablecoin it reports nothing active for. See
    /// <see cref="HoldingUnmanagedBalance"/>.
    /// </remarks>
    public bool NeedsReapply { get; set; }

    /// <summary>
    /// True when the wallet holds a stablecoin balance while reporting nothing active to manage it.
    /// </summary>
    /// <remarks>
    /// Either the deactivation conversion is still running, or the wallet has lost its stable-balance
    /// configuration and the balance is stranded. The two are indistinguishable from outside, and re-applying
    /// is safe in both.
    /// </remarks>
    public bool HoldingUnmanagedBalance { get; set; }

    /// <summary>True when the wallet's own state could not be read, so nothing here should be read as agreement.</summary>
    public bool ActiveStateUnknown { get; set; }

    /// <summary>False on any network but mainnet, where this feature cannot work at all.</summary>
    public bool Available { get; set; }

    public bool WalletRunning { get; set; }

    /// <summary>Set when the wallet is running but its stable-balance state could not be read.</summary>
    public string? WalletError { get; set; }

    /// <summary>The token balance the wallet holds, or null when it holds none.</summary>
    public SparkTokenBalanceData? Balance { get; set; }

    /// <summary>
    /// Spark's own floor on a Bitcoin-to-token conversion, in satoshi, when it could be read.
    /// </summary>
    /// <remarks>
    /// A configured <c>autoConvertThresholdSats</c> below this is clamped <em>up</em> to it rather than
    /// honoured, so setting less does not do what it looks like it does.
    /// </remarks>
    public long? ConversionMinimumSats { get; set; }

    /// <summary>The stored configuration. The same shape <c>PUT</c> accepts.</summary>
    public StableBalanceInput Settings { get; set; } = new();

    /// <summary>What the last write did, when this is a write's response.</summary>
    public string? Message { get; set; }

    public static SparkStableBalanceData From(SparkStableBalanceView view, string? message = null) => new()
    {
        DesiredActive = view.DesiredActive,
        ActiveLabel = view.ActiveLabel,
        NeedsReapply = view.NeedsReapply,
        HoldingUnmanagedBalance = view.HoldingUnmanagedBalance,
        ActiveStateUnknown = view.ActiveStateUnknown,
        Available = !view.MainnetOnly,
        WalletRunning = view.WalletRunning,
        WalletError = view.WalletError,
        Balance = view.Balance is { } balance ? SparkTokenBalanceData.From(balance) : null,
        ConversionMinimumSats = view.ConversionMinimumSats,
        Settings = StableBalanceInput.From(view.Settings),
        Message = message
    };
}

/// <param name="BaseUnits">
/// <b>Base units as a decimal string, not satoshi and not a quantity.</b> $35.60 of a 6-decimal token is
/// <c>"35600000"</c>. A string because the underlying value is a <c>u128</c> and an 18-decimal token overflows
/// every JSON number a client is likely to parse it into.
/// </param>
/// <param name="Amount">The same figure as a human-readable quantity, e.g. <c>35.6</c>.</param>
/// <param name="IsFreezable">
/// Whether the issuer can freeze this balance. <b>True for USDB.</b> Reported rather than assumed, because it is
/// the counterparty risk a merchant accepts by holding one.
/// </param>
public sealed record SparkTokenBalanceData(
    string Identifier,
    string Ticker,
    string Name,
    string BaseUnits,
    string Amount,
    uint Decimals,
    bool IsFreezable)
{
    public static SparkTokenBalanceData From(SparkTokenBalance balance) => new(
        balance.Identifier.Value,
        balance.Ticker,
        balance.Name,
        balance.BaseUnits.ToString(System.Globalization.CultureInfo.InvariantCulture),
        SparkSendAmount.FormatBaseUnits(balance.BaseUnits, balance.Decimals),
        balance.Decimals,
        balance.IsFreezable);
}

#endregion

/// <summary>
/// A cross-chain quote, for a store sweeping to an EVM address.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="AmountInSats"/> is larger than the amount asked for.</b> The provider overpays the source leg
/// to absorb its fee and slippage, so a sweep sized to exactly the sweepable balance cannot be funded. The pad
/// is not derivable from a slippage setting; read it here.
/// </para>
/// <para>
/// The fee figures are in <b>destination-asset base units</b>, and <c>serviceFeeAsset</c> may name a third asset
/// again — <c>USDC</c> was observed on a <c>USDT</c> route. Do not add them, and do not assume either is in
/// satoshi.
/// </para>
/// <para>
/// There is <b>no arrival estimate</b>. The SDK exposes none, so nothing here can offer one.
/// </para>
/// </remarks>
public sealed record SparkCrossChainQuoteData(
    string Provider,
    string Chain,
    string? ChainId,
    string Asset,
    uint AssetDecimals,
    long AmountInSats,
    string AssetAmountIn,
    string EstimatedOut,
    string EstimatedOutAmount,
    string FeeAmount,
    string ServiceFeeAmount,
    string? ServiceFeeAsset,
    long SourceTransferFeeSats,
    DateTimeOffset ExpiresAt,
    string? ProviderQuoteId)
{
    public static SparkCrossChainQuoteData From(SparkCrossChainQuote quote) => new(
        quote.Route.Provider.ToString(),
        quote.Route.Chain,
        quote.Route.ChainId,
        quote.Route.Asset,
        quote.Route.Decimals,
        quote.AmountInSats,
        quote.AssetAmountIn.ToString(System.Globalization.CultureInfo.InvariantCulture),
        quote.EstimatedOut.ToString(System.Globalization.CultureInfo.InvariantCulture),
        quote.DescribeEstimatedOut(),
        quote.FeeAmount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        quote.ServiceFeeAmount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        quote.ServiceFeeAsset,
        quote.SourceTransferFeeSats,
        quote.ExpiresAt,
        quote.ProviderQuoteId);
}

#region Server settings

/// <summary>Flint server-level settings, shared across all stores on this BTCPay instance.</summary>
public class SparkServerSettingsData
{
    /// <summary>
    /// When set, Flint POSTs a <c>plugin.update-available</c> event here whenever a newer version is
    /// detected on the plugin registry. The check runs once per day.
    /// </summary>
    public string? UpdateWebhookUrl { get; set; }
}

/// <summary>Request body for <c>PUT /api/v1/server/spark</c>.</summary>
public class SparkServerSettingsRequest
{
    /// <summary>
    /// The URL to call when a plugin update is available, or null/empty to disable the notification.
    /// Must be a valid http or https URL when non-empty.
    /// </summary>
    public string? UpdateWebhookUrl { get; set; }
}

#endregion
