using Npgsql;

namespace JNotes.Tests.Infrastructure;

/// <summary>
/// A throwaway PostgreSQL database for one test class: CREATE DATABASE in setup, DROP in teardown.
/// The replacement for "a temp SQLite file per test class", and deliberately a whole database
/// rather than a schema so EF Core migrations run against a virgin database exactly as they do in
/// production — the same fidelity-over-convenience reasoning that keeps these tests off EF Core's
/// in-memory provider.
/// Point the tests at a different server with JNOTES_TEST_POSTGRES_CONNECTION_STRING, mirroring how
/// POSTGRES_CONNECTION_STRING works for the app. The configured user needs CREATEDB (the official
/// postgres image's POSTGRES_USER is a superuser, so the documented `docker run` just works).
/// </summary>
internal sealed class TestPostgresDatabase : IAsyncDisposable
{
    private const string ConnectionStringEnvVar = "JNOTES_TEST_POSTGRES_CONNECTION_STRING";
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=jnotes;Username=jnotes;Password=jnotes";
    private const int MaxCreateAttempts = 3;
    private static readonly TimeSpan CreateRetryDelay = TimeSpan.FromMilliseconds(500);

    private readonly string _maintenanceConnectionString;
    private readonly string _databaseName;

    private TestPostgresDatabase(string maintenanceConnectionString, string databaseName, string connectionString)
    {
        _maintenanceConnectionString = maintenanceConnectionString;
        _databaseName = databaseName;
        ConnectionString = connectionString;
    }

    /// <summary>The connection string for this test's own database — what JNotesOptions gets.</summary>
    public string ConnectionString { get; }

    /// <param name="namePrefix">Prefix of the generated database name; <c>jnotes_test_</c> for the unit/integration
    /// suite, <c>jnotes_ui_</c> for the browser suite (so a leftover is attributable at a glance in <c>\l</c>).</param>
    public static async Task<TestPostgresDatabase> CreateAsync(string namePrefix = "jnotes_test_")
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvVar) ?? DefaultConnectionString;
        var maintenance = new NpgsqlConnectionStringBuilder(baseConnectionString);
        if (string.IsNullOrEmpty(maintenance.Database))
        {
            // CREATE/DROP DATABASE need *some* existing database to connect to.
            maintenance.Database = "postgres";
        }

        var databaseName = namePrefix + Guid.NewGuid().ToString("N");
        var target = new NpgsqlConnectionStringBuilder(maintenance.ConnectionString) { Database = databaseName };

        // The name is interpolated because PostgreSQL can't parameterize identifiers; it's a
        // locally-generated GUID, never user input.
        var createDatabaseSql = $"CREATE DATABASE \"{databaseName}\"";

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(maintenance.ConnectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(createDatabaseSql, connection);
                await command.ExecuteNonQueryAsync();
                break;
            }
            catch (PostgresException) when (attempt < MaxCreateAttempts)
            {
                // xUnit runs test classes in parallel, so several CREATE DATABASE statements can
                // overlap and contend on the template database — retry rather than serializing
                // every DB-touching test class into one xUnit collection.
                await Task.Delay(CreateRetryDelay);
            }
        }

        return new TestPostgresDatabase(maintenance.ConnectionString, databaseName, target.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        // Npgsql pools per connection string, and disposing EF's contexts only returns connections to
        // the pool rather than closing them — DROP DATABASE would fail with "database is being accessed
        // by other users". Clearing the pools handles the common case; WITH (FORCE) (PostgreSQL 13+)
        // terminates anything still attached.
        NpgsqlConnection.ClearAllPools();

        await using var connection = new NpgsqlConnection(_maintenanceConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}
