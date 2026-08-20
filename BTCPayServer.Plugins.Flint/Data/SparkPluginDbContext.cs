using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BTCPayServer.Plugins.Flint.Data;

/// <summary>
/// The plugin's EF Core context. Lives in its own Postgres schema
/// (<see cref="Constants.DatabaseSchema"/>) so it never collides with BTCPay's own tables and can
/// be migrated independently, which is the convention BTCPay plugins follow.
/// </summary>
public class SparkPluginDbContext : DbContext
{
    public DbSet<InvoiceRecord> InvoiceRecords { get; set; } = null!;
    public DbSet<OutgoingPaymentRecord> OutgoingPayments { get; set; } = null!;
    public DbSet<SweepRecord> SweepRecords { get; set; } = null!;
    public DbSet<InvoicePaymentHash> InvoicePaymentHashes { get; set; } = null!;
    public DbSet<StablecoinQuote> StablecoinQuotes { get; set; } = null!;

    public DbSet<UnilateralExitRecord> UnilateralExitRecords { get; set; } = null!;

    /// <summary>
    /// Name of the partial unique index that enforces one active unilateral exit per store.
    /// </summary>
    /// <remarks>
    /// Named explicitly rather than left to EF's convention because
    /// <see cref="EfUnilateralExitRecordStore"/> matches Postgres's unique-violation by constraint name: the
    /// primary key raises the same SQLSTATE and means something entirely different. Renaming this index without
    /// renaming it there turns an ordinary race into an unhandled exception on a money-moving page.
    /// </remarks>
    public const string ActiveUnilateralExitIndexName = "UX_UnilateralExitRecords_ActiveStore";

    public SparkPluginDbContext(DbContextOptions<SparkPluginDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema(Constants.DatabaseSchema);

        modelBuilder.Entity<InvoiceRecord>(entity =>
        {
            // The payment hash is the primary key, so BTCPay's per-invoice GetInvoice(id) lookups and the
            // reconciliation task's per-invoice resolution are both point reads on the PK index.
            entity.HasKey(record => record.PaymentHash);
            // Every read path is store-scoped, and expiry sweeps scan by store + status.
            entity.HasIndex(record => new { record.StoreId, record.Status });
            // ListInvoices pages newest-first within a store.
            entity.HasIndex(record => new { record.StoreId, record.CreatedAt });
            // The every-minute cross-store credit walk (ListStoreIdsAwaitingCreditAsync) and the
            // per-store ListUncreditedAsync both filter on exactly this predicate. Without it the
            // cross-store DISTINCT scans every InvoiceRecords row each pass; with it both queries are
            // index-only over a partial index that holds roughly the unsettled tail of the table.
            // Quoted identifiers: the columns are created mixed-case and Postgres would otherwise
            // fold the filter's references to lowercase and fail to match.
            entity.HasIndex(record => new { record.StoreId, record.SettledAt })
                .HasFilter("\"Status\" = 1 AND \"CreditedAt\" IS NULL AND \"CreditAbandonedAt\" IS NULL AND \"SettledAt\" IS NOT NULL");

            // ListForReconciliationAsync walks this store's settleable tail: StoreId equality,
            // Status <> Paid, an ExpiresAt floor, then ExpiresAt/PaymentHash order with a small
            // LIMIT. The partial index serves the walk end to end: the key is the equality column
            // followed by the range floor, and it matches the walk's own ordering, so a page is a
            // forward scan with a keyset seek instead of a sort — the planner only picks this index
            // when the ORDER BY rides its columns, which is why the walk orders by expiry at all.
            // There is deliberately no INCLUDE payload: the walk materialises whole InvoiceRecord
            // entities, so no index-only scan is possible and every returned row is a heap fetch
            // anyway — the index earns its keep on the key and the seek alone. Paid is the enum
            // value 1 (InvoiceRecordStatus.Paid), so the predicate admits Unpaid (0) and Expired (2),
            // exactly matching the query's "a cancelled invoice is still payable" rule. Status is a
            // non-nullable int, so <> 1 is null-safe here.
            // Quoted identifiers as above: the columns are mixed-case.
            entity.HasIndex(record => new { record.StoreId, record.ExpiresAt })
                .HasDatabaseName("IX_InvoiceRecords_StoreId_ExpiresAt_Settleable")
                .HasFilter("\"Status\" <> 1");
        });

        modelBuilder.Entity<OutgoingPaymentRecord>(entity =>
        {
            // Keyed on store <b>and</b> payment hash, matching every query. A payment-hash-only key would be a
            // silent cross-store defect: two stores on one server can each be asked to pay the same BOLT11 (a
            // shared supplier invoice, say), and the second store's insert would collide with the first store's
            // row, while the store-scoped read that follows would find nothing. The synthesized fallback record
            // would then report ReportedAt = null forever, so a legitimate crash-retry of a payment that had
            // already been sent would be reported to BTCPay as a failure.
            entity.HasKey(record => new { record.StoreId, record.PaymentHash });
            entity.HasIndex(record => new { record.StoreId, record.FirstAttemptAt });
        });

        modelBuilder.Entity<SweepRecord>(entity =>
        {
            // The SDK's idempotency key is the primary key, which is what makes "one sweep per key" a database
            // guarantee rather than a convention: the insert that precedes every send would collide.
            entity.HasKey(record => record.IdempotencyKey);
            // The history page pages newest-first within a store.
            entity.HasIndex(record => new { record.StoreId, record.CreatedAt });
            // Every pass of the sweep engine opens by looking for this store's in-flight rows.
            entity.HasIndex(record => new { record.StoreId, record.Status });
        });

        modelBuilder.Entity<InvoicePaymentHash>(entity =>
        {
            // The payment hash is the primary key — the read path is a point read on it, exactly as in core's
            // AddressInvoices. No store-scoped key: a hash is unique to one invoice, and the reader never
            // filters by store.
            entity.HasKey(record => record.PaymentHash);
            // The table name is pinned so the raw SQL in EfInvoicePaymentHashIndex.RecordAsync names the same
            // table EF creates, rather than relying on both ending up at EF's default pluralisation.
            entity.ToTable("InvoicePaymentHashes");
            // The retention pass (Services.SparkPaymentHashRetentionTask) deletes by exactly this column:
            // DELETE ... WHERE "FirstSeenAt" < cutoff. Without an index that delete is a full scan of the
            // whole association table on every pass; with one it is a range seek over the stale head of the
            // table. The read path (FindByPaymentHashAsync) never touches FirstSeenAt, so this index serves
            // the delete alone. Name pinned rather than left to EF's default, as the settleable index above
            // pins its own.
            entity.HasIndex(record => record.FirstSeenAt)
                .HasDatabaseName("IX_InvoicePaymentHashes_FirstSeenAt");
        });

        modelBuilder.Entity<StablecoinQuote>(entity =>
        {
            entity.HasKey(quote => quote.Id);
            entity.ToTable("StablecoinQuotes");
            // One Spark payment settles one quote. Partial, because every unsettled quote holds null here; this is
            // the database backstop under the compare-and-set in EfStablecoinQuoteStore.TrySettleAsync.
            // Quoted identifier: the column is mixed-case and Postgres would otherwise fold the filter to lowercase.
            entity.HasIndex(quote => quote.SdkPaymentId)
                .IsUnique()
                .HasFilter("\"SdkPaymentId\" IS NOT NULL")
                .HasDatabaseName("IX_StablecoinQuotes_SdkPaymentId");
            // The quotes one checkout reuses, caps and lists.
            entity.HasIndex(quote => quote.InvoiceId);
            // The matching candidates and the reconciliation pass's store list: unsettled quotes still inside
            // their window, which is a small tail of a table that otherwise only grows.
            entity.HasIndex(quote => new { quote.StoreId, quote.ExpiresAt })
                .HasFilter("\"SdkPaymentId\" IS NULL")
                .HasDatabaseName("IX_StablecoinQuotes_StoreId_ExpiresAt_Open");
            // The credit retry walk: settled, not yet on the BTCPay invoice.
            entity.HasIndex(quote => new { quote.StoreId, quote.SettledAt })
                .HasFilter("\"SdkPaymentId\" IS NOT NULL AND \"CreditedAt\" IS NULL")
                .HasDatabaseName("IX_StablecoinQuotes_StoreId_SettledAt_Uncredited");
            entity.Property(quote => quote.DueAmount).HasPrecision(38, 18);
            entity.Property(quote => quote.FeeAmount).HasPrecision(38, 18);
        });

        modelBuilder.Entity<UnilateralExitRecord>(entity =>
        {
            // The plugin-generated UUID is the primary key. Unlike the sweep table's, it is not an SDK
            // idempotency key and guarantees nothing beyond uniqueness — a unilateral exit has no SDK-side
            // identity to be idempotent on, because the SDK never broadcasts it.
            entity.HasKey(record => record.Id);
            // The exit page reads the store's history newest-first. Store-leading, so it also serves the plain
            // "this store's exits" scan — including the MAX(FundingKeyIndex) a new quote allocates from —
            // without a second single-column index.
            entity.HasIndex(record => new { record.StoreId, record.CreatedUtc });
            // Every entry to the page opens by looking for the store's one active exit, which is the
            // single-flight guard on quoting.
            entity.HasIndex(record => new { record.StoreId, record.Status });
            // And that guard is enforced here rather than only in the service. The service's "does this store
            // already have an active exit?" read and the insert that follows it are two statements, so a second
            // server — or a request that slipped past the in-process gate — could pass the check and still
            // insert. Two active exits would compete for the same leaves, so the database refuses the second.
            //
            // The filter names the two non-terminal statuses by their persisted numbers (AwaitingFunding = 0,
            // Built = 1) because it is raw SQL and cannot see the enum. Adding a status means changing this, the
            // store's queries and UnilateralExitRecord.IsActive together.
            entity.HasIndex(record => record.StoreId)
                .HasDatabaseName(ActiveUnilateralExitIndexName)
                .IsUnique()
                .HasFilter("\"Status\" IN (0, 1)");
        });
    }
}

/// <summary>
/// Design-time factory, used only by <c>dotnet ef</c> when authoring migrations. The connection
/// string is never used at runtime; it only has to be a valid Npgsql string so the provider can
/// build a model. Matches the pattern in BTCPay's other plugins.
/// </summary>
public class SparkPluginDesignTimeDbContextFactory : IDesignTimeDbContextFactory<SparkPluginDbContext>
{
    public SparkPluginDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<SparkPluginDbContext>();
        builder.UseNpgsql("User ID=postgres;Host=127.0.0.1;Port=39372;Database=designtimebtcpay");
        return new SparkPluginDbContext(builder.Options);
    }
}
