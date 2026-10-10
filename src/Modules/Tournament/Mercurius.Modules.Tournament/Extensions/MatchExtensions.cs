using Mercurius.Modules.Tournament.Domain;

namespace Mercurius.Modules.Tournament.Extensions;

internal static class MatchExtensions
{
    public static void AssignByeWinnersNextMatch(this IEnumerable<Match> matches)
    {
        foreach (var match in matches)
        {
            if (match.WinnerNextMatch is null)
                continue;

            var targetMatch = match.WinnerNextMatch;
            if (!match.HasWinner())
                continue;

            if (match.ParticipationMode == ParticipationMode.Individual)
            {
                if (match.MatchNumber % 2 != 0)
                {
                    targetMatch.SetIndividualParticipant1(match.UserWinnerId);
                    targetMatch.Participant1SourceMatchId = match.Id;
                }
                else
                {
                    targetMatch.SetIndividualParticipant2(match.UserWinnerId);
                    targetMatch.Participant2SourceMatchId = match.Id;
                }
            }
            else if (match.MatchNumber % 2 != 0)
            {
                targetMatch.SetTeamParticipant1(match.TeamWinnerId);
                targetMatch.Participant1SourceMatchId = match.Id;
            }
            else
            {
                targetMatch.SetTeamParticipant2(match.TeamWinnerId);
                targetMatch.Participant2SourceMatchId = match.Id;
            }
        }

        foreach (var match in matches)
            match.TryAssignByeWin();
    }
}
