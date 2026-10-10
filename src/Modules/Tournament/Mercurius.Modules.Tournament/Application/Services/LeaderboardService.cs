using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Shared.Exceptions;
using Mercurius.Modules.Tournament.Application.DTOs.Leaderboards;
using Mercurius.Modules.Tournament.Domain;
using Mercurius.Modules.Tournament.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Contracts = Mercurius.Modules.Tournament.Contracts;
using RankingMetric = Mercurius.Modules.Tournament.Domain.LeaderboardRankingMetric;

namespace Mercurius.Modules.Tournament.Application.Services;

internal sealed class LeaderboardService(ITournamentDbContext dbContext, IIdentityModule identityModule) : ILeaderboardService
{
    public async Task<LeaderboardResponseDTO> GetPublicLeaderboardAsync(Guid tournamentId, CancellationToken cancellationToken = default)
    {
        var leaderboard = await dbContext.Tournaments.AsNoTracking()
            .Where(item => item.Id == tournamentId && item.BracketType == BracketType.Leaderboard)
            .Select(item => new LeaderboardResponseDTO
            {
                TournamentId = item.Id,
                RankingMetric = (Contracts.LeaderboardRankingMetric)item.LeaderboardRankingMetric!.Value,
                Rows = item.LeaderboardParticipants
                    .Where(participant => item.LeaderboardRankingMetric == RankingMetric.HighestScore
                        ? participant.Attempts.Any(attempt => attempt.Score.HasValue)
                        : participant.Attempts.Any(attempt => attempt.DurationMilliseconds.HasValue))
                    .Select(participant => new LeaderboardRowDTO
                    {
                        ParticipantId = participant.Id,
                        DisplayName = participant.DisplayName,
                        ParticipantKind = participant.LinkedUserId.HasValue
                            ? Contracts.LeaderboardParticipantKind.LinkedUser
                            : Contracts.LeaderboardParticipantKind.Guest,
                        LinkedUserId = participant.LinkedUserId,
                        Score = item.LeaderboardRankingMetric == RankingMetric.HighestScore
                            ? participant.Attempts.Where(attempt => attempt.Score.HasValue).Max(attempt => attempt.Score)
                            : null,
                        DurationMilliseconds = item.LeaderboardRankingMetric == RankingMetric.FastestTime
                            ? participant.Attempts.Where(attempt => attempt.DurationMilliseconds.HasValue).Min(attempt => attempt.DurationMilliseconds)
                            : null
                    })
                    .ToList()
            })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Leaderboard tournament not found.");
        AssignCompetitionRanks(leaderboard);
        return leaderboard;
    }

    public async Task<AdminLeaderboardResponseDTO> GetAdminLeaderboardAsync(Guid tournamentId, CancellationToken cancellationToken = default)
    {
        return await dbContext.Tournaments.AsNoTracking()
            .Where(item => item.Id == tournamentId && item.BracketType == BracketType.Leaderboard)
            .Select(item => new AdminLeaderboardResponseDTO
            {
                TournamentId = item.Id,
                RankingMetric = (Contracts.LeaderboardRankingMetric)item.LeaderboardRankingMetric!.Value,
                Participants = item.LeaderboardParticipants
                    .OrderBy(participant => participant.Id)
                    .Select(participant => new AdminLeaderboardParticipantDTO
                    {
                        Id = participant.Id,
                        DisplayName = participant.DisplayName,
                        ParticipantKind = participant.LinkedUserId.HasValue
                            ? Contracts.LeaderboardParticipantKind.LinkedUser
                            : Contracts.LeaderboardParticipantKind.Guest,
                        LinkedUserId = participant.LinkedUserId,
                        Attempts = participant.Attempts
                            .OrderBy(attempt => attempt.CreatedAtUtc)
                            .ThenBy(attempt => attempt.Id)
                            .Select(attempt => new LeaderboardAttemptDTO
                            {
                                Id = attempt.Id,
                                Score = attempt.Score,
                                DurationMilliseconds = attempt.DurationMilliseconds,
                                CreatedAtUtc = attempt.CreatedAtUtc,
                                UpdatedAtUtc = attempt.UpdatedAtUtc,
                                RowVersion = attempt.RowVersion
                            })
                            .ToList()
                    })
                    .ToList()
            })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Leaderboard tournament not found.");
    }

    public async Task<AdminLeaderboardParticipantDTO> RecordAttemptAsync(
        Guid tournamentId,
        RecordLeaderboardAttemptDTO request,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var tournament = await GetLeaderboardForMutationAsync(tournamentId, cancellationToken);
        var existingParticipant = tournament.ValidateCanRecordLeaderboardAttempt(
            request.ParticipantId,
            request.LinkedUserId,
            request.GuestDisplayName,
            request.Score,
            request.DurationMilliseconds);
        string? linkedUserDisplayName = null;
        if (request.LinkedUserId.HasValue && existingParticipant is null)
        {
            var linkedUserId = new UserId(request.LinkedUserId.Value);
            var profile = await identityModule.GetUserProfileAsync(linkedUserId, cancellationToken);
            if (profile is null || profile.IsDeleted)
                throw new NotFoundException("Linked user not found.");

            var publicUsernames = await identityModule.GetPublicUsernamesByIdsAsync([linkedUserId], cancellationToken);
            linkedUserDisplayName = publicUsernames.TryGetValue(linkedUserId, out var username) &&
                                    !string.IsNullOrWhiteSpace(username)
                ? username
                : "Incomplete profile";
        }

        var (participant, attempt) = tournament.RecordLeaderboardAttempt(
            request.ParticipantId,
            request.LinkedUserId,
            request.GuestDisplayName,
            linkedUserDisplayName,
            request.Score,
            request.DurationMilliseconds,
            DateTime.UtcNow);
        if (existingParticipant is null)
            dbContext.LeaderboardParticipants.Add(participant);
        dbContext.LeaderboardAttempts.Add(attempt);
        tournament.IncrementRevision();
        await SaveMutationAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToAdminParticipantDto(participant);
    }

    public async Task<LeaderboardAttemptDTO> UpdateAttemptAsync(
        Guid tournamentId,
        Guid attemptId,
        UpdateLeaderboardAttemptDTO request,
        CancellationToken cancellationToken = default)
    {
        if (!request.RowVersion.HasValue)
            throw new ValidationException("rowVersion is required.");
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var tournament = await GetLeaderboardForMutationAsync(tournamentId, cancellationToken);
        var attempt = tournament.UpdateLeaderboardAttempt(
            attemptId,
            request.RowVersion.Value,
            request.Score,
            request.DurationMilliseconds,
            DateTime.UtcNow);
        tournament.IncrementRevision();
        await SaveMutationAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToAttemptDto(attempt);
    }

    public async Task RemoveAttemptAsync(Guid tournamentId, Guid attemptId, Guid rowVersion, CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var tournament = await GetLeaderboardForMutationAsync(tournamentId, cancellationToken);
        tournament.RemoveLeaderboardAttempt(attemptId, rowVersion);
        tournament.IncrementRevision();
        await SaveMutationAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static void AssignCompetitionRanks(LeaderboardResponseDTO leaderboard)
    {
        var ordered = leaderboard.RankingMetric == Contracts.LeaderboardRankingMetric.HighestScore
            ? leaderboard.Rows.OrderByDescending(row => row.Score).ThenBy(row => row.ParticipantId).ToList()
            : leaderboard.Rows.OrderBy(row => row.DurationMilliseconds).ThenBy(row => row.ParticipantId).ToList();

        for (var index = 0; index < ordered.Count; index++)
        {
            var tied = index > 0 && (leaderboard.RankingMetric == Contracts.LeaderboardRankingMetric.HighestScore
                ? ordered[index].Score == ordered[index - 1].Score
                : ordered[index].DurationMilliseconds == ordered[index - 1].DurationMilliseconds);
            ordered[index].Rank = tied ? ordered[index - 1].Rank : index + 1;
        }

        leaderboard.Rows = ordered;
    }

    private static AdminLeaderboardParticipantDTO ToAdminParticipantDto(LeaderboardParticipant participant) => new()
    {
        Id = participant.Id,
        DisplayName = participant.DisplayName,
        ParticipantKind = ToParticipantKind(participant),
        LinkedUserId = participant.LinkedUserId,
        Attempts = participant.Attempts.OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.Id).Select(ToAttemptDto).ToList()
    };

    private static Contracts.LeaderboardParticipantKind ToParticipantKind(LeaderboardParticipant participant) =>
        participant.LinkedUserId.HasValue
            ? Contracts.LeaderboardParticipantKind.LinkedUser
            : Contracts.LeaderboardParticipantKind.Guest;

    private static LeaderboardAttemptDTO ToAttemptDto(LeaderboardAttempt attempt) => new()
    {
        Id = attempt.Id,
        Score = attempt.Score,
        DurationMilliseconds = attempt.DurationMilliseconds,
        CreatedAtUtc = attempt.CreatedAtUtc,
        UpdatedAtUtc = attempt.UpdatedAtUtc,
        RowVersion = attempt.RowVersion
    };

    private IQueryable<TournamentAggregate> GetLeaderboardForMutationQuery() => dbContext.Tournaments
        .Where(item => item.BracketType == BracketType.Leaderboard)
        .Include(item => item.LeaderboardParticipants)
            .ThenInclude(participant => participant.Attempts);

    private async Task<TournamentAggregate> GetLeaderboardForMutationAsync(Guid tournamentId, CancellationToken cancellationToken) =>
        await GetLeaderboardForMutationQuery().SingleOrDefaultAsync(item => item.Id == tournamentId, cancellationToken)
        ?? throw new NotFoundException("Leaderboard tournament not found.");

    private async Task SaveMutationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("leaderboard_changed", "The leaderboard changed. Refresh and try again.");
        }
        catch (DbUpdateException exception) when (IsLinkedUserUniqueViolation(exception))
        {
            throw new ConflictException("leaderboard_participant_exists", "The linked user already participates in this leaderboard.");
        }
    }

    private static bool IsLinkedUserUniqueViolation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current.Message.Contains("IX_leaderboard_participants_TournamentId_LinkedUserId", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
