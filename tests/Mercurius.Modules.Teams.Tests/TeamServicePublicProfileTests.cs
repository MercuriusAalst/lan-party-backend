using Mercurius.TestInfrastructure;
using Mercurius.LAN.API.Data;
using Mercurius.Modules.Teams.Application.DTOs;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Shared.Exceptions;
using Mercurius.Modules.Teams.Application.Services;
using Mercurius.Modules.Teams.Infrastructure;
using Mercurius.Modules.Teams.Contracts;
using Mercurius.Modules.Media.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Mercurius.Modules.Teams.Tests;

public class TeamServicePublicProfileTests
{
    [Fact]
    public async Task GetPublicTeamProfileAsync_ReturnsProfile_ForCaseInsensitiveLookup()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("CaptainMerc");
        var zetaMember = CreateUser("zeta");
        var alphaMember = CreateUser("Alpha");
        var team = new Team("Mercury Wolves", captain.Id) { Id = Guid.NewGuid() };
        team.AddMember(captain.Id);
        team.AddMember(zetaMember.Id);
        team.AddMember(alphaMember.Id);
        dbContext.Users.AddRange(captain, zetaMember, alphaMember);
        dbContext.Teams.Add(team);

        var tournament = CreateTournament(Guid.Parse("00000000-0000-0000-0000-000000000001"), "Alpha Cup");
        dbContext.Set<TournamentAggregate>().Add(tournament);
        AddActiveTeamRegistration(dbContext, tournament, team, captain);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext);

        var profile = await service.GetPublicTeamProfileAsync("  mErCuRy WoLvEs  ");

        Assert.Equal("Mercury Wolves", profile.TeamName);
        Assert.Equal("CaptainMerc", profile.CaptainUsername);
        Assert.Equal(["Alpha", "CaptainMerc", "zeta"], profile.Members.Select(member => member.Username).ToList());
    }

    [Fact]
    public async Task GetPublicTeamProfileAsync_Throws_WhenTeamDoesNotExist()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        await Assert.ThrowsAsync<NotFoundException>(() => service.GetPublicTeamProfileAsync("missing-team"));
    }

    [Fact]
    public async Task GetPublicTeamProfileAsync_ValidatesAndNormalizesTeamNameInput()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var exception = await Assert.ThrowsAsync<ValidationException>(() => service.GetPublicTeamProfileAsync("   "));
        Assert.Equal("Team name is required.", exception.Message);
    }

    [Fact]
    public async Task GetPublicTeamProfileAsync_OmitsPrivateAndInviteData()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("CaptainOne");
        var bravoMember = CreateUser("Bravo");
        var unnamedMember = CreateUser(null);
        var blankMember = CreateUser(" ");
        var team = new Team("Privacy Squad", captain.Id) { Id = Guid.NewGuid() };
        team.AddMember(captain.Id);
        team.AddMember(bravoMember.Id);
        team.AddMember(unnamedMember.Id);
        team.AddMember(blankMember.Id);
        dbContext.Users.AddRange(captain, bravoMember, unnamedMember, blankMember);
        team.TeamInvites.Add(new TeamInvite
        {
            TeamId = team.Id,
            UserId = Guid.NewGuid(),
            Status = TeamInviteStatus.Pending
        });
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext);
        var profile = await service.GetPublicTeamProfileAsync("privacy squad");

        Assert.Equal(["Bravo", "CaptainOne"], profile.Members.Select(member => member.Username).ToList());
        Assert.Null(typeof(PublicTeamProfileDTO).GetProperty("TeamInvites"));
        Assert.Equal(["Username"], typeof(PublicTeamMemberDTO).GetProperties().Select(property => property.Name).ToArray());
        Assert.Null(typeof(PublicTeamMemberDTO).GetProperty("Email"));
    }

    [Fact]
    public async Task GetPublicTeamProfileAsync_ReturnsRegisteredTournamentsInStableOrder()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("CaptainTournament");
        var team = new Team("Tournament Squad", captain.Id) { Id = Guid.NewGuid() };
        team.AddMember(captain.Id);
        dbContext.Users.Add(captain);
        dbContext.Teams.Add(team);

        var alphaOne = CreateTournament(Guid.Parse("00000000-0000-0000-0000-000000000001"), "Alpha Cup");
        var alphaTwo = CreateTournament(Guid.Parse("00000000-0000-0000-0000-000000000002"), "Alpha Cup");
        var zeta = CreateTournament(Guid.Parse("00000000-0000-0000-0000-000000000003"), "Zeta Clash");

        var otherCaptain = CreateUser("CaptainOther");
        var otherTeam = new Team("Other Team", otherCaptain.Id) { Id = Guid.NewGuid() };
        otherTeam.AddMember(otherCaptain.Id);
        dbContext.Users.Add(otherCaptain);
        var hiddenTournament = CreateTournament(Guid.Parse("00000000-0000-0000-0000-000000000004"), "Aardvark Cup");

        dbContext.Teams.Add(otherTeam);
        dbContext.Set<TournamentAggregate>().AddRange(alphaOne, alphaTwo, zeta, hiddenTournament);
        AddActiveTeamRegistration(dbContext, alphaOne, team, captain);
        AddActiveTeamRegistration(dbContext, alphaTwo, team, captain);
        AddActiveTeamRegistration(dbContext, zeta, team, captain);
        AddActiveTeamRegistration(dbContext, hiddenTournament, otherTeam, otherCaptain);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext);
        var profile = await service.GetPublicTeamProfileAsync("tournament squad");

        Assert.Equal(
            [
                "00000000-0000-0000-0000-000000000001",
                "00000000-0000-0000-0000-000000000002",
                "00000000-0000-0000-0000-000000000003"
            ],
            profile.Tournaments.Select(tournament => tournament.TournamentId.ToString()).ToList());

        Assert.Equal(["Alpha Cup", "Alpha Cup", "Zeta Clash"], profile.Tournaments.Select(tournament => tournament.Name).ToList());
    }

    private static TeamService CreateService(MercuriusDBContext dbContext)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TeamInvite:ResendCooldownDays"] = "7"
            })
            .Build();

        return new TeamService(
            new TeamsDbContextAdapter<MercuriusDBContext>(dbContext),
            configuration,
            new DbContextIdentityModule(dbContext),
            new NoopMediaModule(),
            tournamentReadService: new StubTeamTournamentReadService(dbContext),
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<TeamService>.Instance,
            TimeProvider.System);
    }

    private static MercuriusDBContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new MercuriusDBContext(options);
    }

    private static User CreateUser(string? username)
    {
        return new User
        {
            Id = Guid.NewGuid(),
            Auth0UserId = $"auth0|{Guid.NewGuid():N}",
            Username = username,
            Firstname = "First",
            Lastname = "Last",
            Email = "user@example.com",
            DiscordId = "discord",
            SteamId = "steam",
            RiotId = "riot"
        };
    }

    private static TournamentAggregate CreateTournament(Guid id, string name)
    {
        return new TournamentAggregate(
            name,
            BracketType.SingleElimination,
            GameFormat.BestOf3,
            GameFormat.BestOf5,
            ParticipationMode.Team,
            5,
            DateTime.UtcNow,
            30,
            10).Set(x => x.Id, id);
    }

    private static void AddActiveTeamRegistration(MercuriusDBContext dbContext, TournamentAggregate tournament, Team team, User captain)
    {
        dbContext.Set<TournamentRegistration>().Add(new TournamentRegistration
        {
            Id = Guid.NewGuid(),
            Tournament = tournament,
            TournamentId = tournament.Id,
            Kind = TournamentRegistrationKind.Team,
            Status = TournamentRegistrationStatus.Active,
            RegisteredByUserId = captain.Id,
            RegisteredByUsernameAtRegistration = captain.Username ?? string.Empty,
            TeamId = team.Id,
            TeamNameAtRegistration = team.Name,
            TeamCaptainUserIdAtRegistration = team.CaptainUserId,
            RosterMembers =
            [
                new TournamentRegistrationRosterMember
                {
                    Id = Guid.NewGuid(),
                    Tournament = tournament,
                    TournamentId = tournament.Id,
                    TeamId = team.Id,
                    TeamNameAtRegistration = team.Name,
                    UserId = captain.Id,
                    UsernameAtRegistration = captain.Username ?? string.Empty,
                    DisplayNameAtRegistration = captain.DisplayName,
                    IsCaptain = true,
                    ConfirmationStatus = RosterMemberConfirmationStatus.AutoConfirmed
                }
            ]
        });
    }

    private sealed class StubTeamTournamentReadService(MercuriusDBContext dbContext) : ITeamTournamentReadService
    {
        public async Task<IReadOnlyList<PublicTeamTournamentSummary>> GetPublicTeamTournamentsAsync(Guid teamId, CancellationToken cancellationToken = default)
        {
            return await dbContext.Set<TournamentRegistration>()
                .AsNoTracking()
                .Where(registration => registration.TeamId == teamId && registration.Status == TournamentRegistrationStatus.Active)
                .Select(registration => new PublicTeamTournamentSummary(
                    new TournamentId(registration.TournamentId),
                    registration.Tournament.Name))
                .OrderBy(tournament => tournament.Name)
                .ThenBy(tournament => tournament.TournamentId.Value)
                .ToListAsync(cancellationToken);
        }

        public Task<bool> IsUserInProtectedTournamentRosterAsync(Guid teamId, Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<bool> IsTeamInDeleteBlockingTournamentAsync(Guid teamId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<bool> IsTeamLogoReferencedAsync(string logoUrl, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private sealed class NoopMediaModule : IMediaModule
    {
        public Task<StoredMediaAsset> SaveImageAsync(MediaUpload upload, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StoredMediaAsset("https://example.test/team-logo.webp"));

        public Task DeleteImageAsync(string? mediaUrl, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
