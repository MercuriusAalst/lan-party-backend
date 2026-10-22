using Mercurius.LAN.API.Data;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Shared.Exceptions;
using Mercurius.Modules.Teams.Application.DTOs;
using Mercurius.Modules.Teams.Application.Services;
using Mercurius.Modules.Teams.Infrastructure;
using Mercurius.TestInfrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mercurius.Modules.Teams.Tests;

// Write-time guards and read-time filters for deleted (anonymized) accounts that cleanup has not yet removed.
public class DeletedUserTeamGuardTests
{
    [Fact]
    public async Task TransferCaptain_ToDeletedMember_IsRejectedWithoutPersistedChange()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var captain = TeamTestUsers.Create();
        var deletedMember = TeamTestUsers.CreateDeleted();
        var team = TeamsTestHarness.CreateTeam("Alpha", captain);
        team.AddMember(deletedMember.Id, DateTime.UtcNow);
        dbContext.Users.AddRange(captain, deletedMember);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        var service = TeamsTestHarness.CreateTeamService(dbContext, identityModule: TeamsTestHarness.CreateIdentityFacade(dbContext));

        await Assert.ThrowsAsync<ValidationException>(() =>
            service.TransferCaptainAsync(captain.Auth0UserId, team.Id, deletedMember.Id));

        dbContext.ChangeTracker.Clear();
        var persisted = await dbContext.Teams.Include(candidate => candidate.Members).SingleAsync(candidate => candidate.Id == team.Id);
        Assert.Equal(captain.Id, persisted.CaptainUserId);
        Assert.Equal(0, persisted.Version);
        Assert.Contains(persisted.Members, member => member.UserId == deletedMember.Id);
    }

    [Fact]
    public async Task TransferCaptain_ToActiveMember_IncrementsTeamVersion()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var captain = TeamTestUsers.Create();
        var member = TeamTestUsers.Create();
        var team = TeamsTestHarness.CreateTeam("Alpha", captain);
        team.AddMember(member.Id, DateTime.UtcNow);
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        var service = TeamsTestHarness.CreateTeamService(dbContext, identityModule: TeamsTestHarness.CreateIdentityFacade(dbContext));

        await service.TransferCaptainAsync(captain.Auth0UserId, team.Id, member.Id);

        dbContext.ChangeTracker.Clear();
        var persisted = await dbContext.Teams.SingleAsync(candidate => candidate.Id == team.Id);
        Assert.Equal(member.Id, persisted.CaptainUserId);
        Assert.Equal(1, persisted.Version);
    }

    [Fact]
    public async Task InviteUser_DeletedUser_IsRejected()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var captain = TeamTestUsers.Create();
        var deleted = TeamTestUsers.CreateDeleted();
        var team = TeamsTestHarness.CreateTeam("Alpha", captain);
        dbContext.Users.AddRange(captain, deleted);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        var service = TeamsTestHarness.CreateTeamService(dbContext, identityModule: TeamsTestHarness.CreateIdentityFacade(dbContext));

        await Assert.ThrowsAsync<NotFoundException>(() => service.InviteUserAsync(captain.Auth0UserId, team.Id, deleted.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => service.InviteUserAsync(team.Id, deleted.Id));

        Assert.False(await dbContext.Set<TeamInvite>().AnyAsync(invite => invite.UserId == deleted.Id));
    }

    [Fact]
    public async Task AcceptInvite_ByDeletedUser_IsRejectedWithoutMembership()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var captain = TeamTestUsers.Create();
        var invited = TeamTestUsers.Create();
        var team = TeamsTestHarness.CreateTeam("Alpha", captain);
        var invite = TeamsTestHarness.CreatePendingInvite(team, invited);
        dbContext.Users.AddRange(captain, invited);
        dbContext.Teams.Add(team);
        dbContext.Set<TeamInvite>().Add(invite);
        await dbContext.SaveChangesAsync();
        invited.Anonymize(DateTime.UtcNow);
        await dbContext.SaveChangesAsync();
        var service = TeamsTestHarness.CreateTeamService(dbContext, identityModule: TeamsTestHarness.CreateIdentityFacade(dbContext));

        await Assert.ThrowsAsync<NotFoundException>(() => service.RespondToInviteAsync(invited.Auth0UserId, invite.Id, true));
        await Assert.ThrowsAsync<NotFoundException>(() => service.RespondToInviteAsync(team.Id, invited.Id, true));

        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Set<TeamMember>().AnyAsync(member => member.UserId == invited.Id));
        Assert.Equal(TeamInviteStatus.Pending, (await dbContext.Set<TeamInvite>().SingleAsync(candidate => candidate.Id == invite.Id)).Status);
    }

    [Fact]
    public async Task AdminCaptainChange_ToDeletedMember_IsRejected()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var captain = TeamTestUsers.Create();
        var deletedMember = TeamTestUsers.CreateDeleted();
        var team = TeamsTestHarness.CreateTeam("Alpha", captain);
        team.AddMember(deletedMember.Id, DateTime.UtcNow);
        dbContext.Users.AddRange(captain, deletedMember);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        var service = TeamsTestHarness.CreateTeamService(dbContext, identityModule: TeamsTestHarness.CreateIdentityFacade(dbContext));

        await Assert.ThrowsAsync<ValidationException>(() =>
            service.UpdateTeamAsync(team.Id, new UpdateTeamDTO { CaptainUserId = deletedMember.Id }));

        dbContext.ChangeTracker.Clear();
        Assert.Equal(captain.Id, (await dbContext.Teams.SingleAsync(candidate => candidate.Id == team.Id)).CaptainUserId);
    }

    [Fact]
    public async Task CreateTeam_WithDeletedCaptain_IsRejected()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var deleted = TeamTestUsers.CreateDeleted();
        dbContext.Users.Add(deleted);
        await dbContext.SaveChangesAsync();
        var service = TeamsTestHarness.CreateTeamService(dbContext, identityModule: TeamsTestHarness.CreateIdentityFacade(dbContext));

        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.CreateTeamAsync(new CreateTeamDTO { Name = "Ghosts", CaptainUserId = deleted.Id }));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.CreateCurrentUserTeamAsync(deleted.Auth0UserId, new CreateTeamDTO { Name = "Ghosts" }));

        Assert.False(await dbContext.Teams.AnyAsync());
    }

    [Fact]
    public async Task LiveTeamProjections_ExcludeDeletedCaptainMembersAndInvitees()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var deletedCaptain = TeamTestUsers.CreateDeleted();
        var member = TeamTestUsers.Create();
        var deletedInvitee = TeamTestUsers.CreateDeleted();
        var team = TeamsTestHarness.CreateTeam("Alpha", deletedCaptain);
        team.AddMember(member.Id, DateTime.UtcNow);
        var otherCaptain = TeamTestUsers.Create();
        var otherTeam = TeamsTestHarness.CreateTeam("Bravo", otherCaptain);
        dbContext.Users.AddRange(deletedCaptain, member, deletedInvitee, otherCaptain);
        dbContext.Teams.AddRange(team, otherTeam);
        dbContext.Set<TeamInvite>().Add(TeamsTestHarness.CreatePendingInvite(otherTeam, deletedInvitee));
        await dbContext.SaveChangesAsync();
        var service = TeamsTestHarness.CreateInnerTeamService(dbContext);

        var byId = await service.GetTeamByIdAsync(team.Id);
        var profile = await service.GetPublicTeamProfileAsync("Alpha");
        var memberSummary = await service.GetCurrentUserTeamSummaryAsync(member.Auth0UserId);
        var captainSummary = await service.GetCurrentUserTeamSummaryAsync(otherCaptain.Auth0UserId);
        var sentInvites = await service.GetCurrentUserSentInvitesAsync(otherCaptain.Auth0UserId);
        var adminInvites = await service.GetUserInvitesAsync(deletedInvitee.Id);

        Assert.Equal(Guid.Empty, byId.CaptainUserId);
        Assert.Equal([member.Id], byId.Members.Select(user => user.Id));
        Assert.Null(profile.CaptainUsername);
        Assert.Equal([member.Username], profile.Members.Select(user => user.Username));
        var managed = Assert.Single(memberSummary.MemberTeams);
        Assert.Equal(Guid.Empty, managed.CaptainUserId);
        Assert.Null(managed.CaptainUsername);
        Assert.DoesNotContain(managed.Members, user => user.Id == deletedCaptain.Id);
        Assert.Empty(captainSummary.SentPendingInvites);
        Assert.Empty(sentInvites);
        Assert.Empty(adminInvites);
    }

    [Fact]
    public async Task TeamsContract_ExcludesDeletedUsersFromSnapshotsAndAuthority()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var deletedCaptain = TeamTestUsers.CreateDeleted();
        var member = TeamTestUsers.Create();
        var team = TeamsTestHarness.CreateTeam("Alpha", deletedCaptain);
        team.AddMember(member.Id, DateTime.UtcNow);
        dbContext.Users.AddRange(deletedCaptain, member);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        var facade = TeamsTestHarness.CreateTeamsFacade(dbContext);

        var snapshot = await facade.GetTeamRosterSnapshotAsync(new TeamId(team.Id));
        var summary = await facade.GetTeamSummaryAsync(new TeamId(team.Id));
        var captained = await facade.GetCaptainedTeamIdsAsync(new UserId(deletedCaptain.Id));
        var eligibility = await facade.GetRegistrationEligibilityAsync(new TeamId(team.Id), new UserId(deletedCaptain.Id), new TournamentId(Guid.NewGuid()));
        var guard = await facade.CanMutateMembershipAsync(new TeamId(team.Id), new UserId(deletedCaptain.Id));
        var publicProfile = await facade.GetPublicTeamProfileAsync("Alpha");

        Assert.NotNull(snapshot);
        Assert.Null(snapshot.CaptainUserId);
        Assert.Equal([member.Id], snapshot.Members.Select(user => user.UserId.Value));
        Assert.Null(summary!.CaptainUserId);
        Assert.Empty(captained);
        Assert.False(eligibility.Eligible);
        Assert.Contains("captain_required", eligibility.ReasonCodes);
        Assert.False(guard.CanMutate);
        Assert.Null(publicProfile!.CaptainUsername);
        Assert.Equal([member.Username], publicProfile.Members.Select(user => user.Username));
    }
}
