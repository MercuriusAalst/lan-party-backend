using Mercurius.LAN.API;
using Mercurius.LAN.API.Data;
using Mercurius.Modules.Identity.Application.Services;
using Mercurius.Modules.Identity.Domain;
using Mercurius.Modules.Teams.Domain;
using Mercurius.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Eventing;

namespace Mercurius.Api.Tests;

// Every account deletion route publishes the durable user-deleted event, and the composed host's Teams handler
// removes the account from its teams with captain succession.
public sealed class DeletedUserCleanupDispatchTests
{
    private static readonly string StorageLocation = Path.Combine(Path.GetTempPath(), "mercurius-host-tests");

    public static TheoryData<string> DeletionRoutes => ["self", "admin-by-id", "admin-by-username", "admin-account-by-username"];

    [Theory]
    [MemberData(nameof(DeletionRoutes))]
    public async Task DeletionRoute_RemovesCaptainFromTeamAndAppointsSuccessor(string route)
    {
        await using var database = PostgresTestDatabase.CreateMigrated();
        var captain = CreateUser();
        var member = CreateUser();
        var team = new Team("Routes", captain.Id) { Id = Guid.NewGuid() };
        team.AddMember(captain.Id, DateTime.UtcNow.AddDays(-2));
        team.AddMember(member.Id, DateTime.UtcNow.AddDays(-1));
        await using (var setup = CreateDbContext(database.ConnectionString))
        {
            setup.Users.AddRange(captain, member);
            setup.Teams.Add(team);
            await setup.SaveChangesAsync();
        }

        await using var app = Program.CreateApp(
        [
            "--environment=Production",
            "--Logging:LogLevel:Default=Warning",
            $"--ConnectionStrings:MercuriusDB={database.ConnectionString}",
            $"--FileStorage:Location={StorageLocation}",
            "--Auth0:AccountCleanupEncryptionKey=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
            "--Auth0:Audience=https://api.example.test",
            "--Database:ApplyMigrationsOnStartup=false"
        ]);
        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IUserService>();
            switch (route)
            {
                case "self":
                    await users.AnonymizeCurrentUserAsync(captain.Auth0UserId);
                    break;
                case "admin-by-id":
                    await users.DeleteUserByIdAsync(captain.Id);
                    break;
                default:
                    // Both admin username routes call the same service operation.
                    await users.DeleteUserAsync(captain.Username!);
                    break;
            }
        }

        for (var pass = 0; pass < 5; pass++)
        {
            using var scope = app.Services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<IModuleEventDispatcher>().DispatchPendingAsync() == 0)
                break;
        }

        await using var dbContext = CreateDbContext(database.ConnectionString);
        var persisted = await dbContext.Teams.Include(candidate => candidate.Members).SingleAsync(candidate => candidate.Id == team.Id);
        Assert.Equal(member.Id, persisted.CaptainUserId);
        Assert.Equal([member.Id], persisted.Members.Select(teamMember => teamMember.UserId));
        Assert.All(await dbContext.OutboxMessages.AsNoTracking()
            .Where(message => !message.EventType.Contains(nameof(Mercurius.Modules.Identity.Contracts.Auth0AccountCleanupRequestedIntegrationEvent)))
            .ToListAsync(), message => Assert.True(
            message.ProcessedAtUtc is not null,
            $"{message.EventType} was not delivered: {message.LastError}"));
    }

    private static MercuriusDBContext CreateDbContext(string connectionString) =>
        new(new DbContextOptionsBuilder<MercuriusDBContext>().UseNpgsql(connectionString).Options);

    private static User CreateUser()
    {
        var username = $"route{Guid.NewGuid():N}"[..20];
        return new User
        {
            Id = Guid.NewGuid(),
            Auth0UserId = $"auth0|{username}",
            Username = username,
            NormalizedUsername = username,
            Firstname = "First",
            Lastname = "Last",
            Email = $"{username}@example.test"
        };
    }
}
