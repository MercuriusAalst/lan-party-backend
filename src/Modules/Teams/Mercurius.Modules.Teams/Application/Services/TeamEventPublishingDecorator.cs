using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Teams.DTOs;
using Mercurius.Modules.Teams.Contracts;
using Mercurius.Modules.Teams.Domain;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Teams.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Platform.Eventing;
using Platform.Realtime;
using System.Runtime.ExceptionServices;
using TeamCreatedIntegrationEvent = Mercurius.Modules.Teams.Contracts.TeamCreatedIntegrationEvent;
using TeamDeletedIntegrationEvent = Mercurius.Modules.Teams.Contracts.TeamDeletedIntegrationEvent;
using TeamRenamedIntegrationEvent = Mercurius.Modules.Teams.Contracts.TeamRenamedIntegrationEvent;

namespace Mercurius.Modules.Teams.Services;

internal sealed class TeamEventPublishingDecorator : ITeamManagementCommands, ITeamInviteWorkflows
{
    private readonly TeamService _inner;
    private readonly ITeamsDbContext _dbContext;
    private readonly IIdentityModule _identityModule;
    private readonly ITeamEventPublisher _teamEventPublisher;
    private readonly IModuleEventPublisher _moduleEventPublisher;
    private readonly IRealtimeConnectionManager _realtimeConnectionManager;
    private DbSet<TeamInvite> TeamInvites => _dbContext.Set<TeamInvite>();

    public TeamEventPublishingDecorator(
        TeamService inner,
        ITeamsDbContext dbContext,
        IIdentityModule identityModule,
        ITeamEventPublisher teamEventPublisher,
        IModuleEventPublisher moduleEventPublisher,
        IRealtimeConnectionManager realtimeConnectionManager)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _identityModule = identityModule ?? throw new ArgumentNullException(nameof(identityModule));
        _teamEventPublisher = teamEventPublisher ?? throw new ArgumentNullException(nameof(teamEventPublisher));
        _moduleEventPublisher = moduleEventPublisher ?? throw new ArgumentNullException(nameof(moduleEventPublisher));
        _realtimeConnectionManager = realtimeConnectionManager ?? throw new ArgumentNullException(nameof(realtimeConnectionManager));
    }

    public async Task<GetTeamDTO> CreateTeamAsync(CreateTeamDTO teamDTO, CancellationToken cancellationToken = default)
    {
        return await ExecuteWithDurableEventTransactionAsync(
            () => _inner.CreateTeamAsync(teamDTO, cancellationToken),
            team => PublishTeamCreatedAsync(team.Id, team.CaptainUserId, cancellationToken),
            cancellationToken);
    }

    public async Task<TeamManagementSummaryDTO> CreateCurrentUserTeamAsync(string auth0UserId, CreateTeamDTO teamDTO, CancellationToken cancellationToken = default)
    {
        return await ExecuteWithDurableEventTransactionAsync(
            () => _inner.CreateCurrentUserTeamAsync(auth0UserId, teamDTO, cancellationToken),
            team => PublishTeamCreatedAsync(team.Id, team.CaptainUserId, cancellationToken),
            cancellationToken);
    }

    public async Task DeleteTeamAsync(Guid teamId, CancellationToken cancellationToken = default)
    {
        var deletion = await ExecuteWithDurableEventTransactionAsync(
            async () =>
            {
                var deletionState = await _inner.DeleteTeamAndGetStateAsync(teamId, cancellationToken);
                return new TeamDeletionResult(deletionState is not null, deletionState);
            },
            result => result.TeamDeleted
                ? PublishTeamDeletedAsync(teamId, cancellationToken)
                : Task.CompletedTask,
            cancellationToken,
            teamId);

        if (deletion.TeamDeleted)
        {
            try
            {
                await _realtimeConnectionManager.RevokeGroupAsync(
                    TeamRealtimeGroups.GetTeamGroup(teamId),
                    CancellationToken.None);
                await NotifyDeletedTeamUsersAsync(teamId, deletion.PreviousState!);
            }
            finally
            {
                await _inner.RetireLogoIfUnreferencedAsync(deletion.PreviousState!.LogoUrl, null);
            }
        }
    }

    public async Task DeleteTeamAsync(string auth0UserId, Guid teamId, CancellationToken cancellationToken = default)
    {
        var previousState = await ExecuteWithDurableEventTransactionAsync(
            () => _inner.DeleteTeamAndGetStateAsync(auth0UserId, teamId, cancellationToken),
            _ => PublishTeamDeletedAsync(teamId, cancellationToken),
            cancellationToken,
            teamId);

        try
        {
            await _realtimeConnectionManager.RevokeGroupAsync(
                TeamRealtimeGroups.GetTeamGroup(teamId),
                CancellationToken.None);
            await NotifyDeletedTeamUsersAsync(teamId, previousState!);
        }
        finally
        {
            await _inner.RetireLogoIfUnreferencedAsync(previousState!.LogoUrl, null);
        }
    }

    public Task<IReadOnlyList<GetTeamDTO>> GetAllTeamsAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        return _inner.GetAllTeamsAsync(page, pageSize, cancellationToken);
    }

    public Task<CurrentUserTeamSummaryDTO> GetCurrentUserTeamSummaryAsync(string auth0UserId, CancellationToken cancellationToken = default)
    {
        return _inner.GetCurrentUserTeamSummaryAsync(auth0UserId, cancellationToken);
    }

    public Task<PublicTeamProfileDTO> GetPublicTeamProfileAsync(string teamName, CancellationToken cancellationToken = default)
    {
        return _inner.GetPublicTeamProfileAsync(teamName, cancellationToken);
    }

    public Task<IEnumerable<TeamInviteDTO>> GetUserInvitesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return _inner.GetUserInvitesAsync(userId, cancellationToken);
    }

    public Task<IEnumerable<TeamInviteSummaryDTO>> GetCurrentUserInvitesAsync(string auth0UserId, CancellationToken cancellationToken = default)
    {
        return _inner.GetCurrentUserInvitesAsync(auth0UserId, cancellationToken);
    }

    public Task<IEnumerable<TeamInviteSummaryDTO>> GetCurrentUserSentInvitesAsync(string auth0UserId, CancellationToken cancellationToken = default)
    {
        return _inner.GetCurrentUserSentInvitesAsync(auth0UserId, cancellationToken);
    }

    public Task<GetTeamDTO> GetTeamByIdAsync(Guid teamId, CancellationToken cancellationToken = default)
    {
        return _inner.GetTeamByIdAsync(teamId, cancellationToken);
    }

    public Task<GetTeamDTO> GetTeamByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        return _inner.GetTeamByNameAsync(name, cancellationToken);
    }

    public async Task<TeamInviteDTO> InviteUserAsync(Guid teamId, Guid userId, CancellationToken cancellationToken = default)
    {
        return await PublishExpiredInviteEventsAroundAsync(
            () => _inner.InviteUserAsync(teamId, userId, cancellationToken),
            teamId,
            userId,
            cancellationToken);
    }

    public async Task<TeamInviteDTO> InviteUserAsync(string auth0UserId, Guid teamId, Guid userId, CancellationToken cancellationToken = default)
    {
        var invite = await PublishExpiredInviteEventsAroundAsync(
            () => _inner.InviteUserAsync(auth0UserId, teamId, userId, cancellationToken),
            teamId,
            userId,
            cancellationToken);

        await _teamEventPublisher.InviteChangedAsync(teamId, invite.Id, userId, invite.Status, CancellationToken.None);
        return invite;
    }

    public async Task<TeamInviteDTO> CancelInviteAsync(string auth0UserId, Guid teamId, Guid inviteId, CancellationToken cancellationToken = default)
    {
        var invite = await ExecuteTeamMutationAsync(
            teamId,
            () => _inner.CancelInviteAsync(auth0UserId, teamId, inviteId, cancellationToken),
            cancellationToken);
        await _teamEventPublisher.InviteChangedAsync(teamId, invite.Id, invite.UserId, invite.Status, CancellationToken.None);
        return invite;
    }

    public async Task<GetTeamDTO> RemoveMemberAsync(Guid id, Guid userId, CancellationToken cancellationToken = default)
    {
        var team = await ExecuteTeamMutationAsync(
            id,
            () => _inner.RemoveMemberAsync(id, userId, cancellationToken),
            cancellationToken);

        await _realtimeConnectionManager.RevokeUserFromGroupAsync(
            userId,
            TeamRealtimeGroups.GetTeamGroup(id),
            CancellationToken.None);
        return team;
    }

    public async Task<TeamManagementSummaryDTO> RemoveMemberAsync(string auth0UserId, Guid teamId, Guid userId, CancellationToken cancellationToken = default)
    {
        var team = await ExecuteTeamMutationAsync(
            teamId,
            () => _inner.RemoveMemberAsync(auth0UserId, teamId, userId, cancellationToken),
            cancellationToken);

        await _realtimeConnectionManager.RevokeUserFromGroupAsync(
            userId,
            TeamRealtimeGroups.GetTeamGroup(teamId),
            CancellationToken.None);
        await _teamEventPublisher.MembershipChangedAsync(teamId, userId, "Removed", CancellationToken.None);
        return team;
    }

    public async Task<TeamManagementSummaryDTO> LeaveTeamAsync(string auth0UserId, Guid teamId, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteTeamMutationAsync(
            teamId,
            async () =>
            {
                var team = await _inner.LeaveTeamAsync(auth0UserId, teamId, cancellationToken);
                var userId = await GetCurrentUserIdAsync(auth0UserId, cancellationToken);
                return new TeamMemberRemovedResult(team, userId);
            },
            cancellationToken);

        var userId = result.UserId;
        await _realtimeConnectionManager.RevokeUserFromGroupAsync(
            userId,
            TeamRealtimeGroups.GetTeamGroup(teamId),
            CancellationToken.None);
        await _teamEventPublisher.MembershipChangedAsync(teamId, userId, "Left", CancellationToken.None);
        return result.Team;
    }

    public async Task<TeamInviteDTO> RespondToInviteAsync(Guid teamId, Guid userId, bool accept, CancellationToken cancellationToken = default)
    {
        return await ExecuteTeamMutationAsync(
            teamId,
            () => _inner.RespondToInviteAsync(teamId, userId, accept, cancellationToken),
            cancellationToken);
    }

    public async Task<TeamInviteDTO> RespondToInviteAsync(string auth0UserId, Guid inviteId, bool accept, CancellationToken cancellationToken = default)
    {
        var teamId = await TeamInvites
            .AsNoTracking()
            .Where(invite => invite.Id == inviteId)
            .Select(invite => (Guid?)invite.TeamId)
            .SingleOrDefaultAsync(cancellationToken);
        if (!teamId.HasValue)
            throw new Mercurius.Modules.Shared.Exceptions.NotFoundException("No pending invite found");

        var invite = await ExecuteTeamMutationAsync(
            teamId.Value,
            () => _inner.RespondToInviteAsync(auth0UserId, inviteId, accept, cancellationToken),
            cancellationToken);

        await _teamEventPublisher.InviteChangedAsync(invite.TeamId, invite.Id, invite.UserId, invite.Status, CancellationToken.None);
        if (accept)
            await _teamEventPublisher.MembershipChangedAsync(invite.TeamId, invite.UserId, "Joined", CancellationToken.None);

        return invite;
    }

    public async Task<TeamManagementSummaryDTO> TransferCaptainAsync(string auth0UserId, Guid teamId, Guid newCaptainUserId, CancellationToken cancellationToken = default)
    {
        var team = await ExecuteTeamMutationAsync(
            teamId,
            () => _inner.TransferCaptainAsync(auth0UserId, teamId, newCaptainUserId, cancellationToken),
            cancellationToken);

        await _teamEventPublisher.CaptainTransferredAsync(teamId, newCaptainUserId, CancellationToken.None);
        return team;
    }

    public Task<TeamLogoResponseDTO> UploadTeamLogoAsync(string auth0UserId, Guid teamId, IFormFile logo, CancellationToken cancellationToken = default)
    {
        return _inner.UploadTeamLogoAsync(auth0UserId, teamId, logo, cancellationToken);
    }

    public Task<TeamLogoResponseDTO> RemoveTeamLogoAsync(string auth0UserId, Guid teamId, CancellationToken cancellationToken = default)
    {
        return _inner.RemoveTeamLogoAsync(auth0UserId, teamId, cancellationToken);
    }

    public Task<IEnumerable<GetTeamDTO>> SearchTeamsByNameAsync(string query, int? limit = null, CancellationToken cancellationToken = default)
    {
        return _inner.SearchTeamsByNameAsync(query, limit, cancellationToken);
    }

    public async Task<GetTeamDTO> UpdateTeamAsync(Guid id, UpdateTeamDTO teamDTO, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteWithDurableEventTransactionAsync(
            async () =>
            {
                var currentNormalizedName = await _dbContext.Teams
                    .AsNoTracking()
                    .Where(team => team.Id == id)
                    .Select(team => team.NormalizedName)
                    .FirstOrDefaultAsync(cancellationToken);

                var team = await _inner.UpdateTeamAsync(id, teamDTO, cancellationToken);

                var shouldPublishRenamed =
                    currentNormalizedName is not null &&
                    teamDTO.Name is not null &&
                    !string.Equals(currentNormalizedName, Team.NormalizeName(teamDTO.Name), StringComparison.Ordinal);

                return new TeamUpdatedResult(team, shouldPublishRenamed);
            },
            result => result.NameChanged
                ? PublishTeamRenamedAsync(id, cancellationToken)
                : Task.CompletedTask,
            cancellationToken,
            id);

        return result.Team;
    }

    private async Task ExecuteWithDurableEventTransactionAsync(Func<Task> operation, Func<Task> publishDurableEvents, CancellationToken cancellationToken)
    {
        await ExecuteWithDurableEventTransactionAsync(
            async () =>
            {
                await operation();
                return true;
            },
            _ => publishDurableEvents(),
            cancellationToken);
    }

    private Task<TResult> ExecuteTeamMutationAsync<TResult>(
        Guid teamId,
        Func<Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        return ExecuteWithDurableEventTransactionAsync(
            operation,
            _ => Task.CompletedTask,
            cancellationToken,
            teamId);
    }

    private async Task<TResult> ExecuteWithDurableEventTransactionAsync<TResult>(
        Func<Task<TResult>> operation,
        Func<TResult, Task> publishDurableEvents,
        CancellationToken cancellationToken,
        Guid? lockedTeamId = null)
    {
        await using var transaction = await BeginDurableEventTransactionAsync(cancellationToken);

        try
        {
            if (lockedTeamId.HasValue)
                await TeamMutationLock.AcquireAsync(_dbContext, lockedTeamId.Value, cancellationToken);

            var result = await operation();
            await publishDurableEvents(result);

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);

            return result;
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);

            throw;
        }
    }

    private async Task<IDbContextTransaction?> BeginDurableEventTransactionAsync(CancellationToken cancellationToken)
    {
        if (!_dbContext.Database.IsRelational())
            return null;

        return await _dbContext.Database.BeginTransactionAsync(cancellationToken);
    }

    private async Task<TResult> PublishExpiredInviteEventsAroundAsync<TResult>(
        Func<Task<TResult>> operation,
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginDurableEventTransactionAsync(cancellationToken);
        List<ExpiredTeamInviteChangedCandidate> candidates;

        ExceptionDispatchInfo? operationException = null;
        TResult? result = default;

        try
        {
            await TeamMutationLock.AcquireAsync(_dbContext, teamId, cancellationToken);
            candidates = await GetExpiredInviteEventCandidatesAsync(teamId, userId, cancellationToken);

            try
            {
                result = await operation();
            }
            catch (Exception exception)
            {
                operationException = ExceptionDispatchInfo.Capture(exception);
            }

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None);

            throw;
        }

        try
        {
            await PublishExpiredInviteEventsAsync(candidates);
        }
        catch when (operationException is not null)
        {
        }

        operationException?.Throw();
        return result!;
    }

    private async Task<List<ExpiredTeamInviteChangedCandidate>> GetExpiredInviteEventCandidatesAsync(Guid teamId, Guid userId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        return await TeamInvites
            .AsNoTracking()
            .Where(invite =>
                invite.Status == TeamInviteStatus.Pending &&
                invite.ExpiresAt <= now &&
                invite.TeamId == teamId &&
                invite.UserId == userId)
            .Select(invite => new ExpiredTeamInviteChangedCandidate(
                invite.TeamId,
                invite.Id,
                invite.UserId,
                nameof(TeamInviteStatus.Expired)))
            .ToListAsync(cancellationToken);
    }

    private async Task PublishExpiredInviteEventsAsync(IReadOnlyCollection<ExpiredTeamInviteChangedCandidate> candidates)
    {
        if (candidates.Count == 0)
            return;

        var candidateIds = candidates.Select(candidate => candidate.InviteId).ToList();
        var expiredInviteIds = await TeamInvites
            .AsNoTracking()
            .Where(invite => candidateIds.Contains(invite.Id) && invite.Status == TeamInviteStatus.Expired)
            .Select(invite => invite.Id)
            .ToListAsync(CancellationToken.None);

        foreach (var candidate in candidates.Where(candidate => expiredInviteIds.Contains(candidate.InviteId)))
        {
            await _teamEventPublisher.InviteChangedAsync(
                candidate.TeamId,
                candidate.InviteId,
                candidate.UserId,
                candidate.Status,
                CancellationToken.None);
        }
    }

    private async Task PublishTeamCreatedAsync(Guid teamId, Guid captainUserId, CancellationToken cancellationToken)
    {
        var team = await GetTeamForEventAsync(teamId, cancellationToken);
        IncrementTeamVersion(team);
        _moduleEventPublisher.Publish(new TeamCreatedIntegrationEvent(
            new TeamId(team.Id),
            team.Version,
            team.Name,
            new UserId(captainUserId)));
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task PublishTeamRenamedAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var team = await GetTeamForEventAsync(teamId, cancellationToken);
        IncrementTeamVersion(team);
        _moduleEventPublisher.Publish(new TeamRenamedIntegrationEvent(
            new TeamId(team.Id),
            team.Version,
            team.Name));
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task PublishTeamDeletedAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var team = await GetTeamForEventAsync(teamId, cancellationToken);
        IncrementTeamVersion(team);
        _moduleEventPublisher.Publish(new TeamDeletedIntegrationEvent(
            new TeamId(team.Id),
            team.Version));
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<Team> GetTeamForEventAsync(Guid teamId, CancellationToken cancellationToken)
    {
        return await _dbContext.Teams.FindAsync([teamId], cancellationToken)
            ?? throw new InvalidOperationException($"Team '{teamId}' was not found after a successful team mutation.");
    }

    private async Task<Guid> GetCurrentUserIdAsync(string auth0UserId, CancellationToken cancellationToken)
    {
        var user = await _identityModule.GetUserProfileByAuth0IdAsync(auth0UserId, cancellationToken);
        if (user is null || user.IsDeleted)
            throw new InvalidOperationException("Current user profile was not found after a successful team mutation.");

        return user.Id.Value;
    }

    private async Task NotifyDeletedTeamUsersAsync(Guid teamId, TeamService.TeamDeletionState state)
    {
        List<Exception>? failures = null;

        foreach (var userId in state.MemberUserIds)
        {
            try
            {
                await _teamEventPublisher.MembershipChangedAsync(teamId, userId, "Removed", CancellationToken.None);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        foreach (var invite in state.PendingInvites)
        {
            try
            {
                await _teamEventPublisher.InviteChangedAsync(
                    teamId,
                    invite.InviteId,
                    invite.UserId,
                    nameof(TeamInviteStatus.Cancelled),
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null)
            throw new AggregateException("One or more deleted-team recipient notifications failed.", failures);
    }

    private static void IncrementTeamVersion(Team team)
    {
        team.Version++;
    }

    private sealed record TeamMemberRemovedResult(TeamManagementSummaryDTO Team, Guid UserId);

    private sealed record TeamUpdatedResult(
        GetTeamDTO Team,
        bool NameChanged);

    private sealed record TeamDeletionResult(bool TeamDeleted, TeamService.TeamDeletionState? PreviousState);

    private sealed record ExpiredTeamInviteChangedCandidate(
        Guid TeamId,
        Guid InviteId,
        Guid UserId,
        string Status);
}
