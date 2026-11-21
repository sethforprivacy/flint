using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

public sealed record BackfillCount(int Skipped, int Created, int Failed)
{
    public static BackfillCount Zero => new(0, 0, 0);

    public BackfillCount Add(BackfillCount other) =>
        new(Skipped + other.Skipped, Created + other.Created, Failed + other.Failed);
}

public sealed record BackfillReport(
    bool DryRun,
    string StoreId,
    BackfillCount LightningReceives,
    BackfillCount StablecoinReceives,
    BackfillCount Sends,
    BackfillCount Sweeps,
    BackfillCount UnilateralExits,
    BackfillCount SdkPayments)
{
    public BackfillCount Total =>
        LightningReceives
            .Add(StablecoinReceives)
            .Add(Sends)
            .Add(Sweeps)
            .Add(UnilateralExits)
            .Add(SdkPayments);
}

public sealed class SparkLedgerBackfillService(
    SparkPluginDbContextFactory dbFactory,
    ISparkLedgerStore ledger,
    ISparkStoreRuntime runtime,
    ILogger<SparkLedgerBackfillService> logger)
{
    public async Task<BackfillReport> RunAsync(
        string storeId,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Store {StoreId}: ledger backfill starting (dry={DryRun})",
            storeId, dryRun);

        var lightning = await BackfillLightningAsync(storeId, dryRun, cancellationToken)
            .ConfigureAwait(false);
        var stablecoin = await BackfillStablecoinAsync(storeId, dryRun, cancellationToken)
            .ConfigureAwait(false);
        var sends = await BackfillSendsAsync(storeId, dryRun, cancellationToken)
            .ConfigureAwait(false);
        var sweeps = await BackfillSweepsAsync(storeId, dryRun, cancellationToken)
            .ConfigureAwait(false);
        var exits = await BackfillUnilateralExitsAsync(storeId, dryRun, cancellationToken)
            .ConfigureAwait(false);
        var sdk = await BackfillFromSdkAsync(storeId, dryRun, cancellationToken)
            .ConfigureAwait(false);

        var report = new BackfillReport(dryRun, storeId, lightning, stablecoin, sends, sweeps, exits, sdk);
        logger.LogInformation(
            "Store {StoreId}: ledger backfill done (dry={DryRun}) lightning={L} stablecoin={S} sends={Se} sweeps={Sw} exits={E} sdk={Sdk}",
            storeId, dryRun,
            $"{lightning.Created}c/{lightning.Skipped}s/{lightning.Failed}f",
            $"{stablecoin.Created}c/{stablecoin.Skipped}s/{stablecoin.Failed}f",
            $"{sends.Created}c/{sends.Skipped}s/{sends.Failed}f",
            $"{sweeps.Created}c/{sweeps.Skipped}s/{sweeps.Failed}f",
            $"{exits.Created}c/{exits.Skipped}s/{exits.Failed}f",
            $"{sdk.Created}c/{sdk.Skipped}s/{sdk.Failed}f");
        return report;
    }

    private async Task<BackfillCount> BackfillLightningAsync(
        string storeId, bool dryRun, CancellationToken cancellationToken)
    {
        var skipped = 0;
        var created = 0;
        var failed = 0;

        await using var context = dbFactory.CreateContext();
        var records = await context.InvoiceRecords
            .AsNoTracking()
            .Where(r => r.StoreId == storeId && r.CreditedAt != null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Build a PaymentHash -> InvoiceId lookup so each ledger entry carries the BTCPay invoice ID.
        var paymentHashes = records
            .Select(r => r.PaymentHash)
            .Where(h => h != null)
            .ToHashSet();
        var invoiceIdByHash = await context.InvoicePaymentHashes
            .AsNoTracking()
            .Where(h => paymentHashes.Contains(h.PaymentHash))
            .ToDictionaryAsync(h => h.PaymentHash, h => h.InvoiceId, cancellationToken)
            .ConfigureAwait(false);

        foreach (var record in records)
        {
            try
            {
                var existsByHash = record.PaymentHash != null
                    && await ledger.ExistsForPaymentHashAsync(storeId, record.PaymentHash, cancellationToken)
                        .ConfigureAwait(false);
                var existsBySdk = record.SdkPaymentId != null
                    && await ledger.ExistsForSdkPaymentAsync(storeId, record.SdkPaymentId, cancellationToken)
                        .ConfigureAwait(false);
                if (existsByHash || existsBySdk)
                {
                    skipped++;
                    continue;
                }

                invoiceIdByHash.TryGetValue(record.PaymentHash ?? "", out var btcPayInvoiceId);

                if (!dryRun)
                {
                    await ledger.AppendAsync(new SparkLedgerEntry
                    {
                        Id = Guid.NewGuid().ToString(),
                        StoreId = storeId,
                        EntryType = SparkLedgerEntryType.LightningReceive,
                        AmountMsat = record.AmountReceivedMsat ?? 0,
                        FeeMsat = 0,
                        OccurredAt = record.SettledAt ?? record.CreditedAt!.Value,
                        RecordedAt = record.CreditedAt!.Value,
                        SdkPaymentId = record.SdkPaymentId,
                        PaymentHash = record.PaymentHash,
                        BtcPayInvoiceId = btcPayInvoiceId,
                        IsBackfill = true,
                    }, cancellationToken).ConfigureAwait(false);
                }
                created++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogWarning(ex,
                    "Store {StoreId}: lightning backfill failed for hash {PaymentHash}",
                    storeId, record.PaymentHash);
            }
        }

        return new BackfillCount(skipped, created, failed);
    }

    private async Task<BackfillCount> BackfillStablecoinAsync(
        string storeId, bool dryRun, CancellationToken cancellationToken)
    {
        var skipped = 0;
        var created = 0;
        var failed = 0;

        await using var context = dbFactory.CreateContext();
        var quotes = await context.StablecoinQuotes
            .AsNoTracking()
            .Where(q => q.StoreId == storeId && q.CreditedAt != null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var quote in quotes)
        {
            try
            {
                var exists = quote.SdkPaymentId != null
                    && await ledger.ExistsForSdkPaymentAsync(storeId, quote.SdkPaymentId, cancellationToken)
                        .ConfigureAwait(false);
                if (exists)
                {
                    skipped++;
                    continue;
                }

                if (!dryRun)
                {
                    await ledger.AppendAsync(new SparkLedgerEntry
                    {
                        Id = Guid.NewGuid().ToString(),
                        StoreId = storeId,
                        EntryType = SparkLedgerEntryType.StablecoinReceive,
                        AmountMsat = quote.DestinationAsset == "BTC" && quote.DeliveredBaseUnits is not null
                            ? (long)StablecoinQuote.ParseBaseUnits(quote.DeliveredBaseUnits) * 1000L
                            : 0,
                        FeeMsat = 0,
                        OccurredAt = quote.SettledAt ?? quote.CreditedAt!.Value,
                        RecordedAt = quote.CreditedAt!.Value,
                        SdkPaymentId = quote.SdkPaymentId,
                        BtcPayInvoiceId = quote.InvoiceId,
                        StablecoinQuoteId = quote.Id,
                        StablecoinChain = quote.Chain,
                        StablecoinAsset = quote.Asset,
                        StablecoinPaidBaseUnits = quote.PaidBaseUnits,
                        StablecoinExternalTxHash = quote.ExternalTxHash,
                        IsBackfill = true,
                    }, cancellationToken).ConfigureAwait(false);
                }
                created++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogWarning(ex,
                    "Store {StoreId}: stablecoin backfill failed for quote {QuoteId}",
                    storeId, quote.Id);
            }
        }

        return new BackfillCount(skipped, created, failed);
    }

    // OutgoingPaymentRecord does not persist AmountSats or FeeSats; recovering those from the SDK
    // would require live SDK queries and is out of scope for the initial backfill. Sends made after
    // this ledger was added are written at the SparkSendPaymentService.SendAsync write point.
    private static Task<BackfillCount> BackfillSendsAsync(
        string storeId, bool dryRun, CancellationToken cancellationToken) =>
        Task.FromResult(BackfillCount.Zero);

    private async Task<BackfillCount> BackfillSweepsAsync(
        string storeId, bool dryRun, CancellationToken cancellationToken)
    {
        var skipped = 0;
        var created = 0;
        var failed = 0;

        await using var context = dbFactory.CreateContext();
        var records = await context.SweepRecords
            .AsNoTracking()
            .Where(r => r.StoreId == storeId
                && (r.Status == SweepRecordStatus.Sent || r.Status == SweepRecordStatus.Confirmed))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var record in records)
        {
            try
            {
                var exists = await ledger.ExistsForSdkPaymentAsync(storeId, record.IdempotencyKey, cancellationToken)
                    .ConfigureAwait(false);
                if (exists)
                {
                    skipped++;
                    continue;
                }

                if (!dryRun)
                {
                    await ledger.AppendAsync(MapSweepRecord(storeId, record), cancellationToken).ConfigureAwait(false);
                }
                created++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogWarning(ex,
                    "Store {StoreId}: sweep backfill failed for {IdempotencyKey}",
                    storeId, record.IdempotencyKey);
            }
        }

        return new BackfillCount(skipped, created, failed);
    }

    private async Task<BackfillCount> BackfillUnilateralExitsAsync(
        string storeId, bool dryRun, CancellationToken cancellationToken)
    {
        var skipped = 0;
        var created = 0;
        var failed = 0;

        await using var context = dbFactory.CreateContext();
        var records = await context.UnilateralExitRecords
            .AsNoTracking()
            .Where(r => r.StoreId == storeId && r.Status == UnilateralExitStatus.Completed)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // No SdkPaymentId for unilateral exits; check by time + amount to avoid duplicates.
        var existingExits = await context.SparkLedger
            .AsNoTracking()
            .Where(e => e.StoreId == storeId && e.EntryType == SparkLedgerEntryType.UnilateralExit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var record in records)
        {
            try
            {
                var exists = existingExits.Exists(e =>
                    e.OccurredAt == record.UpdatedUtc
                    && e.AmountMsat == record.RecoverableValueSat * 1000L);
                if (exists)
                {
                    skipped++;
                    continue;
                }

                if (!dryRun)
                {
                    await ledger.AppendAsync(new SparkLedgerEntry
                    {
                        Id = Guid.NewGuid().ToString(),
                        StoreId = storeId,
                        EntryType = SparkLedgerEntryType.UnilateralExit,
                        AmountMsat = record.RecoverableValueSat * 1000L,
                        FeeMsat = record.TotalFeeSat * 1000L,
                        OccurredAt = record.UpdatedUtc,
                        RecordedAt = record.UpdatedUtc,
                        IsBackfill = true,
                    }, cancellationToken).ConfigureAwait(false);
                }
                created++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogWarning(ex,
                    "Store {StoreId}: unilateral exit backfill failed for record {RecordId}",
                    storeId, record.Id);
            }
        }

        return new BackfillCount(skipped, created, failed);
    }

    // Maps SDK payment to ledger entry type. Returns null for payment methods not yet modelled
    // (Token send = stablecoin send via token balance).
    internal static SparkLedgerEntryType? EntryTypeFor(SparkPayment payment) =>
        (payment.Direction, payment.Method) switch
        {
            (SparkPaymentDirection.Receive, SparkPaymentMethod.Lightning) => SparkLedgerEntryType.LightningReceive,
            // Stablecoin cross-chain receive that landed as BTC (Method=Spark) or as token/USDB (Method=Token).
            (SparkPaymentDirection.Receive, SparkPaymentMethod.Spark)     => SparkLedgerEntryType.StablecoinReceive,
            (SparkPaymentDirection.Receive, SparkPaymentMethod.Token)     => SparkLedgerEntryType.StablecoinReceive,
            // Auto-claimed on-chain deposit to the wallet's static Bitcoin address.
            // FeeSats = on-chain claim fee; no payment hash; identifier is SdkPaymentId + txid:vout in Description.
            (SparkPaymentDirection.Receive, SparkPaymentMethod.Deposit)   => SparkLedgerEntryType.OnchainDeposit,
            (SparkPaymentDirection.Send,    SparkPaymentMethod.Lightning) => SparkLedgerEntryType.Send,
            // Spark transfer to another Spark address, or cross-chain send funded from BTC balance.
            (SparkPaymentDirection.Send,    SparkPaymentMethod.Spark)     => SparkLedgerEntryType.Send,
            // Cooperative exit (auto-sweep) to an on-chain address.
            (SparkPaymentDirection.Send,    SparkPaymentMethod.Withdraw)  => SparkLedgerEntryType.Sweep,
            _ => null,
        };

    /// <summary>
    /// Maps one completed <see cref="SweepRecord"/> to a <see cref="SparkLedgerEntry"/>.
    /// AmountMsat = RecipientAmountSats (net) so that debit = Amount + Fee = gross.
    /// Extracted for unit testability; called by <see cref="BackfillSweepsAsync"/>.
    /// </summary>
    internal static SparkLedgerEntry MapSweepRecord(string storeId, SweepRecord record) =>
        new()
        {
            Id = Guid.NewGuid().ToString(),
            StoreId = storeId,
            EntryType = SparkLedgerEntryType.Sweep,
            AmountMsat = record.RecipientAmountSats * 1000L,
            FeeMsat = (record.FeeSats ?? 0) * 1000L,
            OccurredAt = record.CompletedAt ?? record.CreatedAt,
            RecordedAt = record.CompletedAt ?? record.CreatedAt,
            SdkPaymentId = record.IdempotencyKey,
            IsBackfill = true,
        };

    /// <summary>
    /// Maps a list of completed SDK payments to ledger entries, applying DB enrichment lookups.
    /// Extracted for unit testability; called by <see cref="BackfillFromSdkAsync"/>.
    /// </summary>
    internal static IReadOnlyList<SparkLedgerEntry> MapSdkPayments(
        string storeId,
        DateTimeOffset recordedAt,
        IReadOnlyList<SparkPayment> payments,
        IReadOnlyDictionary<string, string> invoiceIdByHash,
        IReadOnlyDictionary<string, StablecoinQuote> quoteBySdkId)
    {
        var entries = new List<SparkLedgerEntry>(payments.Count);
        foreach (var payment in payments)
        {
            var entryType = EntryTypeFor(payment);
            if (entryType is null)
                continue;

            long amountMsat;
            long feeMsat;
            string? btcPayInvoiceId = null;
            string? stablecoinQuoteId = null;
            string? stablecoinChain = null;
            string? stablecoinAsset = null;
            string? stablecoinPaidBaseUnits = null;
            string? stablecoinExternalTxHash = null;

            if (entryType == SparkLedgerEntryType.LightningReceive)
            {
                amountMsat = payment.AmountSats * 1000L;
                feeMsat = 0;
                invoiceIdByHash.TryGetValue(payment.PaymentHash ?? "", out btcPayInvoiceId);
            }
            else if (entryType == SparkLedgerEntryType.StablecoinReceive)
            {
                // Token receives (Stable Balance USDB) cannot be expressed as sats yet.
                // AmountMsat = 0 is intentional; a separate token-amount column is needed.
                amountMsat = payment.Method == SparkPaymentMethod.Token
                    ? 0
                    : payment.AmountSats * 1000L;
                feeMsat = 0;

                if (quoteBySdkId.TryGetValue(payment.SdkPaymentId, out var quote))
                {
                    btcPayInvoiceId = quote.InvoiceId;
                    stablecoinQuoteId = quote.Id;
                    stablecoinChain = quote.Chain;
                    stablecoinAsset = quote.Asset;
                    stablecoinPaidBaseUnits = quote.PaidBaseUnits;
                    stablecoinExternalTxHash = quote.ExternalTxHash;
                }
            }
            else
            {
                amountMsat = payment.AmountSats * 1000L;
                feeMsat = payment.FeeSats * 1000L;
            }

            entries.Add(new SparkLedgerEntry
            {
                Id = Guid.NewGuid().ToString(),
                StoreId = storeId,
                EntryType = entryType.Value,
                AmountMsat = amountMsat,
                FeeMsat = feeMsat,
                OccurredAt = payment.Timestamp,
                RecordedAt = recordedAt,
                SdkPaymentId = payment.SdkPaymentId,
                PaymentHash = payment.PaymentHash,
                Description = payment.Description,
                BtcPayInvoiceId = btcPayInvoiceId,
                StablecoinQuoteId = stablecoinQuoteId,
                StablecoinChain = stablecoinChain,
                StablecoinAsset = stablecoinAsset,
                StablecoinPaidBaseUnits = stablecoinPaidBaseUnits,
                StablecoinExternalTxHash = stablecoinExternalTxHash,
                IsBackfill = true,
            });
        }
        return entries;
    }

    /// <summary>
    /// SDK-primary pass: pages through all completed SDK payments and writes ledger entries for any
    /// that are not already present. Enriches each entry from the Flint DB (invoice id, stablecoin
    /// quote fields) where available.
    /// <para>
    /// NOTE (Stable Balance): when a stablecoin receive lands as a token (Method=Token, e.g. USDB),
    /// <see cref="SparkLedgerEntry.AmountMsat"/> is set to 0. A separate token-amount representation
    /// is needed to track this correctly; that is tracked as a future work item.
    /// </para>
    /// </summary>
    internal async Task<BackfillCount> BackfillFromSdkAsync(
        string storeId, bool dryRun, CancellationToken cancellationToken)
    {
        var sdk = await runtime.GetSdkClientAsync(storeId).ConfigureAwait(false);
        if (sdk is null)
        {
            logger.LogDebug(
                "Store {StoreId}: SDK not available, skipping SDK-primary backfill", storeId);
            return BackfillCount.Zero;
        }

        // Page through all completed payments, newest first (SDK default).
        var allPayments = new List<SparkPayment>();
        const int pageSize = 100;
        var offset = 0;
        while (true)
        {
            var page = await sdk.ListPaymentsAsync(
                new SparkListPaymentsQuery(
                    Direction: null,
                    CompletedOnly: true,
                    Offset: offset,
                    Limit: pageSize),
                cancellationToken).ConfigureAwait(false);
            allPayments.AddRange(page);
            if (page.Count < pageSize) break;
            offset += pageSize;
        }

        if (allPayments.Count == 0)
            return BackfillCount.Zero;

        // Bulk load enrichment from DB in one pass. dbFactory is null only in unit tests
        // that seed the SDK client but exercise idempotency without needing DB enrichment.
        await using var context = dbFactory?.CreateContext();

        var lnHashes = allPayments
            .Where(p => p.Direction == SparkPaymentDirection.Receive
                     && p.Method == SparkPaymentMethod.Lightning
                     && p.PaymentHash is not null)
            .Select(p => p.PaymentHash!)
            .ToHashSet();

        var invoiceIdByHash = (context is not null && lnHashes.Count > 0)
            ? await context.InvoicePaymentHashes
                .AsNoTracking()
                .Where(h => lnHashes.Contains(h.PaymentHash))
                .ToDictionaryAsync(h => h.PaymentHash, h => h.InvoiceId, cancellationToken)
                .ConfigureAwait(false)
            : new Dictionary<string, string>();

        var stablecoinSdkIds = allPayments
            .Where(p => p.Direction == SparkPaymentDirection.Receive
                     && p.Method is SparkPaymentMethod.Spark or SparkPaymentMethod.Token)
            .Select(p => p.SdkPaymentId)
            .ToHashSet();

        var quoteBySdkId = (context is not null && stablecoinSdkIds.Count > 0)
            ? await context.StablecoinQuotes
                .AsNoTracking()
                .Where(q => q.SdkPaymentId != null && stablecoinSdkIds.Contains(q.SdkPaymentId))
                .ToDictionaryAsync(q => q.SdkPaymentId!, q => q, cancellationToken)
                .ConfigureAwait(false)
            : new Dictionary<string, StablecoinQuote>();

        var entries = MapSdkPayments(storeId, DateTimeOffset.UtcNow, allPayments, invoiceIdByHash, quoteBySdkId);

        var skipped = allPayments.Count - entries.Count; // payments skipped because method not modelled
        var created = 0;
        var failed = 0;

        foreach (var entry in entries)
        {
            try
            {
                var existsBySdk = entry.SdkPaymentId is not null
                    && await ledger.ExistsForSdkPaymentAsync(
                        storeId, entry.SdkPaymentId, cancellationToken).ConfigureAwait(false);
                var existsByHash = entry.PaymentHash is not null
                    && await ledger.ExistsForPaymentHashAsync(
                        storeId, entry.PaymentHash, cancellationToken).ConfigureAwait(false);

                if (existsBySdk || existsByHash)
                {
                    skipped++;
                    continue;
                }

                if (!dryRun)
                {
                    await ledger.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
                }
                created++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogWarning(ex,
                    "Store {StoreId}: SDK backfill failed for payment {SdkPaymentId}",
                    storeId, entry.SdkPaymentId);
            }
        }

        return new BackfillCount(skipped, created, failed);
    }
}
