using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mercurius.Modules.Identity.Infrastructure;
using Mercurius.Modules.Identity.Application.Services;

namespace Mercurius.Modules.Identity.Application.Services.Auth0;

internal sealed class Auth0LegacyAccountBackfill : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<Auth0LegacyAccountBackfill> _logger;

    public Auth0LegacyAccountBackfill(IServiceScopeFactory scopeFactory, ILogger<Auth0LegacyAccountBackfill> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await ProcessBatchAsync(stoppingToken) > 0)
                    continue;
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError("Legacy Auth0 cleanup backfill failed ({FailureType}); it will retry.", exception.GetType().Name);
            }

            await Task.Delay(RetryDelay, stoppingToken);
        }
    }

    private async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IIdentityDbContext>();
        var lifecycle = scope.ServiceProvider.GetRequiredService<Auth0AccountLifecycleService>();
        var legacyUserIds = await dbContext.Users.AsNoTracking()
            .Where(user => user.IsDeleted && !user.Auth0UserId.StartsWith("tombstone:"))
            .OrderBy(user => user.Id)
            .Select(user => user.Id)
            .Take(100)
            .ToListAsync(cancellationToken);

        foreach (var userId in legacyUserIds)
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var expectedSubject = await dbContext.Users.AsNoTracking()
                .Where(user => user.Id == userId && user.IsDeleted)
                .Select(user => user.Auth0UserId)
                .FirstOrDefaultAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(expectedSubject) || expectedSubject.StartsWith("tombstone:", StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken);
                continue;
            }

            await UserMutationLock.AcquireSubjectAsync(dbContext, expectedSubject, cancellationToken);
            await UserMutationLock.AcquireAsync(dbContext, userId, cancellationToken);
            var user = await dbContext.Users.SingleOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);
            if (user is null || !user.IsDeleted || !string.Equals(user.Auth0UserId, expectedSubject, StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken);
                continue;
            }

            await lifecycle.QueueCleanupAsync(
                expectedSubject,
                user.Id,
                user.DeletedAtUtc ?? user.UpdatedAtUtc,
                cancellationToken);
            user.Auth0UserId = $"tombstone:{user.Id:N}";
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return legacyUserIds.Count;
    }
}
