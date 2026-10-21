using System.Text.Json;
using Mercurius.LAN.API;
using Mercurius.LAN.API.Data;
using Mercurius.LAN.API.Migrations;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Identity.Domain;
using Mercurius.Modules.Teams.Domain;
using Mercurius.Modules.Tournament.Domain;
using Mercurius.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Eventing;
using TeamInviteStatus = Mercurius.Modules.Teams.Contracts.TeamInviteStatus;
using TournamentAggregate = Mercurius.Modules.Tournament.Domain.Tournament;

namespace Mercurius.Api.Tests;

// The TeamMemberJoinedAt migration replays the user-deleted event for accounts deleted before the cleanup handlers
// existed; the regular outbox dispatch and the Teams and Tournament handlers then repair the data.
public sealed class DeletedUserLegacyRepairTests
{
    private const string UserDeletedEventType = "Mercurius.Modules.Identity.Contracts.UserDeletedIntegrationEvent";
    private static readonly string StorageLocation = Path.Combine(Path.GetTempPath(), "mercurius-host-tests");

    [Fact]
    public async Task LegacyReplay_EnqueuesOneDelayedEventPerAffectedDeletedUser_AndIsIdempotent()
    {
        await using var database = PostgresTestDatabase.CreateMigrated();
        await using var dbContext = CreateDbContext(database.ConnectionString);
        var seed = await SeedLegacyCorruptionAsync(dbContext);

        var inserted = await dbContext.Database.ExecuteSqlRawAsync(TeamMemberJoinedAt.EnqueueLegacyDeletedUserCleanupSql);
        var insertedAgain = await dbContext.Database.ExecuteSqlRawAsync(TeamMemberJoinedAt.EnqueueLegacyDeletedUserCleanupSql);

        var rows = await dbContext.OutboxMessages.AsNoTracking().Where(message => message.EventType == UserDeletedEventType).ToListAsync();
        var payloads = rows
            .Select(row => JsonSerializer.Deserialize<UserDeletedIntegrationEvent>(row.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToList();
        Assert.Equal(3, inserted);
        Assert.Equal(0, insertedAgain);
        Assert.Equal(
            new[] { seed.DeletedCaptain.Id, seed.DeletedInvitee.Id, seed.DeletedIndividual.Id }.Order(),
            payloads.Select(payload => payload.UserId.Value).Order());
        Assert.DoesNotContain(payloads, payload => payload.UserId.Value == seed.DeletedHistoricalOnly.Id);
        Assert.All(rows, row => Assert.True(row.NextAttemptAtUtc > row.OccurredAtUtc));
        Assert.Equal(DateTimeKind.Utc, payloads[0].DeletedAtUtc.Kind);
    }

    [Fact]
    public async Task LegacyReplay_IsRepairedByTheRegularOutboxDispatch()
    {
        await using var database = PostgresTestDatabase.CreateMigrated();
        LegacySeed seed;
        await using (var seedContext = CreateDbContext(database.ConnectionString))
        {
            seed = await SeedLegacyCorruptionAsync(seedContext);
            await seedContext.Database.ExecuteSqlRawAsync(TeamMemberJoinedAt.EnqueueLegacyDeletedUserCleanupSql);
            await seedContext.Database.ExecuteSqlRawAsync("UPDATE platform.outbox_messages SET next_attempt_at_utc = now() - interval '1 second'");
        }

        await using var app = Program.CreateApp(
        [
            "--environment=Production",
            "--Logging:LogLevel:Default=Warning",
            $"--ConnectionStrings:MercuriusDB={database.ConnectionString}",
            $"--FileStorage:Location={StorageLocation}",
            "--Auth0:Audience=https://api.example.test",
            "--Database:ApplyMigrationsOnStartup=false"
        ]);
        for (var pass = 0; pass < 5; pass++)
        {
            using var scope = app.Services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<IModuleEventDispatcher>().DispatchPendingAsync() == 0)
                break;
        }

        await using var dbContext = CreateDbContext(database.ConnectionString);
        var team = await dbContext.Teams.Include(candidate => candidate.Members).SingleAsync(candidate => candidate.Name == "Legacy");
        var registration = await dbContext.Set<TournamentRegistration>()
            .Include(candidate => candidate.RosterMembers)
            .SingleAsync(candidate => candidate.TeamId == team.Id);
        var invites = await dbContext.Set<TeamInvite>().ToListAsync();
        var outbox = await dbContext.OutboxMessages.AsNoTracking().ToListAsync();

        Assert.Single(team.Members);
        Assert.Equal(team.Members[0].UserId, team.CaptainUserId);
        Assert.Equal(TournamentRegistrationStatus.PendingConfirmation, registration.Status);
        Assert.Equal(team.CaptainUserId, registration.TeamCaptainUserIdAtRegistration);
        Assert.True(Assert.Single(registration.RosterMembers).IsCaptain);
        Assert.All(invites, invite => Assert.Equal(TeamInviteStatus.Cancelled, invite.Status));
        Assert.False(await dbContext.Set<TournamentRegistration>().AnyAsync(candidate => candidate.UserId == seed.DeletedIndividual.Id));
        Assert.True(await dbContext.Set<TournamentRegistration>().AnyAsync(candidate => candidate.UserId == seed.DeletedHistoricalOnly.Id));
        Assert.Equal(2, await dbContext.Set<TournamentRegistration>().CountAsync(candidate => candidate.Kind == TournamentRegistrationKind.Individual));
        Assert.All(outbox, message => Assert.True(
            message.ProcessedAtUtc is not null && message.DeadLetteredAtUtc is null,
            $"{message.EventType} was not delivered: {message.LastError}"));
    }

    private static MercuriusDBContext CreateDbContext(string connectionString) =>
        new(new DbContextOptionsBuilder<MercuriusDBContext>().UseNpgsql(connectionString).Options);

    // Data as it looked before this change: deleted accounts left in teams, invites and scheduled registrations.
    private static async Task<LegacySeed> SeedLegacyCorruptionAsync(MercuriusDBContext dbContext)
    {
        var deletedCaptain = CreateUser();
        var member = CreateUser();
        var deletedInvitee = CreateUser();
        var deletedIndividual = CreateUser();
        var deletedHistoricalOnly = CreateUser();
        var activeOnly = CreateUser();
        var team = new Team("Legacy", deletedCaptain.Id) { Id = Guid.NewGuid() };
        team.AddMember(deletedCaptain.Id);
        team.AddMember(member.Id);
        var teamTournament = CreateTournament(ParticipationMode.Team, TournamentStatus.Scheduled, 2);
        var soloTournament = CreateTournament(ParticipationMode.Individual, TournamentStatus.Scheduled, null);
        var completedTournament = CreateTournament(ParticipationMode.Individual, TournamentStatus.Completed, null);
        dbContext.Users.AddRange(deletedCaptain, member, deletedInvitee, deletedIndividual, deletedHistoricalOnly, activeOnly);
        dbContext.Teams.Add(team);
        dbContext.Set<TeamInvite>().Add(new TeamInvite
        {
            Id = Guid.NewGuid(),
            Team = team,
            TeamId = team.Id,
            UserId = deletedInvitee.Id,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        });
        dbContext.Set<TournamentAggregate>().AddRange(teamTournament, soloTournament, completedTournament);
        dbContext.Set<TournamentRegistration>().Add(new TournamentRegistration
        {
            Id = Guid.NewGuid(),
            TournamentId = teamTournament.Id,
            Kind = TournamentRegistrationKind.Team,
            Status = TournamentRegistrationStatus.Active,
            RegisteredByUserId = deletedCaptain.Id,
            RegisteredByUsernameAtRegistration = deletedCaptain.Username!,
            TeamId = team.Id,
            TeamNameAtRegistration = team.Name,
            TeamCaptainUserIdAtRegistration = deletedCaptain.Id,
            RosterMembers = new[] { deletedCaptain, member }.Select(user => new TournamentRegistrationRosterMember
            {
                Id = Guid.NewGuid(),
                TournamentId = teamTournament.Id,
                TeamId = team.Id,
                UserId = user.Id,
                UsernameAtRegistration = user.Username!,
                DisplayNameAtRegistration = user.DisplayName,
                IsCaptain = user.Id == deletedCaptain.Id,
                ConfirmationStatus = user.Id == deletedCaptain.Id
                    ? RosterMemberConfirmationStatus.AutoConfirmed
                    : RosterMemberConfirmationStatus.Confirmed
            }).ToList()
        });
        foreach (var (tournament, user) in new[] { (soloTournament, deletedIndividual), (completedTournament, deletedHistoricalOnly), (soloTournament, activeOnly) })
        {
            dbContext.Set<TournamentRegistration>().Add(new TournamentRegistration
            {
                Id = Guid.NewGuid(),
                TournamentId = tournament.Id,
                Kind = TournamentRegistrationKind.Individual,
                Status = TournamentRegistrationStatus.Active,
                RegisteredByUserId = user.Id,
                RegisteredByUsernameAtRegistration = user.Username!,
                UserId = user.Id,
                UsernameAtRegistration = user.Username
            });
        }

        await dbContext.SaveChangesAsync();
        foreach (var user in new[] { deletedCaptain, deletedInvitee, deletedIndividual, deletedHistoricalOnly })
            user.Anonymize(DateTime.UtcNow);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        return new LegacySeed(deletedCaptain, deletedInvitee, deletedIndividual, deletedHistoricalOnly);
    }

    private static User CreateUser()
    {
        var username = $"legacy-{Guid.NewGuid():N}"[..20];
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

    private static TournamentAggregate CreateTournament(ParticipationMode mode, TournamentStatus status, int? teamSize) =>
        new TournamentAggregate($"Cup {Guid.NewGuid():N}"[..20], BracketType.SingleElimination, GameFormat.BestOf1, GameFormat.BestOf1, mode, teamSize, DateTime.UtcNow, 30, 10)
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.Status, status);

    private sealed record LegacySeed(User DeletedCaptain, User DeletedInvitee, User DeletedIndividual, User DeletedHistoricalOnly);
}
