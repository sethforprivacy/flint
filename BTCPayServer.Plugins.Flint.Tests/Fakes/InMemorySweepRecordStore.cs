using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// In-memory <see cref="ISweepRecordStore"/> with the same observable semantics as the EF implementation.
/// </summary>
/// <remarks>
/// Held to the same contract tests as the real one, because the engine tests run against this and mean nothing if
/// the two disagree — which is exactly how the outgoing-payment store's cross-store key defect stayed invisible.
/// <para>
/// Records are copied in and out of every query method, for a reason: the engine mutates its own in-memory copy of
/// a record after resolving it, and a store that handed out live references would let that mutation silently "fix"
/// the stored row, hiding a compare-and-set that never landed.
/// </para>
/// <para>
/// <see cref="Records"/> is the deliberate exception — it exposes the live rows, so a test can assert on stored
/// state without going through a query. Read it, do not mutate through it.
/// </para>
/// </remarks>
public sealed class InMemorySweepRecordStore : ISweepRecordStore
{
    private readonly WriteLog? _writeLog;
    private readonly Dictionary<string, SweepRecord> _records = [];

    public InMemorySweepRecordStore(WriteLog? writeLog = null)
    {
        _writeLog = writeLog;
    }

    /// <summary>Thrown by <see cref="AddAsync"/> when set.</summary>
    public Exception? FailAddWith { get; set; }

    /// <summary>Thrown by <see cref="TryResolveAsync"/> when set.</summary>
    public Exception? FailResolveWith { get; set; }

    public Exception? FailReportWith { get; set; }

    /// <summary>
    /// Makes <see cref="TryRecordProviderQuoteAsync"/> report that it changed nothing.
    /// </summary>
    /// <remarks>
    /// The real one returns false when the row is no longer <c>Pending</c> — another pass resolved it, or a
    /// concurrent write got there first. The caller must then refuse the send rather than proceed, because a
    /// send whose committed quote id was not recorded is a send nothing can recover.
    /// </remarks>
    public bool RefuseQuoteWrites { get; set; }

    /// <summary>The live rows. See the class remarks: read-only by convention, not by type.</summary>
    public IReadOnlyDictionary<string, SweepRecord> Records => _records;

    public SweepRecord? Single() => _records.Count == 1 ? Copy(_records.Values.First()) : null;

    public Task AddAsync(SweepRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        // Mirrors the EF store's guards. Omitting them let this implementation silently insert a row with an empty
        // store id where the real one throws — precisely the kind of divergence the shared contract exists to catch.
        ArgumentException.ThrowIfNullOrEmpty(record.IdempotencyKey);
        ArgumentException.ThrowIfNullOrEmpty(record.StoreId);
        if (FailAddWith is not null)
            throw FailAddWith;

        // The primary key is the guarantee, not a convention: two sweeps sharing a key must be impossible.
        if (!_records.TryAdd(record.IdempotencyKey, Copy(record)))
        {
            throw new InvalidOperationException(
                $"A sweep record already exists for idempotency key {record.IdempotencyKey}.");
        }

        _writeLog?.Record($"sweep:add:{record.IdempotencyKey}");
        return Task.CompletedTask;
    }

    public Task<SweepRecord?> GetAsync(
        string storeId,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            _records.TryGetValue(idempotencyKey, out var record) && record.StoreId == storeId
                ? Copy(record)
                : null);

    public Task<IReadOnlyList<SweepRecord>> ListAsync(
        string storeId,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return Task.FromResult<IReadOnlyList<SweepRecord>>(_records.Values
            .Where(r => r.StoreId == storeId)
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.IdempotencyKey, StringComparer.Ordinal)
            .Skip(offset)
            .Take(limit)
            .Select(Copy)
            .ToList());
    }

    public Task<IReadOnlyList<SweepRecord>> ListForReportAsync(
        string storeId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailReportWith is not null)
            throw FailReportWith;

        return Task.FromResult<IReadOnlyList<SweepRecord>>(_records.Values
            .Where(r => r.StoreId == storeId
                        && r.CreatedAt >= from
                        && r.CreatedAt <= to
                        && r.Status != SweepRecordStatus.Refused)
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.IdempotencyKey, StringComparer.Ordinal)
            .Select(Copy)
            .ToList());
    }

    public Task<int> CountAsync(string storeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_records.Values.Count(r => r.StoreId == storeId));

    public Task<IReadOnlyList<SweepRecord>> ListUnresolvedAsync(
        string storeId,
        DateTimeOffset sentCreatedAfter,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SweepRecord>>(_records.Values
            // Pending unbounded, Sent age-bounded, non-terminal cross-chain conversions exempt from the age
            // bound — as in the EF store.
            .Where(r => r.StoreId == storeId
                        && (r.Status is SweepRecordStatus.Pending
                            || (r.Status is SweepRecordStatus.Sent
                                && (r.CreatedAt > sentCreatedAfter
                                    || (r.DestinationKind is SweepDestinationKind.EvmAddress
                                        && r.ConversionStatus is not (SparkConversionStatus.Completed
                                            or SparkConversionStatus.Failed
                                            or SparkConversionStatus.Refunded))))))
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.IdempotencyKey, StringComparer.Ordinal)
            .Select(Copy)
            .ToList());

    public Task<SweepRecord?> FindOpenRefusalAsync(
        string storeId,
        SweepRefusalCode code,
        SweepDestinationMode mode,
        DateTimeOffset activeSince,
        CancellationToken cancellationToken = default)
    {
        if (code is SweepRefusalCode.None)
            return Task.FromResult<SweepRecord?>(null);

        return Task.FromResult(_records.Values
            .Where(r => r.StoreId == storeId
                        && r.Status is SweepRecordStatus.Refused
                        && r.Trigger is SweepTrigger.Automatic
                        && r.RefusalCode == code
                        && r.DestinationMode == mode
                        && (r.LastSeenAt ?? r.CreatedAt) >= activeSince)
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.IdempotencyKey, StringComparer.Ordinal)
            .Select(Copy)
            .FirstOrDefault());
    }

    public Task<bool> TryRecordRepeatRefusalAsync(
        string storeId,
        string idempotencyKey,
        string error,
        DateTimeOffset seenAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);

        if (!_records.TryGetValue(idempotencyKey, out var record) ||
            record.StoreId != storeId ||
            record.Status is not SweepRecordStatus.Refused)
        {
            return Task.FromResult(false);
        }

        record.AttemptCount += 1;
        record.LastSeenAt = seenAt;
        record.Error = error;
        _writeLog?.Record($"sweep:repeat:{idempotencyKey}:{record.AttemptCount}");
        return Task.FromResult(true);
    }

    public Task<bool> TryRecordProviderQuoteAsync(
        string storeId,
        string idempotencyKey,
        string providerQuoteId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);
        ArgumentException.ThrowIfNullOrEmpty(providerQuoteId);

        if (RefuseQuoteWrites ||
            !_records.TryGetValue(idempotencyKey, out var record) ||
            record.StoreId != storeId ||
            record.Status != SweepRecordStatus.Pending)
        {
            return Task.FromResult(false);
        }

        record.ProviderQuoteId = providerQuoteId;
        _writeLog?.Record($"sweep:quote:{idempotencyKey}:{providerQuoteId}");
        return Task.FromResult(true);
    }

    public Task<bool> TryResolveAsync(
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

        if (FailResolveWith is not null)
            throw FailResolveWith;

        if (!_records.TryGetValue(idempotencyKey, out var record) ||
            record.StoreId != storeId ||
            !allowedFrom.Contains(record.Status))
        {
            return Task.FromResult(false);
        }

        record.Status = resolution.Status;
        record.CompletedAt = resolution.CompletedAt;
        // Coalesced, matching the EF store: a Sent -> Confirmed step learns nothing new about the txid, and
        // overwriting it with null would erase the only record of where the money went.
        record.FeeSats = resolution.FeeSats ?? record.FeeSats;
        record.TxId = resolution.TxId ?? record.TxId;
        record.Error = resolution.Error ?? record.Error;
        if (resolution.RefusalCode is not SweepRefusalCode.None)
            record.RefusalCode = resolution.RefusalCode;
        // Coalesced for the same reason, and with more force: the provider's state and the delivered amount
        // arrive late and through no event at all, so a poll that could not read them must not erase what an
        // earlier one did.
        record.ConversionStatus = resolution.ConversionStatus ?? record.ConversionStatus;
        record.DeliveredAmountBaseUnits =
            resolution.DeliveredAmountBaseUnits ?? record.DeliveredAmountBaseUnits;
        record.ProviderOrderId = resolution.ProviderOrderId ?? record.ProviderOrderId;

        _writeLog?.Record($"sweep:resolve:{idempotencyKey}:{resolution.Status}");
        return Task.FromResult(true);
    }

    /// <summary>
    /// A detached copy of a row.
    /// </summary>
    /// <remarks>
    /// Hand-written, and therefore able to drop a column silently — which it did: every Wave 7 field was missing
    /// here at first, so a cross-chain row round-tripped through this store as a cooperative exit and the engine
    /// could not tell which recovery strategy it needed. The store contract's
    /// <c>A_record_round_trips_with_every_field</c> now sets every property by reflection and fails when a round
    /// trip loses one, so it cannot happen again quietly.
    /// </remarks>
    internal static SweepRecord Copy(SweepRecord source) => new()
    {
        IdempotencyKey = source.IdempotencyKey,
        StoreId = source.StoreId,
        DestinationAddress = source.DestinationAddress,
        DestinationMode = source.DestinationMode,
        AmountSats = source.AmountSats,
        FeesIncluded = source.FeesIncluded,
        ConfirmationSpeed = source.ConfirmationSpeed,
        QuotedFeeSats = source.QuotedFeeSats,
        FeeSats = source.FeeSats,
        BalanceAtDecisionSats = source.BalanceAtDecisionSats,
        TxId = source.TxId,
        Trigger = source.Trigger,
        Status = source.Status,
        CreatedAt = source.CreatedAt,
        CompletedAt = source.CompletedAt,
        Error = source.Error,
        RefusalCode = source.RefusalCode,
        LastSeenAt = source.LastSeenAt,
        AttemptCount = source.AttemptCount,
        DestinationKind = source.DestinationKind,
        DestinationChain = source.DestinationChain,
        DestinationAsset = source.DestinationAsset,
        DestinationAssetDecimals = source.DestinationAssetDecimals,
        Provider = source.Provider,
        ProviderQuoteId = source.ProviderQuoteId,
        ProviderOrderId = source.ProviderOrderId,
        IdempotencyKeyAccepted = source.IdempotencyKeyAccepted,
        SourceTokenIdentifier = source.SourceTokenIdentifier,
        SourceAmountBaseUnits = source.SourceAmountBaseUnits,
        SourceTokenDecimals = source.SourceTokenDecimals,
        EstimatedOutBaseUnits = source.EstimatedOutBaseUnits,
        DeliveredAmountBaseUnits = source.DeliveredAmountBaseUnits,
        ConversionStatus = source.ConversionStatus
    };
}
