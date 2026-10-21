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
