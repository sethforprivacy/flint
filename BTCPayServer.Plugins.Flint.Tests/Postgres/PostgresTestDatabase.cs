using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.Flint.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests.Postgres;

/// <summary>
/// A real Postgres database behind the production <see cref="SparkPluginDbContextFactory"/>.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately the production factory rather than a hand-rolled <c>DbContextOptionsBuilder</c>. The factory
/// inherits <c>BaseDbContextFactory.ConfigureBuilder</c>, which enables <c>EnableRetryOnFailure(10)</c> — and
/// that setting is the whole reason these tests exist. EF Core's retrying execution strategy refuses
/// user-initiated transactions, throwing <c>InvalidOperationException</c> on the first operation inside one, so
/// a store that opens a transaction fails every single call. A hand-configured context without the retry
/// strategy would pass happily and prove nothing.
/// </para>
/// <para>
/// The plugin's <c>DbContext</c> pins its tables to a fixed schema, so per-test isolation cannot come from the
/// connection's search path: the schema is created once and each test truncates it. Tests that use it therefore
/// share one xunit collection and do not run in parallel.
/// </para>
/// <para>
/// Enabled by setting <c>SPARK_POSTGRES_TESTS</c> to a connection string; see docs/testing.md for a one-line Docker
/// command.
/// </para>
/// </remarks>
public sealed class PostgresTestDatabase : IAsyncLifetime
{
    public const string EnvironmentVariable = "SPARK_POSTGRES_TESTS";

    public const string CollectionName = "Spark Postgres";

    public static string SkipReason =>
        $"Set {EnvironmentVariable} to a Postgres connection string to run the store contract against a real "
        + "database (see docs/testing.md).";

    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } value ? value : null;

    public static bool IsEnabled => ConnectionString is not null;

    private SparkPluginDbContextFactory? _factory;

    /// <summary>
    /// Database names this fixture refuses to touch.
    /// </summary>
    /// <remarks>
    /// <see cref="InitializeAsync"/> calls <c>EnsureDeletedAsync</c>, which drops the whole target database.
    /// Pointing the variable at a server's default <c>postgres</c> database would therefore destroy it, and the
    /// mistake is easy to make because that is what a bare connection string defaults to. Refuse instead.
    /// </remarks>
    private static readonly string[] ProtectedDatabases = ["postgres", "template0", "template1"];

    public async ValueTask InitializeAsync()
    {
        if (ConnectionString is not { } connectionString)
            return;

        var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
        if (string.IsNullOrEmpty(database) ||
            ProtectedDatabases.Contains(database, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariable} points at the database '{database}', which these tests refuse to use: "
                + "they drop and recreate the target database, so that would destroy it. Point the connection "
                + "string at a dedicated throwaway database (see docs/testing.md).");
        }

        _factory = new SparkPluginDbContextFactory(
            Options.Create(new DatabaseOptions { ConnectionString = connectionString }),
            NullLoggerFactory.Instance);

        await using var context = _factory.CreateContext();
        // Built from the current model rather than by running migrations: these tests are about the store's
        // runtime behaviour against real SQL, and rebuilding from the model keeps them from also failing every
        // time a migration is added. The migrations themselves are exercised by the plugin's startup task.
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Returns a factory against an empty database, skipping the test when Postgres is not configured.
    /// </summary>
    public async Task<SparkPluginDbContextFactory> CreateFactoryAsync()
    {
        Assert.SkipUnless(IsEnabled, SkipReason);
        var factory = _factory
            ?? throw new InvalidOperationException("The Postgres test database was not initialised.");

        await using var context = factory.CreateContext();
        // TRUNCATE rather than dropping and recreating the schema: it is far faster, and it keeps the schema
        // identical to what EnsureCreated built, so a test cannot accidentally pass against a stale shape.
        await context.Database.ExecuteSqlRawAsync(
            $"""
             TRUNCATE TABLE
                 "{Constants.DatabaseSchema}"."InvoiceRecords",
                 "{Constants.DatabaseSchema}"."OutgoingPayments",
                 "{Constants.DatabaseSchema}"."SweepRecords",
                 "{Constants.DatabaseSchema}"."UnilateralExitRecords";
             """);
        return factory;
    }
}

/// <summary>
/// Serialises everything that shares the one Postgres database.
/// </summary>
[CollectionDefinition(PostgresTestDatabase.CollectionName, DisableParallelization = true)]
public sealed class PostgresTestCollection : ICollectionFixture<PostgresTestDatabase>;
