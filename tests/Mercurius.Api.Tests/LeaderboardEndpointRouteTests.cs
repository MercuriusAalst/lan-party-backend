using Mercurius.Modules.Tournament;
using Mercurius.Modules.Tournament.Application.Services;
using Platform.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Mercurius.Api.Tests;

public sealed class LeaderboardEndpointRouteTests
{
    private const string Prefix = "v{version:apiVersion}/lan/tournaments/{tournamentId:guid}/leaderboard";

    [Fact]
    public void PublicLeaderboard_IsAnonymous()
    {
        var endpoint = GetEndpoint("GET", Prefix + "/");

        Assert.Contains(endpoint.Metadata, item => item is IAllowAnonymous);
    }

    [Fact]
    public void MatchOpponentProfile_RequiresAuthentication()
    {
        var endpoint = GetEndpoint("GET", "v{version:apiVersion}/lan/matches/{id:guid}/opponent-profile");

        Assert.DoesNotContain(endpoint.Metadata, item => item is IAllowAnonymous);
        Assert.Contains(endpoint.Metadata, item => item is IAuthorizeData);
    }

    [Theory]
    [InlineData("GET", Prefix + "/attempts")]
    [InlineData("POST", Prefix + "/attempts")]
    [InlineData("PUT", Prefix + "/attempts/{attemptId:guid}")]
    [InlineData("DELETE", Prefix + "/attempts/{attemptId:guid}")]
    public void ManagementRoutes_RequireAdmin(string method, string pattern)
    {
        var endpoint = GetEndpoint(method, pattern);
        var authorization = endpoint.Metadata.OfType<AuthorizeAttribute>().ToList();

        Assert.DoesNotContain(endpoint.Metadata, item => item is IAllowAnonymous);
        Assert.Contains(authorization, item => item.Roles == "admin");
    }

    [Fact]
    public void FormerAdminHistoryRoute_IsNotExposed()
    {
        Assert.DoesNotContain(GetEndpoints(), endpoint => endpoint.RoutePattern.RawText == Prefix + "/admin" && endpoint.Metadata
            .OfType<IHttpMethodMetadata>().Any(item => item.HttpMethods.Contains("GET")));
    }

    private static RouteEndpoint GetEndpoint(string method, string pattern)
    {
        return GetEndpoints().Single(endpoint => endpoint.RoutePattern.RawText == pattern && endpoint.Metadata
            .OfType<IHttpMethodMetadata>().Any(item => item.HttpMethods.Contains(method)));
    }

    private static IReadOnlyList<RouteEndpoint> GetEndpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddApiVersioning();
        builder.Services.AddHttpConventions();
        builder.Services.AddScoped<ITournamentQueries>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<ITournamentManagementCommands>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<ITournamentLifecycleCommands>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<ITournamentRegistrationService>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<IMatchService>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<ILeaderboardService>(_ => throw new NotSupportedException());
        var app = builder.Build();
        app.MapTournamentModule();

        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();
    }
}
