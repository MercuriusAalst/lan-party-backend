using Mercurius.Modules.Identity.Infrastructure;
using Mercurius.Modules.Identity.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Eventing.Persistence;

namespace Mercurius.Modules.Identity.Application.Services.Auth0;

internal sealed class Auth0CleanupOperations
{
    private readonly IIdentityDbContext _identityDb;
    private readonly IModuleEventDbContext _eventDb;
    private readonly Auth0SubjectCryptography _cryptography;
    private readonly Auth0ManagementOptions _options;
    private readonly TimeProvider _timeProvider;

    public Auth0CleanupOperations(
        IIdentityDbContext identityDb,
        IModuleEventDbContext eventDb,
        Auth0SubjectCryptography cryptography,
        IOptions<Auth0ManagementOptions> options,
        TimeProvider timeProvider)
    {
        _identityDb = identityDb;
        _eventDb = eventDb;
        _cryptography = cryptography;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public async Task<Auth0CleanupStatus> GetStatusAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var totalJobs = await _identityDb.Auth0AccountCleanups.CountAsync(cancellationToken);
        var pendingJobs = await _identityDb.Auth0AccountCleanups.CountAsync(job => job.CompletedAtUtc == null, cancellationToken);
        var jobs = await _identityDb.Auth0AccountCleanups.AsNoTracking()
            .OrderBy(job => job.CompletedAtUtc != null)
            .ThenByDescending(job => job.RequestedAtUtc)
            .ThenBy(job => job.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        var messageIds = jobs.Select(job => job.OutboxMessageId).ToArray();
        var messages = await _eventDb.OutboxMessages.AsNoTracking()
            .Where(message => messageIds.Contains(message.Id))
            .ToDictionaryAsync(message => message.Id, cancellationToken);
        var legacyPending = await _identityDb.Users.CountAsync(
            user => user.IsDeleted && !user.Auth0UserId.StartsWith("tombstone:"),
            cancellationToken);

        return new Auth0CleanupStatus(
            legacyPending,
            totalJobs,
            pendingJobs,
            !_cryptography.IsConfigured ? "Account cleanup encryption key is missing or invalid." :
            !_options.HasManagementApiConfiguration ? "Auth0 Management API configuration is incomplete." : null,
            jobs.Select(job =>
            {
                messages.TryGetValue(job.OutboxMessageId, out var message);
                return new Auth0CleanupJobStatus(
                    job.Id,
                    job.UserId,
                    job.CompletedAtUtc is not null ? "completed" : message?.DeadLetteredAtUtc is not null ? "dead-lettered" : "pending",
                    message?.RetryCount ?? 0,
                    job.RequestedAtUtc,
                    job.CompletedAtUtc,
                    message?.NextAttemptAtUtc,
                    job.SanitizedOutcome ?? message?.LastError);
            }).ToArray());
    }

    public async Task<bool> RetryAsync(Guid cleanupId, CancellationToken cancellationToken)
    {
        var job = await _identityDb.Auth0AccountCleanups.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == cleanupId, cancellationToken);
        if (job is null || job.CompletedAtUtc is not null)
            return false;

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var updated = await _eventDb.OutboxMessages
            .Where(message => message.Id == job.OutboxMessageId &&
                              message.ProcessedAtUtc == null &&
                              (message.ClaimExpiresAtUtc == null || message.ClaimExpiresAtUtc <= now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(message => message.RetryCount, 0)
                .SetProperty(message => message.LastAttemptAtUtc, (DateTime?)null)
                .SetProperty(message => message.NextAttemptAtUtc, (DateTime?)null)
                .SetProperty(message => message.DeadLetteredAtUtc, (DateTime?)null)
                .SetProperty(message => message.LastError, (string?)null), cancellationToken);
        return updated == 1;
    }
}

internal sealed record Auth0CleanupStatus(
    int LegacyPendingCount,
    int TotalJobs,
    int PendingJobs,
    string? ConfigurationIssue,
    IReadOnlyList<Auth0CleanupJobStatus> Jobs);

internal sealed record Auth0CleanupJobStatus(
    Guid CleanupId,
    Guid UserId,
    string Status,
    int Attempts,
    DateTime RequestedAtUtc,
    DateTime? CompletedAtUtc,
    DateTime? NextAttemptAtUtc,
    string? SanitizedOutcome);
