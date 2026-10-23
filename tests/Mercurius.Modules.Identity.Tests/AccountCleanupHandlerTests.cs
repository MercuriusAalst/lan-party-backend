using Mercurius.LAN.API.Data;
using Mercurius.Modules.Identity.Application.Services;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Identity.Domain;
using Mercurius.Modules.Identity.Infrastructure;
using Mercurius.TestInfrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mercurius.Modules.Identity.Tests;

public sealed class AccountCleanupHandlerTests
{
    [Fact]
    public async Task HandleAsync_DeletesCapturedOldGenerationThenPurgesPayloadAndRecordsCompletion()
    {
        await using var database = PostgresTestDatabase.CreateMigrated();
        await using var dbContext = CreateDbContext(database.ConnectionString);
        var subject = $"google-oauth2|cleanup-{Guid.NewGuid():N}";
        var requestedAt = DateTime.UtcNow.AddMinutes(-10);
        var providerCreatedAt = requestedAt.AddDays(-20);
        var job = await SeedCleanupAsync(dbContext, subject, requestedAt);
        var management = new RecordingManagementService(providerCreatedAt);
        var handler = new AccountCleanupHandler(
            new IdentityDbContextAdapter<MercuriusDBContext>(dbContext),
            management,
            TimeProvider.System);

        await handler.HandleAsync(
            new AccountCleanupRequestedIntegrationEvent(job.Id, job.UserId),
            new Platform.Eventing.ModuleEventContext(job.OutboxMessageId, "cleanup", requestedAt));

        dbContext.ChangeTracker.Clear();
        var completed = await dbContext.Set<AccountCleanup>().SingleAsync(candidate => candidate.Id == job.Id);
        Assert.Equal(1, management.DeleteCalls);
        Assert.Equal(subject, management.LastDeletedSubject);
        Assert.Equal(TruncateToPostgresPrecision(providerCreatedAt), completed.ProviderCreatedAtUtc);
        Assert.NotNull(completed.CompletedAtUtc);
        Assert.Equal("deleted", completed.SanitizedOutcome);
        Assert.Equal(string.Empty, completed.Subject);
        Assert.Equal(job.SubjectHash, completed.SubjectHash);

        await handler.HandleAsync(
            new AccountCleanupRequestedIntegrationEvent(job.Id, job.UserId),
            new Platform.Eventing.ModuleEventContext(job.OutboxMessageId, "cleanup", requestedAt));
        Assert.Equal(1, management.DeleteCalls);
    }

    [Fact]
    public async Task HandleAsync_PreservesRecreatedProviderGenerationAndCompletesOldCleanup()
    {
        await using var database = PostgresTestDatabase.CreateMigrated();
        await using var dbContext = CreateDbContext(database.ConnectionString);
        var subject = $"auth0|replacement-{Guid.NewGuid():N}";
        var requestedAt = DateTime.UtcNow.AddMinutes(-10);
        var providerCreatedAt = requestedAt.AddMinutes(1);
        var job = await SeedCleanupAsync(dbContext, subject, requestedAt);
        var management = new RecordingManagementService(providerCreatedAt);
        var handler = new AccountCleanupHandler(
            new IdentityDbContextAdapter<MercuriusDBContext>(dbContext),
            management,
            TimeProvider.System);

        await handler.HandleAsync(
            new AccountCleanupRequestedIntegrationEvent(job.Id, job.UserId),
            new Platform.Eventing.ModuleEventContext(job.OutboxMessageId, "cleanup", requestedAt));

        dbContext.ChangeTracker.Clear();
        var completed = await dbContext.Set<AccountCleanup>().SingleAsync(candidate => candidate.Id == job.Id);
        Assert.Equal(0, management.DeleteCalls);
        Assert.Null(completed.ProviderCreatedAtUtc);
        Assert.Equal("replacement-preserved", completed.SanitizedOutcome);
        Assert.Empty(completed.Subject);
        Assert.NotNull(completed.CompletedAtUtc);
    }

    [Fact]
    public async Task HandleAsync_RetryAfterReplacementCompletionFailureNeverDeletesReplacement()
    {
        await using var database = PostgresTestDatabase.CreateMigrated();
        await using var dbContext = CreateDbContext(database.ConnectionString);
        var subject = $"auth0|replacement-retry-{Guid.NewGuid():N}";
        var requestedAt = DateTime.UtcNow.AddMinutes(-10);
        var replacementCreatedAt = requestedAt.AddMinutes(1);
        var job = await SeedCleanupAsync(dbContext, subject, requestedAt);
        var management = new RecordingManagementService(replacementCreatedAt);
        var handler = new AccountCleanupHandler(
            new IdentityDbContextAdapter<MercuriusDBContext>(dbContext),
            management,
            TimeProvider.System);

        await dbContext.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION identity.fail_auth0_cleanup_completion() RETURNS trigger
            LANGUAGE plpgsql AS $trigger$
            BEGIN
                IF NEW."CompletedAtUtc" IS NOT NULL AND OLD."CompletedAtUtc" IS NULL THEN
                    RAISE EXCEPTION 'simulated cleanup completion failure';
                END IF;
                RETURN NEW;
            END;
            $trigger$;
            """);
        await dbContext.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_auth0_cleanup_completion
            BEFORE UPDATE ON identity.account_cleanups
            FOR EACH ROW EXECUTE FUNCTION identity.fail_auth0_cleanup_completion();
            """);

        var failedAttempt = await Record.ExceptionAsync(() => handler.HandleAsync(
            new AccountCleanupRequestedIntegrationEvent(job.Id, job.UserId),
            new Platform.Eventing.ModuleEventContext(job.OutboxMessageId, "cleanup", requestedAt)));

        Assert.Equal("Account cleanup is temporarily unavailable.", failedAttempt?.Message);
        dbContext.ChangeTracker.Clear();
        await using (var triggerCleanup = CreateDbContext(database.ConnectionString))
        {
            await triggerCleanup.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_auth0_cleanup_completion ON identity.account_cleanups;");
            await triggerCleanup.Database.ExecuteSqlRawAsync("DROP FUNCTION identity.fail_auth0_cleanup_completion();");
        }

        await handler.HandleAsync(
            new AccountCleanupRequestedIntegrationEvent(job.Id, job.UserId),
            new Platform.Eventing.ModuleEventContext(job.OutboxMessageId, "cleanup", requestedAt));

        dbContext.ChangeTracker.Clear();
        var completed = await dbContext.Set<AccountCleanup>().SingleAsync(candidate => candidate.Id == job.Id);
        Assert.Equal(0, management.DeleteCalls);
        Assert.Null(completed.ProviderCreatedAtUtc);
        Assert.Equal("replacement-preserved", completed.SanitizedOutcome);
        Assert.NotNull(completed.CompletedAtUtc);
        Assert.Empty(completed.Subject);
    }

    [Theory]
    [InlineData("429")]
    [InlineData("503")]
    [InlineData("timeout")]
    public async Task HandleAsync_PersistsOnlyCuratedProviderFailureAndLeavesCleanupRetryable(string failureKind)
    {
        await using var database = PostgresTestDatabase.CreateMigrated();
        await using var dbContext = CreateDbContext(database.ConnectionString);
        var subject = $"auth0|failure-{Guid.NewGuid():N}";
        var requestedAt = DateTime.UtcNow.AddMinutes(-10);
        var job = await SeedCleanupAsync(dbContext, subject, requestedAt);
        Exception failure = failureKind == "timeout"
            ? new TaskCanceledException($"request timeout for https://tenant.example/users/{subject}")
            : new IdentityProviderRequestException($"Account lookup failed ({failureKind}).");
        var management = new RecordingManagementService(null)
        {
            Failure = failure
        };
        var handler = new AccountCleanupHandler(
            new IdentityDbContextAdapter<MercuriusDBContext>(dbContext),
            management,
            TimeProvider.System);

        var exception = await Record.ExceptionAsync(() => handler.HandleAsync(
            new AccountCleanupRequestedIntegrationEvent(job.Id, job.UserId),
            new Platform.Eventing.ModuleEventContext(job.OutboxMessageId, "cleanup", requestedAt)));

        dbContext.ChangeTracker.Clear();
        var retryable = await dbContext.Set<AccountCleanup>().SingleAsync(candidate => candidate.Id == job.Id);
        Assert.NotNull(exception);
        Assert.Equal(failureKind == "timeout" ? "Account cleanup is temporarily unavailable." : $"Account lookup failed ({failureKind}).", exception!.Message);
        Assert.DoesNotContain(subject, exception.ToString(), StringComparison.Ordinal);
        Assert.Null(retryable.CompletedAtUtc);
        Assert.Equal(subject, retryable.Subject);
    }

    private static async Task<AccountCleanup> SeedCleanupAsync(
        MercuriusDBContext dbContext,
        string subject,
        DateTime requestedAt)
    {
        var job = new AccountCleanup
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            SubjectHash = IdentitySubjectHash.Compute(subject),
            Subject = subject,
            RequestedAtUtc = requestedAt,
            OutboxMessageId = Guid.NewGuid()
        };
        dbContext.Add(job);
        await dbContext.SaveChangesAsync();
        return job;
    }

    private static MercuriusDBContext CreateDbContext(string connectionString) =>
        new(new DbContextOptionsBuilder<MercuriusDBContext>().UseNpgsql(connectionString).Options);

    private static DateTime TruncateToPostgresPrecision(DateTime value) =>
        new(value.Ticks - value.Ticks % 10, DateTimeKind.Utc);

    private sealed class RecordingManagementService(DateTime? createdAt) : IIdentityProviderAccountService
    {
        public Exception? Failure { get; init; }
        public int DeleteCalls { get; private set; }
        public string? LastDeletedSubject { get; private set; }

        public Task<DateTime?> GetAccountCreatedAtAsync(string auth0UserId, CancellationToken cancellationToken = default) =>
            Failure is null ? Task.FromResult(createdAt) : Task.FromException<DateTime?>(Failure);
        public Task DeleteAccountAsync(string auth0UserId, CancellationToken cancellationToken = default)
        {
            if (Failure is not null)
                return Task.FromException(Failure);
            DeleteCalls++;
            LastDeletedSubject = auth0UserId;
            return Task.CompletedTask;
        }
    }
}
