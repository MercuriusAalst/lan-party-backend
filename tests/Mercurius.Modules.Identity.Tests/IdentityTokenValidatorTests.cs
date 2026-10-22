using Mercurius.LAN.API.Data;
using Mercurius.Modules.Identity.Application.Services;
using Mercurius.Modules.Identity.Application.Services.Auth0;
using Mercurius.Modules.Identity.Domain;
using Mercurius.Modules.Identity.Infrastructure;
using Mercurius.Modules.Identity.Options;
using Mercurius.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mercurius.Modules.Identity.Tests;

public sealed class IdentityTokenValidatorTests
{
    [Fact]
    public async Task ValidateAndPin_RejectsPendingFenceAndMalformedIssuedAt()
    {
        await using var database = PostgresTestDatabase.CreateMigrated();
        await using var dbContext = CreateDbContext(database.ConnectionString);
        var subject = "auth0|pending-fence";
        dbContext.Set<Auth0SubjectFence>().Add(new Auth0SubjectFence
        {
            SubjectHash = Auth0SubjectCryptography.Hash(subject),
            PendingCleanupJobId = Guid.NewGuid()
        });
        await dbContext.SaveChangesAsync();
        var context = new AuthenticatedIdentityContext();
        var validator = CreateValidator(dbContext, context);

        Assert.False(await validator.ValidateAndPinAsync(subject, "not-a-number", false));
        Assert.False(context.IsInitialized);
    }

    [Fact]
    public async Task ValidateAndPin_RequiresStrictIssuedAtCutoffForRecreatedUser()
    {
        await using var database = PostgresTestDatabase.CreateMigrated();
        await using var dbContext = CreateDbContext(database.ConnectionString);
        var subject = "auth0|recreated";
        var newUser = CreateUser(subject);
        dbContext.Users.Add(newUser);
        dbContext.Set<Auth0SubjectFence>().Add(new Auth0SubjectFence
        {
            SubjectHash = Auth0SubjectCryptography.Hash(subject),
            RejectTokensBeforeUnixSeconds = 200
        });
        await dbContext.SaveChangesAsync();
        var context = new AuthenticatedIdentityContext();
        var validator = CreateValidator(dbContext, context);

        Assert.False(await validator.ValidateAndPinAsync(subject, "199", false));
        Assert.False(context.IsInitialized);
        Assert.True(await validator.ValidateAndPinAsync(subject, "200", false));
        Assert.Equal(newUser.Id, context.UserId);
    }

    [Fact]
    public async Task ValidateAndPin_LocksFenceAndMappingAsOneIncarnationSnapshot()
    {
        await using var database = PostgresTestDatabase.CreateMigrated();
        var subject = "auth0|incarnation-race";
        await using var first = CreateDbContext(database.ConnectionString);
        var original = CreateUser(subject);
        first.Users.Add(original);
        await first.SaveChangesAsync();

        await using var second = CreateDbContext(database.ConnectionString);
        var context = new AuthenticatedIdentityContext();
        var validator = CreateValidator(second, context);
        await using var mutation = await first.Database.BeginTransactionAsync();
        await UserMutationLock.AcquireSubjectAsync(new IdentityDbContextAdapter<MercuriusDBContext>(first), subject);

        var validation = validator.ValidateAndPinAsync(subject, "100", false);
        await WaitForAdvisoryLockWaiterAsync(database.ConnectionString);

        original.Anonymize(DateTime.UtcNow);
        var replacement = CreateUser(subject);
        first.Users.Add(replacement);
        first.Set<Auth0SubjectFence>().Add(new Auth0SubjectFence
        {
            SubjectHash = Auth0SubjectCryptography.Hash(subject),
            RejectTokensBeforeUnixSeconds = 200
        });
        await first.SaveChangesAsync();
        await mutation.CommitAsync();

        Assert.False(await validation.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(context.IsInitialized);
    }

    private static IdentityTokenValidator CreateValidator(MercuriusDBContext dbContext, AuthenticatedIdentityContext context)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new Auth0ManagementOptions
        {
            AccountCleanupEncryptionKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="
        });
        return new IdentityTokenValidator(
            new IdentityDbContextAdapter<MercuriusDBContext>(dbContext),
            new Auth0SubjectCryptography(options),
            context);
    }

    private static MercuriusDBContext CreateDbContext(string connectionString) =>
        new(new DbContextOptionsBuilder<MercuriusDBContext>().UseNpgsql(connectionString).Options);

    private static User CreateUser(string subject)
    {
        var username = $"identity{Guid.NewGuid():N}"[..20];
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

    private static async Task WaitForAdvisoryLockWaiterAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            if ((long)(await command.ExecuteScalarAsync(timeout.Token) ?? 0L) > 0)
                return;
            await Task.Delay(20, timeout.Token);
        }
    }
}
