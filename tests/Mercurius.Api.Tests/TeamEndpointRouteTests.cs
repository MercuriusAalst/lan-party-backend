using Mercurius.Modules.Teams;
using Mercurius.Modules.Teams.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Mercurius.Api.Tests;

public class TeamEndpointRouteTests
{
    [Theory]
    [InlineData("POST", "v{version:apiVersion}/lan/teams/")]
    [InlineData("GET", "v{version:apiVersion}/lan/teams/me/summary")]
    [InlineData("GET", "v{version:apiVersion}/lan/teams/me/invites")]
    [InlineData("GET", "v{version:apiVersion}/lan/teams/me/sent-invites")]
    [InlineData("DELETE", "v{version:apiVersion}/lan/teams/{id:guid}/members/me")]
    [InlineData("POST", "v{version:apiVersion}/lan/teams/{id:guid}/invites")]
    [InlineData("DELETE", "v{version:apiVersion}/lan/teams/{id}/invites/{inviteId}")]
    [InlineData("PATCH", "v{version:apiVersion}/lan/team-invites/{inviteId:guid}")]
    [InlineData("PUT", "v{version:apiVersion}/lan/teams/{id}/captain")]
    [InlineData("PUT", "v{version:apiVersion}/lan/teams/{id}/logo")]
    [InlineData("DELETE", "v{version:apiVersion}/lan/teams/{id}/logo")]
    public void UserOwnedTeamMutationRoutes_RequireAuthorization(string method, string routePattern)
    {
        var endpoint = GetTeamRouteEndpoint(method, routePattern);

        Assert.DoesNotContain(endpoint.Metadata, metadata => metadata is IAllowAnonymous);
        Assert.Contains(endpoint.Metadata, metadata => metadata is IAuthorizeData);
    }

    [Theory]
    [InlineData("DELETE", "v{version:apiVersion}/lan/teams/{id}/users/{userId}")]
    [InlineData("PUT", "v{version:apiVersion}/lan/teams/{id}")]
    [InlineData("DELETE", "v{version:apiVersion}/lan/teams/{id}")]
    [InlineData("POST", "v{version:apiVersion}/lan/teams/{id}/users/invite/{userId}")]
    [InlineData("PUT", "v{version:apiVersion}/lan/teams/{id}/users/invite/{userId}")]
    [InlineData("GET", "v{version:apiVersion}/lan/teams/users/{userId}/invites")]
    public void AdminOnlyTeamMutationRoutes_AreRemoved(string method, string routePattern)
    {
        var endpoints = GetTeamRouteEndpoints(method);

        Assert.DoesNotContain(endpoints, endpoint => endpoint.RoutePattern.RawText == routePattern);
    }

    [Theory]
    [InlineData("POST", "v{version:apiVersion}/lan/teams/{id}/leave")]
    [InlineData("POST", "v{version:apiVersion}/lan/teams/{id}/invites/{userId}")]
    [InlineData("PUT", "v{version:apiVersion}/lan/teams/invites/{inviteId}")]
    [InlineData("POST", "v{version:apiVersion}/lan/teams/{id}/logo")]
    public void ReplacedTeamActionRoutes_AreRemoved(string method, string routePattern)
    {
        var endpoints = GetTeamRouteEndpoints(method);

        Assert.DoesNotContain(endpoints, endpoint => endpoint.RoutePattern.RawText == routePattern);
    }

    private static RouteEndpoint GetTeamRouteEndpoint(string method, string routePattern)
    {
        return GetTeamRouteEndpoints(method)
            .Single(endpoint => endpoint.RoutePattern.RawText == routePattern);
    }

    private static IEnumerable<RouteEndpoint> GetTeamRouteEndpoints(string method)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddApiVersioning();
        builder.Services.AddScoped<ITeamEndpointService>(_ => throw new NotSupportedException());

        var app = builder.Build();
        app.MapTeamsModule();

        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata
                .OfType<IHttpMethodMetadata>()
                .Any(metadata => metadata.HttpMethods.Contains(method)));
    }
}
