using System.Net;
using System.Text;
using Mercurius.Modules.Identity.Application.Services.Auth0;
using Mercurius.Modules.Identity.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mercurius.Modules.Identity.Tests;

public sealed class Auth0ManagementServiceTests
{
    [Fact]
    public async Task GetUserCreatedAt_ConvertsExplicitOffsetToUtc()
    {
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("oauth/token", StringComparison.Ordinal))
                return Json("{\"access_token\":\"token\",\"expires_in\":3600}");
            return Json("{\"created_at\":\"2026-01-15T12:30:00+02:00\"}");
        }));
        var service = CreateService(httpClient);

        var createdAt = await service.GetUserCreatedAtAsync("google-oauth2|subject");

        Assert.Equal(new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc), createdAt);
    }

    [Fact]
    public async Task DeleteUser_EscapesExactSubjectAndTreatsNotFoundAsSuccess()
    {
        string? deletePath = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("oauth/token", StringComparison.Ordinal))
                return Json("{\"access_token\":\"token\",\"expires_in\":3600}");
            deletePath = request.RequestUri.OriginalString;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var service = CreateService(httpClient);

        await service.DeleteUserAsync("google-oauth2|subject/with space");

        Assert.Contains("google-oauth2%7Csubject%2Fwith%20space", deletePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetUserCreatedAt_TreatsNotFoundAsAlreadyAbsent()
    {
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("oauth/token", StringComparison.Ordinal))
                return Json("{\"access_token\":\"token\",\"expires_in\":3600}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var service = CreateService(httpClient);

        Assert.Null(await service.GetUserCreatedAtAsync("auth0|absent"));
    }

    [Fact]
    public async Task GetUserCreatedAt_ReportsMissingManagementConfigurationWithoutCallingHttp()
    {
        using var httpClient = new HttpClient(new StubHandler(_ => throw new Xunit.Sdk.XunitException("HTTP should not be called.")));
        var service = new Auth0ManagementService(
            httpClient,
            Microsoft.Extensions.Options.Options.Create(new Auth0ManagementOptions { Authority = "https://tenant.example/" }),
            NullLogger<Auth0ManagementService>.Instance,
            TimeProvider.System);

        var exception = await Assert.ThrowsAsync<Auth0ManagementRequestException>(
            () => service.GetUserCreatedAtAsync("auth0|missing-config"));

        Assert.Equal("Auth0 Management API is not configured.", exception.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task GetUserCreatedAt_PropagatesProviderStatusForDurableRetry(HttpStatusCode statusCode)
    {
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("oauth/token", StringComparison.Ordinal))
                return Json("{\"access_token\":\"token\",\"expires_in\":3600}");
            return new HttpResponseMessage(statusCode);
        }));
        var service = CreateService(httpClient);

        var exception = await Assert.ThrowsAsync<Auth0ManagementRequestException>(
            () => service.GetUserCreatedAtAsync("auth0|retry"));

        Assert.Contains(((int)statusCode).ToString(), exception.Message, StringComparison.Ordinal);
    }

    private static Auth0ManagementService CreateService(HttpClient httpClient)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new Auth0ManagementOptions
        {
            Authority = "https://tenant.example/",
            ManagementAudience = "https://tenant.example/api/v2/",
            ManagementClientId = "client",
            ManagementClientSecret = "secret"
        });
        return new Auth0ManagementService(httpClient, options, NullLogger<Auth0ManagementService>.Instance, TimeProvider.System);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
