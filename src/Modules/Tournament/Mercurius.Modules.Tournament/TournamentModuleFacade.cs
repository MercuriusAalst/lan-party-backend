using Mercurius.Modules.Tournament.Application;
using Mercurius.Modules.Tournament.Application.Services;
using Mercurius.Modules.Tournament.Contracts;
using Mercurius.Modules.Tournament.Domain;
using Mercurius.Modules.Tournament.Infrastructure;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Teams.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL;
using TournamentConfigurationContract = Mercurius.Modules.Tournament.Contracts.TournamentConfiguration;

namespace Mercurius.Modules.Tournament;

internal sealed class TournamentModuleFacade : ITournamentModule
{
    private readonly ITournamentDbContext _dbContext;
    private readonly ITeamsModule _teamsModule;
    private readonly TournamentEligibilityEvaluator _eligibilityEvaluator;
    private readonly PublicProfileMatchSummaryReadService _publicProfileMatchSummaryReadService;

    public TournamentModuleFacade(
        ITournamentDbContext dbContext,
        ITeamsModule teamsModule,
        TournamentEligibilityEvaluator eligibilityEvaluator,
        PublicProfileMatchSummaryReadService publicProfileMatchSummaryReadService)
    {
        _dbContext = dbContext;
        _teamsModule = teamsModule;
        _eligibilityEvaluator = eligibilityEvaluator;
        _publicProfileMatchSummaryReadService = publicProfileMatchSummaryReadService;
    }

    public Task<PublicProfileMatchSummarySet> GetPublicUserMatchSummariesAsync(
        UserId userId,
        CancellationToken cancellationToken = default) =>
        _publicProfileMatchSummaryReadService.GetPublicUserMatchSummariesAsync(userId, cancellationToken);

    public Task<PublicProfileMatchSummarySet> GetPublicTeamMatchSummariesAsync(
        TeamId teamId,
        CancellationToken cancellationToken = default) =>
        _publicProfileMatchSummaryReadService.GetPublicTeamMatchSummariesAsync(teamId, cancellationToken);

    public Task<TournamentSummary?> GetTournamentSummaryAsync(
        TournamentId tournamentId,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.Tournaments
            .AsNoTracking()
            .Where(tournament => tournament.Id == tournamentId.Value)
            .Select(tournament => new TournamentSummary(
                new TournamentId(tournament.Id),
                tournament.Name,
                (Contracts.TournamentStatus)tournament.Status,
                (Contracts.ParticipationMode)tournament.ParticipationMode,
                tournament.TeamSize,
                tournament.PlannedStartTime,
                tournament.ImageUrl))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public Task<TournamentConfigurationContract?> GetTournamentConfigurationAsync(
        TournamentId tournamentId,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.Tournaments
            .AsNoTracking()
            .Where(tournament => tournament.Id == tournamentId.Value)
            .Select(tournament => new TournamentConfigurationContract(
                new TournamentId(tournament.Id),
                (Contracts.BracketType)tournament.BracketType,
                (Contracts.GameFormat)tournament.Format,
                (Contracts.GameFormat)tournament.FinalsFormat,
                (Contracts.ParticipationMode)tournament.ParticipationMode,
                tournament.TeamSize,
                tournament.PlannedStartTime,
                tournament.AverageGameDurationMinutes,
                tournament.RoundBreakDurationMinutes))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public Task<bool> IsRegistrationOpenAsync(
        TournamentId tournamentId,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.Tournaments
            .AsNoTracking()
            .AnyAsync(
                tournament => tournament.Id == tournamentId.Value && tournament.Status == Domain.TournamentStatus.Scheduled,
                cancellationToken);
    }

    public async Task<RegistrationEligibility> CheckIndividualRegistrationEligibilityAsync(
        TournamentId tournamentId,
        UserId userId,
        CancellationToken cancellationToken = default)
    {
        var tournament = await _dbContext.Tournaments
            .AsNoTracking()
            .Where(candidate => candidate.Id == tournamentId.Value)
            .Select(candidate => new { candidate.Status, candidate.ParticipationMode })
            .SingleOrDefaultAsync(cancellationToken);
        if (tournament is null)
            return new RegistrationEligibility(false, ["tournament_not_found"]);

        var reasons = await _eligibilityEvaluator.GetIndividualTournamentFailuresAsync(
            TournamentAggregate.ForEligibilityCheck(
                tournamentId.Value,
                tournament.ParticipationMode,
                tournament.Status),
            userId.Value,
            null,
            cancellationToken);
        return new RegistrationEligibility(reasons.Count == 0, reasons);
    }

    public async Task<RegistrationEligibility> CheckTeamRegistrationEligibilityAsync(
        TournamentId tournamentId,
        TeamId teamId,
        UserId requestedBy,
        CancellationToken cancellationToken = default)
    {
        var tournament = await _dbContext.Tournaments
            .AsNoTracking()
            .Where(candidate => candidate.Id == tournamentId.Value)
            .Select(candidate => new
            {
                candidate.Status,
                candidate.ParticipationMode,
                candidate.TeamSize
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (tournament is null)
            return new RegistrationEligibility(false, ["tournament_not_found"]);

        var teamEligibility = await _teamsModule.GetRegistrationEligibilityAsync(
            teamId,
            requestedBy,
            tournamentId,
            cancellationToken);
        var reasons = teamEligibility.ReasonCodes.ToList();
        reasons.AddRange(await _eligibilityEvaluator.GetTeamTournamentFailuresAsync(
            TournamentAggregate.ForEligibilityCheck(
                tournamentId.Value,
                tournament.ParticipationMode,
                tournament.Status,
                tournament.TeamSize),
            teamId.Value,
            requestedBy.Value,
            null,
            cancellationToken));

        return new RegistrationEligibility(reasons.Count == 0, reasons.Distinct().ToList());
    }

    public async Task<IReadOnlyList<TournamentSummary>> SearchTournamentsAsync(
        string normalizedQuery,
        TournamentSearchCursor? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, 500);
        var query = BuildTournamentSearchQuery(normalizedQuery);

        if (cursor is not null)
        {
            query = query.Where(candidate =>
                candidate.RelevanceRank > cursor.RelevanceRank ||
                (candidate.RelevanceRank == cursor.RelevanceRank &&
                 string.Compare(candidate.NormalizedLabel, cursor.NormalizedLabel) > 0) ||
                (candidate.RelevanceRank == cursor.RelevanceRank &&
                 candidate.NormalizedLabel == cursor.NormalizedLabel &&
                 2 > cursor.TypeOrder) ||
                (candidate.RelevanceRank == cursor.RelevanceRank &&
                 candidate.NormalizedLabel == cursor.NormalizedLabel &&
                 cursor.TypeOrder == 2 &&
                 candidate.Id.CompareTo(cursor.StableId) > 0));
        }

        return await query
            .OrderBy(candidate => candidate.RelevanceRank)
            .ThenBy(candidate => candidate.NormalizedLabel)
            .ThenBy(candidate => candidate.Id)
            .Take(boundedLimit)
            .Select(candidate => new TournamentSummary(
                new TournamentId(candidate.Id),
                candidate.Name,
                (Contracts.TournamentStatus)candidate.Status,
                (Contracts.ParticipationMode)candidate.ParticipationMode,
                candidate.TeamSize,
                candidate.PlannedStartTime,
                candidate.ImageUrl))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TournamentSearchDocument>> GetTournamentSearchDocumentsPageAsync(
        TournamentId? afterId,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var afterValue = afterId?.Value;
        return await _dbContext.Tournaments
            .AsNoTracking()
            .Where(tournament => !afterValue.HasValue || tournament.Id > afterValue.Value)
            .OrderBy(tournament => tournament.Id)
            .Select(tournament => new TournamentSearchDocument(
                new TournamentId(tournament.Id),
                tournament.Name,
                tournament.ImageUrl))
            .Take(Math.Clamp(pageSize, 1, 1000))
            .ToListAsync(cancellationToken);
    }

    private IQueryable<TournamentSearchCandidate> BuildTournamentSearchQuery(string normalizedQuery)
    {
        var escapedQuery = EscapeLikePattern(normalizedQuery);
        var exactPattern = escapedQuery;
        var containsPattern = $"%{escapedQuery}%";
        var prefixPattern = $"{escapedQuery}%";

        return _dbContext.Tournaments
            .AsNoTracking()
            .Where(tournament => EF.Functions.Like(tournament.Name.ToLower(), containsPattern, "\\"))
            .Select(tournament => new TournamentSearchCandidate(
                tournament.Id,
                tournament.Name,
                tournament.Status,
                tournament.ParticipationMode,
                tournament.TeamSize,
                tournament.PlannedStartTime,
                tournament.ImageUrl,
                tournament.Name.ToLower(),
                EF.Functions.Like(tournament.Name.ToLower(), exactPattern, "\\")
                    ? 0
                    : EF.Functions.Like(tournament.Name.ToLower(), prefixPattern, "\\")
                        ? 1
                        : 2));
    }

    private sealed record TournamentSearchCandidate(
        Guid Id,
        string Name,
        Domain.TournamentStatus Status,
        Domain.ParticipationMode ParticipationMode,
        int? TeamSize,
        DateTime PlannedStartTime,
        string? ImageUrl,
        string NormalizedLabel,
        int RelevanceRank);

    private static string EscapeLikePattern(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }
}
