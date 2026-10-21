using Mercurius.LAN.API.Data;
using Mercurius.Modules.Identity;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Identity.Infrastructure;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Teams;
using Mercurius.Modules.Teams.Contracts;
using Mercurius.Modules.Teams.Infrastructure;
using Mercurius.Modules.Tournament.Application.DTOs.Participants;
using Mercurius.Modules.Tournament.Infrastructure;
using Mercurius.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Eventing;

namespace Mercurius.Modules.Tournament.Tests;

public class DeletedUserTournamentCleanupTests
{
    private static int _nextUser;

    [Fact]
    public async Task ScheduledIndividualRegistration_IsRemoved_OtherStatusesAreKept()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var user = CreateUser();
        var scheduled = CreateTournament(ParticipationMode.Individual, TournamentStatus.Scheduled);
        var inProgress = CreateTournament(ParticipationMode.Individual, TournamentStatus.InProgress);
        var completed = CreateTournament(ParticipationMode.Individual, TournamentStatus.Completed);
        dbContext.Users.Add(user);
        dbContext.Set<TournamentAggregate>().AddRange(scheduled, inProgress, completed);
        foreach (var tournament in new[] { scheduled, inProgress, completed })
            AddIndividualRegistration(dbContext, tournament, user);
        await dbContext.SaveChangesAsync();
        var scheduledRevision = scheduled.LeaderboardRevision;
        await DeleteAsync(dbContext, user);

        var changed = await CreateService(dbContext).RemoveDeletedUserAsync(user.Id);

        dbContext.ChangeTracker.Clear();
        Assert.Equal(1, changed);
        var remaining = await dbContext.Set<TournamentRegistration>().Select(registration => registration.TournamentId).ToListAsync();
        Assert.Equal(new[] { inProgress.Id, completed.Id }.Order(), remaining.Order());
        Assert.Equal(scheduledRevision + 1, (await dbContext.Set<TournamentAggregate>().SingleAsync(t => t.Id == scheduled.Id)).LeaderboardRevision);
    }

    [Fact]
    public async Task ScheduledRosterMember_IsRemoved_AndRegistrationReturnsToPending()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var captain = CreateUser();
        var member = CreateUser();
        var team = CreateTeam(captain, member);
        var tournament = CreateTournament(ParticipationMode.Team, TournamentStatus.Scheduled);
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        var registration = AddTeamRegistration(dbContext, tournament, team, captain, [captain, member]);
        await dbContext.SaveChangesAsync();
        await DeleteAsync(dbContext, member);

        await CreateService(dbContext).RemoveDeletedUserAsync(member.Id);

        var persisted = await LoadRegistrationAsync(dbContext, registration.Id);
        Assert.NotNull(persisted);
        Assert.Equal(TournamentRegistrationStatus.PendingConfirmation, persisted.Status);
        Assert.Equal([captain.Id], persisted.RosterMembers.Select(row => row.UserId));
        Assert.Equal(captain.Id, persisted.TeamCaptainUserIdAtRegistration);
    }

    [Fact]
    public async Task ScheduledRosterCaptain_IsRePointedToSuccessor_WhenTeamsAlreadySucceeded()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var captain = CreateUser();
        var successor = CreateUser();
        var team = CreateTeam(captain, successor);
        var tournament = CreateTournament(ParticipationMode.Team, TournamentStatus.Scheduled);
        dbContext.Users.AddRange(captain, successor);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        var registration = AddTeamRegistration(dbContext, tournament, team, captain, [captain, successor], successorPending: true);
        await dbContext.SaveChangesAsync();
        await DeleteAsync(dbContext, captain);
        await SetTeamCaptainAsync(dbContext, team.Id, successor.Id, removeMember: captain.Id);

        await CreateService(dbContext).RemoveDeletedUserAsync(captain.Id);

        var persisted = await LoadRegistrationAsync(dbContext, registration.Id);
        Assert.NotNull(persisted);
        Assert.Equal(TournamentRegistrationStatus.PendingConfirmation, persisted.Status);
        Assert.Equal(successor.Id, persisted.TeamCaptainUserIdAtRegistration);
        Assert.Equal(successor.Id, persisted.RegisteredByUserId);
        var successorRow = Assert.Single(persisted.RosterMembers);
        Assert.True(successorRow.IsCaptain);
        Assert.Equal(RosterMemberConfirmationStatus.AutoConfirmed, successorRow.ConfirmationStatus);
    }

    [Fact]
    public async Task ScheduledRosterCaptain_SuccessionEventAfterUserDeletion_RePointsRegistration()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var captain = CreateUser();
        var successor = CreateUser();
        var team = CreateTeam(captain, successor);
        var tournament = CreateTournament(ParticipationMode.Team, TournamentStatus.Scheduled);
        dbContext.Users.AddRange(captain, successor);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        var registration = AddTeamRegistration(dbContext, tournament, team, captain, [captain, successor]);
        await dbContext.SaveChangesAsync();
        await DeleteAsync(dbContext, captain);
        var service = CreateService(dbContext);

        await service.RemoveDeletedUserAsync(captain.Id);
        var beforeSuccession = await LoadRegistrationAsync(dbContext, registration.Id);
        await SetTeamCaptainAsync(dbContext, team.Id, successor.Id, removeMember: captain.Id);
        await service.ApplyCaptainSuccessionAsync(team.Id, captain.Id, successor.Id);
        var afterSuccession = await LoadRegistrationAsync(dbContext, registration.Id);
        var replayed = await service.ApplyCaptainSuccessionAsync(team.Id, captain.Id, successor.Id);

        Assert.Equal(captain.Id, beforeSuccession!.TeamCaptainUserIdAtRegistration);
        Assert.Equal([successor.Id], beforeSuccession.RosterMembers.Select(row => row.UserId));
        Assert.Equal(successor.Id, afterSuccession!.TeamCaptainUserIdAtRegistration);
        Assert.True(Assert.Single(afterSuccession.RosterMembers).IsCaptain);
        Assert.Equal(0, replayed);
    }

    [Fact]
    public async Task ScheduledRosterCaptain_SuccessionBeforeUserDeletionEvent_Converges()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var captain = CreateUser();
        var successor = CreateUser();
        var team = CreateTeam(captain, successor);
        var tournament = CreateTournament(ParticipationMode.Team, TournamentStatus.Scheduled);
        dbContext.Users.AddRange(captain, successor);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        var registration = AddTeamRegistration(dbContext, tournament, team, captain, [captain, successor]);
        await dbContext.SaveChangesAsync();
        await DeleteAsync(dbContext, captain);
        await SetTeamCaptainAsync(dbContext, team.Id, successor.Id, removeMember: captain.Id);
        var service = CreateService(dbContext);

        await service.ApplyCaptainSuccessionAsync(team.Id, captain.Id, successor.Id);
        var afterUserDeletion = await service.RemoveDeletedUserAsync(captain.Id);

        var persisted = await LoadRegistrationAsync(dbContext, registration.Id);
        Assert.Equal(0, afterUserDeletion);
        Assert.Equal(TournamentRegistrationStatus.PendingConfirmation, persisted!.Status);
        Assert.Equal(successor.Id, persisted.TeamCaptainUserIdAtRegistration);
        Assert.True(Assert.Single(persisted.RosterMembers).IsCaptain);
    }

    [Fact]
    public async Task ScheduledRosterCaptain_WithoutSuccessor_RemovesTeamRegistration()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var captain = CreateUser();
        var member = CreateUser();
        var team = CreateTeam(captain, member);
        var tournament = CreateTournament(ParticipationMode.Team, TournamentStatus.Scheduled);
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        var registration = AddTeamRegistration(dbContext, tournament, team, captain, [captain, member], successorPending: true);
        await dbContext.SaveChangesAsync();
        await DeleteAsync(dbContext, captain);
        await SetTeamCaptainAsync(dbContext, team.Id, null, removeMember: captain.Id);
        var publisher = TournamentTestSupport.CreateRealtimePublisher();
        var service = CreateService(dbContext, publisher);

        await service.RemoveDeletedUserAsync(captain.Id);
        await service.ApplyCaptainSuccessionAsync(team.Id, captain.Id, null);

        Assert.Null(await LoadRegistrationAsync(dbContext, registration.Id));
        Assert.False(await dbContext.Set<TournamentRegistrationRosterMember>().AnyAsync());
        Assert.Contains(publisher.Events, evt => evt.UserId == member.Id && evt.Status == "Withdrawn");
    }

    [Fact]
    public async Task InProgressAndCompletedTournaments_KeepRostersRegistrationsAndPlaceholder()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var captain = CreateUser();
        var member = CreateUser();
        var team = CreateTeam(captain, member);
        var inProgress = CreateTournament(ParticipationMode.Team, TournamentStatus.InProgress);
        var completed = CreateTournament(ParticipationMode.Team, TournamentStatus.Completed);
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().AddRange(inProgress, completed);
        AddTeamRegistration(dbContext, inProgress, team, captain, [captain, member]);
        AddTeamRegistration(dbContext, completed, team, captain, [captain, member]);
        await dbContext.SaveChangesAsync();
        await DeleteAsync(dbContext, captain);
        await SetTeamCaptainAsync(dbContext, team.Id, member.Id, removeMember: captain.Id);
        var service = CreateService(dbContext);

        Assert.Equal(0, await service.RemoveDeletedUserAsync(captain.Id));
        Assert.Equal(0, await service.ApplyCaptainSuccessionAsync(team.Id, captain.Id, member.Id));

        dbContext.ChangeTracker.Clear();
        var registrations = await dbContext.Set<TournamentRegistration>().Include(registration => registration.RosterMembers).ToListAsync();
        Assert.Equal(2, registrations.Count);
        Assert.All(registrations, registration =>
        {
            Assert.Equal(TournamentRegistrationStatus.Active, registration.Status);
            Assert.Equal(captain.Id, registration.TeamCaptainUserIdAtRegistration);
            Assert.Equal(2, registration.RosterMembers.Count);
        });
        var profiles = await CreateIdentity(dbContext).GetUsersByIdsAsync([new UserId(captain.Id)]);
        var placeholder = profiles[new UserId(captain.Id)];
        Assert.True(placeholder.IsDeleted);
        Assert.Equal("Deleted user", placeholder.DisplayName);
        Assert.StartsWith("deleted-user-", new PublicUserDTO(placeholder).Username);
    }

    [Fact]
    public async Task RemoveDeletedUser_IsIdempotentAndIgnoresActiveUsers()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var user = CreateUser();
        var active = CreateUser();
        var tournament = CreateTournament(ParticipationMode.Individual, TournamentStatus.Scheduled);
        dbContext.Users.AddRange(user, active);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        AddIndividualRegistration(dbContext, tournament, user);
        AddIndividualRegistration(dbContext, tournament, active);
        await dbContext.SaveChangesAsync();
        await DeleteAsync(dbContext, user);
        var service = CreateService(dbContext);

        Assert.Equal(1, await service.RemoveDeletedUserAsync(user.Id));
        Assert.Equal(0, await service.RemoveDeletedUserAsync(user.Id));
        Assert.Equal(0, await service.RemoveDeletedUserAsync(active.Id));

        dbContext.ChangeTracker.Clear();
        Assert.Equal([active.Id], await dbContext.Set<TournamentRegistration>().Select(registration => registration.UserId!.Value).ToListAsync());
    }

    [Fact]
    public async Task Handler_UsesStableConsumerNameForBothEvents()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var user = CreateUser();
        var tournament = CreateTournament(ParticipationMode.Individual, TournamentStatus.Scheduled);
        dbContext.Users.Add(user);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        AddIndividualRegistration(dbContext, tournament, user);
        await dbContext.SaveChangesAsync();
        await DeleteAsync(dbContext, user);
        var handler = new DeletedUserTournamentCleanupHandler(CreateService(dbContext));

        await handler.HandleAsync(
            new UserDeletedIntegrationEvent(new UserId(user.Id), DateTime.UtcNow),
            new ModuleEventContext(Guid.NewGuid(), typeof(UserDeletedIntegrationEvent).FullName!, DateTime.UtcNow));
        await handler.HandleAsync(
            new TeamCaptainSucceededIntegrationEvent(new TeamId(Guid.NewGuid()), 1, new UserId(user.Id), null),
            new ModuleEventContext(Guid.NewGuid(), typeof(TeamCaptainSucceededIntegrationEvent).FullName!, DateTime.UtcNow));

        Assert.Equal("tournament-deleted-user-cleanup-v1", handler.ConsumerName);
        Assert.False(await dbContext.Set<TournamentRegistration>().AnyAsync());
    }

    private static DeletedUserTournamentCleanupService CreateService(
        MercuriusDBContext dbContext,
        ITournamentRealtimePublisher? publisher = null)
    {
        var identity = CreateIdentity(dbContext);
        return new DeletedUserTournamentCleanupService(
            new TournamentDbContextAdapter<MercuriusDBContext>(dbContext),
            identity,
            new TeamsModuleFacade(new TeamsDbContextAdapter<MercuriusDBContext>(dbContext), identity, new TournamentTeamReadService(new TournamentDbContextAdapter<MercuriusDBContext>(dbContext))),
            publisher ?? TournamentTestSupport.CreateRealtimePublisher(),
            TimeProvider.System,
            NullLogger<DeletedUserTournamentCleanupService>.Instance);
    }

    private static IIdentityModule CreateIdentity(MercuriusDBContext dbContext) =>
        new IdentityModuleFacade(new IdentityDbContextAdapter<MercuriusDBContext>(dbContext), new TournamentTestSupport.NoopAuth0ManagementService());

    private static User CreateUser()
    {
        var id = Interlocked.Increment(ref _nextUser);
        var username = $"dut{id}-{Guid.NewGuid():N}"[..20];
        return new User
        {
            Id = Guid.NewGuid(),
            Auth0UserId = $"auth0|{username}",
            Username = username,
            NormalizedUsername = username,
            Firstname = "First",
            Lastname = "Last",
            Email = $"{username}@example.test"
        };
    }

    private static Team CreateTeam(User captain, params User[] members)
    {
        var team = new Team($"Team {Guid.NewGuid():N}"[..20], captain.Id) { Id = Guid.NewGuid() };
        team.AddMember(captain.Id, DateTime.UtcNow.AddDays(-10));
        foreach (var member in members)
            team.AddMember(member.Id, DateTime.UtcNow.AddDays(-5));
        return team;
    }

    private static TournamentAggregate CreateTournament(ParticipationMode mode, TournamentStatus status)
    {
        return new TournamentAggregate(
                $"Cup {Guid.NewGuid():N}"[..20],
                BracketType.SingleElimination,
                GameFormat.BestOf1,
                GameFormat.BestOf1,
                mode,
                mode == ParticipationMode.Team ? 2 : null,
                DateTime.UtcNow,
                30,
                10)
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.Status, status);
    }

    private static void AddIndividualRegistration(MercuriusDBContext dbContext, TournamentAggregate tournament, User user)
    {
        dbContext.Set<TournamentRegistration>().Add(new TournamentRegistration
        {
            Id = Guid.NewGuid(),
            Tournament = tournament,
            TournamentId = tournament.Id,
            Kind = TournamentRegistrationKind.Individual,
            Status = TournamentRegistrationStatus.Active,
            RegisteredByUserId = user.Id,
            RegisteredByUsernameAtRegistration = user.Username!,
            UserId = user.Id,
            UsernameAtRegistration = user.Username
        });
    }

    private static TournamentRegistration AddTeamRegistration(
        MercuriusDBContext dbContext,
        TournamentAggregate tournament,
        Team team,
        User captain,
        IReadOnlyCollection<User> roster,
        bool successorPending = false)
    {
        var registration = new TournamentRegistration
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
            RosterMembers = roster.Select(user => new TournamentRegistrationRosterMember
            {
                Id = Guid.NewGuid(),
                Tournament = tournament,
                TournamentId = tournament.Id,
                TeamId = team.Id,
                TeamNameAtRegistration = team.Name,
                UserId = user.Id,
                UsernameAtRegistration = user.Username!,
                DisplayNameAtRegistration = user.DisplayName,
                IsCaptain = user.Id == captain.Id,
                ConfirmationStatus = user.Id == captain.Id
                    ? RosterMemberConfirmationStatus.AutoConfirmed
                    : successorPending ? RosterMemberConfirmationStatus.Pending : RosterMemberConfirmationStatus.Confirmed
            }).ToList()
        };
        if (successorPending)
            registration.Status = TournamentRegistrationStatus.PendingConfirmation;
        dbContext.Set<TournamentRegistration>().Add(registration);
        return registration;
    }

    private static async Task DeleteAsync(MercuriusDBContext dbContext, User user)
    {
        var tracked = await dbContext.Users.SingleAsync(candidate => candidate.Id == user.Id);
        tracked.Anonymize(DateTime.UtcNow);
        await dbContext.SaveChangesAsync();
    }

    // Stands in for the Teams cleanup that runs from the same deletion event.
    private static async Task SetTeamCaptainAsync(MercuriusDBContext dbContext, Guid teamId, Guid? captainUserId, Guid removeMember)
    {
        dbContext.ChangeTracker.Clear();
        var team = await dbContext.Teams.Include(candidate => candidate.Members).SingleAsync(candidate => candidate.Id == teamId);
        team.Members.Remove(team.Members.Single(member => member.UserId == removeMember));
        team.CaptainUserId = captainUserId;
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
    }

    private static async Task<TournamentRegistration?> LoadRegistrationAsync(MercuriusDBContext dbContext, Guid registrationId)
    {
        dbContext.ChangeTracker.Clear();
        return await dbContext.Set<TournamentRegistration>()
            .AsNoTracking()
            .Include(registration => registration.RosterMembers)
            .SingleOrDefaultAsync(registration => registration.Id == registrationId);
    }
}
