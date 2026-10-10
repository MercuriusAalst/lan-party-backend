using Mercurius.LAN.API.Data;
using Mercurius.Modules.Tournament.Contracts;
using Mercurius.Modules.Discovery;
using Mercurius.Modules.Discovery.Application;
using Mercurius.Modules.Discovery.Contracts;
using Mercurius.Modules.Discovery.Domain;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Sponsorship.Contracts;
using Mercurius.Modules.Sponsorship.Contracts.V1;
using Mercurius.Modules.Teams.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mercurius.TestInfrastructure;
using Platform.Eventing;
using Platform.Extensions;

namespace Mercurius.Modules.Discovery.Tests;

public class DiscoveryModuleTests
{
    [Fact]
    public async Task SearchAsync_UsesOnlyDiscoveryDocumentsAndPreservesPublicResultOrdering()
    {
        var sources = new DiscoverySources();
        await using var provider = CreateProvider(sources);
        using var scope = provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MercuriusDBContext>();
        var projector = scope.ServiceProvider.GetRequiredService<SearchDocumentProjector>();
        var module = scope.ServiceProvider.GetRequiredService<IDiscoveryModule>();

        dbContext.Users.Add(new Mercurius.Modules.Identity.Domain.User
        {
            Id = Guid.NewGuid(),
            Auth0UserId = "auth0|live-only",
            Username = "alpha-live-only",
            NormalizedUsername = "alpha-live-only",
            Firstname = "Live",
            Lastname = "Only"
        });
        await projector.UpsertAsync(SearchDocumentTypes.User, Guid.NewGuid().ToString(), "alpha", "User", null, "/users/alpha", 1, DateTime.UtcNow, default);
        await projector.UpsertAsync(SearchDocumentTypes.Team, Guid.NewGuid().ToString(), "alphateam", "Team", null, "/teams/alphateam", 1, DateTime.UtcNow, default);
        var tournamentId = Guid.NewGuid();
        await projector.UpsertAsync(SearchDocumentTypes.Tournament, tournamentId.ToString(), "Winter Alpha Cup", "Tournament", null, $"/tournaments/{tournamentId}", 1, DateTime.UtcNow, default);
        await dbContext.SaveChangesAsync();

        var result = await module.SearchAsync(new DiscoverySearchRequest(" ALPHA ", null, 10));

        Assert.Collection(result.Results,
            user =>
            {
                Assert.Equal("user", user.Type);
                Assert.Equal("alpha", user.Username);
                Assert.Null(user.TeamName);
                Assert.Null(user.TournamentId);
            },
            team =>
            {
                Assert.Equal("team", team.Type);
                Assert.Equal("alphateam", team.TeamName);
                Assert.Null(team.Username);
                Assert.Null(team.TournamentId);
            },
            tournament =>
            {
                Assert.Equal("tournament", tournament.Type);
                Assert.Equal(tournamentId, tournament.TournamentId);
                Assert.Null(tournament.Username);
                Assert.Null(tournament.TeamName);
            });
        Assert.DoesNotContain(result.Results, entry => entry.DisplayLabel == "alpha-live-only");
    }

    [Fact]
    public async Task SearchAsync_PaginatesAcrossExactPrefixAndContainsRanksWithoutDuplicates()
    {
        await using var provider = CreateProvider(new DiscoverySources());
        using var scope = provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MercuriusDBContext>();
        var projector = scope.ServiceProvider.GetRequiredService<SearchDocumentProjector>();
        var module = scope.ServiceProvider.GetRequiredService<IDiscoveryModule>();

        await projector.UpsertAsync(SearchDocumentTypes.User, Guid.NewGuid().ToString(), "alpha", "User", null, "/users/alpha", 1, DateTime.UtcNow, default);
        await projector.UpsertAsync(SearchDocumentTypes.Team, Guid.NewGuid().ToString(), "alpha-team", "Team", null, "/teams/alpha-team", 1, DateTime.UtcNow, default);
        await projector.UpsertAsync(SearchDocumentTypes.Tournament, Guid.NewGuid().ToString(), "winter alpha cup", "Tournament", null, "/tournaments/1", 1, DateTime.UtcNow, default);
        await dbContext.SaveChangesAsync();

        var first = await module.SearchAsync(new DiscoverySearchRequest("alpha", null, 1));
        var second = await module.SearchAsync(new DiscoverySearchRequest("alpha", first.NextCursor, 1));
        var third = await module.SearchAsync(new DiscoverySearchRequest("alpha", second.NextCursor, 1));

        Assert.Equal("alpha", Assert.Single(first.Results).DisplayLabel);
        Assert.Equal("alpha-team", Assert.Single(second.Results).DisplayLabel);
        Assert.Equal("winter alpha cup", Assert.Single(third.Results).DisplayLabel);
        Assert.True(first.HasMore);
        Assert.True(second.HasMore);
        Assert.False(third.HasMore);
    }

    [Fact]
    public async Task ProjectionWriter_IgnoresStaleUpdatesAndKeepsDeletedDocumentsHidden()
    {
        await using var provider = CreateProvider(new DiscoverySources());
        using var scope = provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MercuriusDBContext>();
        var projector = scope.ServiceProvider.GetRequiredService<SearchDocumentProjector>();
        var module = scope.ServiceProvider.GetRequiredService<IDiscoveryModule>();
        var userId = Guid.NewGuid().ToString();

        await projector.UpsertAsync(SearchDocumentTypes.User, userId, "alpha-new", "User", null, "/users/alpha-new", 20, DateTime.UtcNow, default);
        await projector.UpsertAsync(SearchDocumentTypes.User, userId, "alpha-old", "User", null, "/users/alpha-old", 10, DateTime.UtcNow, default);
        await dbContext.SaveChangesAsync();

        var updated = await module.SearchAsync(new DiscoverySearchRequest("alpha", null, 10));
        Assert.Equal("alpha-new", Assert.Single(updated.Results).Username);

        await projector.MarkDeletedAsync(SearchDocumentTypes.User, userId, 30, DateTime.UtcNow, default);
        await projector.UpsertAsync(SearchDocumentTypes.User, userId, "alpha-restored", "User", null, "/users/alpha-restored", 20, DateTime.UtcNow, default);
        await dbContext.SaveChangesAsync();

        var deleted = await module.SearchAsync(new DiscoverySearchRequest("alpha", null, 10));
        Assert.Empty(deleted.Results);
    }

    [Fact]
    public async Task Dispatcher_ProjectsSearchableUsersAndRemovesIncompleteProfiles()
    {
        await using var provider = CreateProvider(new DiscoverySources());
        using var scope = provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MercuriusDBContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IModuleEventPublisher>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IModuleEventDispatcher>();
        var module = scope.ServiceProvider.GetRequiredService<IDiscoveryModule>();
        var userId = new UserId(Guid.NewGuid());

        publisher.Publish(new UserProfileChangedIntegrationEvent(
            userId,
            "alpha",
            "Alpha User",
            false,
            true,
            DateTime.UtcNow));
        await dbContext.SaveChangesAsync();
        Assert.Equal(1, await dispatcher.DispatchPendingAsync());
        Assert.Equal("alpha", Assert.Single((await module.SearchAsync(new DiscoverySearchRequest("alpha", null, 10))).Results).Username);

        publisher.Publish(new UserProfileChangedIntegrationEvent(
            userId,
            "alpha",
            "Incomplete profile",
            false,
            false,
            DateTime.UtcNow));
        await dbContext.SaveChangesAsync();
        Assert.Equal(1, await dispatcher.DispatchPendingAsync());
        Assert.Empty((await module.SearchAsync(new DiscoverySearchRequest("alpha", null, 10))).Results);
    }

    [Fact]
    public async Task Dispatcher_ProjectsTeamTournamentAndSponsorUpdates_WithoutDuplicatingOrOverwritingNewerDocuments()
    {
        await using var provider = CreateProvider(new DiscoverySources());
        using var scope = provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MercuriusDBContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IModuleEventPublisher>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IModuleEventDispatcher>();
        var teamId = new TeamId(Guid.NewGuid());
        var tournamentId = new TournamentId(Guid.NewGuid());
        var sponsorId = new SponsorId(7);
        var currentTime = DateTime.UtcNow;
        currentTime = currentTime.AddTicks(-(currentTime.Ticks % TimeSpan.TicksPerMicrosecond));

        publisher.Publish(new TeamRenamedIntegrationEvent(teamId, 2, "New Team Name"), currentTime);
        publisher.Publish(new TournamentUpdatedIntegrationEvent(tournamentId, "Updated Tournament Name"), currentTime);
        publisher.Publish(new SponsorUpdated(
            sponsorId,
            "Updated Sponsor Name",
            SponsorTier.Gold,
            "/images/updated-sponsor.webp",
            "https://example.test/sponsor",
            "Updated description"),
            currentTime);
        await dbContext.SaveChangesAsync();
        Assert.Equal(3, await dispatcher.DispatchPendingAsync());

        publisher.Publish(new TeamRenamedIntegrationEvent(teamId, 2, "New Team Name"), currentTime);
        publisher.Publish(new TeamRenamedIntegrationEvent(teamId, 1, "Old Team Name"), currentTime.AddMilliseconds(-1));
        await dbContext.SaveChangesAsync();
        Assert.Equal(2, await dispatcher.DispatchPendingAsync());

        dbContext.ChangeTracker.Clear();
        var documents = await dbContext.Set<SearchDocument>()
            .OrderBy(document => document.EntityType)
            .ToListAsync();

        Assert.Collection(documents,
            sponsor =>
            {
                Assert.Equal(SearchDocumentTypes.Sponsor, sponsor.EntityType);
                Assert.Equal("Updated Sponsor Name", sponsor.Title);
                Assert.Equal("/images/updated-sponsor.webp", sponsor.ImageUrl);
            },
            team =>
            {
                Assert.Equal(SearchDocumentTypes.Team, team.EntityType);
                Assert.Equal("New Team Name", team.Title);
                Assert.Equal($"/teams/New%20Team%20Name", team.Route);
                Assert.Equal(currentTime.Ticks, team.SourceVersion);
            },
            tournament =>
            {
                Assert.Equal(SearchDocumentTypes.Tournament, tournament.EntityType);
                Assert.Equal("Updated Tournament Name", tournament.Title);
                Assert.Equal($"/tournaments/{tournamentId.Value}", tournament.Route);
            });
    }

    [Fact]
    public async Task HostedDispatcher_ProjectsPublishedUsersWithoutManualDispatch()
    {
        using var host = CreateHost(new DiscoverySources());
        Assert.Equal(2, host.Services.GetServices<IHostedService>().Count());
        await host.StartAsync();

        try
        {
            using var scope = host.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<MercuriusDBContext>();
            var publisher = scope.ServiceProvider.GetRequiredService<IModuleEventPublisher>();
            var module = scope.ServiceProvider.GetRequiredService<IDiscoveryModule>();
            var userId = new UserId(Guid.NewGuid());

            Assert.Single(scope.ServiceProvider.GetServices<IModuleEventHandler<UserProfileChangedIntegrationEvent>>());
            var initialRebuildCompleted = await WaitForAsync(() =>
                dbContext.Set<SearchIndexRebuildJob>()
                    .AsNoTracking()
                    .AnyAsync(job => job.Status == SearchIndexRebuildJobStatus.Completed));
            Assert.True(initialRebuildCompleted, "The hosted initial search rebuild did not complete within five seconds.");

            publisher.Publish(new UserProfileChangedIntegrationEvent(
                userId,
                "worker-projected-user",
                "Worker Projected User",
                false,
                true,
                DateTime.UtcNow));
            await dbContext.SaveChangesAsync();

            var projected = await WaitForAsync(async () =>
                (await module.SearchAsync(new DiscoverySearchRequest("worker-projected-user", null, 10)))
                .Results.SingleOrDefault()?.Username == "worker-projected-user");
            var outbox = await dbContext.OutboxMessages.AsNoTracking().SingleAsync();
            var documents = await dbContext.Set<SearchDocument>().AsNoTracking().ToListAsync();
            Assert.True(
                projected,
                $"The hosted event dispatcher did not project the published user. Processed: {outbox.ProcessedAtUtc}; Last error: {outbox.LastError}; " +
                $"documents: {string.Join(", ", documents.Select(document => $"{document.Title}/{document.IsDeleted}"))}");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task RebuildJob_UsesSourceContractsAndCoalescesCurrentDocuments()
    {
        var userId = new UserId(Guid.NewGuid());
        var teamId = new TeamId(Guid.NewGuid());
        var tournamentId = new TournamentId(Guid.NewGuid());
        var sources = new DiscoverySources
        {
            Users = [new PublicUserSearchDocument(userId, "alpha-user")],
            Teams = [new PublicTeamSearchDocument(teamId, "alpha-team")],
            Tournaments = [new TournamentSearchDocument(tournamentId, "Alpha Cup", "/images/alpha.webp")],
            Sponsors = [new SponsorSummary(new SponsorId(7), "Alpha Sponsor", SponsorTier.Gold, "/images/sponsor.webp", "https://example.test", null)]
        };
        await using var provider = CreateProvider(sources);
        using var scope = provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MercuriusDBContext>();
        var module = scope.ServiceProvider.GetRequiredService<IDiscoveryModule>();
        var rebuildService = scope.ServiceProvider.GetRequiredService<SearchIndexRebuildService>();
        await using var ownershipLease = await AcquireOwnershipAsync(scope.ServiceProvider);

        await rebuildService.EnsureInitialJobAsync(default);
        var firstJob = await module.CreateSearchIndexRebuildJobAsync();
        var coalescedJob = await module.CreateSearchIndexRebuildJobAsync();
        Assert.Equal(firstJob.Id, coalescedJob.Id);
        Assert.True(await rebuildService.RunNextAsync(default));

        var completedJob = await module.GetSearchIndexRebuildJobAsync(firstJob.Id);
        Assert.Equal("completed", completedJob!.Status);
        var rebuiltResults = await module.SearchAsync(new DiscoverySearchRequest("alpha", null, 10));
        Assert.Equal(["tournament", "team", "user"], rebuiltResults.Results.Select(result => result.Type));

        sources.Users = [];
        sources.Teams = [];
        sources.Tournaments = [];
        sources.Sponsors = [];
        _ = await module.CreateSearchIndexRebuildJobAsync();
        Assert.True(await rebuildService.RunNextAsync(default));

        Assert.Empty((await module.SearchAsync(new DiscoverySearchRequest("alpha", null, 10))).Results);

        await rebuildService.EnsureInitialJobAsync(default);
        Assert.Equal(2, await dbContext.Set<SearchIndexRebuildJob>().CountAsync());
    }

    [Fact]
    public async Task RebuildJob_DoesNotExposeExceptionDetails()
    {
        var sources = new DiscoverySources
        {
            RebuildException = new InvalidOperationException("database-secret")
        };
        await using var provider = CreateProvider(sources);
        using var scope = provider.CreateScope();
        var module = scope.ServiceProvider.GetRequiredService<IDiscoveryModule>();
        var rebuildService = scope.ServiceProvider.GetRequiredService<SearchIndexRebuildService>();

        var job = await module.CreateSearchIndexRebuildJobAsync();
        await using var ownershipLease = await AcquireOwnershipAsync(scope.ServiceProvider);
        Assert.True(await rebuildService.RunNextAsync(default));

        var failedJob = await module.GetSearchIndexRebuildJobAsync(job.Id);
        Assert.Equal("failed", failedJob!.Status);
        Assert.Equal("The rebuild failed. Check server logs for details.", failedJob.Error);
        Assert.DoesNotContain("database-secret", failedJob.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureInitialJobAsync_QueuesRebuildWhenOnlyDeletedDocumentsRemain()
    {
        await using var provider = CreateProvider(new DiscoverySources());
        using var scope = provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MercuriusDBContext>();
        var projector = scope.ServiceProvider.GetRequiredService<SearchDocumentProjector>();
        var rebuildService = scope.ServiceProvider.GetRequiredService<SearchIndexRebuildService>();
        await using var ownershipLease = await AcquireOwnershipAsync(scope.ServiceProvider);

        await projector.MarkDeletedAsync(SearchDocumentTypes.User, Guid.NewGuid().ToString(), 1, DateTime.UtcNow, default);
        await dbContext.SaveChangesAsync();

        await rebuildService.EnsureInitialJobAsync(default);

        var job = await dbContext.Set<SearchIndexRebuildJob>().SingleAsync();
        Assert.Equal(SearchIndexRebuildJobStatus.Pending, job.Status);
    }

    [Fact]
    public async Task HostedWorker_RecoversARunningJobAtStartupRegardlessOfAge()
    {
        var sources = new DiscoverySources
        {
            Users = [new PublicUserSearchDocument(new UserId(Guid.NewGuid()), "recovered-user")]
        };
        var host = CreateHost(sources);
        try
        {
            Guid jobId;
            using (var scope = host.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<MercuriusDBContext>();
                var runningJob = new SearchIndexRebuildJob
                {
                    Status = SearchIndexRebuildJobStatus.Running,
                    CreatedAtUtc = DateTime.UtcNow.AddHours(-1),
                    StartedAtUtc = DateTime.UtcNow.AddMinutes(1)
                };
                dbContext.Add(runningJob);
                dbContext.Add(new SearchIndexRebuildDocument
                {
                    JobId = runningJob.Id,
                    EntityType = SearchDocumentTypes.User,
                    EntityId = Guid.NewGuid().ToString(),
                    TypeOrder = SearchDocumentTypes.GetTypeOrder(SearchDocumentTypes.User),
                    Title = "stale-stage",
                    Subtitle = "User",
                    Route = "/users/stale-stage",
                    NormalizedText = "stale-stage",
                    SourceVersion = 1,
                    UpdatedAtUtc = DateTime.UtcNow
                });
                await dbContext.SaveChangesAsync();
                jobId = runningJob.Id;
            }

            await host.StartAsync();

            Assert.True(await WaitForAsync(async () =>
            {
                using var scope = host.Services.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<MercuriusDBContext>();
                var job = await dbContext.Set<SearchIndexRebuildJob>().AsNoTracking().SingleAsync();
                return job.Id == jobId &&
                       job.Status == SearchIndexRebuildJobStatus.Completed &&
                       await dbContext.Set<SearchIndexRebuildDocument>().AnyAsync() is false;
            }));

            using var verificationScope = host.Services.CreateScope();
            var verificationContext = verificationScope.ServiceProvider.GetRequiredService<MercuriusDBContext>();
            Assert.Equal("recovered-user", (await verificationContext.Set<SearchDocument>().SingleAsync()).Title);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task CreateJobAsync_DoesNotRequeueAnOldRunningJob()
    {
        await using var provider = CreateProvider(new DiscoverySources());
        using var scope = provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MercuriusDBContext>();
        var module = scope.ServiceProvider.GetRequiredService<IDiscoveryModule>();
        var startedAtUtc = DateTime.UtcNow.AddHours(-1);
        var runningJob = new SearchIndexRebuildJob
        {
            Status = SearchIndexRebuildJobStatus.Running,
            CreatedAtUtc = startedAtUtc.AddMinutes(-1),
            StartedAtUtc = startedAtUtc
        };
        dbContext.Add(runningJob);
        await dbContext.SaveChangesAsync();

        var job = await module.CreateSearchIndexRebuildJobAsync();

        Assert.Equal(runningJob.Id, job.Id);
        Assert.Equal("running", job.Status);
        Assert.True(Math.Abs((job.StartedAtUtc!.Value - startedAtUtc).Ticks) <= TimeSpan.TicksPerMicrosecond);
        var persistedJob = await dbContext.Set<SearchIndexRebuildJob>().AsNoTracking().SingleAsync();
        Assert.Equal(SearchIndexRebuildJobStatus.Running, persistedJob.Status);
        Assert.True(Math.Abs((persistedJob.StartedAtUtc!.Value - startedAtUtc).Ticks) <= TimeSpan.TicksPerMicrosecond);
    }

    [Fact]
    public async Task RunNextAsync_LeavesCanceledRunningJobRecoverable()
    {
        var sources = new DiscoverySources
        {
            WaitForRebuildCancellation = true
        };
        await using var provider = CreateProvider(sources);
        using var scope = provider.CreateScope();
        var module = scope.ServiceProvider.GetRequiredService<IDiscoveryModule>();
        var rebuildService = scope.ServiceProvider.GetRequiredService<SearchIndexRebuildService>();
        var job = await module.CreateSearchIndexRebuildJobAsync();
        await using var ownershipLease = await AcquireOwnershipAsync(scope.ServiceProvider);
        using var cancellationTokenSource = new CancellationTokenSource();

        var runTask = rebuildService.RunNextAsync(cancellationTokenSource.Token);
        await sources.RebuildStarted.Task;
        await cancellationTokenSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

        var canceledJob = await module.GetSearchIndexRebuildJobAsync(job.Id);
        Assert.Equal("running", canceledJob!.Status);
        Assert.NotNull(canceledJob.StartedAtUtc);
        Assert.Null(canceledJob.CompletedAtUtc);
        Assert.Null(canceledJob.Error);

        await rebuildService.RecoverInterruptedJobsAsync(default);

        var recoveredJob = await module.GetSearchIndexRebuildJobAsync(job.Id);
        Assert.Equal("pending", recoveredJob!.Status);
        Assert.Null(recoveredJob.StartedAtUtc);
    }

    [Fact]
    public async Task RebuildJob_FailureBeforeMergeLeavesLiveDocumentsUnchanged()
    {
        var sources = new DiscoverySources
        {
            RebuildException = new InvalidOperationException("source unavailable")
        };
        await using var provider = CreateProvider(sources);
        using var scope = provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MercuriusDBContext>();
        var projector = scope.ServiceProvider.GetRequiredService<SearchDocumentProjector>();
        var module = scope.ServiceProvider.GetRequiredService<IDiscoveryModule>();
        var rebuildService = scope.ServiceProvider.GetRequiredService<SearchIndexRebuildService>();
        await using var ownershipLease = await AcquireOwnershipAsync(scope.ServiceProvider);

        await projector.UpsertAsync(SearchDocumentTypes.User, Guid.NewGuid().ToString(), "existing-user", "User", null, "/users/existing-user", 1, DateTime.UtcNow, default);
        await dbContext.SaveChangesAsync();

        _ = await module.CreateSearchIndexRebuildJobAsync();
        Assert.True(await rebuildService.RunNextAsync(default));

        var result = await module.SearchAsync(new DiscoverySearchRequest("existing", null, 10));
        Assert.Equal("existing-user", Assert.Single(result.Results).Username);
        Assert.Empty(await dbContext.Set<SearchIndexRebuildDocument>().ToListAsync());
    }

    [Fact]
    public async Task RebuildJob_StagesAndMergesMultipleSourcePages()
    {
        var sources = new DiscoverySources
        {
            Users = Enumerable.Range(1, 1001)
                .Select(index => new PublicUserSearchDocument(new UserId(Guid.NewGuid()), $"alpha-{index:D4}"))
                .ToList()
        };
        await using var provider = CreateProvider(sources);
        using var scope = provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MercuriusDBContext>();
        var module = scope.ServiceProvider.GetRequiredService<IDiscoveryModule>();
        var rebuildService = scope.ServiceProvider.GetRequiredService<SearchIndexRebuildService>();
        await using var ownershipLease = await AcquireOwnershipAsync(scope.ServiceProvider);

        _ = await module.CreateSearchIndexRebuildJobAsync();
        Assert.True(await rebuildService.RunNextAsync(default));

        Assert.Equal(1001, await dbContext.Set<SearchDocument>().CountAsync(document => !document.IsDeleted));
        Assert.Empty(await dbContext.Set<SearchIndexRebuildDocument>().ToListAsync());
    }

    internal static ServiceProvider CreateProvider(DiscoverySources sources)
    {
        var services = new ServiceCollection();
        var database = PostgresTestDatabase.CreateMigrated();
        ConfigureServices(services, sources, database.ConnectionString);
        services.AddSingleton<PostgresTestDatabaseLease>(_ => database);

        var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<PostgresTestDatabaseLease>();
        using var scope = provider.CreateScope();
        PostgresTestDatabase.Initialize(scope.ServiceProvider.GetRequiredService<MercuriusDBContext>());
        return provider;
    }

    private static IHost CreateHost(DiscoverySources sources)
    {
        var database = PostgresTestDatabase.Create();
        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                ConfigureServices(services, sources, database.ConnectionString);
                services.AddSingleton<PostgresTestDatabaseLease>(_ => database);
            })
            .Build();
        _ = host.Services.GetRequiredService<PostgresTestDatabaseLease>();
        using var scope = host.Services.CreateScope();
        PostgresTestDatabase.Initialize(scope.ServiceProvider.GetRequiredService<MercuriusDBContext>());
        return host;
    }

    internal static IHost CreateHostForDatabase(string connectionString, DiscoverySources sources)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services => ConfigureServices(services, sources, connectionString))
            .Build();
    }

    internal static void ConfigureServices(
        IServiceCollection services,
        DiscoverySources sources,
        string connectionString)
    {
        var configuration = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddDbContext<MercuriusDBContext>(options => options.UseNpgsql(connectionString));
        services.AddModuleEventing<MercuriusDBContext>();
        services.AddSingleton<IIdentityModule>(new StubIdentityModule(sources));
        services.AddSingleton<ITeamsModule>(new StubTeamsModule(sources));
        services.AddSingleton<ITournamentModule>(new StubTournamentModule(sources));
        services.AddSingleton<ISponsorshipModule>(new StubSponsorshipModule(sources));
        services.AddDiscoveryModule<MercuriusDBContext>(configuration);
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition)
    {
        var timeoutAtUtc = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < timeoutAtUtc)
        {
            if (await condition())
                return true;

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        return false;
    }

    internal sealed class DiscoverySources
    {
        public IReadOnlyList<PublicUserSearchDocument> Users { get; set; } = [];
        public IReadOnlyList<PublicTeamSearchDocument> Teams { get; set; } = [];
        public IReadOnlyList<TournamentSearchDocument> Tournaments { get; set; } = [];
        public IReadOnlyList<SponsorSummary> Sponsors { get; set; } = [];
        public Exception? RebuildException { get; set; }
        public bool WaitForRebuildCancellation { get; set; }
        public bool PauseAfterFirstUserPage { get; set; }
        public TaskCompletionSource RebuildStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueRebuild { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task<DiscoveryRebuildOwnership.Lease> AcquireOwnershipAsync(IServiceProvider services)
    {
        var lease = await services.GetRequiredService<DiscoveryRebuildOwnership>().TryAcquireAsync(default);
        return Assert.IsType<DiscoveryRebuildOwnership.Lease>(lease);
    }

    private sealed class StubIdentityModule(DiscoverySources sources) : IIdentityModule
    {
        public async Task<IReadOnlyList<PublicUserSearchDocument>> GetPublicUserSearchDocumentsPageAsync(
            UserId? afterId,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            if (sources.RebuildException is not null)
                throw sources.RebuildException;

            if (sources.WaitForRebuildCancellation)
            {
                sources.RebuildStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (sources.PauseAfterFirstUserPage && afterId.HasValue)
            {
                sources.RebuildStarted.TrySetResult();
                await sources.ContinueRebuild.Task.WaitAsync(cancellationToken);
            }

            return sources.Users
                .Where(user => !afterId.HasValue || user.UserId.Value.CompareTo(afterId.Value.Value) > 0)
                .OrderBy(user => user.UserId.Value)
                .Take(pageSize)
                .ToList();
        }
        public Task<UserProfileSummary?> GetUserProfileAsync(UserId userId, CancellationToken cancellationToken = default) => Task.FromResult<UserProfileSummary?>(null);
        public Task<UserProfileSummary?> GetUserProfileByAuth0IdAsync(string auth0UserId, CancellationToken cancellationToken = default) => Task.FromResult<UserProfileSummary?>(null);
        public Task<PublicUserProfileSummary?> GetPublicProfileByUsernameAsync(string username, CancellationToken cancellationToken = default) => Task.FromResult<PublicUserProfileSummary?>(null);
        public Task<PublicUserProfileSummary?> GetPublicProfileByIdAsync(UserId userId, CancellationToken cancellationToken = default) => Task.FromResult<PublicUserProfileSummary?>(null);
        public Task<IReadOnlyDictionary<UserId, UserProfileSummary>> GetUsersByIdsAsync(IReadOnlyCollection<UserId> userIds, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<UserId, UserProfileSummary>>(new Dictionary<UserId, UserProfileSummary>());
        public Task<IReadOnlyDictionary<UserId, string>> GetPublicUsernamesByIdsAsync(IReadOnlyCollection<UserId> userIds, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<UserId, string>>(new Dictionary<UserId, string>());
    }

    private sealed class StubTeamsModule(DiscoverySources sources) : ITeamsModule
    {
        public Task<IReadOnlyList<PublicTeamSearchDocument>> GetPublicTeamSearchDocumentsPageAsync(TeamId? afterId, int pageSize, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PublicTeamSearchDocument>>(sources.Teams
                .Where(team => !afterId.HasValue || team.TeamId.Value.CompareTo(afterId.Value.Value) > 0)
                .OrderBy(team => team.TeamId.Value)
                .Take(pageSize)
                .ToList());
        public Task<TeamSummary?> GetTeamSummaryAsync(TeamId teamId, CancellationToken cancellationToken = default) => Task.FromResult<TeamSummary?>(null);
        public Task<IReadOnlyList<TeamId>> GetCaptainedTeamIdsAsync(UserId userId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TeamId>>([]);
        public Task<TeamRosterSnapshot?> GetTeamRosterSnapshotAsync(TeamId teamId, CancellationToken cancellationToken = default) => Task.FromResult<TeamRosterSnapshot?>(null);
        public Task<IReadOnlyDictionary<TeamId, TeamRosterSnapshot>> GetTeamRosterSnapshotsAsync(IReadOnlyCollection<TeamId> teamIds, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<TeamId, TeamRosterSnapshot>>(new Dictionary<TeamId, TeamRosterSnapshot>());
        public Task<IReadOnlyDictionary<TeamId, string>> GetPublicTeamNamesByIdsAsync(IReadOnlyCollection<TeamId> teamIds, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<TeamId, string>>(new Dictionary<TeamId, string>());
        public Task<PublicTeamProfile?> GetPublicTeamProfileAsync(string teamName, CancellationToken cancellationToken = default) => Task.FromResult<PublicTeamProfile?>(null);
        public Task<TeamId?> GetPublicTeamIdByNameAsync(string teamName, CancellationToken cancellationToken = default) => Task.FromResult<TeamId?>(null);
        public Task<TeamRegistrationEligibility> GetRegistrationEligibilityAsync(TeamId teamId, UserId requestedBy, TournamentId tournamentId, CancellationToken cancellationToken = default) => Task.FromResult(new TeamRegistrationEligibility(true, []));
        public Task<MembershipMutationGuard> CanMutateMembershipAsync(TeamId teamId, UserId userId, CancellationToken cancellationToken = default) => Task.FromResult(new MembershipMutationGuard(true, []));
    }

    private sealed class StubTournamentModule(DiscoverySources sources) : ITournamentModule
    {
        public Task<PublicProfileMatchSummarySet> GetPublicUserMatchSummariesAsync(UserId userId, CancellationToken cancellationToken = default) => Task.FromResult(new PublicProfileMatchSummarySet([], []));
        public Task<PublicProfileMatchSummarySet> GetPublicTeamMatchSummariesAsync(TeamId teamId, CancellationToken cancellationToken = default) => Task.FromResult(new PublicProfileMatchSummarySet([], []));
        public Task<IReadOnlyList<TournamentSearchDocument>> GetTournamentSearchDocumentsPageAsync(TournamentId? afterId, int pageSize, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TournamentSearchDocument>>(sources.Tournaments
                .Where(tournament => !afterId.HasValue || tournament.TournamentId.Value.CompareTo(afterId.Value.Value) > 0)
                .OrderBy(tournament => tournament.TournamentId.Value)
                .Take(pageSize)
                .ToList());
        public Task<TournamentSummary?> GetTournamentSummaryAsync(TournamentId tournamentId, CancellationToken cancellationToken = default) => Task.FromResult<TournamentSummary?>(null);
        public Task<TournamentConfiguration?> GetTournamentConfigurationAsync(TournamentId tournamentId, CancellationToken cancellationToken = default) => Task.FromResult<TournamentConfiguration?>(null);
        public Task<bool> IsRegistrationOpenAsync(TournamentId tournamentId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<RegistrationEligibility> CheckIndividualRegistrationEligibilityAsync(TournamentId tournamentId, UserId userId, CancellationToken cancellationToken = default) => Task.FromResult(new RegistrationEligibility(true, []));
        public Task<RegistrationEligibility> CheckTeamRegistrationEligibilityAsync(TournamentId tournamentId, TeamId teamId, UserId requestedBy, CancellationToken cancellationToken = default) => Task.FromResult(new RegistrationEligibility(true, []));
        public Task<IReadOnlyList<TournamentSummary>> SearchTournamentsAsync(string normalizedQuery, TournamentSearchCursor? cursor, int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TournamentSummary>>([]);
    }

    private sealed class StubSponsorshipModule(DiscoverySources sources) : ISponsorshipModule
    {
        public Task<IReadOnlyList<SponsorSearchDocument>> GetSponsorSearchDocumentsPageAsync(SponsorId? afterId, int pageSize, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SponsorSearchDocument>>(sources.Sponsors
                .Where(sponsor => !afterId.HasValue || sponsor.Id.Value > afterId.Value.Value)
                .OrderBy(sponsor => sponsor.Id.Value)
                .Take(pageSize)
                .Select(sponsor => new SponsorSearchDocument(sponsor.Id, sponsor.Name, sponsor.LogoUrl))
                .ToList());
        public Task<SponsorSummary?> GetSponsorSummaryAsync(SponsorId sponsorId, CancellationToken cancellationToken = default) => Task.FromResult<SponsorSummary?>(null);
        public Task<IReadOnlyDictionary<TournamentId, SponsorPlacementSummary>> GetSponsorPlacementsAsync(IReadOnlyCollection<TournamentId> tournamentIds, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<TournamentId, SponsorPlacementSummary>>(new Dictionary<TournamentId, SponsorPlacementSummary>());
        public Task<SponsorPlacementSummary?> GetSponsorPlacementAsync(TournamentId tournamentId, CancellationToken cancellationToken = default) => Task.FromResult<SponsorPlacementSummary?>(null);
        public Task ReplaceSponsorPlacementAsync(TournamentId tournamentId, SponsorPlacementInput? placement, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
