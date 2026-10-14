using Npgsql;
using Testcontainers.PostgreSql;
using Mercurius.LAN.API.Data;
using Microsoft.EntityFrameworkCore;

namespace Mercurius.TestInfrastructure;

internal static class PostgresTestDatabase
{
    // An empty database, for tests that drive migrations themselves.
    public static PostgresTestDatabaseLease Create() => Create(template: null);

    // A fully migrated database, copied from a template migrated once per test process.
    public static PostgresTestDatabaseLease CreateMigrated() => Create(MigratedTemplate.Value);

    private static PostgresTestDatabaseLease Create(string? template)
    {
        var databaseName = $"mercurius_tests_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(GetBaseConnectionString())
        {
            Database = "postgres"
        };

        ExecuteAdmin(adminBuilder.ConnectionString, template is null
            ? $"CREATE DATABASE \"{databaseName}\""
            : $"CREATE DATABASE \"{databaseName}\" TEMPLATE \"{template}\"");

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
        var database = CreateMigrated();
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseNpgsql(database.ConnectionString)
            .Options;
        return new DisposableMercuriusDbContext(options, database);
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

    private static readonly Lazy<string> MigratedTemplate = new(CreateMigratedTemplate);

    private static string CreateMigratedTemplate()
    {
        var templateName = $"mercurius_template_{Guid.NewGuid():N}";
        var adminConnectionString = new NpgsqlConnectionStringBuilder(GetBaseConnectionString())
        {
            Database = "postgres"
        }.ConnectionString;
        ExecuteAdmin(adminConnectionString, $"CREATE DATABASE \"{templateName}\"");

        // No pooling: PostgreSQL refuses to copy a template that still has open connections.
        var templateConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = templateName,
            Pooling = false
        }.ConnectionString;
        using (var migrationContext = new MercuriusDBContext(new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseNpgsql(templateConnectionString)
            .Options))
            migrationContext.Database.Migrate();

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            ExecuteAdmin(adminConnectionString, $"DROP DATABASE IF EXISTS \"{templateName}\" WITH (FORCE)");
        return templateName;
    }

    private static void ExecuteAdmin(string adminConnectionString, string sql)
    {
        using var connection = new NpgsqlConnection(adminConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

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
