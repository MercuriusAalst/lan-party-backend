using Mercurius.LAN.API.Data;
using Mercurius.Modules.Identity;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Identity.Infrastructure;
using Mercurius.Modules.Media.Contracts;
using Mercurius.Modules.Teams.Application;
using Mercurius.Modules.Teams.Application.Services;
using Mercurius.Modules.Teams.Contracts;
using Mercurius.Modules.Teams.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Eventing;

namespace Mercurius.Modules.Teams.Tests;

// Builds Teams services over a real PostgreSQL context for deleted-user and join-time scenarios.
internal static class TeamsTestHarness
{
    public static TeamEventPublishingDecorator CreateTeamService(
        MercuriusDBContext dbContext,
        TimeProvider? clock = null,
        IIdentityModule? identityModule = null,
        ITeamEventPublisher? eventPublisher = null,
        IModuleEventPublisher? moduleEventPublisher = null)
    {
        var timeProvider = clock ?? TimeProvider.System;
        var identity = identityModule ?? new DbContextIdentityModule(dbContext);
        var teamsDbContext = new TeamsDbContextAdapter<MercuriusDBContext>(dbContext);

        return new TeamEventPublishingDecorator(
            CreateInnerTeamService(dbContext, timeProvider, identity),
            teamsDbContext,
            identity,
            eventPublisher ?? new NoopTeamEventPublisher(),
            moduleEventPublisher ?? new NoopModuleEventPublisher(),
            new NoopRealtimeConnectionManager(),
            timeProvider);
    }

    public static TeamService CreateInnerTeamService(
        MercuriusDBContext dbContext,
        TimeProvider? clock = null,
        IIdentityModule? identityModule = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TeamInvite:ResendCooldownDays"] = "7",
                ["TeamInvite:ExpirationDays"] = "14",
                ["TeamInvite:DeclinedResendLimit"] = "3"
            })
            .Build();

        return new TeamService(
            new TeamsDbContextAdapter<MercuriusDBContext>(dbContext),
            configuration,
            identityModule ?? new DbContextIdentityModule(dbContext),
            new NoopMediaModule(),
            new NoopTeamTournamentReadService(),
            NullLogger<TeamService>.Instance,
            clock ?? TimeProvider.System);
    }

    public static UserDeletedTeamCleanupHandler CreateCleanupService(
        MercuriusDBContext dbContext,
        IModuleEventPublisher? moduleEventPublisher = null,
        ITeamEventPublisher? eventPublisher = null,
        ILogger<UserDeletedTeamCleanupHandler>? logger = null,
        IIdentityModule? identityModule = null) =>
        new(
            new TeamsDbContextAdapter<MercuriusDBContext>(dbContext),
            identityModule ?? new DbContextIdentityModule(dbContext),
            moduleEventPublisher ?? new NoopModuleEventPublisher(),
            eventPublisher ?? new NoopTeamEventPublisher(),
            new NoopRealtimeConnectionManager(),
            TimeProvider.System,
            logger ?? NullLogger<UserDeletedTeamCleanupHandler>.Instance);

    // The production identity facade, which takes the real identity mutation lock.
    public static IIdentityModule CreateIdentityFacade(MercuriusDBContext dbContext) =>
        new IdentityModuleFacade(new IdentityDbContextAdapter<MercuriusDBContext>(dbContext), null!, new Mercurius.Modules.Identity.Application.Services.AuthenticatedIdentityContext());

    public static TeamsModuleFacade CreateTeamsFacade(MercuriusDBContext dbContext, IIdentityModule? identityModule = null) =>
        new(
            new TeamsDbContextAdapter<MercuriusDBContext>(dbContext),
            identityModule ?? new DbContextIdentityModule(dbContext),
            new NoopTeamTournamentReadService());

    public static Team CreateTeam(string name, User captain, DateTime? captainJoinedAtUtc = null)
    {
        var team = new Team(name, captain.Id) { Id = Guid.NewGuid() };
        team.AddMember(captain.Id, captainJoinedAtUtc ?? DateTime.UtcNow);
        return team;
    }

    // A membership created before join times were recorded (null join time), as the migration leaves them.
    public static void AddLegacyMember(Team team, Guid userId) =>
        team.Members.Add(new TeamMember { Team = team, TeamId = team.Id, UserId = userId, JoinedAtUtc = null });

    public static TeamInvite CreatePendingInvite(Team team, User user, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        return new TeamInvite
        {
            Id = Guid.NewGuid(),
            Team = team,
            TeamId = team.Id,
            UserId = user.Id,
            Status = TeamInviteStatus.Pending,
            CreatedAt = now,
            ExpiresAt = now.AddDays(7)
        };
    }

    internal sealed class NoopMediaModule : IMediaModule
    {
        public Task<StoredMediaAsset> SaveImageAsync(MediaUpload upload, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StoredMediaAsset("https://example.test/logo.webp"));

        public Task DeleteImageAsync(string? imageUrl, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    internal sealed class NoopTeamTournamentReadService : ITeamTournamentReadService
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

    internal sealed class NoopTeamEventPublisher : ITeamEventPublisher
    {
        public Task InviteChangedAsync(Guid teamId, Guid inviteId, Guid affectedUserId, string status, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MembershipChangedAsync(Guid teamId, Guid affectedUserId, string action, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CaptainTransferredAsync(Guid teamId, Guid newCaptainUserId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    internal sealed class NoopModuleEventPublisher : IModuleEventPublisher
    {
        public Guid Publish<TPayload>(TPayload payload, DateTime? occurredAtUtc = null)
            where TPayload : notnull => Guid.NewGuid();
    }

    internal sealed class RecordingModuleEventPublisher : IModuleEventPublisher
    {
        public List<object> Events { get; } = [];

        public Guid Publish<TPayload>(TPayload payload, DateTime? occurredAtUtc = null)
            where TPayload : notnull
        {
            Events.Add(payload);
            return Guid.NewGuid();
        }
    }

    internal sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}

internal sealed class MutableTimeProvider(DateTime utcNow) : TimeProvider
{
    public DateTime UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
}

internal static class TeamTestUsers
{
    private static int _nextId;

    public static User Create(DateTime? createdAtUtc = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var username = $"dtuser{id}-{Guid.NewGuid():N}"[..20];
        return new User
        {
            Id = Guid.NewGuid(),
            Auth0UserId = $"auth0|{username}",
            Username = username,
            NormalizedUsername = username,
            Firstname = $"First{id}",
            Lastname = $"Last{id}",
            Email = $"{username}@example.com",
            CreatedAtUtc = createdAtUtc ?? DateTime.UtcNow
        };
    }

    public static User CreateDeleted(DateTime? createdAtUtc = null)
    {
        var user = Create(createdAtUtc);
        user.Anonymize(DateTime.UtcNow);
        return user;
    }
}
