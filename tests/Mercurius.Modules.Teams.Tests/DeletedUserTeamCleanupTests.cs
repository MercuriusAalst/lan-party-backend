using Mercurius.LAN.API.Data;
using Mercurius.Modules.Teams.Application;
using Mercurius.Modules.Teams.Application.Services;
using Mercurius.Modules.Teams.Contracts;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Shared;
using Mercurius.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Platform.Eventing;

namespace Mercurius.Modules.Teams.Tests;

public class DeletedUserTeamCleanupTests
{
    private static readonly DateTime T0 = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task DeletedMember_IsRemoved_WithoutChangingCaptainOrOtherMembers()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var captain = TeamTestUsers.Create();
        var member = TeamTestUsers.Create();
        var other = TeamTestUsers.Create();
        var team = TeamsTestHarness.CreateTeam("Alpha", captain, T0);
        team.AddMember(member.Id, T0.AddDays(1));
        team.AddMember(other.Id, T0.AddDays(2));
        dbContext.Users.AddRange(captain, member, other);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        await DeleteAsync(dbContext, member);
        var events = new TeamsTestHarness.RecordingModuleEventPublisher();

        var changed = await TeamsTestHarness.CreateCleanupService(dbContext, events).RemoveDeletedUserAsync(member.Id);

        var persisted = await LoadTeamAsync(dbContext, team.Id);
        Assert.Equal(1, changed);
        Assert.Equal(captain.Id, persisted.CaptainUserId);
        Assert.Equal(new[] { captain.Id, other.Id }.Order(), persisted.Members.Select(teamMember => teamMember.UserId).Order());
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task PendingInvitesToDeletedUser_AreCancelledAndKept()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var captain = TeamTestUsers.Create();
        var invited = TeamTestUsers.Create();
        var team = TeamsTestHarness.CreateTeam("Alpha", captain, T0);
        var invite = TeamsTestHarness.CreatePendingInvite(team, invited);
        dbContext.Users.AddRange(captain, invited);
        dbContext.Teams.Add(team);
        dbContext.Set<TeamInvite>().Add(invite);
        await dbContext.SaveChangesAsync();
        await DeleteAsync(dbContext, invited);

        await TeamsTestHarness.CreateCleanupService(dbContext).RemoveDeletedUserAsync(invited.Id);

        dbContext.ChangeTracker.Clear();
        var persisted = await dbContext.Set<TeamInvite>().SingleAsync(candidate => candidate.Id == invite.Id);
        Assert.Equal(TeamInviteStatus.Cancelled, persisted.Status);
        Assert.NotNull(persisted.CancelledAt);
    }

    [Fact]
    public async Task DeletedCaptain_LongestServingMemberSucceeds_AndSuccessionIsPublished()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var captain = TeamTestUsers.Create();
        var junior = TeamTestUsers.Create();
        var senior = TeamTestUsers.Create();
        var team = TeamsTestHarness.CreateTeam("Alpha", captain, T0);
        team.AddMember(junior.Id, T0.AddDays(5));
        team.AddMember(senior.Id, T0.AddDays(1));
        dbContext.Users.AddRange(captain, junior, senior);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        await DeleteAsync(dbContext, captain);
        var events = new TeamsTestHarness.RecordingModuleEventPublisher();

        await TeamsTestHarness.CreateCleanupService(dbContext, events).RemoveDeletedUserAsync(captain.Id);

        var persisted = await LoadTeamAsync(dbContext, team.Id);
        Assert.Equal(senior.Id, persisted.CaptainUserId);
        Assert.DoesNotContain(persisted.Members, member => member.UserId == captain.Id);
        Assert.Equal(T0.AddDays(1), persisted.Members.Single(member => member.UserId == senior.Id).JoinedAtUtc);
        Assert.Equal(1, persisted.Version);
        var succession = Assert.IsType<TeamCaptainSucceededIntegrationEvent>(Assert.Single(events.Events));
        Assert.Equal(new TeamId(team.Id), succession.TeamId);
        Assert.Equal(new UserId(captain.Id), succession.DeletedCaptainUserId);
        Assert.Equal(new UserId(senior.Id), succession.NewCaptainUserId);
        Assert.Equal(1, succession.Version);
    }

    [Fact]
    public async Task EqualJoinTimes_LowerUserIdSucceeds()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var captain = TeamTestUsers.Create();
        var first = TeamTestUsers.Create();
        var second = TeamTestUsers.Create();
        var team = TeamsTestHarness.CreateTeam("Alpha", captain, T0);
        team.AddMember(first.Id, T0.AddDays(1));
        team.AddMember(second.Id, T0.AddDays(1));
        dbContext.Users.AddRange(captain, first, second);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        await DeleteAsync(dbContext, captain);

        await TeamsTestHarness.CreateCleanupService(dbContext).RemoveDeletedUserAsync(captain.Id);

        var expected = new[] { first.Id, second.Id }.Order().First();
        Assert.Equal(expected, (await LoadTeamAsync(dbContext, team.Id)).CaptainUserId);
    }

    [Fact]
    public async Task LegacyMembers_PrecedeRecordedJoiners_OrderedByAccountCreation()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var captain = TeamTestUsers.Create(T0);
        var recordedEarly = TeamTestUsers.Create(T0.AddDays(-30));
        var legacyNewerAccount = TeamTestUsers.Create(T0.AddDays(-5));
        var legacyOlderAccount = TeamTestUsers.Create(T0.AddDays(-10));
        var team = TeamsTestHarness.CreateTeam("Alpha", captain);
        team.AddMember(recordedEarly.Id, T0.AddYears(-1));
        TeamsTestHarness.AddLegacyMember(team, legacyNewerAccount.Id);
        TeamsTestHarness.AddLegacyMember(team, legacyOlderAccount.Id);
        dbContext.Users.AddRange(captain, recordedEarly, legacyNewerAccount, legacyOlderAccount);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        await DeleteAsync(dbContext, captain);

        await TeamsTestHarness.CreateCleanupService(dbContext).RemoveDeletedUserAsync(captain.Id);

        Assert.Equal(legacyOlderAccount.Id, (await LoadTeamAsync(dbContext, team.Id)).CaptainUserId);
    }

    [Fact]
    public async Task RejoinedMember_LosesSeniorityToContinuousMember()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var clock = new MutableTimeProvider(T0);
        var captain = TeamTestUsers.Create();
        var rejoiner = TeamTestUsers.Create();
        var continuous = TeamTestUsers.Create();
        dbContext.Users.AddRange(captain, rejoiner, continuous);
        await dbContext.SaveChangesAsync();
        var service = TeamsTestHarness.CreateTeamService(dbContext, clock);
        var team = await service.CreateCurrentUserTeamAsync(captain.Auth0UserId, new Application.DTOs.CreateTeamDTO { Name = "Alpha" });
        clock.UtcNow = T0.AddDays(1);
        await JoinAsync(service, captain, rejoiner, team.Id);
        clock.UtcNow = T0.AddDays(2);
        await JoinAsync(service, captain, continuous, team.Id);
        clock.UtcNow = T0.AddDays(3);
        await service.LeaveTeamAsync(rejoiner.Auth0UserId, team.Id);
        clock.UtcNow = T0.AddDays(4);
        await JoinAsync(service, captain, rejoiner, team.Id);
        await DeleteAsync(dbContext, captain);

        await TeamsTestHarness.CreateCleanupService(dbContext).RemoveDeletedUserAsync(captain.Id);

        Assert.Equal(continuous.Id, (await LoadTeamAsync(dbContext, team.Id)).CaptainUserId);
    }

    [Fact]
    public async Task Succession_SkipsDeletedAndCaptainLimitIneligibleCandidates()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var captain = TeamTestUsers.Create();
        var deletedSenior = TeamTestUsers.Create();
        var busyCaptain = TeamTestUsers.Create();
        var eligible = TeamTestUsers.Create();
        var team = TeamsTestHarness.CreateTeam("Alpha", captain, T0);
        team.AddMember(deletedSenior.Id, T0.AddDays(1));
        team.AddMember(busyCaptain.Id, T0.AddDays(2));
        team.AddMember(eligible.Id, T0.AddDays(3));
        var deletedTeam = TeamsTestHarness.CreateTeam("Old", eligible);
        deletedTeam.Delete(T0);
        deletedTeam.CaptainUserId = eligible.Id;
        dbContext.Users.AddRange(captain, deletedSenior, busyCaptain, eligible);
        dbContext.Teams.AddRange(
            team,
            TeamsTestHarness.CreateTeam("Busy One", busyCaptain),
            TeamsTestHarness.CreateTeam("Busy Two", busyCaptain),
            TeamsTestHarness.CreateTeam("Eligible One", eligible),
            deletedTeam);
        await dbContext.SaveChangesAsync();
        await DeleteAsync(dbContext, deletedSenior);
        await DeleteAsync(dbContext, captain);

        await TeamsTestHarness.CreateCleanupService(dbContext).RemoveDeletedUserAsync(captain.Id);

        Assert.Equal(eligible.Id, (await LoadTeamAsync(dbContext, team.Id)).CaptainUserId);
    }

    [Fact]
    public async Task NoEligibleSuccessor_LeavesTeamWithoutCaptainAndLogsIt()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var captain = TeamTestUsers.Create();
        var busyCaptain = TeamTestUsers.Create();
        var team = TeamsTestHarness.CreateTeam("Alpha", captain, T0);
        team.AddMember(busyCaptain.Id, T0.AddDays(1));
        dbContext.Users.AddRange(captain, busyCaptain);
        dbContext.Teams.AddRange(
            team,
            TeamsTestHarness.CreateTeam("Busy One", busyCaptain),
            TeamsTestHarness.CreateTeam("Busy Two", busyCaptain));
        await dbContext.SaveChangesAsync();
        await DeleteAsync(dbContext, captain);
        var events = new TeamsTestHarness.RecordingModuleEventPublisher();
        var logger = new TeamsTestHarness.RecordingLogger<UserDeletedTeamCleanupHandler>();

        await TeamsTestHarness.CreateCleanupService(dbContext, events, logger: logger).RemoveDeletedUserAsync(captain.Id);

        var persisted = await LoadTeamAsync(dbContext, team.Id);
        Assert.Null(persisted.CaptainUserId);
        Assert.Equal([busyCaptain.Id], persisted.Members.Select(member => member.UserId));
        var succession = Assert.IsType<TeamCaptainSucceededIntegrationEvent>(Assert.Single(events.Events));
        Assert.Null(succession.NewCaptainUserId);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains(team.Id.ToString()));
    }

    [Fact]
    public async Task CaptainOfSeveralTeams_EveryTeamIsProcessed()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var captain = TeamTestUsers.Create();
        var alphaMember = TeamTestUsers.Create();
        var bravoMember = TeamTestUsers.Create();
        var alpha = TeamsTestHarness.CreateTeam("Alpha", captain, T0);
        alpha.AddMember(alphaMember.Id, T0.AddDays(1));
        var bravo = TeamsTestHarness.CreateTeam("Bravo", captain, T0);
        bravo.AddMember(bravoMember.Id, T0.AddDays(1));
        dbContext.Users.AddRange(captain, alphaMember, bravoMember);
        dbContext.Teams.AddRange(alpha, bravo);
        await dbContext.SaveChangesAsync();
        await DeleteAsync(dbContext, captain);

        var changed = await TeamsTestHarness.CreateCleanupService(dbContext).RemoveDeletedUserAsync(captain.Id);

        Assert.Equal(2, changed);
        Assert.Equal(alphaMember.Id, (await LoadTeamAsync(dbContext, alpha.Id)).CaptainUserId);
        Assert.Equal(bravoMember.Id, (await LoadTeamAsync(dbContext, bravo.Id)).CaptainUserId);
    }

    [Fact]
    public async Task ProtectedTournamentRoster_DoesNotBlockCleanup()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var captain = TeamTestUsers.Create();
        var member = TeamTestUsers.Create();
        var team = TeamsTestHarness.CreateTeam("Alpha", captain, T0);
        team.AddMember(member.Id, T0.AddDays(1));
        var tournament = new TournamentAggregate("Cup", BracketType.SingleElimination, GameFormat.BestOf1, GameFormat.BestOf1, ParticipationMode.Team, 2, DateTime.UtcNow, 30, 10)
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.Status, TournamentStatus.InProgress);
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        dbContext.Set<TournamentRegistration>().Add(new TournamentRegistration
        {
            Id = Guid.NewGuid(),
            Tournament = tournament,
            TournamentId = tournament.Id,
            Kind = TournamentRegistrationKind.Team,
            Status = TournamentRegistrationStatus.Active,
            RegisteredByUserId = captain.Id,
            RegisteredByUsernameAtRegistration = captain.Username!,
            TeamId = team.Id,
            TeamNameAtRegistration = team.Name,
            TeamCaptainUserIdAtRegistration = captain.Id,
            RosterMembers = new[] { captain, member }.Select(user => new TournamentRegistrationRosterMember
            {
                Id = Guid.NewGuid(),
                Tournament = tournament,
                TournamentId = tournament.Id,
                TeamId = team.Id,
                UserId = user.Id,
                UsernameAtRegistration = user.Username!,
                DisplayNameAtRegistration = user.DisplayName,
                IsCaptain = user.Id == captain.Id,
                ConfirmationStatus = RosterMemberConfirmationStatus.Confirmed
            }).ToList()
        });
        await dbContext.SaveChangesAsync();
        await DeleteAsync(dbContext, member);

        await TeamsTestHarness.CreateCleanupService(dbContext).RemoveDeletedUserAsync(member.Id);

        Assert.DoesNotContain((await LoadTeamAsync(dbContext, team.Id)).Members, teamMember => teamMember.UserId == member.Id);
        Assert.True(await dbContext.Set<TournamentRegistrationRosterMember>().AnyAsync(row => row.UserId == member.Id));
    }

    [Fact]
    public async Task ProcessingTwice_IsANoOp()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var captain = TeamTestUsers.Create();
        var member = TeamTestUsers.Create();
        var team = TeamsTestHarness.CreateTeam("Alpha", captain, T0);
        team.AddMember(member.Id, T0.AddDays(1));
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.Add(team);
        dbContext.Set<TeamInvite>().Add(TeamsTestHarness.CreatePendingInvite(TeamsTestHarness.CreateTeam("Bravo", member), captain));
        await dbContext.SaveChangesAsync();
        await DeleteAsync(dbContext, captain);
        var events = new TeamsTestHarness.RecordingModuleEventPublisher();
        var service = TeamsTestHarness.CreateCleanupService(dbContext, events);

        Assert.Equal(2, await service.RemoveDeletedUserAsync(captain.Id));
        var afterFirst = await LoadTeamAsync(dbContext, team.Id);
        Assert.Equal(0, await service.RemoveDeletedUserAsync(captain.Id));
        var afterSecond = await LoadTeamAsync(dbContext, team.Id);

        Assert.Single(events.Events);
        Assert.Equal(afterFirst.CaptainUserId, afterSecond.CaptainUserId);
        Assert.Equal(afterFirst.Version, afterSecond.Version);
    }

    [Fact]
    public async Task ActiveUser_IsNotCleanedUp()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var captain = TeamTestUsers.Create();
        var team = TeamsTestHarness.CreateTeam("Alpha", captain, T0);
        dbContext.Users.Add(captain);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        Assert.Equal(0, await TeamsTestHarness.CreateCleanupService(dbContext).RemoveDeletedUserAsync(captain.Id));
        Assert.Equal(captain.Id, (await LoadTeamAsync(dbContext, team.Id)).CaptainUserId);
    }

    [Fact]
    public async Task Handler_UsesStableConsumerNameAndDelegatesToCleanup()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var captain = TeamTestUsers.Create();
        var member = TeamTestUsers.Create();
        var team = TeamsTestHarness.CreateTeam("Alpha", captain, T0);
        team.AddMember(member.Id, T0.AddDays(1));
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        await DeleteAsync(dbContext, member);
        var handler = TeamsTestHarness.CreateCleanupService(dbContext);

        await handler.HandleAsync(
            new UserDeletedIntegrationEvent(new UserId(member.Id), DateTime.UtcNow),
            new ModuleEventContext(Guid.NewGuid(), typeof(UserDeletedIntegrationEvent).FullName!, DateTime.UtcNow));

        Assert.Equal("teams-deleted-user-cleanup-v1", handler.ConsumerName);
        Assert.DoesNotContain((await LoadTeamAsync(dbContext, team.Id)).Members, teamMember => teamMember.UserId == member.Id);
    }

    private static async Task JoinAsync(TeamEventPublishingDecorator service, User captain, User user, Guid teamId)
    {
        var invite = await service.InviteUserAsync(captain.Auth0UserId, teamId, user.Id);
        await service.RespondToInviteAsync(user.Auth0UserId, invite.Id, true);
    }

    private static async Task DeleteAsync(MercuriusDBContext dbContext, User user)
    {
        var tracked = await dbContext.Users.SingleAsync(candidate => candidate.Id == user.Id);
        tracked.Anonymize(DateTime.UtcNow);
        await dbContext.SaveChangesAsync();
    }

    private static async Task<Team> LoadTeamAsync(MercuriusDBContext dbContext, Guid teamId)
    {
        dbContext.ChangeTracker.Clear();
        return await dbContext.Teams
            .AsNoTracking()
            .Include(team => team.Members)
            .SingleAsync(team => team.Id == teamId);
    }
}
