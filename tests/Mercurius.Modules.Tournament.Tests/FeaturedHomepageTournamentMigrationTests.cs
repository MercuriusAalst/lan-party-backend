using Mercurius.LAN.API.Data;
using Mercurius.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Mercurius.Modules.Tournament.Tests;

public sealed class FeaturedHomepageTournamentMigrationTests
{
    private const string PreviousMigrationId = "20261010081503_RestrictCrossModuleDeletes";
    private const string FeaturedMigrationId = "20261015142322_FeaturedHomepageTournaments";
    private const string SelectionTable = "tournament.featured_homepage_tournaments";

    [Fact]
    public async Task UpCreatesTableThatAcceptsOneToFourAndRejectsInvalidSelections()
    {
        await using var database = PostgresTestDatabase.Create();
        await using var db = CreateContext(database.ConnectionString);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(PreviousMigrationId);
        Assert.False(await TableExistsAsync(database.ConnectionString));

        await migrator.MigrateAsync(FeaturedMigrationId);
        Assert.True(await TableExistsAsync(database.ConnectionString));
        Assert.Contains(FeaturedMigrationId, await db.Database.GetAppliedMigrationsAsync());

        var firstIds = new[] { Guid.NewGuid() };
        await InsertSelectionAsync(database.ConnectionString, firstIds);
        Assert.Equal(firstIds, await ReadSelectionAsync(database.ConnectionString));

        foreach (var count in new[] { 1, 2, 3, 4 })
        {
            var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
            await UpdateSelectionAsync(db, ids);
            Assert.Equal(ids, await ReadSelectionAsync(database.ConnectionString));
        }

        var emptyViolation = await Assert.ThrowsAsync<PostgresException>(() => UpdateSelectionAsync(db, []));
        Assert.Equal(PostgresErrorCodes.CheckViolation, emptyViolation.SqlState);
        Assert.Equal("CK_featured_homepage_tournaments_count", emptyViolation.ConstraintName);

        var fiveIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var tooManyViolation = await Assert.ThrowsAsync<PostgresException>(() => UpdateSelectionAsync(db, fiveIds));
        Assert.Equal(PostgresErrorCodes.CheckViolation, tooManyViolation.SqlState);
        Assert.Equal("CK_featured_homepage_tournaments_count", tooManyViolation.ConstraintName);

        var singletonViolation = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteSelectionCommandAsync(
                database.ConnectionString,
                $"INSERT INTO {SelectionTable} (\"Id\", \"TournamentIds\") VALUES (2, @ids)",
                [Guid.NewGuid()]));
        Assert.Equal(PostgresErrorCodes.CheckViolation, singletonViolation.SqlState);
        Assert.Equal("CK_featured_homepage_tournaments_singleton", singletonViolation.ConstraintName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownDropsOnlyTheFeaturedTableAndReapplyingWorks(bool withSavedRow)
    {
        await using var database = PostgresTestDatabase.Create();
        await using var db = CreateContext(database.ConnectionString);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(FeaturedMigrationId);
        if (withSavedRow)
        {
            await InsertSelectionAsync(database.ConnectionString, [Guid.NewGuid(), Guid.NewGuid()]);
        }

        await migrator.MigrateAsync(PreviousMigrationId);
        Assert.False(await TableExistsAsync(database.ConnectionString));
        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.DoesNotContain(FeaturedMigrationId, applied);
        Assert.Contains(PreviousMigrationId, applied);

        await migrator.MigrateAsync(FeaturedMigrationId);
        Assert.True(await TableExistsAsync(database.ConnectionString));
        Assert.Contains(FeaturedMigrationId, await db.Database.GetAppliedMigrationsAsync());

        var ids = new[] { Guid.NewGuid() };
        await InsertSelectionAsync(database.ConnectionString, ids);
        Assert.Equal(ids, await ReadSelectionAsync(database.ConnectionString));
    }

    private static MercuriusDBContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<MercuriusDBContext>().UseNpgsql(connectionString).Options);

    private static async Task<bool> TableExistsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass('tournament.featured_homepage_tournaments') IS NOT NULL";
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static Task<int> InsertSelectionAsync(string connectionString, Guid[] tournamentIds) =>
        ExecuteSelectionCommandAsync(
            connectionString,
            "INSERT INTO tournament.featured_homepage_tournaments (\"Id\", \"TournamentIds\") VALUES (1, @ids)",
            tournamentIds);

    private static Task<int> UpdateSelectionAsync(MercuriusDBContext db, Guid[] tournamentIds) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE tournament.featured_homepage_tournaments
            SET "TournamentIds" = {tournamentIds}
            WHERE "Id" = 1
            """);

    private static async Task<int> ExecuteSelectionCommandAsync(
        string connectionString,
        string commandText,
        Guid[] tournamentIds)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.AddWithValue("ids", tournamentIds);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<Guid[]> ReadSelectionAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"TournamentIds\" FROM tournament.featured_homepage_tournaments WHERE \"Id\" = 1";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return reader.GetFieldValue<Guid[]>(0);
    }
}
