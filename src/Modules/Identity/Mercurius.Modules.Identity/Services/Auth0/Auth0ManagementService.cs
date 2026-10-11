using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mercurius.Modules.Identity.DTOs;
using Mercurius.Modules.Identity.Options;
using Mercurius.Modules.Shared.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mercurius.Modules.Identity.Services.Auth0;

internal sealed class Auth0ManagementService : IAuth0ManagementService
{
    private readonly HttpClient _httpClient;
    private readonly Auth0ManagementOptions _options;
    private readonly ILogger<Auth0ManagementService> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _managementToken;
    private DateTimeOffset _managementTokenExpiresAtUtc;

    public Auth0ManagementService(
        HttpClient httpClient,
        IOptions<Auth0ManagementOptions> options,
        ILogger<Auth0ManagementService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Auth0ProfileSnapshot> GetUserProfileAsync(string auth0UserId, CancellationToken cancellationToken = default)
    {
        if (!_options.HasManagementApiConfiguration)
            return new Auth0ProfileSnapshot(null, null, false);

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri($"api/v2/users/{Uri.EscapeDataString(auth0UserId)}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetManagementTokenAsync(cancellationToken));

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Auth0 profile sync failed for user {Auth0UserId} with status {StatusCode}.", auth0UserId, response.StatusCode);
            return new Auth0ProfileSnapshot(null, null, false);
        }

        var profile = await response.Content.ReadFromJsonAsync<Auth0UserResponse>(cancellationToken);
        var hasPasswordResetIdentity = profile?.Identities?.Any(identity =>
            !string.IsNullOrWhiteSpace(identity.Connection) &&
            string.Equals(identity.Connection, _options.DatabaseConnection, StringComparison.OrdinalIgnoreCase)) == true;

        return new Auth0ProfileSnapshot(profile?.Email, profile?.EmailVerified, hasPasswordResetIdentity);
    }

    public async Task<bool> HasAdminRoleAsync(string auth0UserId, CancellationToken cancellationToken = default)
    {
        var page = 0;
        while (true)
        {
            var roles = await SendManagementRequestAsync<List<Auth0RoleResponse>>(
                $"api/v2/users/{Uri.EscapeDataString(auth0UserId)}/roles?per_page=100&page={page}",
                cancellationToken,
                notFoundIsEmpty: true);
            if (roles is null)
                return false;
            if (roles.Any(role => string.Equals(role.Name, "admin", StringComparison.Ordinal)))
                return true;
            if (roles.Count < 100)
                return false;
            page++;
        }
    }

    public async Task<IReadOnlyList<string>> GetAdminUserIdsAsync(CancellationToken cancellationToken = default)
    {
        var roleIds = new List<string>();
        for (var page = 0; ; page++)
        {
            var roles = await SendManagementRequestAsync<List<Auth0RoleResponse>>(
                            $"api/v2/roles?name_filter=admin&type=tenant&per_page=100&page={page}",
                            cancellationToken)
                        ?? throw new ServiceUnavailableException("Auth0 did not return its role list.");
            roleIds.AddRange(roles
                .Where(role => string.Equals(role.Name, "admin", StringComparison.Ordinal))
                .Select(role => role.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id)));
            if (roles.Count < 100)
                break;
        }

        var userIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var roleId in roleIds.Distinct(StringComparer.Ordinal))
        {
            string? next = null;
            var userCursors = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                var path = $"api/v2/roles/{Uri.EscapeDataString(roleId)}/users?take=100" +
                           (next is null ? string.Empty : $"&from={Uri.EscapeDataString(next)}");
                var page = await SendManagementRequestAsync<Auth0RoleUsersPage>(path, cancellationToken)
                           ?? throw new ServiceUnavailableException("Auth0 did not return its role users.");
                var users = page.Users ?? throw new ServiceUnavailableException("Auth0 did not return its role users.");
                foreach (var user in users)
                {
                    if (!string.IsNullOrWhiteSpace(user.UserId))
                        userIds.Add(user.UserId);
                }
                next = page.Next;
                if (next is not null && !userCursors.Add(next))
                    throw new ServiceUnavailableException("Auth0 role-user pagination did not advance.");
            } while (next is not null);
        }

        return userIds.ToArray();
    }

    private async Task<T?> SendManagementRequestAsync<T>(
        string path,
        CancellationToken cancellationToken,
        bool notFoundIsEmpty = false)
        where T : class
    {
        if (!_options.HasManagementApiConfiguration)
            throw new ServiceUnavailableException("Auth0 admin role verification is not configured.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetManagementTokenAsync(cancellationToken));

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (notFoundIsEmpty && response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
                   ?? throw new JsonException("Auth0 returned an empty role response.");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Auth0 admin role lookup failed for {Path}.", path);
            throw new ServiceUnavailableException("Auth0 admin role verification is unavailable.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Auth0 admin role lookup timed out for {Path}.", path);
            throw new ServiceUnavailableException("Auth0 admin role verification is unavailable.", exception);
        }
        catch (UriFormatException exception)
        {
            _logger.LogWarning(exception, "Auth0 admin role lookup has invalid Management API configuration.");
            throw new ServiceUnavailableException("Auth0 admin role verification is unavailable.", exception);
        }
    }

    public async Task SendVerificationEmailAsync(string auth0UserId, CancellationToken cancellationToken = default)
    {
        if (!_options.HasManagementApiConfiguration)
        {
            _logger.LogWarning("Auth0 verification email was requested, but Management API configuration is incomplete.");
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri("api/v2/jobs/verification-email"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetManagementTokenAsync(cancellationToken));
        request.Content = JsonContent.Create(new VerificationEmailRequest(auth0UserId));

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task SendPasswordResetEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        if (!_options.HasPasswordResetConfiguration)
        {
            _logger.LogWarning("Auth0 password reset was requested, but password reset configuration is incomplete.");
            return;
        }

        using var response = await _httpClient.PostAsJsonAsync(
            BuildUri("dbconnections/change_password"),
            new PasswordResetRequest(
                _options.PasswordResetClientId!,
                email,
                _options.DatabaseConnection!),
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    private async Task<string> GetManagementTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_managementToken) &&
            _managementTokenExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return _managementToken;
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_managementToken) &&
                _managementTokenExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return _managementToken;
            }

            using var response = await _httpClient.PostAsJsonAsync(
                BuildUri("oauth/token"),
                new ManagementTokenRequest(
                    _options.ManagementClientId!,
                    _options.ManagementClientSecret!,
                    _options.ManagementAudience!,
                    "client_credentials"),
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var token = await response.Content.ReadFromJsonAsync<ManagementTokenResponse>(cancellationToken);
            _managementToken = token?.AccessToken ?? throw new InvalidOperationException("Auth0 did not return a management access token.");
            _managementTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn - 60));

            return _managementToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private Uri BuildUri(string path)
    {
        return new Uri(new Uri(_options.AuthorityBaseUri), path);
    }

    private sealed record Auth0UserResponse(
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("email_verified")] bool? EmailVerified,
        [property: JsonPropertyName("identities")] IReadOnlyList<Auth0IdentityResponse>? Identities);

    private sealed record Auth0IdentityResponse(
        [property: JsonPropertyName("provider")] string? Provider,
        [property: JsonPropertyName("connection")] string? Connection);

    private sealed record Auth0RoleResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string? Name);

    private sealed record Auth0RoleUsersPage(
        [property: JsonPropertyName("users")] IReadOnlyList<Auth0RoleUserResponse>? Users,
        [property: JsonPropertyName("next")] string? Next);

    private sealed record Auth0RoleUserResponse(
        [property: JsonPropertyName("user_id")] string? UserId);

    private sealed record VerificationEmailRequest(
        [property: JsonPropertyName("user_id")] string UserId);

    private sealed record PasswordResetRequest(
        [property: JsonPropertyName("client_id")] string ClientId,
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("connection")] string Connection);

    private sealed record ManagementTokenRequest(
        [property: JsonPropertyName("client_id")] string ClientId,
        [property: JsonPropertyName("client_secret")] string ClientSecret,
        [property: JsonPropertyName("audience")] string Audience,
        [property: JsonPropertyName("grant_type")] string GrantType);

    private sealed record ManagementTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
