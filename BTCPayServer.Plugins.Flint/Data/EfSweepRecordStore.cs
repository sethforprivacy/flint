using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Flint.Data;

/// <summary>
/// <see cref="ISweepRecordStore"/> over the plugin's own Postgres schema.
/// </summary>
/// <remarks>
/// As in <see cref="EfInvoiceRecordStore"/>, nothing here may open an explicit transaction: the shared context
/// factory enables retry-on-failure, and EF's retrying execution strategy refuses user-initiated transactions.
/// Atomicity comes from single conditional statements.
/// </remarks>
public class EfSweepRecordStore : ISweepRecordStore
{
    /// <summary>
    /// Postgres collation used for the idempotency-key tie-break.
    /// </summary>
    /// <remarks>
    /// Named explicitly rather than left to the database's default, so that ordering is byte order on both
    /// implementations. The in-memory store compares with <c>StringComparer.Ordinal</c>, and under an ICU default
    /// collation Postgres would disagree with it on hyphenated UUID keys — which is exactly the kind of divergence
    /// the store contract exists to catch, and would otherwise catch only intermittently and only on one machine's
    /// locale. "C" is always present in Postgres.
    /// </remarks>
    private const string ByteOrderCollation = "C";

    private readonly SparkPluginDbContextFactory _contextFactory;

    public EfSweepRecordStore(SparkPluginDbContextFactory contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task AddAsync(SweepRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrEmpty(record.IdempotencyKey);
        ArgumentException.ThrowIfNullOrEmpty(record.StoreId);

        await using var context = _contextFactory.CreateContext();
        context.SweepRecords.Add(record);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<SweepRecord?> GetAsync(
        string storeId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        await using var context = _contextFactory.CreateContext();
        return await context.SweepRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.IdempotencyKey == idempotencyKey && r.StoreId == storeId, cancellationToken);
    }

    public async Task<IReadOnlyList<SweepRecord>> ListAsync(
        string storeId,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var context = _contextFactory.CreateContext();
        return await context.SweepRecords
            .AsNoTracking()
            .Where(r => r.StoreId == storeId)
            // The idempotency key breaks ties: two rows created in the same tick would otherwise be free to
            // swap places between pages, so one of them could appear twice and another never.
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => EF.Functions.Collate(r.IdempotencyKey, ByteOrderCollation))
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SweepRecord>> ListForReportAsync(
        string storeId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        // Npgsql writes only UTC offsets to timestamptz, and BTCPay hands a report the bounds of whatever timezone
        // the merchant (or the Greenfield caller's timezone: filter) picked.
        var fromUtc = from.ToUniversalTime();
        var toUtc = to.ToUniversalTime();

        await using var context = _contextFactory.CreateContext();
        return await context.SweepRecords
            .AsNoTracking()
            .Where(r => r.StoreId == storeId
                        && r.CreatedAt >= fromUtc
                        && r.CreatedAt <= toUtc
                        && r.Status != SweepRecordStatus.Refused)
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => EF.Functions.Collate(r.IdempotencyKey, ByteOrderCollation))
            .ToListAsync(cancellationToken);
    }

    public async Task<int> CountAsync(string storeId, CancellationToken cancellationToken = default)
    {
        await using var context = _contextFactory.CreateContext();
        return await context.SweepRecords.CountAsync(r => r.StoreId == storeId, cancellationToken);
    }

    public async Task<IReadOnlyList<SweepRecord>> ListUnresolvedAsync(
        string storeId,
        DateTimeOffset sentCreatedAfter,
        CancellationToken cancellationToken = default)
    {
        await using var context = _contextFactory.CreateContext();
        return await context.SweepRecords
            .AsNoTracking()
            // Pending is unbounded and Sent is age-bounded, for the reason on the interface: a Pending row is the
            // one thing that must be chased until Spark answers, however long the wallet stays down. One
            // exception to the age bound: a cross-chain Sent row whose conversion has not reached a terminal
            // state. The conversion's outcome has no event — it is learned solely from this poll — so ageing
            // such a row out would strand a pending conversion silently and a needed refund would never be
            // requested, however old the row grows.
            .Where(r => r.StoreId == storeId
                        && (r.Status == SweepRecordStatus.Pending
                            || (r.Status == SweepRecordStatus.Sent
                                && (r.CreatedAt > sentCreatedAfter
                                    || (r.DestinationKind == SweepDestinationKind.EvmAddress
                                        && r.ConversionStatus != SparkConversionStatus.Completed
                                        && r.ConversionStatus != SparkConversionStatus.Failed
                                        && r.ConversionStatus != SparkConversionStatus.Refunded)))))
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => EF.Functions.Collate(r.IdempotencyKey, ByteOrderCollation))
            .ToListAsync(cancellationToken);
    }

    public async Task<SweepRecord?> FindOpenRefusalAsync(
        string storeId,
        SweepRefusalCode code,
        SweepDestinationMode mode,
        DateTimeOffset activeSince,
        CancellationToken cancellationToken = default)
    {
        if (code is SweepRefusalCode.None)
            return null;

        await using var context = _contextFactory.CreateContext();
        return await context.SweepRecords
            .AsNoTracking()
            .Where(r => r.StoreId == storeId
                        && r.Status == SweepRecordStatus.Refused
                        && r.Trigger == SweepTrigger.Automatic
                        && r.RefusalCode == code
                        && r.DestinationMode == mode
                        && (r.LastSeenAt ?? r.CreatedAt) >= activeSince)
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => EF.Functions.Collate(r.IdempotencyKey, ByteOrderCollation))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> TryRecordRepeatRefusalAsync(
        string storeId,
        string idempotencyKey,
        string error,
        DateTimeOffset seenAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);

        await using var context = _contextFactory.CreateContext();

        // One conditional UPDATE, guarded on the row still being a refusal, so this can never touch a row that
        // describes a real send. The increment is computed in SQL rather than read-modify-written, so two passes
        // cannot lose a sighting.
        var updated = await context.SweepRecords
            .Where(r => r.IdempotencyKey == idempotencyKey
                        && r.StoreId == storeId
                        && r.Status == SweepRecordStatus.Refused)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(r => r.AttemptCount, r => r.AttemptCount + 1)
                    .SetProperty(r => r.LastSeenAt, seenAt)
                    // Refreshed, so the figures a merchant reads are the current ones rather than the ones from
                    // whenever the condition started.
                    .SetProperty(r => r.Error, error),
                cancellationToken);

        return updated == 1;
    }

    public async Task<bool> TryRecordProviderQuoteAsync(
        string storeId,
        string idempotencyKey,
        string providerQuoteId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);
        ArgumentException.ThrowIfNullOrEmpty(providerQuoteId);

        await using var context = _contextFactory.CreateContext();

        var updated = await context.SweepRecords
            .Where(r => r.IdempotencyKey == idempotencyKey
                        && r.StoreId == storeId
                        && r.Status == SweepRecordStatus.Pending)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(r => r.ProviderQuoteId, providerQuoteId),
                cancellationToken);

        return updated == 1;
    }

    public async Task<bool> TryResolveAsync(
        string storeId,
        string idempotencyKey,
        IReadOnlyCollection<SweepRecordStatus> allowedFrom,
        SweepResolution resolution,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);
        ArgumentNullException.ThrowIfNull(resolution);
        if (allowedFrom is null || allowedFrom.Count == 0)
            throw new ArgumentException("At least one source status is required.", nameof(allowedFrom));

        // Materialised so the provider translates it to an IN list rather than closing over the interface.
        var from = allowedFrom.ToArray();
        var status = resolution.Status;
        var completedAt = resolution.CompletedAt;
        var fee = resolution.FeeSats;
        var txId = resolution.TxId;
        var error = resolution.Error;
        var refusalCode = resolution.RefusalCode;
        var conversionStatus = resolution.ConversionStatus;
        var delivered = resolution.DeliveredAmountBaseUnits;
        var providerOrderId = resolution.ProviderOrderId;

        await using var context = _contextFactory.CreateContext();

        // One conditional UPDATE, so the database decides the winner. Two callers cannot both be told they
        // resolved the same sweep, which is what keeps a crash-recovery pass from racing a retry.
        var updated = await context.SweepRecords
            .Where(r => r.IdempotencyKey == idempotencyKey
                        && r.StoreId == storeId
                        && from.Contains(r.Status))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(r => r.Status, status)
                    .SetProperty(r => r.CompletedAt, completedAt)
                    // Coalesced, not assigned: a Sent → Confirmed step learns nothing new about the txid or the
                    // fee, and overwriting them with null would erase the only record of which transaction holds
                    // the merchant's money.
                    .SetProperty(r => r.FeeSats, r => fee ?? r.FeeSats)
                    .SetProperty(r => r.TxId, r => txId ?? r.TxId)
                    .SetProperty(r => r.Error, r => error ?? r.Error)
                    .SetProperty(
                        r => r.RefusalCode,
                        r => refusalCode == SweepRefusalCode.None ? r.RefusalCode : refusalCode)
                    // Coalesced for the same reason as the txid: the provider's state and the delivered amount
                    // arrive late and through no event, so a poll that could not read them must not erase what
                    // an earlier one did.
                    .SetProperty(r => r.ConversionStatus, r => conversionStatus ?? r.ConversionStatus)
                    .SetProperty(
                        r => r.DeliveredAmountBaseUnits, r => delivered ?? r.DeliveredAmountBaseUnits)
                    // The bridge provider's own order id — the handle an investigation into a stuck delivery
                    // quotes at the provider. Coalesced like the rest; it arrives once and must survive
                    // every later Sent → Confirmed poll.
                    .SetProperty(r => r.ProviderOrderId, r => providerOrderId ?? r.ProviderOrderId),
                cancellationToken);

        return updated == 1;
    }
}
