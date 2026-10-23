using System.Globalization;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Identity.Infrastructure;
using Mercurius.Modules.Identity.Application.Services;
using Microsoft.EntityFrameworkCore;

namespace Mercurius.Modules.Identity.Application.Services.Auth0;

internal sealed class IdentityTokenValidator : IIdentityTokenValidator
{
    private readonly IIdentityDbContext _dbContext;
    private readonly IAuthenticatedIdentityContext _identityContext;

    public IdentityTokenValidator(
        IIdentityDbContext dbContext,
        IAuthenticatedIdentityContext identityContext)
    {
        _dbContext = dbContext;
        _identityContext = identityContext;
    }

    public async Task<bool> ValidateAndPinAsync(
        string subject,
        string? issuedAtUnixSeconds,
        bool allowDeletedSelfRead,
        CancellationToken cancellationToken = default)
    {
        subject = subject.Trim();
        if (subject.Length == 0)
        {
            _identityContext.Clear();
            return false;
        }

        var hash = Auth0SubjectCryptography.Hash(subject);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await UserMutationLock.AcquireSubjectAsync(_dbContext, subject, cancellationToken);
        var pendingCleanupExists = await _dbContext.Auth0AccountCleanups.AsNoTracking()
            .AnyAsync(cleanup => cleanup.SubjectHash == hash && cleanup.CompletedAtUtc == null, cancellationToken);
        var lastCompletedAtUtc = await _dbContext.Auth0AccountCleanups.AsNoTracking()
            .Where(cleanup => cleanup.SubjectHash == hash)
            .MaxAsync(cleanup => cleanup.CompletedAtUtc, cancellationToken);
        var user = await _dbContext.Users.AsNoTracking()
            .Where(candidate => candidate.Auth0UserId == subject)
            .Select(candidate => new { candidate.Id, candidate.IsDeleted })
            .FirstOrDefaultAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var hasCleanupHistory = pendingCleanupExists || lastCompletedAtUtc is not null;
        var hasValidIssuedAt = long.TryParse(issuedAtUnixSeconds, NumberStyles.None, CultureInfo.InvariantCulture, out var issuedAt);
        var activeLocalUser = user is { IsDeleted: false };
        var tokenCutoff = lastCompletedAtUtc is DateTime completedAt
            ? new DateTimeOffset(completedAt).ToUnixTimeSeconds() + 1
            : (long?)null;
        var staleIssuedAt = tokenCutoff is long cutoff &&
                            (!hasValidIssuedAt || issuedAt < cutoff);

        if (pendingCleanupExists && (!allowDeletedSelfRead || activeLocalUser))
            return Deny();
        if (tokenCutoff is not null && staleIssuedAt &&
            !(allowDeletedSelfRead && !activeLocalUser))
            return Deny();
        if (user is { IsDeleted: true } && !allowDeletedSelfRead)
            return Deny();
        if (!hasValidIssuedAt && hasCleanupHistory && !allowDeletedSelfRead)
            return Deny();

        var deletedSession = allowDeletedSelfRead && !activeLocalUser &&
                             (user is { IsDeleted: true } || pendingCleanupExists || staleIssuedAt);
        _identityContext.Pin(subject, activeLocalUser ? user!.Id : null, deletedSession, issuedAtUnixSeconds);
        return true;

        bool Deny()
        {
            _identityContext.Clear();
            return false;
        }
    }
}
