using System.Security.Claims;
using Mercurius.LAN.API.Data;
using Mercurius.LAN.API.Middleware;
using Mercurius.Modules.Identity.Application.Services;
using Mercurius.Modules.Identity.Application.Services.Auth0;
using Mercurius.Modules.Identity.Domain;
using Mercurius.Modules.Identity.Infrastructure;
using Mercurius.TestInfrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mercurius.Api.Tests;

public sealed class IdentityTokenValidationMiddlewareTests
{
    [Fact]
    public async Task ProtectedRequest_RejectsOldTokenAfterSameSubjectIsRecreatedAndPinsNewLocalId()
    {
        await using var database = PostgresTestDatabase.CreateMigrated();
        var subject = $"auth0|middleware-{Guid.NewGuid():N}";
        var oldUser = CreateUser(subject);
        await using (var seed = CreateDbContext(database.ConnectionString))
        {
            seed.Users.Add(oldUser);
            await seed.SaveChangesAsync();
        }

        var oldRequestWasInvoked = false;
        var oldContext = await InvokeAsync(database.ConnectionString, subject, "100", () => oldRequestWasInvoked = true);
        Assert.Equal(StatusCodes.Status200OK, oldContext.Response.StatusCode);
        Assert.True(oldRequestWasInvoked);

        var replacement = CreateUser(subject);
        await using (var mutate = CreateDbContext(database.ConnectionString))
        {
            var oldPersisted = await mutate.Users.SingleAsync(user => user.Id == oldUser.Id);
            oldPersisted.Anonymize(DateTime.UtcNow);
            mutate.Users.Add(replacement);
            mutate.Set<Auth0AccountCleanup>().Add(new Auth0AccountCleanup
            {
                Id = Guid.NewGuid(),
                UserId = oldUser.Id,
                SubjectHash = Auth0SubjectHash.Compute(subject),
                Subject = string.Empty,
                RequestedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                CompletedAtUtc = DateTimeOffset.FromUnixTimeSeconds(199).UtcDateTime,
                OutboxMessageId = Guid.NewGuid()
            });
            await mutate.SaveChangesAsync();
        }

        var staleRequestWasInvoked = false;
        var staleContext = await InvokeAsync(database.ConnectionString, subject, "199", () => staleRequestWasInvoked = true);
        Assert.Equal(StatusCodes.Status401Unauthorized, staleContext.Response.StatusCode);
        Assert.False(staleRequestWasInvoked);

        var replacementRequestWasInvoked = false;
        var freshContext = await InvokeAsync(database.ConnectionString, subject, "200", () => replacementRequestWasInvoked = true);
        Assert.Equal(StatusCodes.Status200OK, freshContext.Response.StatusCode);
        Assert.True(replacementRequestWasInvoked);
        Assert.Equal(replacement.Id, Assert.IsType<AuthenticatedIdentityContext>(
            freshContext.Items[nameof(AuthenticatedIdentityContext)]).UserId);
        Assert.NotEqual(oldUser.Id, replacement.Id);
    }

    private static async Task<DefaultHttpContext> InvokeAsync(
        string connectionString,
        string subject,
        string issuedAt,
        Action next)
    {
        await using var dbContext = CreateDbContext(connectionString);
        var identityContext = new AuthenticatedIdentityContext();
        var validator = new IdentityTokenValidator(
            new IdentityDbContextAdapter<MercuriusDBContext>(dbContext),
            identityContext);
        var services = new ServiceCollection()
            .AddSingleton<Mercurius.Modules.Identity.Contracts.IIdentityTokenValidator>(validator)
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sub", subject),
                new Claim("iat", issuedAt)
            ], "Bearer"))
        };
        context.Request.Path = "/v1/lan/users/admins";
        context.Response.Body = new MemoryStream();
        var middleware = new IdentityTokenValidationMiddleware(_ =>
        {
            context.Items[nameof(AuthenticatedIdentityContext)] = identityContext;
            next();
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, validator);
        await services.DisposeAsync();
        return context;
    }

    private static MercuriusDBContext CreateDbContext(string connectionString) =>
        new(new DbContextOptionsBuilder<MercuriusDBContext>().UseNpgsql(connectionString).Options);

    private static User CreateUser(string subject)
    {
        var username = $"middleware-{Guid.NewGuid():N}"[..20];
        return new User
        {
            Id = Guid.NewGuid(),
            Auth0UserId = subject,
            Username = username,
            NormalizedUsername = username,
            Firstname = "First",
            Lastname = "User"
        };
    }
}
