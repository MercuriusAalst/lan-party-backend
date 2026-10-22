using Mercurius.Modules.Identity.Domain;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Identity.Application.Services;
using Mercurius.Modules.Identity.Infrastructure;
using Platform.Eventing;
using Microsoft.EntityFrameworkCore;
using Mercurius.Modules.Shared.Exceptions;

namespace Mercurius.Modules.Identity.Application.Services.Auth0;

internal sealed class Auth0AccountLifecycleService
{
    private readonly IIdentityDbContext _dbContext;
    private readonly IModuleEventPublisher _eventPublisher;
    private readonly Auth0SubjectCryptography _cryptography;
    private readonly TimeProvider _timeProvider;

    public Auth0AccountLifecycleService(
        IIdentityDbContext dbContext,
        IModuleEventPublisher eventPublisher,
        Auth0SubjectCryptography cryptography,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _eventPublisher = eventPublisher;
        _cryptography = cryptography;
        _timeProvider = timeProvider;
    }

    public async Task<Guid> QueueCleanupAsync(string subject, Guid userId, DateTime requestedAtUtc, CancellationToken cancellationToken = default)
    {
        await UserMutationLock.AcquireSubjectAsync(_dbContext, subject, cancellationToken);
        var subjectHash = Auth0SubjectCryptography.Hash(subject);
        var fence = await _dbContext.Auth0SubjectFences.SingleOrDefaultAsync(
            candidate => candidate.SubjectHash == subjectHash,
            cancellationToken);
        if (fence?.PendingCleanupJobId is Guid existingJobId)
            return existingJobId;

        var jobId = Guid.NewGuid();
        var messageId = _eventPublisher.Publish(
            new Auth0AccountCleanupRequestedIntegrationEvent(jobId, userId),
            requestedAtUtc);
        _dbContext.Auth0AccountCleanups.Add(new Auth0AccountCleanup
        {
            Id = jobId,
            UserId = userId,
            SubjectHash = subjectHash,
            EncryptedSubject = _cryptography.Encrypt(subject, jobId),
            RequestedAtUtc = requestedAtUtc,
            OutboxMessageId = messageId
        });
        if (fence is null)
        {
            fence = new Auth0SubjectFence { SubjectHash = subjectHash };
            _dbContext.Auth0SubjectFences.Add(fence);
        }
        fence.PendingCleanupJobId = jobId;
        return jobId;
    }

    public DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    public async Task EnsureSubjectCanRegisterAsync(string subject, CancellationToken cancellationToken = default)
    {
        var subjectHash = Auth0SubjectCryptography.Hash(subject.Trim());
        var fence = await _dbContext.Auth0SubjectFences.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.SubjectHash == subjectHash, cancellationToken);
        if (fence?.PendingCleanupJobId is not null)
            throw new ConflictException("account_cleanup_pending", "This account is still being removed from the identity provider.");
    }
}
