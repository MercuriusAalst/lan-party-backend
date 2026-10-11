using Mercurius.LAN.API.Hubs;
using Mercurius.Modules.Tournament;
using Mercurius.Modules.Tournament.Application.DTOs.PublicProfiles;
using Mercurius.Modules.Tournament.Application.Services;
using Mercurius.Modules.Tournament.Contracts;
using Mercurius.LAN.API.Data;
using Mercurius.Modules.Discovery;
using Mercurius.Modules.Discovery.Contracts;
using Mercurius.Modules.Identity;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Identity.Services;
using Mercurius.Modules.Sponsorship;
using Mercurius.Modules.Teams;
using Mercurius.Modules.Teams.Services;
using Platform.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Mercurius.Api.Tests;

public class ApiEndpointContractTests
{
    [Theory]
    [InlineData("GET", "v{version:apiVersion}/lan/tournaments/", "Tournaments")]
    [InlineData("GET", "v{version:apiVersion}/lan/tournaments/{tournamentId}", "Tournaments")]
    [InlineData("GET", "v{version:apiVersion}/lan/matches/{id}", "Matches")]
    [InlineData("GET", "v{version:apiVersion}/lan/sponsors/", "Sponsors")]
    [InlineData("GET", "v{version:apiVersion}/lan/sponsors/{id}", "Sponsors")]
    [InlineData("GET", "v{version:apiVersion}/lan/teams/", "Teams")]
    [InlineData("GET", "v{version:apiVersion}/lan/teams/{id:guid}", "Teams")]
    [InlineData("GET", "v{version:apiVersion}/lan/public/teams/{teamName}", "Public Teams")]
    [InlineData("GET", "v{version:apiVersion}/lan/public/users/{username}", "Users")]
    [InlineData("GET", "v{version:apiVersion}/lan/public/teams/{teamName}/match-summaries", "Public Teams")]
    [InlineData("GET", "v{version:apiVersion}/lan/search/", "Search")]
    public void PublicReadRoutes_AllowAnonymousAndKeepTags(string method, string routePattern, string expectedTag)
    {
        var endpoint = GetEndpoint(method, routePattern);

        Assert.Contains(endpoint.Metadata, metadata => metadata is IAllowAnonymous);
        Assert.Contains(expectedTag, endpoint.Metadata.GetMetadata<ITagsMetadata>()?.Tags ?? []);
    }

    [Fact]
    public void UserMatchSummaries_RequireAdminAuthorization_AndFormerPublicRouteIsAbsent()
    {
        var endpoint = GetEndpoint("GET", "v{version:apiVersion}/lan/users/{username:nonguid}/match-summaries");

        Assert.DoesNotContain(endpoint.Metadata, metadata => metadata is IAllowAnonymous);
        Assert.Contains(endpoint.Metadata.OfType<AuthorizeAttribute>(), metadata => metadata.Roles == "admin");
        Assert.DoesNotContain(GetEndpoints(), candidate => candidate.RoutePattern.RawText == "v{version:apiVersion}/lan/public/users/{username}/match-summaries");
    }

    [Fact]
    public void PublicProfileMatchSummaryResponse_UsesStableGuidJsonProperties()
    {
        var matchId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var tournamentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var scheduledStart = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var response = new PublicProfileMatchSummariesResponseDTO
        {
            PreviousMatches =
            [
                new PublicProfileMatchSummaryResponseDTO
                {
                    MatchId = matchId,
                    TournamentId = tournamentId,
                    TournamentName = "Public Cup",
                    OpponentDisplayName = "Opponent",
                    ScheduledStartTime = scheduledStart,
                    LifecycleState = MatchLifecycleState.Completed,
                    ResultKind = MatchResultKind.Score,
                    ParticipantScore = 1,
                    OpponentScore = 0,
                    RoundNumber = 1,
                    MatchNumber = 1
                }
            ]
        };

        var json = System.Text.Json.JsonSerializer.Serialize(
            response,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Contains($"\"matchId\":\"{matchId:D}\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"\"tournamentId\":\"{tournamentId:D}\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"\"scheduledStartTime\":{System.Text.Json.JsonSerializer.Serialize(scheduledStart)}", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"value\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reportedScore", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("GET", "v{version:apiVersion}/lan/users/me")]
    [InlineData("PUT", "v{version:apiVersion}/lan/users/me")]
    [InlineData("PATCH", "v{version:apiVersion}/lan/users/me")]
    [InlineData("GET", "v{version:apiVersion}/lan/users/me/username-availability")]
    [InlineData("GET", "v{version:apiVersion}/lan/users/")]
    [InlineData("POST", "v{version:apiVersion}/lan/users/me/resend-verification-email")]
    [InlineData("POST", "v{version:apiVersion}/lan/users/me/password-reset")]
    [InlineData("DELETE", "v{version:apiVersion}/lan/users/me")]
    [InlineData("POST", "v{version:apiVersion}/lan/teams/")]
    [InlineData("GET", "v{version:apiVersion}/lan/teams/me/summary")]
    [InlineData("GET", "v{version:apiVersion}/lan/teams/me/invites")]
    [InlineData("GET", "v{version:apiVersion}/lan/teams/me/sent-invites")]
    [InlineData("DELETE", "v{version:apiVersion}/lan/teams/{id:guid}/members/me")]
    [InlineData("DELETE", "v{version:apiVersion}/lan/teams/{id:guid}/members/{userId:guid}")]
    [InlineData("DELETE", "v{version:apiVersion}/lan/teams/{id:guid}")]
    [InlineData("POST", "v{version:apiVersion}/lan/teams/{id:guid}/invites")]
    [InlineData("DELETE", "v{version:apiVersion}/lan/teams/{id}/invites/{inviteId}")]
    [InlineData("PATCH", "v{version:apiVersion}/lan/team-invites/{inviteId:guid}")]
    [InlineData("PUT", "v{version:apiVersion}/lan/teams/{id}/captain")]
    [InlineData("PUT", "v{version:apiVersion}/lan/teams/{id}/logo")]
    [InlineData("DELETE", "v{version:apiVersion}/lan/teams/{id}/logo")]
    [InlineData("GET", "v{version:apiVersion}/lan/tournaments/{tournamentId:guid}/registrations/me")]
    [InlineData("GET", "v{version:apiVersion}/lan/matches/{id}/me")]
    [InlineData("GET", "v{version:apiVersion}/lan/tournaments/{tournamentId:guid}/registrations/individual/eligibility")]
    [InlineData("GET", "v{version:apiVersion}/lan/tournaments/{tournamentId:guid}/registrations/teams/{teamId:guid}/eligibility")]
    [InlineData("POST", "v{version:apiVersion}/lan/tournaments/{tournamentId:guid}/registrations/teams/{teamId:guid}/roster/eligibility")]
    [InlineData("PUT", "v{version:apiVersion}/lan/tournaments/{tournamentId:guid}/registrations/individual/me")]
    [InlineData("DELETE", "v{version:apiVersion}/lan/tournaments/{tournamentId:guid}/registrations/individual/me")]
    [InlineData("PUT", "v{version:apiVersion}/lan/tournaments/{tournamentId:guid}/registrations/teams/{teamId:guid}/roster")]
    [InlineData("DELETE", "v{version:apiVersion}/lan/tournaments/{tournamentId:guid}/registrations/teams/{teamId:guid}")]
    [InlineData("PATCH", "v{version:apiVersion}/lan/tournaments/{tournamentId:guid}/registrations/roster-members/{rosterMemberId:guid}")]
    [InlineData("POST", "v{version:apiVersion}/lan/matches/{id}/confirm-ended")]
    [InlineData("POST", "v{version:apiVersion}/lan/matches/{id}/forfeit")]
    [InlineData("PUT", "v{version:apiVersion}/lan/matches/{id}/score")]
    public void AuthenticatedUserRoutes_RequireAuthorization(string method, string routePattern)
    {
        var endpoint = GetEndpoint(method, routePattern);

        AssertRequiresAuthorization(endpoint);
    }

    [Theory]
    [InlineData("POST", "v{version:apiVersion}/lan/tournaments/")]
    [InlineData("PATCH", "v{version:apiVersion}/lan/tournaments/{tournamentId}")]
    [InlineData("DELETE", "v{version:apiVersion}/lan/tournaments/{tournamentId}")]
    [InlineData("PUT", "v{version:apiVersion}/lan/tournaments/{tournamentId}/sponsors")]
    [InlineData("PUT", "v{version:apiVersion}/lan/tournaments/{tournamentId}/lifecycle-state")]
    [InlineData("PUT", "v{version:apiVersion}/lan/matches/{id}")]
    [InlineData("POST", "v{version:apiVersion}/lan/matches/{id}/resolve")]
    [InlineData("POST", "v{version:apiVersion}/lan/matches/{id}/reverse")]
    [InlineData("POST", "v{version:apiVersion}/lan/matches/{id}/admin/forfeit")]
    [InlineData("POST", "v{version:apiVersion}/lan/sponsors/")]
    [InlineData("PATCH", "v{version:apiVersion}/lan/sponsors/{id}")]
    [InlineData("DELETE", "v{version:apiVersion}/lan/sponsors/{id}")]
    [InlineData("POST", "v{version:apiVersion}/lan/users/")]
    [InlineData("GET", "v{version:apiVersion}/lan/users/{id:guid}")]
    [InlineData("PATCH", "v{version:apiVersion}/lan/users/{id:guid}")]
    [InlineData("DELETE", "v{version:apiVersion}/lan/users/{id:guid}")]
    [InlineData("DELETE", "v{version:apiVersion}/lan/users/{username:nonguid}")]
    [InlineData("DELETE", "v{version:apiVersion}/lan/users/{username}/account")]
    [InlineData("GET", "v{version:apiVersion}/lan/tournaments/{tournamentId:guid}/registrations/admin/")]
    [InlineData("DELETE", "v{version:apiVersion}/lan/tournaments/{tournamentId:guid}/registrations/admin/users/{userId:guid}")]
    [InlineData("DELETE", "v{version:apiVersion}/lan/tournaments/{tournamentId:guid}/registrations/admin/teams/{teamId:guid}")]
    public void AdminRoutes_RequireAdminAuthorization(string method, string routePattern)
    {
        var endpoint = GetEndpoint(method, routePattern);

        AssertRequiresAuthorization(endpoint);
        Assert.Contains(endpoint.Metadata.OfType<AuthorizeAttribute>(), metadata => metadata.Roles == "admin");
    }

    [Theory]
    [InlineData("POST", "/internal/discovery/search-index-rebuild-jobs/")]
    [InlineData("GET", "/internal/discovery/search-index-rebuild-jobs/{jobId:guid}")]
    public void DiscoveryRebuildRoutes_RequireAdminAuthorization(string method, string routePattern)
    {
        var endpoint = GetEndpoint(method, routePattern);

        AssertRequiresAuthorization(endpoint);
        Assert.Contains(endpoint.Metadata.OfType<AuthorizeAttribute>(), metadata => metadata.Roles == "admin");
    }

    [Theory]
    [InlineData("POST", "v{version:apiVersion}/lan/tournaments/{id}/users")]
    [InlineData("DELETE", "v{version:apiVersion}/lan/tournaments/{id}/users/{userId}")]
    [InlineData("POST", "v{version:apiVersion}/lan/tournaments/{id}/teams")]
    [InlineData("DELETE", "v{version:apiVersion}/lan/tournaments/{id}/teams/{teamId}")]
    [InlineData("DELETE", "v{version:apiVersion}/lan/teams/{id}/users/{userId}")]
    [InlineData("PUT", "v{version:apiVersion}/lan/teams/{id}")]
    [InlineData("POST", "v{version:apiVersion}/lan/teams/{id}/users/invite/{userId}")]
    [InlineData("GET", "v{version:apiVersion}/lan/players/")]
    [InlineData("POST", "v{version:apiVersion}/lan/users/me/complete-profile")]
    [InlineData("POST", "v{version:apiVersion}/lan/teams/{id}/leave")]
    [InlineData("POST", "v{version:apiVersion}/lan/teams/{id}/invites/{userId}")]
    [InlineData("PUT", "v{version:apiVersion}/lan/teams/invites/{inviteId}")]
    [InlineData("POST", "v{version:apiVersion}/lan/teams/{id}/logo")]
    [InlineData("POST", "v{version:apiVersion}/lan/tournaments/{id}/start")]
    [InlineData("POST", "v{version:apiVersion}/lan/tournaments/{id}/reset")]
    [InlineData("POST", "v{version:apiVersion}/lan/tournaments/{id}/complete")]
    [InlineData("POST", "v{version:apiVersion}/lan/tournaments/{id}/cancel")]
    [InlineData("GET", "v{version:apiVersion}/lan/tournaments/{tournamentId:guid}/registrations/eligibility/individual")]
    [InlineData("GET", "v{version:apiVersion}/lan/tournaments/{tournamentId:guid}/registrations/eligibility/teams/{teamId:guid}")]
    [InlineData("POST", "v{version:apiVersion}/lan/tournaments/{tournamentId:guid}/registrations/eligibility/teams/{teamId:guid}/roster")]
    [InlineData("POST", "v{version:apiVersion}/lan/tournaments/{tournamentId:guid}/registrations/individual")]
    [InlineData("POST", "v{version:apiVersion}/lan/tournaments/{tournamentId:guid}/registrations/roster-confirmations/{rosterMemberId:guid}/confirm")]
    [InlineData("GET", "v{version:apiVersion}/lan/games/")]
    [InlineData("GET", "v{version:apiVersion}/lan/games/{id}")]
    [InlineData("PATCH", "v{version:apiVersion}/lan/games/{id}")]
    [InlineData("DELETE", "v{version:apiVersion}/lan/games/{id}")]
    [InlineData("PUT", "v{version:apiVersion}/lan/games/{id}/sponsors")]
    [InlineData("PUT", "v{version:apiVersion}/lan/games/{id}/lifecycle-state")]
    [InlineData("GET", "v{version:apiVersion}/lan/games/{gameId:guid}/registrations/me")]
    [InlineData("GET", "v{version:apiVersion}/lan/games/{gameId:guid}/registrations/admin/")]
    [InlineData("PATCH", "v{version:apiVersion}/lan/games/{gameId:guid}/registrations/roster-members/{rosterMemberId:guid}")]
    public void RemovedOrUnavailableRoutes_StayUnavailable(string method, string routePattern)
    {
        var endpoints = GetEndpoints(method);

        Assert.DoesNotContain(endpoints, endpoint => endpoint.RoutePattern.RawText == routePattern);
    }

    [Fact]
    public void TeamManagementHub_RemainsMappedAndAuthenticated()
    {
        var endpoint = GetRouteEndpoint(TeamManagementHub.Route);

        AssertRequiresAuthorization(endpoint);
    }

    [Fact]
    public void TeamManagementHub_ClosesConnectionOnAuthenticationExpiration()
    {
        var endpoint = GetRouteEndpoint($"{TeamManagementHub.Route}/negotiate");
        var options = Assert.IsType<HttpConnectionDispatcherOptions>(
            endpoint.Metadata.GetMetadata<HttpConnectionDispatcherOptions>());

        Assert.True(options.CloseOnAuthenticationExpiration);
    }

    private static void AssertRequiresAuthorization(RouteEndpoint endpoint)
    {
        Assert.DoesNotContain(endpoint.Metadata, metadata => metadata is IAllowAnonymous);
        Assert.Contains(endpoint.Metadata, metadata => metadata is IAuthorizeData);
    }

    private static RouteEndpoint GetEndpoint(string method, string routePattern)
    {
        return GetEndpoints(method)
            .Single(endpoint => endpoint.RoutePattern.RawText == routePattern);
    }

    private static RouteEndpoint GetRouteEndpoint(string routePattern)
    {
        return GetEndpoints()
            .Single(endpoint => endpoint.RoutePattern.RawText == routePattern);
    }

    private static IReadOnlyList<RouteEndpoint> GetEndpoints(string method)
    {
        return GetEndpoints()
            .Where(endpoint => endpoint.Metadata
                .OfType<IHttpMethodMetadata>()
                .Any(metadata => metadata.HttpMethods.Contains(method)))
            .ToList();
    }

    private static IReadOnlyList<RouteEndpoint> GetEndpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddApiVersioning();
        builder.Services.AddRealtimeNotificationServices();
        RegisterEndpointServices(builder.Services);
        builder.Services.AddSponsorshipModule<MercuriusDBContext>(builder.Configuration);
        builder.Services.AddHttpConventions();

        var app = builder.Build();
        app.MapTournamentModule();
        app.MapTeamsModule();
        app.MapSponsorshipModule();
        app.MapIdentityModule();
        app.MapDiscoveryModule();
        app.MapHub<TeamManagementHub>(
                TeamManagementHub.Route,
                options => options.CloseOnAuthenticationExpiration = true)
            .RequireAuthorization();

        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();
    }

    private static void RegisterEndpointServices(IServiceCollection services)
    {
        services.AddScoped<ITournamentQueries>(_ => throw new NotSupportedException());
        services.AddScoped<ITournamentManagementCommands>(_ => throw new NotSupportedException());
        services.AddScoped<ITournamentLifecycleCommands>(_ => throw new NotSupportedException());
        services.AddScoped<ITournamentRegistrationService>(_ => throw new NotSupportedException());
        services.AddScoped<IMatchService>(_ => throw new NotSupportedException());
        services.AddScoped<PublicProfileMatchSummaryReadService>(_ => throw new NotSupportedException());
        services.AddScoped<ITeamEndpointService>(_ => throw new NotSupportedException());
        services.AddScoped<IUserService>(_ => throw new NotSupportedException());
        services.AddScoped<IIdentityModule>(_ => throw new NotSupportedException());
        services.AddScoped<IDiscoveryModule>(_ => throw new NotSupportedException());
    }
}
