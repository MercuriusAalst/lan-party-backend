using Mercurius.Modules.Teams.Contracts;
using Platform.Realtime;

namespace Mercurius.Modules.Teams.Application.Services;

internal sealed class RealtimeTeamEventPublisher : ITeamEventPublisher
{
    private readonly IRealtimePublisher _realtimePublisher;

    public RealtimeTeamEventPublisher(IRealtimePublisher realtimePublisher)
    {
        _realtimePublisher = realtimePublisher;
    }

    public Task InviteChangedAsync(Guid teamId, Guid inviteId, Guid affectedUserId, string status, CancellationToken cancellationToken = default)
    {
        return _realtimePublisher.PublishAsync(new RealtimePublishRequest<TeamInviteChangedRealtimeEvent>(
            "TeamInviteChanged",
            new TeamInviteChangedRealtimeEvent(teamId, inviteId, affectedUserId, status),
            [TeamRealtimeGroups.GetUserGroup(affectedUserId)]), cancellationToken);
    }

    public Task MembershipChangedAsync(Guid teamId, Guid affectedUserId, string action, CancellationToken cancellationToken = default)
    {
        return _realtimePublisher.PublishAsync(new RealtimePublishRequest<TeamMembershipChangedRealtimeEvent>(
            "TeamMembershipChanged",
            new TeamMembershipChangedRealtimeEvent(teamId, affectedUserId, action),
            [TeamRealtimeGroups.GetTeamGroup(teamId), TeamRealtimeGroups.GetUserGroup(affectedUserId)]), cancellationToken);
    }

    public Task CaptainTransferredAsync(Guid teamId, Guid newCaptainUserId, CancellationToken cancellationToken = default)
    {
        return _realtimePublisher.PublishAsync(new RealtimePublishRequest<TeamCaptainTransferredRealtimeEvent>(
            "TeamCaptainTransferred",
            new TeamCaptainTransferredRealtimeEvent(teamId, newCaptainUserId),
            [TeamRealtimeGroups.GetTeamGroup(teamId)]), cancellationToken);
    }
}

internal sealed record TeamInviteChangedRealtimeEvent(Guid TeamId, Guid InviteId, Guid UserId, string Status);
internal sealed record TeamMembershipChangedRealtimeEvent(Guid TeamId, Guid UserId, string Action);
internal sealed record TeamCaptainTransferredRealtimeEvent(Guid TeamId, Guid NewCaptainUserId);
