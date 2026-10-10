using Mercurius.LAN.API.Data;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Tournament.Application.Services;
using Mercurius.Modules.Tournament.Domain;
using Mercurius.Modules.Tournament.Infrastructure;
using Mercurius.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Eventing;
using ContractLifecycleState = Mercurius.Modules.Tournament.Contracts.MatchLifecycleState;

namespace Mercurius.Modules.Tournament.Tests;

public class MatchServiceDeadlineTests
{
    [Fact]
    public async Task PublicRead_DoesNotPersistExpiredDeadlineWhenTournamentIsNoLongerInProgress()
    {
        var nowUtc = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        await using var dbContext = CreateDbContext();
        var tournament = new TournamentAggregate(
            "Inactive tournament",
            BracketType.SingleElimination,
            GameFormat.BestOf1,
            GameFormat.BestOf1,
            ParticipationMode.Individual)
        {
            Status = TournamentStatus.Completed
        };
        var match = new Match
        {
            Id = Guid.NewGuid(),
            TournamentId = tournament.Id,
            Tournament = tournament,
            Format = GameFormat.BestOf1,
            ParticipationMode = ParticipationMode.Individual,
            LifecycleState = MatchLifecycleState.ScoreConfirmation,
            ScoreConfirmationDeadlineUtc = nowUtc.AddMinutes(-1),
            Participant1ReportedScore1 = 1,
            Participant1ReportedScore2 = 0
        };
        tournament.Matches.Add(match);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var service = new MatchService(
            new TournamentDbContextAdapter<MercuriusDBContext>(dbContext),
            TournamentTestSupport.CreateIdentityModule(),
            TournamentTestSupport.CreateTeamsModule(),
            TournamentTestSupport.CreateModuleEventPublisher(),
            new FixedTimeProvider(nowUtc),
            new MatchBracketImpactAnalyzer(new TournamentDbContextAdapter<MercuriusDBContext>(dbContext)));

        var result = await service.GetMatchByIdAsync(match.Id);

        Assert.Equal(ContractLifecycleState.ScoreConfirmation, result.LifecycleState);
        var persisted = await dbContext.Set<Match>().AsNoTracking().SingleAsync(candidate => candidate.Id == match.Id);
        Assert.Equal(MatchLifecycleState.ScoreConfirmation, persisted.LifecycleState);
        Assert.Null(persisted.Participant1Score);
        Assert.Empty(await dbContext.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task PublicRead_ShowsExpiredDeadlineWithoutPersistingIt()
    {
        var nowUtc = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        await using var dbContext = CreateDbContext();
        var participant1Id = Guid.NewGuid();
        var participant2Id = Guid.NewGuid();
        var tournament = new TournamentAggregate(
            "Running tournament",
            BracketType.SingleElimination,
            GameFormat.BestOf1,
            GameFormat.BestOf1,
            ParticipationMode.Individual)
        {
            Status = TournamentStatus.InProgress
        };
        var match = new Match
        {
            Id = Guid.NewGuid(),
            TournamentId = tournament.Id,
            Tournament = tournament,
            Format = GameFormat.BestOf1,
            ParticipationMode = ParticipationMode.Individual,
            LifecycleState = MatchLifecycleState.ScoreConfirmation,
            ScoreConfirmationDeadlineUtc = nowUtc.AddMinutes(-1),
            UserParticipant1Id = participant1Id,
            UserParticipant2Id = participant2Id,
            Participant1ReportedScore1 = 1,
            Participant1ReportedScore2 = 0
        };
        tournament.Matches.Add(match);
        dbContext.Users.AddRange(
            new User { Id = participant1Id, Auth0UserId = $"auth0|{participant1Id:N}" },
            new User { Id = participant2Id, Auth0UserId = $"auth0|{participant2Id:N}" });
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var service = new MatchService(
            new TournamentDbContextAdapter<MercuriusDBContext>(dbContext),
            TournamentTestSupport.CreateIdentityModule(),
            TournamentTestSupport.CreateTeamsModule(),
            TournamentTestSupport.CreateModuleEventPublisher(),
            new FixedTimeProvider(nowUtc),
            new MatchBracketImpactAnalyzer(new TournamentDbContextAdapter<MercuriusDBContext>(dbContext)));

        var result = await service.GetMatchByIdAsync(match.Id);

        Assert.Equal(ContractLifecycleState.Completed, result.LifecycleState);
        var persisted = await dbContext.Set<Match>().AsNoTracking().SingleAsync(candidate => candidate.Id == match.Id);
        Assert.Equal(MatchLifecycleState.ScoreConfirmation, persisted.LifecycleState);
        Assert.Null(persisted.Participant1Score);
        Assert.Empty(await dbContext.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task DeadlineProcessor_CompletesExpiredMatch_AndLoadsOnlyDirectNextMatches()
    {
        var nowUtc = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        var tournament = new TournamentAggregate(
            "Deadline tournament",
            BracketType.SingleElimination,
            GameFormat.BestOf1,
            GameFormat.BestOf1,
            ParticipationMode.Individual)
        {
            Id = Guid.NewGuid(),
            Status = TournamentStatus.InProgress
        };
        var participant1Id = Guid.NewGuid();
        var participant2Id = Guid.NewGuid();
        var source = new Match
        {
            Id = Guid.NewGuid(),
            TournamentId = tournament.Id,
            Tournament = tournament,
            Format = GameFormat.BestOf1,
            ParticipationMode = ParticipationMode.Individual,
            MatchNumber = 1,
            LifecycleState = MatchLifecycleState.ScoreConfirmation,
            ScoreConfirmationDeadlineUtc = nowUtc.AddMinutes(-1),
            UserParticipant1Id = participant1Id,
            UserParticipant2Id = participant2Id,
            Participant1ReportedScore1 = 1,
            Participant1ReportedScore2 = 0
        };
        var directNextMatch = new Match
        {
            Id = Guid.NewGuid(),
            TournamentId = tournament.Id,
            Tournament = tournament,
            Format = GameFormat.BestOf1,
            ParticipationMode = ParticipationMode.Individual
        };
        source.WinnerNextMatchId = directNextMatch.Id;
        var unrelatedMatch = new Match
        {
            Id = Guid.NewGuid(),
            TournamentId = tournament.Id,
            Tournament = tournament,
            Format = GameFormat.BestOf1,
            ParticipationMode = ParticipationMode.Individual,
            LifecycleState = MatchLifecycleState.ScoreConfirmation,
            ScoreConfirmationDeadlineUtc = nowUtc.AddMinutes(5),
            Participant1ReportedScore1 = 1,
            Participant1ReportedScore2 = 0
        };
        tournament.Matches.Add(source);
        tournament.Matches.Add(directNextMatch);
        tournament.Matches.Add(unrelatedMatch);

        await using var dbContext = CreateDbContext();
        dbContext.Users.AddRange(
            new User { Id = participant1Id, Auth0UserId = $"auth0|{participant1Id:N}" },
            new User { Id = participant2Id, Auth0UserId = $"auth0|{participant2Id:N}" });
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var adapter = new TournamentDbContextAdapter<MercuriusDBContext>(dbContext);
        var publisher = TournamentTestSupport.CreateModuleEventPublisher();
        using var serviceProvider = new ServiceCollection()
            .AddScoped<ITournamentDbContext>(_ => adapter)
            .AddScoped<IModuleEventPublisher>(_ => publisher)
            .BuildServiceProvider();
        using var processor = new MatchDeadlineProcessor(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            new FixedTimeProvider(nowUtc),
            NullLogger<MatchDeadlineProcessor>.Instance);

        var processMethod = typeof(MatchDeadlineProcessor).GetMethod(
            "ProcessExpiredMatchesAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing deadline processing method.");
        await (Task)processMethod.Invoke(processor, [CancellationToken.None])!;

        var persistedSource = await dbContext.Set<Match>().AsNoTracking().SingleAsync(match => match.Id == source.Id);
        var persistedDirectNext = await dbContext.Set<Match>().AsNoTracking().SingleAsync(match => match.Id == directNextMatch.Id);
        var persistedUnrelated = await dbContext.Set<Match>().AsNoTracking().SingleAsync(match => match.Id == unrelatedMatch.Id);
        Assert.Equal(MatchLifecycleState.Completed, persistedSource.LifecycleState);
        Assert.Equal(persistedSource.UserWinnerId, persistedDirectNext.UserParticipant1Id);
        Assert.Equal(source.Id, persistedDirectNext.Participant1SourceMatchId);
        Assert.Equal(MatchLifecycleState.ScoreConfirmation, persistedUnrelated.LifecycleState);
        Assert.Contains(publisher.Events, payload => payload is Mercurius.Modules.Tournament.Contracts.MatchCompletedIntegrationEvent);
    }

    [Fact]
    public async Task LifecycleConcurrencyProperties_AreConfiguredAsConcurrencyTokens()
    {
        await using var dbContext = CreateDbContext();

        var tournamentStatus = dbContext.Model
            .FindEntityType(typeof(TournamentAggregate))!
            .FindProperty(nameof(TournamentAggregate.Status));
        var matchResultVersion = dbContext.Model
            .FindEntityType(typeof(Match))!
            .FindProperty(nameof(Match.ResultVersion));

        Assert.True(tournamentStatus!.IsConcurrencyToken);
        Assert.True(matchResultVersion!.IsConcurrencyToken);
    }

    private static MercuriusDBContext CreateDbContext()
        => PostgresTestDatabase.CreateDbContext();

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
