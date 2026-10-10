using Mercurius.TestInfrastructure;
using Mercurius.Modules.Tournament.Application.DTOs.Matches;
using Mercurius.Modules.Shared.Exceptions;
using DataAnnotations = System.ComponentModel.DataAnnotations;

namespace Mercurius.Modules.Tournament.Tests;

public class MatchTests
{
    [Fact]
    public void TryAssignByeWin_AssignsUserWinner_WhenOnlyParticipant2Exists()
    {
        var user2 = CreateUser(2);
        var match = new Match().Set(x => x.ParticipationMode, ParticipationMode.Individual);
        match.SetIndividualParticipants(null, user2.Id);
        match.SetParticipantBYEs(true, false);

        match.TryAssignByeWin();

        Assert.Equal(user2.Id, match.UserWinnerId);
        Assert.Null(match.UserLoserId);
    }

    [Fact]
    public void TryAssignByeWin_AssignsTeamWinner_WhenOnlyParticipant1Exists()
    {
        var team1 = CreateTeam(1);
        var match = new Match().Set(x => x.ParticipationMode, ParticipationMode.Team);
        match.SetTeamParticipants(team1.Id, null);
        match.SetParticipantBYEs(false, true);

        match.TryAssignByeWin();

        Assert.Equal(team1.Id, match.TeamWinnerId);
        Assert.Null(match.TeamLoserId);
    }

    [Fact]
    public void UpdateParticipantsNextMatch_PropagatesUserWinnerToUpperBracketSlot1_WhenMatchNumberIsOdd()
    {
        var winner = CreateUser(10);
        var nextMatch = new Match().Set(x => x.ParticipationMode, ParticipationMode.Individual);
        var match = new Match()
            .Set(x => x.ParticipationMode, ParticipationMode.Individual)
            .Set(x => x.MatchNumber, 1)
            .Set(x => x.UserWinnerId, winner.Id)
            .Set(x => x.WinnerNextMatch, nextMatch);

        match.UpdateParticipantsNextMatch();

        Assert.Equal(winner.Id, nextMatch.UserParticipant1Id);
    }

    [Fact]
    public void UpdateParticipantsNextMatch_PropagatesTeamWinnerToLowerBracketSlot2_WhenAvailable()
    {
        var winner = CreateTeam(3);
        var nextMatch = new Match()
            .Set(x => x.ParticipationMode, ParticipationMode.Team)
            .Set(x => x.IsLowerBracketMatch, true);
        var match = new Match()
            .Set(x => x.ParticipationMode, ParticipationMode.Team)
            .Set(x => x.TeamWinnerId, winner.Id)
            .Set(x => x.WinnerNextMatch, nextMatch);

        match.UpdateParticipantsNextMatch();

        Assert.Equal(winner.Id, nextMatch.TeamParticipant2Id);
    }

    [Fact]
    public void UpdateParticipantsNextMatch_PropagatesUserLoserToLowerBracketSlot1_AfterFirstRound()
    {
        var winner = CreateUser(1);
        var loser = CreateUser(2);
        var nextMatch = new Match().Set(x => x.ParticipationMode, ParticipationMode.Individual);
        var match = new Match()
            .Set(x => x.ParticipationMode, ParticipationMode.Individual)
            .Set(x => x.RoundNumber, 2)
            .Set(x => x.MatchNumber, 2)
            .Set(x => x.UserWinnerId, winner.Id)
            .Set(x => x.UserLoserId, loser.Id)
            .Set(x => x.LoserNextMatch, nextMatch);

        match.UpdateParticipantsNextMatch();

        Assert.Equal(loser.Id, nextMatch.UserParticipant1Id);
    }

    [Theory]
    [InlineData((int)GameFormat.BestOf1, 1, 0)]
    [InlineData((int)GameFormat.BestOf3, 2, 1)]
    [InlineData((int)GameFormat.BestOf5, 3, 2)]
    public void SetScoresAndWinner_SetsUserWinnerAndLoser(int format, int participant1Score, int participant2Score)
    {
        var match = CreateIndividualMatch((GameFormat)format);

        match.SetScoresAndWinner(participant1Score, participant2Score);

        Assert.Equal(match.UserParticipant1Id, match.UserWinnerId);
        Assert.Equal(match.UserParticipant2Id, match.UserLoserId);
    }

    [Fact]
    public void SetScoresAndWinner_SetsTeamWinnerAndLoser()
    {
        var match = CreateTeamMatch(GameFormat.BestOf3);

        match.SetScoresAndWinner(1, 2);

        Assert.Equal(match.TeamParticipant2Id, match.TeamWinnerId);
        Assert.Equal(match.TeamParticipant1Id, match.TeamLoserId);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void SetScoresAndWinner_ThrowsValidationException_WhenScoreIsNegative(int participant1Score, int participant2Score)
    {
        var match = CreateIndividualMatch();

        var exception = Assert.Throws<ValidationException>(() => match.SetScoresAndWinner(participant1Score, participant2Score));

        Assert.Equal("Scores cannot be negative", exception.Message);
    }

    [Fact]
    public void SetScoresAndWinner_ThrowsValidationException_WhenScoresAreEqualInBo1()
    {
        var match = CreateIndividualMatch(GameFormat.BestOf1);

        var exception = Assert.Throws<ValidationException>(() => match.SetScoresAndWinner(1, 1));

        Assert.Equal("Scores cannot be equal in Bo1 format", exception.Message);
    }

    [Fact]
    public void SetParticipants_ThrowsValidationException_WhenUsersAreAssignedToTeamMatch()
    {
        var match = new Match().Set(x => x.ParticipationMode, ParticipationMode.Team);

        var exception = Assert.Throws<ValidationException>(() => match.SetIndividualParticipants(CreateUser(1).Id, CreateUser(2).Id));

        Assert.Equal("Match only accepts individual participants.", exception.Message);
    }

    [Fact]
    public void ConfirmEnded_EntersAwaitingScoreOnlyAfterBothSidesConfirm()
    {
        var now = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        var match = CreateIndividualMatch();

        match.ConfirmEnded(1, now);

        Assert.Equal(MatchLifecycleState.AwaitingEndedConfirmation, match.LifecycleState);
        Assert.True(match.Participant1Ended);
        Assert.False(match.Participant2Ended);

        match.ConfirmEnded(2, now);

        Assert.Equal(MatchLifecycleState.AwaitingScore, match.LifecycleState);
    }

    [Fact]
    public void SubmitScore_RequiresConsensus_AndAutoAcceptsFirstReportAtDeadline()
    {
        var now = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        var match = CreateIndividualMatch();
        match.ConfirmEnded(1, now);
        match.ConfirmEnded(2, now);

        match.SubmitScore(1, 1, 0, now);

        Assert.Equal(MatchLifecycleState.ScoreConfirmation, match.LifecycleState);
        Assert.Equal(now.AddMinutes(5), match.ScoreConfirmationDeadlineUtc);

        match.ApplyDeadline(now.AddMinutes(5));

        Assert.Equal(MatchLifecycleState.Completed, match.LifecycleState);
        Assert.Equal(1, match.Participant1Score);
        Assert.Equal(match.UserParticipant1Id, match.UserWinnerId);
    }

    [Fact]
    public void SubmitScore_RejectsDuplicateInitialReportBeforeOpponentResponds()
    {
        var now = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        var match = CreateIndividualMatch();
        match.ConfirmEnded(1, now);
        match.ConfirmEnded(2, now);
        match.SubmitScore(1, 1, 0, now);

        var exception = Assert.Throws<ValidationException>(() => match.SubmitScore(1, 1, 0, now.AddMinutes(1)));

        Assert.Contains("already", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SubmitScore_DifferingReportsEnterCorrectionAndLimitEachSideToOneCorrection()
    {
        var now = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        var match = CreateIndividualMatch(GameFormat.BestOf3);
        match.ConfirmEnded(1, now);
        match.ConfirmEnded(2, now);
        match.SubmitScore(1, 2, 0, now);
        match.SubmitScore(2, 0, 2, now.AddMinutes(1));

        Assert.Equal(MatchLifecycleState.Disputed, match.LifecycleState);
        Assert.Equal(now.AddMinutes(6), match.CorrectionDeadlineUtc);

        match.SubmitScore(1, 1, 2, now.AddMinutes(2));

        var exception = Assert.Throws<ValidationException>(() =>
            match.SubmitScore(1, 2, 0, now.AddMinutes(3)));
        Assert.Contains("correction", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SubmitScore_DifferingReports_ClearInitialDeadlineAndIncrementVersion()
    {
        var now = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        var match = CreateIndividualMatch(GameFormat.BestOf3);
        match.ConfirmEnded(1, now);
        match.ConfirmEnded(2, now);
        match.SubmitScore(1, 2, 0, now);
        var versionBeforeMismatch = match.ResultVersion;

        match.SubmitScore(2, 0, 2, now.AddMinutes(1));

        Assert.Equal(MatchLifecycleState.Disputed, match.LifecycleState);
        Assert.Null(match.ScoreConfirmationDeadlineUtc);
        Assert.Equal(now.AddMinutes(6), match.CorrectionDeadlineUtc);
        Assert.True(match.ResultVersion > versionBeforeMismatch);
    }

    [Fact]
    public void ApplyDeadline_TransitionsUnresolvedCorrectionToAdminResolutionRequiredIdempotently()
    {
        var now = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        var match = CreateIndividualMatch(GameFormat.BestOf3);
        match.ConfirmEnded(1, now);
        match.ConfirmEnded(2, now);
        match.SubmitScore(1, 2, 0, now);
        match.SubmitScore(2, 0, 2, now.AddMinutes(1));
        var deadline = match.CorrectionDeadlineUtc!.Value;

        match.ApplyDeadline(deadline);
        match.ApplyDeadline(deadline.AddMinutes(1));

        Assert.Equal(MatchLifecycleState.AdminResolutionRequired, match.LifecycleState);
        Assert.Null(match.CorrectionDeadlineUtc);
    }

    [Fact]
    public void Forfeit_MarksOpposingParticipantAsWinner()
    {
        var now = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        var match = CreateIndividualMatch();

        match.Forfeit(1, now);

        Assert.Equal(MatchLifecycleState.Forfeited, match.LifecycleState);
        Assert.Equal(match.UserParticipant2Id, match.UserWinnerId);
        Assert.Equal(match.UserParticipant1Id, match.UserLoserId);
        Assert.Equal(MatchResultKind.Forfeit, match.ResultKind);
    }

    [Fact]
    public void Forfeit_RejectsIncompleteAndByeMatches()
    {
        var incompleteMatch = new Match()
            .Set(x => x.ParticipationMode, ParticipationMode.Individual)
            .Set(x => x.Format, GameFormat.BestOf1);
        incompleteMatch.SetIndividualParticipants(CreateUser(1).Id, null);

        var incompleteException = Assert.Throws<ValidationException>(() => incompleteMatch.Forfeit(1, DateTime.UtcNow));

        var byeMatch = CreateIndividualMatch();
        byeMatch.SetParticipantBYEs(true, false);
        var byeException = Assert.Throws<ValidationException>(() => byeMatch.Forfeit(1, DateTime.UtcNow));

        Assert.Contains("two assigned", incompleteException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("non-BYE", byeException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClearParticipantFromSource_PreservesUnrelatedDownstreamAssignment()
    {
        var winner = CreateTeam(1);
        var unrelated = CreateTeam(2);
        var target = new Match()
            .Set(x => x.ParticipationMode, ParticipationMode.Team)
            .Set(x => x.MatchNumber, 1);
        target.SetTeamParticipant2(unrelated.Id);
        var source = new Match()
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.ParticipationMode, ParticipationMode.Team)
            .Set(x => x.MatchNumber, 1)
            .Set(x => x.TeamWinnerId, winner.Id)
            .Set(x => x.WinnerNextMatch, target);

        source.UpdateParticipantsNextMatch();
        target.ClearParticipantFromSource(source.Id);

        Assert.Null(target.TeamParticipant1Id);
        Assert.Equal(unrelated.Id, target.TeamParticipant2Id);
        Assert.Null(target.Participant1SourceMatchId);
    }

    [Fact]
    public void ResolveScore_CompletesAdminResolution()
    {
        var now = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        var match = CreateIndividualMatch(GameFormat.BestOf3);
        match.ConfirmEnded(1, now);
        match.ConfirmEnded(2, now);
        match.SubmitScore(1, 2, 0, now);
        match.SubmitScore(2, 0, 2, now.AddMinutes(1));
        match.ApplyDeadline(match.CorrectionDeadlineUtc!.Value);

        match.ResolveScore(2, 1, now.AddMinutes(7));

        Assert.Equal(MatchLifecycleState.Completed, match.LifecycleState);
        Assert.Equal(MatchResultKind.AdminResolution, match.ResultKind);
        Assert.Equal(match.UserParticipant1Id, match.UserWinnerId);
    }

    [Fact]
    public void UpdateMatchDTO_FailsValidation_WhenScoresAreNegative()
    {
        var dto = new UpdateMatchDTO
        {
            Participant1Score = -1,
            Participant2Score = -2
        };
        var validationContext = new DataAnnotations.ValidationContext(dto);
        var validationResults = new List<DataAnnotations.ValidationResult>();

        var isValid = DataAnnotations.Validator.TryValidateObject(dto, validationContext, validationResults, validateAllProperties: true);

        Assert.False(isValid);
        Assert.Equal(2, validationResults.Count);
    }

    private static Match CreateIndividualMatch(GameFormat format = GameFormat.BestOf1)
    {
        var user1 = CreateUser(1);
        var user2 = CreateUser(2);
        var match = new Match()
            .Set(x => x.ParticipationMode, ParticipationMode.Individual)
            .Set(x => x.Format, format);
        match.SetIndividualParticipants(user1.Id, user2.Id);
        return match;
    }

    private static Match CreateTeamMatch(GameFormat format = GameFormat.BestOf1)
    {
        var team1 = CreateTeam(1);
        var team2 = CreateTeam(2);
        var match = new Match()
            .Set(x => x.ParticipationMode, ParticipationMode.Team)
            .Set(x => x.Format, format);
        match.SetTeamParticipants(team1.Id, team2.Id);
        return match;
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
        var team = new Team($"Team {id}", captain.Id)
        {
            Id = Guid.NewGuid(),
            CaptainUserId = captain.Id
        };
        team.AddMember(captain.Id);
        return team;
    }
}
