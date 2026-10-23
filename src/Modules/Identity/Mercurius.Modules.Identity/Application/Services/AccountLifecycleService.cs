using Mercurius.Modules.Identity.Domain;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Identity.Infrastructure;
using Platform.Eventing;
using Microsoft.EntityFrameworkCore;
using Mercurius.Modules.Shared.Exceptions;

namespace Mercurius.Modules.Identity.Application.Services;

internal sealed class AccountLifecycleService
{
    private readonly IIdentityDbContext _dbContext;
    private readonly IModuleEventPublisher _eventPublisher;

    public AccountLifecycleService(
        IIdentityDbContext dbContext,
        IModuleEventPublisher eventPublisher)
    {
        _dbContext = dbContext;
        _eventPublisher = eventPublisher;
    }

    public async Task<Guid> QueueCleanupAsync(string subject, Guid userId, DateTime requestedAtUtc, CancellationToken cancellationToken = default)
    {
        await UserMutationLock.AcquireSubjectAsync(_dbContext, subject, cancellationToken);
        var subjectHash = IdentitySubjectHash.Compute(subject);
        var pendingCleanupJobId = await _dbContext.AccountCleanups
            .Where(cleanup => cleanup.SubjectHash == subjectHash && cleanup.CompletedAtUtc == null)
            .Select(cleanup => (Guid?)cleanup.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (pendingCleanupJobId is Guid existingJobId)
            return existingJobId;

        var jobId = Guid.NewGuid();
        var messageId = _eventPublisher.Publish(
            new AccountCleanupRequestedIntegrationEvent(jobId, userId),
            requestedAtUtc);
        _dbContext.AccountCleanups.Add(new AccountCleanup
        {
            Id = jobId,
            UserId = userId,
            SubjectHash = subjectHash,
            Subject = subject,
            RequestedAtUtc = requestedAtUtc,
            OutboxMessageId = messageId
        });
        return jobId;
    }

    public async Task EnsureSubjectCanRegisterAsync(string subject, string? issuedAtUnixSeconds, bool enforceCallerToken, CancellationToken cancellationToken = default)
    {
        var subjectHash = IdentitySubjectHash.Compute(subject.Trim());
        var pendingCleanupExists = await _dbContext.AccountCleanups.AsNoTracking()
            .AnyAsync(cleanup => cleanup.SubjectHash == subjectHash && cleanup.CompletedAtUtc == null, cancellationToken);
        if (pendingCleanupExists)
            throw new ConflictException("account_cleanup_pending", "This account is still being removed from the identity provider.");
        var currentUserExists = await _dbContext.Users.AsNoTracking()
            .AnyAsync(user => user.Auth0UserId == subject.Trim(), cancellationToken);
        if (currentUserExists)
            throw new ConflictException("profile_exists", "The account already has a local profile.");
        var lastCompletedAtUtc = await _dbContext.AccountCleanups.AsNoTracking()
            .Where(cleanup => cleanup.SubjectHash == subjectHash)
            .MaxAsync(cleanup => cleanup.CompletedAtUtc, cancellationToken);
        var cutoff = lastCompletedAtUtc is DateTime completedAt
            ? new DateTimeOffset(completedAt).ToUnixTimeSeconds() + 1
            : (long?)null;
        if (enforceCallerToken && cutoff is long requiredCutoff &&
            (!long.TryParse(issuedAtUnixSeconds, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var issuedAt) || issuedAt < requiredCutoff))
            throw new UnauthorizedAccessException("The authenticated account changed during this request.");
    }

    public async Task<bool> IsDeletionPendingAsync(Guid? userId, CancellationToken cancellationToken)
    {
        if (userId is not Guid id)
            return false;

        return await _dbContext.AccountCleanups.AnyAsync(
            cleanup => cleanup.UserId == id && cleanup.CompletedAtUtc == null,
            cancellationToken);
    }
}
