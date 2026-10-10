using Npgsql;
using Testcontainers.PostgreSql;
using Mercurius.LAN.API.Data;
using Microsoft.EntityFrameworkCore;

namespace Mercurius.TestInfrastructure;

internal static class PostgresTestDatabase
{
    public static PostgresTestDatabaseLease Create()
    {
        var databaseName = $"mercurius_tests_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(GetBaseConnectionString())
        {
            Database = "postgres"
        };

        using var connection = new NpgsqlConnection(adminBuilder.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        command.ExecuteNonQuery();

        var databaseBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
        {
            Database = databaseName
        };

        return new PostgresTestDatabaseLease(
            databaseName,
            adminBuilder.ConnectionString,
            databaseBuilder.ConnectionString);
    }

    public static MercuriusDBContext CreateDbContext()
    {
        var database = Create();
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseNpgsql(database.ConnectionString)
            .Options;
        try
        {
            using (var migrationContext = new MercuriusDBContext(options))
                migrationContext.Database.Migrate();

            return new DisposableMercuriusDbContext(options, database);
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    public static void Initialize(MercuriusDBContext dbContext)
    {
        dbContext.Database.Migrate();
    }

    // CI points TEST_POSTGRES_CONNECTION at its service container; elsewhere one PostgreSQL 17 container is
    // started per test process (Testcontainers' reaper removes it when the process exits).
    private static readonly Lazy<string> BaseConnectionString = new(() =>
        Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION") is { Length: > 0 } configured
            ? configured
            : StartContainer());

    private static string GetBaseConnectionString() => BaseConnectionString.Value;

    private static string StartContainer()
    {
        var container = new PostgreSqlBuilder("postgres:17").Build();
        // Start off xUnit's synchronization context: blocking on it while every test thread waits here deadlocks.
        Task.Run(() => container.StartAsync()).GetAwaiter().GetResult();
        return container.GetConnectionString();
    }

    private sealed class DisposableMercuriusDbContext(
        DbContextOptions<MercuriusDBContext> options,
        PostgresTestDatabaseLease database) : MercuriusDBContext(options)
    {
        public override void Dispose()
        {
            try
            {
                base.Dispose();
            }
            finally
            {
                database.Dispose();
            }
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                await base.DisposeAsync();
            }
            finally
            {
                await database.DisposeAsync();
            }
        }
    }
}

internal sealed class PostgresTestDatabaseLease : IDisposable, IAsyncDisposable
{
    private readonly string _adminConnectionString;
    private readonly string _databaseName;
    private int _disposed;

    internal PostgresTestDatabaseLease(
        string databaseName,
        string adminConnectionString,
        string connectionString)
    {
        _databaseName = databaseName;
        _adminConnectionString = adminConnectionString;
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
        GC.SuppressFinalize(this);
    }
}
