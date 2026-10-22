using Mercurius.LAN.API;
using Mercurius.LAN.API.Data;
using Mercurius.Modules.Identity.Application.Services.Auth0;
using Mercurius.Modules.Identity.Domain;
using Mercurius.TestInfrastructure;
using Microsoft.EntityFrameworkCore;

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

        await using var app = Program.CreateApp(
        [
            "--environment=Production",
            "--Logging:LogLevel:Default=Warning",
            $"--ConnectionStrings:MercuriusDB={database.ConnectionString}",
            $"--FileStorage:Location={Path.Combine(Path.GetTempPath(), "mercurius-host-tests")}",
            "--Auth0:AccountCleanupEncryptionKey=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
            "--Auth0:Audience=https://api.example.test",
            "--Database:ApplyMigrationsOnStartup=false",
            "--urls=http://127.0.0.1:0"
        ]);
        await app.StartAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Auth0AccountCleanup? cleanup = null;
            User? detached = null;
            while (timeout.IsCancellationRequested is false)
            {
                await using var read = CreateDbContext(database.ConnectionString);
                cleanup = await read.Set<Auth0AccountCleanup>().SingleOrDefaultAsync(job => job.UserId == legacyUser.Id, timeout.Token);
                detached = await read.Users.AsNoTracking().SingleAsync(user => user.Id == legacyUser.Id, timeout.Token);
                if (cleanup is not null && detached.Auth0UserId == $"tombstone:{legacyUser.Id:N}")
                    break;
                await Task.Delay(50, timeout.Token);
            }

            Assert.NotNull(cleanup);
            Assert.Equal($"tombstone:{legacyUser.Id:N}", detached!.Auth0UserId);
            Assert.NotEmpty(cleanup!.EncryptedSubject);
            Assert.Equal(Auth0SubjectCryptography.Hash(subject), cleanup.SubjectHash);
            Assert.Equal(subject, new Auth0SubjectCryptography(Microsoft.Extensions.Options.Options.Create(
                new Mercurius.Modules.Identity.Options.Auth0ManagementOptions
                {
                    AccountCleanupEncryptionKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="
                })).Decrypt(cleanup.EncryptedSubject, cleanup.Id));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static MercuriusDBContext CreateDbContext(string connectionString) =>
        new(new DbContextOptionsBuilder<MercuriusDBContext>().UseNpgsql(connectionString).Options);
}
