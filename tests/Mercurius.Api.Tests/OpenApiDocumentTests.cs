using System.Security.Claims;
using Mercurius.LAN.API.Hubs;
using Mercurius.Modules.Tournament;
using Mercurius.Modules.Tournament.Application.Services;
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
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.Swagger;

namespace Mercurius.Api.Tests;

public class OpenApiDocumentTests
{
    [Fact]
    public async Task SwaggerDocument_GeneratesV1DocumentWithRepresentativePaths()
    {
        using var app = CreateSwaggerApp();
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();

        try
        {
            var swaggerProvider = app.Services.GetRequiredService<ISwaggerProvider>();
            var endpointCount = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).Count();
            var apiDescriptionCount = app.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>()
                .ApiDescriptionGroups.Items.Sum(group => group.Items.Count);

            var document = swaggerProvider.GetSwagger("v1");

            Assert.True(endpointCount > 0, "The Swagger test host should have mapped endpoints.");
            Assert.True(apiDescriptionCount > 0, "ApiExplorer should discover the mapped endpoints.");
            Assert.Equal("Mercurius API", document.Info.Title);
            AssertPathHasOperation(document, "/v1/lan/tournaments", OperationType.Get);
            AssertPathHasOperation(document, "/v1/lan/tournaments/{tournamentId}", OperationType.Get);
            AssertPathHasOperation(document, "/v1/lan/tournaments/{tournamentId}", OperationType.Patch);
            AssertPathHasOperation(document, "/v1/lan/tournaments/{tournamentId}/lifecycle-state", OperationType.Put);
            AssertPathHasOperation(document, "/v1/lan/teams", OperationType.Get);
            AssertPagedRawArrayOperation(document, "/v1/lan/tournaments");
            AssertPagedRawArrayOperation(document, "/v1/lan/teams");
            AssertPathHasOperation(document, "/v1/lan/teams/{id}", OperationType.Delete);
            AssertPathHasOperation(document, "/v1/lan/teams/{id}/members/me", OperationType.Delete);
            AssertPathHasOperation(document, "/v1/lan/teams/{id}/invites", OperationType.Post);
            AssertPathHasOperation(document, "/v1/lan/team-invites/{inviteId}", OperationType.Patch);
            AssertPathHasOperation(document, "/v1/lan/teams/{id}/logo", OperationType.Put);
            AssertPathHasOperation(document, "/v1/lan/public/teams/{teamName}", OperationType.Get);
            AssertPathHasOperation(document, "/v1/lan/public/teams/{teamName}/match-summaries", OperationType.Get);
            AssertPathHasOperation(document, "/v1/lan/users/me", OperationType.Get);
            AssertPathHasOperation(document, "/v1/lan/public/users/{username}", OperationType.Get);
            AssertPathHasOperation(document, "/v1/lan/users/{username}/match-summaries", OperationType.Get);
            AssertOperationHasTag(document, "/v1/lan/users/{username}/match-summaries", OperationType.Get, "Users");
            AssertOperationHasTag(document, "/v1/lan/public/teams/{teamName}/match-summaries", OperationType.Get, "Public Teams");
            AssertPagedRawArrayOperation(document, "/v1/lan/users");
            AssertPagedRawArrayOperation(document, "/v1/lan/users/admins");
            AssertPathHasOperation(document, "/v1/lan/tournaments/{tournamentId}/registrations/me", OperationType.Get);
            AssertPathHasOperation(document, "/v1/lan/tournaments/{tournamentId}/registrations/individual/eligibility", OperationType.Get);
            AssertPathHasOperation(document, "/v1/lan/tournaments/{tournamentId}/registrations/teams/{teamId}/eligibility", OperationType.Get);
            AssertPathHasOperation(document, "/v1/lan/tournaments/{tournamentId}/registrations/teams/{teamId}/roster/eligibility", OperationType.Post);
            AssertPathHasOperation(document, "/v1/lan/tournaments/{tournamentId}/registrations/individual/me", OperationType.Put);
            AssertPathHasOperation(document, "/v1/lan/tournaments/{tournamentId}/registrations/roster-members/{rosterMemberId}", OperationType.Patch);
            AssertPathHasOperation(document, "/v1/lan/tournaments/{tournamentId}/registrations/admin", OperationType.Get);
            AssertPagedRawArrayOperation(document, "/v1/lan/tournaments/{tournamentId}/registrations/admin");
            AssertPathHasOperation(document, "/v1/lan/search", OperationType.Get);
            AssertPathHasOperation(document, "/v1/lan/sponsors", OperationType.Post);
            AssertPathHasOperation(document, "/v1/lan/matches/{id}", OperationType.Put);
            AssertPathIsAbsent(document, "/v1/lan/games");
            AssertPathIsAbsent(document, "/v1/lan/games/{id}");
            AssertPathIsAbsent(document, "/v1/lan/games/{id}/sponsors");
            AssertPathIsAbsent(document, "/v1/lan/games/{id}/lifecycle-state");
            AssertPathIsAbsent(document, "/v1/lan/tournaments/{id}/start");
            AssertPathIsAbsent(document, "/v1/lan/teams/{id}/leave");
            AssertPathIsAbsent(document, "/v1/lan/users/me/complete-profile");
            Assert.DoesNotContain(document.Paths.Keys, path => path.StartsWith("/v2/", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static void AssertPathIsAbsent(OpenApiDocument document, string path)
    {
        Assert.DoesNotContain(document.Paths.Keys, candidate => string.Equals(candidate.TrimEnd('/'), path, StringComparison.Ordinal));
    }

    private static void AssertPathHasOperation(OpenApiDocument document, string path, OperationType operation)
    {
        var matchingPath = document.Paths.Keys.SingleOrDefault(candidate =>
            string.Equals(candidate.TrimEnd('/'), path, StringComparison.Ordinal));

        Assert.True(matchingPath is not null, $"Missing {path}. Available paths: {string.Join(", ", document.Paths.Keys.OrderBy(key => key, StringComparer.Ordinal))}");
        Assert.True(document.Paths[matchingPath].Operations.ContainsKey(operation), $"{path} should expose {operation}.");
    }

    private static void AssertOperationHasTag(OpenApiDocument document, string path, OperationType operation, string expectedTag)
    {
        var matchingPath = document.Paths.Keys.Single(candidate =>
            string.Equals(candidate.TrimEnd('/'), path, StringComparison.Ordinal));

        Assert.Contains(expectedTag, document.Paths[matchingPath].Operations[operation].Tags.Select(tag => tag.Name));
    }

    private static void AssertPagedRawArrayOperation(OpenApiDocument document, string path)
    {
        var matchingPath = document.Paths.Keys.Single(candidate =>
            string.Equals(candidate.TrimEnd('/'), path, StringComparison.Ordinal));
        var operation = document.Paths[matchingPath].Operations[OperationType.Get];

        foreach (var name in new[] { "page", "pageSize" })
        {
            var parameter = Assert.Single(operation.Parameters, parameter => parameter.Name == name);
            Assert.Equal(ParameterLocation.Query, parameter.In);
            Assert.False(parameter.Required);
            Assert.Equal("integer", parameter.Schema.Type);
        }

        var successResponse = operation.Responses["200"];
        Assert.Contains(successResponse.Content.Values, mediaType => mediaType.Schema.Type == "array");
    }

    private static WebApplication CreateSwaggerApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddVersionedSwagger(
            builder.Environment,
            documentTitle: "Mercurius API",
            includeXmlComments: false,
            useEnumSchemaFilter: true);
        builder.Services.AddAuthorization();
        builder.Services.AddRealtimeNotificationServices();
        RegisterEndpointServices(builder.Services);
        builder.Services.AddSponsorshipModule<MercuriusDBContext>(builder.Configuration);
        builder.Services.AddHttpConventions();

        var app = builder.Build();
        app.Services.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "auth0|swagger-test"),
                new Claim(ClaimTypes.Role, "admin")
            ], "SwaggerTest"))
        };

        app.MapTournamentModule();
        app.MapTeamsModule();
        app.MapSponsorshipModule();
        app.MapIdentityModule();
        app.MapDiscoveryModule();
        app.MapHub<TeamManagementHub>(
                TeamManagementHub.Route,
                options => options.CloseOnAuthenticationExpiration = true)
            .RequireAuthorization();

        return app;
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
