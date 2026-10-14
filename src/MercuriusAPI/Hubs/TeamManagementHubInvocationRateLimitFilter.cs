using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Platform.Extensions;

namespace Mercurius.LAN.API.Hubs;

public sealed class TeamManagementHubInvocationRateLimitFilter : IHubFilter, IDisposable
{
    public const int PermitLimit = 20;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(
        partitionKey => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = PermitLimit,
                Window = Window,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            }));

    public ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        if (!IsSubscriptionInvocation(invocationContext.HubMethodName))
            return next(invocationContext);

        using var lease = _limiter.AttemptAcquire(GetPartitionKey(invocationContext.Context));
        if (!lease.IsAcquired)
            throw new HubException(CreateRetryMessage(lease));

        return next(invocationContext);
    }

    public void Dispose() => _limiter.Dispose();

    private static bool IsSubscriptionInvocation(string hubMethodName) =>
        hubMethodName is nameof(TeamManagementHub.JoinTeam) or nameof(TeamManagementHub.LeaveTeam);

    private static string GetPartitionKey(HubCallerContext context)
    {
        var subject = context.User?.FindAuth0UserId();
        if (!string.IsNullOrWhiteSpace(subject))
            return $"user:{subject.Trim()}";

        return $"connection:{context.ConnectionId}";
    }

    private static string CreateRetryMessage(RateLimitLease lease)
    {
        if (!lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            return "Too many team subscription requests. Please try again later.";

        var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        return $"Too many team subscription requests. Retry after {retryAfterSeconds} seconds.";
    }
}
