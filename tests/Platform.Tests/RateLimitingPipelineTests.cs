using System.Net;
using System.Security.Claims;
using System.Diagnostics;
using Platform;
using Platform.Extensions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Platform.Tests;

public class RateLimitingPipelineTests
{
    [Fact]
    public async Task UseSecurityPipeline_LimitsAnonymousProtectedRequestsBeforeAuthorization()
    {
        var services = CreateServices();
        await using var provider = services.BuildServiceProvider();
        var pipeline = CreateProtectedPipeline(provider, requireAdminRole: false);

        var firstResponse = await SendAsync(provider, pipeline, "/protected");
        var secondResponse = await SendAsync(provider, pipeline, "/protected");

        Assert.Equal(StatusCodes.Status401Unauthorized, firstResponse.StatusCode);
        Assert.Equal(StatusCodes.Status429TooManyRequests, secondResponse.StatusCode);
    }

    [Fact]
    public async Task UseSecurityPipeline_LimitsForbiddenRequestsBeforeAuthorization()
    {
        var services = CreateServices();
        await using var provider = services.BuildServiceProvider();
        var pipeline = CreateProtectedPipeline(provider, requireAdminRole: true);

        var firstResponse = await SendAsync(provider, pipeline, "/protected", "wrong-role");
        var secondResponse = await SendAsync(provider, pipeline, "/protected", "wrong-role");

        Assert.Equal(StatusCodes.Status403Forbidden, firstResponse.StatusCode);
        Assert.Equal(StatusCodes.Status429TooManyRequests, secondResponse.StatusCode);
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new DiagnosticListener("Platform.Tests"));
        services.AddRouting();
        services.AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                TestAuthenticationHandler.SchemeName,
                _ => { });
        services.AddAuthorization();
        services.AddFixedWindowRateLimiting(new FixedWindowRateLimitingOptions
        {
            GlobalPermitLimit = 1,
            PolicyPermitLimit = 1,
            Window = TimeSpan.FromMinutes(1),
            UnconditionalPolicyName = "anonymous-search",
            ConditionalPolicyName = "authenticated-search",
            ConditionalQueryParameterName = "query"
        });

        return services;
    }

    private static RequestDelegate CreateProtectedPipeline(IServiceProvider services, bool requireAdminRole)
    {
        var app = new ApplicationBuilder(services);
        app.UseRouting();
        app.UseSecurityPipeline();
        app.UseEndpoints(endpoints =>
        {
            var endpoint = endpoints.MapGet("/protected", () => Results.NoContent());
            if (requireAdminRole)
                endpoint.RequireAuthorization(new AuthorizeAttribute { Roles = "admin" });
            else
                endpoint.RequireAuthorization();
        });

        return app.Build();
    }

    private static async Task<HttpResponse> SendAsync(
        IServiceProvider requestServices,
        RequestDelegate pipeline,
        string path,
        string? identity = null)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = requestServices;
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        if (identity is not null)
            context.Request.Headers[TestAuthenticationHandler.IdentityHeaderName] = identity;

        await pipeline(context);

        return context.Response;
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "test";
        public const string IdentityHeaderName = "X-Test-Identity";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(IdentityHeaderName, out var identityName))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(
                [
                    new Claim("sub", identityName!),
                    new Claim(ClaimTypes.Role, "user")
                ],
                SchemeName);

            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, SchemeName)));
        }
    }
}
