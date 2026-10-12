using System.Text.Json;
using Mercurius.LAN.API.Data;
using Mercurius.Modules.Teams.DTOs;
using Mercurius.LAN.API.Migrations;
using Mercurius.Modules.Teams.Services;
using Mercurius.Modules.Teams.Infrastructure;
using Mercurius.Modules.Identity;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Identity.DTOs;
using Mercurius.Modules.Identity.Services;
using Mercurius.Modules.Identity.Services.Auth0;
using Mercurius.Modules.Media.Contracts;
using Mercurius.Modules.Tournament.Contracts;
using Mercurius.Modules.Teams.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mercurius.TestInfrastructure;
using Platform.Eventing;
using Platform.Eventing.Persistence;
using Platform.Extensions;
using Platform.Realtime;

namespace Platform.Tests;

public class ModuleEventingTests
{
    private static int _nextId;

    [Fact]
    public async Task Publisher_PersistsPendingOutboxMessage()
    {
        await using var dbContext = CreateDbContext();
        var publisher = new ModuleEventPublisher(dbContext);

        var messageId = publisher.Publish(new TestModuleEvent("alpha", 1));
        await dbContext.SaveChangesAsync();

        var message = await dbContext.OutboxMessages.SingleAsync();
        Assert.Equal(messageId, message.Id);
        Assert.Equal(typeof(TestModuleEvent).FullName, message.EventType);
        Assert.Contains("alpha", message.Payload, StringComparison.Ordinal);
        Assert.Null(message.ProcessedAtUtc);
        Assert.Equal(0, message.RetryCount);
    }

    [Fact]
    public async Task Dispatcher_InvokesHandlerAndMarksMessageProcessed()
    {
        var state = new HandlerState();
        await using var provider = CreateEventingProvider(
            state,
            services => services.AddModuleEventHandler<TestModuleEvent, RecordingHandler>());
        using var scope = provider.CreateScope();

        await PublishTestEventAsync(scope.ServiceProvider);
        var processed = await scope.ServiceProvider
            .GetRequiredService<IModuleEventDispatcher>()
            .DispatchPendingAsync();

        var dbContext = scope.ServiceProvider.GetRequiredService<MercuriusDBContext>();
        var outbox = await dbContext.OutboxMessages.SingleAsync();
        Assert.Equal(1, processed);
        Assert.NotNull(outbox.ProcessedAtUtc);
        Assert.Single(await dbContext.InboxMessages.ToListAsync());
        Assert.Equal(1, state.RecordingHandlerCalls);
    }

    [Fact]
    public async Task Dispatcher_InvokesOnlyHandlersForThePublishedEventType()
    {
        var state = new HandlerState();
        await using var provider = CreateEventingProvider(
            state,
            services =>
            {
                services.AddModuleEventHandler<TestModuleEvent, RecordingHandler>();
                services.AddModuleEventHandler<OtherModuleEvent, OtherRecordingHandler>();
            });
        using var scope = provider.CreateScope();

        await PublishTestEventAsync(scope.ServiceProvider);
        await scope.ServiceProvider
            .GetRequiredService<IModuleEventDispatcher>()
            .DispatchPendingAsync();

        Assert.Equal(1, state.RecordingHandlerCalls);
        Assert.Equal(0, state.OtherRecordingHandlerCalls);
    }

    [Fact]
    public async Task Dispatcher_RecordsRetryStateWhenHandlerFails()
    {
        var state = new HandlerState { ThrowAlways = true };
        await using var provider = CreateEventingProvider(
            state,
            services => services.AddModuleEventHandler<TestModuleEvent, ThrowingHandler>());
        using var scope = provider.CreateScope();

        await PublishTestEventAsync(scope.ServiceProvider);
        var processed = await scope.ServiceProvider
            .GetRequiredService<IModuleEventDispatcher>()
            .DispatchPendingAsync();

        var dbContext = scope.ServiceProvider.GetRequiredService<MercuriusDBContext>();
        var outbox = await dbContext.OutboxMessages.SingleAsync();
        Assert.Equal(0, processed);
        Assert.Null(outbox.ProcessedAtUtc);
        Assert.Equal(1, outbox.RetryCount);
        Assert.Contains("planned handler failure", outbox.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispatcher_DoesNotCommitFailedHandlerEntityChangesWithRetryState()
    {
        var user = CreateUser();
        var originalUsername = user.Username;
        var state = new HandlerState { UserId = user.Id };
        await using var provider = CreateEventingProvider(
            state,
            services => services.AddModuleEventHandler<TestModuleEvent, MutatingThrowingHandler>());
        using var scope = provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MercuriusDBContext>();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        await PublishTestEventAsync(scope.ServiceProvider);
        var processed = await scope.ServiceProvider
            .GetRequiredService<IModuleEventDispatcher>()
            .DispatchPendingAsync();

        dbContext.ChangeTracker.Clear();
        var outbox = await dbContext.OutboxMessages.SingleAsync();
        var persistedUser = await dbContext.Users.SingleAsync(storedUser => storedUser.Id == user.Id);
        Assert.Equal(0, processed);
        Assert.Equal(1, outbox.RetryCount);
        Assert.Null(outbox.ProcessedAtUtc);
        Assert.Equal(originalUsername, persistedUser.Username);
    }

    [Fact]
    public async Task Dispatcher_SkipsConsumerThatAlreadyProcessedMessage()
    {
        var state = new HandlerState();
        await using var provider = CreateEventingProvider(
            state,
            services => services.AddModuleEventHandler<TestModuleEvent, RecordingHandler>());
        using var scope = provider.CreateScope();

        var messageId = await PublishTestEventAsync(scope.ServiceProvider);
        var dbContext = scope.ServiceProvider.GetRequiredService<MercuriusDBContext>();
        dbContext.InboxMessages.Add(new InboxMessage
        {
            ConsumerName = RecordingHandler.Name,
            MessageId = messageId,
            ProcessedAtUtc = DateTime.UtcNow
        });
        await dbContext.SaveChangesAsync();

        await scope.ServiceProvider.GetRequiredService<IModuleEventDispatcher>().DispatchPendingAsync();

        Assert.Equal(0, state.RecordingHandlerCalls);
        Assert.NotNull((await dbContext.OutboxMessages.SingleAsync()).ProcessedAtUtc);
    }

    [Fact]
    public async Task Dispatcher_RetryDoesNotRepeatPreviouslyCompletedConsumer()
    {
        var state = new HandlerState { ThrowOnce = true };
        await using var provider = CreateEventingProvider(
            state,
            services =>
            {
                services.AddModuleEventHandler<TestModuleEvent, RecordingHandler>();
                services.AddModuleEventHandler<TestModuleEvent, FlakyHandler>();
            });
        using var scope = provider.CreateScope();

        await PublishTestEventAsync(scope.ServiceProvider);
        await scope.ServiceProvider.GetRequiredService<IModuleEventDispatcher>().DispatchPendingAsync();
        var dbContext = scope.ServiceProvider.GetRequiredService<MercuriusDBContext>();
        var failedMessage = await dbContext.OutboxMessages.SingleAsync();
        failedMessage.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await dbContext.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<IModuleEventDispatcher>().DispatchPendingAsync();

        var outbox = await dbContext.OutboxMessages.SingleAsync();
        Assert.Equal(1, state.RecordingHandlerCalls);
        Assert.Equal(2, state.FlakyHandlerCalls);
        Assert.Equal(1, outbox.RetryCount);
        Assert.NotNull(outbox.ProcessedAtUtc);
        Assert.Equal(2, await dbContext.InboxMessages.CountAsync());
    }

    [Fact]
    public async Task Dispatcher_ResolvesLegacyTournamentTypeAndGameIdPayload()
    {
        var state = new LegacyTournamentEventState();
        await using var provider = CreateEventingProvider(
            state,
            services => services.AddModuleEventHandler<TournamentCreatedIntegrationEvent, LegacyTournamentCreatedHandler>());
        using var scope = provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MercuriusDBContext>();
        var tournamentId = Guid.NewGuid();
        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            EventType = "Mercurius.Modules.Competition.Contracts.GameCreatedIntegrationEvent, Mercurius.Modules.Competition.Contracts",
            Payload = JsonSerializer.Serialize(
                new { gameId = new Mercurius.Modules.Shared.TournamentId(tournamentId), name = "Legacy tournament" },
                serializerOptions),
            OccurredAtUtc = DateTime.UtcNow,
            NextAttemptAtUtc = DateTime.UtcNow
        });
        await dbContext.SaveChangesAsync();

        var processed = await scope.ServiceProvider
            .GetRequiredService<IModuleEventDispatcher>()
            .DispatchPendingAsync();

        Assert.Equal(1, processed);
        Assert.Equal(tournamentId, state.TournamentId);
        Assert.Equal("Legacy tournament", state.Name);
    }

    [Fact]
    public void ProjectionVersionGuard_IdentifiesOnlyOlderVersionsAsStale()
    {
        Assert.True(ProjectionVersionGuard.IsStale(incomingVersion: 2, storedVersion: 3));
        Assert.False(ProjectionVersionGuard.IsStale(incomingVersion: 3, storedVersion: 3));
        Assert.False(ProjectionVersionGuard.IsStale(incomingVersion: 4, storedVersion: 3));
    }

    [Fact]
    public async Task Dispatcher_TestProjectionIgnoresStaleTeamVersion()
    {
        var projection = new TeamProjectionState { Name = "Newer name", Version = 2 };
        var services = new ServiceCollection();
        services.AddLogging();
        var database = PostgresTestDatabase.Create();
        services.AddDbContext<MercuriusDBContext>(options =>
            options.UseNpgsql(database.ConnectionString));
        services.AddSingleton<PostgresTestDatabaseLease>(_ => database);
        services.AddSingleton(projection);
        services.AddModuleEventing<MercuriusDBContext>();
        services.AddModuleEventHandler<TeamRenamedIntegrationEvent, TeamRenamedProjectionHandler>();
        await using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<PostgresTestDatabaseLease>();
        using (var initializationScope = provider.CreateScope())
            PostgresTestDatabase.Initialize(initializationScope.ServiceProvider.GetRequiredService<MercuriusDBContext>());
        using var scope = provider.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IModuleEventPublisher>();
        var dbContext = scope.ServiceProvider.GetRequiredService<MercuriusDBContext>();

        publisher.Publish(new TeamRenamedIntegrationEvent(new Mercurius.Modules.Shared.TeamId(Guid.NewGuid()), 1, "Older name"));
        await dbContext.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<IModuleEventDispatcher>().DispatchPendingAsync();

        Assert.Equal("Newer name", projection.Name);
        Assert.Equal(2, projection.Version);
    }

    [Fact]
    public void ModuleEventingMigration_AddsPlatformOutboxInboxAndTeamVersion()
    {
        var migration = new ModuleEventingInfrastructure();
        var operations = migration.UpOperations.ToList();

        Assert.Contains(operations, operation =>
            operation is EnsureSchemaOperation ensureSchema &&
            ensureSchema.Name == "platform");
        Assert.Contains(operations, operation =>
            operation is AddColumnOperation addColumn &&
            addColumn.Table == "Teams" &&
            addColumn.Name == "Version");
        Assert.Contains(operations, operation =>
            operation is CreateTableOperation createTable &&
            createTable.Schema == "platform" &&
            createTable.Name == "outbox_messages");
        Assert.Contains(operations, operation =>
            operation is CreateTableOperation createTable &&
            createTable.Schema == "platform" &&
            createTable.Name == "inbox_messages" &&
            createTable.PrimaryKey?.Columns.SequenceEqual(["consumer_name", "message_id"]) == true);
    }

    [Fact]
    public async Task TeamsLifecycleMutations_IncrementVersionAndEnqueueDurableEvents()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var invited = CreateUser();
        dbContext.Users.AddRange(captain, invited);
        await dbContext.SaveChangesAsync();
        var teamService = CreateTeamService(dbContext);

        var created = await teamService.CreateCurrentUserTeamAsync(captain.Auth0UserId, new CreateTeamDTO { Name = "Alpha" });
        await teamService.InviteUserAsync(captain.Auth0UserId, created.Id, invited.Id);
        await teamService.RespondToInviteAsync(invited.Auth0UserId, (await dbContext.Set<TeamInvite>().SingleAsync()).Id, true);
        await teamService.UpdateTeamAsync(created.Id, new UpdateTeamDTO { Name = "Bravo" });
        await teamService.RemoveMemberAsync(captain.Auth0UserId, created.Id, invited.Id);
        await teamService.DeleteTeamAsync(captain.Auth0UserId, created.Id);

        var team = await dbContext.Teams.IgnoreQueryFilters().SingleAsync(team => team.Id == created.Id);
        var eventTypes = await dbContext.OutboxMessages
            .OrderBy(message => message.OccurredAtUtc)
            .ThenBy(message => message.Id)
            .Select(message => message.EventType)
            .ToListAsync();

        Assert.Equal(5, team.Version);
        Assert.Contains(typeof(TeamCreatedIntegrationEvent).FullName!, eventTypes);
        Assert.Contains(typeof(TeamMemberAddedIntegrationEvent).FullName!, eventTypes);
        Assert.Contains(typeof(TeamRenamedIntegrationEvent).FullName!, eventTypes);
        Assert.Contains(typeof(TeamMemberRemovedIntegrationEvent).FullName!, eventTypes);
        Assert.Contains(typeof(TeamDeletedIntegrationEvent).FullName!, eventTypes);
    }

    [Fact]
    public async Task TransferCaptainAsync_IncrementsVersionAndEnqueuesDurableEvent()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var newCaptain = CreateUser();
        var team = new Team("Alpha", captain.Id) { Id = Guid.NewGuid() };
        team.AddMember(captain.Id);
        team.AddMember(newCaptain.Id);
        dbContext.Users.AddRange(captain, newCaptain);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        var teamService = CreateTeamService(dbContext);

        await teamService.TransferCaptainAsync(captain.Auth0UserId, team.Id, newCaptain.Id);

        Assert.Equal(1, team.Version);
        var outbox = await dbContext.OutboxMessages.SingleAsync();
        Assert.Equal(typeof(TeamCaptainTransferredIntegrationEvent).FullName, outbox.EventType);
    }

    [Fact]
    public async Task UserProfileUpdate_EnqueuesDurableProfileEvents()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var user = CreateUser();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        var userService = CreateUserService(dbContext);

        await userService.UpdateCurrentUserAsync(user.Auth0UserId, new UpdateUserProfileRequest
        {
            Username = "updateduser",
            Firstname = "Updated",
            Lastname = "User"
        });

        var eventTypes = await dbContext.OutboxMessages
            .Select(message => message.EventType)
            .ToListAsync();

        Assert.Contains(typeof(UserProfileChangedIntegrationEvent).FullName!, eventTypes);
        Assert.Contains(typeof(UsernameChangedIntegrationEvent).FullName!, eventTypes);
    }

    [Fact]
    public async Task UserAnonymize_EnqueuesDurableDeletionEvents()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var user = CreateUser();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        var userService = CreateUserService(dbContext);

        await userService.AnonymizeCurrentUserAsync(user.Auth0UserId);

        var eventTypes = await dbContext.OutboxMessages
            .Select(message => message.EventType)
            .ToListAsync();

        Assert.Contains(typeof(UserAnonymizedIntegrationEvent).FullName!, eventTypes);
        Assert.Contains(typeof(UserDeletedIntegrationEvent).FullName!, eventTypes);
        Assert.Contains(typeof(UserProfileChangedIntegrationEvent).FullName!, eventTypes);
    }

    [Fact]
    public async Task AccountDeletionEntryPoints_RevokeUserGroupsOnlyForFirstDeletion()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var currentUser = CreateUser();
        var usernameDeletedUser = CreateUser();
        var idDeletedUser = CreateUser();
        usernameDeletedUser.NormalizedUsername = usernameDeletedUser.Username!.ToLowerInvariant();
        dbContext.Users.AddRange(currentUser, usernameDeletedUser, idDeletedUser);
        await dbContext.SaveChangesAsync();
        var realtimeConnectionManager = new RecordingRealtimeConnectionManager();
        var userService = CreateUserService(
            dbContext,
            realtimeConnectionManager: realtimeConnectionManager);

        await userService.AnonymizeCurrentUserAsync(currentUser.Auth0UserId);
        await userService.DeleteUserAsync(usernameDeletedUser.Username!);
        await userService.DeleteUserByIdAsync(idDeletedUser.Id);
        await userService.AnonymizeCurrentUserAsync(currentUser.Auth0UserId);

        Assert.Equal(
            [currentUser.Id, usernameDeletedUser.Id, idDeletedUser.Id],
            realtimeConnectionManager.UserRevocations.Select(revocation => revocation.UserId));
        Assert.All(
            realtimeConnectionManager.UserRevocations,
            revocation => Assert.Equal(CancellationToken.None, revocation.CancellationToken));
    }

    [Fact]
    public async Task AccountDeletion_DoesNotRevokeWhenDurableEventPublicationFails()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var user = CreateUser();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        var originalUser = await dbContext.Users
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == user.Id);
        var realtimeConnectionManager = new RecordingRealtimeConnectionManager();
        var userService = CreateUserService(
            dbContext,
            new ThrowingModuleEventPublisher(),
            realtimeConnectionManager);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            userService.AnonymizeCurrentUserAsync(user.Auth0UserId));

        dbContext.ChangeTracker.Clear();
        var persistedUser = await dbContext.Users
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == user.Id);
        Assert.Equal(originalUser.Auth0UserId, persistedUser.Auth0UserId);
        Assert.Equal(originalUser.Username, persistedUser.Username);
        Assert.Equal(originalUser.NormalizedUsername, persistedUser.NormalizedUsername);
        Assert.Equal(originalUser.Firstname, persistedUser.Firstname);
        Assert.Equal(originalUser.Lastname, persistedUser.Lastname);
        Assert.Equal(originalUser.Email, persistedUser.Email);
        Assert.Equal(originalUser.EmailVerified, persistedUser.EmailVerified);
        Assert.Equal(originalUser.DiscordId, persistedUser.DiscordId);
        Assert.Equal(originalUser.SteamId, persistedUser.SteamId);
        Assert.Equal(originalUser.RiotId, persistedUser.RiotId);
        Assert.False(persistedUser.IsDeleted);
        Assert.Null(persistedUser.DeletedAtUtc);
        Assert.Equal(originalUser.CreatedAtUtc, persistedUser.CreatedAtUtc);
        Assert.Equal(originalUser.UpdatedAtUtc, persistedUser.UpdatedAtUtc);
        Assert.Empty(await dbContext.OutboxMessages.ToListAsync());
        Assert.Empty(realtimeConnectionManager.UserRevocations);
    }

    [Fact]
    public async Task AccountDeletion_PostCommitRevocationFailureLeavesDeletionAndOutboxCommitted()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var user = CreateUser();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        var realtimeConnectionManager = new RecordingRealtimeConnectionManager
        {
            ThrowOnRevocation = true
        };
        var userService = CreateUserService(
            dbContext,
            realtimeConnectionManager: realtimeConnectionManager);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            userService.AnonymizeCurrentUserAsync(user.Auth0UserId));

        Assert.Equal("planned post-commit revocation failure", exception.Message);
        dbContext.ChangeTracker.Clear();
        Assert.True((await dbContext.Users.SingleAsync(candidate => candidate.Id == user.Id)).IsDeleted);
        Assert.Contains(
            await dbContext.OutboxMessages.Select(message => message.EventType).ToListAsync(),
            eventType => eventType == typeof(UserDeletedIntegrationEvent).FullName);
    }

    private static async Task<Guid> PublishTestEventAsync(IServiceProvider serviceProvider)
    {
        var publisher = serviceProvider.GetRequiredService<IModuleEventPublisher>();
        var dbContext = serviceProvider.GetRequiredService<MercuriusDBContext>();
        var messageId = publisher.Publish(new TestModuleEvent("alpha", 1));
        await dbContext.SaveChangesAsync();
        return messageId;
    }

    private static ServiceProvider CreateEventingProvider<TState>(
        TState state,
        Action<IServiceCollection> configureHandlers)
        where TState : class
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var database = PostgresTestDatabase.Create();
        services.AddDbContext<MercuriusDBContext>(options =>
            options.UseNpgsql(database.ConnectionString));
        services.AddSingleton<PostgresTestDatabaseLease>(_ => database);
        services.AddSingleton(state);
        services.AddModuleEventing<MercuriusDBContext>();
        configureHandlers(services);

        var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<PostgresTestDatabaseLease>();
        using var scope = provider.CreateScope();
        PostgresTestDatabase.Initialize(scope.ServiceProvider.GetRequiredService<MercuriusDBContext>());
        return provider;
    }

    private static MercuriusDBContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new MercuriusDBContext(options);
    }

    private static TeamEventPublishingDecorator CreateTeamService(MercuriusDBContext dbContext)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TeamInvite:ResendCooldownDays"] = "7",
                ["TeamInvite:ExpirationDays"] = "14",
                ["TeamInvite:RetentionDays"] = "90",
                ["TeamInvite:DeclinedResendLimit"] = "3"
            })
            .Build();
        var moduleEventPublisher = new ModuleEventPublisher(dbContext);
        var identityModule = new IdentityModuleFacade(dbContext, new NoopAuth0ManagementService());
        var teamsDbContext = new TeamsDbContextAdapter<MercuriusDBContext>(dbContext);

        return new TeamEventPublishingDecorator(
            new TeamService(
                teamsDbContext,
                configuration,
                identityModule,
                new NoopMediaModule(),
                new NoopTeamTournamentReadService(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<TeamService>.Instance),
            teamsDbContext,
            identityModule,
            new NoopTeamEventPublisher(),
            moduleEventPublisher,
            new NoopRealtimeConnectionManager());
    }

    private static IUserService CreateUserService(
        MercuriusDBContext dbContext,
        IModuleEventPublisher? moduleEventPublisher = null,
        IRealtimeConnectionManager? realtimeConnectionManager = null)
    {
        return new UserIntegrationEventPublishingService(
            new UserService(dbContext, new NoopAuth0ManagementService(), new IdentityModuleFacade(dbContext, new NoopAuth0ManagementService())),
            dbContext,
            moduleEventPublisher ?? new ModuleEventPublisher(dbContext),
            realtimeConnectionManager ?? new NoopRealtimeConnectionManager());
    }

    private static User CreateUser()
    {
        var id = Interlocked.Increment(ref _nextId);
        return new User
        {
            Id = Guid.NewGuid(),
            Auth0UserId = $"auth0|event-user{id}",
            Username = $"event-user{id}",
            Firstname = $"Event{id}",
            Lastname = $"User{id}",
            Email = $"event-user{id}@example.com"
        };
    }

    private sealed record TestModuleEvent(string Name, long Version);

    private sealed record OtherModuleEvent(string Name);

    private sealed class NoopAuth0ManagementService : IAuth0ManagementService
    {
        public Task<Auth0ProfileSnapshot> GetUserProfileAsync(
            string auth0UserId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new Auth0ProfileSnapshot(null, null, false));
        }

        public Task SendVerificationEmailAsync(string auth0UserId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendPasswordResetEmailAsync(string email, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NoopMediaModule : IMediaModule
    {
        public Task<StoredMediaAsset> SaveImageAsync(MediaUpload upload, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StoredMediaAsset("https://example.test/team-logo.webp"));

        public Task DeleteImageAsync(string? mediaUrl, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoopTeamTournamentReadService : ITeamTournamentReadService
    {
        public Task<IReadOnlyList<PublicTeamTournamentSummary>> GetPublicTeamTournamentsAsync(Guid teamId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PublicTeamTournamentSummary>>([]);

        public Task<bool> IsUserInProtectedTournamentRosterAsync(Guid teamId, Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> IsTeamInDeleteBlockingTournamentAsync(Guid teamId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> IsTeamLogoReferencedAsync(string logoUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class NoopTeamEventPublisher : ITeamEventPublisher
    {
        public Task InviteChangedAsync(Guid teamId, Guid inviteId, Guid affectedUserId, string status, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MembershipChangedAsync(Guid teamId, Guid affectedUserId, string action, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CaptainTransferredAsync(Guid teamId, Guid newCaptainUserId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ThrowingModuleEventPublisher : IModuleEventPublisher
    {
        public Guid Publish<TPayload>(TPayload payload, DateTime? occurredAtUtc = null)
            where TPayload : notnull => throw new InvalidOperationException("planned durable event publication failure");
    }

    private sealed class HandlerState
    {
        public bool ThrowAlways { get; set; }
        public bool ThrowOnce { get; set; }
        public Guid UserId { get; set; }
        public int RecordingHandlerCalls { get; set; }
        public int OtherRecordingHandlerCalls { get; set; }
        public int FlakyHandlerCalls { get; set; }
    }

    private sealed class TeamProjectionState
    {
        public string Name { get; set; } = string.Empty;
        public long Version { get; set; }
    }

    private sealed class LegacyTournamentEventState
    {
        public Guid TournamentId { get; set; }
        public string? Name { get; set; }
    }

    private sealed class LegacyTournamentCreatedHandler : IModuleEventHandler<TournamentCreatedIntegrationEvent>
    {
        private readonly LegacyTournamentEventState _state;

        public LegacyTournamentCreatedHandler(LegacyTournamentEventState state)
        {
            _state = state;
        }

        public string ConsumerName => "legacy-tournament-created-consumer";

        public Task HandleAsync(
            TournamentCreatedIntegrationEvent payload,
            ModuleEventContext context,
            CancellationToken cancellationToken = default)
        {
            _state.TournamentId = payload.TournamentId.Value;
            _state.Name = payload.Name;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHandler : IModuleEventHandler<TestModuleEvent>
    {
        public const string Name = "recording-consumer";
        private readonly HandlerState _state;

        public RecordingHandler(HandlerState state)
        {
            _state = state;
        }

        public string ConsumerName => Name;

        public Task HandleAsync(TestModuleEvent payload, ModuleEventContext context, CancellationToken cancellationToken = default)
        {
            _state.RecordingHandlerCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingHandler : IModuleEventHandler<TestModuleEvent>
    {
        private readonly HandlerState _state;

        public ThrowingHandler(HandlerState state)
        {
            _state = state;
        }

        public string ConsumerName => "throwing-consumer";

        public Task HandleAsync(TestModuleEvent payload, ModuleEventContext context, CancellationToken cancellationToken = default)
        {
            if (_state.ThrowAlways)
                throw new InvalidOperationException("planned handler failure");

            return Task.CompletedTask;
        }
    }

    private sealed class OtherRecordingHandler : IModuleEventHandler<OtherModuleEvent>
    {
        private readonly HandlerState _state;

        public OtherRecordingHandler(HandlerState state)
        {
            _state = state;
        }

        public string ConsumerName => "other-recording-consumer";

        public Task HandleAsync(OtherModuleEvent payload, ModuleEventContext context, CancellationToken cancellationToken = default)
        {
            _state.OtherRecordingHandlerCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FlakyHandler : IModuleEventHandler<TestModuleEvent>
    {
        private readonly HandlerState _state;

        public FlakyHandler(HandlerState state)
        {
            _state = state;
        }

        public string ConsumerName => "flaky-consumer";

        public Task HandleAsync(TestModuleEvent payload, ModuleEventContext context, CancellationToken cancellationToken = default)
        {
            _state.FlakyHandlerCalls++;
            if (_state.ThrowOnce)
            {
                _state.ThrowOnce = false;
                throw new InvalidOperationException("planned handler failure");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class MutatingThrowingHandler : IModuleEventHandler<TestModuleEvent>
    {
        private readonly MercuriusDBContext _dbContext;
        private readonly HandlerState _state;

        public MutatingThrowingHandler(MercuriusDBContext dbContext, HandlerState state)
        {
            _dbContext = dbContext;
            _state = state;
        }

        public string ConsumerName => "mutating-throwing-consumer";

        public async Task HandleAsync(TestModuleEvent payload, ModuleEventContext context, CancellationToken cancellationToken = default)
        {
            var user = await _dbContext.Users.SingleAsync(user => user.Id == _state.UserId, cancellationToken);
            user.Username = "mutated-before-failure";
            throw new InvalidOperationException("planned handler failure after entity mutation");
        }
    }

    private sealed class TeamRenamedProjectionHandler : IModuleEventHandler<TeamRenamedIntegrationEvent>
    {
        private readonly TeamProjectionState _projection;

        public TeamRenamedProjectionHandler(TeamProjectionState projection)
        {
            _projection = projection;
        }

        public string ConsumerName => "test-team-projection";

        public Task HandleAsync(TeamRenamedIntegrationEvent payload, ModuleEventContext context, CancellationToken cancellationToken = default)
        {
            if (ProjectionVersionGuard.IsStale(payload.Version, _projection.Version))
                return Task.CompletedTask;

            _projection.Name = payload.Name;
            _projection.Version = payload.Version;
            return Task.CompletedTask;
        }
    }
}
