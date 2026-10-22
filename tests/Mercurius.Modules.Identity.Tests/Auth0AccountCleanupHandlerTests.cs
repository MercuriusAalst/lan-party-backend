using Mercurius.LAN.API.Data;
using Mercurius.Modules.Identity.Application.Services.Auth0;
using Mercurius.Modules.Identity.Application.DTOs;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Identity.Domain;
using Mercurius.Modules.Identity.Infrastructure;
using Mercurius.Modules.Identity.Options;
using Mercurius.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mercurius.Modules.Identity.Tests;

public sealed class Auth0AccountCleanupHandlerTests
{
    private const string EncryptionKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Fact]
    public async Task HandleAsync_DeletesCapturedOldGenerationThenPurgesPayloadAndSetsFenceCutoff()
    {
        await using var database = PostgresTestDatabase.CreateMigrated();
        await using var dbContext = CreateDbContext(database.ConnectionString);
        var subject = $"google-oauth2|cleanup-{Guid.NewGuid():N}";
        var requestedAt = DateTime.UtcNow.AddMinutes(-10);
        var providerCreatedAt = requestedAt.AddDays(-20);
        var (job, fence, crypto) = await SeedCleanupAsync(dbContext, subject, requestedAt);
        var management = new RecordingManagementService(providerCreatedAt);
        var handler = new Auth0AccountCleanupHandler(
            new IdentityDbContextAdapter<MercuriusDBContext>(dbContext),
            management,
            crypto,
            TimeProvider.System);

        await handler.HandleAsync(
            new Auth0AccountCleanupRequestedIntegrationEvent(job.Id, job.UserId),
            new Platform.Eventing.ModuleEventContext(job.OutboxMessageId, "cleanup", requestedAt));

        dbContext.ChangeTracker.Clear();
        var completed = await dbContext.Set<Auth0AccountCleanup>().SingleAsync(candidate => candidate.Id == job.Id);
        var persistedFence = await dbContext.Set<Auth0SubjectFence>().SingleAsync(candidate => candidate.SubjectHash == fence.SubjectHash);
        Assert.Equal(1, management.DeleteCalls);
        Assert.Equal(subject, management.LastDeletedSubject);
        Assert.Equal(TruncateToPostgresPrecision(providerCreatedAt), completed.ProviderCreatedAtUtc);
        Assert.NotNull(completed.CompletedAtUtc);
        Assert.Equal("deleted", completed.SanitizedOutcome);
        Assert.Equal(string.Empty, completed.EncryptedSubject);
        Assert.Null(persistedFence.PendingCleanupJobId);
        Assert.Equal(
            new DateTimeOffset(completed.CompletedAtUtc!.Value).ToUnixTimeSeconds() + 1,
            persistedFence.RejectTokensBeforeUnixSeconds);

        await handler.HandleAsync(
            new Auth0AccountCleanupRequestedIntegrationEvent(job.Id, job.UserId),
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
        var (job, fence, crypto) = await SeedCleanupAsync(dbContext, subject, requestedAt);
        var management = new RecordingManagementService(providerCreatedAt);
        var handler = new Auth0AccountCleanupHandler(
            new IdentityDbContextAdapter<MercuriusDBContext>(dbContext),
            management,
            crypto,
            TimeProvider.System);

        await handler.HandleAsync(
            new Auth0AccountCleanupRequestedIntegrationEvent(job.Id, job.UserId),
            new Platform.Eventing.ModuleEventContext(job.OutboxMessageId, "cleanup", requestedAt));

        dbContext.ChangeTracker.Clear();
        var completed = await dbContext.Set<Auth0AccountCleanup>().SingleAsync(candidate => candidate.Id == job.Id);
        var persistedFence = await dbContext.Set<Auth0SubjectFence>().SingleAsync(candidate => candidate.SubjectHash == fence.SubjectHash);
        Assert.Equal(0, management.DeleteCalls);
        Assert.Equal(TruncateToPostgresPrecision(providerCreatedAt), completed.ProviderCreatedAtUtc);
        Assert.Equal("replacement-preserved", completed.SanitizedOutcome);
        Assert.Empty(completed.EncryptedSubject);
        Assert.Null(persistedFence.PendingCleanupJobId);
        Assert.NotNull(persistedFence.RejectTokensBeforeUnixSeconds);
    }

    [Fact]
    public async Task HandleAsync_RetryAfterReplacementCompletionFailureNeverDeletesReplacement()
    {
        await using var database = PostgresTestDatabase.CreateMigrated();
        await using var dbContext = CreateDbContext(database.ConnectionString);
        var subject = $"auth0|replacement-retry-{Guid.NewGuid():N}";
        var requestedAt = DateTime.UtcNow.AddMinutes(-10);
        var replacementCreatedAt = requestedAt.AddMinutes(1);
        var (job, _, crypto) = await SeedCleanupAsync(dbContext, subject, requestedAt);
        var management = new RecordingManagementService(replacementCreatedAt);
        var handler = new Auth0AccountCleanupHandler(
            new IdentityDbContextAdapter<MercuriusDBContext>(dbContext),
            management,
            crypto,
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
            BEFORE UPDATE ON identity.auth0_account_cleanups
            FOR EACH ROW EXECUTE FUNCTION identity.fail_auth0_cleanup_completion();
            """);

        var failedAttempt = await Record.ExceptionAsync(() => handler.HandleAsync(
            new Auth0AccountCleanupRequestedIntegrationEvent(job.Id, job.UserId),
            new Platform.Eventing.ModuleEventContext(job.OutboxMessageId, "cleanup", requestedAt)));

        Assert.Equal("Auth0 account cleanup is temporarily unavailable.", failedAttempt?.Message);
        dbContext.ChangeTracker.Clear();
        await using (var triggerCleanup = CreateDbContext(database.ConnectionString))
        {
            await triggerCleanup.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_auth0_cleanup_completion ON identity.auth0_account_cleanups;");
            await triggerCleanup.Database.ExecuteSqlRawAsync("DROP FUNCTION identity.fail_auth0_cleanup_completion();");
        }

        await handler.HandleAsync(
            new Auth0AccountCleanupRequestedIntegrationEvent(job.Id, job.UserId),
            new Platform.Eventing.ModuleEventContext(job.OutboxMessageId, "cleanup", requestedAt));

        dbContext.ChangeTracker.Clear();
        var completed = await dbContext.Set<Auth0AccountCleanup>().SingleAsync(candidate => candidate.Id == job.Id);
        Assert.Equal(0, management.DeleteCalls);
        Assert.Null(completed.ProviderCreatedAtUtc);
        Assert.Equal("replacement-preserved", completed.SanitizedOutcome);
        Assert.NotNull(completed.CompletedAtUtc);
        Assert.Empty(completed.EncryptedSubject);
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
        var (job, fence, crypto) = await SeedCleanupAsync(dbContext, subject, requestedAt);
        Exception failure = failureKind == "timeout"
            ? new TaskCanceledException($"request timeout for https://tenant.example/users/{subject}")
            : new Auth0ManagementRequestException($"Auth0 account lookup failed ({failureKind}).");
        var management = new RecordingManagementService(null)
        {
            Failure = failure
        };
        var handler = new Auth0AccountCleanupHandler(
            new IdentityDbContextAdapter<MercuriusDBContext>(dbContext),
            management,
            crypto,
            TimeProvider.System);

        var exception = await Record.ExceptionAsync(() => handler.HandleAsync(
            new Auth0AccountCleanupRequestedIntegrationEvent(job.Id, job.UserId),
            new Platform.Eventing.ModuleEventContext(job.OutboxMessageId, "cleanup", requestedAt)));

        dbContext.ChangeTracker.Clear();
        var retryable = await dbContext.Set<Auth0AccountCleanup>().SingleAsync(candidate => candidate.Id == job.Id);
        var pendingFence = await dbContext.Set<Auth0SubjectFence>().SingleAsync(candidate => candidate.SubjectHash == fence.SubjectHash);
        Assert.NotNull(exception);
        Assert.Equal(failureKind == "timeout" ? "Auth0 account cleanup is temporarily unavailable." : $"Auth0 account lookup failed ({failureKind}).", exception!.Message);
        Assert.DoesNotContain(subject, exception.ToString(), StringComparison.Ordinal);
        Assert.Null(retryable.CompletedAtUtc);
        Assert.NotEmpty(retryable.EncryptedSubject);
        Assert.Equal(job.Id, pendingFence.PendingCleanupJobId);
    }

    private static async Task<(Auth0AccountCleanup Job, Auth0SubjectFence Fence, Auth0SubjectCryptography Crypto)> SeedCleanupAsync(
        MercuriusDBContext dbContext,
        string subject,
        DateTime requestedAt)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new Auth0ManagementOptions { AccountCleanupEncryptionKey = EncryptionKey });
        var crypto = new Auth0SubjectCryptography(options);
        var job = new Auth0AccountCleanup
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            SubjectHash = Auth0SubjectCryptography.Hash(subject),
            EncryptedSubject = string.Empty,
            RequestedAtUtc = requestedAt,
            OutboxMessageId = Guid.NewGuid()
        };
        // Bind the ciphertext to the durable cleanup ID.
        job.EncryptedSubject = crypto.Encrypt(subject, job.Id);
        var fence = new Auth0SubjectFence { SubjectHash = job.SubjectHash, PendingCleanupJobId = job.Id };
        dbContext.AddRange(job, fence);
        await dbContext.SaveChangesAsync();
        return (job, fence, crypto);
    }

    private static MercuriusDBContext CreateDbContext(string connectionString) =>
        new(new DbContextOptionsBuilder<MercuriusDBContext>().UseNpgsql(connectionString).Options);

    private static DateTime TruncateToPostgresPrecision(DateTime value) =>
        new(value.Ticks - value.Ticks % 10, DateTimeKind.Utc);

    private sealed class RecordingManagementService(DateTime? createdAt) : IAuth0ManagementService
    {
        public Exception? Failure { get; init; }
        public int DeleteCalls { get; private set; }
        public string? LastDeletedSubject { get; private set; }

        public Task<Auth0ProfileSnapshot> GetUserProfileAsync(string auth0UserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Auth0ProfileSnapshot(null, null, false));
        public Task<DateTime?> GetUserCreatedAtAsync(string auth0UserId, CancellationToken cancellationToken = default) =>
            Failure is null ? Task.FromResult(createdAt) : Task.FromException<DateTime?>(Failure);
        public Task DeleteUserAsync(string auth0UserId, CancellationToken cancellationToken = default)
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
