using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// What a migration does to rows that already exist.
/// </summary>
/// <remarks>
/// <para>
/// Migrations are otherwise unexercised by this suite: the Postgres store tests build a schema from scratch, so
/// they never observe a backfill. That is fine for column shapes and wrong for <em>defaults</em>, which only
/// matter to rows written before the migration ran — the rows on a real merchant's server.
/// </para>
/// <para>
/// Inspecting the operations through EF's own API rather than scanning the file, so a hand edit that changed
/// the value would be caught but a reformat would not.
/// </para>
/// </remarks>
public class SparkMigrationTests
{
    /// <summary>
    /// Existing sweep rows are backfilled as carrying a usable idempotency key.
    /// </summary>
    /// <remarks>
    /// <b>The scaffolder generates <c>false</c> here, and <c>false</c> would be wrong in a way that costs
    /// money.</b> Every row written before Wave 7 is a cooperative exit whose idempotency key the SDK adopted as
    /// its own payment id — that is what makes a crashed sweep resolvable. Backfilling <c>false</c> would tell
    /// the engine's recovery walk that those rows must instead be matched by a provider quote id, a column that
    /// is null on all of them; any sweep in flight across the upgrade would then be written off as unresolvable
    /// after the grace period, for a send that very likely succeeded and could have been looked up.
    /// </remarks>
    [Fact]
    public void The_cross_chain_migration_treats_existing_rows_as_having_a_usable_idempotency_key()
    {
        var operation = Assert.Single(
            new CrossChainSweepFields().UpOperations
                .OfType<AddColumnOperation>()
                .Where(o => o.Name == nameof(SweepRecord.IdempotencyKeyAccepted)));

        Assert.Equal("SweepRecords", operation.Table);
        Assert.False(operation.IsNullable);
        Assert.Equal(true, operation.DefaultValue);
    }

    /// <summary>
    /// Existing rows are backfilled as cooperative exits, which is what they are.
    /// </summary>
    /// <remarks>
    /// True by the enum's zero value rather than by an explicit default, which is exactly why it is worth
    /// pinning: reordering <see cref="Services.SweepDestinationKind"/> so that <c>EvmAddress</c> came first
    /// would silently reclassify every historical sweep as cross-chain, and nothing else would notice.
    /// </remarks>
    [Fact]
    public void The_cross_chain_migration_treats_existing_rows_as_cooperative_exits()
    {
        var operation = Assert.Single(
            new CrossChainSweepFields().UpOperations
                .OfType<AddColumnOperation>()
                .Where(o => o.Name == nameof(SweepRecord.DestinationKind)));

        Assert.Equal((int)Services.SweepDestinationKind.BitcoinAddress, operation.DefaultValue);
    }

    /// <summary>
    /// Settlements that predate the credit column are treated as <em>not yet credited</em>.
    /// </summary>
    /// <remarks>
    /// <b>A default would be the wrong answer in the direction that loses money.</b> Backfilling a timestamp —
    /// <c>now()</c>, or the settlement time — would tell the first reconciliation pass that every historical
    /// settlement had already reached its BTCPay invoice. For the ones BTCPay's own listener credited that
    /// happens to be true, and the pass would have confirmed it in a single indexed lookup; for the ones it
    /// missed, which is the very defect the column exists to close, it is false and the credit would never be
    /// attempted. Null costs one lookup per historical settlement inside the listing bound, once.
    /// </remarks>
    [Fact]
    public void Settlements_that_predate_the_credit_column_are_left_uncredited()
    {
        var operation = Assert.Single(
            new InvoiceRecordCreditedAt().UpOperations
                .OfType<AddColumnOperation>()
                .Where(o => o.Name == nameof(InvoiceRecord.CreditedAt)));

        Assert.Equal("InvoiceRecords", operation.Table);
        Assert.True(operation.IsNullable);
        Assert.Null(operation.DefaultValue);
        Assert.Null(operation.DefaultValueSql);
    }

    /// <summary>
    /// Settlements that predate the abandoned column are treated as <em>not given up on</em>.
    /// </summary>
    /// <remarks>
    /// The mirror of the column above, and the default matters in the same direction. A backfilled timestamp
    /// here would mark every historical settlement as one this plugin had already given up on and reported —
    /// removing it from the credit walk on the first pass, so the ones BTCPay's listener genuinely missed would
    /// never be routed and never be mentioned to anyone. Null means the walk classifies each of them on its
    /// merits, exactly once, which is what the column is for.
    /// </remarks>
    [Fact]
    public void Settlements_that_predate_the_abandoned_column_are_not_treated_as_given_up_on()
    {
        var operation = Assert.Single(
            new InvoiceRecordCreditAbandonedAt().UpOperations
                .OfType<AddColumnOperation>()
                .Where(o => o.Name == nameof(InvoiceRecord.CreditAbandonedAt)));

        Assert.Equal("InvoiceRecords", operation.Table);
        Assert.True(operation.IsNullable);
        Assert.Null(operation.DefaultValue);
        Assert.Null(operation.DefaultValueSql);
    }

    /// <summary>
    /// The two credit columns are separate, because they record opposite facts about the same money.
    /// </summary>
    /// <remarks>
    /// Pinned as a schema property rather than left to the classes that read it. The cheap way to retire a
    /// settlement that can never be credited is to stamp <c>CreditedAt</c> and be done — it leaves the retry set
    /// and stops warning — and it would silently turn this table, which is what an operator reconciles a wallet
    /// balance against, into one that claims every abandoned payment was collected. A future "simplification"
    /// that dropped one column in favour of the other fails here.
    /// </remarks>
    [Fact]
    public void The_credited_and_abandoned_stamps_are_distinct_columns()
    {
        var added = new[] { nameof(InvoiceRecord.CreditedAt), nameof(InvoiceRecord.CreditAbandonedAt) };
        var operations = new Migration[] { new InvoiceRecordCreditedAt(), new InvoiceRecordCreditAbandonedAt() }
            .SelectMany(m => m.UpOperations.OfType<AddColumnOperation>())
            .Where(o => added.Contains(o.Name))
            .ToList();

        Assert.Equal(added.Length, operations.Count);
        Assert.Equal(added.Length, operations.Select(o => o.Name).Distinct().Count());
    }

    /// <summary>
    /// The reconciliation walk gets a partial index, and nothing about the walk's shape is accidental.
    /// </summary>
    /// <remarks>
    /// <b>Every field here is load-bearing for the query it serves.</b>
    /// <c>ListForReconciliationAsync</c> filters on store, on not-yet-paid, and on an expiry floor, then pages
    /// by <c>ExpiresAt</c>/<c>PaymentHash</c> under a small limit. The index leads with <c>ExpiresAt</c> after
    /// <c>StoreId</c> so the floor is a seek, and its key <em>is</em> the walk's ordering — measured against
    /// Postgres, the planner only reaches for this index when the ORDER BY rides its columns. It carries no
    /// INCLUDE payload: the walk materialises whole <c>InvoiceRecord</c> entities, so there is no index-only
    /// scan for a payload to serve and every returned row is a heap fetch anyway. It is partial on
    /// <c>"Status" &lt;&gt; 1</c>, the enum value of <see cref="InvoiceRecordStatus.Paid"/>, so paid (terminal)
    /// rows never enter it, exactly mirroring the query's rule that only a paid invoice is settled. A dropped
    /// key column, a re-added INCLUDE payload, or a filter widened to include paid rows fails here. The Down
    /// has to drop the same index in the same schema, or unwinding an upgrade would strand it.
    /// </remarks>
    [Fact]
    public void The_settleable_index_is_partial_and_drops_cleanly()
    {
        var operation = Assert.Single(
            new SettleableInvoiceIndex().UpOperations.OfType<CreateIndexOperation>());

        Assert.Equal("IX_InvoiceRecords_StoreId_ExpiresAt_Settleable", operation.Name);
        Assert.Equal("BTCPayServer.Plugins.Flint", operation.Schema);
        Assert.Equal("InvoiceRecords", operation.Table);
        Assert.Equal(
            new[] { nameof(InvoiceRecord.StoreId), nameof(InvoiceRecord.ExpiresAt) },
            operation.Columns);
        // Deliberately no INCLUDE payload: the walk materialises whole entities, so there is no
        // index-only scan for a payload to serve. Pinned so it cannot creep back in.
        Assert.Null(operation["Npgsql:IndexInclude"]);
        Assert.Equal("\"Status\" <> 1", operation.Filter);

        var drop = Assert.Single(
            new SettleableInvoiceIndex().DownOperations.OfType<DropIndexOperation>());
        Assert.Equal(operation.Name, drop.Name);
        Assert.Equal(operation.Schema, drop.Schema);
        Assert.Equal(operation.Table, drop.Table);
    }

    /// <summary>
    /// The retention delete gets its range index, changes nothing else, and drops cleanly.
    /// </summary>
    /// <remarks>
    /// <c>SparkPaymentHashRetentionTask</c>'s pass is a <c>DELETE ... WHERE "FirstSeenAt" &lt; cutoff</c> on a
    /// table that grows with every prompt mint on the server; without this index the retention pass full-
    /// scans the very table it exists to shrink. No read path touches <c>FirstSeenAt</c>, so dropping the
    /// index would degrade only the delete, silently, on a real merchant's server. The migration must also
    /// carry nothing else: the retention work is a schema addition, not a row change, and an unexpected
    /// column or backfill riding along with an index migration is exactly the surprise this pin exists to
    /// surface. The <c>Down</c> must drop the same named index in the same schema, or unwinding an upgrade
    /// strands it.
    /// </remarks>
    [Fact]
    public void The_retention_index_covers_the_delete_column_and_carries_nothing_else()
    {
        var operation = Assert.Single(
            new PaymentHashRetentionIndex().UpOperations.OfType<CreateIndexOperation>());

        Assert.Equal("IX_InvoicePaymentHashes_FirstSeenAt", operation.Name);
        Assert.Equal("BTCPayServer.Plugins.Flint", operation.Schema);
        Assert.Equal("InvoicePaymentHashes", operation.Table);
        Assert.Equal(new[] { nameof(InvoicePaymentHash.FirstSeenAt) }, operation.Columns);

        // Index only — no column, data, or other-table operations ride along.
        Assert.Single(new PaymentHashRetentionIndex().UpOperations);

        var drop = Assert.Single(
            new PaymentHashRetentionIndex().DownOperations.OfType<DropIndexOperation>());
        Assert.Equal(operation.Name, drop.Name);
        Assert.Equal(operation.Schema, drop.Schema);
        Assert.Equal(operation.Table, drop.Table);
    }

    /// <summary>
    /// The quote table is new, touches no existing table, and carries the unique index attribution rests on.
    /// </summary>
    /// <remarks>
    /// The partial unique index on <c>SdkPaymentId</c> is the database half of "one payment settles one quote": the
    /// store's compare-and-set also checks it, but a filter widened to cover nulls would refuse every unsettled row
    /// after the first, and a dropped index would leave only the application check between one arrival and two
    /// credited invoices. The open-quote index is partial the other way round, so the matching walk reads only the
    /// small unsettled tail of a table that otherwise only grows.
    /// </remarks>
    [Fact]
    public void The_stablecoin_quote_table_indexes_what_attribution_depends_on()
    {
        var migration = new StablecoinQuotes();

        var create = Assert.Single(migration.UpOperations.OfType<CreateTableOperation>());
        Assert.Equal("StablecoinQuotes", create.Name);
        Assert.Equal("BTCPayServer.Plugins.Flint", create.Schema);
        Assert.DoesNotContain(migration.UpOperations, operation => operation is AddColumnOperation or AlterColumnOperation);

        var indexes = migration.UpOperations.OfType<CreateIndexOperation>().ToDictionary(i => i.Name);

        var byPayment = indexes["IX_StablecoinQuotes_SdkPaymentId"];
        Assert.True(byPayment.IsUnique);
        Assert.Equal(new[] { nameof(StablecoinQuote.SdkPaymentId) }, byPayment.Columns);
        Assert.Equal("\"SdkPaymentId\" IS NOT NULL", byPayment.Filter);

        var open = indexes["IX_StablecoinQuotes_StoreId_ExpiresAt_Open"];
        Assert.Equal(new[] { nameof(StablecoinQuote.StoreId), nameof(StablecoinQuote.ExpiresAt) }, open.Columns);
        Assert.Equal("\"SdkPaymentId\" IS NULL", open.Filter);

        var drop = Assert.Single(migration.DownOperations.OfType<DropTableOperation>());
        Assert.Equal("StablecoinQuotes", drop.Name);
        Assert.Equal("BTCPayServer.Plugins.Flint", drop.Schema);
    }

    /// <summary>The migrations describe the model the code runs against: nothing is left for a future one to find.</summary>
    [Fact]
    public void The_model_has_no_changes_the_migrations_do_not_carry()
    {
        using var context = new SparkPluginDesignTimeDbContextFactory().CreateDbContext([]);

        Assert.False(
            Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.HasPendingModelChanges(context.Database));
    }
}
