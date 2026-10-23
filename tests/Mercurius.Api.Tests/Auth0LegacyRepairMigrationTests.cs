using System.Text.Json;
using Mercurius.LAN.API.Data;
using Mercurius.LAN.API.Migrations;
using Mercurius.Modules.Identity;
using Mercurius.Modules.Identity.Application.DTOs;
using Mercurius.Modules.Identity.Application.Services.Auth0;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Identity.Domain;
using Mercurius.Modules.Identity.Options;
using Mercurius.TestInfrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Eventing;
using Platform.Eventing.Persistence;
using Platform.Extensions;

namespace Mercurius.Api.Tests;

public sealed class Auth0LegacyRepairMigrationTests
{
    private const string LegacyPrerequisiteMigration = "20261021192739_TeamMemberJoinedAt";
    private const string CleanupEventType =
        "Mercurius.Modules.Identity.Contracts.Auth0AccountCleanupRequestedIntegrationEvent";

    [Fact]
    public async Task LifecycleMigration_RepairsLegacyUsersInBoundedIdempotentBatches()
    {
        await using var database = PostgresTestDatabase.Create();
        await using var dbContext = CreateDbContext(database.ConnectionString);
        var migrator = dbContext.GetService<IMigrator>();
        await migrator.MigrateAsync(LegacyPrerequisiteMigration);

        var unicodeSubject = "auth0|Zoë-重要-ß";
        var legacyUsers = Enumerable.Range(0, 105)
            .Select(index => CreateUser(
                $"legacy{index:D3}",
                index == 0 ? unicodeSubject : $"auth0|legacy-{index:D3}",
                isDeleted: true))
            .ToList();
        var activeUser = CreateUser("active", "auth0|active", isDeleted: false);
        dbContext.Users.AddRange(legacyUsers);
        dbContext.Users.Add(activeUser);
        await dbContext.SaveChangesAsync();

        await migrator.MigrateAsync();
        await AssertRepairAsync(dbContext, legacyUsers, activeUser, unicodeSubject);

        await dbContext.Database.ExecuteSqlRawAsync(Auth0AccountLifecycle.LegacyCleanupRepairSql);
        await AssertRepairAsync(dbContext, legacyUsers, activeUser, unicodeSubject);
    }

    [Fact]
    public async Task SeededCleanup_UsesExistingDispatcherAndRetriesProviderFailure()
    {
        await using var database = PostgresTestDatabase.Create();
        await using (var migration = CreateDbContext(database.ConnectionString))
        {
            var migrator = migration.GetService<IMigrator>();
            await migrator.MigrateAsync(LegacyPrerequisiteMigration);
            migration.Users.Add(CreateUser("dispatch", "auth0|dispatch-legacy", isDeleted: true));
            await migration.SaveChangesAsync();
            await migrator.MigrateAsync();
        }

        var provider = new RecordingAuth0ManagementService
        {
            Failure = new Auth0ManagementRequestException("Auth0 account lookup failed (429)."),
            CreatedAtUtc = DateTime.UtcNow.AddDays(-10)
        };
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth0:Audience"] = "https://api.example.test"
        });
        builder.Services.AddDbContext<MercuriusDBContext>(options => options.UseNpgsql(database.ConnectionString));
        builder.Services.AddModuleEventing<MercuriusDBContext>();
        builder.Services.AddIdentityModule<MercuriusDBContext>(builder.Configuration);
        builder.Services.AddScoped<IAuth0ManagementService>(_ => provider);
        await using var app = builder.Build();

        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IModuleEventDispatcher>().DispatchPendingAsync();

        await using (var failedAttempt = CreateDbContext(database.ConnectionString))
        {
            var cleanup = await failedAttempt.Set<Auth0AccountCleanup>().SingleAsync();
            var outbox = await failedAttempt.OutboxMessages.SingleAsync(message => message.EventType == CleanupEventType);
            Assert.Null(cleanup.CompletedAtUtc);
            Assert.Equal("auth0|dispatch-legacy", cleanup.Subject);
            Assert.True(outbox.RetryCount > 0);
            Assert.Null(outbox.ProcessedAtUtc);
        }

        provider.Failure = null;
        await using (var release = CreateDbContext(database.ConnectionString))
        {
            await release.OutboxMessages
                .Where(message => message.EventType == CleanupEventType)
                .ExecuteUpdateAsync(setters => setters.SetProperty(message => message.NextAttemptAtUtc, (DateTime?)null));
        }

        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IModuleEventDispatcher>().DispatchPendingAsync();

        await using var completedAttempt = CreateDbContext(database.ConnectionString);
        var completed = await completedAttempt.Set<Auth0AccountCleanup>().SingleAsync();
        Assert.NotNull(completed.CompletedAtUtc);
        Assert.Equal(string.Empty, completed.Subject);
        Assert.Equal("deleted", completed.SanitizedOutcome);
        Assert.Equal(1, provider.DeleteCalls);
        Assert.Equal("auth0|dispatch-legacy", provider.LastDeletedSubject);
    }

    [Fact]
    public async Task DocumentedRetrySql_RequeuesOnlyEligibleDeadLetteredCleanup()
    {
        await using var database = PostgresTestDatabase.CreateMigrated();
        await using var dbContext = CreateDbContext(database.ConnectionString);
        var (eligible, eligibleMessage) = CreatePendingCleanup();
        var (completed, completedMessage) = CreatePendingCleanup();
        completed.CompletedAtUtc = DateTime.UtcNow;
        completed.Subject = string.Empty;
        var (claimed, claimedMessage) = CreatePendingCleanup();
        claimedMessage.ClaimToken = Guid.NewGuid();
        claimedMessage.ClaimExpiresAtUtc = DateTime.UtcNow.AddMinutes(5);
        dbContext.AddRange(eligible, completed, claimed);
        dbContext.OutboxMessages.AddRange(eligibleMessage, completedMessage, claimedMessage);
        await dbContext.SaveChangesAsync();

        var retrySql = LoadRetrySql();
        await ExecuteRetrySqlAsync(database.ConnectionString, retrySql, eligible.Id);
        await ExecuteRetrySqlAsync(database.ConnectionString, retrySql, completed.Id);
        await ExecuteRetrySqlAsync(database.ConnectionString, retrySql, claimed.Id);

        dbContext.ChangeTracker.Clear();
        var messages = await dbContext.OutboxMessages
            .Where(message => message.EventType == CleanupEventType)
            .ToDictionaryAsync(message => message.Id);
        Assert.Equal(0, messages[eligible.OutboxMessageId].RetryCount);
        Assert.Null(messages[eligible.OutboxMessageId].DeadLetteredAtUtc);
        Assert.NotNull(messages[eligible.OutboxMessageId].NextAttemptAtUtc);
        Assert.Equal(5, messages[completed.OutboxMessageId].RetryCount);
        Assert.NotNull(messages[completed.OutboxMessageId].DeadLetteredAtUtc);
        Assert.Equal(5, messages[claimed.OutboxMessageId].RetryCount);
        Assert.NotNull(messages[claimed.OutboxMessageId].DeadLetteredAtUtc);
        Assert.NotNull(messages[claimed.OutboxMessageId].ClaimExpiresAtUtc);
    }

    private static async Task AssertRepairAsync(
        MercuriusDBContext dbContext,
        IReadOnlyList<User> legacyUsers,
        User activeUser,
        string unicodeSubject)
    {
        dbContext.ChangeTracker.Clear();
        var users = await dbContext.Users.AsNoTracking().ToDictionaryAsync(user => user.Id);
        foreach (var legacy in legacyUsers)
        {
            var persisted = users[legacy.Id];
            Assert.Equal($"tombstone:{legacy.Id:N}", persisted.Auth0UserId);
            Assert.Equal(legacy.Username, persisted.Username);
            Assert.Equal(legacy.NormalizedUsername, persisted.NormalizedUsername);
            Assert.Equal(TruncateToPostgresPrecision(legacy.CreatedAtUtc), persisted.CreatedAtUtc);
            Assert.True(persisted.IsDeleted);
        }
        var persistedActive = users[activeUser.Id];
        Assert.Equal(activeUser.Auth0UserId, persistedActive.Auth0UserId);
        Assert.Equal(activeUser.Username, persistedActive.Username);
        Assert.False(persistedActive.IsDeleted);

        var cleanups = await dbContext.Set<Auth0AccountCleanup>().AsNoTracking().ToListAsync();
        Assert.Equal(legacyUsers.Count, cleanups.Count);
        var messages = await dbContext.OutboxMessages.AsNoTracking()
            .Where(message => message.EventType == CleanupEventType)
            .ToDictionaryAsync(message => message.Id);
        Assert.Equal(legacyUsers.Count, messages.Count);

        foreach (var cleanup in cleanups)
        {
            Assert.Equal(Auth0SubjectHash.Compute(cleanup.Subject), cleanup.SubjectHash);
            Assert.Matches("^[0-9a-f]{64}$", cleanup.SubjectHash);
            var message = messages[cleanup.OutboxMessageId];
            using var payload = JsonDocument.Parse(message.Payload);
            Assert.Equal(cleanup.Id, payload.RootElement.GetProperty("cleanupId").GetGuid());
            Assert.Equal(cleanup.UserId, payload.RootElement.GetProperty("userId").GetGuid());
            Assert.DoesNotContain(cleanup.Subject, message.Payload, StringComparison.Ordinal);
        }
        Assert.Equal(
            Auth0SubjectHash.Compute(unicodeSubject),
            cleanups.Single(cleanup => cleanup.Subject == unicodeSubject).SubjectHash);
    }

    private static (Auth0AccountCleanup Cleanup, OutboxMessage Message) CreatePendingCleanup()
    {
        var subject = $"auth0|retry-{Guid.NewGuid():N}";
        var cleanup = new Auth0AccountCleanup
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Subject = subject,
            SubjectHash = Auth0SubjectHash.Compute(subject),
            RequestedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            OutboxMessageId = Guid.NewGuid()
        };
        var message = new OutboxMessage
        {
            Id = cleanup.OutboxMessageId,
            EventType = CleanupEventType,
            Payload = "{}",
            OccurredAtUtc = DateTime.UtcNow.AddMinutes(-10),
            RetryCount = 5,
            NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(5),
            DeadLetteredAtUtc = DateTime.UtcNow,
            LastError = "Auth0 account cleanup is temporarily unavailable."
        };
        return (cleanup, message);
    }

    private static string LoadRetrySql()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "docs", "architecture", "auth0-account-lifecycle.md")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        var document = File.ReadAllText(Path.Combine(
            directory.FullName,
            "docs",
            "architecture",
            "auth0-account-lifecycle.md"));
        const string startMarker = "<!-- cleanup-retry-sql:start -->";
        const string endMarker = "<!-- cleanup-retry-sql:end -->";
        var start = document.IndexOf(startMarker, StringComparison.Ordinal);
        var end = document.IndexOf(endMarker, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var block = document[(start + startMarker.Length)..end];
        const string openingFence = "```sql";
        var codeStart = block.IndexOf(openingFence, StringComparison.Ordinal) + openingFence.Length;
        var lastFence = block.LastIndexOf("```", StringComparison.Ordinal);
        return block[codeStart..lastFence].Trim();
    }

    private static async Task ExecuteRetrySqlAsync(string connectionString, string retrySql, Guid cleanupId)
    {
        var sql = retrySql.Replace(":'cleanup_id'::uuid", $"'{cleanupId:D}'::uuid", StringComparison.Ordinal);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static User CreateUser(string username, string subject, bool isDeleted)
    {
        var now = DateTime.UtcNow;
        return new User
        {
            Id = Guid.NewGuid(),
            Auth0UserId = subject,
            Username = username,
            NormalizedUsername = username,
            Firstname = "Legacy",
            Lastname = "User",
            CreatedAtUtc = now.AddDays(-10),
            UpdatedAtUtc = now.AddDays(-1),
            IsDeleted = isDeleted,
            DeletedAtUtc = isDeleted ? now.AddDays(-1) : null
        };
    }

    private static MercuriusDBContext CreateDbContext(string connectionString) =>
        new(new DbContextOptionsBuilder<MercuriusDBContext>().UseNpgsql(connectionString).Options);

    private static DateTime TruncateToPostgresPrecision(DateTime value) =>
        new(value.Ticks - value.Ticks % 10, DateTimeKind.Utc);

    private sealed class RecordingAuth0ManagementService : IAuth0ManagementService
    {
        public Exception? Failure { get; set; }
        public DateTime? CreatedAtUtc { get; set; }
        public int DeleteCalls { get; private set; }
        public string? LastDeletedSubject { get; private set; }

        public Task<Auth0ProfileSnapshot> GetUserProfileAsync(string auth0UserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Auth0ProfileSnapshot(null, null, false));

        public Task<DateTime?> GetUserCreatedAtAsync(string auth0UserId, CancellationToken cancellationToken = default) =>
            Failure is null ? Task.FromResult(CreatedAtUtc) : Task.FromException<DateTime?>(Failure);

        public Task DeleteUserAsync(string auth0UserId, CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            LastDeletedSubject = auth0UserId;
            return Task.CompletedTask;
        }

        public Task<bool> HasAdminRoleAsync(string auth0UserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<string>> GetAdminUserIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task SendVerificationEmailAsync(string auth0UserId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SendPasswordResetEmailAsync(string email, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
