using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.Flint.Data;

/// <summary>
/// Append-only record of one Spark wallet movement: a Lightning receive, a stablecoin receive,
/// an outgoing payment, a cooperative-exit sweep, or a unilateral exit.
/// </summary>
/// <remarks>
/// <para>
/// Every financial event that changes the wallet's sats balance writes one row here.
/// The invariant is: <c>opening_balance + SUM(credits) - SUM(debits) = current_SDK_balance</c>,
/// where <see cref="SparkLedgerEntryType.LightningReceive"/>,
/// <see cref="SparkLedgerEntryType.StablecoinReceive"/>, and
/// <see cref="SparkLedgerEntryType.OnchainDeposit"/> are credits, and
/// <see cref="SparkLedgerEntryType.Send"/>, <see cref="SparkLedgerEntryType.Sweep"/>, and
/// <see cref="SparkLedgerEntryType.UnilateralExit"/> are debits.
/// </para>
/// <para>
/// <see cref="AmountMsat"/> is always positive; the direction is implied by <see cref="EntryType"/>.
/// Routing fees on sends are stored in <see cref="FeeMsat"/> and are excluded from
/// <see cref="AmountMsat"/> so the two columns sum to the total wallet debit.
/// </para>
/// </remarks>
public class SparkLedgerEntry
{
    /// <summary>GUID string; unique across the table.</summary>
    public string Id { get; set; } = null!;

    public string StoreId { get; set; } = null!;

    public SparkLedgerEntryType EntryType { get; set; }

    /// <summary>
    /// Principal amount in millisatoshi. Always positive; direction is implied by
    /// <see cref="EntryType"/>. For stablecoin receives this is the sats delivered to the wallet.
    /// </summary>
    public long AmountMsat { get; set; }

    /// <summary>
    /// Network/routing fee paid in millisatoshi. Zero for receives. For sends, the routing fee
    /// deducted from the wallet on top of <see cref="AmountMsat"/>.
    /// </summary>
    public long FeeMsat { get; set; }

    /// <summary>
    /// When the payment was finalised in the Spark wallet. For receives this is
    /// <c>payment.Timestamp</c> from the SDK. For sends it is when the payment succeeded.
    /// </summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>When this row was inserted.</summary>
    public DateTimeOffset RecordedAt { get; set; }

    /// <summary>The SDK's own payment id. Present for all types that have one.</summary>
    public string? SdkPaymentId { get; set; }

    /// <summary>
    /// Lightning payment hash (lower-case hex). Set for <see cref="SparkLedgerEntryType.LightningReceive"/>
    /// and <see cref="SparkLedgerEntryType.Send"/>; foreign-key-equivalent to <c>InvoiceRecord.PaymentHash</c>.
    /// </summary>
    public string? PaymentHash { get; set; }

    /// <summary>
    /// SDK payment description (e.g. "Bitrefill &lt;order-id&gt;").
    /// Preserved for downstream WooCommerce order correlation.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// BTCPay invoice id. Set for <see cref="SparkLedgerEntryType.LightningReceive"/> and
    /// <see cref="SparkLedgerEntryType.StablecoinReceive"/>.
    /// </summary>
    public string? BtcPayInvoiceId { get; set; }

    /// <summary>
    /// Foreign key to <c>StablecoinQuotes.Id</c>. Set only for
    /// <see cref="SparkLedgerEntryType.StablecoinReceive"/>.
    /// </summary>
    public string? StablecoinQuoteId { get; set; }

    /// <summary>Source chain of the stablecoin deposit (e.g. <c>ethereum</c>, <c>solana</c>).</summary>
    public string? StablecoinChain { get; set; }

    /// <summary>Source asset symbol (e.g. <c>USDC</c>, <c>USDT</c>).</summary>
    public string? StablecoinAsset { get; set; }

    /// <summary>
    /// What the payer deposited in the source asset's base units, as a decimal string
    /// (e.g. <c>"49111281"</c> for 49.111281 USDC with 6 decimals). Nullable because it
    /// is not available until <c>PaymentMetadataUpdated</c>.
    /// </summary>
    public string? StablecoinPaidBaseUnits { get; set; }

    /// <summary>The payer's transaction on <see cref="StablecoinChain"/>.</summary>
    public string? StablecoinExternalTxHash { get; set; }

    /// <summary>
    /// BTC/EUR rate at the moment the BTCPay invoice was created, in EUR per BTC.
    /// Null for entries written by the backfill job when the invoice is no longer available.
    /// </summary>
    public decimal? InvoiceRateEur { get; set; }

    /// <summary>
    /// BTC/RON rate at the moment the BTCPay invoice was created, in RON per BTC.
    /// Null for entries written by the backfill job when the invoice is no longer available.
    /// </summary>
    public decimal? InvoiceRateRon { get; set; }

    /// <summary>
    /// Source of <see cref="InvoiceRateEur"/> and <see cref="InvoiceRateRon"/> (e.g.
    /// <c>"btcpay_invoice"</c>). Null when both rates are null.
    /// </summary>
    public string? InvoiceRateSource { get; set; }

    /// <summary>
    /// Timestamp at which the rate was sampled. For <c>btcpay_invoice</c> this is the invoice
    /// creation time.
    /// </summary>
    public DateTimeOffset? InvoiceRateAt { get; set; }

    /// <summary>
    /// True when this entry was written by the backfill job rather than the live write path.
    /// Used to distinguish historical population from real-time records.
    /// </summary>
    public bool IsBackfill { get; set; }
}

/// <summary>What kind of Spark wallet movement the row records.</summary>
public enum SparkLedgerEntryType
{
    /// <summary>A Lightning invoice was paid and credited to the store.</summary>
    LightningReceive = 0,

    /// <summary>A stablecoin deposit was converted and credited to the store.</summary>
    StablecoinReceive = 1,

    /// <summary>An outgoing Lightning payment was made from the store's wallet.</summary>
    Send = 2,

    /// <summary>A cooperative-exit sweep was sent on-chain.</summary>
    Sweep = 3,

    /// <summary>A unilateral exit transaction was broadcast.</summary>
    UnilateralExit = 4,

    /// <summary>
    /// An on-chain deposit to the wallet's static Bitcoin address was claimed.
    /// AmountSats = net credited after claim fee (FeeSats = on-chain claim fee).
    /// Identifier: SdkPaymentId + Description carries txid:vout (no payment hash exists).
    /// </summary>
    OnchainDeposit = 5,
}

/// <summary>
/// Append-only store for <see cref="SparkLedgerEntry"/> rows. Only inserts are supported;
/// rows are never updated or deleted.
/// </summary>
public interface ISparkLedgerStore
{
    /// <summary>Writes one entry. Callers are responsible for idempotency guards.</summary>
    Task AppendAsync(SparkLedgerEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns true when any entry with the given <paramref name="sdkPaymentId"/> already exists
    /// for <paramref name="storeId"/>. Used as the idempotency guard on live write paths.
    /// </summary>
    Task<bool> ExistsForSdkPaymentAsync(
        string storeId,
        string sdkPaymentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns true when any entry with the given <paramref name="paymentHash"/> already exists
    /// for <paramref name="storeId"/>. Used for Lightning receives keyed only by hash.
    /// </summary>
    Task<bool> ExistsForPaymentHashAsync(
        string storeId,
        string paymentHash,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pages through the store's ledger, newest first.
    /// </summary>
    Task<IReadOnlyList<SparkLedgerEntry>> ListAsync(
        string storeId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>Total number of entries for <paramref name="storeId"/>.</summary>
    Task<long> CountAsync(string storeId, CancellationToken cancellationToken = default);
}
