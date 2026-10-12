using System.Net;
using System.Text;
using Mercurius.Modules.Identity.Options;
using Mercurius.Modules.Identity.Services.Auth0;
using Mercurius.Modules.Shared.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mercurius.Modules.Identity.Tests;

public class Auth0ManagementServiceAdminRoleTests
{
    [Fact]
    public async Task HasAdminRoleAsync_UsesExactRoleNameAndReusesManagementToken()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal))
                return Json("""{"access_token":"management-token","expires_in":3600}""");

            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("management-token", request.Headers.Authorization?.Parameter);
            return request.RequestUri.AbsolutePath.Contains("admin-user", StringComparison.Ordinal)
                ? Json("""[{"id":"role-1","name":"admin"}]""")
                : Json("""[{"id":"role-2","name":"manager"}]""");
        });
        var service = CreateService(handler);

        Assert.True(await service.HasAdminRoleAsync("auth0|admin-user"));
        Assert.False(await service.HasAdminRoleAsync("auth0|regular-user"));

        Assert.Equal(1, handler.TokenRequestCount);
        Assert.Equal(2, handler.ManagementRequestCount);
        Assert.Equal("https://example.auth0.com/api/v2/users/auth0|admin-user/roles?per_page=100&page=0", handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task GetAdminUserIdsAsync_PaginatesRolesAndMembersAndDeduplicatesIds()
    {
        var handler = new RecordingHandler(request =>
        {
            var pathAndQuery = request.RequestUri!.PathAndQuery;
            if (request.RequestUri.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal))
                return Json("""{"access_token":"management-token","expires_in":3600}""");

            return pathAndQuery switch
            {
                "/api/v2/roles?name_filter=admin&type=tenant&per_page=100&page=0" => Json("[" + string.Join(',', Enumerable.Repeat("{\"id\":\"admin-role\",\"name\":\"admin\"}", 100)) + "]"),
                "/api/v2/roles?name_filter=admin&type=tenant&per_page=100&page=1" => Json("""[{"id":"admin-role","name":"admin"}]"""),
                "/api/v2/roles/admin-role/users?take=100" => Json("""{"users":[{"user_id":"auth0|one"}],"next":"user-cursor"}"""),
                "/api/v2/roles/admin-role/users?take=100&from=user-cursor" => Json("""{"users":[{"user_id":"auth0|one"},{"user_id":"auth0|two"}],"next":null}"""),
                _ => throw new InvalidOperationException($"Unexpected Auth0 request: {pathAndQuery}")
            };
        });
        var service = CreateService(handler);

        var userIds = await service.GetAdminUserIdsAsync();

        Assert.Equal(["auth0|one", "auth0|two"], userIds.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(1, handler.TokenRequestCount);
        Assert.Equal(4, handler.ManagementRequestCount);
    }

    [Fact]
    public async Task HasAdminRoleAsync_ThrowsServiceUnavailableWhenAuth0Fails()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal))
                return Json("""{"access_token":"management-token","expires_in":3600}""");

            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        var service = CreateService(handler);

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => service.HasAdminRoleAsync("auth0|user"));
        Assert.Equal(1, handler.TokenRequestCount);
    }

    [Fact]
    public async Task HasAdminRoleAsync_MapsAuth0TimeoutToServiceUnavailable()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal))
                return Json("""{"access_token":"management-token","expires_in":3600}""");

            throw new TaskCanceledException("Auth0 request timed out.");
        });
        var service = CreateService(handler);

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => service.HasAdminRoleAsync("auth0|user"));
    }

    [Fact]
    public async Task HasAdminRoleAsync_PreservesCallerCancellation()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal))
                return Json("""{"access_token":"management-token","expires_in":3600}""");

            throw new OperationCanceledException("Caller canceled.");
        });
        var service = CreateService(handler);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.HasAdminRoleAsync("auth0|user", cancellationSource.Token));
    }

    [Fact]
    public async Task HasAdminRoleAsync_ThrowsServiceUnavailableWhenManagementConfigurationIsMissing()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("No request expected."));
        var service = CreateService(handler, configured: false);

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => service.HasAdminRoleAsync("auth0|user"));
        Assert.Empty(handler.Requests);
    }

    private static Auth0ManagementService CreateService(RecordingHandler handler, bool configured = true)
    {
        var options = new Auth0ManagementOptions
        {
            Authority = configured ? "https://example.auth0.com/" : null,
            ManagementAudience = configured ? "https://example.auth0.com/api/v2/" : null,
            ManagementClientId = configured ? "client-id" : null,
            ManagementClientSecret = configured ? "client-secret" : null
        };
        return new Auth0ManagementService(
            new HttpClient(handler),
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<Auth0ManagementService>.Instance);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public int TokenRequestCount => Requests.Count(request => request.RequestUri!.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal));
        public int ManagementRequestCount => Requests.Count - TokenRequestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responseFactory(request));
        }
    }
}
