using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Asp.Versioning;
using Mercurius.LAN.API.Data;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Identity.Domain;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Teams.Contracts;
using Mercurius.Modules.Tournament;
using Mercurius.Modules.Tournament.Application.DTOs.Matches;
using Mercurius.Modules.Tournament.Application.Services;
using Mercurius.Modules.Tournament.Endpoints;
using Mercurius.Modules.Tournament.Infrastructure;
using Mercurius.TestInfrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Eventing;
using Platform.Extensions;
using Domain = Mercurius.Modules.Tournament.Domain;
using TournamentAggregate = Mercurius.Modules.Tournament.Domain.Tournament;

namespace Mercurius.Api.Tests;

// Drives GET /lan/matches/pending-resolution over real HTTP against the real MatchService and PostgreSQL;
// only identity, teams and the event publisher are stubbed, and the test handler maps headers to claims.
public sealed class PendingResolutionEndpointTests
{
    private const string Route = "/v1/lan/matches/pending-resolution";
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task PendingResolution_Anonymous_IsUnauthorized()
    {
        await using var fixture = await StartAsync();

        using var response = await fixture.Client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PendingResolution_AuthenticatedNonAdmin_IsForbidden()
    {
        await using var fixture = await StartAsync();

        using var response = await fixture.GetAsAsync(Route, fixture.Regular, "user");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PendingResolution_AssignedAdmin_GetsTheirAdminResolutionRequiredMatch()
    {
        await using var fixture = await StartAsync();

        using var response = await fixture.GetAsAsync(Route, fixture.AdminA, "admin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await ReadItemsAsync(response);
        var item = Assert.Single(items, candidate => candidate.GetProperty("matchId").GetGuid() == fixture.AssignedToA.Id);
        Assert.Equal(fixture.TournamentA.Id, item.GetProperty("tournamentId").GetGuid());
        Assert.Equal("Assigned To A", item.GetProperty("tournamentName").GetString());
        Assert.Equal("MissingOpponentReport", item.GetProperty("reason").GetString());
        Assert.Equal(1, item.GetProperty("participant1ReportedScore1").GetInt32());
        Assert.Equal("player-one", item.GetProperty("participant1Name").GetString());
        Assert.False(item.TryGetProperty("assignedAdminUserId", out _));
    }

    [Fact]
    public async Task PendingResolution_OtherAdmin_SeesLegacyUnassignedButNotAnotherAdminsMatch()
    {
        await using var fixture = await StartAsync();

        using var response = await fixture.GetAsAsync(Route, fixture.AdminB, "admin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var matchIds = (await ReadItemsAsync(response)).Select(item => item.GetProperty("matchId").GetGuid()).ToList();
        Assert.Contains(fixture.Legacy.Id, matchIds);
        Assert.Contains(fixture.AssignedToB.Id, matchIds);
        Assert.DoesNotContain(fixture.AssignedToA.Id, matchIds);

        using var responseA = await fixture.GetAsAsync(Route, fixture.AdminA, "admin");
        var matchIdsA = (await ReadItemsAsync(responseA)).Select(item => item.GetProperty("matchId").GetGuid()).ToList();
        Assert.Contains(fixture.Legacy.Id, matchIdsA);
        Assert.DoesNotContain(fixture.AssignedToB.Id, matchIdsA);
    }

    [Fact]
    public async Task PendingResolution_OmitsMatchesNotAwaitingResolutionAndTournamentsNotInProgress()
    {
        await using var fixture = await StartAsync();

        using var response = await fixture.GetAsAsync(Route, fixture.AdminA, "admin");

        var matchIds = (await ReadItemsAsync(response)).Select(item => item.GetProperty("matchId").GetGuid()).ToList();
        Assert.DoesNotContain(fixture.StillConfirming.Id, matchIds);
        Assert.DoesNotContain(fixture.InFinishedTournament.Id, matchIds);
        Assert.DoesNotContain(fixture.AssignedToB.Id, matchIds);
        // Own, unassigned, orphaned and escalated matches are all visible to this admin; nothing else is.
        Assert.Equal(
            new[] { fixture.AssignedToA.Id, fixture.Legacy.Id, fixture.FormerAdminMatch.Id, fixture.SlowMatch.Id }.Order(),
            matchIds.Order());
    }

    [Fact]
    public async Task PendingResolution_AdminWhoIsNotAssigned_SeesUnassignedOrphanedAndEscalatedMatchesWithTheirAssignment()
    {
        await using var fixture = await StartAsync();

        using var response = await fixture.GetAsAsync(Route, fixture.AdminB, "admin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var assignments = (await ReadItemsAsync(response)).ToDictionary(
            item => item.GetProperty("matchId").GetGuid(),
            item => item.GetProperty("assignment").GetString());
        Assert.Equal(nameof(PendingMatchAssignment.Assigned), assignments[fixture.AssignedToB.Id]);
        Assert.Equal(nameof(PendingMatchAssignment.Unassigned), assignments[fixture.Legacy.Id]);
        Assert.Equal(nameof(PendingMatchAssignment.AssignedAdminUnavailable), assignments[fixture.FormerAdminMatch.Id]);
        Assert.Equal(nameof(PendingMatchAssignment.Escalated), assignments[fixture.SlowMatch.Id]);
        Assert.DoesNotContain(fixture.AssignedToA.Id, assignments.Keys);
        Assert.DoesNotContain(fixture.StillConfirming.Id, assignments.Keys);
        Assert.DoesNotContain(fixture.InFinishedTournament.Id, assignments.Keys);
    }

    [Fact]
    public async Task PendingResolution_AssignedAdmin_StillSeesEscalatedMatchAsAssigned()
    {
        await using var fixture = await StartAsync();

        using var response = await fixture.GetAsAsync(Route, fixture.AdminA, "admin");

        var assignments = (await ReadItemsAsync(response)).ToDictionary(
            item => item.GetProperty("matchId").GetGuid(),
            item => item.GetProperty("assignment").GetString());
        Assert.Equal(nameof(PendingMatchAssignment.Assigned), assignments[fixture.SlowMatch.Id]);
        Assert.Equal(nameof(PendingMatchAssignment.Assigned), assignments[fixture.AssignedToA.Id]);
        Assert.Equal(nameof(PendingMatchAssignment.Unassigned), assignments[fixture.Legacy.Id]);
        Assert.Equal(nameof(PendingMatchAssignment.AssignedAdminUnavailable), assignments[fixture.FormerAdminMatch.Id]);
    }

    [Theory]
    [InlineData(null, 10)]
    [InlineData("3", 3)]
    public void PendingResolution_EscalationWait_DefaultsToTenMinutesAndBindsFromConfiguration(string? configured, int expectedMinutes)
    {
        using var provider = BuildTournamentServices(configured);

        var options = provider.GetRequiredService<IOptions<MatchResolutionOptions>>().Value;

        Assert.Equal(expectedMinutes, options.EscalationAfterMinutes);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void PendingResolution_EscalationWait_RejectsNonPositiveValues(string configured)
    {
        using var provider = BuildTournamentServices(configured);

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<MatchResolutionOptions>>().Value);
    }

    private static ServiceProvider BuildTournamentServices(string? escalationAfterMinutes)
    {
        var values = new Dictionary<string, string?>();
        if (escalationAfterMinutes is not null)
            values["Tournament:MatchResolution:EscalationAfterMinutes"] = escalationAfterMinutes;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddDbContext<MercuriusDBContext>(options => options.UseNpgsql());
        services.AddTournamentModule<MercuriusDBContext>(configuration);
        return services.BuildServiceProvider();
    }

    private static async Task<List<JsonElement>> ReadItemsAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.EnumerateArray().Select(element => element.Clone()).ToList();
    }

    private static async Task<Fixture> StartAsync()
    {
        var dbContext = PostgresTestDatabase.CreateDbContext();
        var adminA = CreateUser("admin-a");
        var adminB = CreateUser("admin-b");
        var regular = CreateUser("regular-user");
        var player1 = CreateUser("player-one");
        var player2 = CreateUser("player-two");
        var formerAdmin = CreateUser("former-assigned");
        var users = new[] { adminA, adminB, regular, player1, player2, formerAdmin };

        var tournamentA = CreateTournament("Assigned To A", adminA.Id, Domain.TournamentStatus.InProgress);
        var assignedToA = CreateMatch(tournamentA, player1, player2, Domain.MatchLifecycleState.AdminResolutionRequired);
        var stillConfirming = CreateMatch(tournamentA, player1, player2, Domain.MatchLifecycleState.ScoreConfirmation);
        var tournamentB = CreateTournament("Assigned To B", adminB.Id, Domain.TournamentStatus.InProgress);
        var assignedToB = CreateMatch(tournamentB, player1, player2, Domain.MatchLifecycleState.AdminResolutionRequired);
        var legacyTournament = CreateTournament("Legacy Cup", null, Domain.TournamentStatus.InProgress);
        var legacy = CreateMatch(legacyTournament, player1, player2, Domain.MatchLifecycleState.AdminResolutionRequired);
        var finishedTournament = CreateTournament("Finished Cup", adminA.Id, Domain.TournamentStatus.Completed);
        var inFinished = CreateMatch(finishedTournament, player1, player2, Domain.MatchLifecycleState.AdminResolutionRequired);

        var orphanedTournament = CreateTournament("Orphaned Cup", formerAdmin.Id, Domain.TournamentStatus.InProgress);
        var formerAdminMatch = CreateMatch(orphanedTournament, player1, player2, Domain.MatchLifecycleState.AdminResolutionRequired);
        var slowTournament = CreateTournament("Slow Cup", adminA.Id, Domain.TournamentStatus.InProgress);
        var slowMatch = CreateMatch(slowTournament, player1, player2, Domain.MatchLifecycleState.AdminResolutionRequired);

        dbContext.Users.AddRange(users);
        dbContext.Set<TournamentAggregate>().AddRange(
            tournamentA, tournamentB, legacyTournament, finishedTournament, orphanedTournament, slowTournament);
        await dbContext.SaveChangesAsync();
        // The escalation wait is one minute in this host: the slow match has been pending for five, the others for none.
        dbContext.Set<Domain.MatchResolutionNotification>().Add(new Domain.MatchResolutionNotification
        {
            Id = Guid.NewGuid(),
            MatchId = slowMatch.Id,
            TournamentId = slowTournament.Id,
            RecipientUserId = adminA.Id,
            RecipientKind = Domain.MatchResolutionNotificationRecipientKind.AssignedAdmin,
            OccurredAtUtc = DateTime.UtcNow.AddMinutes(-5),
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
        });
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var connectionString = dbContext.Database.GetConnectionString()!;

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, HeaderPersonaAuthenticationHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddApiVersioning();
        builder.Services.AddHttpConventions();
        builder.Services.AddScoped(_ => new MercuriusDBContext(
            new DbContextOptionsBuilder<MercuriusDBContext>().UseNpgsql(connectionString).Options));
        builder.Services.AddScoped<ITournamentDbContext>(provider =>
            new TournamentDbContextAdapter<MercuriusDBContext>(provider.GetRequiredService<MercuriusDBContext>()));
        builder.Services.AddSingleton<IIdentityModule>(new StubIdentityModule(users, [adminA.Id, adminB.Id]));
        builder.Services.AddSingleton<ITeamsModule, StubTeamsModule>();
        builder.Services.AddSingleton<IModuleEventPublisher, NoopModuleEventPublisher>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.Configure<MatchResolutionOptions>(options => options.EscalationAfterMinutes = 1);
        builder.Services.AddSingleton<AssignedAdminAvailabilityCache>();
        builder.Services.AddScoped<MatchBracketImpactAnalyzer>();
        builder.Services.AddScoped<IMatchService, MatchService>();

        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMatchEndpoints();
        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!;
        var client = new HttpClient { BaseAddress = new Uri(Assert.Single(addresses.Addresses)) };
        return new Fixture(app, client, dbContext, adminA, adminB, regular, tournamentA, assignedToA, stillConfirming, assignedToB, legacy, inFinished)
        {
            FormerAdminMatch = formerAdminMatch,
            SlowMatch = slowMatch
        };
    }

    private static TournamentAggregate CreateTournament(string name, Guid? assignedAdminUserId, Domain.TournamentStatus status) =>
        new TournamentAggregate(
            name,
            Domain.BracketType.SingleElimination,
            Domain.GameFormat.BestOf1,
            Domain.GameFormat.BestOf1,
            Domain.ParticipationMode.Individual,
            null,
            Now,
            30,
            10)
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.Status, status)
            .Set(x => x.AssignedAdminUserId, assignedAdminUserId);

    private static Domain.Match CreateMatch(
        TournamentAggregate tournament,
        User player1,
        User player2,
        Domain.MatchLifecycleState state)
    {
        var match = new Domain.Match()
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.TournamentId, tournament.Id)
            .Set(x => x.Tournament, tournament)
            .Set(x => x.Format, Domain.GameFormat.BestOf1)
            .Set(x => x.ParticipationMode, Domain.ParticipationMode.Individual)
            .Set(x => x.LifecycleState, state)
            .Set(x => x.UserParticipant1Id, player1.Id)
            .Set(x => x.UserParticipant2Id, player2.Id)
            .Set(x => x.Participant1ReportedScore1, 1)
            .Set(x => x.Participant1ReportedScore2, 0);
        tournament.Matches.Add(match);
        return match;
    }

    private static User CreateUser(string name) => new()
    {
        Id = Guid.NewGuid(),
        Auth0UserId = $"auth0|{name}",
        Username = name,
        NormalizedUsername = name,
        Firstname = $"First {name}",
        Lastname = $"Last {name}"
    };

    private sealed class Fixture(
        WebApplication app,
        HttpClient client,
        MercuriusDBContext dbContext,
        User adminA,
        User adminB,
        User regular,
        TournamentAggregate tournamentA,
        Domain.Match assignedToA,
        Domain.Match stillConfirming,
        Domain.Match assignedToB,
        Domain.Match legacy,
        Domain.Match inFinishedTournament) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public User AdminA { get; } = adminA;
        public User AdminB { get; } = adminB;
        public User Regular { get; } = regular;
        public TournamentAggregate TournamentA { get; } = tournamentA;
        public Domain.Match AssignedToA { get; } = assignedToA;
        public Domain.Match StillConfirming { get; } = stillConfirming;
        public Domain.Match AssignedToB { get; } = assignedToB;
        public Domain.Match Legacy { get; } = legacy;
        public Domain.Match InFinishedTournament { get; } = inFinishedTournament;
        public Domain.Match FormerAdminMatch { get; init; } = null!;
        public Domain.Match SlowMatch { get; init; } = null!;

        public Task<HttpResponseMessage> GetAsAsync(string route, User persona, string role)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, route);
            request.Headers.Add("X-Test-Role", role);
            request.Headers.Add("X-Test-Sub", persona.Auth0UserId);
            return Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            await dbContext.DisposeAsync();
        }
    }

    private sealed class HeaderPersonaAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Role", out var role))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim> { new(ClaimTypes.Role, role.ToString()) };
            if (Request.Headers.TryGetValue("X-Test-Sub", out var subject))
                claims.Add(new Claim("sub", subject.ToString()));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private sealed class NoopModuleEventPublisher : IModuleEventPublisher
    {
        public Guid Publish<TPayload>(TPayload payload, DateTime? occurredAtUtc = null)
            where TPayload : notnull => Guid.NewGuid();
    }

    private sealed class StubIdentityModule(IReadOnlyCollection<User> users, IReadOnlyCollection<Guid> adminUserIds) : IIdentityModule
    {
        public Task<bool> IsAdminUserAsync(UserId userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(adminUserIds.Contains(userId.Value));

        private static UserProfileSummary ToSummary(User user) => new(
            new UserId(user.Id), user.Username, user.Username ?? string.Empty, false, null, null, null);

        public Task<UserProfileSummary?> GetUserProfileByAuth0IdAsync(string auth0UserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(users.Where(user => user.Auth0UserId == auth0UserId).Select(ToSummary).SingleOrDefault());

        public Task<IReadOnlyDictionary<UserId, string>> GetPublicUsernamesByIdsAsync(
            IReadOnlyCollection<UserId> userIds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<UserId, string>>(users
                .Where(user => userIds.Contains(new UserId(user.Id)))
                .ToDictionary(user => new UserId(user.Id), user => user.Username!));

        public Task<UserProfileSummary?> GetUserProfileAsync(UserId userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<PublicUserProfileSummary?> GetPublicProfileByUsernameAsync(string username, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<PublicUserProfileSummary?> GetPublicProfileByIdAsync(UserId userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyDictionary<UserId, UserProfileSummary>> GetUsersByIdsAsync(IReadOnlyCollection<UserId> userIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<PublicUserSearchDocument>> GetPublicUserSearchDocumentsPageAsync(UserId? afterId, int pageSize, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubTeamsModule : ITeamsModule
    {
        public Task<IReadOnlyDictionary<TeamId, string>> GetPublicTeamNamesByIdsAsync(
            IReadOnlyCollection<TeamId> teamIds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<TeamId, string>>(new Dictionary<TeamId, string>());

        public Task<TeamSummary?> GetTeamSummaryAsync(TeamId teamId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<TeamId>> GetCaptainedTeamIdsAsync(UserId userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<TeamRosterSnapshot?> GetTeamRosterSnapshotAsync(TeamId teamId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyDictionary<TeamId, TeamRosterSnapshot>> GetTeamRosterSnapshotsAsync(IReadOnlyCollection<TeamId> teamIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<PublicTeamProfile?> GetPublicTeamProfileAsync(string teamName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<TeamId?> GetPublicTeamIdByNameAsync(string teamName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<TeamRegistrationEligibility> GetRegistrationEligibilityAsync(TeamId teamId, UserId requestedBy, TournamentId tournamentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<MembershipMutationGuard> CanMutateMembershipAsync(TeamId teamId, UserId userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<PublicTeamSearchDocument>> GetPublicTeamSearchDocumentsPageAsync(TeamId? afterId, int pageSize, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
