using Mercurius.LAN.API;
using Mercurius.LAN.API.Data;
using Mercurius.Modules.Identity.Application.Services.Auth0;
using Mercurius.Modules.Identity.Domain;
using Mercurius.TestInfrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Mercurius.Api.Tests;

public sealed class Auth0LegacyBackfillTests
{
    [Fact]
    public async Task HostedBackfill_DetachesLegacyDeletedSubjectAndPersistsPrivateCleanupJob()
    {
        await using var database = PostgresTestDatabase.CreateMigrated();
        var subject = $"auth0|legacy-backfill-{Guid.NewGuid():N}";
        var username = $"legacy-{Guid.NewGuid():N}"[..20];
        var legacyUser = new User
        {
            Id = Guid.NewGuid(),
            Auth0UserId = subject,
            Username = username,
            NormalizedUsername = username,
            IsDeleted = true,
            DeletedAtUtc = DateTime.UtcNow.AddDays(-1)
        };
        await using (var seed = CreateDbContext(database.ConnectionString))
        {
            seed.Users.Add(legacyUser);
            await seed.SaveChangesAsync();
        }

        await using var app = CreateApp(database.ConnectionString);
        await app.StartAsync();
        try
        {
            var (cleanup, detached) = await WaitForDetachedAsync(database.ConnectionString, legacyUser.Id);

            Assert.NotNull(cleanup);
            Assert.Equal($"tombstone:{legacyUser.Id:N}", detached!.Auth0UserId);
            Assert.Equal(subject, cleanup!.Subject);
            Assert.Equal(Auth0SubjectHash.Compute(subject), cleanup.SubjectHash);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task HostedBackfill_ExitsAfterDrainAndRestartProcessesLaterLegacyRows()
    {
        await using var database = PostgresTestDatabase.CreateMigrated();
        var first = CreateLegacyUser();
        await SeedAsync(database.ConnectionString, first);

        await using var app = CreateApp(database.ConnectionString);
        await app.StartAsync();
        await WaitForDetachedAsync(database.ConnectionString, first.Id);

        var backfill = app.Services.GetServices<IHostedService>().OfType<Auth0LegacyAccountBackfill>().Single();
        await backfill.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

        var later = CreateLegacyUser();
        await SeedAsync(database.ConnectionString, later);
        await Task.Delay(200);
        await using (var verifyStopped = CreateDbContext(database.ConnectionString))
        {
            var stillAttached = await verifyStopped.Users.AsNoTracking().SingleAsync(user => user.Id == later.Id);
            Assert.Equal(later.Auth0UserId, stillAttached.Auth0UserId);
        }

        await app.StopAsync();

        await using var restarted = CreateApp(database.ConnectionString);
        await restarted.StartAsync();
        try
        {
            var (cleanup, detached) = await WaitForDetachedAsync(database.ConnectionString, later.Id);
            Assert.Equal($"tombstone:{later.Id:N}", detached.Auth0UserId);
            Assert.Equal(Auth0SubjectHash.Compute(later.Auth0UserId), cleanup.SubjectHash);
        }
        finally
        {
            await restarted.StopAsync();
        }
    }

    private static User CreateLegacyUser()
    {
        var username = $"legacy-{Guid.NewGuid():N}"[..20];
        return new User
        {
            Id = Guid.NewGuid(),
            Auth0UserId = $"auth0|legacy-backfill-{Guid.NewGuid():N}",
            Username = username,
            NormalizedUsername = username,
            IsDeleted = true,
            DeletedAtUtc = DateTime.UtcNow.AddDays(-1)
        };
    }

    private static async Task SeedAsync(string connectionString, User user)
    {
        await using var seed = CreateDbContext(connectionString);
        seed.Users.Add(user);
        await seed.SaveChangesAsync();
    }

    private static async Task<(Auth0AccountCleanup Cleanup, User Detached)> WaitForDetachedAsync(
        string connectionString,
        Guid userId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var read = CreateDbContext(connectionString);
            var cleanup = await read.Set<Auth0AccountCleanup>()
                .SingleOrDefaultAsync(job => job.UserId == userId, timeout.Token);
            var detached = await read.Users.AsNoTracking().SingleAsync(user => user.Id == userId, timeout.Token);
            if (cleanup is not null && detached.Auth0UserId == $"tombstone:{userId:N}")
                return (cleanup, detached);
            await Task.Delay(50, timeout.Token);
        }
    }

    private static WebApplication CreateApp(string connectionString)
    {
        return Program.CreateApp(
        [
            "--environment=Production",
            "--Logging:LogLevel:Default=Warning",
            $"--ConnectionStrings:MercuriusDB={connectionString}",
            $"--FileStorage:Location={Path.Combine(Path.GetTempPath(), "mercurius-host-tests")}",
            "--Auth0:Audience=https://api.example.test",
            "--Database:ApplyMigrationsOnStartup=false",
            "--urls=http://127.0.0.1:0"
        ]);
    }

    private static MercuriusDBContext CreateDbContext(string connectionString) =>
        new(new DbContextOptionsBuilder<MercuriusDBContext>().UseNpgsql(connectionString).Options);
}
