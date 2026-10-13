using Mercurius.Modules.Tournament.Domain;
using Mercurius.Modules.Tournament.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mercurius.Modules.Tournament.Application.Services;

internal sealed class MatchBracketImpactAnalyzer
{
    private const int MaxDownstreamMatches = 512;
    private readonly ITournamentDbContext _dbContext;

    public MatchBracketImpactAnalyzer(ITournamentDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Analysis> AnalyzeAsync(
        Match source,
        CancellationToken cancellationToken)
    {
        if (!await LoadDownstreamMatchesAsync(source, cancellationToken))
            return new Analysis(true, false, null, []);

        var downstream = GetDownstreamMatches(source);
        var blockingMatch = downstream.FirstOrDefault(HasPlayedResult);
        var hasUnprovenancedAssignment = HasUnprovenancedDownstreamAssignment(source, downstream);
        return new Analysis(
            false,
            blockingMatch is null && !hasUnprovenancedAssignment,
            blockingMatch,
            downstream);
    }

    public void ClearDownstreamAssignments(Match source, Analysis analysis)
    {
        foreach (var downstreamMatch in analysis.DownstreamMatches)
            downstreamMatch.ClearParticipantFromSource(source.Id);
    }

    private async Task<bool> LoadDownstreamMatchesAsync(
        Match source,
        CancellationToken cancellationToken)
    {
        var discovered = new HashSet<Guid> { source.Id };
        var pending = new Queue<(Match Parent, Guid ChildId, bool IsWinnerLink)>();
        if (!EnqueueDownstreamLinks(source, pending, discovered))
            return false;

        while (pending.Count != 0)
        {
            var batch = new List<(Match Parent, Guid ChildId, bool IsWinnerLink)>();
            while (pending.Count != 0)
                batch.Add(pending.Dequeue());

            var childIds = batch
                .Select(link => link.ChildId)
                .Distinct()
                .ToArray();
            var children = await _dbContext.Matches
                .Where(candidate => childIds.Contains(candidate.Id))
                .ToListAsync(cancellationToken);
            var childrenById = children.ToDictionary(candidate => candidate.Id);
            foreach (var link in batch)
            {
                if (!childrenById.TryGetValue(link.ChildId, out var child))
                    return false;
                if (child.TournamentId != source.TournamentId)
                    return false;

                if (link.IsWinnerLink)
                    link.Parent.SetWinnerNextMatch(child);
                else
                    link.Parent.SetLoserNextMatch(child);

                if (!EnqueueDownstreamLinks(child, pending, discovered))
                    return false;
            }
        }

        return true;
    }

    private static bool EnqueueDownstreamLinks(
        Match match,
        Queue<(Match Parent, Guid ChildId, bool IsWinnerLink)> pending,
        HashSet<Guid> discovered)
    {
        if (match.WinnerNextMatchId.HasValue && discovered.Add(match.WinnerNextMatchId.Value))
        {
            if (discovered.Count > MaxDownstreamMatches + 1)
                return false;
            pending.Enqueue((match, match.WinnerNextMatchId.Value, true));
        }
        if (match.LoserNextMatchId.HasValue && discovered.Add(match.LoserNextMatchId.Value))
        {
            if (discovered.Count > MaxDownstreamMatches + 1)
                return false;
            pending.Enqueue((match, match.LoserNextMatchId.Value, false));
        }

        return true;
    }

    private static IReadOnlyList<Match> GetDownstreamMatches(Match source)
    {
        var seen = new HashSet<Guid> { source.Id };
        var queue = new Queue<Match>();
        if (source.WinnerNextMatch is not null)
            queue.Enqueue(source.WinnerNextMatch);
        if (source.LoserNextMatch is not null)
            queue.Enqueue(source.LoserNextMatch);

        var result = new List<Match>();
        while (queue.Count != 0 && result.Count < MaxDownstreamMatches)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current.Id))
                continue;
            result.Add(current);
            if (current.WinnerNextMatch is not null)
                queue.Enqueue(current.WinnerNextMatch);
            if (current.LoserNextMatch is not null)
                queue.Enqueue(current.LoserNextMatch);
        }
        return result;
    }

    private static bool HasPlayedResult(Match match) =>
        match.HasResult ||
        HasActuallyStartedOrFinished(match) ||
        match.Participant1Ended ||
        match.Participant2Ended ||
        match.Participant1ReportedScore1.HasValue ||
        match.Participant2ReportedScore1.HasValue ||
        match.Participant1Score.HasValue ||
        match.Participant2Score.HasValue ||
        match.HasWinner() ||
        match.GetLoserForMutation().HasValue ||
        match.ForfeitedParticipantNumber.HasValue;

    private static bool HasActuallyStartedOrFinished(Match match) =>
        match.StartTime != default || match.EndTime != default;

    private static bool HasUnprovenancedDownstreamAssignment(
        Match source,
        IReadOnlyList<Match> downstream)
    {
        var sourceParticipants = new[]
        {
            source.GetWinnerId(),
            source.GetLoserForMutation()
        };

        if (sourceParticipants.Any(participantId => !participantId.HasValue))
        {
            return downstream.Any(match =>
                (match.GetParticipant1Id().HasValue && match.Participant1SourceMatchId != source.Id) ||
                (match.GetParticipant2Id().HasValue && match.Participant2SourceMatchId != source.Id));
        }

        return downstream.Any(match => sourceParticipants.Any(participantId =>
            participantId.HasValue &&
            ((match.GetParticipant1Id() == participantId && match.Participant1SourceMatchId != source.Id) ||
             (match.GetParticipant2Id() == participantId && match.Participant2SourceMatchId != source.Id))));
    }

    internal sealed record Analysis(
        bool IsGraphTooLarge,
        bool CanReverse,
        Match? BlockingMatch,
        IReadOnlyList<Match> DownstreamMatches);
}
