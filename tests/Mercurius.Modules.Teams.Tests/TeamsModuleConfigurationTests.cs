using System.Reflection;
using Mercurius.LAN.API.Data;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Media.Contracts;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Teams;
using Mercurius.Modules.Teams.Contracts;
using Mercurius.Modules.Teams.Application.DTOs;
using Mercurius.Modules.Teams.Infrastructure;
using Mercurius.Modules.Teams.Application.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Platform.Eventing;
using Platform.Realtime;

namespace Mercurius.Modules.Teams.Tests;

public class TeamsModuleConfigurationTests
{
    [Fact]
    public void AddTeamsModule_RegistersExpectedLifetimesAndEventPublishingServices()
    {
        var services = CreateServiceCollection();

        AssertLifetime<ITeamsDbContext>(services, ServiceLifetime.Scoped);
        AssertLifetime<ITeamsModule>(services, ServiceLifetime.Transient);
        AssertLifetime<TeamService>(services, ServiceLifetime.Scoped);
        AssertLifetime<ITeamQueries>(services, ServiceLifetime.Scoped);
        AssertLifetime<ITeamLogoCommands>(services, ServiceLifetime.Scoped);
        AssertLifetime<TeamEventPublishingDecorator>(services, ServiceLifetime.Scoped);
        AssertLifetime<ITeamManagementCommands>(services, ServiceLifetime.Scoped);
        AssertLifetime<ITeamInviteWorkflows>(services, ServiceLifetime.Scoped);
        AssertLifetime<TeamInviteMaintenanceService>(services, ServiceLifetime.Scoped);
        AssertLifetime<ITeamEndpointService>(services, ServiceLifetime.Transient);
        AssertLifetime<ITeamEventPublisher>(services, ServiceLifetime.Transient);
        AssertLifetime<Mercurius.Modules.Teams.Contracts.ITeamRealtimeAuthorizer>(services, ServiceLifetime.Transient);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(TeamInviteMaintenanceWorker) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var endpointService = Assert.IsType<TeamEndpointService>(
            scope.ServiceProvider.GetRequiredService<ITeamEndpointService>());
        var queries = scope.ServiceProvider.GetRequiredService<ITeamQueries>();
        var logoCommands = scope.ServiceProvider.GetRequiredService<ITeamLogoCommands>();
        var managementCommands = scope.ServiceProvider.GetRequiredService<ITeamManagementCommands>();
        var inviteWorkflows = scope.ServiceProvider.GetRequiredService<ITeamInviteWorkflows>();
        var teamsModule = scope.ServiceProvider.GetRequiredService<ITeamsModule>();

        Assert.IsType<TeamService>(queries);
        Assert.Same(queries, logoCommands);
        var decorator = Assert.IsType<TeamEventPublishingDecorator>(managementCommands);
        Assert.Same(decorator, inviteWorkflows);
        Assert.IsType<TeamsModuleFacade>(teamsModule);
        var maintenanceOptions = provider.GetRequiredService<IOptions<TeamInviteMaintenanceOptions>>().Value;

        Assert.Equal(25, maintenanceOptions.MaintenanceBatchSize);
        Assert.Equal(120, maintenanceOptions.MaintenanceIntervalSeconds);
        Assert.Equal(3, maintenanceOptions.MaintenanceEventConcurrency);

        var innerService = typeof(TeamEventPublishingDecorator)
            .GetField("_inner", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(decorator);

        Assert.Same(queries, innerService);
        Assert.Same(queries, GetPrivateField(endpointService, "_queries"));
        Assert.Same(decorator, GetPrivateField(endpointService, "_managementCommands"));
        Assert.Same(decorator, GetPrivateField(endpointService, "_inviteWorkflows"));
        Assert.Same(logoCommands, GetPrivateField(endpointService, "_logoCommands"));
    }

    private static object? GetPrivateField(object instance, string fieldName)
    {
        return instance.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(instance);
    }

    [Fact]
    public void AddTeamsModule_UsesScopedAdapterAndKeepsHostDbContextOutsideTeamsContract()
    {
        var services = CreateServiceCollection();
        using var provider = services.BuildServiceProvider();

        using var firstScope = provider.CreateScope();
        var firstAdapter = firstScope.ServiceProvider.GetRequiredService<ITeamsDbContext>();
        var firstAdapterAgain = firstScope.ServiceProvider.GetRequiredService<ITeamsDbContext>();

        using var secondScope = provider.CreateScope();
        var secondAdapter = secondScope.ServiceProvider.GetRequiredService<ITeamsDbContext>();

        Assert.IsType<TeamsDbContextAdapter<MercuriusDBContext>>(firstAdapter);
        Assert.Same(firstAdapter, firstAdapterAgain);
        Assert.NotSame(firstAdapter, secondAdapter);
        Assert.DoesNotContain(typeof(ITeamsDbContext), typeof(MercuriusDBContext).GetInterfaces());
    }

    [Fact]
    public void AddTeamsModule_RequiresAnExplicitTournamentReadContract()
    {
        var services = new ServiceCollection();
        services.AddTeamsModule<MercuriusDBContext>(CreateConfiguration());

        Assert.Equal(1, services.Count(descriptor => descriptor.ServiceType == typeof(ITeamsDbContext)));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(ITeamTournamentReadService));
    }

    [Fact]
    public void AddTeamsModule_RejectsMaintenanceEventConcurrencyAboveBatchSize()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TeamInvite:RetentionDays"] = "90",
                ["TeamInvite:MaintenanceBatchSize"] = "2",
                ["TeamInvite:MaintenanceIntervalSeconds"] = "60",
                ["TeamInvite:MaintenanceEventConcurrency"] = "3"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddTeamsModule<MercuriusDBContext>(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<TeamInviteMaintenanceOptions>>().Value);
    }

    [Fact]
    public void TeamsAsyncInterfaces_RequireTrailingCancellationToken()
    {
        var interfaces = new[]
        {
            typeof(ITeamsModule),
            typeof(ITeamTournamentReadService),
            typeof(ITeamEventPublisher),
            typeof(Mercurius.Modules.Teams.Contracts.ITeamRealtimeAuthorizer),
            typeof(ITeamEndpointService)
        };

        foreach (var interfaceType in interfaces)
        {
            var asyncMethods = interfaceType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(method => typeof(Task).IsAssignableFrom(method.ReturnType));

            foreach (var method in asyncMethods)
            {
                var parameters = method.GetParameters();
                Assert.Equal(typeof(CancellationToken), parameters.Last().ParameterType);
                Assert.True(parameters.Last().HasDefaultValue, $"{interfaceType.Name}.{method.Name} should default its cancellation token.");
            }
        }
    }

    [Fact]
    public async Task RealtimeTeamEventPublisher_ForwardsCancellationToken()
    {
        var realtimePublisher = new RecordingRealtimePublisher();
        var publisher = new RealtimeTeamEventPublisher(realtimePublisher);
        using var cancellationSource = new CancellationTokenSource();

        await publisher.InviteChangedAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Pending", cancellationSource.Token);

        Assert.Equal(cancellationSource.Token, realtimePublisher.LastCancellationToken);
    }

    [Fact]
    public async Task TeamsModuleFacade_PreCancelledRead_ThrowsOperationCanceledException()
    {
        var services = CreateServiceCollection();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var teamsModule = scope.ServiceProvider.GetRequiredService<ITeamsModule>();
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            teamsModule.GetTeamSummaryAsync(new TeamId(Guid.NewGuid()), cancellationSource.Token));
    }

    private static ServiceCollection CreateServiceCollection()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddDbContext<MercuriusDBContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSingleton<IIdentityModule, StubIdentityModule>();
        services.AddSingleton<IMediaModule, NoopMediaModule>();
        services.AddSingleton<ITeamTournamentReadService, NoopTeamTournamentReadService>();
        services.AddSingleton<IModuleEventPublisher, NoopModuleEventPublisher>();
        services.AddSingleton<IRealtimePublisher, RecordingRealtimePublisher>();
        services.AddSingleton<IRealtimeConnectionManager, NoopRealtimeConnectionManager>();
        services.AddLogging();
        services.AddTeamsModule<MercuriusDBContext>(configuration);

        return services;
    }

    private static IConfiguration CreateConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TeamInvite:ResendCooldownDays"] = "7",
                ["TeamInvite:ExpirationDays"] = "14",
                ["TeamInvite:RetentionDays"] = "90",
                ["TeamInvite:DeclinedResendLimit"] = "3",
                ["TeamInvite:MaintenanceBatchSize"] = "25",
                ["TeamInvite:MaintenanceIntervalSeconds"] = "120",
                ["TeamInvite:MaintenanceEventConcurrency"] = "3",
                ["FileStorage:MaxFileSizeInMB"] = "2",
                ["FileStorage:Location"] = Path.Combine(Path.GetTempPath(), "teams-module-tests")
            })
            .Build();
    }

    private static void AssertLifetime<TService>(IServiceCollection services, ServiceLifetime expectedLifetime)
    {
        var descriptor = services.Last(service => service.ServiceType == typeof(TService));
        Assert.Equal(expectedLifetime, descriptor.Lifetime);
    }

    private sealed class RecordingRealtimePublisher : IRealtimePublisher
    {
        public CancellationToken LastCancellationToken { get; private set; }

        public Task PublishAsync<TPayload>(
            RealtimePublishRequest<TPayload> request,
            CancellationToken cancellationToken = default)
        {
            LastCancellationToken = cancellationToken;
            return Task.CompletedTask;
        }
    }

    private sealed class StubIdentityModule : IIdentityModule
    {
        public Task<UserProfileSummary?> GetUserProfileAsync(UserId userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<UserProfileSummary?>(new UserProfileSummary(userId, "captain", "captain", false, null, null, null));
        }

        public Task<UserProfileSummary?> GetUserProfileByAuth0IdAsync(string auth0UserId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<UserProfileSummary?>(new UserProfileSummary(new UserId(Guid.NewGuid()), "captain", "captain", false, null, null, null));
        }

        public Task<PublicUserProfileSummary?> GetPublicProfileByUsernameAsync(string username, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<PublicUserProfileSummary?>(null);
        }

        public Task<PublicUserProfileSummary?> GetPublicProfileByIdAsync(UserId userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<PublicUserProfileSummary?>(null);
        }

        public Task<IReadOnlyDictionary<UserId, UserProfileSummary>> GetUsersByIdsAsync(
            IReadOnlyCollection<UserId> userIds,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyDictionary<UserId, UserProfileSummary> users = new Dictionary<UserId, UserProfileSummary>();
            return Task.FromResult(users);
        }

        public Task<IReadOnlyDictionary<UserId, string>> GetPublicUsernamesByIdsAsync(
            IReadOnlyCollection<UserId> userIds,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyDictionary<UserId, string> users = new Dictionary<UserId, string>();
            return Task.FromResult(users);
        }

        public Task<IReadOnlyList<PublicUserSearchDocument>> GetPublicUserSearchDocumentsPageAsync(
            UserId? afterId,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PublicUserSearchDocument>>([]);
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

    private sealed class NoopModuleEventPublisher : IModuleEventPublisher
    {
        public Guid Publish<TPayload>(TPayload payload, DateTime? occurredAtUtc = null)
            where TPayload : notnull => Guid.NewGuid();
    }
}
