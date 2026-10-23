using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Identity.Infrastructure;
using Mercurius.Modules.Identity.Application.Services;
using Microsoft.EntityFrameworkCore;
using Platform.Eventing;

namespace Mercurius.Modules.Identity.Application.Services.Auth0;

internal sealed class Auth0AccountCleanupHandler : IModuleEventHandler<Auth0AccountCleanupRequestedIntegrationEvent>
{
    private readonly IIdentityDbContext _dbContext;
    private readonly IAuth0ManagementService _managementService;
    private readonly TimeProvider _timeProvider;

    public Auth0AccountCleanupHandler(
        IIdentityDbContext dbContext,
        IAuth0ManagementService managementService,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _managementService = managementService;
        _timeProvider = timeProvider;
    }

    public string ConsumerName => "identity.auth0-account-cleanup";

    public async Task HandleAsync(Auth0AccountCleanupRequestedIntegrationEvent payload, ModuleEventContext context, CancellationToken cancellationToken = default)
    {
        using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptTimeout.CancelAfter(TimeSpan.FromSeconds(60));
        var attemptToken = attemptTimeout.Token;
        var cleanup = await _dbContext.Auth0AccountCleanups.AsNoTracking()
            .SingleOrDefaultAsync(job => job.Id == payload.CleanupId, attemptToken)
            ?? throw new Auth0CleanupRetryException();
        if (cleanup.CompletedAtUtc is not null)
            return;

        var subject = cleanup.Subject;
        try
        {
            var providerCreatedAt = await _managementService.GetUserCreatedAtAsync(subject, attemptToken);
            string outcome;
            if (providerCreatedAt is null)
            {
                outcome = "already-absent";
            }
            else if (cleanup.ProviderCreatedAtUtc is DateTime capturedGeneration
                         ? providerCreatedAt.Value != capturedGeneration
                         : providerCreatedAt.Value > cleanup.RequestedAtUtc)
            {
                // A recreated provider account is a different incarnation. Keep it intact.
                // Do not save the replacement timestamp as the target generation: a retry after
                // completion fails must still compare it with the original deletion request.
                outcome = "replacement-preserved";
            }
            else
            {
                await RecordProviderGenerationAsync(cleanup.Id, providerCreatedAt.Value, attemptToken);
                // Auth0 does not offer a conditional delete by generation: a recreate between
                // this GET and DELETE remains an external-provider TOCTOU limitation.
                await _managementService.DeleteUserAsync(subject, attemptToken);
                outcome = "deleted";
            }

            await CompleteAsync(cleanup.Id, subject, outcome, attemptToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Auth0ManagementRequestException exception)
        {
            // These messages are deliberately curated and contain no provider subject, URI or body.
            throw new Auth0CleanupRetryException(exception.Message);
        }
        catch
        {
            // Outbox LastError persists exception text. Never retain provider URL or subject.
            throw new Auth0CleanupRetryException();
        }
    }

    private async Task CompleteAsync(Guid cleanupId, string subject, string outcome, CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await UserMutationLock.AcquireSubjectAsync(_dbContext, subject, cancellationToken);
        var job = await _dbContext.Auth0AccountCleanups.SingleAsync(candidate => candidate.Id == cleanupId, cancellationToken);
        if (job.CompletedAtUtc is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var completedAt = _timeProvider.GetUtcNow().UtcDateTime;
        job.CompletedAtUtc = completedAt;
        job.SanitizedOutcome = outcome;
        job.Subject = string.Empty;

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task RecordProviderGenerationAsync(Guid cleanupId, DateTime providerCreatedAtUtc, CancellationToken cancellationToken)
    {
        await _dbContext.Auth0AccountCleanups
            .Where(job => job.Id == cleanupId && job.CompletedAtUtc == null && job.ProviderCreatedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.ProviderCreatedAtUtc, providerCreatedAtUtc), cancellationToken);
    }

    private sealed class Auth0CleanupRetryException : Exception
    {
        public Auth0CleanupRetryException() : base("Auth0 account cleanup is temporarily unavailable.") { }
        public Auth0CleanupRetryException(string safeMessage) : base(safeMessage) { }
    }
}
