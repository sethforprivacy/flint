using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Flint.Data;

/// <summary>
/// Durable record of the USDC/USDT quotes shown to payers, and of which Spark payment settled each.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exactly-once is the database's job here too.</b> <see cref="TrySettleAsync"/> is a compare-and-set on an
/// unsettled row, and the SDK payment id is unique across the table, so a payment event, a metadata event and a
/// reconciliation pass racing on the same receive settle one quote once — and one payment can never settle two
/// quotes. The BTCPay credit that follows is keyed on the same payment id, so it cannot land twice either.
/// </para>
/// <para>
/// Nothing here opens an explicit transaction, for the reason given on <see cref="EfOutgoingPaymentStore"/>.
/// </para>
/// </remarks>
public interface IStablecoinQuoteStore
{
    Task AddAsync(StablecoinQuote quote, CancellationToken cancellationToken = default);

    /// <summary>Every quote made for one invoice, newest first.</summary>
    Task<IReadOnlyList<StablecoinQuote>> ListForInvoiceAsync(
        string invoiceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The store's unsettled quotes whose expiry is after <paramref name="expiredAfter"/>: every quote a receive
    /// arriving now could have come from, and so every quote a new one must not be confused with.
    /// </summary>
    Task<IReadOnlyList<StablecoinQuote>> ListOpenAsync(
        string storeId,
        DateTimeOffset expiredAfter,
        CancellationToken cancellationToken = default);

    /// <summary>Stores holding at least one quote <see cref="ListOpenAsync"/> would return.</summary>
    Task<IReadOnlyList<string>> ListStoresWithOpenQuotesAsync(
        DateTimeOffset expiredAfter,
        CancellationToken cancellationToken = default);

    /// <summary>The quote a Spark payment settled, if it settled one.</summary>
    Task<StablecoinQuote?> FindBySdkPaymentIdAsync(
        string storeId,
        string sdkPaymentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a settlement on an unsettled quote. False when the quote was already settled, or when this payment
    /// already settled another quote.
    /// </summary>
    Task<bool> TrySettleAsync(
        string quoteId,
        StablecoinSettlement settlement,
        CancellationToken cancellationToken = default);

    /// <summary>Marks a settled quote as recorded on its BTCPay invoice. False when it already was.</summary>
    Task<bool> TryMarkCreditedAsync(
        string quoteId,
        DateTimeOffset creditedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Settled quotes whose BTCPay credit has not landed yet, oldest first.</summary>
    Task<IReadOnlyList<StablecoinQuote>> ListUncreditedAsync(
        string storeId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Stores holding a settled quote whose BTCPay credit has not landed yet.</summary>
    Task<IReadOnlyList<string>> ListStoresAwaitingCreditAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes finished quotes: those credited before <paramref name="before"/>, and unsettled ones that expired
    /// before it. A settled quote whose credit never landed is kept. Returns how many were deleted.
    /// </summary>
    Task<int> DeleteFinishedAsync(DateTimeOffset before, CancellationToken cancellationToken = default);
}
