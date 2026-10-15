using Mercurius.LAN.API.Data;
using Mercurius.Modules.Tournament.Application.DTOs.Matches;
using Mercurius.Modules.Tournament.Application.Services;
using Mercurius.Modules.Tournament.Domain;
using Mercurius.Modules.Tournament.Infrastructure;
using Mercurius.TestInfrastructure;
using ContractResolutionReason = Mercurius.Modules.Tournament.Contracts.MatchResolutionReason;

namespace Mercurius.Modules.Tournament.Tests;

public class PendingMatchResolutionTests
{
    private static readonly DateTime NowUtc = new(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetPendingResolutionsAsync_ReturnsOnlyMatchesOfTheCallersAssignedTournaments()
    {
        var assignedAdmin = CreateUser("assigned-admin");
        var otherAdmin = CreateUser("other-admin");
        var player1 = CreateUser("player-one");
        var player2 = CreateUser("player-two");
        var tournament = CreateTournament("Assigned Cup", assignedAdmin.Id);
        var missingReport = CreatePendingMatch(tournament, player1, player2, bothReports: false);
        var disputed = CreatePendingMatch(tournament, player1, player2, bothReports: true);
        tournament.Matches.Add(missingReport);
        tournament.Matches.Add(disputed);
        await using var dbContext = CreateDbContext();
        await SaveAsync(dbContext, [tournament], [assignedAdmin, otherAdmin, player1, player2]);
        var service = CreateService(dbContext, [assignedAdmin, otherAdmin, player1, player2]);

        var assigned = await service.GetPendingResolutionsAsync(assignedAdmin.Auth0UserId);
        var other = await service.GetPendingResolutionsAsync(otherAdmin.Auth0UserId);

        Assert.Empty(other);
        Assert.Equal(2, assigned.Count);
        var missing = assigned.Single(item => item.MatchId == missingReport.Id);
        Assert.Equal(ContractResolutionReason.MissingOpponentReport, missing.Reason);
        Assert.Equal(tournament.Id, missing.TournamentId);
        Assert.Equal("Assigned Cup", missing.TournamentName);
        Assert.Equal("player-one", missing.Participant1Name);
        Assert.Equal("player-two", missing.Participant2Name);
        Assert.Equal(1, missing.Participant1ReportedScore1);
        Assert.Equal(0, missing.Participant1ReportedScore2);
        Assert.Null(missing.Participant2ReportedScore1);
        Assert.Null(missing.Participant2ReportedScore2);
        var conflict = assigned.Single(item => item.MatchId == disputed.Id);
        Assert.Equal(ContractResolutionReason.Disputed, conflict.Reason);
        Assert.Equal(0, conflict.Participant2ReportedScore1);
        Assert.Equal(1, conflict.Participant2ReportedScore2);
    }

    [Fact]
    public async Task GetPendingResolutionsAsync_LegacyTournamentWithoutAdmin_IsVisibleToEveryAdmin()
    {
        var firstAdmin = CreateUser("first-admin");
        var secondAdmin = CreateUser("second-admin");
        var player1 = CreateUser("player-one");
        var player2 = CreateUser("player-two");
        var tournament = CreateTournament("Legacy Cup", assignedAdminUserId: null);
        var match = CreatePendingMatch(tournament, player1, player2, bothReports: false);
        tournament.Matches.Add(match);
        await using var dbContext = CreateDbContext();
        await SaveAsync(dbContext, [tournament], [firstAdmin, secondAdmin, player1, player2]);
        var service = CreateService(dbContext, [firstAdmin, secondAdmin, player1, player2]);

        var first = await service.GetPendingResolutionsAsync(firstAdmin.Auth0UserId);
        var second = await service.GetPendingResolutionsAsync(secondAdmin.Auth0UserId);

        Assert.Equal(match.Id, Assert.Single(first).MatchId);
        Assert.Equal(match.Id, Assert.Single(second).MatchId);
    }

    [Fact]
    public async Task GetPendingResolutionsAsync_ExcludesMatchesThatDoNotNeedResolutionOrCannotBeResolved()
    {
        var admin = CreateUser("assigned-admin");
        var player1 = CreateUser("player-one");
        var player2 = CreateUser("player-two");
        var tournament = CreateTournament("Active Cup", admin.Id);
        var waiting = CreatePendingMatch(tournament, player1, player2, bothReports: false)
            .Set(x => x.LifecycleState, MatchLifecycleState.ScoreConfirmation);
        var disputedInWindow = CreatePendingMatch(tournament, player1, player2, bothReports: true)
            .Set(x => x.LifecycleState, MatchLifecycleState.Disputed);
        var completed = CreatePendingMatch(tournament, player1, player2, bothReports: true)
            .Set(x => x.LifecycleState, MatchLifecycleState.Completed);
        tournament.Matches.Add(waiting);
        tournament.Matches.Add(disputedInWindow);
        tournament.Matches.Add(completed);
        var completedTournament = CreateTournament("Finished Cup", admin.Id)
            .Set(x => x.Status, TournamentStatus.Completed);
        completedTournament.Matches.Add(CreatePendingMatch(completedTournament, player1, player2, bothReports: false));
        await using var dbContext = CreateDbContext();
        await SaveAsync(dbContext, [tournament, completedTournament], [admin, player1, player2]);
        var service = CreateService(dbContext, [admin, player1, player2]);

        var pending = await service.GetPendingResolutionsAsync(admin.Auth0UserId);

        Assert.Empty(pending);
    }

    [Fact]
    public async Task GetPendingResolutionsAsync_ReportsRequiredSinceFromTheLatestNotification()
    {
        var admin = CreateUser("assigned-admin");
        var player1 = CreateUser("player-one");
        var player2 = CreateUser("player-two");
        var tournament = CreateTournament("Notified Cup", admin.Id);
        var notified = CreatePendingMatch(tournament, player1, player2, bothReports: false);
        var unnotified = CreatePendingMatch(tournament, player1, player2, bothReports: false);
        tournament.Matches.Add(notified);
        tournament.Matches.Add(unnotified);
        await using var dbContext = CreateDbContext();
        await SaveAsync(dbContext, [tournament], [admin, player1, player2]);
        dbContext.Set<MatchResolutionNotification>().AddRange(
            CreateNotification(notified, admin.Id, NowUtc.AddMinutes(-30)),
            CreateNotification(notified, admin.Id, NowUtc.AddMinutes(-10)));
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext, [admin, player1, player2]);

        var pending = await service.GetPendingResolutionsAsync(admin.Auth0UserId);

        Assert.Equal(2, pending.Count);
        Assert.Equal(notified.Id, pending[0].MatchId);
        Assert.Equal(NowUtc.AddMinutes(-10), pending[0].RequiredSinceUtc);
        Assert.Equal(unnotified.Id, pending[1].MatchId);
        Assert.Null(pending[1].RequiredSinceUtc);
    }

    [Fact]
    public async Task GetPendingResolutionsAsync_IgnoresNotificationsFromBeforeTheLastReversal()
    {
        var admin = CreateUser("assigned-admin");
        var player1 = CreateUser("player-one");
        var player2 = CreateUser("player-two");
        var tournament = CreateTournament("Reversed Cup", admin.Id);
        // Resolved, reversed at -5 minutes, then escalated again: the -30 minute notification is from the first cycle.
        var reEscalatedNotYetNotified = CreatePendingMatch(tournament, player1, player2, bothReports: false)
            .Set(x => x.ResultRecordedAtUtc, NowUtc.AddMinutes(-5));
        var reEscalatedAndNotified = CreatePendingMatch(tournament, player1, player2, bothReports: false)
            .Set(x => x.ResultRecordedAtUtc, NowUtc.AddMinutes(-5));
        tournament.Matches.Add(reEscalatedNotYetNotified);
        tournament.Matches.Add(reEscalatedAndNotified);
        await using var dbContext = CreateDbContext();
        await SaveAsync(dbContext, [tournament], [admin, player1, player2]);
        dbContext.Set<MatchResolutionNotification>().AddRange(
            CreateNotification(reEscalatedNotYetNotified, admin.Id, NowUtc.AddMinutes(-30)),
            CreateNotification(reEscalatedAndNotified, admin.Id, NowUtc.AddMinutes(-30)),
            CreateNotification(reEscalatedAndNotified, admin.Id, NowUtc.AddMinutes(-1)));
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext, [admin, player1, player2]);

        var pending = (await service.GetPendingResolutionsAsync(admin.Auth0UserId)).ToDictionary(item => item.MatchId);

        Assert.Null(pending[reEscalatedNotYetNotified.Id].RequiredSinceUtc);
        Assert.Equal(NowUtc.AddMinutes(-1), pending[reEscalatedAndNotified.Id].RequiredSinceUtc);
    }

    [Fact]
    public async Task GetPendingResolutionsAsync_UsesTeamNamesForTeamMatches()
    {
        var admin = CreateUser("assigned-admin");
        var captain1 = CreateUser("captain-one");
        var captain2 = CreateUser("captain-two");
        var team1 = new Team("Alpha", captain1.Id) { Id = Guid.NewGuid() };
        var team2 = new Team("Beta", captain2.Id) { Id = Guid.NewGuid() };
        var tournament = CreateTournament("Team Cup", admin.Id)
            .Set(x => x.ParticipationMode, ParticipationMode.Team);
        tournament.Matches.Add(new Match()
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.TournamentId, tournament.Id)
            .Set(x => x.Tournament, tournament)
            .Set(x => x.Format, GameFormat.BestOf1)
            .Set(x => x.ParticipationMode, ParticipationMode.Team)
            .Set(x => x.LifecycleState, MatchLifecycleState.AdminResolutionRequired)
            .Set(x => x.TeamParticipant1Id, team1.Id)
            .Set(x => x.TeamParticipant2Id, team2.Id)
            .Set(x => x.Participant2ReportedScore1, 0)
            .Set(x => x.Participant2ReportedScore2, 1));
        await using var dbContext = CreateDbContext();
        dbContext.Teams.AddRange(team1, team2);
        await SaveAsync(dbContext, [tournament], [admin, captain1, captain2]);
        var service = CreateService(dbContext, [admin, captain1, captain2], [team1, team2]);

        var item = Assert.Single(await service.GetPendingResolutionsAsync(admin.Auth0UserId));

        Assert.Equal("Alpha", item.Participant1Name);
        Assert.Equal("Beta", item.Participant2Name);
        Assert.Equal(ContractResolutionReason.MissingOpponentReport, item.Reason);
    }

    [Fact]
    public async Task GetPendingResolutionsAsync_ClearsItselfOnceTheMatchIsResolved()
    {
        var admin = CreateUser("assigned-admin");
        var player1 = CreateUser("player-one");
        var player2 = CreateUser("player-two");
        var tournament = CreateTournament("Resolved Cup", admin.Id);
        var match = CreatePendingMatch(tournament, player1, player2, bothReports: false);
        tournament.Matches.Add(match);
        await using var dbContext = CreateDbContext();
        await SaveAsync(dbContext, [tournament], [admin, player1, player2]);
        var service = CreateService(dbContext, [admin, player1, player2]);
        Assert.Single(await service.GetPendingResolutionsAsync(admin.Auth0UserId));

        await service.ResolveAsync(
            match.Id,
            admin.Auth0UserId,
            new ResolveMatchDTO { Participant1Score = 0, Participant2Score = 1 });

        Assert.Empty(await service.GetPendingResolutionsAsync(admin.Auth0UserId));
    }

    private static TournamentAggregate CreateTournament(string name, Guid? assignedAdminUserId) =>
        new TournamentAggregate(
            name,
            BracketType.SingleElimination,
            GameFormat.BestOf1,
            GameFormat.BestOf1,
            ParticipationMode.Individual,
            null,
            NowUtc,
            30,
            10)
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.Status, TournamentStatus.InProgress)
            .Set(x => x.AssignedAdminUserId, assignedAdminUserId);

    private static Match CreatePendingMatch(TournamentAggregate tournament, User player1, User player2, bool bothReports)
    {
        var match = new Match()
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.TournamentId, tournament.Id)
            .Set(x => x.Tournament, tournament)
            .Set(x => x.Format, GameFormat.BestOf1)
            .Set(x => x.ParticipationMode, ParticipationMode.Individual)
            .Set(x => x.LifecycleState, MatchLifecycleState.AdminResolutionRequired)
            .Set(x => x.UserParticipant1Id, player1.Id)
            .Set(x => x.UserParticipant2Id, player2.Id)
            .Set(x => x.Participant1ReportedScore1, 1)
            .Set(x => x.Participant1ReportedScore2, 0);
        return bothReports
            ? match
                .Set(x => x.Participant2ReportedScore1, 0)
                .Set(x => x.Participant2ReportedScore2, 1)
            : match;
    }

    private static MatchResolutionNotification CreateNotification(Match match, Guid adminId, DateTime occurredAtUtc) =>
        new()
        {
            Id = Guid.NewGuid(),
            MatchId = match.Id,
            TournamentId = match.TournamentId,
            RecipientUserId = adminId,
            RecipientKind = MatchResolutionNotificationRecipientKind.AssignedAdmin,
            OccurredAtUtc = occurredAtUtc,
            CreatedAtUtc = occurredAtUtc
        };

    private static async Task SaveAsync(
        MercuriusDBContext dbContext,
        IReadOnlyCollection<TournamentAggregate> tournaments,
        IReadOnlyCollection<User> users)
    {
        dbContext.Users.AddRange(users);
        dbContext.Set<TournamentAggregate>().AddRange(tournaments);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
    }

    private static MatchService CreateService(
        MercuriusDBContext dbContext,
        IReadOnlyCollection<User> users,
        IReadOnlyCollection<Team>? teams = null) =>
        new(
            new TournamentDbContextAdapter<MercuriusDBContext>(dbContext),
            TournamentTestSupport.CreateIdentityModule(users),
            TournamentTestSupport.CreateTeamsModule(teams, users),
            TournamentTestSupport.CreateModuleEventPublisher(),
            new FixedTimeProvider(NowUtc),
            new MatchBracketImpactAnalyzer(new TournamentDbContextAdapter<MercuriusDBContext>(dbContext)));

    private static MercuriusDBContext CreateDbContext() => PostgresTestDatabase.CreateDbContext();

    private static User CreateUser(string name) => new()
    {
        Id = Guid.NewGuid(),
        Auth0UserId = $"auth0|{name}",
        Username = name,
        NormalizedUsername = name,
        Firstname = $"First {name}",
        Lastname = $"Last {name}"
    };

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
