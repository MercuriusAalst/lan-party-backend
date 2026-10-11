using Mercurius.LAN.API.Data;
using Mercurius.Modules.Media.Contracts;
using Mercurius.Modules.Shared.Exceptions;
using Mercurius.Modules.Tournament.Application.DTOs.Leaderboards;
using Mercurius.Modules.Tournament.Application.DTOs.Placements;
using Mercurius.Modules.Tournament.Application.Services;
using Mercurius.Modules.Tournament.Infrastructure;
using Mercurius.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using System.Text.Json;
using Contracts = Mercurius.Modules.Tournament.Contracts;

namespace Mercurius.Modules.Tournament.Tests;

public sealed class LeaderboardPersistenceTests
{
    private const string PreviousMigrationId = "20260829110558_MatchResolutionNotifications";

    private const string TournamentTablesSql =
        "SELECT table_name FROM information_schema.tables WHERE table_schema = 'tournament'";

    private const string TournamentColumnsSql =
        "SELECT column_name FROM information_schema.columns WHERE table_schema = 'tournament' AND table_name = 'tournaments'";

    private const string ScoreColumnTypeSql =
        "SELECT data_type || '(' || numeric_precision || ',' || numeric_scale || ')' FROM information_schema.columns " +
        "WHERE table_schema = 'tournament' AND table_name = 'leaderboard_attempts' AND column_name = 'Score'";

    private const string MetricCheckConstraintSql =
        "SELECT conname FROM pg_constraint WHERE conname = 'CK_leaderboard_attempts_metric_value'";

    private const string LinkedUserIndexSql =
        "SELECT indexname FROM pg_indexes WHERE schemaname = 'tournament' " +
        "AND indexname = 'IX_leaderboard_participants_TournamentId_LinkedUserId'";

    [Fact]
    public async Task Migration_AppliesAndRollsBackPhysicalLeaderboardSchema()
    {
        await using var database = PostgresTestDatabase.Create();
        await using var db = new MercuriusDBContext(CreateOptions(database));
        await db.Database.MigrateAsync();

        var connectionString = database.ConnectionString;
        Assert.Contains("leaderboard_participants", await QueryStringsAsync(connectionString, TournamentTablesSql));
        Assert.Contains("leaderboard_attempts", await QueryStringsAsync(connectionString, TournamentTablesSql));
        Assert.Contains("placement_leaderboard_participants", await QueryStringsAsync(connectionString, TournamentTablesSql));
        Assert.Contains("LeaderboardRevision", await QueryStringsAsync(connectionString, TournamentColumnsSql));
        Assert.Contains("LeaderboardRankingMetric", await QueryStringsAsync(connectionString, TournamentColumnsSql));
        Assert.Equal(["numeric(18,6)"], await QueryStringsAsync(connectionString, ScoreColumnTypeSql));
        Assert.Contains("CK_leaderboard_attempts_metric_value", await QueryStringsAsync(connectionString, MetricCheckConstraintSql));
        Assert.Contains(
            "IX_leaderboard_participants_TournamentId_LinkedUserId",
            await QueryStringsAsync(connectionString, LinkedUserIndexSql));

        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigrationId);

        Assert.DoesNotContain("leaderboard_participants", await QueryStringsAsync(connectionString, TournamentTablesSql));
        Assert.DoesNotContain("leaderboard_attempts", await QueryStringsAsync(connectionString, TournamentTablesSql));
        Assert.DoesNotContain("placement_leaderboard_participants", await QueryStringsAsync(connectionString, TournamentTablesSql));
        Assert.DoesNotContain("LeaderboardRevision", await QueryStringsAsync(connectionString, TournamentColumnsSql));
        Assert.DoesNotContain("LeaderboardRankingMetric", await QueryStringsAsync(connectionString, TournamentColumnsSql));
        Assert.Empty(await QueryStringsAsync(connectionString, MetricCheckConstraintSql));
        Assert.Empty(await QueryStringsAsync(connectionString, LinkedUserIndexSql));

        await migrator.MigrateAsync();

        Assert.Contains("leaderboard_attempts", await QueryStringsAsync(connectionString, TournamentTablesSql));
        Assert.Contains("LeaderboardRevision", await QueryStringsAsync(connectionString, TournamentColumnsSql));
    }

    [Fact]
    public async Task AttemptValues_EnforceDatabasePrecisionAndMetricCheckConstraint()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = CreateOptions(database);
        var tournament = await SeedInProgressLeaderboardAsync(options);
        await using var db = new MercuriusDBContext(options);
        var service = CreateLeaderboardService(db);

        var participant = await service.RecordAttemptAsync(
            tournament.Id,
            new RecordLeaderboardAttemptDTO { GuestDisplayName = "Precision", Score = 999_999_999_999.123456m });

        var attemptId = participant.Attempts.Single().Id;
        await using (var verify = new MercuriusDBContext(options))
        {
            var stored = await verify.Set<LeaderboardAttempt>().AsNoTracking().SingleAsync(item => item.Id == attemptId);
            Assert.Equal(999_999_999_999.123456m, stored.Score);
            Assert.Null(stored.DurationMilliseconds);
        }

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO tournament.leaderboard_attempts " +
            "(\"Id\", \"ParticipantId\", \"Score\", \"DurationMilliseconds\", \"CreatedAtUtc\", \"UpdatedAtUtc\", \"RowVersion\") " +
            "VALUES (@id, @participantId, 5, 100, now(), now(), @rowVersion)";
        insert.Parameters.AddWithValue("id", Guid.NewGuid());
        insert.Parameters.AddWithValue("participantId", participant.Id);
        insert.Parameters.AddWithValue("rowVersion", Guid.NewGuid());

        var exception = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(1, await CountAsync(database.ConnectionString, "tournament.leaderboard_attempts"));
    }

    [Fact]
    public async Task PartialUniqueIndex_RejectsDuplicateLinkedParticipantsAndKeepsGuestRowsIndependent()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = CreateOptions(database);
        var tournament = await SeedInProgressLeaderboardAsync(options);
        var linkedUserId = Guid.NewGuid();

        await InsertParticipantAsync(database.ConnectionString, tournament.Id, Guid.NewGuid(), "Guest one");
        await InsertParticipantAsync(database.ConnectionString, tournament.Id, Guid.NewGuid(), "Guest one");
        await InsertParticipantAsync(database.ConnectionString, tournament.Id, linkedUserId, "Linked");

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertParticipantAsync(database.ConnectionString, tournament.Id, linkedUserId, "Linked again"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal(3, await CountAsync(database.ConnectionString, "tournament.leaderboard_participants"));
    }

    [Theory]
    [InlineData(StaleLifecycle.Complete, true)]
    [InlineData(StaleLifecycle.Complete, false)]
    [InlineData(StaleLifecycle.Cancel, true)]
    [InlineData(StaleLifecycle.Cancel, false)]
    public async Task MutationAndLifecycle_ConflictWhenBothWritersLoadedTheSameRevision(
        StaleLifecycle lifecycle,
        bool mutationCommitsFirst)
    {
        await using var database = PostgresTestDatabase.Create();
        var options = CreateOptions(database);
        var tournament = await SeedInProgressLeaderboardAsync(
            options,
            aggregate => AddGuestParticipant(aggregate, "Recorded", 1m));
        await using var mutationDb = new MercuriusDBContext(options);
        await using var lifecycleDb = new MercuriusDBContext(options);
        var mutationService = CreateLeaderboardService(mutationDb);
        var lifecycleService = CreateTournamentService(lifecycleDb);
        await LoadTrackedTournamentAsync(mutationDb, tournament.Id);
        await LoadTrackedTournamentAsync(lifecycleDb, tournament.Id);

        var lateAttempt = () => mutationService.RecordAttemptAsync(
            tournament.Id,
            new RecordLeaderboardAttemptDTO { GuestDisplayName = "Late", Score = 7m });

        if (mutationCommitsFirst)
        {
            await lateAttempt();
            var conflict = await Assert.ThrowsAsync<ConflictException>(() =>
                RunLifecycleAsync(lifecycleService, lifecycle, tournament.Id));
            Assert.Equal("leaderboard_changed", conflict.Code);
        }
        else
        {
            await RunLifecycleAsync(lifecycleService, lifecycle, tournament.Id);
            var conflict = await Assert.ThrowsAsync<ConflictException>(lateAttempt);
            Assert.Equal("leaderboard_changed", conflict.Code);
        }

        await using var verify = new MercuriusDBContext(options);
        var persisted = await verify.Set<TournamentAggregate>().AsNoTracking().SingleAsync(item => item.Id == tournament.Id);
        Assert.Equal(1, persisted.LeaderboardRevision);
        var expectedStatus = mutationCommitsFirst
            ? TournamentStatus.InProgress
            : lifecycle == StaleLifecycle.Complete ? TournamentStatus.Completed : TournamentStatus.Canceled;
        Assert.Equal(expectedStatus, persisted.Status);
        Assert.Equal(mutationCommitsFirst ? 2 : 1, await CountAsync(database.ConnectionString, "tournament.leaderboard_participants"));
        Assert.Equal(mutationCommitsFirst ? 2 : 1, await CountAsync(database.ConnectionString, "tournament.leaderboard_attempts"));
    }

    [Fact]
    public async Task ConcurrentDuplicateLinkedParticipant_ConflictsAndRollsBackSecondWriter()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = CreateOptions(database);
        var tournament = await SeedInProgressLeaderboardAsync(options);
        var linkedUser = new User
        {
            Id = Guid.NewGuid(),
            Username = "runner",
            NormalizedUsername = "RUNNER",
            Firstname = "Ada",
            Lastname = "Lovelace"
        };
        await using var winnerDb = new MercuriusDBContext(options);
        await using var loserDb = new MercuriusDBContext(options);
        var winnerService = CreateLeaderboardService(winnerDb, [linkedUser]);
        var loserService = CreateLeaderboardService(loserDb, [linkedUser]);
        await LoadTrackedTournamentAsync(winnerDb, tournament.Id);
        await LoadTrackedTournamentAsync(loserDb, tournament.Id);

        await winnerService.RecordAttemptAsync(
            tournament.Id,
            new RecordLeaderboardAttemptDTO { LinkedUserId = linkedUser.Id, Score = 10m });

        var conflict = await Assert.ThrowsAsync<ConflictException>(() => loserService.RecordAttemptAsync(
            tournament.Id,
            new RecordLeaderboardAttemptDTO { LinkedUserId = linkedUser.Id, Score = 20m }));

        // The second writer loses on the tournament revision before its insert can reach the
        // partial unique index, so the revision conflict is the observable failure here.
        Assert.Equal("leaderboard_changed", conflict.Code);
        Assert.Equal(1, await CountAsync(database.ConnectionString, "tournament.leaderboard_participants"));
        Assert.Equal(1, await CountAsync(database.ConnectionString, "tournament.leaderboard_attempts"));
        await using var verify = new MercuriusDBContext(options);
        var attempt = await verify.Set<LeaderboardAttempt>().AsNoTracking().SingleAsync();
        Assert.Equal(10m, attempt.Score);
    }

    [Fact]
    public async Task StaleRowVersion_RejectsEditAndDeleteAndKeepsStoredValue()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = CreateOptions(database);
        LeaderboardParticipant seeded = null!;
        var tournament = await SeedInProgressLeaderboardAsync(
            options,
            aggregate => seeded = AddGuestParticipant(aggregate, "Recorded", 1m));
        var attemptId = seeded.Attempts.Single().Id;
        var staleRowVersion = seeded.Attempts.Single().RowVersion;

        Guid currentRowVersion;
        await using (var firstDb = new MercuriusDBContext(options))
        {
            var updated = await CreateLeaderboardService(firstDb).UpdateAttemptAsync(
                tournament.Id,
                attemptId,
                new UpdateLeaderboardAttemptDTO { Score = 20m, RowVersion = staleRowVersion });
            currentRowVersion = updated.RowVersion;
        }

        Assert.NotEqual(staleRowVersion, currentRowVersion);
        await using (var staleEditDb = new MercuriusDBContext(options))
        {
            var conflict = await Assert.ThrowsAsync<ConflictException>(() => CreateLeaderboardService(staleEditDb).UpdateAttemptAsync(
                tournament.Id,
                attemptId,
                new UpdateLeaderboardAttemptDTO { Score = 30m, RowVersion = staleRowVersion }));
            Assert.Equal("leaderboard_attempt_changed", conflict.Code);
        }

        await using (var staleDeleteDb = new MercuriusDBContext(options))
        {
            var conflict = await Assert.ThrowsAsync<ConflictException>(() => CreateLeaderboardService(staleDeleteDb)
                .RemoveAttemptAsync(tournament.Id, attemptId, staleRowVersion));
            Assert.Equal("leaderboard_attempt_changed", conflict.Code);
        }

        await using (var verify = new MercuriusDBContext(options))
        {
            var stored = await verify.Set<LeaderboardAttempt>().AsNoTracking().SingleAsync(item => item.Id == attemptId);
            Assert.Equal(20m, stored.Score);
            Assert.Equal(currentRowVersion, stored.RowVersion);
        }

        await using (var deleteDb = new MercuriusDBContext(options))
            await CreateLeaderboardService(deleteDb).RemoveAttemptAsync(tournament.Id, attemptId, currentRowVersion);

        Assert.Equal(0, await CountAsync(database.ConnectionString, "tournament.leaderboard_attempts"));
    }

    [Fact]
    public async Task ResetAfterCompletion_CascadesLeaderboardParticipantsAttemptsAndPlacements()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = CreateOptions(database);
        var linkedUser = new User
        {
            Id = Guid.NewGuid(),
            Username = "runner",
            NormalizedUsername = "RUNNER",
            Firstname = "Ada",
            Lastname = "Lovelace"
        };
        var tournament = await SeedInProgressLeaderboardAsync(options, aggregate =>
        {
            AddGuestParticipant(aggregate, "Guest winner", 50m);
            AddLinkedParticipant(aggregate, linkedUser.Id, 40m, 45m);
        });

        await using (var completionDb = new MercuriusDBContext(options))
        {
            var placements = (await CreateTournamentService(completionDb).CompleteTournamentAsync(tournament.Id)).ToList();
            Assert.Equal([1, 2], placements.Select(item => item.Place).OrderBy(place => place));
        }

        Assert.Equal(2, await CountAsync(database.ConnectionString, "tournament.leaderboard_participants"));
        Assert.Equal(3, await CountAsync(database.ConnectionString, "tournament.leaderboard_attempts"));
        Assert.Equal(2, await CountAsync(database.ConnectionString, "tournament.placements"));
        Assert.Equal(2, await CountAsync(database.ConnectionString, "tournament.placement_leaderboard_participants"));

        await using (var resetDb = new MercuriusDBContext(options))
            await CreateTournamentService(resetDb).ResetTournamentAsync(tournament.Id);

        Assert.Equal(0, await CountAsync(database.ConnectionString, "tournament.leaderboard_participants"));
        Assert.Equal(0, await CountAsync(database.ConnectionString, "tournament.leaderboard_attempts"));
        Assert.Equal(0, await CountAsync(database.ConnectionString, "tournament.placements"));
        Assert.Equal(0, await CountAsync(database.ConnectionString, "tournament.placement_leaderboard_participants"));
        await using var verify = new MercuriusDBContext(options);
        var persisted = await verify.Set<TournamentAggregate>().AsNoTracking().SingleAsync(item => item.Id == tournament.Id);
        Assert.Equal(TournamentStatus.Scheduled, persisted.Status);
        Assert.Equal(2, persisted.LeaderboardRevision);
    }

    [Fact]
    public async Task StaleMutationAfterCompletionAndReset_CannotRestoreLeaderboardRows()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = CreateOptions(database);
        var tournament = await SeedInProgressLeaderboardAsync(
            options,
            aggregate => AddGuestParticipant(aggregate, "Recorded", 1m));
        await using var staleDb = new MercuriusDBContext(options);
        var staleService = CreateLeaderboardService(staleDb);
        await LoadTrackedTournamentAsync(staleDb, tournament.Id);

        await using (var completionDb = new MercuriusDBContext(options))
            await CreateTournamentService(completionDb).CompleteTournamentAsync(tournament.Id);
        await using (var resetDb = new MercuriusDBContext(options))
            await CreateTournamentService(resetDb).ResetTournamentAsync(tournament.Id);

        Assert.Equal(0, await CountAsync(database.ConnectionString, "tournament.leaderboard_participants"));

        var conflict = await Assert.ThrowsAsync<ConflictException>(() => staleService.RecordAttemptAsync(
            tournament.Id,
            new RecordLeaderboardAttemptDTO { GuestDisplayName = "Late", Score = 7m }));

        Assert.Equal("leaderboard_changed", conflict.Code);
        Assert.Equal(0, await CountAsync(database.ConnectionString, "tournament.leaderboard_participants"));
        Assert.Equal(0, await CountAsync(database.ConnectionString, "tournament.leaderboard_attempts"));
        Assert.Equal(0, await CountAsync(database.ConnectionString, "tournament.placements"));
    }

    [Fact]
    public async Task PublicLeaderboard_ProjectsBestResultPerParticipantWithoutAdminHistory()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = CreateOptions(database);
        var linkedUser = new User
        {
            Id = Guid.NewGuid(),
            Username = "runner",
            NormalizedUsername = "RUNNER",
            Firstname = "Ada",
            Lastname = "Lovelace"
        };
        var tournament = await SeedInProgressLeaderboardAsync(options, aggregate =>
        {
            AddGuestParticipant(aggregate, "Best", 90m, 100m);
            AddLinkedParticipant(aggregate, linkedUser.Id, 100m);
            AddGuestParticipant(aggregate, "Third", 80m);
        });

        await using var db = new MercuriusDBContext(options);
        var response = await CreateLeaderboardService(db).GetPublicLeaderboardAsync(tournament.Id);

        Assert.Equal(tournament.Id, response.TournamentId);
        Assert.Equal(Contracts.LeaderboardRankingMetric.HighestScore, response.RankingMetric);
        Assert.Equal([1, 1, 3], response.Rows.Select(row => row.Rank));
        Assert.Equal([100m, 100m, 80m], response.Rows.Select(row => row.Score));
        Assert.All(response.Rows, row => Assert.Null(row.DurationMilliseconds));

        var guest = response.Rows.Single(row => row.DisplayName == "Best");
        Assert.Equal(Contracts.LeaderboardParticipantKind.Guest, guest.ParticipantKind);
        Assert.Null(guest.LinkedUserId);
        var linked = response.Rows.Single(row => row.LinkedUserId == linkedUser.Id);
        Assert.Equal("runner", linked.DisplayName);
        Assert.Equal(Contracts.LeaderboardParticipantKind.LinkedUser, linked.ParticipantKind);
        Assert.Equal(linkedUser.Id, linked.LinkedUserId);

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"rows\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"participants\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"attempts\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"rowVersion\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"createdAtUtc\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"updatedAtUtc\":", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminLeaderboard_ReturnsFullAttemptHistoryAfterCorrection()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = CreateOptions(database);
        LeaderboardParticipant seeded = null!;
        var tournament = await SeedInProgressLeaderboardAsync(
            options,
            aggregate => seeded = AddGuestParticipant(aggregate, "Recorded", 1m));
        var correctedAttemptId = seeded.Attempts.Single().Id;
        var staleRowVersion = seeded.Attempts.Single().RowVersion;

        Guid correctedRowVersion;
        await using (var mutationDb = new MercuriusDBContext(options))
        {
            var service = CreateLeaderboardService(mutationDb);
            await service.RecordAttemptAsync(
                tournament.Id,
                new RecordLeaderboardAttemptDTO { ParticipantId = seeded.Id, Score = 5m });
            correctedRowVersion = (await service.UpdateAttemptAsync(
                tournament.Id,
                correctedAttemptId,
                new UpdateLeaderboardAttemptDTO { Score = 2m, RowVersion = staleRowVersion })).RowVersion;
        }

        await using var db = new MercuriusDBContext(options);
        var response = await CreateLeaderboardService(db).GetAdminLeaderboardAsync(tournament.Id);

        Assert.Equal(tournament.Id, response.TournamentId);
        Assert.Equal(Contracts.LeaderboardRankingMetric.HighestScore, response.RankingMetric);
        var participant = Assert.Single(response.Participants);
        Assert.Equal(seeded.Id, participant.Id);
        Assert.Equal("Recorded", participant.DisplayName);
        Assert.Equal(Contracts.LeaderboardParticipantKind.Guest, participant.ParticipantKind);
        Assert.Null(participant.LinkedUserId);
        Assert.Equal(2, participant.Attempts.Count);

        var corrected = participant.Attempts.Single(item => item.Id == correctedAttemptId);
        Assert.Equal(2m, corrected.Score);
        Assert.Equal(correctedRowVersion, corrected.RowVersion);
        Assert.NotEqual(staleRowVersion, corrected.RowVersion);
        Assert.True(corrected.UpdatedAtUtc >= corrected.CreatedAtUtc);

        var recorded = participant.Attempts.Single(item => item.Id != correctedAttemptId);
        Assert.Equal(5m, recorded.Score);
        Assert.Equal(recorded.CreatedAtUtc, recorded.UpdatedAtUtc);

        var adminJson = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"participants\":", adminJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"attempts\":", adminJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"rowVersion\":", adminJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecordAttempt_WhenPublicUsernameLookupFails_DoesNotPersistAttempt()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = CreateOptions(database);
        var linkedUser = new User
        {
            Id = Guid.NewGuid(),
            Username = "runner",
            NormalizedUsername = "RUNNER",
            Firstname = "Ada",
            Lastname = "Lovelace"
        };
        var tournament = await SeedInProgressLeaderboardAsync(options);

        await using var db = new MercuriusDBContext(options);
        var service = CreateLeaderboardService(db, [linkedUser], throwOnPublicUsernameLookup: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordAttemptAsync(
            tournament.Id,
            new RecordLeaderboardAttemptDTO { LinkedUserId = linkedUser.Id, Score = 12m }));

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.Set<LeaderboardAttempt>().CountAsync());
        Assert.Equal(0, await db.Set<LeaderboardParticipant>().CountAsync());

        var response = await CreateLeaderboardService(db, [linkedUser]).RecordAttemptAsync(
            tournament.Id,
            new RecordLeaderboardAttemptDTO { LinkedUserId = linkedUser.Id, Score = 12m });
        Assert.Equal("runner", response.DisplayName);
        Assert.Equal(1, await db.Set<LeaderboardAttempt>().CountAsync());
    }

    [Fact]
    public async Task RecordAttempt_WhenLinkedProfileHasNoPublicUsername_StoresGenericLabel()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = CreateOptions(database);
        var incompleteUser = new User
        {
            Id = Guid.NewGuid(),
            Username = "pending",
            Firstname = "Ada",
            Lastname = "Lovelace"
        };
        var tournament = await SeedInProgressLeaderboardAsync(options);

        await using var db = new MercuriusDBContext(options);
        var service = CreateLeaderboardService(db, [incompleteUser]);

        var response = await service.RecordAttemptAsync(
            tournament.Id,
            new RecordLeaderboardAttemptDTO { LinkedUserId = incompleteUser.Id, Score = 12m });

        Assert.Equal("Incomplete profile", response.DisplayName);
        Assert.DoesNotContain("Ada", response.DisplayName, StringComparison.Ordinal);
        Assert.DoesNotContain("Lovelace", response.DisplayName, StringComparison.Ordinal);

        await using var verify = new MercuriusDBContext(options);
        var stored = await verify.Set<LeaderboardParticipant>().AsNoTracking().SingleAsync();
        Assert.Equal("Incomplete profile", stored.DisplayName);

        var publicResponse = await CreateLeaderboardService(verify).GetPublicLeaderboardAsync(tournament.Id);
        Assert.Equal("Incomplete profile", publicResponse.Rows.Single().DisplayName);
    }

    [Fact]
    public async Task Completion_MapsFinalPlacementsToLeaderboardParticipants()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = CreateOptions(database);
        var linkedUser = new User
        {
            Id = Guid.NewGuid(),
            Username = "runner",
            NormalizedUsername = "RUNNER",
            Firstname = "Ada",
            Lastname = "Lovelace"
        };
        LeaderboardParticipant guest = null!;
        LeaderboardParticipant linked = null!;
        var tournament = await SeedInProgressLeaderboardAsync(options, aggregate =>
        {
            guest = AddGuestParticipant(aggregate, "Guest winner", 50m);
            linked = AddLinkedParticipant(aggregate, linkedUser.Id, 45m);
        });

        IReadOnlyList<GetPlacementDTO> placements;
        await using (var completionDb = new MercuriusDBContext(options))
            placements = (await CreateTournamentService(completionDb).CompleteTournamentAsync(tournament.Id)).ToList();

        Assert.Equal([1, 2], placements.Select(item => item.Place));

        var winner = placements.Single(item => item.Place == 1).LeaderboardParticipants.Single();
        Assert.Equal(guest.Id, winner.ParticipantId);
        Assert.Equal("Guest winner", winner.DisplayName);
        Assert.Equal(Contracts.LeaderboardParticipantKind.Guest, winner.ParticipantKind);
        Assert.Equal(50m, winner.Score);

        var runnerUp = placements.Single(item => item.Place == 2).LeaderboardParticipants.Single();
        Assert.Equal(linked.Id, runnerUp.ParticipantId);
        Assert.Equal("runner", runnerUp.DisplayName);
        Assert.Equal(Contracts.LeaderboardParticipantKind.LinkedUser, runnerUp.ParticipantKind);
        Assert.Equal(linkedUser.Id, runnerUp.LinkedUserId);

        await using var db = new MercuriusDBContext(options);
        var publicResponse = await CreateLeaderboardService(db).GetPublicLeaderboardAsync(tournament.Id);
        Assert.Equal([guest.Id, linked.Id], publicResponse.Rows.Select(row => row.ParticipantId));
    }

    public enum StaleLifecycle
    {
        Complete,
        Cancel
    }

    private static Task RunLifecycleAsync(TournamentService service, StaleLifecycle lifecycle, Guid tournamentId) =>
        lifecycle == StaleLifecycle.Complete
            ? service.CompleteTournamentAsync(tournamentId)
            : service.CancelTournamentAsync(tournamentId);

    private static DbContextOptions<MercuriusDBContext> CreateOptions(PostgresTestDatabaseLease database) =>
        new DbContextOptionsBuilder<MercuriusDBContext>().UseNpgsql(database.ConnectionString).Options;

    private static async Task<TournamentAggregate> SeedInProgressLeaderboardAsync(
        DbContextOptions<MercuriusDBContext> options,
        Action<TournamentAggregate>? addParticipants = null)
    {
        var tournament = new TournamentAggregate(
            "Leaderboard",
            BracketType.Leaderboard,
            GameFormat.BestOf1,
            GameFormat.BestOf1,
            ParticipationMode.Individual,
            null,
            DateTime.UtcNow,
            0,
            0,
            LeaderboardRankingMetric.HighestScore)
        {
            Id = Guid.NewGuid()
        };
        addParticipants?.Invoke(tournament);
        tournament.Start();
        await using var seedDb = new MercuriusDBContext(options);
        await seedDb.Database.MigrateAsync();
        seedDb.Set<TournamentAggregate>().Add(tournament);
        await seedDb.SaveChangesAsync();
        return tournament;
    }

    private static LeaderboardParticipant AddGuestParticipant(TournamentAggregate tournament, string displayName, params decimal[] scores) =>
        AddParticipant(tournament, displayName, null, scores);

    private static LeaderboardParticipant AddLinkedParticipant(TournamentAggregate tournament, Guid linkedUserId, params decimal[] scores) =>
        AddParticipant(tournament, "runner", linkedUserId, scores);

    private static LeaderboardParticipant AddParticipant(
        TournamentAggregate tournament,
        string displayName,
        Guid? linkedUserId,
        params decimal[] scores)
    {
        var now = DateTime.UtcNow;
        var participant = new LeaderboardParticipant
        {
            Id = Guid.NewGuid(),
            TournamentId = tournament.Id,
            DisplayName = displayName,
            LinkedUserId = linkedUserId,
            Attempts = scores.Select(score => new LeaderboardAttempt
            {
                Id = Guid.NewGuid(),
                Score = score,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                RowVersion = Guid.NewGuid()
            }).ToList()
        };
        tournament.LeaderboardParticipants.Add(participant);
        return participant;
    }

    private static Task<TournamentAggregate> LoadTrackedTournamentAsync(MercuriusDBContext db, Guid tournamentId) =>
        db.Set<TournamentAggregate>()
            .Include(item => item.LeaderboardParticipants)
            .ThenInclude(participant => participant.Attempts)
            .SingleAsync(item => item.Id == tournamentId);

    private static LeaderboardService CreateLeaderboardService(
        MercuriusDBContext db,
        IReadOnlyCollection<User>? users = null,
        bool throwOnPublicUsernameLookup = false) => new(
        new TournamentDbContextAdapter<MercuriusDBContext>(db),
        TournamentTestSupport.CreateIdentityModule(users, throwOnPublicUsernameLookup));

    private static TournamentService CreateTournamentService(MercuriusDBContext db) => new(
        new TournamentDbContextAdapter<MercuriusDBContext>(db),
        new LeaderboardModeratorFactory(),
        new UnsupportedMediaModule(),
        TournamentTestSupport.CreateIdentityModule(),
        TournamentTestSupport.CreateSponsorshipModule(),
        TournamentTestSupport.CreateMapper(),
        TournamentTestSupport.CreateModuleEventPublisher(),
        NullLogger<TournamentService>.Instance);

    private static async Task<long> CountAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> InsertParticipantAsync(
        string connectionString,
        Guid tournamentId,
        Guid linkedUserId,
        string displayName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO tournament.leaderboard_participants (\"Id\", \"TournamentId\", \"LinkedUserId\", \"DisplayName\") " +
            "VALUES (@id, @tournamentId, @linkedUserId, @displayName)";
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("tournamentId", tournamentId);
        command.Parameters.AddWithValue("linkedUserId", linkedUserId);
        command.Parameters.AddWithValue("displayName", displayName);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> QueryStringsAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));
        return values;
    }

    private sealed class LeaderboardModeratorFactory : IMatchModeratorFactory
    {
        public IMatchModerator GetMatchModerator(BracketType bracketType) => bracketType == BracketType.Leaderboard
            ? new LeaderboardMatchModerator()
            : throw new InvalidOperationException($"Only leaderboard tournaments are expected in these tests but got {bracketType}.");
    }

    private sealed class UnsupportedMediaModule : IMediaModule
    {
        public Task<StoredMediaAsset> SaveImageAsync(MediaUpload upload, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteImageAsync(string? imageUrl, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
