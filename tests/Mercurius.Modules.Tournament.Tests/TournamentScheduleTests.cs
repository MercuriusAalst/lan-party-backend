using Mercurius.LAN.API.Data;
using Mercurius.Modules.Tournament.Application.DTOs.Tournaments;
using Mercurius.Modules.Tournament.Application.DTOs.Matches;
using Mercurius.Modules.Tournament.Infrastructure;
using Mercurius.Modules.Shared.Exceptions;
using Mercurius.LAN.API.Migrations;
using Mercurius.Modules.Media.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Mercurius.Modules.Tournament.Tests;

public class TournamentScheduleTests
{
    private static readonly DateTime PlannedStart = new(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Tournament_StoresScheduleConfiguration()
    {
        var tournament = CreateScheduledTournament();

        Assert.Equal(PlannedStart, tournament.PlannedStartTime);
        Assert.Equal(10, tournament.AverageGameDurationMinutes);
        Assert.Equal(5, tournament.RoundBreakDurationMinutes);
    }

    [Fact]
    public void Tournament_AllowsRoundBreakDurationsAbovePreviousLimit()
    {
        var tournament = CreateScheduledTournament(breakMinutes: 241);

        Assert.Equal(241, tournament.RoundBreakDurationMinutes);
    }

    [Theory]
    [InlineData(true, 10, 5, "Planned tournament start time is required.")]
    [InlineData(false, 0, 5, "Average tournament duration must be greater than zero.")]
    [InlineData(false, -1, 5, "Average tournament duration must be greater than zero.")]
    [InlineData(false, 1441, 5, "Average tournament duration cannot exceed 1440 minutes.")]
    [InlineData(false, 10, 0, "Round break duration must be greater than zero.")]
    [InlineData(false, 10, -1, "Round break duration must be greater than zero.")]
    public void Tournament_RejectsInvalidScheduleConfiguration(
        bool missingPlannedStart,
        int averageMinutes,
        int breakMinutes,
        string expectedMessage)
    {
        var plannedStart = missingPlannedStart ? DateTime.MinValue : PlannedStart;

        var exception = Assert.Throws<ValidationException>(() =>
            CreateScheduledTournament(plannedStartTime: plannedStart, averageMinutes: averageMinutes, breakMinutes: breakMinutes));

        Assert.Equal(expectedMessage, exception.Message);
    }

    [Fact]
    public void Update_BlocksScheduleChangesAfterMatchesExist()
    {
        var tournament = CreateScheduledTournament();
        tournament.Matches.Add(new Match { RoundNumber = 1 });

        var exception = Assert.Throws<ValidationException>(() => tournament.Update(
            "Schedule Change",
            BracketType.SingleElimination,
            GameFormat.BestOf1,
            GameFormat.BestOf5,
            ParticipationMode.Individual,
            null,
            PlannedStart.AddHours(1),
            10,
            5));

        Assert.Equal("Schedule configuration cannot be changed once match generation has started.", exception.Message);
    }

    [Fact]
    public async Task StartTournamentAsync_AssignsEstimatedWindowsWithRoundBreaksAndFinalsDuration()
    {
        await using var dbContext = CreateDbContext();
        var tournament = CreateScheduledTournament(format: GameFormat.BestOf1, finalsFormat: GameFormat.BestOf5);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        AddIndividualRegistration(dbContext, tournament, CreateUser(1));
        AddIndividualRegistration(dbContext, tournament, CreateUser(2));
        await dbContext.SaveChangesAsync();

        var service = CreateTournamentService(dbContext, new FixedScheduleMatchModerator());

        await service.StartTournamentAsync(tournament.Id);

        var storedTournament = await dbContext.Set<TournamentAggregate>()
            .Include(g => g.Matches)
            .SingleAsync(g => g.Id == tournament.Id);
        var matches = storedTournament.Matches.OrderBy(match => match.RoundNumber).ThenBy(match => match.MatchNumber).ToList();

        Assert.Equal(PlannedStart, matches[0].EstimatedStartTime);
        Assert.Equal(PlannedStart.AddMinutes(10), matches[0].EstimatedEndTime);
        Assert.Equal(PlannedStart, matches[1].EstimatedStartTime);
        Assert.Equal(PlannedStart.AddMinutes(30), matches[1].EstimatedEndTime);
        Assert.Equal(PlannedStart.AddMinutes(35), matches[2].EstimatedStartTime);
        Assert.Equal(PlannedStart.AddMinutes(85), matches[2].EstimatedEndTime);
        Assert.Equal(PlannedStart.AddMinutes(85), storedTournament.EstimatedEndTime);
    }

    [Fact]
    public async Task StartTournamentAsync_StartsLeaderboardWithoutMatchesAndAdvancesRevision()
    {
        await using var dbContext = CreateDbContext();
        var tournament = new TournamentAggregate(
            "Leaderboard Cup",
            BracketType.Leaderboard,
            GameFormat.BestOf1,
            GameFormat.BestOf5,
            ParticipationMode.Individual,
            null,
            PlannedStart,
            10,
            5,
            LeaderboardRankingMetric.HighestScore)
        {
            Id = Guid.NewGuid(),
            LeaderboardRevision = 19
        };
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var service = CreateTournamentService(dbContext, new LeaderboardMatchModerator());

        await service.StartTournamentAsync(tournament.Id);

        var startedTournament = await dbContext.Set<TournamentAggregate>()
            .Include(item => item.Matches)
            .SingleAsync(item => item.Id == tournament.Id);

        Assert.Equal(TournamentStatus.InProgress, startedTournament.Status);
        Assert.Empty(startedTournament.Matches);
        Assert.Equal(20, startedTournament.LeaderboardRevision);
    }

    [Fact]
    public async Task StartTournamentAsync_DoesNotApplyFinalsFormatToRoundRobinLastRound()
    {
        await using var dbContext = CreateDbContext();
        var tournament = CreateScheduledTournament(
            format: GameFormat.BestOf1,
            finalsFormat: GameFormat.BestOf5,
            bracketType: BracketType.RoundRobin);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        AddIndividualRegistration(dbContext, tournament, CreateUser(1));
        AddIndividualRegistration(dbContext, tournament, CreateUser(2));
        AddIndividualRegistration(dbContext, tournament, CreateUser(3));
        AddIndividualRegistration(dbContext, tournament, CreateUser(4));
        await dbContext.SaveChangesAsync();

        var service = CreateTournamentService(dbContext, new RoundRobinMatchModerator());

        await service.StartTournamentAsync(tournament.Id);

        var matches = await dbContext.Set<Match>().ToListAsync();

        Assert.All(matches, match =>
            Assert.Equal(TimeSpan.FromMinutes(10), match.EstimatedEndTime - match.EstimatedStartTime));
    }

    [Fact]
    public async Task StartTournamentAsync_RejectsEstimatedScheduleDateOverflow()
    {
        await using var dbContext = CreateDbContext();
        var tournament = CreateScheduledTournament(plannedStartTime: DateTime.MaxValue.AddMinutes(-5));
        dbContext.Set<TournamentAggregate>().Add(tournament);
        AddIndividualRegistration(dbContext, tournament, CreateUser(1));
        AddIndividualRegistration(dbContext, tournament, CreateUser(2));
        await dbContext.SaveChangesAsync();

        var service = CreateTournamentService(dbContext, new FixedScheduleMatchModerator());

        var exception = await Assert.ThrowsAsync<ValidationException>(() => service.StartTournamentAsync(tournament.Id));

        Assert.Equal("Estimated tournament schedule exceeds supported date range.", exception.Message);
    }

    [Fact]
    public async Task CreateTournamentAsync_ForwardsCancellationTokenToImageStorage()
    {
        await using var dbContext = CreateDbContext();
        var mediaModule = new RecordingMediaModule();
        var service = new TournamentService(
            new TournamentDbContextAdapter<MercuriusDBContext>(dbContext),
            new FixedMatchModeratorFactory(new FixedScheduleMatchModerator()),
            mediaModule,
            TournamentTestSupport.CreateIdentityModule(),
            TournamentTestSupport.CreateSponsorshipModule(),
            TournamentTestSupport.CreateMapper(),
            TournamentTestSupport.CreateModuleEventPublisher(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TournamentService>.Instance);
        using var cancellationSource = new CancellationTokenSource();
        var imageBytes = new byte[] { 1, 2, 3 };
        var image = new FormFile(new MemoryStream(imageBytes), 0, imageBytes.Length, "image", "tournament.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };

        await service.CreateTournamentAsync(new CreateTournamentDTO
        {
            Name = "Cancellation token tournament",
            BracketType = Mercurius.Modules.Tournament.Contracts.BracketType.SingleElimination,
            Format = Mercurius.Modules.Tournament.Contracts.GameFormat.BestOf1,
            FinalsFormat = Mercurius.Modules.Tournament.Contracts.GameFormat.BestOf3,
            ParticipationMode = Mercurius.Modules.Tournament.Contracts.ParticipationMode.Individual,
            Image = image,
            PlannedStartTime = PlannedStart,
            AverageGameDurationMinutes = 10,
            RoundBreakDurationMinutes = 5
        }, cancellationSource.Token);

        Assert.Equal(cancellationSource.Token, mediaModule.ReceivedCancellationToken);
    }

    [Fact]
    public void ScheduleMigration_AddsFieldsWithSafeDefaults()
    {
        var migration = new TournamentScheduleEstimation();
        var operations = migration.UpOperations.ToList();

        Assert.Contains(operations.OfType<AddColumnOperation>(), operation =>
            operation.Table == "Games" &&
            operation.Name == "AverageGameDurationMinutes" &&
            Equals(operation.DefaultValue, 30));
        Assert.Contains(operations.OfType<AddColumnOperation>(), operation =>
            operation.Table == "Games" &&
            operation.Name == "RoundBreakDurationMinutes" &&
            Equals(operation.DefaultValue, 10));
        Assert.Contains(operations.OfType<AddColumnOperation>(), operation =>
            operation.Table == "Games" &&
            operation.Name == "PlannedStartTime" &&
            operation.DefaultValueSql == "CURRENT_TIMESTAMP");
        Assert.Contains(operations.OfType<AddColumnOperation>(), operation =>
            operation.Table == "Matches" &&
            operation.Name == "EstimatedStartTime" &&
            operation.IsNullable);
        Assert.Contains(operations.OfType<AddColumnOperation>(), operation =>
            operation.Table == "Matches" &&
            operation.Name == "EstimatedEndTime" &&
            operation.IsNullable);
    }

    [Fact]
    public void ResponseDtos_ExposeScheduleFields()
    {
        var tournament = CreateScheduledTournament();
        tournament.EstimatedEndTime = PlannedStart.AddHours(2);
        var match = new Match
        {
            EstimatedStartTime = PlannedStart,
            EstimatedEndTime = PlannedStart.AddMinutes(10)
        };

        var tournamentDto = tournament.ToGetTournamentDTO();
        var matchDto = match.ToGetMatchDTO();

        Assert.Equal(PlannedStart, tournamentDto.PlannedStartTime);
        Assert.Equal(10, tournamentDto.AverageGameDurationMinutes);
        Assert.Equal(5, tournamentDto.RoundBreakDurationMinutes);
        Assert.Equal(PlannedStart.AddHours(2), tournamentDto.EstimatedEndTime);
        Assert.Equal(PlannedStart, matchDto.EstimatedStartTime);
        Assert.Equal(PlannedStart.AddMinutes(10), matchDto.EstimatedEndTime);
    }

    private static TournamentAggregate CreateScheduledTournament(
        DateTime? plannedStartTime = null,
        int averageMinutes = 10,
        int breakMinutes = 5,
        GameFormat format = GameFormat.BestOf1,
        GameFormat finalsFormat = GameFormat.BestOf5,
        BracketType bracketType = BracketType.SingleElimination)
    {
        return new TournamentAggregate(
            "Schedule Cup",
            bracketType,
            format,
            finalsFormat,
            ParticipationMode.Individual,
            null,
            plannedStartTime ?? PlannedStart,
            averageMinutes,
            breakMinutes)
        {
            Id = Guid.NewGuid()
        };
    }

    private static User CreateUser(int id)
    {
        return new User
        {
            Id = Guid.NewGuid(),
            Username = $"user{id}",
            Firstname = $"First{id}",
            Lastname = $"Last{id}",
            Email = $"user{id}@example.test"
        };
    }

    private static MercuriusDBContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new MercuriusDBContext(options);
    }

    private static void AddIndividualRegistration(MercuriusDBContext dbContext, TournamentAggregate tournament, User user)
    {
        dbContext.Users.Add(user);
        dbContext.Set<TournamentRegistration>().Add(new TournamentRegistration
        {
            Id = Guid.NewGuid(),
            Tournament = tournament,
            TournamentId = tournament.Id,
            Kind = TournamentRegistrationKind.Individual,
            Status = TournamentRegistrationStatus.Active,
            RegisteredByUserId = user.Id,
            RegisteredByUsernameAtRegistration = user.Username ?? string.Empty,
            UserId = user.Id,
            UsernameAtRegistration = user.Username
        });
    }

    private static TournamentService CreateTournamentService(MercuriusDBContext dbContext, IMatchModerator matchModerator)
    {
        return new TournamentService(
            new TournamentDbContextAdapter<MercuriusDBContext>(dbContext),
            new FixedMatchModeratorFactory(matchModerator),
            new UnsupportedMediaModule(),
            TournamentTestSupport.CreateIdentityModule(),
            TournamentTestSupport.CreateSponsorshipModule(),
            TournamentTestSupport.CreateMapper(),
            TournamentTestSupport.CreateModuleEventPublisher(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TournamentService>.Instance);
    }

    private sealed class FixedScheduleMatchModerator : IMatchModerator
    {
        public IEnumerable<Match> GenerateMatchesForTournament(TournamentAggregate tournament)
        {
            return
            [
                new Match
                {
                    TournamentId = tournament.Id,
                    RoundNumber = 1,
                    MatchNumber = 1,
                    Format = GameFormat.BestOf1,
                    ParticipationMode = tournament.ParticipationMode
                },
                new Match
                {
                    TournamentId = tournament.Id,
                    RoundNumber = 1,
                    MatchNumber = 2,
                    Format = GameFormat.BestOf3,
                    ParticipationMode = tournament.ParticipationMode
                },
                new Match
                {
                    TournamentId = tournament.Id,
                    RoundNumber = 2,
                    MatchNumber = 1,
                    Format = tournament.FinalsFormat,
                    ParticipationMode = tournament.ParticipationMode
                }
            ];
        }

        public void DeterminePlacements(TournamentAggregate tournament)
        {
            throw new NotSupportedException();
        }

        public void EnsureCanComplete(TournamentAggregate tournament)
        {
        }
    }

    private sealed class FixedMatchModeratorFactory(IMatchModerator matchModerator) : IMatchModeratorFactory
    {
        public IMatchModerator GetMatchModerator(BracketType bracketType)
        {
            return matchModerator;
        }
    }

    private sealed class UnsupportedMediaModule : IMediaModule
    {
        public Task<StoredMediaAsset> SaveImageAsync(MediaUpload upload, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task DeleteImageAsync(string? imageUrl, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class RecordingMediaModule : IMediaModule
    {
        public CancellationToken ReceivedCancellationToken { get; private set; }

        public Task<StoredMediaAsset> SaveImageAsync(MediaUpload upload, CancellationToken cancellationToken = default)
        {
            ReceivedCancellationToken = cancellationToken;
            return Task.FromResult(new StoredMediaAsset("images/tournament.webp"));
        }

        public Task DeleteImageAsync(string? imageUrl, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
