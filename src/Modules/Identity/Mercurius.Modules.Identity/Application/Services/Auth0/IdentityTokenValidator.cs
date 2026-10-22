using System.Globalization;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Identity.Infrastructure;
using Mercurius.Modules.Identity.Application.Services;
using Microsoft.EntityFrameworkCore;

namespace Mercurius.Modules.Identity.Application.Services.Auth0;

internal sealed class IdentityTokenValidator : IIdentityTokenValidator
{
    private readonly IIdentityDbContext _dbContext;
    private readonly Auth0SubjectCryptography _cryptography;
    private readonly IAuthenticatedIdentityContext _identityContext;

    public IdentityTokenValidator(
        IIdentityDbContext dbContext,
        Auth0SubjectCryptography cryptography,
        IAuthenticatedIdentityContext identityContext)
    {
        _dbContext = dbContext;
        _cryptography = cryptography;
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
        var fence = await _dbContext.Auth0SubjectFences.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.SubjectHash == hash, cancellationToken);
        var user = await _dbContext.Users.AsNoTracking()
            .Where(candidate => candidate.Auth0UserId == subject)
            .Select(candidate => new { candidate.Id, candidate.IsDeleted })
            .FirstOrDefaultAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var hasFence = fence is not null;
        var hasValidIssuedAt = long.TryParse(issuedAtUnixSeconds, NumberStyles.None, CultureInfo.InvariantCulture, out var issuedAt);
        var activeLocalUser = user is { IsDeleted: false };
        var staleIssuedAt = fence?.RejectTokensBeforeUnixSeconds is long tokenCutoff &&
                            (!hasValidIssuedAt || issuedAt < tokenCutoff);

        if (fence?.PendingCleanupJobId is not null && (!allowDeletedSelfRead || activeLocalUser))
            return Deny();
        if (fence?.RejectTokensBeforeUnixSeconds is not null && staleIssuedAt &&
            !(allowDeletedSelfRead && !activeLocalUser))
            return Deny();
        if (user is { IsDeleted: true } && !allowDeletedSelfRead)
            return Deny();
        if (!hasValidIssuedAt && hasFence && !allowDeletedSelfRead)
            return Deny();

        var deletedSession = allowDeletedSelfRead && !activeLocalUser &&
                             (user is { IsDeleted: true } || fence?.PendingCleanupJobId is not null || staleIssuedAt);
        _identityContext.Pin(subject, activeLocalUser ? user!.Id : null, deletedSession, issuedAtUnixSeconds);
        return true;

        bool Deny()
        {
            _identityContext.Clear();
            return false;
        }
    }
}
