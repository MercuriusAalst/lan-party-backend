using Mercurius.TestInfrastructure;
using System.Numerics;


namespace Mercurius.Modules.Tournament.Tests;

public class MatchModeratorTests
{
    [Fact]
    public void SingleElimination_GenerateMatchesForTournament_KeepsUsersModeSafe_AndAdvancesByeWinner()
    {
        var tournament = new TournamentAggregate("Bracket", BracketType.SingleElimination, GameFormat.BestOf1, GameFormat.BestOf1, ParticipationMode.Individual, null, DateTime.UtcNow, 30, 10);
        AddIndividualRegistration(tournament, CreateUser(1));
        AddIndividualRegistration(tournament, CreateUser(2));
        AddIndividualRegistration(tournament, CreateUser(3));

        var moderator = new SingleEliminationMatchModerator();

        var matches = moderator.GenerateMatchesForTournament(tournament).ToList();

        Assert.All(matches, match => Assert.Equal(ParticipationMode.Individual, match.ParticipationMode));
        Assert.All(matches, match => Assert.Null(match.TeamParticipant1Id));
        Assert.All(matches, match => Assert.Null(match.TeamParticipant2Id));

        var byeMatch = matches.Single(match => match.RoundNumber == 1 && (match.Participant1IsBYE || match.Participant2IsBYE));
        Assert.NotNull(byeMatch.UserWinnerId);

        var finalMatch = matches.Single(match => match.RoundNumber == 2);
        Assert.Contains(byeMatch.UserWinnerId, new[] { finalMatch.UserParticipant1Id, finalMatch.UserParticipant2Id });
    }

    [Fact]
    public void DoubleElimination_GenerateMatchesForTournament_KeepsTeamsModeSafe_AndPropagatesByeWinner()
    {
        var tournament = new TournamentAggregate("Bracket", BracketType.DoubleElimination, GameFormat.BestOf1, GameFormat.BestOf3, ParticipationMode.Team, 1, DateTime.UtcNow, 30, 10);
        AddTeamRegistration(tournament, CreateTeam(1));
        AddTeamRegistration(tournament, CreateTeam(2));
        AddTeamRegistration(tournament, CreateTeam(3));

        var moderator = new DoubleEliminationMatchModerator();

        var matches = moderator.GenerateMatchesForTournament(tournament).ToList();

        Assert.All(matches, match => Assert.Equal(ParticipationMode.Team, match.ParticipationMode));
        Assert.All(matches, match => Assert.Null(match.UserParticipant1Id));
        Assert.All(matches, match => Assert.Null(match.UserParticipant2Id));

        var byeMatch = matches.Single(match => !match.IsLowerBracketMatch && match.RoundNumber == 1 && (match.Participant1IsBYE || match.Participant2IsBYE));
        Assert.NotNull(byeMatch.TeamWinnerId);
        Assert.NotNull(byeMatch.WinnerNextMatch);
        Assert.Contains(byeMatch.TeamWinnerId, new[] { byeMatch.WinnerNextMatch.TeamParticipant1Id, byeMatch.WinnerNextMatch.TeamParticipant2Id });
    }

    [Fact]
    public void RoundRobin_GenerateMatchesForTournament_KeepsTeamsModeSafe()
    {
        var tournament = new TournamentAggregate("Bracket", BracketType.RoundRobin, GameFormat.BestOf1, GameFormat.BestOf1, ParticipationMode.Team, 1, DateTime.UtcNow, 30, 10);
        AddTeamRegistration(tournament, CreateTeam(1));
        AddTeamRegistration(tournament, CreateTeam(2));
        AddTeamRegistration(tournament, CreateTeam(3));

        var moderator = new RoundRobinMatchModerator();

        var matches = moderator.GenerateMatchesForTournament(tournament).ToList();

        Assert.NotEmpty(matches);
        Assert.All(matches, match => Assert.Equal(ParticipationMode.Team, match.ParticipationMode));
        Assert.All(matches, match => Assert.Null(match.UserParticipant1Id));
        Assert.All(matches, match => Assert.Null(match.UserParticipant2Id));
    }

    public static TheoryData<int> BracketSizes()
    {
        var sizes = new TheoryData<int>();
        for (var participantCount = 2; participantCount <= 33; participantCount++)
            sizes.Add(participantCount);
        return sizes;
    }

    [Theory]
    [MemberData(nameof(BracketSizes))]
    public void SingleElimination_GenerateMatchesForTournament_SeedsByesAgainstRealParticipants(int participantCount)
    {
        var tournament = new TournamentAggregate("Bracket", BracketType.SingleElimination, GameFormat.BestOf1, GameFormat.BestOf1, ParticipationMode.Individual, null, DateTime.UtcNow, 30, 10);
        for (var i = 1; i <= participantCount; i++)
            AddIndividualRegistration(tournament, CreateUser(i));

        var matches = new SingleEliminationMatchModerator().GenerateMatchesForTournament(tournament).ToList();

        AssertValidFirstRound(tournament, matches);
    }

    [Theory]
    [MemberData(nameof(BracketSizes))]
    public void DoubleElimination_GenerateMatchesForTournament_SeedsByesAgainstRealParticipants(int participantCount)
    {
        var tournament = new TournamentAggregate("Bracket", BracketType.DoubleElimination, GameFormat.BestOf1, GameFormat.BestOf1, ParticipationMode.Individual, null, DateTime.UtcNow, 30, 10);
        for (var i = 1; i <= participantCount; i++)
            AddIndividualRegistration(tournament, CreateUser(i));

        var matches = new DoubleEliminationMatchModerator().GenerateMatchesForTournament(tournament).ToList();

        AssertValidFirstRound(tournament, matches.Where(match => !match.IsLowerBracketMatch).ToList());
    }

    [Theory]
    [MemberData(nameof(BracketSizes))]
    public void SingleElimination_PlaysThroughToPlacements(int participantCount) =>
        AssertPlaysThroughToPlacements(BracketType.SingleElimination, new SingleEliminationMatchModerator(), participantCount);

    [Theory]
    [MemberData(nameof(BracketSizes))]
    public void DoubleElimination_PlaysThroughToPlacements(int participantCount)
    {
        // Two participants leave no lower bracket, so the grand final never receives a second participant.
        if (participantCount == 2)
            return;

        AssertPlaysThroughToPlacements(BracketType.DoubleElimination, new DoubleEliminationMatchModerator(), participantCount);
    }

    [Theory]
    [MemberData(nameof(BracketSizes))]
    public void EliminationBrackets_ByeAdvancementKeepsNullSourceMatch(int participantCount)
    {
        foreach (var (bracketType, moderator) in new (BracketType, IMatchModerator)[]
        {
            (BracketType.SingleElimination, new SingleEliminationMatchModerator()),
            (BracketType.DoubleElimination, new DoubleEliminationMatchModerator())
        })
        {
            var tournament = new TournamentAggregate("Bracket", bracketType, GameFormat.BestOf1, GameFormat.BestOf1, ParticipationMode.Team, 1, DateTime.UtcNow, 30, 10);
            for (var i = 1; i <= participantCount; i++)
                AddTeamRegistration(tournament, CreateTeam(i));

            var matches = moderator.GenerateMatchesForTournament(tournament).ToList();

            Assert.DoesNotContain(matches, match =>
                match.Participant1SourceMatchId == Guid.Empty || match.Participant2SourceMatchId == Guid.Empty);
        }
    }

    private static void AssertPlaysThroughToPlacements(BracketType bracketType, IMatchModerator moderator, int participantCount)
    {
        var tournament = new TournamentAggregate("Bracket", bracketType, GameFormat.BestOf1, GameFormat.BestOf1, ParticipationMode.Individual, null, DateTime.UtcNow, 30, 10);
        for (var i = 1; i <= participantCount; i++)
            AddIndividualRegistration(tournament, CreateUser(i));
        tournament.Set(x => x.Matches, moderator.GenerateMatchesForTournament(tournament).ToList());

        while (tournament.Matches.FirstOrDefault(match => match.HasBothParticipants && !match.HasWinner()) is { } playable)
            playable.SetScoresAndWinner(1, 0, DateTime.UtcNow);

        Assert.DoesNotContain(tournament.Matches, match =>
            (match.Participant1IsBYE && match.HasParticipant1()) || (match.Participant2IsBYE && match.HasParticipant2()));
        moderator.DeterminePlacements(tournament);
        var placed = tournament.Placements.SelectMany(placement => placement.Users).Select(user => user.UserId).ToList();
        Assert.Equal(tournament.GetActiveRegisteredUserIds().Order(), placed.Order());
    }

    private static void AssertValidFirstRound(TournamentAggregate tournament, IReadOnlyList<Match> upperBracket)
    {
        var participantIds = tournament.GetActiveRegisteredUserIds();
        var slotCount = (int)BitOperations.RoundUpToPowerOf2((uint)participantIds.Count);
        var firstRound = upperBracket.Where(match => match.RoundNumber == 1).ToList();

        Assert.Equal(slotCount / 2, firstRound.Count);
        Assert.DoesNotContain(firstRound, match => !match.HasParticipant1() && !match.HasParticipant2());
        Assert.Equal(slotCount - participantIds.Count, firstRound.Count(match => match.HasParticipant1() != match.HasParticipant2()));
        var seated = firstRound
            .SelectMany(match => new[] { match.UserParticipant1Id, match.UserParticipant2Id })
            .OfType<Guid>()
            .ToList();
        Assert.Equal(participantIds.Order(), seated.Order());
        Assert.DoesNotContain(upperBracket, match => match.RoundNumber > 1 && (match.Participant1IsBYE || match.Participant2IsBYE || match.HasWinner()));
    }

    private static User CreateUser(int id)
    {
        return new User
        {
            Id = Guid.NewGuid(),
            Username = $"user{id}",
            Firstname = $"First{id}",
            Lastname = $"Last{id}",
            Email = $"user{id}@example.test"
        };
    }

    private static Team CreateTeam(int id)
    {
        var captain = CreateUser(id + 100);
        var team = new Team($"Team{id}", captain.Id)
        {
            Id = Guid.NewGuid(),
            CaptainUserId = captain.Id
        };
        team.AddMember(captain.Id);
        return team;
    }

    private static void AddIndividualRegistration(TournamentAggregate tournament, User user)
    {
        tournament.TournamentRegistrations.Add(new TournamentRegistration
        {
            Id = Guid.NewGuid(),
            Tournament = tournament,
            TournamentId = tournament.Id,
            Kind = TournamentRegistrationKind.Individual,
            Status = TournamentRegistrationStatus.Active,
            RegisteredByUserId = user.Id,
            RegisteredByUsernameAtRegistration = user.Username ?? string.Empty,
            UserId = user.Id,
            UsernameAtRegistration = user.Username
        });
    }

    private static void AddTeamRegistration(TournamentAggregate tournament, Team team)
    {
        tournament.TournamentRegistrations.Add(new TournamentRegistration
        {
            Id = Guid.NewGuid(),
            Tournament = tournament,
            TournamentId = tournament.Id,
            Kind = TournamentRegistrationKind.Team,
            Status = TournamentRegistrationStatus.Active,
            RegisteredByUserId = team.CaptainUserId!.Value,
            RegisteredByUsernameAtRegistration = string.Empty,
            TeamId = team.Id,
            TeamNameAtRegistration = team.Name,
            TeamCaptainUserIdAtRegistration = team.CaptainUserId,
            TeamLogoUrlAtRegistration = team.LogoUrl
        });
    }
}
