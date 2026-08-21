using BTCPayServer.Plugins.Flint.Data;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IUnilateralExitRecordStore"/> with the same observable semantics as the EF one.
/// </summary>
/// <remarks>
/// <para>
/// Held to <c>UnilateralExitRecordStoreContractTests</c> alongside the production store, because the exit
/// service's tests run against this and mean nothing if the two disagree. Three divergences would matter most,
/// and each is reproduced deliberately below: <see cref="UpdateAsync"/> must leave the identity columns alone,
/// or a service test would happily "prove" that a build can rewrite the destination the operator approved; it
/// must honour the expected-from status, or a service test could not distinguish a compare-and-set from a
/// blind write; and <see cref="CreateAsync"/> must refuse a second active exit, because in production that is a
/// unique index rather than a service-side check.
/// </para>
/// <para>
/// Records are copied on the way in and on the way out. The service mutates its own copy of a record before
/// handing it to <see cref="UpdateAsync"/> — that is the intended usage — and a store handing out live references
/// would let those mutations land in storage without any write at all, hiding an update that never happened.
/// </para>
/// </remarks>
public sealed class InMemoryUnilateralExitRecordStore : IUnilateralExitRecordStore
{
    private readonly WriteLog? _writeLog;
    private readonly Dictionary<string, UnilateralExitRecord> _records = [];

    public InMemoryUnilateralExitRecordStore(WriteLog? writeLog = null)
    {
        _writeLog = writeLog;
    }

    /// <summary>Thrown by <see cref="CreateAsync"/> when set: the quote could not be recorded.</summary>
    public Exception? FailCreateWith { get; set; }

    /// <summary>Makes <see cref="UpdateAsync"/> report that it changed nothing, as a vanished row would.</summary>
    public bool RefuseUpdates { get; set; }

    /// <summary>The live rows. Read them; do not mutate through them.</summary>
    public IReadOnlyDictionary<string, UnilateralExitRecord> Records => _records;

    public UnilateralExitRecord? Single() => _records.Count == 1 ? Copy(_records.Values.First()) : null;

    public Task<bool> CreateAsync(UnilateralExitRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        // The EF store's own guards. Omitting them would let this one insert a row with an empty store id where
        // the real one throws, which is exactly the divergence the shared contract exists to catch.
        ArgumentException.ThrowIfNullOrEmpty(record.Id);
        ArgumentException.ThrowIfNullOrEmpty(record.StoreId);

        // Observed, as Npgsql observes it: a cancelled token means the write does not happen. The service relies
        // on that being true, which is why it passes CancellationToken.None for the one write it must never skip.
        cancellationToken.ThrowIfCancellationRequested();

        if (FailCreateWith is not null)
            throw FailCreateWith;

        // The partial unique index, in memory: unique on the store, filtered to the two non-terminal statuses.
        // A refusal rather than an exception, matching how the EF store translates Postgres's unique violation.
        if (record.IsActive &&
            _records.Values.Any(r => r.StoreId == record.StoreId && r.IsActive))
        {
            return Task.FromResult(false);
        }

        if (!_records.TryAdd(record.Id, Copy(record)))
            throw new InvalidOperationException($"A unilateral exit already exists with id {record.Id}.");

        _writeLog?.Record($"exit:create:{record.Id}");
        return Task.FromResult(true);
    }

    public Task<bool> UpdateAsync(
        UnilateralExitRecord record,
        UnilateralExitStatus expectedStatus,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrEmpty(record.Id);
        ArgumentException.ThrowIfNullOrEmpty(record.StoreId);

        // See CreateAsync: a cancelled token means no write, which is what makes the service's use of
        // CancellationToken.None after a successful build load-bearing rather than decorative.
        cancellationToken.ThrowIfCancellationRequested();

        if (RefuseUpdates ||
            !_records.TryGetValue(record.Id, out var stored) ||
            stored.StoreId != record.StoreId ||
            // The compare-and-set. Whatever the caller read is the only status this write may overwrite.
            stored.Status != expectedStatus)
        {
            return Task.FromResult(false);
        }

        // The mutable half only, matching the EF store's setter list. Everything absent from it — store, creation
        // time, destination, fee rate, leaf ids, funding address, funding key index — is what the operator funded
        // against.
        stored.Status = record.Status;
        stored.UpdatedUtc = record.UpdatedUtc;
        stored.RecoverableValueSat = record.RecoverableValueSat;
        stored.TotalFeeSat = record.TotalFeeSat;
        stored.SingleUtxoFundingSat = record.SingleUtxoFundingSat;
        // Coalesced, matching the EF store: these two hold the exit's only copy of its signed transactions and
        // the outpoint they spend, and a caller writing a status or an error knows nothing about them.
        stored.FundingUtxosJson = record.FundingUtxosJson ?? stored.FundingUtxosJson;
        stored.TransactionsJson = record.TransactionsJson ?? stored.TransactionsJson;
        // An assignment and not a coalesce: a build that gets further has to be able to clear the previous
        // attempt's complaint.
        stored.LastError = record.LastError;

        _writeLog?.Record($"exit:update:{record.Id}:{record.Status}");
        return Task.FromResult(true);
    }

    public Task<UnilateralExitRecord?> GetAsync(
        string storeId,
        string id,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            _records.TryGetValue(id, out var record) && record.StoreId == storeId ? Copy(record) : null);

    public Task<UnilateralExitRecord?> GetActiveForStoreAsync(
        string storeId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Newest(_records.Values.Where(r => r.StoreId == storeId && r.IsActive)));

    public Task<IReadOnlyList<UnilateralExitRecord>> ListTerminalForStoreAsync(
        string storeId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return Task.FromResult<IReadOnlyList<UnilateralExitRecord>>(Ordered(
                _records.Values.Where(r => r.StoreId == storeId && !r.IsActive))
            .Take(limit)
            .Select(Project)
            .ToList());
    }

    public Task<long> NextFundingKeyIndexAsync(
        string storeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        // Every row of the store, terminal ones included, so an index is never reused — see the interface.
        var rows = _records.Values.Where(r => r.StoreId == storeId).ToList();
        return Task.FromResult(rows.Count == 0 ? 0 : rows.Max(r => r.FundingKeyIndex) + 1);
    }

    private static UnilateralExitRecord? Newest(IEnumerable<UnilateralExitRecord> candidates)
    {
        var found = Ordered(candidates).FirstOrDefault();
        return found is null ? null : Copy(found);
    }

    /// <remarks>
    /// Newest first, ties broken by id in <em>byte</em> order — <see cref="StringComparer.Ordinal"/> — to match
    /// the "C" collation the EF store names for exactly this reason. An ICU-style comparison would order
    /// hyphenated UUIDs differently and the two implementations would disagree on nothing that matters until they
    /// did.
    /// </remarks>
    private static IOrderedEnumerable<UnilateralExitRecord> Ordered(
        IEnumerable<UnilateralExitRecord> candidates) =>
        candidates
            .OrderByDescending(r => r.CreatedUtc)
            .ThenByDescending(r => r.Id, StringComparer.Ordinal);

    /// <summary>
    /// A detached copy of a row.
    /// </summary>
    /// <remarks>
    /// Hand-written, so it can silently drop a column — and on this table a dropped column is a merchant's only
    /// copy of signed transactions. The contract's round-trip test is what catches that.
    /// </remarks>
    internal static UnilateralExitRecord Copy(UnilateralExitRecord source) => new()
    {
        Id = source.Id,
        StoreId = source.StoreId,
        Status = source.Status,
        CreatedUtc = source.CreatedUtc,
        UpdatedUtc = source.UpdatedUtc,
        DestinationAddress = source.DestinationAddress,
        FeeRateSatPerVbyte = source.FeeRateSatPerVbyte,
        LeafIdsJson = source.LeafIdsJson,
        RecoverableValueSat = source.RecoverableValueSat,
        TotalFeeSat = source.TotalFeeSat,
        SingleUtxoFundingSat = source.SingleUtxoFundingSat,
        FundingAddress = source.FundingAddress,
        FundingKeyIndex = source.FundingKeyIndex,
        FundingUtxosJson = source.FundingUtxosJson,
        TransactionsJson = source.TransactionsJson,
        LastError = source.LastError
    };

    /// <summary>
    /// A history row: everything except the three JSON columns, which the EF store does not select.
    /// </summary>
    /// <remarks>
    /// Reproduced rather than glossed over. If this handed back the blobs, a service test could read a
    /// transaction set off a history row that production would report as null — and, worse, hand that row back to
    /// <see cref="UpdateAsync"/> without discovering that the coalescing is what makes it safe.
    /// </remarks>
    private static UnilateralExitRecord Project(UnilateralExitRecord source)
    {
        var row = Copy(source);
        row.LeafIdsJson = string.Empty;
        row.FundingUtxosJson = null;
        row.TransactionsJson = null;
        return row;
    }
}
