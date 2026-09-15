using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BTCPayServer.Plugins.Flint.Data;

/// <summary>
/// <see cref="IUnilateralExitRecordStore"/> over the plugin's own Postgres schema.
/// </summary>
/// <remarks>
/// As in <see cref="EfSweepRecordStore"/>, nothing here may open an explicit transaction: the shared context
/// factory enables retry-on-failure, and EF's retrying execution strategy refuses user-initiated transactions.
/// Atomicity comes from single conditional statements and from one unique index.
/// </remarks>
public class EfUnilateralExitRecordStore : IUnilateralExitRecordStore
{
    /// <summary>
    /// Postgres collation used for the id tie-break.
    /// </summary>
    /// <remarks>
    /// Named explicitly for the same reason as <see cref="EfSweepRecordStore"/>'s: ordering has to be byte order
    /// so that this implementation and any in-memory one agree on hyphenated UUIDs, which an ICU default
    /// collation would not. "C" is always present in Postgres.
    /// </remarks>
    private const string ByteOrderCollation = "C";

    /// <summary>
    /// SQLSTATE for <c>unique_violation</c>.
    /// </summary>
    /// <remarks>
    /// Matched on the code and then on the constraint name, not on the message: the message is localised by the
    /// server's <c>lc_messages</c> and would make this behave differently on a non-English database.
    /// </remarks>
    private const string UniqueViolation = "23505";

    private readonly SparkPluginDbContextFactory _contextFactory;

    public EfUnilateralExitRecordStore(SparkPluginDbContextFactory contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<bool> CreateAsync(
        UnilateralExitRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrEmpty(record.Id);
        ArgumentException.ThrowIfNullOrEmpty(record.StoreId);

        await using var context = _contextFactory.CreateContext();
        context.UnilateralExitRecords.Add(record);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (IsActiveExitCollision(ex))
        {
            // The store already has an exit awaiting funding or built. An ordinary race rather than a fault —
            // see the interface — so it comes back as a refusal the service can word for a merchant. Note that
            // this deliberately does not catch a primary-key collision: a reused id is a programming error.
            return false;
        }
    }

    public async Task<bool> UpdateAsync(
        UnilateralExitRecord record,
        UnilateralExitStatus expectedStatus,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrEmpty(record.Id);
        ArgumentException.ThrowIfNullOrEmpty(record.StoreId);

        // Read out of the entity before the query, so the expression tree closes over values rather than over a
        // tracked instance the provider would then try to translate.
        var id = record.Id;
        var storeId = record.StoreId;
        var from = expectedStatus;
        var status = record.Status;
        var updatedUtc = record.UpdatedUtc;
        var recoverable = record.RecoverableValueSat;
        var totalFee = record.TotalFeeSat;
        var funding = record.SingleUtxoFundingSat;
        var fundingUtxosJson = record.FundingUtxosJson;
        var transactionsJson = record.TransactionsJson;
        var lastError = record.LastError;

        await using var context = _contextFactory.CreateContext();

        // One conditional UPDATE, store-scoped and guarded on the status the caller read, touching only the
        // mutable half of the row: the identity columns are what the operator approved and funded against, and
        // the signed transactions are only meaningful relative to them, so they are not in the setter list.
        var updated = await context.UnilateralExitRecords
            .Where(r => r.Id == id && r.StoreId == storeId && r.Status == from)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(r => r.Status, status)
                    .SetProperty(r => r.UpdatedUtc, updatedUtc)
                    // Assigned rather than coalesced: a build re-quotes with the pinned leaf set, and the second
                    // quote's figures are the ones the operator is funding against from then on.
                    .SetProperty(r => r.RecoverableValueSat, recoverable)
                    .SetProperty(r => r.TotalFeeSat, totalFee)
                    .SetProperty(r => r.SingleUtxoFundingSat, funding)
                    // Coalesced, not assigned. These two are the exit itself — the signed transactions and the
                    // outpoint they spend — and every caller that writes a status or an error is entitled to
                    // know nothing about them, including a history row that was projected without them.
                    .SetProperty(r => r.FundingUtxosJson, r => fundingUtxosJson ?? r.FundingUtxosJson)
                    .SetProperty(r => r.TransactionsJson, r => transactionsJson ?? r.TransactionsJson)
                    // An assignment, so a build that gets further clears the previous attempt's complaint
                    // instead of leaving it on the page next to a successful result.
                    .SetProperty(r => r.LastError, lastError),
                cancellationToken);

        return updated == 1;
    }

    public async Task<UnilateralExitRecord?> GetAsync(
        string storeId,
        string id,
        CancellationToken cancellationToken = default)
    {
        await using var context = _contextFactory.CreateContext();
        return await context.UnilateralExitRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.StoreId == storeId, cancellationToken);
    }

    public async Task<UnilateralExitRecord?> GetActiveForStoreAsync(
        string storeId,
        CancellationToken cancellationToken = default)
    {
        await using var context = _contextFactory.CreateContext();
        return await context.UnilateralExitRecords
            .AsNoTracking()
            // The two non-terminal statuses, spelled out because EF cannot translate
            // UnilateralExitRecord.IsActive. Adding a status means changing this, the index filter in
            // SparkPluginDbContext, and the property.
            .Where(r => r.StoreId == storeId
                        && (r.Status == UnilateralExitStatus.AwaitingFunding
                            || r.Status == UnilateralExitStatus.Built))
            .OrderByDescending(r => r.CreatedUtc)
            .ThenByDescending(r => EF.Functions.Collate(r.Id, ByteOrderCollation))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UnilateralExitRecord>> ListTerminalForStoreAsync(
        string storeId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var context = _contextFactory.CreateContext();

        // Projected into an anonymous type first and assembled below, rather than selected into the entity: EF
        // will not construct a mapped entity inside a query, and the point of the projection is to keep the three
        // JSON columns out of the SELECT list. See the interface for why that matters on this table.
        var rows = await context.UnilateralExitRecords
            .AsNoTracking()
            .Where(r => r.StoreId == storeId
                        && (r.Status == UnilateralExitStatus.Completed
                            || r.Status == UnilateralExitStatus.Abandoned))
            // The id breaks ties: two exits created in the same tick would otherwise be free to swap places
            // between reads, so one could appear twice and another never.
            .OrderByDescending(r => r.CreatedUtc)
            .ThenByDescending(r => EF.Functions.Collate(r.Id, ByteOrderCollation))
            .Take(limit)
            .Select(r => new
            {
                r.Id,
                r.StoreId,
                r.Status,
                r.CreatedUtc,
                r.UpdatedUtc,
                r.DestinationAddress,
                r.FeeRateSatPerVbyte,
                r.RecoverableValueSat,
                r.TotalFeeSat,
                r.SingleUtxoFundingSat,
                r.FundingAddress,
                r.FundingKeyIndex,
                r.LastError
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new UnilateralExitRecord
            {
                Id = row.Id,
                StoreId = row.StoreId,
                Status = row.Status,
                CreatedUtc = row.CreatedUtc,
                UpdatedUtc = row.UpdatedUtc,
                DestinationAddress = row.DestinationAddress,
                FeeRateSatPerVbyte = row.FeeRateSatPerVbyte,
                // Not loaded, and empty rather than null so a caller reading it gets a well-formed "no ids"
                // instead of a NullReferenceException far from here.
                LeafIdsJson = string.Empty,
                RecoverableValueSat = row.RecoverableValueSat,
                TotalFeeSat = row.TotalFeeSat,
                SingleUtxoFundingSat = row.SingleUtxoFundingSat,
                FundingAddress = row.FundingAddress,
                FundingKeyIndex = row.FundingKeyIndex,
                LastError = row.LastError
            })
            .ToList();
    }

    public async Task<long> NextFundingKeyIndexAsync(
        string storeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        await using var context = _contextFactory.CreateContext();

        // MAX over a nullable projection, so an empty set comes back as null rather than throwing — EF's
        // MaxAsync over a non-nullable long has no answer for "no rows".
        var highest = await context.UnilateralExitRecords
            .Where(r => r.StoreId == storeId)
            .Select(r => (long?)r.FundingKeyIndex)
            .MaxAsync(cancellationToken);

        return (highest ?? -1) + 1;
    }

    /// <summary>
    /// Whether a save failed on the "one active exit per store" index rather than on anything else.
    /// </summary>
    /// <remarks>
    /// Matched by constraint name, because the primary key raises the same SQLSTATE and means something entirely
    /// different — a reused id, which must not be reported to a merchant as "you already have an exit running".
    /// </remarks>
    private static bool IsActiveExitCollision(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: UniqueViolation,
            ConstraintName: SparkPluginDbContext.ActiveUnilateralExitIndexName
        };
}
