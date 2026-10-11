using Mercurius.LAN.API.Configuration;
using Mercurius.Modules.Identity;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Identity.Services;
using Platform;
using Platform.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Mercurius.Api.Tests;

public class UserEndpointRouteTests
{
    [Fact]
    public void UsernameDeleteRoute_RequiresAdminAuthorization()
    {
        var endpoint = GetUserRouteEndpoint("DELETE", "v{version:apiVersion}/lan/users/{username:nonguid}");

        var authorizeMetadata = endpoint.Metadata.OfType<AuthorizeAttribute>().ToList();

        Assert.DoesNotContain(endpoint.Metadata, metadata => metadata is IAllowAnonymous);
        Assert.Contains(authorizeMetadata, metadata => metadata.Roles == "admin");
    }

    [Fact]
    public void DetailedUsernameProfileRoute_RequiresAdminAuthorization()
    {
        var endpoint = GetUserRouteEndpoint("GET", "v{version:apiVersion}/lan/users/{username:nonguid}");

        Assert.DoesNotContain(endpoint.Metadata, metadata => metadata is IAllowAnonymous);
        Assert.Contains(endpoint.Metadata.OfType<AuthorizeAttribute>(), metadata => metadata.Roles == "admin");
    }

    [Fact]
    public void PublicUsernameProfileRoute_RemainsAnonymous()
    {
        var endpoint = GetUserRouteEndpoint("GET", "v{version:apiVersion}/lan/public/users/{username}");

        Assert.Contains(endpoint.Metadata, metadata => metadata is IAllowAnonymous);
    }

    [Fact]
    public void UsernameDeleteCompatibilityRoute_RemainsAvailable()
    {
        var endpoint = GetUserRouteEndpoint("DELETE", "v{version:apiVersion}/lan/users/{username}/account");

        Assert.NotNull(endpoint);
    }

    [Fact]
    public void UserCollectionRoute_RequiresAuthorization()
    {
        var endpoint = GetUserRouteEndpoint("GET", "v{version:apiVersion}/lan/users/");

        Assert.DoesNotContain(endpoint.Metadata, metadata => metadata is IAllowAnonymous);
        Assert.Contains(endpoint.Metadata, metadata => metadata is IAuthorizeData);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public void CurrentUserProfileRoutes_RequireAuthorization(string method)
    {
        var endpoint = GetUserRouteEndpoint(method, "v{version:apiVersion}/lan/users/me");

        Assert.DoesNotContain(endpoint.Metadata, metadata => metadata is IAllowAnonymous);
        Assert.Contains(endpoint.Metadata, metadata => metadata is IAuthorizeData);
    }

    [Theory]
    [InlineData("POST", "v{version:apiVersion}/lan/users/me/resend-verification-email")]
    [InlineData("POST", "v{version:apiVersion}/lan/users/me/password-reset")]
    public void CurrentUserIdentityCommandRoutes_RequireAuthorization(string method, string routePattern)
    {
        var endpoint = GetUserRouteEndpoint(method, routePattern);

        Assert.DoesNotContain(endpoint.Metadata, metadata => metadata is IAllowAnonymous);
        Assert.Contains(endpoint.Metadata, metadata => metadata is IAuthorizeData);
    }

    [Theory]
    [InlineData("POST", "v{version:apiVersion}/lan/users/me/complete-profile")]
    public void ReplacedCurrentUserActionRoutes_AreRemoved(string method, string routePattern)
    {
        var endpoints = GetUserRouteEndpoints(method);

        Assert.DoesNotContain(endpoints, endpoint => endpoint.RoutePattern.RawText == routePattern);
    }

    [Fact]
    public void UserCollectionRoute_UsesAuthenticatedSearchRateLimitPolicy()
    {
        var endpoint = GetUserRouteEndpoint("GET", "v{version:apiVersion}/lan/users/");

        var rateLimitMetadata = Assert.Single(endpoint.Metadata.Where(metadata =>
            metadata.GetType().GetProperty("PolicyName")?.GetValue(metadata) is not null));
        var policyName = rateLimitMetadata.GetType().GetProperty("PolicyName")!.GetValue(rateLimitMetadata);

        Assert.Equal(RateLimitPolicies.AuthenticatedSearch, policyName);
    }

    [Fact]
    public void AdminPickerRoute_RequiresAdminAuthorizationAndUnconditionalSearchRateLimit()
    {
        var endpoint = GetUserRouteEndpoint("GET", "v{version:apiVersion}/lan/users/admins");

        Assert.DoesNotContain(endpoint.Metadata, metadata => metadata is IAllowAnonymous);
        Assert.Contains(endpoint.Metadata.OfType<AuthorizeAttribute>(), metadata => metadata.Roles == "admin");
        var rateLimitMetadata = Assert.Single(endpoint.Metadata.Where(metadata =>
            metadata.GetType().GetProperty("PolicyName")?.GetValue(metadata) is not null));
        Assert.Equal(
            RateLimitPolicies.AnonymousSearch,
            rateLimitMetadata.GetType().GetProperty("PolicyName")!.GetValue(rateLimitMetadata));
    }

    [Fact]
    public void GuidAndUsernameDeleteRoutes_HaveDistinctRoutePatterns()
    {
        var endpoints = GetUserRouteEndpoints("DELETE").ToList();

        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "v{version:apiVersion}/lan/users/{id:guid}");
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "v{version:apiVersion}/lan/users/{username:nonguid}");
    }

    [Fact]
    public void NonGuidRouteConstraint_RejectsGuidShapedValues()
    {
        var constraint = new NonGuidRouteConstraint();
        var values = new RouteValueDictionary
        {
            ["username"] = "0123456789abcdef0123456789abcdef"
        };

        var matches = constraint.Match(null, null, "username", values, RouteDirection.IncomingRequest);

        Assert.False(matches);
    }

    [Fact]
    public void NonGuidRouteConstraint_AllowsRegularUsernames()
    {
        var constraint = new NonGuidRouteConstraint();
        var values = new RouteValueDictionary
        {
            ["username"] = "PlayerOne"
        };

        var matches = constraint.Match(null, null, "username", values, RouteDirection.IncomingRequest);

        Assert.True(matches);
    }

    private static RouteEndpoint GetUserRouteEndpoint(string method, string routePattern)
    {
        return GetUserRouteEndpoints(method)
            .Single(endpoint => endpoint.RoutePattern.RawText == routePattern);
    }

    private static IEnumerable<RouteEndpoint> GetUserRouteEndpoints(string method)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddApiVersioning();
        builder.Services.AddHttpConventions();
        builder.Services.AddScoped<IUserService>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<IIdentityModule>(_ => throw new NotSupportedException());

        var app = builder.Build();
        app.MapIdentityModule();

        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata
                .OfType<IHttpMethodMetadata>()
                .Any(metadata => metadata.HttpMethods.Contains(method)));
    }
}
