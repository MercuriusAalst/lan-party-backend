using Mercurius.LAN.API.Data;
using Mercurius.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Mercurius.Modules.Tournament.Tests;

public sealed class FeaturedHomepageTournamentMigrationTests
{
    private const string PreviousMigrationId = "20261009160724_FeaturedHomepageTournaments";
    private const string RangeMigrationId = "20261011200010_FeaturedHomepageTournamentCountRange";

    [Fact]
    public async Task RangeMigrationPreservesFourItemDataAndEnforcesOneToFourAcrossUpgradeAndRollback()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseNpgsql(database.ConnectionString)
            .Options;
        await using var db = new MercuriusDBContext(options);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(PreviousMigrationId);
        var originalIds = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        await InsertSelectionAsync(database.ConnectionString, originalIds);

        await migrator.MigrateAsync(RangeMigrationId);
        Assert.Equal(originalIds, await ReadSelectionAsync(database.ConnectionString));

        foreach (var count in new[] { 1, 3, 4 })
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

        var partialIds = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        await UpdateSelectionAsync(db, partialIds);
        await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(PreviousMigrationId));

        Assert.Contains(RangeMigrationId, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(partialIds, await ReadSelectionAsync(database.ConnectionString));

        var stillWidenedIds = new[] { Guid.NewGuid() };
        await UpdateSelectionAsync(db, stillWidenedIds);
        Assert.Equal(stillWidenedIds, await ReadSelectionAsync(database.ConnectionString));

        await UpdateSelectionAsync(db, originalIds);
        await migrator.MigrateAsync(PreviousMigrationId);
        Assert.DoesNotContain(RangeMigrationId, await db.Database.GetAppliedMigrationsAsync());

        var narrowedViolation = await Assert.ThrowsAsync<PostgresException>(() =>
            UpdateSelectionAsync(db, [Guid.NewGuid()]));
        Assert.Equal(PostgresErrorCodes.CheckViolation, narrowedViolation.SqlState);
        Assert.Equal("CK_featured_homepage_tournaments_count", narrowedViolation.ConstraintName);
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
