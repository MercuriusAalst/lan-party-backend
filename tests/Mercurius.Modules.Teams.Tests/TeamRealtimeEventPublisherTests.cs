using Mercurius.Modules.Teams.Contracts;
using Mercurius.Modules.Teams.Application.Services;
using Platform.Realtime;

namespace Mercurius.Modules.Teams.Tests;

public sealed class TeamRealtimeEventPublisherTests
{
    [Fact]
    public async Task MembershipChangedTargetsTheTeamAndOnlyTheAffectedUserGroups()
    {
        var teamId = Guid.NewGuid();
        var affectedUserId = Guid.NewGuid();
        var realtimePublisher = new RecordingRealtimePublisher();
        var publisher = new RealtimeTeamEventPublisher(realtimePublisher);

        await publisher.MembershipChangedAsync(teamId, affectedUserId, "Removed");

        Assert.Equal("TeamMembershipChanged", realtimePublisher.ClientMethod);
        Assert.Equal(
            [TeamRealtimeGroups.GetTeamGroup(teamId), TeamRealtimeGroups.GetUserGroup(affectedUserId)],
            realtimePublisher.Groups);
        var payload = Assert.IsType<TeamMembershipChangedRealtimeEvent>(realtimePublisher.Payload);
        Assert.Equal(teamId, payload.TeamId);
        Assert.Equal(affectedUserId, payload.UserId);
        Assert.Equal("Removed", payload.Action);
    }

    private sealed class RecordingRealtimePublisher : IRealtimePublisher
    {
        public string? ClientMethod { get; private set; }
        public object? Payload { get; private set; }
        public IReadOnlyList<string>? Groups { get; private set; }

        public Task PublishAsync<TPayload>(
            RealtimePublishRequest<TPayload> request,
            CancellationToken cancellationToken = default)
        {
            ClientMethod = request.ClientMethod;
            Payload = request.Payload;
            Groups = request.Groups;
            return Task.CompletedTask;
        }
    }
}
