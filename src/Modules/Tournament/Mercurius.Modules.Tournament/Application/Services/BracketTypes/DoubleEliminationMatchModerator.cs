using Mercurius.Modules.Shared.Exceptions;
using Mercurius.Modules.Tournament.Application.Services.Helpers;
using Mercurius.Modules.Tournament.Domain;
using Mercurius.Modules.Tournament.Extensions;

namespace Mercurius.Modules.Tournament.Application.Services.BracketTypes;

/// <summary>
/// Handles the generation and management of matches for a double-elimination tournament.
/// </summary>
internal sealed class DoubleEliminationMatchModerator : IMatchModerator
{
    /// <summary>
    /// Match brackets enforce their completion preconditions while placements are determined.
    /// </summary>
    public void EnsureCanComplete(TournamentAggregate tournament)
    {
    }

    /// <summary>
    /// Generates all matches for a given tournament in a double-elimination format.
    /// </summary>
    /// <param name="tournament">The tournament for which matches are to be generated.</param>
    /// <returns>A collection of matches for the tournament.</returns>
    public IEnumerable<Match> GenerateMatchesForTournament(TournamentAggregate tournament)
    {
        var matches = new List<Match>();
        switch (tournament.ParticipationMode)
        {
            case ParticipationMode.Individual:
                GenerateUpperBracketMatches(
                    tournament,
                    tournament.GetActiveRegisteredUserIds(),
                    matches,
                    (match, participant1, participant2) => match.SetIndividualParticipants(participant1, participant2));
                break;
            case ParticipationMode.Team:
                GenerateUpperBracketMatches(
                    tournament,
                    tournament.GetActiveRegisteredTeamIds(),
                    matches,
                    (match, participant1, participant2) => match.SetTeamParticipants(participant1, participant2));
                break;
            default:
                throw new ValidationException($"Unsupported participation mode {tournament.ParticipationMode}.");
        }

        GenerateLowerBracketMatches(tournament, matches);
        GenerateGrandFinalMatch(tournament, matches);
        int maxRoundNumber = matches.Max(x => x.RoundNumber);
        matches = AssignNextMatchesForDoubleElimination(matches, maxRoundNumber).ToList();
        matches.AssignByeWinnersNextMatch();

        return matches;
    }

    /// <summary>
    /// Generates matches for the upper bracket of the tournament.
    /// </summary>
    /// <param name="tournament">The tournament for which matches are to be generated.</param>
    /// <param name="participants">The participants in the tournament.</param>
    /// <param name="matches">The list to which generated matches will be added.</param>
    /// <param name="assignParticipants">Assigns the selected participants to the created match.</param>
    private void GenerateUpperBracketMatches<TParticipant>(TournamentAggregate tournament, IReadOnlyCollection<TParticipant> participants, List<Match> matches, Action<Match, TParticipant?, TParticipant?> assignParticipants)
        where TParticipant : struct
    {
        int participantCount = participants.Count;
        int nextPowerOfTwo = (int)Math.Pow(2, Math.Ceiling(Math.Log2(participantCount)));
        int totalMatches = nextPowerOfTwo - 1;
        int totalRounds = (int)Math.Ceiling(Math.Log2(participantCount));
        int firstRoundMatchCount = nextPowerOfTwo / 2;

        var slots = AssignParticipantsToSlots(participants, firstRoundMatchCount, nextPowerOfTwo);

        int matchNumber = 1;
        int previousRound = totalRounds + 1;

        for (int i = 0; i < totalMatches; i++)
        {
            int round = CalculateRoundNumber(nextPowerOfTwo, i);

            if (round < previousRound)
                matchNumber = 1;
            else
                matchNumber++;

            var match = CreateMatch(tournament, round, matchNumber);

            if (i >= totalMatches - firstRoundMatchCount)
            {
                AssignParticipantsToMatch(slots, match, i, totalMatches, firstRoundMatchCount, assignParticipants);
            }

            previousRound = round;
            matches.Add(match);
        }
    }

    /// <summary>
    /// Assigns participants to slots for the first round of the tournament.
    /// </summary>
    /// <param name="participants">The participants in the tournament.</param>
    /// <param name="firstRoundMatchCount">The number of matches in the first round.</param>
    /// <param name="nextPowerOfTwo">The next power of two greater than or equal to the number of participants.</param>
    /// <returns>An array of participants assigned to slots.</returns>
    private TParticipant?[] AssignParticipantsToSlots<TParticipant>(IEnumerable<TParticipant> participants, int firstRoundMatchCount, int nextPowerOfTwo)
        where TParticipant : struct
    {
        var shuffled = participants.OrderBy(_ => Guid.NewGuid()).ToList();
        int[] slotOrder = SeedingHelper.GenerateBracketSlotOrder(nextPowerOfTwo);
        var slots = new TParticipant?[firstRoundMatchCount * 2];

        for (int i = 0; i < shuffled.Count; i++)
            slots[slotOrder[i]] = shuffled[i];

        return slots;
    }

    /// <summary>
    /// Calculates the round number for a given match index.
    /// </summary>
    /// <param name="nextPowerOfTwo">The next power of two greater than or equal to the number of participants.</param>
    /// <param name="matchIndex">The index of the match.</param>
    /// <returns>The round number for the match.</returns>
    private int CalculateRoundNumber(int nextPowerOfTwo, int matchIndex)
    {
        return (int)Math.Floor(Math.Log2(nextPowerOfTwo)) - (int)Math.Floor(Math.Log2(matchIndex + 1));
    }

    /// <summary>
    /// Creates a new match for the tournament.
    /// </summary>
    /// <param name="tournament">The tournament for which the match is being created.</param>
    /// <param name="round">The round number of the match.</param>
    /// <param name="matchNumber">The match number within the round.</param>
    /// <returns>A new match object.</returns>
    private Match CreateMatch(TournamentAggregate tournament, int round, int matchNumber)
    {
        return new Match
        {
            TournamentId = tournament.Id,
            RoundNumber = round,
            BracketType = tournament.BracketType,
            Format = tournament.Format,
            MatchNumber = matchNumber,
            ParticipationMode = tournament.ParticipationMode
        };
    }

    /// <summary>
    /// Assigns participants to a match for the first round.
    /// </summary>
    /// <param name="slots">The array of participants assigned to slots.</param>
    /// <param name="match">The match to which participants are being assigned.</param>
    /// <param name="matchIndex">The index of the match.</param>
    /// <param name="totalMatches">The total number of matches in the tournament.</param>
    /// <param name="firstRoundMatchCount">The number of matches in the first round.</param>
    /// <param name="assignParticipants">Assigns the selected participants to the match.</param>
    private void AssignParticipantsToMatch<TParticipant>(TParticipant?[] slots, Match match, int matchIndex, int totalMatches, int firstRoundMatchCount, Action<Match, TParticipant?, TParticipant?> assignParticipants)
        where TParticipant : struct
    {
        int firstRoundStart = totalMatches - firstRoundMatchCount;
        int leafIndex = matchIndex - firstRoundStart;
        assignParticipants(match, slots[leafIndex * 2], slots[leafIndex * 2 + 1]);

        match.SetParticipantBYEs(!match.HasParticipant1(), !match.HasParticipant2());
        match.TryAssignByeWin();
    }

    /// <summary>
    /// Generates matches for the lower bracket of the tournament.
    /// </summary>
    /// <param name="tournament">The tournament for which matches are to be generated.</param>
    /// <param name="matches">The list to which generated matches will be added.</param>
    private void GenerateLowerBracketMatches(TournamentAggregate tournament, List<Match> matches)
    {
        var upperBracketMatches = matches.Where(m => !m.IsLowerBracketMatch).ToList();
        if (!upperBracketMatches.Any())
            return;

        int upperBracketRounds = upperBracketMatches.Max(m => m.RoundNumber);
        int totalLBRounds = (upperBracketRounds - 1) * 2;

        // Dynamically calculate the number of matches for the first round of the lower bracket
        int matchesThisRound = (int)Math.Pow(2, upperBracketRounds - 2);

        for (int round = 1; round <= totalLBRounds; round++)
        {
            for (int i = 0; i < matchesThisRound; i++)
            {
                var match = new Match
                {
                    TournamentId = tournament.Id,
                    RoundNumber = round,
                    MatchNumber = i + 1,
                    Format = tournament.Format,
                    BracketType = tournament.BracketType,
                    ParticipationMode = tournament.ParticipationMode,
                    IsLowerBracketMatch = true
                };

                matches.Add(match);
            }

            // Adjust the match count for the next round.
            // The number of matches halves every two rounds (one entry round and one consolidation round).
            if (round % 2 == 0)
            {
                matchesThisRound /= 2;
            }
        }
    }

    /// <summary>
    /// Generates the grand final match for the tournament.
    /// </summary>
    /// <param name="tournament">The tournament for which the grand final match is being generated.</param>
    /// <param name="matches">The list to which the grand final match will be added.</param>
    private void GenerateGrandFinalMatch(TournamentAggregate tournament, List<Match> matches)
    {
        // Intentionally a single grand final without a bracket reset: if the lower bracket winner wins,
        // it takes the title even though the upper bracket winner has then lost only once.
        var grandFinalMatch = new Match
        {
            TournamentId = tournament.Id,
            RoundNumber = matches.Max(m => m.RoundNumber) + 1,
            MatchNumber = 1,
            Format = tournament.FinalsFormat,
            BracketType = tournament.BracketType,
            ParticipationMode = tournament.ParticipationMode,
            IsLowerBracketMatch = false
        };
        matches.Add(grandFinalMatch);
    }

    /// <summary>
    /// Links matches for a double-elimination tournament.
    /// </summary>
    /// <param name="matches">The list of matches in the tournament.</param>
    /// <param name="maxRoundNumber">The maximum round number in the tournament.</param>
    /// <returns>A collection of matches with linked next matches.</returns>
    private IEnumerable<Match> AssignNextMatchesForDoubleElimination(List<Match> matches, int maxRoundNumber)
    {
        var uBMatches = matches.Where(m => !m.IsLowerBracketMatch && m.RoundNumber < maxRoundNumber).OrderBy(m => m.RoundNumber).ThenBy(m => m.MatchNumber).ToList();
        var lBMatches = matches.Where(m => m.IsLowerBracketMatch).OrderBy(m => m.RoundNumber).ThenBy(m => m.MatchNumber).ToList();
        var grandFinal = matches.Single(m => !m.IsLowerBracketMatch && m.RoundNumber == maxRoundNumber);

        foreach (var currentUBMatch in uBMatches)
        {
            var nextUBMatch = uBMatches.FirstOrDefault(m =>
                m.RoundNumber == currentUBMatch.RoundNumber + 1 &&
                m.MatchNumber == (int)Math.Ceiling((double)currentUBMatch.MatchNumber / 2));

            currentUBMatch.WinnerNextMatch = nextUBMatch;

            // Link UB losers to the correct LB match
            int targetLBRoundNumber = (currentUBMatch.RoundNumber <= 2)
                ? currentUBMatch.RoundNumber
                : (currentUBMatch.RoundNumber - 1) * 2;

            int nextLBMatchNumber = (currentUBMatch.RoundNumber == 1)
                ? (int)Math.Ceiling((double)currentUBMatch.MatchNumber / 2)
                : currentUBMatch.MatchNumber;

            var nextLBMatch = lBMatches.FirstOrDefault(m =>
                m.RoundNumber == targetLBRoundNumber &&
                m.MatchNumber == nextLBMatchNumber);

            currentUBMatch.LoserNextMatch = nextLBMatch;
        }

        foreach (var currentLBMatch in lBMatches)
        {
            int nextLBMatchNumber = (currentLBMatch.RoundNumber % 2 != 0)
                ? currentLBMatch.MatchNumber
                : (int)Math.Ceiling((double)currentLBMatch.MatchNumber / 2);

            var nextLBMatch = lBMatches.FirstOrDefault(m =>
                m.RoundNumber == currentLBMatch.RoundNumber + 1 &&
                m.MatchNumber == nextLBMatchNumber);

            currentLBMatch.WinnerNextMatch = nextLBMatch;
        }

        LinkFinalMatches(uBMatches, lBMatches, grandFinal);

        var linkedMatches = uBMatches.Concat(lBMatches).Append(grandFinal).ToList();
        MarkByes(linkedMatches);
        return linkedMatches;
    }

    /// <summary>
    /// Flags the lower bracket slots that can never be filled because their feeding match yields nobody
    /// (a first-round bye produces no loser), so a lone participant advances automatically
    /// and a match is never decided before its second real participant arrives.
    /// </summary>
    /// <param name="matches">The linked matches, ordered so every feeding match precedes the match it feeds.</param>
    private static void MarkByes(List<Match> matches)
    {
        var feeds = matches
            .SelectMany(match => new[]
            {
                (Source: match, Target: match.WinnerNextMatch, IsLoser: false),
                (Source: match, Target: match.LoserNextMatch, IsLoser: true)
            })
            .Where(feed => feed.Target is not null)
            .ToLookup(feed => feed.Target!);
        var participantCounts = new Dictionary<Match, int>();

        foreach (var match in matches)
        {
            if (!match.IsLowerBracketMatch)
            {
                // Seeding never pairs two byes, so every upper bracket match after the first round is full.
                participantCounts[match] = match.RoundNumber > 1
                    ? 2
                    : (match.HasParticipant1() ? 1 : 0) + (match.HasParticipant2() ? 1 : 0);
                continue;
            }

            var liveFeeds = feeds[match]
                .Where(feed => feed.IsLoser ? participantCounts[feed.Source] == 2 : participantCounts[feed.Source] > 0)
                .ToList();
            participantCounts[match] = liveFeeds.Count;

            if (liveFeeds.Count == 0)
            {
                match.Participant1IsBYE = true;
                match.Participant2IsBYE = true;
            }
            else if (liveFeeds.Count == 1)
            {
                // Mirrors Match.UpdateParticipantsNextMatch: losers take participant 1 (participant 2 from even
                // first-round matches) and a lone lower bracket winner takes the empty participant 2.
                var (source, _, isLoser) = liveFeeds[0];
                var landsInParticipant1 = isLoser && !(source.RoundNumber == 1 && source.MatchNumber % 2 == 0);
                match.SetParticipantBYEs(!landsInParticipant1, landsInParticipant1);
            }
        }
    }

    /// <summary>
    /// Links the final matches in the tournament.
    /// </summary>
    /// <param name="uBMatches">The list of upper bracket matches.</param>
    /// <param name="lBMatches">The list of lower bracket matches.</param>
    /// <param name="grandFinal">The grand final match.</param>
    private void LinkFinalMatches(List<Match> uBMatches, List<Match> lBMatches, Match grandFinal)
    {
        var ubFinal = uBMatches.LastOrDefault(m => m.RoundNumber == uBMatches.Max(ub => ub.RoundNumber));
        var lbFinal = lBMatches.LastOrDefault();

        if (ubFinal != null)
        {
            ubFinal.WinnerNextMatch = grandFinal;
            ubFinal.LoserNextMatch = lbFinal;
        }
        if (lbFinal != null)
        {
            lbFinal.WinnerNextMatch = grandFinal;
        }

        grandFinal.WinnerNextMatch = null;
        grandFinal.LoserNextMatch = null;
    }

    /// <summary>
    /// Determines the placements of participants in the tournament.
    /// </summary>
    /// <param name="tournament">The tournament for which placements are to be determined.</param>
    public void DeterminePlacements(TournamentAggregate tournament)
    {

        var grandFinal = tournament.Matches
            .Where(m => !m.IsLowerBracketMatch)
            .OrderByDescending(m => m.RoundNumber)
            .ThenByDescending(m => m.MatchNumber)
            .FirstOrDefault();

        if (grandFinal is null)
            return;

        switch (tournament.ParticipationMode)
        {
            case ParticipationMode.Individual:
                if (!grandFinal.UserWinnerId.HasValue)
                    throw new ValidationException("Grand final match has no winner assigned. Cannot determine placements.");
                if (!grandFinal.UserLoserId.HasValue)
                    throw new ValidationException("Grand final match has no loser assigned. Cannot determine placements.");

                tournament.Placements.Add(new Placement
                {
                    TournamentId = tournament.Id,
                    Users = [new PlacementUser { UserId = grandFinal.UserWinnerId.Value }],
                    Place = 1
                });
                tournament.Placements.Add(new Placement
                {
                    TournamentId = tournament.Id,
                    Users = [new PlacementUser { UserId = grandFinal.UserLoserId.Value }],
                    Place = 2
                });
                break;
            case ParticipationMode.Team:
                if (!grandFinal.TeamWinnerId.HasValue)
                    throw new ValidationException("Grand final match has no winner assigned. Cannot determine placements.");
                if (!grandFinal.TeamLoserId.HasValue)
                    throw new ValidationException("Grand final match has no loser assigned. Cannot determine placements.");

                tournament.Placements.Add(new Placement
                {
                    TournamentId = tournament.Id,
                    Teams = [new PlacementTeam { TeamId = grandFinal.TeamWinnerId.Value }],
                    Place = 1
                });
                tournament.Placements.Add(new Placement
                {
                    TournamentId = tournament.Id,
                    Teams = [new PlacementTeam { TeamId = grandFinal.TeamLoserId.Value }],
                    Place = 2
                });
                break;
        }

        var lowerBracket = tournament.Matches
            .Where(m => m.IsLowerBracketMatch)
            .OrderByDescending(m => m.RoundNumber)
            .ThenByDescending(m => m.MatchNumber)
            .GroupBy(m => m.RoundNumber);

        int place = 3;
        foreach (var roundGrouping in lowerBracket)
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
                            Place = place,
                            TournamentId = tournament.Id,
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
                            Place = place,
                            TournamentId = tournament.Id,
                            Teams = teamLosers
                        });
                        place += teamLosers.Count;
                    }
                    break;
            }
        }
    }
}

