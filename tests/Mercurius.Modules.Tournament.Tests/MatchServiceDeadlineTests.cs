using Mercurius.LAN.API.Data;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Tournament.Application.DTOs.Matches;
using Mercurius.Modules.Tournament.Application.Services;
using Mercurius.Modules.Tournament.Domain;
using Mercurius.Modules.Tournament.Infrastructure;
using Mercurius.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
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
            ParticipationMode.Individual,
            null,
            DateTime.UtcNow,
            30,
            10).Set(x => x.Status, TournamentStatus.Completed);
        var match = new Match()
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.TournamentId, tournament.Id)
            .Set(x => x.Tournament, tournament)
            .Set(x => x.Format, GameFormat.BestOf1)
            .Set(x => x.ParticipationMode, ParticipationMode.Individual)
            .Set(x => x.LifecycleState, MatchLifecycleState.ScoreConfirmation)
            .Set(x => x.ScoreConfirmationDeadlineUtc, nowUtc.AddMinutes(-1))
            .Set(x => x.Participant1ReportedScore1, 1)
            .Set(x => x.Participant1ReportedScore2, 0);
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
            ParticipationMode.Individual,
            null,
            DateTime.UtcNow,
            30,
            10).Set(x => x.Status, TournamentStatus.InProgress);
        var match = new Match()
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.TournamentId, tournament.Id)
            .Set(x => x.Tournament, tournament)
            .Set(x => x.Format, GameFormat.BestOf1)
            .Set(x => x.ParticipationMode, ParticipationMode.Individual)
            .Set(x => x.LifecycleState, MatchLifecycleState.ScoreConfirmation)
            .Set(x => x.ScoreConfirmationDeadlineUtc, nowUtc.AddMinutes(-1))
            .Set(x => x.UserParticipant1Id, participant1Id)
            .Set(x => x.UserParticipant2Id, participant2Id)
            .Set(x => x.Participant1ReportedScore1, 1)
            .Set(x => x.Participant1ReportedScore2, 0);
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

        Assert.Equal(ContractLifecycleState.AdminResolutionRequired, result.LifecycleState);
        Assert.Null(result.Participant1Score);
        Assert.Null(result.UserWinnerId);
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
            ParticipationMode.Individual,
            null,
            DateTime.UtcNow,
            30,
            10)
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.Status, TournamentStatus.InProgress);
        var participant1Id = Guid.NewGuid();
        var participant2Id = Guid.NewGuid();
        var source = new Match()
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.TournamentId, tournament.Id)
            .Set(x => x.Tournament, tournament)
            .Set(x => x.Format, GameFormat.BestOf1)
            .Set(x => x.ParticipationMode, ParticipationMode.Individual)
            .Set(x => x.MatchNumber, 1)
            .Set(x => x.LifecycleState, MatchLifecycleState.ScoreConfirmation)
            .Set(x => x.ScoreConfirmationDeadlineUtc, nowUtc.AddMinutes(-1))
            .Set(x => x.UserParticipant1Id, participant1Id)
            .Set(x => x.UserParticipant2Id, participant2Id)
            .Set(x => x.Participant1ReportedScore1, 1)
            .Set(x => x.Participant1ReportedScore2, 0)
            .Set(x => x.Participant2ReportedScore1, 1)
            .Set(x => x.Participant2ReportedScore2, 0);
        var directNextMatch = new Match()
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.TournamentId, tournament.Id)
            .Set(x => x.Tournament, tournament)
            .Set(x => x.Format, GameFormat.BestOf1)
            .Set(x => x.ParticipationMode, ParticipationMode.Individual);
        source.Set(x => x.WinnerNextMatchId, directNextMatch.Id);
        var unrelatedMatch = new Match()
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.TournamentId, tournament.Id)
            .Set(x => x.Tournament, tournament)
            .Set(x => x.Format, GameFormat.BestOf1)
            .Set(x => x.ParticipationMode, ParticipationMode.Individual)
            .Set(x => x.LifecycleState, MatchLifecycleState.ScoreConfirmation)
            .Set(x => x.ScoreConfirmationDeadlineUtc, nowUtc.AddMinutes(5))
            .Set(x => x.Participant1ReportedScore1, 1)
            .Set(x => x.Participant1ReportedScore2, 0);
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

        var publisher = TournamentTestSupport.CreateModuleEventPublisher();
        await RunDeadlineProcessorAsync(dbContext.Database.GetConnectionString()!, nowUtc, publisher);

        var persistedSource = await dbContext.Set<Match>().AsNoTracking().SingleAsync(match => match.Id == source.Id);
        var persistedDirectNext = await dbContext.Set<Match>().AsNoTracking().SingleAsync(match => match.Id == directNextMatch.Id);
        var persistedUnrelated = await dbContext.Set<Match>().AsNoTracking().SingleAsync(match => match.Id == unrelatedMatch.Id);
        Assert.Equal(MatchLifecycleState.Completed, persistedSource.LifecycleState);
        Assert.Equal(persistedSource.UserWinnerId, persistedDirectNext.UserParticipant1Id);
        Assert.Equal(source.Id, persistedDirectNext.Participant1SourceMatchId);
        Assert.Equal(MatchLifecycleState.ScoreConfirmation, persistedUnrelated.LifecycleState);
    }

    [Fact]
    public async Task DeadlineProcessor_SkipsMatchWhoseTournamentChangedStatus_AndProcessesTheRest()
    {
        var nowUtc = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        await using var dbContext = CreateDbContext();
        var conflicted = CreateExpiredMatch(nowUtc.AddMinutes(-2));
        var unaffected = CreateExpiredMatch(nowUtc.AddMinutes(-1));
        dbContext.Set<TournamentAggregate>().AddRange(conflicted.Tournament, unaffected.Tournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var connectionString = dbContext.Database.GetConnectionString()!;

        await RunDeadlineProcessorAsync(
            connectionString,
            nowUtc,
            TournamentTestSupport.CreateModuleEventPublisher(),
            new CompleteTournamentBeforeFirstSave(connectionString, conflicted.Tournament.Id));

        var states = await dbContext.Set<Match>().AsNoTracking().ToDictionaryAsync(match => match.Id, match => match.LifecycleState);
        Assert.Equal(MatchLifecycleState.ScoreConfirmation, states[conflicted.Match.Id]);
        Assert.Equal(MatchLifecycleState.AdminResolutionRequired, states[unaffected.Match.Id]);
    }

    [Fact]
    public async Task DeadlineProcessor_SingleReportExpiry_RequiresAdminResolutionAndPublishesOnce()
    {
        var nowUtc = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        var assignedAdminId = Guid.NewGuid();
        await using var dbContext = CreateDbContext();
        var expired = CreateExpiredMatch(nowUtc.AddMinutes(-1));
        expired.Tournament.Set(x => x.AssignedAdminUserId, assignedAdminId);
        dbContext.Set<TournamentAggregate>().Add(expired.Tournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var publisher = TournamentTestSupport.CreateModuleEventPublisher();
        var connectionString = dbContext.Database.GetConnectionString()!;

        await RunDeadlineProcessorAsync(connectionString, nowUtc, publisher);
        await RunDeadlineProcessorAsync(connectionString, nowUtc.AddMinutes(1), publisher);

        var persisted = await dbContext.Set<Match>().AsNoTracking().SingleAsync(match => match.Id == expired.Match.Id);
        Assert.Equal(MatchLifecycleState.AdminResolutionRequired, persisted.LifecycleState);
        Assert.Null(persisted.ScoreConfirmationDeadlineUtc);
        Assert.Null(persisted.Participant1Score);
        Assert.Null(persisted.UserWinnerId);
        Assert.Null(persisted.ResultKind);
        Assert.Equal(1, persisted.Participant1ReportedScore1);
        Assert.Equal(0, persisted.Participant1ReportedScore2);
        var resolutionEvent = Assert.IsType<Mercurius.Modules.Tournament.Contracts.MatchResolutionRequiredIntegrationEvent>(
            Assert.Single(publisher.Events));
        Assert.Equal(expired.Match.Id, resolutionEvent.MatchId.Value);
        Assert.Equal(assignedAdminId, resolutionEvent.AssignedAdminUserId);
        Assert.Equal(
            Mercurius.Modules.Tournament.Contracts.MatchResolutionReason.MissingOpponentReport,
            resolutionEvent.Reason);
    }

    [Fact]
    public async Task ResolveAsync_AfterSingleReportExpiryInTheSameTransaction_LeavesNoStaleResolutionEvent()
    {
        var nowUtc = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        var admin = new User { Id = Guid.NewGuid(), Auth0UserId = "auth0|resolving-admin" };
        await using var dbContext = CreateDbContext();
        var expired = CreateExpiredMatch(nowUtc.AddMinutes(-1));
        var participant1Id = Guid.NewGuid();
        var participant2Id = Guid.NewGuid();
        expired.Match
            .Set(x => x.UserParticipant1Id, participant1Id)
            .Set(x => x.UserParticipant2Id, participant2Id);
        dbContext.Users.AddRange(
            admin,
            new User { Id = participant1Id, Auth0UserId = $"auth0|{participant1Id:N}" },
            new User { Id = participant2Id, Auth0UserId = $"auth0|{participant2Id:N}" });
        dbContext.Set<TournamentAggregate>().Add(expired.Tournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var tournamentDbContext = new TournamentDbContextAdapter<MercuriusDBContext>(dbContext);
        var service = new MatchService(
            tournamentDbContext,
            TournamentTestSupport.CreateIdentityModule([admin]),
            TournamentTestSupport.CreateTeamsModule(),
            new ModuleEventPublisher(dbContext),
            new FixedTimeProvider(nowUtc),
            new MatchBracketImpactAnalyzer(tournamentDbContext));

        await service.ResolveAsync(
            expired.Match.Id,
            admin.Auth0UserId,
            new ResolveMatchDTO { Participant1Score = 0, Participant2Score = 1 });

        var persisted = await dbContext.Set<Match>().AsNoTracking().SingleAsync(match => match.Id == expired.Match.Id);
        Assert.Equal(MatchLifecycleState.Completed, persisted.LifecycleState);
        Assert.Equal(MatchResultKind.AdminResolution, persisted.ResultKind);
        Assert.Empty(await dbContext.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task DeadlineProcessor_CorrectionWindowExpiry_PublishesDisputedReason()
    {
        var nowUtc = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        await using var dbContext = CreateDbContext();
        var expired = CreateExpiredMatch(nowUtc.AddMinutes(-10));
        expired.Match
            .Set(x => x.LifecycleState, MatchLifecycleState.Disputed)
            .Set(x => x.ScoreConfirmationDeadlineUtc, null)
            .Set(x => x.CorrectionDeadlineUtc, nowUtc.AddMinutes(-1))
            .Set(x => x.Participant2ReportedScore1, 0)
            .Set(x => x.Participant2ReportedScore2, 1);
        dbContext.Set<TournamentAggregate>().Add(expired.Tournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var publisher = TournamentTestSupport.CreateModuleEventPublisher();

        await RunDeadlineProcessorAsync(dbContext.Database.GetConnectionString()!, nowUtc, publisher);

        var persisted = await dbContext.Set<Match>().AsNoTracking().SingleAsync(match => match.Id == expired.Match.Id);
        Assert.Equal(MatchLifecycleState.AdminResolutionRequired, persisted.LifecycleState);
        var resolutionEvent = Assert.IsType<Mercurius.Modules.Tournament.Contracts.MatchResolutionRequiredIntegrationEvent>(
            Assert.Single(publisher.Events));
        Assert.Equal(Mercurius.Modules.Tournament.Contracts.MatchResolutionReason.Disputed, resolutionEvent.Reason);
    }

    [Fact]
    public async Task DeadlineProcessor_DoesNothingWhileAnotherInstanceHoldsTheLock()
    {
        var nowUtc = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        await using var dbContext = CreateDbContext();
        var expired = CreateExpiredMatch(nowUtc.AddMinutes(-1));
        dbContext.Set<TournamentAggregate>().Add(expired.Tournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var connectionString = dbContext.Database.GetConnectionString()!;
        await using var otherInstance = new NpgsqlConnection(connectionString);
        await otherInstance.OpenAsync();
        await using var otherTransaction = await otherInstance.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({MatchDeadlineProcessor.ProcessorLockKey})", otherInstance, otherTransaction))
            await lockCommand.ExecuteNonQueryAsync();

        await RunDeadlineProcessorAsync(connectionString, nowUtc, TournamentTestSupport.CreateModuleEventPublisher());

        var persisted = await dbContext.Set<Match>().AsNoTracking().SingleAsync(match => match.Id == expired.Match.Id);
        Assert.Equal(MatchLifecycleState.ScoreConfirmation, persisted.LifecycleState);
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

    [Fact]
    public async Task SubmitScoreAsync_AcceptsDirectAndLegacyReports_AndAdvertisesCapability()
    {
        var nowUtc = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        var tournament = new TournamentAggregate(
            "Direct score tournament",
            BracketType.SingleElimination,
            GameFormat.BestOf1,
            GameFormat.BestOf1,
            ParticipationMode.Individual,
            null,
            nowUtc,
            30,
            10)
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.Status, TournamentStatus.InProgress);
        var participant1Id = Guid.NewGuid();
        var participant2Id = Guid.NewGuid();
        Match CreateMatch() => new Match()
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.TournamentId, tournament.Id)
            .Set(x => x.Tournament, tournament)
            .Set(x => x.Format, GameFormat.BestOf1)
            .Set(x => x.ParticipationMode, ParticipationMode.Individual)
            .Set(x => x.UserParticipant1Id, participant1Id)
            .Set(x => x.UserParticipant2Id, participant2Id);
        var directMatch = CreateMatch();
        var legacyMatch = CreateMatch();
        legacyMatch.Set(x => x.LifecycleState, MatchLifecycleState.AwaitingScore);
        legacyMatch.Set(x => x.Participant1EndedConfirmedAtUtc, nowUtc);
        legacyMatch.Set(x => x.Participant2EndedConfirmedAtUtc, nowUtc);
        tournament.Matches.Add(directMatch);
        tournament.Matches.Add(legacyMatch);

        await using var dbContext = CreateDbContext();
        var users = new[]
        {
            new User { Id = participant1Id, Auth0UserId = $"auth0|{participant1Id:N}" },
            new User { Id = participant2Id, Auth0UserId = $"auth0|{participant2Id:N}" }
        };
        dbContext.Users.AddRange(users);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var service = new MatchService(
            new TournamentDbContextAdapter<MercuriusDBContext>(dbContext),
            TournamentTestSupport.CreateIdentityModule(users),
            TournamentTestSupport.CreateTeamsModule(),
            TournamentTestSupport.CreateModuleEventPublisher(),
            new FixedTimeProvider(nowUtc),
            new MatchBracketImpactAnalyzer(new TournamentDbContextAdapter<MercuriusDBContext>(dbContext)));

        var actionState = await service.GetMatchActionStateAsync(directMatch.Id, users[0].Auth0UserId, isAdmin: false);
        Assert.True(actionState.CanSubmitScore);

        var directReport = await service.SubmitScoreAsync(
            directMatch.Id,
            users[0].Auth0UserId,
            new SubmitMatchScoreDTO { Participant1Score = 1, Participant2Score = 0 });

        Assert.Equal(ContractLifecycleState.ScoreConfirmation, directReport.LifecycleState);
        Assert.True(directReport.Participant1Ended);

        var legacyReport = await service.SubmitScoreAsync(
            legacyMatch.Id,
            users[0].Auth0UserId,
            new SubmitMatchScoreDTO { Participant1Score = 1, Participant2Score = 0 });

        Assert.Equal(ContractLifecycleState.ScoreConfirmation, legacyReport.LifecycleState);
    }

    private static MercuriusDBContext CreateDbContext()
        => PostgresTestDatabase.CreateDbContext();

    private static (TournamentAggregate Tournament, Match Match) CreateExpiredMatch(DateTime deadlineUtc)
    {
        var tournament = new TournamentAggregate(
            "Deadline tournament",
            BracketType.SingleElimination,
            GameFormat.BestOf1,
            GameFormat.BestOf1,
            ParticipationMode.Individual,
            null,
            DateTime.UtcNow,
            30,
            10)
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.Status, TournamentStatus.InProgress);
        var match = new Match()
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.TournamentId, tournament.Id)
            .Set(x => x.Tournament, tournament)
            .Set(x => x.Format, GameFormat.BestOf1)
            .Set(x => x.ParticipationMode, ParticipationMode.Individual)
            .Set(x => x.LifecycleState, MatchLifecycleState.ScoreConfirmation)
            .Set(x => x.ScoreConfirmationDeadlineUtc, deadlineUtc)
            .Set(x => x.Participant1ReportedScore1, 1)
            .Set(x => x.Participant1ReportedScore2, 0);
        tournament.Matches.Add(match);
        return (tournament, match);
    }

    private static async Task RunDeadlineProcessorAsync(
        string connectionString,
        DateTime nowUtc,
        IModuleEventPublisher publisher,
        params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseNpgsql(connectionString)
            .AddInterceptors(interceptors)
            .Options;
        await using var serviceProvider = new ServiceCollection()
            .AddScoped(_ => new MercuriusDBContext(options))
            .AddScoped<ITournamentDbContext>(provider =>
                new TournamentDbContextAdapter<MercuriusDBContext>(provider.GetRequiredService<MercuriusDBContext>()))
            .AddScoped(_ => publisher)
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
    }

    // Simulates an administrator completing the tournament between the processor loading a match and saving it.
    private sealed class CompleteTournamentBeforeFirstSave(string connectionString, Guid tournamentId) : SaveChangesInterceptor
    {
        private bool _completed;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_completed)
            {
                _completed = true;
                await using var otherContext = new MercuriusDBContext(
                    new DbContextOptionsBuilder<MercuriusDBContext>().UseNpgsql(connectionString).Options);
                await otherContext.Set<TournamentAggregate>()
                    .Where(tournament => tournament.Id == tournamentId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(tournament => tournament.Status, TournamentStatus.Completed), cancellationToken);
            }

            return result;
        }
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
