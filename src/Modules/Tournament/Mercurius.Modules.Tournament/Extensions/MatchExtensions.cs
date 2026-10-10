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
                    targetMatch.SetIndividualParticipant1(match.UserWinnerId);
                else
                    targetMatch.SetIndividualParticipant2(match.UserWinnerId);
            }
            else if (match.MatchNumber % 2 != 0)
                targetMatch.SetTeamParticipant1(match.TeamWinnerId);
            else
                targetMatch.SetTeamParticipant2(match.TeamWinnerId);
        }

        foreach (var match in matches)
            match.TryAssignByeWin();
    }
}
