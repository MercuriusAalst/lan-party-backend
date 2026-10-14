using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Teams.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Platform.Extensions;
using Platform.Realtime;

namespace Mercurius.LAN.API.Hubs;

[Authorize]
public class TeamManagementHub : Hub
{
    public const string Route = "/v1/lan/team-events";

    private readonly IIdentityModule _identityModule;
    private readonly IRealtimeConnectionManager _connectionManager;
    private readonly ITeamRealtimeAuthorizer _teamRealtimeAuthorizer;

    public TeamManagementHub(
        IIdentityModule identityModule,
        ITeamRealtimeAuthorizer teamRealtimeAuthorizer,
        IRealtimeConnectionManager connectionManager)
    {
        _identityModule = identityModule;
        _teamRealtimeAuthorizer = teamRealtimeAuthorizer;
        _connectionManager = connectionManager;
    }

    public override async Task OnConnectedAsync()
    {
        var knownUserId = await GetCurrentUserIdAsync(Context.ConnectionAborted);
        if (!knownUserId.HasValue)
        {
            await base.OnConnectedAsync();
            return;
        }

        await _connectionManager.ExecuteWithAccessGateAsync(
            async cancellationToken =>
            {
                // Revalidate after acquiring the user gate so account revocation cannot race this registration.
                var userId = await GetCurrentUserIdAsync(cancellationToken);
                if (userId != knownUserId)
                    return;

                var personalGroup = TeamRealtimeGroups.GetUserGroup(userId.Value);
                await Groups.AddToGroupAsync(Context.ConnectionId, personalGroup, cancellationToken);
                _connectionManager.RegisterConnection(userId.Value, Context.ConnectionId, personalGroup);
            },
            userId: knownUserId,
            connectionId: Context.ConnectionId,
            cancellationToken: Context.ConnectionAborted);

        await base.OnConnectedAsync();
    }

    public async Task JoinTeam(Guid teamId)
    {
        var knownUserId = await GetCurrentUserIdAsync(Context.ConnectionAborted);
        if (!knownUserId.HasValue)
            throw new HubException("Current user profile was not found.");

        await _connectionManager.ExecuteWithAccessGateAsync(
            async cancellationToken =>
            {
                // Revalidate after acquiring the user gate so account revocation cannot race this subscription.
                var userId = await GetCurrentUserIdAsync(cancellationToken);
                if (userId != knownUserId)
                    throw new HubException("Current user profile was not found.");

                var canJoin = await _teamRealtimeAuthorizer.CanSubscribeToTeamAsync(
                    new TeamId(teamId),
                    new UserId(userId.Value),
                    cancellationToken);

                if (!canJoin)
                    throw new HubException("You are not allowed to subscribe to this team.");

                var teamGroup = TeamRealtimeGroups.GetTeamGroup(teamId);
                await Groups.AddToGroupAsync(Context.ConnectionId, teamGroup, cancellationToken);
                _connectionManager.TrackGroup(Context.ConnectionId, teamGroup);
            },
            userId: knownUserId,
            groupName: TeamRealtimeGroups.GetTeamGroup(teamId),
            connectionId: Context.ConnectionId,
            cancellationToken: Context.ConnectionAborted);
    }

    public Task LeaveTeam(Guid teamId)
    {
        return _connectionManager.ExecuteWithAccessGateAsync(
            async cancellationToken =>
            {
                var teamGroup = TeamRealtimeGroups.GetTeamGroup(teamId);
                try
                {
                    await Groups.RemoveFromGroupAsync(Context.ConnectionId, teamGroup, cancellationToken);
                }
                finally
                {
                    _connectionManager.UntrackGroup(Context.ConnectionId, teamGroup);
                }
            },
            userId: null,
            groupName: TeamRealtimeGroups.GetTeamGroup(teamId),
            connectionId: Context.ConnectionId,
            cancellationToken: Context.ConnectionAborted);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        try
        {
            await _connectionManager.ExecuteWithAccessGateAsync(
                _ =>
                {
                    _connectionManager.UnregisterConnection(Context.ConnectionId);
                    return Task.CompletedTask;
                },
                userId: null,
                connectionId: Context.ConnectionId,
                cancellationToken: CancellationToken.None);
        }
        finally
        {
            await base.OnDisconnectedAsync(exception);
        }
    }

    private async Task<Guid?> GetCurrentUserIdAsync(CancellationToken cancellationToken)
    {
        var auth0UserId = Context.User?.FindAuth0UserId();
        if (string.IsNullOrWhiteSpace(auth0UserId))
            return null;

        var user = await _identityModule.GetUserProfileByAuth0IdAsync(auth0UserId.Trim(), cancellationToken);
        return user is { IsDeleted: false } ? user.Id.Value : null;
    }
}
