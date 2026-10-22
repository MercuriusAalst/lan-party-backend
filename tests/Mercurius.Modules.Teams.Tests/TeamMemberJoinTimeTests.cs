using Mercurius.LAN.API.Data;
using Mercurius.Modules.Teams.Application.DTOs;
using Mercurius.Modules.Teams.Application.Services;
using Mercurius.Modules.Teams.Contracts;
using Mercurius.TestInfrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mercurius.Modules.Teams.Tests;

public class TeamMemberJoinTimeTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task CreateCurrentUserTeam_RecordsCreatorJoinTime()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var clock = new MutableTimeProvider(T0);
        var captain = TeamTestUsers.Create();
        dbContext.Users.Add(captain);
        await dbContext.SaveChangesAsync();

        var created = await CreateTeamService(dbContext, clock)
            .CreateCurrentUserTeamAsync(captain.Auth0UserId, new CreateTeamDTO { Name = "Alpha" });

        Assert.Equal(T0, await GetJoinedAtAsync(dbContext, created.Id, captain.Id));
    }

    [Fact]
    public async Task AcceptInvite_RecordsJoinTime_AndRejoinRecordsNewTime()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var clock = new MutableTimeProvider(T0);
        var captain = TeamTestUsers.Create();
        var member = TeamTestUsers.Create();
        dbContext.Users.AddRange(captain, member);
        await dbContext.SaveChangesAsync();
        var service = CreateTeamService(dbContext, clock);
        var team = await service.CreateCurrentUserTeamAsync(captain.Auth0UserId, new CreateTeamDTO { Name = "Alpha" });

        clock.UtcNow = T0.AddHours(1);
        var firstInvite = await service.InviteUserAsync(captain.Auth0UserId, team.Id, member.Id);
        await service.RespondToInviteAsync(member.Auth0UserId, firstInvite.Id, true);
        Assert.Equal(T0.AddHours(1), await GetJoinedAtAsync(dbContext, team.Id, member.Id));

        clock.UtcNow = T0.AddHours(2);
        await service.LeaveTeamAsync(member.Auth0UserId, team.Id);
        clock.UtcNow = T0.AddHours(3);
        var secondInvite = await service.InviteUserAsync(captain.Auth0UserId, team.Id, member.Id);
        await service.RespondToInviteAsync(member.Auth0UserId, secondInvite.Id, true);

        Assert.Equal(T0.AddHours(3), await GetJoinedAtAsync(dbContext, team.Id, member.Id));
    }

    [Fact]
    public async Task TransferCaptain_DoesNotChangeJoinTimes()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var clock = new MutableTimeProvider(T0);
        var captain = TeamTestUsers.Create();
        var member = TeamTestUsers.Create();
        dbContext.Users.AddRange(captain, member);
        await dbContext.SaveChangesAsync();
        var service = CreateTeamService(dbContext, clock);
        var team = await service.CreateCurrentUserTeamAsync(captain.Auth0UserId, new CreateTeamDTO { Name = "Alpha" });
        clock.UtcNow = T0.AddHours(1);
        var invite = await service.InviteUserAsync(captain.Auth0UserId, team.Id, member.Id);
        await service.RespondToInviteAsync(member.Auth0UserId, invite.Id, true);

        clock.UtcNow = T0.AddHours(5);
        await service.TransferCaptainAsync(captain.Auth0UserId, team.Id, member.Id);

        Assert.Equal(T0, await GetJoinedAtAsync(dbContext, team.Id, captain.Id));
        Assert.Equal(T0.AddHours(1), await GetJoinedAtAsync(dbContext, team.Id, member.Id));
    }

    [Fact]
    public void TeamDtos_DoNotExposeJoinTimes()
    {
        var dtoTypes = typeof(TeamService).Assembly
            .GetTypes()
            .Where(type => type.Namespace == "Mercurius.Modules.Teams.Application.DTOs")
            .Concat(typeof(TeamMemberSnapshot).Assembly.GetTypes());

        Assert.DoesNotContain(
            dtoTypes.SelectMany(type => type.GetProperties()),
            property => property.Name.Contains("Joined", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<DateTime?> GetJoinedAtAsync(MercuriusDBContext dbContext, Guid teamId, Guid userId)
    {
        dbContext.ChangeTracker.Clear();
        return await dbContext.Set<TeamMember>()
            .Where(member => member.TeamId == teamId && member.UserId == userId)
            .Select(member => member.JoinedAtUtc)
            .SingleAsync();
    }

    private static TeamEventPublishingDecorator CreateTeamService(MercuriusDBContext dbContext, TimeProvider clock) =>
        TeamsTestHarness.CreateTeamService(dbContext, clock);
}
