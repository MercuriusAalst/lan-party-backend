using System.Text.Json;
using Mercurius.LAN.API.Data;
using Mercurius.Modules.Identity;
using Mercurius.Modules.Identity.Application.DTOs;
using Mercurius.Modules.Identity.Application.Services.Auth0;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Identity.Domain;
using Mercurius.TestInfrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Eventing;
using Platform.Eventing.Persistence;
using Platform.Extensions;

namespace Mercurius.Api.Tests;

public sealed class Auth0LifecycleOperationsTests
{
    private const string CleanupEventType =
        "Mercurius.Modules.Identity.Contracts.Auth0AccountCleanupRequestedIntegrationEvent";

    [Fact]
    public async Task CurrentLifecycleCleanup_UsesExistingDispatcherAndRetriesProviderFailure()
    {
        await using var database = PostgresTestDatabase.CreateMigrated();
        var cleanupUserId = Guid.NewGuid();
        var subject = $"auth0|dispatch-{Guid.NewGuid():N}";
        var (cleanup, message) = CreateCleanup(subject, cleanupUserId);
        message.Payload = JsonSerializer.Serialize(
            new Auth0AccountCleanupRequestedIntegrationEvent(cleanup.Id, cleanupUserId),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        message.RetryCount = 0;
        message.NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(-1);
        message.DeadLetteredAtUtc = null;
        message.LastError = null;
        await using (var seed = CreateDbContext(database.ConnectionString))
        {
            seed.Add(cleanup);
            seed.OutboxMessages.Add(message);
            await seed.SaveChangesAsync();
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
            var pending = await failedAttempt.Set<Auth0AccountCleanup>().SingleAsync();
            var outbox = await failedAttempt.OutboxMessages.SingleAsync(candidate => candidate.EventType == CleanupEventType);
            Assert.Null(pending.CompletedAtUtc);
            Assert.Equal(subject, pending.Subject);
            Assert.True(outbox.RetryCount > 0);
            Assert.Null(outbox.ProcessedAtUtc);
        }

        provider.Failure = null;
        await using (var release = CreateDbContext(database.ConnectionString))
        {
            await release.OutboxMessages
                .Where(candidate => candidate.EventType == CleanupEventType)
                .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.NextAttemptAtUtc, (DateTime?)null));
        }

        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IModuleEventDispatcher>().DispatchPendingAsync();

        await using var completedAttempt = CreateDbContext(database.ConnectionString);
        var completed = await completedAttempt.Set<Auth0AccountCleanup>().SingleAsync();
        Assert.NotNull(completed.CompletedAtUtc);
        Assert.Equal(string.Empty, completed.Subject);
        Assert.Equal("deleted", completed.SanitizedOutcome);
        Assert.Equal(1, provider.DeleteCalls);
        Assert.Equal(subject, provider.LastDeletedSubject);
    }

    [Fact]
    public async Task DocumentedRetrySql_RequeuesOnlyEligibleDeadLetteredCleanup()
    {
        await using var database = PostgresTestDatabase.CreateMigrated();
        await using var dbContext = CreateDbContext(database.ConnectionString);
        var (eligible, eligibleMessage) = CreateCleanup();
        var (completed, completedMessage) = CreateCleanup();
        completed.CompletedAtUtc = DateTime.UtcNow;
        completed.Subject = string.Empty;
        var (claimed, claimedMessage) = CreateCleanup();
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

    private static (Auth0AccountCleanup Cleanup, OutboxMessage Message) CreateCleanup(
        string? subject = null,
        Guid? userId = null)
    {
        subject ??= $"auth0|retry-{Guid.NewGuid():N}";
        var cleanup = new Auth0AccountCleanup
        {
            Id = Guid.NewGuid(),
            UserId = userId ?? Guid.NewGuid(),
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

    private static MercuriusDBContext CreateDbContext(string connectionString) =>
        new(new DbContextOptionsBuilder<MercuriusDBContext>().UseNpgsql(connectionString).Options);

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
