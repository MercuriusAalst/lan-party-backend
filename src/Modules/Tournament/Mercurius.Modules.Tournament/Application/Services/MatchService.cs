using Mercurius.Modules.Tournament.Application.DTOs.Matches;
using Mercurius.Modules.Tournament.Domain;
using Mercurius.Modules.Tournament.Infrastructure;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Shared.Exceptions;
using Mercurius.Modules.Teams.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Platform.Eventing;
using MatchResolutionRequiredIntegrationEvent = Mercurius.Modules.Tournament.Contracts.MatchResolutionRequiredIntegrationEvent;
using MatchParticipantSide = Mercurius.Modules.Tournament.Contracts.MatchParticipantSide;

namespace Mercurius.Modules.Tournament.Application.Services;

internal sealed class MatchService : IMatchService
{
    private const string ReversalBlockedReason = "match_reversal_blocked";
    private const string DownstreamGraphTooLargeReason = "downstream_graph_too_large";
    private readonly ITournamentDbContext _dbContext;
    private readonly IIdentityModule _identityModule;
    private readonly ITeamsModule _teamsModule;
    private readonly IModuleEventPublisher _moduleEventPublisher;
    private readonly TimeProvider _timeProvider;
    private readonly MatchBracketImpactAnalyzer _bracketImpactAnalyzer;

    public MatchService(
        ITournamentDbContext dbContext,
        IIdentityModule identityModule,
        ITeamsModule teamsModule,
        IModuleEventPublisher moduleEventPublisher,
        TimeProvider timeProvider,
        MatchBracketImpactAnalyzer bracketImpactAnalyzer)
    {
        _dbContext = dbContext;
        _identityModule = identityModule;
        _teamsModule = teamsModule;
        _moduleEventPublisher = moduleEventPublisher;
        _timeProvider = timeProvider;
        _bracketImpactAnalyzer = bracketImpactAnalyzer;
    }

    public async Task<GetMatchDTO> GetMatchByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var (_, match) = await GetMatchReadAsync(id, cancellationToken);
        return TournamentDtoMapper.ToGetMatchDto(match);
    }

    public async Task<OpponentUserProfileDTO> GetOpponentUserProfileAsync(
        Guid id,
        string auth0UserId,
        CancellationToken cancellationToken = default)
    {
        var userId = await GetCurrentUserIdAsync(auth0UserId, cancellationToken);
        var (_, match) = await GetMatchReadAsync(id, cancellationToken);
        var side = await FindParticipantSideAsync(match, userId, cancellationToken);
        if (!side.HasValue)
            throw new ForbiddenException("match_participant_required", "Only participants and team captains can read opponent details.");

        var opponentUserId = await GetOpponentUserIdAsync(match, side.Value, cancellationToken);
        if (!opponentUserId.HasValue || opponentUserId.Value == userId)
            throw new NotFoundException("Match opponent profile not found.");

        var profile = await _identityModule.GetPublicProfileByIdAsync(new UserId(opponentUserId.Value), cancellationToken);
        if (profile is null)
            throw new NotFoundException("Match opponent profile not found.");

        return new OpponentUserProfileDTO(
            profile.Username,
            profile.Firstname,
            profile.Lastname,
            profile.DiscordId,
            profile.SteamId,
            profile.RiotId);
    }

    public async Task<GetMatchActionStateDTO> GetMatchActionStateAsync(
        Guid id,
        string auth0UserId,
        bool isAdmin,
        CancellationToken cancellationToken = default)
    {
        var userId = await GetCurrentUserIdAsync(auth0UserId, cancellationToken);
        var (tournament, match) = await GetMatchReadAsync(id, cancellationToken);
        var side = await FindParticipantSideAsync(match, userId, cancellationToken);
        var tournamentInProgress = tournament.Status == TournamentStatus.InProgress;
        var canViewPrivateReports = isAdmin &&
            (!tournament.AssignedAdminUserId.HasValue || tournament.AssignedAdminUserId.Value == userId);
        var canConfirmEnded = tournamentInProgress && CanConfirmEnded(match, side);
        var canSubmitScore = tournamentInProgress && CanSubmitScore(match, side);
        var canForfeit = tournamentInProgress && CanForfeit(match, side);
        var canResolve = isAdmin &&
            tournamentInProgress &&
            match.LifecycleState is MatchLifecycleState.Disputed or MatchLifecycleState.AdminResolutionRequired;
        var resolveBlockedReason = !isAdmin
            ? "admin_required"
            : !tournamentInProgress
                ? "tournament_not_in_progress"
                : "match_not_disputed";
        var hasBothParticipants = match.GetParticipant1Id().HasValue && match.GetParticipant2Id().HasValue;
        var canForceForfeit = isAdmin &&
            tournamentInProgress &&
            hasBothParticipants &&
            !match.Participant1IsBYE &&
            !match.Participant2IsBYE &&
            !match.HasResult &&
            match.LifecycleState != MatchLifecycleState.AdminResolutionRequired;
        var forceForfeitBlockedReason = !isAdmin
            ? "admin_required"
            : !tournamentInProgress
                ? "tournament_not_in_progress"
                : match.HasResult
                    ? "match_already_completed"
                    : !hasBothParticipants
                        ? "match_not_ready"
                    : match.Participant1IsBYE || match.Participant2IsBYE
                        ? "match_not_forfeitable"
                        : "match_requires_admin_resolution";
        var canReverse = false;
        var reverseBlockedReason = !isAdmin
            ? "admin_required"
            : !tournamentInProgress
                ? "tournament_not_in_progress"
                : !match.HasResult
                    ? "match_not_completed"
                    : ReversalBlockedReason;
        if (isAdmin && tournamentInProgress && match.HasResult)
        {
            var reversalAnalysis = await _bracketImpactAnalyzer.AnalyzeAsync(match, cancellationToken);
            if (reversalAnalysis.IsGraphTooLarge)
            {
                reverseBlockedReason = DownstreamGraphTooLargeReason;
            }
            else
            {
                canReverse = reversalAnalysis.CanReverse;
                reverseBlockedReason = canReverse ? null : ReversalBlockedReason;
            }
        }
        return TournamentDtoMapper.ToGetMatchActionStateDto(
            match,
            side,
            canViewPrivateReports || side.HasValue,
            canConfirmEnded,
            canSubmitScore,
            canForfeit,
            canResolve,
            canResolve ? null : resolveBlockedReason,
            canForceForfeit,
            canForceForfeit ? null : forceForfeitBlockedReason,
            canReverse,
            canReverse ? null : reverseBlockedReason);
    }

    public async Task<GetMatchDTO> ConfirmEndedAsync(
        Guid id,
        string auth0UserId,
        CancellationToken cancellationToken = default)
    {
        var userId = await GetCurrentUserIdAsync(auth0UserId, cancellationToken);
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var (tournament, match) = await GetMatchMutationGraphAsync(id, cancellationToken);
        EnsureInProgress(tournament);
        var side = await RequireParticipantSideAsync(match, userId, cancellationToken);
        var now = UtcNow();
        ApplyDeadline(tournament, match, now);
        match.ConfirmEnded((int)side, now);
        await SaveAndCommitAsync(transaction, tournament, cancellationToken);
        return TournamentDtoMapper.ToGetMatchDto(match);
    }

    public async Task<GetMatchDTO> SubmitScoreAsync(
        Guid id,
        string auth0UserId,
        SubmitMatchScoreDTO request,
        CancellationToken cancellationToken = default)
    {
        var userId = await GetCurrentUserIdAsync(auth0UserId, cancellationToken);
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var (tournament, match) = await GetMatchMutationGraphAsync(id, cancellationToken);
        EnsureInProgress(tournament);
        var side = await RequireParticipantSideAsync(match, userId, cancellationToken);
        var wasResult = match.HasResult;
        var now = UtcNow();
        ApplyDeadline(tournament, match, now);
        match.SubmitScore((int)side, request.Participant1Score, request.Participant2Score, now);
        if (!wasResult && match.HasResult)
        {
            match.ResultRecordedByUserId = userId;
        }

        await SaveAndCommitAsync(transaction, tournament, cancellationToken);
        return TournamentDtoMapper.ToGetMatchDto(match);
    }

    public async Task<GetMatchDTO> ForfeitAsync(
        Guid id,
        string auth0UserId,
        ForfeitMatchDTO request,
        bool isAdmin,
        CancellationToken cancellationToken = default)
    {
        var userId = await GetCurrentUserIdAsync(auth0UserId, cancellationToken);
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var (tournament, match) = await GetMatchMutationGraphAsync(id, cancellationToken);
        EnsureInProgress(tournament);

        var actorSide = await FindParticipantSideAsync(match, userId, cancellationToken);
        var targetSide = request.Participant;
        if (!isAdmin)
        {
            if (!actorSide.HasValue)
                throw new ForbiddenException("match_participant_required", "Only a participant or team captain can forfeit this match.");
            if (targetSide.HasValue && targetSide != actorSide)
                throw new ForbiddenException("match_own_side_required", "Participants may only forfeit their own side.");
            targetSide = actorSide;
        }
        else if (!targetSide.HasValue)
        {
            if (!actorSide.HasValue)
                throw new ValidationException("An administrator must select a participant side to forfeit.");
            targetSide = actorSide;
        }

        var wasResult = match.HasResult;
        var now = UtcNow();
        ApplyDeadline(tournament, match, now);
        match.Forfeit((int)targetSide.Value, now);
        if (!wasResult)
        {
            match.ResultRecordedByUserId = userId;
        }

        await SaveAndCommitAsync(transaction, tournament, cancellationToken);
        return TournamentDtoMapper.ToGetMatchDto(match);
    }

    public async Task<GetMatchDTO> ResolveAsync(
        Guid id,
        string auth0UserId,
        ResolveMatchDTO request,
        CancellationToken cancellationToken = default)
    {
        var userId = await GetCurrentUserIdAsync(auth0UserId, cancellationToken);
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var (tournament, match) = await GetMatchMutationGraphAsync(id, cancellationToken);
        EnsureInProgress(tournament);
        var now = UtcNow();
        ApplyDeadline(tournament, match, now);
        match.ResolveScore(request.Participant1Score, request.Participant2Score, now);
        match.ResultRecordedByUserId = userId;
        await SaveAndCommitAsync(transaction, tournament, cancellationToken);
        return TournamentDtoMapper.ToGetMatchDto(match);
    }

    public async Task<GetMatchDTO> ReverseAsync(
        Guid id,
        string auth0UserId,
        CancellationToken cancellationToken = default)
    {
        var userId = await GetCurrentUserIdAsync(auth0UserId, cancellationToken);
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var (tournament, match) = await GetMatchMutationGraphAsync(id, cancellationToken);
        var reversalAnalysis = await _bracketImpactAnalyzer.AnalyzeAsync(match, cancellationToken);
        if (reversalAnalysis.IsGraphTooLarge)
            throw new ConflictException(
                DownstreamGraphTooLargeReason,
                "This result cannot be reversed because the linked bracket is too large to evaluate safely.");
        EnsureInProgress(tournament);
        if (reversalAnalysis.BlockingMatch is not null)
            throw new ConflictException(
                ReversalBlockedReason,
                $"This result cannot be reversed because linked match {reversalAnalysis.BlockingMatch.Id} already has a result.");
        if (!reversalAnalysis.CanReverse)
            throw new ConflictException(
                ReversalBlockedReason,
                "This result cannot be reversed because a linked participant assignment cannot be proven to originate from it.");

        _bracketImpactAnalyzer.ClearDownstreamAssignments(match, reversalAnalysis);
        match.ReverseResult(UtcNow());
        match.ResultRecordedByUserId = userId;
        await SaveAndCommitAsync(transaction, tournament, cancellationToken);
        return TournamentDtoMapper.ToGetMatchDto(match);
    }

    public async Task<GetMatchDTO> UpdateMatchAsync(
        Guid id,
        string auth0UserId,
        UpdateMatchDTO updateMatchDTO,
        CancellationToken cancellationToken = default)
    {
        var userId = await GetCurrentUserIdAsync(auth0UserId, cancellationToken);
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var (tournament, match) = await GetMatchMutationGraphAsync(id, cancellationToken);
        EnsureInProgress(tournament);
        var now = UtcNow();
        ApplyDeadline(tournament, match, now);
        match.ResolveScore(updateMatchDTO.Participant1Score, updateMatchDTO.Participant2Score, now);
        match.ResultRecordedByUserId = userId;
        await SaveAndCommitAsync(transaction, tournament, cancellationToken);
        return TournamentDtoMapper.ToGetMatchDto(match);
    }

    private async Task<(TournamentAggregate Tournament, Match Match)> GetMatchReadAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var match = await CreateMatchReadQuery(id)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (match is null)
            throw new NotFoundException("Match not found.");

        // Reads never persist: MatchDeadlineProcessor saves expired deadlines and publishes their events.
        // Applying the deadline to this untracked copy only shows the outcome before the processor's next poll.
        if (match.Tournament.Status == TournamentStatus.InProgress)
            match.ApplyDeadline(UtcNow());

        return (match.Tournament, match);
    }

    private async Task<(TournamentAggregate Tournament, Match Match)> GetMatchMutationGraphAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var match = await CreateMatchMutationQuery(id)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (match is null)
            throw new NotFoundException("Match not found.");

        return (match.Tournament, match);
    }

    private IQueryable<Match> CreateMatchReadQuery(Guid id) =>
        _dbContext.Matches
            .AsNoTracking()
            .Include(candidate => candidate.Tournament)
            .Where(candidate => candidate.Id == id);

    private IQueryable<Match> CreateMatchMutationQuery(Guid id) =>
        _dbContext.Matches
            .Include(candidate => candidate.Tournament)
            .Include(candidate => candidate.WinnerNextMatch)
            .Include(candidate => candidate.LoserNextMatch)
            .Where(candidate => candidate.Id == id);

    private void ApplyDeadline(
        TournamentAggregate tournament,
        Match match,
        DateTime nowUtc)
    {
        var beforeState = match.LifecycleState;
        match.ApplyDeadline(nowUtc);
        if (beforeState != MatchLifecycleState.AdminResolutionRequired &&
            match.LifecycleState == MatchLifecycleState.AdminResolutionRequired)
        {
            _moduleEventPublisher.Publish(new MatchResolutionRequiredIntegrationEvent(
                new MatchId(match.Id),
                new TournamentId(match.TournamentId),
                tournament.AssignedAdminUserId));
        }
    }

    private async Task<Guid> GetCurrentUserIdAsync(string auth0UserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(auth0UserId))
            throw new UnauthorizedAccessException("Authenticated user id is missing.");
        var user = await _identityModule.GetUserProfileByAuth0IdAsync(auth0UserId.Trim(), cancellationToken);
        if (user is null || user.IsDeleted)
            throw new NotFoundException("Current user profile was not found.");
        return user.Id.Value;
    }

    private async Task<MatchParticipantSide?> FindParticipantSideAsync(
        Match match,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (match.ParticipationMode == ParticipationMode.Individual)
        {
            if (match.UserParticipant1Id == userId)
                return MatchParticipantSide.Participant1;
            if (match.UserParticipant2Id == userId)
                return MatchParticipantSide.Participant2;
            return null;
        }

        foreach (var (teamId, side) in new[]
        {
            (match.TeamParticipant1Id, MatchParticipantSide.Participant1),
            (match.TeamParticipant2Id, MatchParticipantSide.Participant2)
        })
        {
            if (!teamId.HasValue)
                continue;
            var team = await _teamsModule.GetTeamSummaryAsync(new TeamId(teamId.Value), cancellationToken);
            if (team is not null && !team.IsDeleted && team.CaptainUserId?.Value == userId)
                return side;
        }
        return null;
    }

    private async Task<Guid?> GetOpponentUserIdAsync(
        Match match,
        MatchParticipantSide side,
        CancellationToken cancellationToken)
    {
        var opponentSide = side == MatchParticipantSide.Participant1
            ? MatchParticipantSide.Participant2
            : MatchParticipantSide.Participant1;
        if (match.ParticipationMode == ParticipationMode.Individual)
            return opponentSide == MatchParticipantSide.Participant1
                ? match.UserParticipant1Id
                : match.UserParticipant2Id;

        var teamId = opponentSide == MatchParticipantSide.Participant1
            ? match.TeamParticipant1Id
            : match.TeamParticipant2Id;
        if (!teamId.HasValue)
            return null;

        var team = await _teamsModule.GetTeamSummaryAsync(new TeamId(teamId.Value), cancellationToken);
        if (team is null || team.IsDeleted || !team.CaptainUserId.HasValue)
            return null;

        return team.CaptainUserId.Value.Value;
    }

    private async Task<MatchParticipantSide> RequireParticipantSideAsync(
        Match match,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var side = await FindParticipantSideAsync(match, userId, cancellationToken);
        if (!side.HasValue)
            throw new ForbiddenException("match_participant_required", "Only a participant or team captain can perform this action.");
        return side.Value;
    }

    private static void EnsureInProgress(TournamentAggregate tournament)
    {
        if (tournament.Status != TournamentStatus.InProgress)
            throw new ValidationException("Match actions are available only while the tournament is in progress.");
    }

    private static bool CanConfirmEnded(Match match, MatchParticipantSide? side) =>
        side.HasValue &&
        match.HasBothParticipants &&
        !match.Participant1IsBYE &&
        !match.Participant2IsBYE &&
        !match.HasResult &&
        match.LifecycleState is not MatchLifecycleState.AdminResolutionRequired &&
        ((side == MatchParticipantSide.Participant1 && !match.Participant1Ended) ||
         (side == MatchParticipantSide.Participant2 && !match.Participant2Ended));

    private static bool CanSubmitScore(Match match, MatchParticipantSide? side)
    {
        if (!side.HasValue ||
            !match.HasBothParticipants ||
            match.Participant1IsBYE ||
            match.Participant2IsBYE)
        {
            return false;
        }

        return match.LifecycleState switch
        {
            MatchLifecycleState.AwaitingScore => true,
            MatchLifecycleState.ScoreConfirmation => side == MatchParticipantSide.Participant1
                ? !match.Participant1ReportedScore1.HasValue
                : !match.Participant2ReportedScore1.HasValue,
            MatchLifecycleState.Disputed => side == MatchParticipantSide.Participant1
                ? match.Participant1CorrectionCount < 1
                : match.Participant2CorrectionCount < 1,
            _ => false
        };
    }

    private static bool CanForfeit(Match match, MatchParticipantSide? side) =>
        side.HasValue &&
        match.HasBothParticipants &&
        !match.Participant1IsBYE &&
        !match.Participant2IsBYE &&
        !match.HasResult &&
        match.LifecycleState != MatchLifecycleState.AdminResolutionRequired;

    private async Task<bool> IsTournamentInProgressAsync(
        Guid tournamentId,
        CancellationToken cancellationToken) =>
        await _dbContext.Tournaments
            .AsNoTracking()
            .AnyAsync(
                tournament => tournament.Id == tournamentId && tournament.Status == TournamentStatus.InProgress,
                cancellationToken);

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        _dbContext.Database.BeginTransactionAsync(cancellationToken);

    private async Task SaveAndCommitAsync(
        IDbContextTransaction transaction,
        TournamentAggregate tournament,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await IsTournamentInProgressAsync(tournament.Id, cancellationToken))
                throw new ValidationException("Match actions are available only while the tournament is in progress.");

            _dbContext.Tournaments.Entry(tournament).Property(candidate => candidate.Status).IsModified = true;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(
                "match_state_changed",
                "The match changed while this action was being processed. Refresh and try again.");
        }
    }
}
