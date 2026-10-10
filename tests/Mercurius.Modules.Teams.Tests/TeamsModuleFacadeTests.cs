using Mercurius.TestInfrastructure;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.LAN.API.Data;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Teams;
using Mercurius.Modules.Teams.Contracts;
using Mercurius.Modules.Teams.Infrastructure;
using Mercurius.Modules.Teams.Application.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mercurius.Modules.Teams.Tests;

public class TeamsModuleFacadeTests
{
    [Fact]
    public void AddTeamsModule_RegistersTeamsModuleContract()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MercuriusDBContext>(options =>
            options.UseNpgsql());

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TeamInvite:ResendCooldownDays"] = "7",
                ["FileStorage:MaxFileSizeInMB"] = "2"
            })
            .Build();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<ITeamTournamentReadService>(
            new StubTeamTournamentReadService(Array.Empty<PublicTeamTournamentSummary>()));
        services.AddScoped<IIdentityModule, DbContextIdentityModule>();
        services.AddTeamsModule<MercuriusDBContext>(configuration);

        using var provider = services.BuildServiceProvider();

        var module = provider.GetRequiredService<ITeamsModule>();

        Assert.IsType<TeamsModuleFacade>(module);
    }

    [Fact]
    public async Task GetTeamSummaryAsync_ReturnsContractProjection()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("captain", "Captain", "Player");
        var team = new Team("Alpha Team", captain.Id)
        {
            Id = Guid.NewGuid(),
            LogoUrl = "https://example.test/logo.png"
        };
        team.AddMember(captain.Id);
        dbContext.Users.Add(captain);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        var module = CreateModule(dbContext);

        var summary = await module.GetTeamSummaryAsync(new TeamId(team.Id));

        Assert.NotNull(summary);
        Assert.Equal(team.Id, summary.Id.Value);
        Assert.Equal("Alpha Team", summary.Name);
        Assert.Equal(captain.Id, summary.CaptainUserId!.Value.Value);
        Assert.Equal("https://example.test/logo.png", summary.LogoUrl);
        Assert.False(summary.IsDeleted);
    }

    [Fact]
    public async Task GetCaptainedTeamIdsAsync_ReturnsActiveTeamsInStableOrderWithoutHydratingMembers()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("captain", "Captain", "Player");
        var otherCaptain = CreateUser("other-captain", "Other", "Captain");
        var laterTeam = new Team("Later Team", captain.Id)
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000002")
        };
        laterTeam.AddMember(captain.Id);
        var earlierTeam = new Team("Earlier Team", captain.Id)
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001")
        };
        earlierTeam.AddMember(captain.Id);
        var unrelatedTeam = new Team("Unrelated Team", otherCaptain.Id)
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000003")
        };
        unrelatedTeam.AddMember(otherCaptain.Id);
        var deletedTeam = new Team("Deleted Team", captain.Id)
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000004"),
            IsDeleted = true
        };
        deletedTeam.AddMember(captain.Id);

        dbContext.Users.AddRange(captain, otherCaptain);
        dbContext.Teams.AddRange(laterTeam, earlierTeam, unrelatedTeam, deletedTeam);
        await dbContext.SaveChangesAsync();

        var identityModule = new DbContextIdentityModule(dbContext);
        var module = new TeamsModuleFacade(
            new TeamsDbContextAdapter<MercuriusDBContext>(dbContext),
            identityModule,
            new StubTeamTournamentReadService([]));

        var teamIds = await module.GetCaptainedTeamIdsAsync(new UserId(captain.Id));

        Assert.Equal(
            [earlierTeam.Id, laterTeam.Id],
            teamIds.Select(teamId => teamId.Value).ToArray());
        Assert.Equal(0, identityModule.BatchCallCount);
    }

    [Fact]
    public async Task GetTeamRosterSnapshotAsync_ReturnsOrderedMembersAndDisplayNames()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("captain", "Captain", "Player");
        var zeta = CreateUser("zeta", null, null);
        var alpha = CreateUser("alpha", "Alpha", "Member");
        var team = new Team("Roster Team", captain.Id) { Id = Guid.NewGuid() };
        team.AddMember(captain.Id);
        team.AddMember(zeta.Id);
        team.AddMember(alpha.Id);
        dbContext.Users.AddRange(captain, zeta, alpha);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        var module = CreateModule(dbContext);

        var roster = await module.GetTeamRosterSnapshotAsync(new TeamId(team.Id));

        Assert.NotNull(roster);
        Assert.Equal(team.Id, roster.TeamId.Value);
        Assert.Equal(captain.Id, roster.CaptainUserId!.Value.Value);
        Assert.Equal(["alpha", "captain", "zeta"], roster.Members.Select(member => member.Username).ToList());
        Assert.Equal(["Alpha Member", "Captain Player", "zeta"], roster.Members.Select(member => member.DisplayName).ToList());
        Assert.True(roster.Members.Single(member => member.UserId.Value == captain.Id).IsCaptain);
    }

    [Fact]
    public async Task GetPublicTeamProfileAsync_UsesPublicProjectionAndTournamentContract()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("CaptainMerc", "Captain", "Merc");
        var zeta = CreateUser("zeta", "Zeta", "Member");
        var alpha = CreateUser("Alpha", "Alpha", "Member");
        var hidden = CreateUser(null, "Hidden", "Member");
        var team = new Team("Mercury Wolves", captain.Id)
        {
            Id = Guid.NewGuid(),
            LogoUrl = "https://example.test/wolves.png"
        };
        team.AddMember(captain.Id);
        team.AddMember(zeta.Id);
        team.AddMember(alpha.Id);
        team.AddMember(hidden.Id);
        dbContext.Users.AddRange(captain, zeta, alpha, hidden);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        var tournaments = new[]
        {
            new PublicTeamTournamentSummary(new TournamentId(Guid.Parse("00000000-0000-0000-0000-000000000001")), "Alpha Cup")
        };
        var module = CreateModule(dbContext, tournaments);

        var profile = await module.GetPublicTeamProfileAsync("  mErCuRy WoLvEs  ");

        Assert.NotNull(profile);
        Assert.Equal("Mercury Wolves", profile.TeamName);
        Assert.Equal("CaptainMerc", profile.CaptainUsername);
        Assert.Equal("https://example.test/wolves.png", profile.LogoUrl);
        Assert.Equal(["Alpha", "CaptainMerc", "zeta"], profile.Members.Select(member => member.Username).ToList());
        Assert.Equal(tournaments, profile.Tournaments);
    }

    [Fact]
    public async Task GetPublicTeamIdByNameAsync_ReturnsOnlyActiveNormalizedTeam()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("captain", "Captain", "Player");
        var activeTeam = new Team("Mercury Wolves", captain.Id) { Id = Guid.NewGuid() };
        var deletedTeam = new Team("Deleted Wolves", captain.Id) { Id = Guid.NewGuid(), IsDeleted = true };
        dbContext.Users.Add(captain);
        dbContext.Teams.AddRange(activeTeam, deletedTeam);
        await dbContext.SaveChangesAsync();

        var module = CreateModule(dbContext);

        var activeId = await module.GetPublicTeamIdByNameAsync("  mErCuRy WoLvEs  ");
        var deletedId = await module.GetPublicTeamIdByNameAsync("deleted wolves");
        var missingId = await module.GetPublicTeamIdByNameAsync("missing wolves");

        Assert.Equal(activeTeam.Id, activeId!.Value.Value);
        Assert.Null(deletedId);
        Assert.Null(missingId);
    }

    [Fact]
    public async Task GetPublicTeamNamesByIdsAsync_ReturnsOnlyActiveNamedTeams()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("captain", "Captain", "Player");
        var publicTeam = new Team("Current Wolves", captain.Id) { Id = Guid.NewGuid() };
        var deletedTeam = new Team("Deleted Wolves", captain.Id) { Id = Guid.NewGuid(), IsDeleted = true };
        var unnamedTeam = new Team("Unnamed Wolves", captain.Id) { Id = Guid.NewGuid(), Name = string.Empty };
        dbContext.Users.Add(captain);
        dbContext.Teams.AddRange(publicTeam, deletedTeam, unnamedTeam);
        await dbContext.SaveChangesAsync();

        var module = CreateModule(dbContext);
        var names = await module.GetPublicTeamNamesByIdsAsync(
            [new TeamId(publicTeam.Id), new TeamId(deletedTeam.Id), new TeamId(unnamedTeam.Id)]);

        Assert.Equal(new Dictionary<TeamId, string>
        {
            [new TeamId(publicTeam.Id)] = "Current Wolves"
        }, names);
    }

    [Fact]
    public async Task Guards_ReturnReasonCodes_ForMissingDeletedAndNonCaptainTeams()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("captain", "Captain", "Player");
        var outsider = CreateUser("outsider", "Outside", "Player");
        var team = new Team("Guard Team", captain.Id) { Id = Guid.NewGuid() };
        team.AddMember(captain.Id);
        var deletedTeam = new Team("Deleted Team", captain.Id) { Id = Guid.NewGuid() };
        deletedTeam.AddMember(captain.Id);
        deletedTeam.Delete(DateTime.UtcNow);
        dbContext.Users.AddRange(captain, outsider);
        dbContext.Teams.AddRange(team, deletedTeam);
        await dbContext.SaveChangesAsync();

        var module = CreateModule(dbContext);

        var missing = await module.GetRegistrationEligibilityAsync(
            new TeamId(Guid.NewGuid()),
            new UserId(captain.Id),
            new TournamentId(Guid.NewGuid()));
        var nonCaptain = await module.GetRegistrationEligibilityAsync(
            new TeamId(team.Id),
            new UserId(outsider.Id),
            new TournamentId(Guid.NewGuid()));
        var deleted = await module.CanMutateMembershipAsync(new TeamId(deletedTeam.Id), new UserId(captain.Id));

        Assert.False(missing.Eligible);
        Assert.Equal(["team_not_found"], missing.ReasonCodes);
        Assert.False(nonCaptain.Eligible);
        Assert.Equal(["captain_required"], nonCaptain.ReasonCodes);
        Assert.False(deleted.CanMutate);
        Assert.Equal(["team_deleted", "captain_required"], deleted.ReasonCodes);
    }

    [Fact]
    public async Task GetTeamRosterSnapshotsAsync_BatchHydratesAllMembersOnce()
    {
        await using var dbContext = CreateDbContext();
        var firstCaptain = CreateUser("captain-one", "Captain", "One");
        var firstMember = CreateUser("member-one", "Member", "One");
        var secondCaptain = CreateUser("captain-two", "Captain", "Two");
        var firstTeam = new Team("First Team", firstCaptain.Id) { Id = Guid.NewGuid() };
        firstTeam.AddMember(firstCaptain.Id);
        firstTeam.AddMember(firstMember.Id);
        var secondTeam = new Team("Second Team", secondCaptain.Id) { Id = Guid.NewGuid() };
        secondTeam.AddMember(secondCaptain.Id);

        dbContext.Users.AddRange(firstCaptain, firstMember, secondCaptain);
        dbContext.Teams.AddRange(firstTeam, secondTeam);
        await dbContext.SaveChangesAsync();

        var identityModule = new DbContextIdentityModule(dbContext);
        var module = new TeamsModuleFacade(
            new TeamsDbContextAdapter<MercuriusDBContext>(dbContext),
            identityModule,
            new StubTeamTournamentReadService([]));

        var rosters = await module.GetTeamRosterSnapshotsAsync(
            [new TeamId(firstTeam.Id), new TeamId(secondTeam.Id)]);

        Assert.Equal(2, rosters.Count);
        Assert.Equal(1, identityModule.BatchCallCount);
        Assert.Equal(
            new[] { firstCaptain.Id, firstMember.Id, secondCaptain.Id }.Order().ToArray(),
            identityModule.LastBatchUserIds.Select(userId => userId.Value).Order().ToArray());
    }

    private static TeamsModuleFacade CreateModule(
        MercuriusDBContext dbContext,
        IReadOnlyList<PublicTeamTournamentSummary>? tournaments = null)
    {
        return new TeamsModuleFacade(
            new TeamsDbContextAdapter<MercuriusDBContext>(dbContext),
            new DbContextIdentityModule(dbContext),
            new StubTeamTournamentReadService(tournaments ?? Array.Empty<PublicTeamTournamentSummary>()));
    }

    private static MercuriusDBContext CreateDbContext() => PostgresTestDatabase.CreateDbContext();

    private static User CreateUser(string? username, string? firstname, string? lastname)
    {
        return new User
        {
            Id = Guid.NewGuid(),
            Auth0UserId = $"auth0|{Guid.NewGuid():N}",
            Username = username,
            NormalizedUsername = username?.ToLowerInvariant(),
            Firstname = firstname,
            Lastname = lastname,
            Email = $"{Guid.NewGuid():N}@example.com"
        };
    }

    private sealed class StubTeamTournamentReadService : ITeamTournamentReadService
    {
        private readonly IReadOnlyList<PublicTeamTournamentSummary> _tournaments;

        public StubTeamTournamentReadService(IReadOnlyList<PublicTeamTournamentSummary> tournaments)
        {
            _tournaments = tournaments;
        }

        public Task<IReadOnlyList<PublicTeamTournamentSummary>> GetPublicTeamTournamentsAsync(
            Guid teamId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_tournaments);
        }

        public Task<bool> IsUserInProtectedTournamentRosterAsync(
            Guid teamId,
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public Task<bool> IsTeamInDeleteBlockingTournamentAsync(
            Guid teamId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public Task<bool> IsTeamLogoReferencedAsync(
            string logoUrl,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }
    }
}
