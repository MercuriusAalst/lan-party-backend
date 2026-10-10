using System.Data;
using Mercurius.Modules.Teams.Contracts;
using Mercurius.Modules.Teams.Domain;
using Mercurius.Modules.Teams.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace Mercurius.Modules.Teams.Application.Services;

internal sealed class TeamInviteMaintenanceService
{
    private const long MaintenanceLockKey = 0x5445414D494E5654;
    private readonly ITeamsDbContext _dbContext;
    private readonly ITeamEventPublisher _teamEventPublisher;
    private readonly TeamInviteMaintenanceOptions _options;
    private readonly TimeProvider _timeProvider;
    private DbSet<TeamInvite> TeamInvites => _dbContext.Set<TeamInvite>();

    public TeamInviteMaintenanceService(
        ITeamsDbContext dbContext,
        ITeamEventPublisher teamEventPublisher,
        IOptions<TeamInviteMaintenanceOptions> options,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _teamEventPublisher = teamEventPublisher ?? throw new ArgumentNullException(nameof(teamEventPublisher));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider;
    }

    public async Task<int> RunBatchAsync(CancellationToken cancellationToken = default)
    {
        var now = UtcNow();
        var retentionCutoff = now.AddDays(-_options.RetentionDays);
        var lockedTeamIds = await GetMaintenanceTeamIdsAsync(now, retentionCutoff, cancellationToken);
        var expiredEvents = new List<ExpiredInviteEvent>();
        var deletedCount = 0;

        await using (var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken))
        {
            try
            {
                if (!await TryAcquireMaintenanceLockAsync(cancellationToken))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return 0;
                }

                foreach (var teamId in lockedTeamIds)
                    await TeamMutationLock.AcquireAsync(_dbContext, teamId, cancellationToken);

                now = UtcNow();
                var expiredInvites = await TeamInvites
                    .Where(invite =>
                        invite.Status == TeamInviteStatus.Pending &&
                        invite.ExpiresAt <= now &&
                        lockedTeamIds.Contains(invite.TeamId))
                    .OrderBy(invite => invite.ExpiresAt)
                    .ThenBy(invite => invite.Id)
                    .Take(_options.MaintenanceBatchSize)
                    .ToListAsync(cancellationToken);

                foreach (var invite in expiredInvites)
                {
                    invite.Expire(now);
                    expiredEvents.Add(new ExpiredInviteEvent(invite.TeamId, invite.Id, invite.UserId));
                }

                if (expiredInvites.Count > 0)
                    await _dbContext.SaveChangesAsync(cancellationToken);

                var cleanupIds = await GetTerminalInviteCleanupCandidateIdsAsync(
                    retentionCutoff,
                    cancellationToken,
                    lockedTeamIds);

                if (cleanupIds.Count > 0)
                {
                    deletedCount = await TeamInvites
                        .Where(invite => cleanupIds.Contains(invite.Id))
                        .ExecuteDeleteAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        await PublishExpiredInviteEventsAsync(expiredEvents);
        return expiredEvents.Count + deletedCount;
    }

    private async Task<List<Guid>> GetTerminalInviteCleanupCandidateIdsAsync(
        DateTime cutoff,
        CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? teamIds = null)
    {
        var respondedQuery = TeamInvites
            .AsNoTracking()
            .Where(invite =>
                (invite.Status == TeamInviteStatus.Accepted || invite.Status == TeamInviteStatus.Declined) &&
                invite.RespondedAt.HasValue &&
                invite.RespondedAt.Value < cutoff);
        if (teamIds is not null)
            respondedQuery = respondedQuery.Where(invite => teamIds.Contains(invite.TeamId));
        var respondedInvites = await respondedQuery
            .OrderBy(invite => invite.RespondedAt)
            .ThenBy(invite => invite.Id)
            .Take(_options.MaintenanceBatchSize)
            .Select(invite => new TerminalInviteCandidate
            {
                Id = invite.Id,
                TerminalAt = invite.RespondedAt!.Value
            })
            .ToListAsync(cancellationToken);

        var cancelledQuery = TeamInvites
            .AsNoTracking()
            .Where(invite =>
                invite.Status == TeamInviteStatus.Cancelled &&
                invite.CancelledAt.HasValue &&
                invite.CancelledAt.Value < cutoff);
        if (teamIds is not null)
            cancelledQuery = cancelledQuery.Where(invite => teamIds.Contains(invite.TeamId));
        var cancelledInvites = await cancelledQuery
            .OrderBy(invite => invite.CancelledAt)
            .ThenBy(invite => invite.Id)
            .Take(_options.MaintenanceBatchSize)
            .Select(invite => new TerminalInviteCandidate
            {
                Id = invite.Id,
                TerminalAt = invite.CancelledAt!.Value
            })
            .ToListAsync(cancellationToken);

        var expiredQuery = TeamInvites
            .AsNoTracking()
            .Where(invite =>
                invite.Status == TeamInviteStatus.Expired &&
                invite.ExpiredAt.HasValue &&
                invite.ExpiredAt.Value < cutoff);
        if (teamIds is not null)
            expiredQuery = expiredQuery.Where(invite => teamIds.Contains(invite.TeamId));
        var expiredInvites = await expiredQuery
            .OrderBy(invite => invite.ExpiredAt)
            .ThenBy(invite => invite.Id)
            .Take(_options.MaintenanceBatchSize)
            .Select(invite => new TerminalInviteCandidate
            {
                Id = invite.Id,
                TerminalAt = invite.ExpiredAt!.Value
            })
            .ToListAsync(cancellationToken);

        return respondedInvites
            .Concat(cancelledInvites)
            .Concat(expiredInvites)
            .OrderBy(candidate => candidate.TerminalAt)
            .ThenBy(candidate => candidate.Id)
            .Take(_options.MaintenanceBatchSize)
            .Select(candidate => candidate.Id)
            .ToList();
    }

    private async Task<Guid[]> GetMaintenanceTeamIdsAsync(
        DateTime now,
        DateTime retentionCutoff,
        CancellationToken cancellationToken)
    {
        var expiredTeamIds = await TeamInvites
            .AsNoTracking()
            .Where(invite => invite.Status == TeamInviteStatus.Pending && invite.ExpiresAt <= now)
            .OrderBy(invite => invite.ExpiresAt)
            .ThenBy(invite => invite.Id)
            .Take(_options.MaintenanceBatchSize)
            .Select(invite => invite.TeamId)
            .ToListAsync(cancellationToken);
        var cleanupIds = await GetTerminalInviteCleanupCandidateIdsAsync(retentionCutoff, cancellationToken);
        var cleanupTeamIds = cleanupIds.Count == 0
            ? []
            : await TeamInvites
                .AsNoTracking()
                .Where(invite => cleanupIds.Contains(invite.Id))
                .Select(invite => invite.TeamId)
                .ToListAsync(cancellationToken);

        return expiredTeamIds
            .Concat(cleanupTeamIds)
            .Distinct()
            .Order()
            .ToArray();
    }

    private async Task<bool> TryAcquireMaintenanceLockAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.Database
            .SqlQueryRaw<bool>($"SELECT pg_try_advisory_xact_lock({MaintenanceLockKey}) AS \"Value\"")
            .SingleAsync(cancellationToken);
    }

    private async Task PublishExpiredInviteEventsAsync(
        IReadOnlyCollection<ExpiredInviteEvent> expiredEvents)
    {
        if (expiredEvents.Count == 0)
            return;

        await Parallel.ForEachAsync(
            expiredEvents,
            new ParallelOptions
            {
                CancellationToken = CancellationToken.None,
                MaxDegreeOfParallelism = Math.Min(_options.MaintenanceEventConcurrency, expiredEvents.Count)
            },
            async (expiredEvent, _) =>
            {
                await _teamEventPublisher.InviteChangedAsync(
                    expiredEvent.TeamId,
                    expiredEvent.InviteId,
                    expiredEvent.UserId,
                    nameof(TeamInviteStatus.Expired),
                    CancellationToken.None);
            });
    }

    private sealed class TerminalInviteCandidate
    {
        public Guid Id { get; init; }
        public DateTime TerminalAt { get; init; }
    }

    private sealed record ExpiredInviteEvent(Guid TeamId, Guid InviteId, Guid UserId);

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;
}
