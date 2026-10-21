using Mercurius.LAN.API.Data;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Shared.Exceptions;
using Mercurius.TestInfrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mercurius.Modules.Teams.Tests;

// Team writes take the identity mutation lock of the affected account, so they serialize with account deletion.
public class DeletedUserTeamConcurrencyTests
{
    [Fact]
    public async Task TransferCaptain_WaitsForInFlightDeletion_ThenRejectsWithoutChange()
    {
        await using var database = PostgresTestDatabase.CreateMigrated();
        var options = new DbContextOptionsBuilder<MercuriusDBContext>().UseNpgsql(database.ConnectionString).Options;
        var captain = TeamTestUsers.Create();
        var member = TeamTestUsers.Create();
        var team = TeamsTestHarness.CreateTeam("Alpha", captain);
        team.AddMember(member.Id);
        await using (var setup = new MercuriusDBContext(options))
        {
            setup.Users.AddRange(captain, member);
            setup.Teams.Add(team);
            await setup.SaveChangesAsync();
        }

        await using var deletionContext = new MercuriusDBContext(options);
        await using var deletion = await deletionContext.Database.BeginTransactionAsync();
        await TeamsTestHarness.CreateIdentityFacade(deletionContext).GetUserProfileForUpdateAsync(new UserId(member.Id));
        var deletedMember = await deletionContext.Users.SingleAsync(user => user.Id == member.Id);
        deletedMember.Anonymize(DateTime.UtcNow);
        await deletionContext.SaveChangesAsync();

        await using var transferContext = new MercuriusDBContext(options);
        var transferService = TeamsTestHarness.CreateTeamService(
            transferContext,
            identityModule: TeamsTestHarness.CreateIdentityFacade(transferContext));
        var transfer = transferService.TransferCaptainAsync(captain.Auth0UserId, team.Id, member.Id);

        var completedEarly = await Task.WhenAny(transfer, Task.Delay(TimeSpan.FromMilliseconds(500))) == transfer;
        await deletion.CommitAsync();

        Assert.False(completedEarly, "The transfer must wait for the in-flight account deletion.");
        await Assert.ThrowsAsync<ValidationException>(() => transfer);
        await using var verify = new MercuriusDBContext(options);
        var persisted = await verify.Teams.SingleAsync(candidate => candidate.Id == team.Id);
        Assert.Equal(captain.Id, persisted.CaptainUserId);
        Assert.Equal(0, persisted.Version);
    }

    [Fact]
    public async Task Succession_WaitsForInFlightCandidateDeletion_ThenSkipsThatCandidate()
    {
        await using var database = PostgresTestDatabase.CreateMigrated();
        var options = new DbContextOptionsBuilder<MercuriusDBContext>().UseNpgsql(database.ConnectionString).Options;
        var captain = TeamTestUsers.Create();
        var senior = TeamTestUsers.Create();
        var junior = TeamTestUsers.Create();
        var team = TeamsTestHarness.CreateTeam("Alpha", captain, DateTime.UtcNow.AddDays(-10));
        team.AddMember(senior.Id, DateTime.UtcNow.AddDays(-5));
        team.AddMember(junior.Id, DateTime.UtcNow.AddDays(-1));
        await using (var setup = new MercuriusDBContext(options))
        {
            setup.Users.AddRange(captain, senior, junior);
            setup.Teams.Add(team);
            await setup.SaveChangesAsync();
            (await setup.Users.SingleAsync(user => user.Id == captain.Id)).Anonymize(DateTime.UtcNow);
            await setup.SaveChangesAsync();
        }

        await using var deletionContext = new MercuriusDBContext(options);
        await using var deletion = await deletionContext.Database.BeginTransactionAsync();
        await TeamsTestHarness.CreateIdentityFacade(deletionContext).GetUserProfileForUpdateAsync(new UserId(senior.Id));
        (await deletionContext.Users.SingleAsync(user => user.Id == senior.Id)).Anonymize(DateTime.UtcNow);
        await deletionContext.SaveChangesAsync();

        await using var cleanupContext = new MercuriusDBContext(options);
        var cleanup = TeamsTestHarness.CreateCleanupService(
            cleanupContext,
            identityModule: TeamsTestHarness.CreateIdentityFacade(cleanupContext));
        var succession = cleanup.RemoveDeletedUserAsync(captain.Id);

        var completedEarly = await Task.WhenAny(succession, Task.Delay(TimeSpan.FromMilliseconds(500))) == succession;
        await deletion.CommitAsync();
        await succession;

        Assert.False(completedEarly, "Succession must wait for the in-flight deletion of its candidate.");
        await using var verify = new MercuriusDBContext(options);
        Assert.Equal(junior.Id, (await verify.Teams.SingleAsync(candidate => candidate.Id == team.Id)).CaptainUserId);
    }

    [Fact]
    public async Task AcceptInvite_WaitsForInFlightDeletion_ThenRejectsWithoutMembership()
    {
        await using var database = PostgresTestDatabase.CreateMigrated();
        var options = new DbContextOptionsBuilder<MercuriusDBContext>().UseNpgsql(database.ConnectionString).Options;
        var captain = TeamTestUsers.Create();
        var invited = TeamTestUsers.Create();
        var team = TeamsTestHarness.CreateTeam("Alpha", captain);
        var invite = TeamsTestHarness.CreatePendingInvite(team, invited);
        await using (var setup = new MercuriusDBContext(options))
        {
            setup.Users.AddRange(captain, invited);
            setup.Teams.Add(team);
            setup.Set<TeamInvite>().Add(invite);
            await setup.SaveChangesAsync();
        }

        await using var deletionContext = new MercuriusDBContext(options);
        await using var deletion = await deletionContext.Database.BeginTransactionAsync();
        await TeamsTestHarness.CreateIdentityFacade(deletionContext).GetUserProfileForUpdateAsync(new UserId(invited.Id));
        (await deletionContext.Users.SingleAsync(user => user.Id == invited.Id)).Anonymize(DateTime.UtcNow);
        await deletionContext.SaveChangesAsync();

        await using var acceptContext = new MercuriusDBContext(options);
        var service = TeamsTestHarness.CreateTeamService(
            acceptContext,
            identityModule: TeamsTestHarness.CreateIdentityFacade(acceptContext));
        var accept = service.RespondToInviteAsync(team.Id, invited.Id, true);

        var completedEarly = await Task.WhenAny(accept, Task.Delay(TimeSpan.FromMilliseconds(500))) == accept;
        await deletion.CommitAsync();

        Assert.False(completedEarly, "The acceptance must wait for the in-flight account deletion.");
        await Assert.ThrowsAsync<NotFoundException>(() => accept);
        await using var verify = new MercuriusDBContext(options);
        Assert.False(await verify.Set<TeamMember>().AnyAsync(member => member.UserId == invited.Id));
    }
}
