using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Teams.Application.Services;
using Mercurius.Modules.Teams.Contracts;
using Mercurius.Modules.Teams.Domain;
using Mercurius.Modules.Teams.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Platform.Eventing;
using Platform.Realtime;

namespace Mercurius.Modules.Teams.Application;

// Removes a deleted account from every live team: membership, pending invites and captainship (with succession).
// Each team is processed in its own transaction under the team mutation lock, so it serializes with every other
// team write; reprocessing the same account changes nothing. Triggered by the durable user-deleted event; legacy
// deletions are replayed through the same event by the TeamMemberJoinedAt migration.
internal sealed class UserDeletedTeamCleanupHandler : IModuleEventHandler<UserDeletedIntegrationEvent>
{
    private readonly ITeamsDbContext _dbContext;
    private readonly IIdentityModule _identityModule;
    private readonly IModuleEventPublisher _moduleEventPublisher;
    private readonly ITeamEventPublisher _teamEventPublisher;
    private readonly IRealtimeConnectionManager _realtimeConnectionManager;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<UserDeletedTeamCleanupHandler> _logger;

    public UserDeletedTeamCleanupHandler(
        ITeamsDbContext dbContext,
        IIdentityModule identityModule,
        IModuleEventPublisher moduleEventPublisher,
        ITeamEventPublisher teamEventPublisher,
        IRealtimeConnectionManager realtimeConnectionManager,
        TimeProvider timeProvider,
        ILogger<UserDeletedTeamCleanupHandler> logger)
    {
        _dbContext = dbContext;
        _identityModule = identityModule;
        _moduleEventPublisher = moduleEventPublisher;
        _teamEventPublisher = teamEventPublisher;
        _realtimeConnectionManager = realtimeConnectionManager;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public string ConsumerName => "teams-deleted-user-cleanup-v1";

    public Task HandleAsync(
        UserDeletedIntegrationEvent payload,
        ModuleEventContext context,
        CancellationToken cancellationToken = default)
    {
        return RemoveDeletedUserAsync(payload.UserId.Value, cancellationToken);
    }

    public async Task<int> RemoveDeletedUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _identityModule.GetUserProfileAsync(new UserId(userId), cancellationToken);
        if (user is not { IsDeleted: true })
        {
            _logger.LogWarning("Skipped team cleanup for user {UserId} because the account is not deleted.", userId);
            return 0;
        }

        var changedTeams = 0;
        Guid? afterTeamId = null;
        while (await FindNextAffectedTeamIdAsync(userId, afterTeamId, cancellationToken) is { } teamId)
        {
            afterTeamId = teamId;
            var outcome = await CleanUpTeamAsync(teamId, userId, cancellationToken);
            if (outcome is null)
                continue;

            changedTeams++;
            await NotifyAsync(outcome);
        }

        return changedTeams;
    }

    private Task<Guid?> FindNextAffectedTeamIdAsync(Guid userId, Guid? afterTeamId, CancellationToken cancellationToken)
    {
        return _dbContext.Teams
            .AsNoTracking()
            .Where(team =>
                !team.IsDeleted &&
                (!afterTeamId.HasValue || team.Id > afterTeamId.Value) &&
                (team.CaptainUserId == userId ||
                 team.Members.Any(member => member.UserId == userId) ||
                 team.TeamInvites.Any(invite => invite.UserId == userId && invite.Status == TeamInviteStatus.Pending)))
            .OrderBy(team => team.Id)
            .Select(team => (Guid?)team.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<TeamCleanupOutcome?> CleanUpTeamAsync(Guid teamId, Guid userId, CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await TeamMutationLock.AcquireAsync(_dbContext, teamId, cancellationToken);
            var team = await _dbContext.Teams
                .AsSplitQuery()
                .Include(candidate => candidate.Members)
                .Include(candidate => candidate.TeamInvites
                    .Where(invite => invite.UserId == userId && invite.Status == TeamInviteStatus.Pending))
                .SingleOrDefaultAsync(candidate => candidate.Id == teamId, cancellationToken);
            if (team is null || team.IsDeleted)
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var memberRemoved = team.RemoveDeletedAccount(userId);
            var cancelledInviteIds = new List<Guid>();
            foreach (var invite in team.TeamInvites.Where(invite => invite.UserId == userId && invite.Status == TeamInviteStatus.Pending))
            {
                invite.Cancel(now);
                cancelledInviteIds.Add(invite.Id);
            }

            CaptainSuccession? succession = null;
            if (team.CaptainUserId == userId)
            {
                var successor = await SelectSuccessorAsync(team, cancellationToken);
                team.AssignSuccessor(successor);
                succession = new CaptainSuccession(successor);
                _moduleEventPublisher.Publish(new TeamCaptainSucceededIntegrationEvent(
                    new TeamId(team.Id),
                    team.Version,
                    new UserId(userId),
                    successor.HasValue ? new UserId(successor.Value) : null));
            }

            if (!memberRemoved && cancelledInviteIds.Count == 0 && succession is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            if (succession is { NewCaptainUserId: null })
            {
                _logger.LogWarning(
                    "Team {TeamId} has no captain after deleted user {DeletedUserId} was removed because no remaining member is eligible.",
                    team.Id,
                    userId);
            }
            else if (succession is not null)
            {
                _logger.LogInformation(
                    "Team {TeamId} captainship passed from deleted user {DeletedUserId} to {NewCaptainUserId}.",
                    team.Id,
                    userId,
                    succession.NewCaptainUserId);
            }

            return new TeamCleanupOutcome(team.Id, userId, memberRemoved, cancelledInviteIds, succession);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    // Legacy members (no recorded join time) first by account creation, then recorded joiners by join time; user id
    // breaks ties. Deleted accounts and members already at the captain limit are skipped.
    private async Task<Guid?> SelectSuccessorAsync(Team team, CancellationToken cancellationToken)
    {
        if (team.Members.Count == 0)
            return null;

        var users = await _identityModule.GetUsersByIdsAsync(
            team.Members.Select(member => new UserId(member.UserId)).ToArray(),
            cancellationToken);
        var candidates = team.Members
            .Select(member => (member.UserId, member.JoinedAtUtc, Profile: users.GetValueOrDefault(new UserId(member.UserId))))
            .Where(candidate => candidate.Profile is { IsDeleted: false })
            .OrderBy(candidate => candidate.JoinedAtUtc.HasValue)
            .ThenBy(candidate => candidate.JoinedAtUtc ?? candidate.Profile!.CreatedAtUtc ?? DateTime.MaxValue)
            .ThenBy(candidate => candidate.UserId);

        foreach (var candidate in candidates)
        {
            // Team lock, then the candidate's identity lock (the order TeamService uses): a concurrent deletion of the
            // candidate or a concurrent captain transfer to the candidate is serialized with this choice.
            var lockedProfile = await _identityModule.GetUserProfileForUpdateAsync(new UserId(candidate.UserId), cancellationToken);
            if (lockedProfile is not { IsDeleted: false })
                continue;

            var captainedTeams = await _dbContext.Teams.CountAsync(
                other => !other.IsDeleted && other.CaptainUserId == candidate.UserId && other.Id != team.Id,
                cancellationToken);
            if (captainedTeams < TeamService.MaxCaptainedTeams)
                return candidate.UserId;
        }

        return null;
    }

    private async Task NotifyAsync(TeamCleanupOutcome outcome)
    {
        await TryNotifyAsync(() => _realtimeConnectionManager.RevokeUserFromGroupAsync(
            outcome.UserId,
            TeamRealtimeGroups.GetTeamGroup(outcome.TeamId),
            CancellationToken.None));
        if (outcome.MemberRemoved)
        {
            await TryNotifyAsync(() => _teamEventPublisher.MembershipChangedAsync(
                outcome.TeamId,
                outcome.UserId,
                "Removed",
                CancellationToken.None));
        }

        foreach (var inviteId in outcome.CancelledInviteIds)
        {
            await TryNotifyAsync(() => _teamEventPublisher.InviteChangedAsync(
                outcome.TeamId,
                inviteId,
                outcome.UserId,
                nameof(TeamInviteStatus.Cancelled),
                CancellationToken.None));
        }

        if (outcome.Succession?.NewCaptainUserId is { } newCaptainUserId)
        {
            await TryNotifyAsync(() => _teamEventPublisher.CaptainTransferredAsync(
                outcome.TeamId,
                newCaptainUserId,
                CancellationToken.None));
        }
    }

    private async Task TryNotifyAsync(Func<Task> notification)
    {
        try
        {
            await notification();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "A realtime notification after deleted-user team cleanup failed.");
        }
    }

    private sealed record CaptainSuccession(Guid? NewCaptainUserId);

    private sealed record TeamCleanupOutcome(
        Guid TeamId,
        Guid UserId,
        bool MemberRemoved,
        IReadOnlyList<Guid> CancelledInviteIds,
        CaptainSuccession? Succession);
}
