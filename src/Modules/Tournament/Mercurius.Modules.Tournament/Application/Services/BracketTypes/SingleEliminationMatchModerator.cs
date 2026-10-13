using Mercurius.Modules.Shared.Exceptions;
using Mercurius.Modules.Tournament.Application.Services.Helpers;
using Mercurius.Modules.Tournament.Domain;
using Mercurius.Modules.Tournament.Extensions;

namespace Mercurius.Modules.Tournament.Application.Services.BracketTypes;

/// <summary>
/// Handles the generation and management of matches for a single-elimination tournament.
/// </summary>
internal sealed class SingleEliminationMatchModerator : IMatchModerator
{
    /// <summary>
    /// Match brackets enforce their completion preconditions while placements are determined.
    /// </summary>
    public void EnsureCanComplete(TournamentAggregate tournament)
    {
    }

    /// <summary>
    /// Determines the placements of participants in the tournament.
    /// </summary>
    /// <param name="tournament">The tournament for which placements are to be determined.</param>
    public void DeterminePlacements(TournamentAggregate tournament)
    {
        if (tournament.Matches.Count == 0)
            return;

        // Assign 1st place to the winner of the final match
        var finalMatch = tournament.Matches
            .OrderByDescending(m => m.RoundNumber)
            .ThenByDescending(m => m.MatchNumber)
            .FirstOrDefault();

        if (finalMatch is null)
            return;

        switch (tournament.ParticipationMode)
        {
            case ParticipationMode.Individual:
                if (!finalMatch.UserWinnerId.HasValue)
                    throw new ValidationException("Final match has no winner assigned.");

                tournament.Placements.Add(new Placement
                {
                    TournamentId = tournament.Id,
                    Place = 1,
                    Users = [new PlacementUser { UserId = finalMatch.UserWinnerId.Value }]
                });
                break;
            case ParticipationMode.Team:
                if (!finalMatch.TeamWinnerId.HasValue)
                    throw new ValidationException("Final match has no winner assigned.");

                tournament.Placements.Add(new Placement
                {
                    TournamentId = tournament.Id,
                    Place = 1,
                    Teams = [new PlacementTeam { TeamId = finalMatch.TeamWinnerId.Value }]
                });
                break;
        }

        var matchesOrderedAndGroupedByRound = tournament.Matches
            .OrderByDescending(m => m.RoundNumber)
            .ThenByDescending(m => m.MatchNumber)
            .GroupBy(m => m.RoundNumber);

        int place = 2;

        foreach (var roundGrouping in matchesOrderedAndGroupedByRound)
        {
            switch (tournament.ParticipationMode)
            {
                case ParticipationMode.Individual:
                    var userLosers = roundGrouping
                        .Where(match => match.UserLoserId.HasValue)
                        .Select(match => new PlacementUser { UserId = match.UserLoserId!.Value })
                        .ToList();
                    if (userLosers.Any())
                    {
                        tournament.Placements.Add(new Placement
                        {
                            TournamentId = tournament.Id,
                            Place = place,
                            Users = userLosers
                        });
                        place += userLosers.Count;
                    }
                    break;
                case ParticipationMode.Team:
                    var teamLosers = roundGrouping
                        .Where(match => match.TeamLoserId.HasValue)
                        .Select(match => new PlacementTeam { TeamId = match.TeamLoserId!.Value })
                        .ToList();
                    if (teamLosers.Any())
                    {
                        tournament.Placements.Add(new Placement
                        {
                            TournamentId = tournament.Id,
                            Place = place,
                            Teams = teamLosers
                        });
                        place += teamLosers.Count;
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Generates all matches for a given tournament in a single-elimination format.
    /// </summary>
    /// <param name="tournament">The tournament for which matches are to be generated.</param>
    /// <returns>A collection of matches for the tournament.</returns>
    public IEnumerable<Match> GenerateMatchesForTournament(TournamentAggregate tournament)
    {
        return tournament.ParticipationMode switch
        {
            ParticipationMode.Individual => GenerateMatchesForTournament(
                tournament,
                tournament.GetActiveRegisteredUserIds().OrderBy(_ => Guid.NewGuid()).ToList(),
                (match, participant1, participant2) => match.SetIndividualParticipants(participant1, participant2)),
            ParticipationMode.Team => GenerateMatchesForTournament(
                tournament,
                tournament.GetActiveRegisteredTeamIds().OrderBy(_ => Guid.NewGuid()).ToList(),
                (match, participant1, participant2) => match.SetTeamParticipants(participant1, participant2)),
            _ => throw new ValidationException($"Unsupported participation mode {tournament.ParticipationMode}.")
        };
    }

    private IEnumerable<Match> GenerateMatchesForTournament<TParticipant>(TournamentAggregate tournament, IReadOnlyList<TParticipant> participants, Action<Match, TParticipant?, TParticipant?> assignParticipants)
        where TParticipant : struct
    {
        var matches = new List<Match>();
        int participantCount = participants.Count;
        int nextPowerOfTwo = (int)Math.Pow(2, Math.Ceiling(Math.Log2(participantCount)));
        int totalMatches = nextPowerOfTwo - 1;
        int totalRounds = (int)Math.Ceiling(Math.Log2(participantCount));
        int firstRoundMatchCount = nextPowerOfTwo / 2;

        int[] slotOrder = SeedingHelper.GenerateBracketSlotOrder(nextPowerOfTwo);
        var slots = new TParticipant?[firstRoundMatchCount * 2];
        for (int i = 0; i < participants.Count; i++)
            slots[slotOrder[i]] = participants[i];

        int matchNumber = 1;
        int previousRound = totalRounds + 1;

        for (int i = 0; i < totalMatches; i++)
        {
            int round = (int)Math.Floor(Math.Log2(nextPowerOfTwo)) - (int)Math.Floor(Math.Log2(i + 1));

            if (round < previousRound)
                matchNumber = 1;
            else
                matchNumber++;

            var match = new Match(
                tournament.Id,
                round,
                matchNumber,
                BracketType.SingleElimination,
                round == totalRounds ? tournament.FinalsFormat : tournament.Format,
                tournament.ParticipationMode);

            if (i >= totalMatches - firstRoundMatchCount)
            {
                int leafIndex = i - (totalMatches - firstRoundMatchCount);
                assignParticipants(match, slots[leafIndex * 2], slots[leafIndex * 2 + 1]);
                match.SetParticipantBYEs(!match.HasParticipant1(), !match.HasParticipant2());
                match.TryAssignByeWin();
            }

            previousRound = round;
            matches.Add(match);
        }

        for (int i = 0; i < matches.Count; i++)
        {
            var current = matches[i];

            if (current.RoundNumber == 1)
                continue;

            int childMatchIndex1 = (i * 2) + 1;
            int childMatchIndex2 = (i * 2) + 2;

            if (childMatchIndex1 < matches.Count)
                matches[childMatchIndex1].SetWinnerNextMatch(current);
            if (childMatchIndex2 < matches.Count)
                matches[childMatchIndex2].SetWinnerNextMatch(current);
        }

        matches.AssignByeWinnersNextMatch();

        return matches;
    }
}

