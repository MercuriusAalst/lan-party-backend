using Mercurius.LAN.API.Data;
using Mercurius.LAN.API.Migrations;
using Mercurius.Modules.Tournament.Application.DTOs.Leaderboards;
using Mercurius.Modules.Shared.Exceptions;
using Mercurius.Modules.Tournament.Application.Services;
using Mercurius.Modules.Tournament.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Contracts = Mercurius.Modules.Tournament.Contracts;

namespace Mercurius.Modules.Tournament.Tests;

public sealed class LeaderboardTests
{
    [Fact]
    public void Configuration_RequiresIndividualLeaderboardMetric()
    {
        Assert.Throws<ValidationException>(() => CreateTournament(metric: null));
        Assert.Throws<ValidationException>(() => CreateTournament(ParticipationMode.Team, LeaderboardRankingMetric.HighestScore));

        var tournament = CreateTournament(metric: LeaderboardRankingMetric.HighestScore);

        Assert.Equal(LeaderboardRankingMetric.HighestScore, tournament.LeaderboardRankingMetric);
        Assert.Equal(0, tournament.AverageGameDurationMinutes);
        Assert.Equal(0, tournament.RoundBreakDurationMinutes);
    }

    [Fact]
    public void Start_AllowsEmptyLeaderboardWithoutMatches()
    {
        var tournament = CreateTournament(metric: LeaderboardRankingMetric.HighestScore);

        tournament.Start();

        Assert.Equal(TournamentStatus.InProgress, tournament.Status);
        Assert.Empty(tournament.Matches);
    }

    [Fact]
    public void HighestScoreRanking_UsesBestAttemptAndCompetitionTies()
    {
        var tournament = CreateTournament(metric: LeaderboardRankingMetric.HighestScore);
        AddParticipant(tournament, "A", null, 100m, 90m);
        AddParticipant(tournament, "B", Guid.NewGuid(), 100m);
        AddParticipant(tournament, "C", null, 80m);

        var rows = tournament.GetLeaderboardRanking();

        Assert.Equal([1, 1, 3], rows.Select(row => row.Rank));
        Assert.Equal(new decimal?[] { 100m, 100m, 80m }, rows.Select(row => row.Score));
        Assert.Contains(rows, row => row.Participant.DisplayName == "A" && row.Score == 100m);
    }

    [Fact]
    public void FastestTimeRanking_UsesLowestDuration()
    {
        var tournament = CreateTournament(metric: LeaderboardRankingMetric.FastestTime);
        AddDurationParticipant(tournament, "Slow then fast", 4000, 2500);
        AddDurationParticipant(tournament, "Second", 3000);

        var rows = tournament.GetLeaderboardRanking();

        Assert.Equal("Slow then fast", rows[0].Participant.DisplayName);
        Assert.Equal(2500, rows[0].DurationMilliseconds);
        Assert.Equal(3000, rows[1].DurationMilliseconds);
    }

    [Fact]
    public void AttemptValidation_PreservesSupportedPrecisionAndRejectsWrongMetric()
    {
        var scores = CreateTournament(metric: LeaderboardRankingMetric.HighestScore);
        scores.Start();
        var recorded = scores.RecordLeaderboardAttempt(
            null,
            null,
            "Guest",
            null,
            999_999_999_999.123456m,
            null,
            DateTime.UtcNow);
        Assert.Equal(999_999_999_999.123456m, recorded.Attempt.Score);
        Assert.Throws<ValidationException>(() => scores.RecordLeaderboardAttempt(
            null,
            null,
            "Invalid precision",
            null,
            1.1234567m,
            null,
            DateTime.UtcNow));
        Assert.Throws<ValidationException>(() => scores.RecordLeaderboardAttempt(
            null,
            null,
            "Wrong metric",
            null,
            null,
            100,
            DateTime.UtcNow));

        var times = CreateTournament(metric: LeaderboardRankingMetric.FastestTime);
        times.Start();
        var timed = times.RecordLeaderboardAttempt(null, null, "Runner", null, null, 1, DateTime.UtcNow);
        Assert.Equal(1, timed.Attempt.DurationMilliseconds);
        Assert.Throws<ValidationException>(() => times.RecordLeaderboardAttempt(
            null,
            null,
            "Invalid duration",
            null,
            null,
            0,
            DateTime.UtcNow));
    }

    [Fact]
    public void RecordAttemptValidation_RejectsScheduledTournamentAndInvalidSelectors()
    {
        var tournament = CreateTournament(metric: LeaderboardRankingMetric.HighestScore);

        Assert.Throws<ValidationException>(() => tournament.RecordLeaderboardAttempt(
            null,
            null,
            "Guest",
            null,
            1,
            null,
            DateTime.UtcNow));

        tournament.Start();
        Assert.Throws<ValidationException>(() => tournament.RecordLeaderboardAttempt(
            null,
            null,
            null,
            null,
            1,
            null,
            DateTime.UtcNow));
        Assert.Empty(tournament.LeaderboardParticipants);
    }

    [Fact]
    public async Task ReadModels_ProjectRankedRowsAndOrderedAdminHistory()
    {
        var options = CreateDbOptions();
        await using (var seedDb = new MercuriusDBContext(options))
        {
            var tournament = CreateTournament(metric: LeaderboardRankingMetric.HighestScore);
            AddParticipant(tournament, "Best", null, 90m, 100m);
            AddParticipant(tournament, "Tied", Guid.NewGuid(), 100m);
            AddParticipant(tournament, "No result", null);
            seedDb.Set<TournamentAggregate>().Add(tournament);
            await seedDb.SaveChangesAsync();
        }

        await using var db = new MercuriusDBContext(options);
        var service = CreateLeaderboardService(db);
        var tournamentId = await db.Set<TournamentAggregate>().Select(item => item.Id).SingleAsync();

        var publicResponse = await service.GetPublicLeaderboardAsync(tournamentId);

        Assert.Equal([1, 1], publicResponse.Rows.Select(row => row.Rank));
        Assert.Equal([100m, 100m], publicResponse.Rows.Select(row => row.Score));
        Assert.All(publicResponse.Rows, row => Assert.Null(row.DurationMilliseconds));
        Assert.Contains(publicResponse.Rows, row => row.DisplayName == "Tied");

        var adminResponse = await service.GetAdminLeaderboardAsync(tournamentId);

        Assert.Equal(3, adminResponse.Participants.Count);
        Assert.Contains(adminResponse.Participants, participant => participant.DisplayName == "Tied");
        Assert.Equal(
            adminResponse.Participants.Select(participant => participant.Id).OrderBy(id => id),
            adminResponse.Participants.Select(participant => participant.Id));
        Assert.Equal(3, adminResponse.Participants.Sum(participant => participant.Attempts.Count));
        foreach (var participant in adminResponse.Participants)
            Assert.Equal(
                participant.Attempts.OrderBy(attempt => attempt.CreatedAtUtc).ThenBy(attempt => attempt.Id).Select(attempt => attempt.Id),
                participant.Attempts.Select(attempt => attempt.Id));
    }

    [Fact]
    public async Task Lifecycle_StartsScheduledLeaderboardWithoutGeneratingMatches()
    {
        var options = CreateDbOptions();
        await using var seedDb = new MercuriusDBContext(options);
        var tournament = CreateTournament(metric: LeaderboardRankingMetric.HighestScore);
        seedDb.Set<TournamentAggregate>().Add(tournament);
        await seedDb.SaveChangesAsync();
        seedDb.ChangeTracker.Clear();
        await using var db = new MercuriusDBContext(options);

        await CreateService(db).StartTournamentAsync(tournament.Id);

        var started = await db.Set<TournamentAggregate>()
            .AsNoTracking()
            .Include(item => item.Matches)
            .SingleAsync(item => item.Id == tournament.Id);
        Assert.Equal(TournamentStatus.InProgress, started.Status);
        Assert.Empty(started.Matches);
        Assert.Equal(1, started.LeaderboardRevision);
    }

    [Fact]
    public async Task Lifecycle_RejectsScheduledLeaderboardCompletionOnLeaderboardPrecondition()
    {
        var options = CreateDbOptions();
        await using var seedDb = new MercuriusDBContext(options);
        var tournament = CreateTournament(metric: LeaderboardRankingMetric.HighestScore);
        seedDb.Set<TournamentAggregate>().Add(tournament);
        await seedDb.SaveChangesAsync();
        seedDb.ChangeTracker.Clear();
        await using var db = new MercuriusDBContext(options);
        var tracked = await db.Set<TournamentAggregate>().SingleAsync(item => item.Id == tournament.Id);

        var exception = await Assert.ThrowsAsync<ValidationException>(() => CreateService(db).CompleteTournamentAsync(tournament.Id));

        Assert.Equal("A leaderboard tournament requires at least one valid recorded result before completion.", exception.Message);
        Assert.Equal(TournamentStatus.Scheduled, tracked.Status);
        Assert.Equal(0, tracked.LeaderboardRevision);
    }

    [Fact]
    public async Task Lifecycle_CompletesGuestPlacementsAndResetClearsLeaderboard()
    {
        var options = CreateDbOptions();
        await using var seedDb = new MercuriusDBContext(options);
        var tournament = CreateTournament(metric: LeaderboardRankingMetric.HighestScore);
        AddParticipant(tournament, "Guest winner", null, 50m);
        AddParticipant(tournament, "Guest tie", null, 50m);
        tournament.Start();
        seedDb.Set<TournamentAggregate>().Add(tournament);
        await seedDb.SaveChangesAsync();
        seedDb.ChangeTracker.Clear();
        await using var db = new MercuriusDBContext(options);
        var service = CreateService(db);

        var placements = (await service.CompleteTournamentAsync(tournament.Id)).ToList();

        var first = Assert.Single(placements);
        Assert.Equal(1, first.Place);
        Assert.Equal(2, first.LeaderboardParticipants.Count());
        Assert.All(first.LeaderboardParticipants, row => Assert.Equal(Contracts.LeaderboardParticipantKind.Guest, row.ParticipantKind));

        await service.ResetTournamentAsync(tournament.Id);
        var reset = await db.Set<TournamentAggregate>().Include(item => item.LeaderboardParticipants).Include(item => item.Placements).SingleAsync();
        Assert.Equal(TournamentStatus.Scheduled, reset.Status);
        Assert.Empty(reset.LeaderboardParticipants);
        Assert.Empty(reset.Placements);
    }

    [Fact]
    public async Task Lifecycle_RejectsCompletingEmptyLeaderboard()
    {
        var options = CreateDbOptions();
        await using var seedDb = new MercuriusDBContext(options);
        var tournament = CreateTournament(metric: LeaderboardRankingMetric.FastestTime);
        tournament.Start();
        seedDb.Set<TournamentAggregate>().Add(tournament);
        await seedDb.SaveChangesAsync();
        seedDb.ChangeTracker.Clear();
        await using var db = new MercuriusDBContext(options);
        var tracked = await db.Set<TournamentAggregate>().SingleAsync(item => item.Id == tournament.Id);

        await Assert.ThrowsAsync<ValidationException>(() => CreateService(db).CompleteTournamentAsync(tournament.Id));

        Assert.Equal(TournamentStatus.InProgress, tracked.Status);
        Assert.Equal(DateTime.MinValue, tracked.EndTime);
        Assert.Equal(0, tracked.LeaderboardRevision);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var persisted = await db.Set<TournamentAggregate>().AsNoTracking().SingleAsync(item => item.Id == tournament.Id);
        Assert.Equal(TournamentStatus.InProgress, persisted.Status);
        Assert.Equal(0, persisted.LeaderboardRevision);
    }

    [Fact]
    public async Task RecordAttempt_PersistsLinkedParticipantForActiveProfile()
    {
        var options = CreateDbOptionsIgnoringInMemoryTransactions();
        await using var seedDb = new MercuriusDBContext(options);
        var tournament = CreateTournament(metric: LeaderboardRankingMetric.HighestScore);
        tournament.Start();
        seedDb.Set<TournamentAggregate>().Add(tournament);
        await seedDb.SaveChangesAsync();
        seedDb.ChangeTracker.Clear();

        var activeUser = new User
        {
            Id = Guid.NewGuid(),
            Username = "runner",
            NormalizedUsername = "RUNNER",
            Firstname = "Ada",
            Lastname = "Lovelace"
        };
        await using var db = new MercuriusDBContext(options);
        var service = CreateLeaderboardService(db, [activeUser]);

        var participant = await service.RecordAttemptAsync(
            tournament.Id,
            new RecordLeaderboardAttemptDTO { LinkedUserId = activeUser.Id, Score = 12.5m });

        Assert.Equal(Contracts.LeaderboardParticipantKind.LinkedUser, participant.ParticipantKind);
        Assert.Equal("runner", participant.DisplayName);
        var attempt = Assert.Single(participant.Attempts);
        Assert.Equal(12.5m, attempt.Score);
        Assert.Equal(1, await db.Set<TournamentAggregate>().Select(item => item.LeaderboardRevision).SingleAsync());
    }

    [Fact]
    public async Task RecordAttempt_RejectsDeletedLinkedProfileWithoutPersisting()
    {
        var options = CreateDbOptionsIgnoringInMemoryTransactions();
        await using var seedDb = new MercuriusDBContext(options);
        var tournament = CreateTournament(metric: LeaderboardRankingMetric.HighestScore);
        tournament.Start();
        seedDb.Set<TournamentAggregate>().Add(tournament);
        await seedDb.SaveChangesAsync();
        seedDb.ChangeTracker.Clear();

        var deletedUser = new User { Id = Guid.NewGuid(), Username = "gone", IsDeleted = true, DeletedAtUtc = DateTime.UtcNow };
        await using var db = new MercuriusDBContext(options);
        var service = CreateLeaderboardService(db, [deletedUser]);

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => service.RecordAttemptAsync(
            tournament.Id,
            new RecordLeaderboardAttemptDTO { LinkedUserId = deletedUser.Id, Score = 10m }));

        Assert.Equal("Linked user not found.", exception.Message);
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Set<LeaderboardParticipant>().ToListAsync());
        Assert.Empty(await db.Set<LeaderboardAttempt>().ToListAsync());
        Assert.Equal(0, await db.Set<TournamentAggregate>().Select(item => item.LeaderboardRevision).SingleAsync());
    }

    [Fact]
    public void Migration_CreatesLeaderboardStorageAndConcurrencyColumns()
    {
        var migration = new LeaderboardTournaments();
        var operations = migration.UpOperations.ToList();

        Assert.Contains(operations.OfType<CreateTableOperation>(), item => item.Name == "leaderboard_participants");
        Assert.Contains(operations.OfType<CreateTableOperation>(), item => item.Name == "leaderboard_attempts");
        Assert.Contains(operations.OfType<CreateTableOperation>(), item => item.Name == "placement_leaderboard_participants");
        Assert.Contains(operations.OfType<AddColumnOperation>(), item => item.Name == "LeaderboardRevision" && item.Table == "tournaments");
        Assert.DoesNotContain(operations.OfType<AddColumnOperation>(), item => item.Name == "xmin");
    }

    private static TournamentAggregate CreateTournament(
        ParticipationMode mode = ParticipationMode.Individual,
        LeaderboardRankingMetric? metric = LeaderboardRankingMetric.HighestScore) => new(
        "Leaderboard",
        BracketType.Leaderboard,
        GameFormat.BestOf1,
        GameFormat.BestOf1,
        mode,
        mode == ParticipationMode.Team ? 2 : null,
        DateTime.UtcNow,
        0,
        0,
        metric)
        {
            Id = Guid.NewGuid()
        };

    private static void AddParticipant(
        TournamentAggregate tournament,
        string name,
        Guid? userId,
        params decimal[] scores)
    {
        var participant = new LeaderboardParticipant
        {
            Id = Guid.NewGuid(),
            TournamentId = tournament.Id,
            DisplayName = name,
            LinkedUserId = userId
        };
        var now = DateTime.UtcNow;
        participant.Attempts = scores.Select(score => new LeaderboardAttempt
        {
            Id = Guid.NewGuid(),
            Score = score,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            RowVersion = Guid.NewGuid()
        }).ToList();
        tournament.LeaderboardParticipants.Add(participant);
    }

    private static void AddDurationParticipant(TournamentAggregate tournament, string name, params long[] durations)
    {
        var participant = new LeaderboardParticipant
        {
            Id = Guid.NewGuid(),
            TournamentId = tournament.Id,
            DisplayName = name
        };
        var now = DateTime.UtcNow;
        participant.Attempts = durations.Select(duration => new LeaderboardAttempt
        {
            Id = Guid.NewGuid(),
            DurationMilliseconds = duration,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            RowVersion = Guid.NewGuid()
        }).ToList();
        tournament.LeaderboardParticipants.Add(participant);
    }

    private static DbContextOptions<MercuriusDBContext> CreateDbOptions() =>
        new DbContextOptionsBuilder<MercuriusDBContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private static DbContextOptions<MercuriusDBContext> CreateDbOptionsIgnoringInMemoryTransactions() =>
        new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static LeaderboardService CreateLeaderboardService(MercuriusDBContext db, IReadOnlyCollection<User>? users = null) => new(
        new TournamentDbContextAdapter<MercuriusDBContext>(db),
        TournamentTestSupport.CreateIdentityModule(users));

    private static TournamentService CreateService(MercuriusDBContext db) => new(
        new TournamentDbContextAdapter<MercuriusDBContext>(db),
        new LeaderboardModeratorFactory(),
        new UnsupportedMediaModule(),
        TournamentTestSupport.CreateIdentityModule(),
        TournamentTestSupport.CreateSponsorshipModule(),
        TournamentTestSupport.CreateMapper(),
        TournamentTestSupport.CreateModuleEventPublisher(),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<TournamentService>.Instance);

    private sealed class LeaderboardModeratorFactory : IMatchModeratorFactory
    {
        public IMatchModerator GetMatchModerator(BracketType bracketType) => bracketType == BracketType.Leaderboard
            ? new LeaderboardMatchModerator()
            : throw new InvalidOperationException($"Only leaderboard tournaments are expected in these tests but got {bracketType}.");
    }

    private sealed class UnsupportedMediaModule : Mercurius.Modules.Media.Contracts.IMediaModule
    {
        public Task<Mercurius.Modules.Media.Contracts.StoredMediaAsset> SaveImageAsync(Mercurius.Modules.Media.Contracts.MediaUpload upload, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteImageAsync(string? imageUrl, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
