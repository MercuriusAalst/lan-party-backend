using Mercurius.Modules.Shared.Exceptions;
using Mercurius.Modules.Tournament.Infrastructure;
using Mercurius.TestInfrastructure;
using Mercurius.LAN.API.Data;
using Microsoft.EntityFrameworkCore;

namespace Mercurius.Modules.Tournament.Tests;

public sealed class MatchOpponentProfileTests
{
    [Fact]
    public async Task IndividualParticipant_CanReadOnlyTheirAssignedOpponentDetails()
    {
        var current = CreateUser("current");
        var opponent = CreateUser("opponent");
        var unrelated = CreateUser("unrelated");
        var tournament = CreateTournament(ParticipationMode.Individual);
        var match = CreateMatch(tournament, ParticipationMode.Individual);
        match.Set(x => x.UserParticipant1Id, current.Id);
        match.Set(x => x.UserParticipant2Id, opponent.Id);
        tournament.Matches.Add(match);

        await using var dbContext = CreateDbContext();
        await SaveMatchGraph(dbContext, tournament, [current, opponent, unrelated]);
        var service = CreateService(dbContext, [current, opponent, unrelated]);

        var profile = await service.GetOpponentUserProfileAsync(match.Id, current.Auth0UserId);

        Assert.Equal(opponent.Username, profile.Username);
        Assert.Equal(opponent.Firstname, profile.Firstname);
        Assert.Equal(opponent.Lastname, profile.Lastname);
        Assert.Equal(opponent.DiscordId, profile.DiscordId);
        Assert.Equal(opponent.SteamId, profile.SteamId);
        Assert.Equal(opponent.RiotId, profile.RiotId);
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.GetOpponentUserProfileAsync(match.Id, unrelated.Auth0UserId));
    }

    [Fact]
    public async Task TeamCaptain_CanReadOpposingCaptainDetails_ButTeamMemberCannot()
    {
        var captain1 = CreateUser("captain-one");
        var captain2 = CreateUser("captain-two");
        var member = CreateUser("member");
        var team1 = CreateTeam("Team One", captain1, member);
        var team2 = CreateTeam("Team Two", captain2);
        var tournament = CreateTournament(ParticipationMode.Team);
        var match = CreateMatch(tournament, ParticipationMode.Team);
        match.Set(x => x.TeamParticipant1Id, team1.Id);
        match.Set(x => x.TeamParticipant2Id, team2.Id);
        tournament.Matches.Add(match);

        await using var dbContext = CreateDbContext();
        await SaveMatchGraph(dbContext, tournament, [captain1, captain2, member], [team1, team2]);
        var service = CreateService(dbContext, [captain1, captain2, member], [team1, team2]);

        var profile = await service.GetOpponentUserProfileAsync(match.Id, captain1.Auth0UserId);

        Assert.Equal(captain2.Username, profile.Username);
        Assert.Equal(captain2.DiscordId, profile.DiscordId);
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.GetOpponentUserProfileAsync(match.Id, member.Auth0UserId));
    }

    [Fact]
    public async Task AssignedParticipant_GetsNotFoundWhenOpponentProfileIsMissing()
    {
        var current = CreateUser("current");
        var deletedOpponent = CreateUser("deleted-opponent");
        deletedOpponent.IsDeleted = true;
        var tournament = CreateTournament(ParticipationMode.Individual);
        var match = CreateMatch(tournament, ParticipationMode.Individual);
        match.Set(x => x.UserParticipant1Id, current.Id);
        match.Set(x => x.UserParticipant2Id, deletedOpponent.Id);
        tournament.Matches.Add(match);

        await using var dbContext = CreateDbContext();
        await SaveMatchGraph(dbContext, tournament, [current, deletedOpponent]);
        var service = CreateService(dbContext, [current, deletedOpponent]);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.GetOpponentUserProfileAsync(match.Id, current.Auth0UserId));
    }

    private static MercuriusDBContext CreateDbContext() => PostgresTestDatabase.CreateDbContext();

    private static TournamentAggregate CreateTournament(ParticipationMode mode) => new TournamentAggregate(
        "Profile privacy tournament",
        BracketType.SingleElimination,
        GameFormat.BestOf1,
        GameFormat.BestOf1,
        mode,
        mode == ParticipationMode.Team ? 2 : null)
        .Set(x => x.Id, Guid.NewGuid())
        .Set(x => x.Status, TournamentStatus.InProgress);

    private static Match CreateMatch(TournamentAggregate tournament, ParticipationMode mode) => new Match()
        .Set(x => x.Id, Guid.NewGuid())
        .Set(x => x.TournamentId, tournament.Id)
        .Set(x => x.Tournament, tournament)
        .Set(x => x.ParticipationMode, mode)
        .Set(x => x.Format, GameFormat.BestOf1);

    private static User CreateUser(string name) => new()
    {
        Id = Guid.NewGuid(),
        Auth0UserId = $"auth0|{name}",
        Username = name,
        NormalizedUsername = name,
        Firstname = $"First {name}",
        Lastname = $"Last {name}",
        DiscordId = $"discord-{name}",
        SteamId = $"steam-{name}",
        RiotId = $"riot-{name}"
    };

    private static Team CreateTeam(string name, User captain, User? member = null)
    {
        var team = new Team(name, captain.Id) { Id = Guid.NewGuid() };
        team.AddMember(captain.Id);
        if (member is not null)
            team.AddMember(member.Id);
        return team;
    }

    private static async Task SaveMatchGraph(
        MercuriusDBContext dbContext,
        TournamentAggregate tournament,
        IReadOnlyCollection<User> users,
        IReadOnlyCollection<Team>? teams = null)
    {
        dbContext.Users.AddRange(users);
        if (teams is not null)
            dbContext.Teams.AddRange(teams);
        dbContext.Set<TournamentAggregate>().Add(tournament);
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
            TimeProvider.System,
            new MatchBracketImpactAnalyzer(new TournamentDbContextAdapter<MercuriusDBContext>(dbContext)));
}
