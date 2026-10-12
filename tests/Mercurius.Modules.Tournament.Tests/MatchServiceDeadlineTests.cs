using Mercurius.LAN.API.Data;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Tournament.Application.Services;
using Mercurius.Modules.Tournament.Domain;
using Mercurius.Modules.Tournament.Infrastructure;
using Mercurius.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Platform.Eventing;
using Platform.Eventing.Persistence;
using ContractLifecycleState = Mercurius.Modules.Tournament.Contracts.MatchLifecycleState;

namespace Mercurius.Modules.Tournament.Tests;

public class MatchServiceDeadlineTests
{
    [Theory]
    [InlineData(false, ContractLifecycleState.AdminResolutionRequired)]
    [InlineData(true, ContractLifecycleState.Completed)]
    public async Task ConcurrentExpiredReads_ReturnTheCommittedLifecycleState(
        bool scoreConfirmationExpires,
        ContractLifecycleState expectedLifecycleState)
    {
        var nowUtc = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        await using var database = PostgresTestDatabase.Create();
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseNpgsql(database.ConnectionString)
            .Options;
        await using (var migrationContext = new MercuriusDBContext(options))
            await migrationContext.Database.MigrateAsync();

        var participant = new Mercurius.Modules.Identity.Domain.User
        {
            Id = Guid.NewGuid(),
            Auth0UserId = $"auth0|{Guid.NewGuid():N}"
        };
        var opponent = new Mercurius.Modules.Identity.Domain.User
        {
            Id = Guid.NewGuid(),
            Auth0UserId = $"auth0|{Guid.NewGuid():N}"
        };
        var tournament = new TournamentAggregate(
            "Concurrent deadline tournament",
            BracketType.SingleElimination,
            GameFormat.BestOf1,
            GameFormat.BestOf1,
            ParticipationMode.Individual)
        {
            Id = Guid.NewGuid(),
            Status = TournamentStatus.InProgress
        };
        var match = new Match
        {
            Id = Guid.NewGuid(),
            TournamentId = tournament.Id,
            Tournament = tournament,
            Format = GameFormat.BestOf1,
            ParticipationMode = ParticipationMode.Individual,
            MatchNumber = 1,
            LifecycleState = scoreConfirmationExpires
                ? MatchLifecycleState.ScoreConfirmation
                : MatchLifecycleState.Disputed,
            ScoreConfirmationDeadlineUtc = scoreConfirmationExpires ? nowUtc.AddMinutes(-1) : null,
            CorrectionDeadlineUtc = scoreConfirmationExpires ? null : nowUtc.AddMinutes(-1),
            UserParticipant1Id = participant.Id,
            UserParticipant2Id = opponent.Id,
            Participant1ReportedScore1 = 1,
            Participant1ReportedScore2 = 0,
            Participant2ReportedScore1 = scoreConfirmationExpires ? null : 0,
            Participant2ReportedScore2 = scoreConfirmationExpires ? null : 1
        };
        var nextMatch = new Match
        {
            Id = Guid.NewGuid(),
            TournamentId = tournament.Id,
            Tournament = tournament,
            Format = GameFormat.BestOf1,
            ParticipationMode = ParticipationMode.Individual
        };
        match.WinnerNextMatchId = nextMatch.Id;
        match.WinnerNextMatch = nextMatch;
        tournament.Matches.Add(match);
        tournament.Matches.Add(nextMatch);
        await using (var seedContext = new MercuriusDBContext(options))
        {
            seedContext.Users.AddRange(participant, opponent);
            seedContext.Set<TournamentAggregate>().Add(tournament);
            await seedContext.SaveChangesAsync();
        }

        await using var publicReadContext = new MercuriusDBContext(options);
        await using var actionReadContext = new MercuriusDBContext(options);
        await using var lockContext = new MercuriusDBContext(options);
        await publicReadContext.Database.OpenConnectionAsync();
        await actionReadContext.Database.OpenConnectionAsync();
        var publicBackendPid = await GetBackendPidAsync(publicReadContext);
        var actionBackendPid = await GetBackendPidAsync(actionReadContext);

        await using var lockTransaction = await lockContext.Database.BeginTransactionAsync();
        await using (var lockCommand = lockContext.Database.GetDbConnection().CreateCommand())
        {
            lockCommand.Transaction = lockTransaction.GetDbTransaction();
            lockCommand.CommandText = "SELECT \"Id\" FROM tournament.tournaments WHERE \"Id\" = @tournament_id FOR UPDATE";
            var tournamentIdParameter = lockCommand.CreateParameter();
            tournamentIdParameter.ParameterName = "tournament_id";
            tournamentIdParameter.Value = tournament.Id;
            lockCommand.Parameters.Add(tournamentIdParameter);
            _ = await lockCommand.ExecuteScalarAsync();
        }

        var publicRead = CreateService(publicReadContext, [participant, opponent], nowUtc)
            .GetMatchByIdAsync(match.Id);
        var actionRead = CreateService(actionReadContext, [participant, opponent], nowUtc)
            .GetMatchActionStateAsync(match.Id, participant.Auth0UserId, isAdmin: false);
        var readTasks = new Task[] { publicRead, actionRead };
        using var waitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await WaitForBlockedBackendsAsync(
                database.ConnectionString,
                [publicBackendPid, actionBackendPid],
                readTasks,
                waitTimeout.Token);
        }
        catch
        {
            await lockTransaction.RollbackAsync();
            try
            {
                await Task.WhenAll(readTasks).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // Preserve the lock-probe failure while allowing both contexts to finish before disposal.
            }

            throw;
        }

        await lockTransaction.RollbackAsync();
        var publicResult = await publicRead.WaitAsync(TimeSpan.FromSeconds(10));
        var actionResult = await actionRead.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(expectedLifecycleState, publicResult.LifecycleState);
        Assert.Equal(expectedLifecycleState, actionResult.Match.LifecycleState);

        await using var verifyContext = new MercuriusDBContext(options);
        var persisted = await verifyContext.Set<Match>()
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == match.Id);
        var persistedNextMatch = await verifyContext.Set<Match>()
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == nextMatch.Id);
        Assert.Equal(
            scoreConfirmationExpires ? MatchLifecycleState.Completed : MatchLifecycleState.AdminResolutionRequired,
            persisted.LifecycleState);
        Assert.Equal(match.ResultVersion + 1, persisted.ResultVersion);
        var persistedOutboxMessages = await verifyContext.OutboxMessages.AsNoTracking().ToListAsync();
        var persistedOutboxMessage = Assert.Single(persistedOutboxMessages);
        Assert.Equal(
            scoreConfirmationExpires
                ? typeof(Mercurius.Modules.Tournament.Contracts.MatchCompletedIntegrationEvent).FullName
                : typeof(Mercurius.Modules.Tournament.Contracts.MatchResolutionRequiredIntegrationEvent).FullName,
            persistedOutboxMessage.EventType);

        if (scoreConfirmationExpires)
        {
            Assert.Equal(participant.Id, persistedNextMatch.UserParticipant1Id);
            Assert.Equal(match.Id, persistedNextMatch.Participant1SourceMatchId);
        }

        Assert.Empty(publicReadContext.OutboxMessages.Local
            .Where(message => publicReadContext.Entry(message).State == EntityState.Added));
        Assert.Empty(actionReadContext.OutboxMessages.Local
            .Where(message => actionReadContext.Entry(message).State == EntityState.Added));

        var readContexts = new[] { publicReadContext, actionReadContext };
        var winnerContext = Assert.Single(readContexts.Where(context => context.OutboxMessages.Local.Count != 0));
        var losingContext = Assert.Single(readContexts.Where(context => context.OutboxMessages.Local.Count == 0));
        var losingTrackedMatch = Assert.Single(losingContext.Set<Match>().Local.Where(candidate => candidate.Id == match.Id));
        var losingTrackedNextMatch = Assert.Single(losingContext.Set<Match>().Local.Where(candidate => candidate.Id == nextMatch.Id));
        Assert.Equal(persisted.LifecycleState, losingTrackedMatch.LifecycleState);
        Assert.Equal(persisted.ResultVersion, losingTrackedMatch.ResultVersion);
        Assert.Equal(persisted.UserWinnerId, losingTrackedMatch.UserWinnerId);
        Assert.Equal(persistedNextMatch.UserParticipant1Id, losingTrackedNextMatch.UserParticipant1Id);
        Assert.Equal(persistedNextMatch.Participant1SourceMatchId, losingTrackedNextMatch.Participant1SourceMatchId);
        Assert.Equal(EntityState.Unchanged, winnerContext.Entry(Assert.Single(winnerContext.OutboxMessages.Local)).State);
    }

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

    private static MatchService CreateService(
        MercuriusDBContext dbContext,
        IReadOnlyCollection<Mercurius.Modules.Identity.Domain.User> users,
        DateTime nowUtc)
    {
        var adapter = new TournamentDbContextAdapter<MercuriusDBContext>(dbContext);
        return new MatchService(
            adapter,
            TournamentTestSupport.CreateIdentityModule(users),
            TournamentTestSupport.CreateTeamsModule(users: users),
            new ModuleEventPublisher(dbContext),
            new FixedTimeProvider(nowUtc),
            new MatchBracketImpactAnalyzer(adapter));
    }

    private static async Task<int> GetBackendPidAsync(MercuriusDBContext dbContext)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT pg_backend_pid()";
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task WaitForBlockedBackendsAsync(
        string connectionString,
        int[] backendPids,
        IReadOnlyCollection<Task> readTasks,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM pg_stat_activity
            WHERE pid = ANY(@backend_pids)
              AND wait_event_type = 'Lock'
              AND cardinality(pg_blocking_pids(pid)) > 0
            """;
        command.Parameters.AddWithValue("backend_pids", backendPids);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var readTask in readTasks)
            {
                if (readTask.IsFaulted || readTask.IsCanceled)
                    await readTask;
            }

            var blockedCount = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
            if (blockedCount == backendPids.Length)
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
