using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BTCPayServer.Plugins.Flint.Data;

/// <summary>
/// <see cref="IStablecoinQuoteStore"/> over the plugin's own Postgres schema.
/// </summary>
/// <remarks>
/// As in <see cref="EfInvoiceRecordStore"/>, nothing here opens an explicit transaction: the shared context factory
/// enables retry-on-failure, and EF's retrying execution strategy refuses user-initiated transactions. Atomicity
/// comes from single conditional statements, backed by the unique index on <c>SdkPaymentId</c>.
/// </remarks>
public class EfStablecoinQuoteStore : IStablecoinQuoteStore
{
    private readonly SparkPluginDbContextFactory _contextFactory;

    public EfStablecoinQuoteStore(SparkPluginDbContextFactory contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task AddAsync(StablecoinQuote quote, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(quote);

        await using var context = _contextFactory.CreateContext();
        context.StablecoinQuotes.Add(quote);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StablecoinQuote>> ListForInvoiceAsync(
        string invoiceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(invoiceId);

        await using var context = _contextFactory.CreateContext();
        return await context.StablecoinQuotes
            .AsNoTracking()
            .Where(q => q.InvoiceId == invoiceId)
            .OrderByDescending(q => q.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StablecoinQuote>> ListOpenAsync(
        string storeId,
        DateTimeOffset expiredAfter,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        await using var context = _contextFactory.CreateContext();
        return await context.StablecoinQuotes
            .AsNoTracking()
            .Where(q => q.StoreId == storeId && q.SdkPaymentId == null && q.ExpiresAt > expiredAfter)
            .OrderBy(q => q.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListStoresWithOpenQuotesAsync(
        DateTimeOffset expiredAfter,
        CancellationToken cancellationToken = default)
    {
        await using var context = _contextFactory.CreateContext();
        return await context.StablecoinQuotes
            .AsNoTracking()
            .Where(q => q.SdkPaymentId == null && q.ExpiresAt > expiredAfter)
            .Select(q => q.StoreId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public async Task<StablecoinQuote?> FindBySdkPaymentIdAsync(
        string storeId,
        string sdkPaymentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        ArgumentException.ThrowIfNullOrEmpty(sdkPaymentId);

        await using var context = _contextFactory.CreateContext();
        return await context.StablecoinQuotes
            .AsNoTracking()
            .FirstOrDefaultAsync(q => q.StoreId == storeId && q.SdkPaymentId == sdkPaymentId, cancellationToken);
    }

    public async Task<bool> TrySettleAsync(
        string quoteId,
        StablecoinSettlement settlement,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(quoteId);
        ArgumentNullException.ThrowIfNull(settlement);

        var paymentId = settlement.SdkPaymentId;
        var paid = settlement.PaidBaseUnits is { } p ? StablecoinQuote.FormatBaseUnits(p) : null;
        var delivered = settlement.DeliveredBaseUnits is { } d ? StablecoinQuote.FormatBaseUnits(d) : null;

        await using var context = _contextFactory.CreateContext();
        try
        {
            // One statement, two guards. The row must still be unsettled — the compare-and-set that makes a racing
            // event and poll settle it once — and the payment must not have settled any other quote, which is
            // what stops one arrival being attributed twice. The unique index is the backstop under the second.
            var updated = await context.StablecoinQuotes
                .Where(q => q.Id == quoteId
                            && q.SdkPaymentId == null
                            && !context.StablecoinQuotes.Any(other => other.SdkPaymentId == paymentId))
                .ExecuteUpdateAsync(setters => setters
                        .SetProperty(q => q.SdkPaymentId, paymentId)
                        .SetProperty(q => q.PaidBaseUnits, paid)
                        .SetProperty(q => q.DeliveredBaseUnits, delivered)
                        .SetProperty(q => q.ExternalTxHash, settlement.ExternalTxHash)
                        .SetProperty(q => q.ProviderOrderId, settlement.ProviderOrderId)
                        .SetProperty(q => q.ProviderQuoteId, settlement.ProviderQuoteId)
                        .SetProperty(q => q.SettledAt, settlement.SettledAt),
                    cancellationToken);
            return updated == 1;
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            return false;
        }
    }

    public async Task<bool> TryMarkCreditedAsync(
        string quoteId,
        DateTimeOffset creditedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(quoteId);

        await using var context = _contextFactory.CreateContext();
        var updated = await context.StablecoinQuotes
            .Where(q => q.Id == quoteId && q.SdkPaymentId != null && q.CreditedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(q => q.CreditedAt, creditedAt), cancellationToken);
        return updated == 1;
    }

    public async Task<IReadOnlyList<StablecoinQuote>> ListUncreditedAsync(
        string storeId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        await using var context = _contextFactory.CreateContext();
        return await context.StablecoinQuotes
            .AsNoTracking()
            .Where(q => q.StoreId == storeId && q.SdkPaymentId != null && q.CreditedAt == null)
            .OrderBy(q => q.SettledAt)
            .Take(Math.Max(1, limit))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListStoresAwaitingCreditAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = _contextFactory.CreateContext();
        return await context.StablecoinQuotes
            .AsNoTracking()
            .Where(q => q.SdkPaymentId != null && q.CreditedAt == null)
            .Select(q => q.StoreId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public async Task<int> DeleteFinishedAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
    {
        await using var context = _contextFactory.CreateContext();
        return await context.StablecoinQuotes
            .Where(q => (q.CreditedAt != null && q.CreditedAt < before)
                        || (q.SdkPaymentId == null && q.ExpiresAt < before))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                return true;
        }

        return false;
    }
}
