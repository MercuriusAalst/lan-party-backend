using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Asp.Versioning;
using Mercurius.LAN.API.Data;
using Mercurius.LAN.API.Hubs;
using Mercurius.LAN.API.Middleware;
using Mercurius.Modules.Identity;
using Mercurius.Modules.Identity.Application.DTOs;
using Mercurius.Modules.Identity.Application.Services;
using Mercurius.Modules.Identity.Application.Services.Auth0;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Identity.Domain;
using Mercurius.Modules.Identity.Infrastructure;
using Mercurius.Modules.Identity.Options;
using Mercurius.Modules.Shared.Exceptions;
using Mercurius.Modules.Shared.Search;
using Mercurius.TestInfrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Eventing;
using Platform.Extensions;
using Platform;

namespace Mercurius.Api.Tests;

public sealed class AccountLifecycleHttpTests
{
    [Fact]
    public async Task HttpLifecycle_DeletesWithoutEncryptionKeyThenRejectsOldTokenAfterSameSubjectRecreation()
    {
        await using var database = PostgresTestDatabase.CreateMigrated();
        var subject = $"auth0|http-lifecycle-{Guid.NewGuid():N}";
        var oldUser = CreateUser(subject);
        await using (var seed = CreateDbContext(database.ConnectionString))
        {
            seed.Users.Add(oldUser);
            await seed.SaveChangesAsync();
        }

        var auth0 = new RecordingAuth0ManagementService(DateTime.UtcNow.AddDays(-10));
        await using var app = await CreateAppAsync(database.ConnectionString, auth0);
        using var client = CreateClient(app);

        using var deleteResponse = await SendAsync(client, HttpMethod.Delete, "v1/lan/users/me", subject, "100");
        Assert.Equal(HttpStatusCode.Accepted, deleteResponse.StatusCode);
        var deleteBody = await deleteResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(subject, deleteBody, StringComparison.Ordinal);
        using var deleteJson = JsonDocument.Parse(deleteBody);
        Assert.Equal("Account deletion requested.", deleteJson.RootElement.GetProperty("message").GetString());
        Assert.True(deleteJson.RootElement.GetProperty("deletionPending").GetBoolean());

        using (var read = CreateDbContext(database.ConnectionString))
        {
            var deleted = await read.Users.SingleAsync(user => user.Id == oldUser.Id);
            var cleanup = await read.Set<AccountCleanup>().SingleAsync(job => job.UserId == oldUser.Id);
            Assert.True(deleted.IsDeleted);
            Assert.StartsWith("tombstone:", deleted.Auth0UserId, StringComparison.Ordinal);
            Assert.Equal(subject, cleanup.Subject);
        }

        using var deletedProfile = await SendAsync(client, HttpMethod.Get, "v1/lan/users/me", subject, "100");
        Assert.Equal(HttpStatusCode.Gone, deletedProfile.StatusCode);
        using var staleRegistration = await SendAsync(client, HttpMethod.Put, "v1/lan/users/me", subject, "100", new
        {
            username = "new-after-cleanup",
            firstname = "New",
            lastname = "Account"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, staleRegistration.StatusCode);
        using var staleAdminAccess = await SendAsync(client, HttpMethod.Get, "v1/lan/users/admins", subject, "100", role: "admin");
        Assert.Equal(HttpStatusCode.Unauthorized, staleAdminAccess.StatusCode);

        for (var pass = 0; pass < 5; pass++)
        {
            using var scope = app.Services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<IModuleEventDispatcher>().DispatchPendingAsync() == 0)
                break;
        }
        Assert.Equal(1, auth0.DeleteCalls);
        Assert.Equal(subject, auth0.LastDeletedSubject);

        var freshIssuedAt = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 5).ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var createResponse = await SendAsync(client, HttpMethod.Put, "v1/lan/users/me", subject, freshIssuedAt, new
        {
            username = $"new{Guid.NewGuid():N}"[..15],
            firstname = "New",
            lastname = "Account"
        });
        var createBody = await createResponse.Content.ReadAsStringAsync();
        Assert.True(createResponse.StatusCode == HttpStatusCode.OK, createBody);
        using var createdJson = JsonDocument.Parse(createBody);
        var replacementId = createdJson.RootElement.GetProperty("id").GetGuid();
        Assert.NotEqual(oldUser.Id, replacementId);
        using var nonAdminReplacementAccess = await SendAsync(client, HttpMethod.Get, "v1/lan/users/admins", subject, freshIssuedAt, role: "user");
        Assert.Equal(HttpStatusCode.Forbidden, nonAdminReplacementAccess.StatusCode);

        using var staleMutation = await SendAsync(client, HttpMethod.Patch, "v1/lan/users/me", subject, "100", new
        {
            username = "must-not-overwrite",
            firstname = "Stale",
            lastname = "Token"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, staleMutation.StatusCode);

        using var freshMutation = await SendAsync(client, HttpMethod.Patch, "v1/lan/users/me", subject, freshIssuedAt, new
        {
            username = "freshaccount",
            firstname = "Fresh",
            lastname = "Account"
        });
        Assert.Equal(HttpStatusCode.OK, freshMutation.StatusCode);
        await using var verify = CreateDbContext(database.ConnectionString);
        var persistedOld = await verify.Users.SingleAsync(user => user.Id == oldUser.Id);
        var persistedReplacement = await verify.Users.SingleAsync(user => user.Id == replacementId);
        Assert.True(persistedOld.IsDeleted);
        Assert.StartsWith("tombstone:", persistedOld.Auth0UserId, StringComparison.Ordinal);
        Assert.Equal("freshaccount", persistedReplacement.Username);
    }

    [Fact]
    public async Task HttpLifecycle_TwoDeletionCyclesPreserveReplayRejectionAndNondecreasingCutoff()
    {
        await using var database = PostgresTestDatabase.CreateMigrated();
        var subject = $"auth0|two-cycles-{Guid.NewGuid():N}";
        var firstUser = CreateUser(subject);
        await using (var seed = CreateDbContext(database.ConnectionString))
        {
            seed.Users.Add(firstUser);
            await seed.SaveChangesAsync();
        }

        await using var app = await CreateAppAsync(
            database.ConnectionString,
            new RecordingAuth0ManagementService(DateTime.UtcNow.AddDays(-10)));
        using var client = CreateClient(app);

        using (var firstDelete = await SendAsync(client, HttpMethod.Delete, "v1/lan/users/me", subject, "100"))
            Assert.Equal(HttpStatusCode.Accepted, firstDelete.StatusCode);
        await DispatchCleanupAsync(app);

        long firstCutoff;
        await using (var firstVerify = CreateDbContext(database.ConnectionString))
        {
            var cleanup = await firstVerify.Set<AccountCleanup>()
                .SingleAsync(job => job.UserId == firstUser.Id);
            firstCutoff = CutoffFor(cleanup);
        }

        var firstFreshIssuedAt = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 5)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var firstCreate = await SendAsync(client, HttpMethod.Put, "v1/lan/users/me", subject, firstFreshIssuedAt, new
        {
            username = $"second{Guid.NewGuid():N}"[..15],
            firstname = "Second",
            lastname = "Account"
        });
        Assert.Equal(HttpStatusCode.OK, firstCreate.StatusCode);
        Guid firstReplacementId;
        using (var firstCreateJson = JsonDocument.Parse(await firstCreate.Content.ReadAsStringAsync()))
            firstReplacementId = firstCreateJson.RootElement.GetProperty("id").GetGuid();

        using (var oldReplay = await SendAsync(client, HttpMethod.Get, "v1/lan/users/admins", subject, "100"))
            Assert.Equal(HttpStatusCode.Unauthorized, oldReplay.StatusCode);

        using (var secondDelete = await SendAsync(client, HttpMethod.Delete, "v1/lan/users/me", subject, firstFreshIssuedAt))
            Assert.Equal(HttpStatusCode.Accepted, secondDelete.StatusCode);
        await DispatchCleanupAsync(app);

        long secondCutoff;
        await using (var secondVerify = CreateDbContext(database.ConnectionString))
        {
            var cleanup = await secondVerify.Set<AccountCleanup>()
                .SingleAsync(job => job.UserId == firstReplacementId);
            secondCutoff = CutoffFor(cleanup);
        }
        Assert.True(secondCutoff >= firstCutoff);

        var secondFreshIssuedAt = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 10)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var secondCreate = await SendAsync(client, HttpMethod.Put, "v1/lan/users/me", subject, secondFreshIssuedAt, new
        {
            username = $"third{Guid.NewGuid():N}"[..15],
            firstname = "Third",
            lastname = "Account"
        });
        Assert.Equal(HttpStatusCode.OK, secondCreate.StatusCode);

        using (var replayAfterSecondCycle = await SendAsync(client, HttpMethod.Get, "v1/lan/users/admins", subject, "100"))
            Assert.Equal(HttpStatusCode.Unauthorized, replayAfterSecondCycle.StatusCode);

        await using var finalVerify = CreateDbContext(database.ConnectionString);
        Assert.Equal(2, await finalVerify.Set<AccountCleanup>()
            .CountAsync(job => job.SubjectHash == IdentitySubjectHash.Compute(subject)));
    }

    [Fact]
    public async Task AllAdminDeletionRoutes_ReturnAcceptedPendingContract()
    {
        await using var database = PostgresTestDatabase.CreateMigrated();
        var users = new[]
        {
            CreateUser($"auth0|admin-delete-id-{Guid.NewGuid():N}"),
            CreateUser($"auth0|admin-delete-name-{Guid.NewGuid():N}"),
            CreateUser($"auth0|admin-delete-account-{Guid.NewGuid():N}")
        };
        await using (var seed = CreateDbContext(database.ConnectionString))
        {
            seed.Users.AddRange(users);
            await seed.SaveChangesAsync();
        }

        await using var app = await CreateAppAsync(database.ConnectionString, new RecordingAuth0ManagementService(DateTime.UtcNow.AddDays(-2)));
        using var client = CreateClient(app);
        var paths = new[]
        {
            $"v1/lan/users/{users[0].Id:D}",
            $"v1/lan/users/{users[1].Username}",
            $"v1/lan/users/{users[2].Username}/account"
        };
        for (var index = 0; index < paths.Length; index++)
        {
            using var response = await SendAsync(client, HttpMethod.Delete, paths[index], "auth0|admin", "300", role: "admin");
            var responseBody = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{paths[index]} returned {(int)response.StatusCode}: {responseBody}");
            using var body = JsonDocument.Parse(responseBody);
            Assert.True(body.RootElement.GetProperty("deletionPending").GetBoolean());
        }

        await using var verify = CreateDbContext(database.ConnectionString);
        foreach (var user in users)
        {
            Assert.True(await verify.Users.AnyAsync(candidate => candidate.Id == user.Id && candidate.IsDeleted));
            Assert.Single(await verify.Set<AccountCleanup>().Where(job => job.UserId == user.Id).ToListAsync());
        }
    }

    [Fact]
    public async Task ProviderFailure_LeavesEncryptedCleanupPendingWithSanitizedOutboxRetry()
    {
        await using var database = PostgresTestDatabase.CreateMigrated();
        var subject = $"auth0|retry-{Guid.NewGuid():N}";
        var user = CreateUser(subject);
        await using (var seed = CreateDbContext(database.ConnectionString))
        {
            seed.Users.Add(user);
            await seed.SaveChangesAsync();
        }
        var auth0 = new RecordingAuth0ManagementService(DateTime.UtcNow.AddDays(-5))
        {
            Failure = new Auth0ManagementRequestException("Auth0 account lookup failed (429).")
        };
        await using var app = await CreateAppAsync(database.ConnectionString, auth0);
        using var client = CreateClient(app);

        using var deleteResponse = await SendAsync(client, HttpMethod.Delete, "v1/lan/users/me", subject, "100");
        Assert.Equal(HttpStatusCode.Accepted, deleteResponse.StatusCode);
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IModuleEventDispatcher>().DispatchPendingAsync();

        await using var verify = CreateDbContext(database.ConnectionString);
        var cleanup = await verify.Set<AccountCleanup>().SingleAsync(job => job.UserId == user.Id);
        var outbox = await verify.OutboxMessages.AsNoTracking()
            .SingleAsync(message => message.EventType.Contains(nameof(AccountCleanupRequestedIntegrationEvent)));
        Assert.Null(cleanup.CompletedAtUtc);
        Assert.Equal(subject, cleanup.Subject);
        Assert.True(outbox.RetryCount > 0);
        Assert.Null(outbox.DeadLetteredAtUtc);
        Assert.Contains("Auth0 account lookup failed (429).", outbox.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain(subject, outbox.LastError, StringComparison.Ordinal);
    }

    private static async Task<WebApplication> CreateAppAsync(string connectionString, RecordingAuth0ManagementService auth0)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth0:Audience"] = "https://api.example.test"
        });
        builder.Services.AddDbContext<MercuriusDBContext>(options => options.UseNpgsql(connectionString));
        builder.Services.AddModuleEventing<MercuriusDBContext>();
        builder.Services.AddIdentityModule<MercuriusDBContext>(builder.Configuration);
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, HeaderIdentityAuthenticationHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddApiVersioning();
        builder.Services.AddApiProblemDetails<Mercurius.LAN.API.Middleware.ApiExceptionHandler>();
        builder.Services.AddHttpConventions();
        builder.Services.AddValidation();
        builder.Services.AddFixedWindowRateLimiting(new FixedWindowRateLimitingOptions
        {
            UnconditionalPolicyName = SearchRateLimitPolicyNames.Anonymous,
            ConditionalPolicyName = SearchRateLimitPolicyNames.Authenticated,
            ConditionalQueryParameterName = "query"
        });
        builder.Services.AddRealtimeNotificationServices<TeamManagementHub>();
        builder.Services.AddScoped<IAuth0ManagementService>(_ => auth0);

        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UseApiExceptionHandling();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseMiddleware<IdentityTokenValidationMiddleware>();
        app.UseAuthorization();
        app.MapIdentityModule();
        await app.StartAsync();
        return app;
    }

    private static HttpClient CreateClient(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!;
        return new HttpClient { BaseAddress = new Uri(Assert.Single(addresses.Addresses)) };
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string subject,
        string issuedAt,
        object? body = null,
        string? role = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Test-Sub", subject);
        request.Headers.Add("X-Test-Iat", issuedAt);
        if (role is not null)
            request.Headers.Add("X-Test-Role", role);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static MercuriusDBContext CreateDbContext(string connectionString) =>
        new(new DbContextOptionsBuilder<MercuriusDBContext>().UseNpgsql(connectionString).Options);

    private static async Task DispatchCleanupAsync(WebApplication app)
    {
        for (var pass = 0; pass < 5; pass++)
        {
            using var scope = app.Services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<IModuleEventDispatcher>().DispatchPendingAsync() == 0)
                return;
        }
    }

    private static long CutoffFor(AccountCleanup cleanup) =>
        new DateTimeOffset(cleanup.CompletedAtUtc ?? throw new InvalidOperationException("Cleanup did not complete."))
            .ToUnixTimeSeconds() + 1;

    private static User CreateUser(string subject)
    {
        var username = $"old{Guid.NewGuid():N}"[..15];
        return new User
        {
            Id = Guid.NewGuid(),
            Auth0UserId = subject,
            Username = username,
            NormalizedUsername = username,
            Firstname = "Old",
            Lastname = "Account",
            Email = $"{username}@example.test"
        };
    }

    private sealed class HeaderIdentityAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Sub", out var subject) ||
                !Request.Headers.TryGetValue("X-Test-Iat", out var issuedAt))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim> { new("sub", subject.ToString()), new("iat", issuedAt.ToString()) };
            if (Request.Headers.TryGetValue("X-Test-Role", out var role))
                claims.Add(new Claim(ClaimTypes.Role, role.ToString()));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private sealed class RecordingAuth0ManagementService(DateTime? providerCreatedAt) : IAuth0ManagementService
    {
        public int DeleteCalls { get; private set; }
        public string? LastDeletedSubject { get; private set; }
        public Exception? Failure { get; init; }
        public Task<Auth0ProfileSnapshot> GetUserProfileAsync(string auth0UserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Auth0ProfileSnapshot($"{Guid.NewGuid():N}@example.test", true, false));
        public Task<DateTime?> GetAccountCreatedAtAsync(string auth0UserId, CancellationToken cancellationToken = default) =>
            Failure is null ? Task.FromResult(providerCreatedAt) : Task.FromException<DateTime?>(Failure);
        public Task DeleteAccountAsync(string auth0UserId, CancellationToken cancellationToken = default)
        {
            if (Failure is not null)
                return Task.FromException(Failure);
            DeleteCalls++;
            LastDeletedSubject = auth0UserId;
            return Task.CompletedTask;
        }
        public Task<bool> HasAdminRoleAsync(string auth0UserId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<IReadOnlyList<string>> GetAdminUserIdsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task SendVerificationEmailAsync(string auth0UserId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendPasswordResetEmailAsync(string email, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
