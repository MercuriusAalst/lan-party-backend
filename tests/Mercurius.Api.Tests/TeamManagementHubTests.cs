using Mercurius.TestInfrastructure;
using System.Security.Claims;
using Mercurius.LAN.API.Data;
using Mercurius.LAN.API.Hubs;
using Mercurius.Modules.Identity;
using Mercurius.Modules.Identity.Application.Services;
using Mercurius.Modules.Identity.Domain;
using Mercurius.Modules.Identity.Infrastructure;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Teams.Contracts;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mercurius.Modules.Identity.Contracts;
using System.Reflection;
using Platform.Extensions;
using Platform.Realtime;

namespace Mercurius.Api.Tests;

public class TeamManagementHubTests
{
    [Fact]
    public async Task IdentityFilter_RejectsInvocationWhenConnectionIdentityHasChanged()
    {
        var identityContext = new AuthenticatedIdentityContext();
        var replacementId = Guid.NewGuid();
        var validator = new RebindingIdentityTokenValidator(identityContext, replacementId);
        var filter = new IdentityIncarnationHubFilter(validator, identityContext);
        var caller = new TestHubCallerContext(
            "connection",
            new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "auth0|reused"), new Claim("iat", "200")], "Bearer")));
        caller.Items["Mercurius.Identity.Incarnation"] = Guid.NewGuid().ToString("N");
        var invocation = new HubInvocationContext(
            caller,
            new ServiceCollection().BuildServiceProvider(),
            new TestHub(),
            typeof(TestHub).GetMethod(nameof(TestHub.Ping), BindingFlags.Instance | BindingFlags.Public)!,
            []);
        var invoked = false;

        await Assert.ThrowsAsync<HubException>(async () => await filter.InvokeMethodAsync(
            invocation,
            _ =>
            {
                invoked = true;
                return new ValueTask<object?>((object?)null);
            }));

        Assert.False(invoked);
        Assert.Equal(replacementId, identityContext.UserId);
    }

    [Fact]
    public async Task ConnectionLifecycle_TracksPersonalAndTeamGroupsAndCleansLeaveAndDisconnect()
    {
        await using var dbContext = CreateDbContext();
        var user = CreateUser();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        var groups = new RecordingGroupManager();
        await using var provider = CreateProvider(groups);
        var manager = provider.GetRequiredService<IRealtimeConnectionManager>();
        var hub = CreateHub(dbContext, manager, groups, new StubAuthorizer(true), user);
        var teamId = Guid.NewGuid();
        var teamGroup = TeamRealtimeGroups.GetTeamGroup(teamId);

        await hub.OnConnectedAsync();
        await hub.JoinTeam(teamId);
        await hub.LeaveTeam(teamId);

        Assert.Equal(
            [
                new GroupOperation("Add", "connection", TeamRealtimeGroups.GetUserGroup(user.Id)),
                new GroupOperation("Add", "connection", teamGroup),
                new GroupOperation("Remove", "connection", teamGroup)
            ],
            groups.Operations);

        await manager.RevokeUserFromGroupAsync(user.Id, teamGroup);
        Assert.Equal(3, groups.Operations.Count);

        await hub.OnDisconnectedAsync(null);
        await manager.RevokeUserAsync(user.Id);
        Assert.Equal(3, groups.Operations.Count);
    }

    [Fact]
    public async Task JoinTeam_RevalidatesCurrentAuthorizationBeforeAddingGroup()
    {
        await using var dbContext = CreateDbContext();
        var user = CreateUser();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        var groups = new RecordingGroupManager();
        await using var provider = CreateProvider(groups);
        var manager = provider.GetRequiredService<IRealtimeConnectionManager>();
        var hub = CreateHub(dbContext, manager, groups, new StubAuthorizer(false), user);
        await hub.OnConnectedAsync();

        var exception = await Assert.ThrowsAsync<HubException>(() => hub.JoinTeam(Guid.NewGuid()));

        Assert.Equal("You are not allowed to subscribe to this team.", exception.Message);
        Assert.Single(groups.Operations);
    }

    [Fact]
    public async Task JoinTeam_RacingRevocation_AddsThenRemovesAffectedGroup()
    {
        await using var dbContext = CreateDbContext();
        var user = CreateUser();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        var groups = new RecordingGroupManager();
        await using var provider = CreateProvider(groups);
        var manager = provider.GetRequiredService<IRealtimeConnectionManager>();
        var authorizer = new BlockingAuthorizer();
        var hub = CreateHub(dbContext, manager, groups, authorizer, user);
        var teamId = Guid.NewGuid();
        var teamGroup = TeamRealtimeGroups.GetTeamGroup(teamId);
        await hub.OnConnectedAsync();

        var joinTask = hub.JoinTeam(teamId);
        try
        {
            await authorizer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var revokeTask = manager.RevokeUserFromGroupAsync(user.Id, teamGroup);
            Assert.False(revokeTask.IsCompleted);

            authorizer.Release.SetResult();
            await Task.WhenAll(joinTask, revokeTask).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            authorizer.Release.TrySetResult();
        }

        Assert.Equal(
            [
                new GroupOperation("Add", "connection", teamGroup),
                new GroupOperation("Remove", "connection", teamGroup)
            ],
            groups.Operations.Where(operation => operation.GroupName == teamGroup));
    }

    private static TeamManagementHub CreateHub(
        MercuriusDBContext dbContext,
        IRealtimeConnectionManager manager,
        IGroupManager groups,
        ITeamRealtimeAuthorizer authorizer,
        User user)
    {
        return new TeamManagementHub(
            new IdentityModuleFacade(new IdentityDbContextAdapter<MercuriusDBContext>(dbContext), null!, new Mercurius.Modules.Identity.Application.Services.AuthenticatedIdentityContext()),
            authorizer,
            manager)
        {
            Context = new TestHubCallerContext(
                "connection",
                new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user.Auth0UserId)], "Bearer"))),
            Groups = groups
        };
    }

    private static ServiceProvider CreateProvider(RecordingGroupManager groups)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRealtimeNotificationServices<TeamManagementHub>();
        services.AddSingleton<IHubContext<TeamManagementHub>>(new RecordingHubContext(groups));
        return services.BuildServiceProvider();
    }

    private static MercuriusDBContext CreateDbContext() => PostgresTestDatabase.CreateDbContext();

    private static User CreateUser() => new()
    {
        Id = Guid.NewGuid(),
        Auth0UserId = $"auth0|{Guid.NewGuid():N}",
        Username = $"user-{Guid.NewGuid().ToString("N")[..12]}",
        Firstname = "Realtime",
        Lastname = "User",
        Email = "realtime@example.test"
    };

    private sealed class StubAuthorizer(bool canSubscribe) : ITeamRealtimeAuthorizer
    {
        public Task<bool> CanSubscribeToTeamAsync(
            TeamId teamId,
            UserId userId,
            CancellationToken cancellationToken = default) => Task.FromResult(canSubscribe);
    }

    private sealed class BlockingAuthorizer : ITeamRealtimeAuthorizer
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<bool> CanSubscribeToTeamAsync(
            TeamId teamId,
            UserId userId,
            CancellationToken cancellationToken = default)
        {
            Entered.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return true;
        }
    }

    private sealed class RecordingHubContext(RecordingGroupManager groups) : IHubContext<TeamManagementHub>
    {
        public IHubClients Clients => throw new NotSupportedException();

        public IGroupManager Groups { get; } = groups;
    }

    private sealed class RecordingGroupManager : IGroupManager
    {
        public List<GroupOperation> Operations { get; } = [];

        public Task AddToGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default)
        {
            Operations.Add(new GroupOperation("Add", connectionId, groupName));
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default)
        {
            Operations.Add(new GroupOperation("Remove", connectionId, groupName));
            return Task.CompletedTask;
        }
    }

    private sealed class TestHubCallerContext(
        string connectionId,
        ClaimsPrincipal user) : HubCallerContext
    {
        private readonly CancellationTokenSource _connectionAborted = new();

        public override string ConnectionId { get; } = connectionId;

        public override string? UserIdentifier => null;

        public override ClaimsPrincipal User { get; } = user;

        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override CancellationToken ConnectionAborted => _connectionAborted.Token;

        public override void Abort() => _connectionAborted.Cancel();
    }

    private sealed class TestHub : Hub
    {
        public Task Ping() => Task.CompletedTask;
    }

    private sealed class RebindingIdentityTokenValidator(
        AuthenticatedIdentityContext identityContext,
        Guid replacementId) : IIdentityTokenValidator
    {
        public Task<bool> ValidateAndPinAsync(
            string subject,
            string? issuedAtUnixSeconds,
            bool allowDeletedSelfRead,
            CancellationToken cancellationToken = default)
        {
            identityContext.Pin(subject, replacementId, issuedAtUnixSeconds: issuedAtUnixSeconds);
            return Task.FromResult(true);
        }
    }

    private sealed record GroupOperation(string Action, string ConnectionId, string GroupName);
}
