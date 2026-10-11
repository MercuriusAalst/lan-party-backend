using Mercurius.LAN.API.Data;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Media.Contracts;
using Mercurius.Modules.Shared.Exceptions;
using Mercurius.Modules.Teams.Contracts;
using Mercurius.Modules.Tournament.Application;
using Mercurius.Modules.Tournament.Application.DTOs.Registrations;
using Mercurius.Modules.Tournament.Application.DTOs.Tournaments;
using Mercurius.Modules.Tournament.Application.Services;
using Mercurius.Modules.Tournament.Domain;
using Mercurius.Modules.Tournament.Infrastructure;
using Mercurius.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mercurius.Modules.Tournament.Tests;

public sealed class TournamentRegistrationConfigurationConcurrencyTests
{
    [Fact]
    public async Task RegisterIndividualAsync_WhenRegistrationCommitsFirst_LeaderboardSwitchConflicts()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = CreateOptions(database);
        var (tournament, user) = await SeedAsync(options);
        await using var updateDb = new MercuriusDBContext(options);
        await using var registrationDb = new MercuriusDBContext(options);
        var pausedUpdateDb = new PausingTournamentDbContext(updateDb);
        var updateTask = CreateTournamentService(pausedUpdateDb)
            .UpdateTournamentAsync(tournament.Id, CreateLeaderboardUpdate());

        await pausedUpdateDb.SaveReached.WaitAsync(TimeSpan.FromSeconds(10));
        await CreateRegistrationService(registrationDb, user)
            .RegisterIndividualAsync(user.Auth0UserId, tournament.Id);
        pausedUpdateDb.ContinueSave();

        var conflict = await Assert.ThrowsAsync<ConflictException>(() => updateTask);
        Assert.Equal("leaderboard_changed", conflict.Code);
        Assert.Equal("The tournament or leaderboard changed. Refresh and try again.", conflict.Message);

        await using var verify = new MercuriusDBContext(options);
        var persisted = await verify.Set<TournamentAggregate>().AsNoTracking()
            .SingleAsync(item => item.Id == tournament.Id);
        Assert.Equal(BracketType.SingleElimination, persisted.BracketType);
        Assert.Equal(1, persisted.LeaderboardRevision);
        Assert.Single(await verify.Set<TournamentRegistration>().AsNoTracking()
            .Where(item => item.TournamentId == tournament.Id).ToListAsync());
    }

    [Fact]
    public async Task CancelTournamentAsync_WhenRegistrationCommitsFirst_UsesTournamentConflict()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = CreateOptions(database);
        var (tournament, user) = await SeedAsync(options);
        await using var cancelDb = new MercuriusDBContext(options);
        await using var registrationDb = new MercuriusDBContext(options);
        var pausedCancelDb = new PausingTournamentDbContext(cancelDb);
        var cancelTask = CreateTournamentService(pausedCancelDb).CancelTournamentAsync(tournament.Id);

        await pausedCancelDb.SaveReached.WaitAsync(TimeSpan.FromSeconds(10));
        await CreateRegistrationService(registrationDb, user)
            .RegisterIndividualAsync(user.Auth0UserId, tournament.Id);
        pausedCancelDb.ContinueSave();

        var conflict = await Assert.ThrowsAsync<ConflictException>(() => cancelTask);
        Assert.Equal("tournament_changed", conflict.Code);
        Assert.Equal("The tournament changed. Refresh and try again.", conflict.Message);

        await using var verify = new MercuriusDBContext(options);
        var persisted = await verify.Set<TournamentAggregate>().AsNoTracking()
            .SingleAsync(item => item.Id == tournament.Id);
        Assert.Equal(TournamentStatus.Scheduled, persisted.Status);
        Assert.Equal(1, persisted.LeaderboardRevision);
        Assert.Single(await verify.Set<TournamentRegistration>().AsNoTracking()
            .Where(item => item.TournamentId == tournament.Id).ToListAsync());
    }

    [Fact]
    public async Task RegisterIndividualAsync_WhenLeaderboardSwitchCommitsFirst_RegistrationConflictsAndRollsBack()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = CreateOptions(database);
        var (tournament, user) = await SeedAsync(options);
        await using var registrationDb = new MercuriusDBContext(options);
        await using var updateDb = new MercuriusDBContext(options);
        var pausedRegistrationDb = new PausingTournamentDbContext(registrationDb);
        var registrationTask = CreateRegistrationService(pausedRegistrationDb, user)
            .RegisterIndividualAsync(user.Auth0UserId, tournament.Id);

        await pausedRegistrationDb.SaveReached.WaitAsync(TimeSpan.FromSeconds(10));
        await CreateTournamentService(new TournamentDbContextAdapter<MercuriusDBContext>(updateDb))
            .UpdateTournamentAsync(tournament.Id, CreateLeaderboardUpdate());
        pausedRegistrationDb.ContinueSave();

        var conflict = await Assert.ThrowsAsync<ConflictException>(() => registrationTask);
        Assert.Equal("registration_changed", conflict.Code);

        await using var verify = new MercuriusDBContext(options);
        var persisted = await verify.Set<TournamentAggregate>().AsNoTracking()
            .SingleAsync(item => item.Id == tournament.Id);
        Assert.Equal(BracketType.Leaderboard, persisted.BracketType);
        Assert.Equal(1, persisted.LeaderboardRevision);
        Assert.Empty(await verify.Set<TournamentRegistration>().AsNoTracking()
            .Where(item => item.TournamentId == tournament.Id).ToListAsync());
    }

    [Fact]
    public async Task SubmitTeamRosterAsync_WhenRosterCommitsFirst_LeaderboardSwitchConflicts()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = CreateOptions(database);
        var (tournament, team, captain, member) = await SeedTeamAsync(options);
        await using var updateDb = new MercuriusDBContext(options);
        await using var registrationDb = new MercuriusDBContext(options);
        var pausedUpdateDb = new PausingTournamentDbContext(updateDb);
        var updateTask = CreateTournamentService(pausedUpdateDb)
            .UpdateTournamentAsync(tournament.Id, CreateTeamLeaderboardUpdate());

        await pausedUpdateDb.SaveReached.WaitAsync(TimeSpan.FromSeconds(10));
        await CreateRegistrationService(registrationDb, [captain, member], [team])
            .SubmitTeamRosterAsync(
                captain.Auth0UserId,
                tournament.Id,
                new SubmitTeamRosterDTO(team.Id, [captain.Id, member.Id]));
        pausedUpdateDb.ContinueSave();

        var conflict = await Assert.ThrowsAsync<ConflictException>(() => updateTask);
        Assert.Equal("leaderboard_changed", conflict.Code);

        await using var verify = new MercuriusDBContext(options);
        var persisted = await verify.Set<TournamentAggregate>().AsNoTracking()
            .SingleAsync(item => item.Id == tournament.Id);
        Assert.Equal(BracketType.SingleElimination, persisted.BracketType);
        Assert.Equal(ParticipationMode.Team, persisted.ParticipationMode);
        Assert.Equal(1, persisted.LeaderboardRevision);
        var registration = Assert.Single(await verify.Set<TournamentRegistration>().AsNoTracking()
            .Where(item => item.TournamentId == tournament.Id).ToListAsync());
        Assert.Equal(TournamentRegistrationKind.Team, registration.Kind);
        Assert.Equal(2, await verify.Set<TournamentRegistrationRosterMember>().AsNoTracking()
            .CountAsync(item => item.TournamentRegistrationId == registration.Id));
    }

    [Fact]
    public async Task SubmitTeamRosterAsync_WhenLeaderboardSwitchCommitsFirst_RosterConflictsAndRollsBack()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = CreateOptions(database);
        var (tournament, team, captain, member) = await SeedTeamAsync(options);
        await using var registrationDb = new MercuriusDBContext(options);
        await using var updateDb = new MercuriusDBContext(options);
        var pausedRegistrationDb = new PausingTournamentDbContext(registrationDb);
        var registrationTask = CreateRegistrationService(pausedRegistrationDb, [captain, member], [team])
            .SubmitTeamRosterAsync(
                captain.Auth0UserId,
                tournament.Id,
                new SubmitTeamRosterDTO(team.Id, [captain.Id, member.Id]));

        await pausedRegistrationDb.SaveReached.WaitAsync(TimeSpan.FromSeconds(10));
        await CreateTournamentService(new TournamentDbContextAdapter<MercuriusDBContext>(updateDb))
            .UpdateTournamentAsync(tournament.Id, CreateTeamLeaderboardUpdate());
        pausedRegistrationDb.ContinueSave();

        var conflict = await Assert.ThrowsAsync<ConflictException>(() => registrationTask);
        Assert.Equal("registration_changed", conflict.Code);

        await using var verify = new MercuriusDBContext(options);
        var persisted = await verify.Set<TournamentAggregate>().AsNoTracking()
            .SingleAsync(item => item.Id == tournament.Id);
        Assert.Equal(BracketType.Leaderboard, persisted.BracketType);
        Assert.Equal(ParticipationMode.Individual, persisted.ParticipationMode);
        Assert.Equal(1, persisted.LeaderboardRevision);
        Assert.Empty(await verify.Set<TournamentRegistration>().AsNoTracking()
            .Where(item => item.TournamentId == tournament.Id).ToListAsync());
        Assert.Empty(await verify.Set<TournamentRegistrationRosterMember>().AsNoTracking()
            .Where(item => item.TournamentId == tournament.Id).ToListAsync());
    }

    private static async Task<(TournamentAggregate Tournament, User User)> SeedAsync(
        DbContextOptions<MercuriusDBContext> options)
    {
        await using var db = new MercuriusDBContext(options);
        await db.Database.MigrateAsync();
        var tournament = new TournamentAggregate(
            "Concurrent Cup",
            BracketType.SingleElimination,
            GameFormat.BestOf1,
            GameFormat.BestOf3,
            ParticipationMode.Individual)
        {
            Id = Guid.NewGuid()
        };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Auth0UserId = "auth0|concurrent-player",
            Username = "concurrent-player",
            NormalizedUsername = "CONCURRENT-PLAYER",
            Firstname = "Concurrent",
            Lastname = "Player",
            Email = "concurrent-player@example.test"
        };
        db.Set<TournamentAggregate>().Add(tournament);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (tournament, user);
    }

    private static async Task<(TournamentAggregate Tournament, Team Team, User Captain, User Member)> SeedTeamAsync(
        DbContextOptions<MercuriusDBContext> options)
    {
        await using var db = new MercuriusDBContext(options);
        await db.Database.MigrateAsync();
        var tournament = new TournamentAggregate(
            "Concurrent Team Cup",
            BracketType.SingleElimination,
            GameFormat.BestOf1,
            GameFormat.BestOf3,
            ParticipationMode.Team,
            2)
        {
            Id = Guid.NewGuid()
        };
        var captain = new User
        {
            Id = Guid.NewGuid(),
            Auth0UserId = "auth0|team-captain",
            Username = "team-captain",
            NormalizedUsername = "TEAM-CAPTAIN",
            Firstname = "Team",
            Lastname = "Captain",
            Email = "team-captain@example.test"
        };
        var member = new User
        {
            Id = Guid.NewGuid(),
            Auth0UserId = "auth0|team-member",
            Username = "team-member",
            NormalizedUsername = "TEAM-MEMBER",
            Firstname = "Team",
            Lastname = "Member",
            Email = "team-member@example.test"
        };
        var team = new Team("Concurrent Team", captain.Id) { Id = Guid.NewGuid() };
        team.AddMember(captain.Id);
        team.AddMember(member.Id);
        db.Set<TournamentAggregate>().Add(tournament);
        db.Users.AddRange(captain, member);
        db.Teams.Add(team);
        await db.SaveChangesAsync();
        return (tournament, team, captain, member);
    }

    private static DbContextOptions<MercuriusDBContext> CreateOptions(PostgresTestDatabaseLease database) =>
        new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseNpgsql(database.ConnectionString)
            .Options;

    private static UpdateTournamentDTO CreateLeaderboardUpdate() => new()
    {
        Name = "Concurrent Cup",
        BracketType = Contracts.BracketType.Leaderboard,
        LeaderboardRankingMetric = Contracts.LeaderboardRankingMetric.HighestScore,
        Format = Contracts.GameFormat.BestOf1,
        FinalsFormat = Contracts.GameFormat.BestOf3,
        ParticipationMode = Contracts.ParticipationMode.Individual,
        PlannedStartTime = DateTime.UtcNow.AddHours(1),
        AverageGameDurationMinutes = 0,
        RoundBreakDurationMinutes = 0
    };

    private static UpdateTournamentDTO CreateTeamLeaderboardUpdate() => new()
    {
        Name = "Concurrent Team Cup",
        BracketType = Contracts.BracketType.Leaderboard,
        LeaderboardRankingMetric = Contracts.LeaderboardRankingMetric.HighestScore,
        Format = Contracts.GameFormat.BestOf1,
        FinalsFormat = Contracts.GameFormat.BestOf3,
        ParticipationMode = Contracts.ParticipationMode.Individual,
        PlannedStartTime = DateTime.UtcNow.AddHours(1),
        AverageGameDurationMinutes = 0,
        RoundBreakDurationMinutes = 0
    };

    private static TournamentService CreateTournamentService(ITournamentDbContext dbContext) => new(
        dbContext,
        new UnsupportedMatchModeratorFactory(),
        TournamentTestSupport.CreateMediaModule(),
        TournamentTestSupport.CreateIdentityModule(),
        TournamentTestSupport.CreateSponsorshipModule(),
        TournamentTestSupport.CreateMapper(),
        TournamentTestSupport.CreateModuleEventPublisher(),
        NullLogger<TournamentService>.Instance);

    private static TournamentRegistrationService CreateRegistrationService(
        MercuriusDBContext db,
        User user) => CreateRegistrationService(new TournamentDbContextAdapter<MercuriusDBContext>(db), [user]);

    private static TournamentRegistrationService CreateRegistrationService(
        ITournamentDbContext dbContext,
        User user) => CreateRegistrationService(dbContext, [user]);

    private static TournamentRegistrationService CreateRegistrationService(
        MercuriusDBContext db,
        IReadOnlyCollection<User> users,
        IReadOnlyCollection<Team> teams) =>
        CreateRegistrationService(new TournamentDbContextAdapter<MercuriusDBContext>(db), users, teams);

    private static TournamentRegistrationService CreateRegistrationService(
        ITournamentDbContext dbContext,
        IReadOnlyCollection<User> users,
        IReadOnlyCollection<Team>? teams = null)
    {
        var identityModule = TournamentTestSupport.CreateIdentityModule(users);
        var teamsModule = TournamentTestSupport.CreateTeamsModule(teams ?? [], users);
        var mapper = TournamentTestSupport.CreateMapper(users, teams ?? []);
        return new TournamentRegistrationService(
            dbContext,
            identityModule,
            teamsModule,
            new TournamentEligibilityEvaluator(dbContext),
            new RegistrationMappingContextBuilder(identityModule, teamsModule),
            new TournamentRegistrationPersistenceCoordinator(dbContext),
            new TournamentRegistrationReadModelService(
                dbContext,
                teamsModule,
                new RegistrationMappingContextBuilder(identityModule, teamsModule),
                mapper),
            mapper,
            TournamentTestSupport.CreateRealtimePublisher(),
            TournamentTestSupport.CreateModuleEventPublisher(),
            NullLogger<TournamentRegistrationService>.Instance);
    }

    private sealed class PausingTournamentDbContext(MercuriusDBContext inner) : ITournamentDbContext
    {
        private readonly TaskCompletionSource _saveReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _continueSave = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SaveReached => _saveReached.Task;
        public DbSet<TournamentAggregate> Tournaments => inner.Set<TournamentAggregate>();
        public DbSet<Match> Matches => inner.Set<Match>();
        public DbSet<Placement> Placements => inner.Set<Placement>();
        public DbSet<MatchResolutionNotification> MatchResolutionNotifications => inner.Set<MatchResolutionNotification>();
        public DbSet<TournamentRegistration> TournamentRegistrations => inner.Set<TournamentRegistration>();
        public DbSet<TournamentRegistrationRosterMember> TournamentRegistrationRosterMembers => inner.Set<TournamentRegistrationRosterMember>();
        public DbSet<LeaderboardParticipant> LeaderboardParticipants => inner.Set<LeaderboardParticipant>();
        public DbSet<LeaderboardAttempt> LeaderboardAttempts => inner.Set<LeaderboardAttempt>();
        public DatabaseFacade Database => inner.Database;

        public void ContinueSave() => _continueSave.TrySetResult();

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            _saveReached.TrySetResult();
            await _continueSave.Task.WaitAsync(cancellationToken);
            return await inner.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class UnsupportedMatchModeratorFactory : IMatchModeratorFactory
    {
        public IMatchModerator GetMatchModerator(BracketType bracketType) => throw new NotSupportedException();
    }
}
