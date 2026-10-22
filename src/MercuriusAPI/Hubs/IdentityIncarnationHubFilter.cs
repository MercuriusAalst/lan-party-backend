using Mercurius.Modules.Identity.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace Mercurius.LAN.API.Hubs;

public sealed class IdentityIncarnationHubFilter(
    IIdentityTokenValidator validator,
    IAuthenticatedIdentityContext identityContext) : IHubFilter
{
    private const string ConnectionIdentityKey = "Mercurius.Identity.Incarnation";

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        await ValidateAsync(context.Context, context.Context.ConnectionAborted);
        context.Context.Items[ConnectionIdentityKey] = GetPinnedIdentity();
        await next(context);
    }

    public Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next) =>
        next(context, exception);

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var context = invocationContext.Context;
        await ValidateAsync(context, context.ConnectionAborted);
        if (!context.Items.TryGetValue(ConnectionIdentityKey, out var initialIdentity) ||
            !string.Equals(initialIdentity as string, GetPinnedIdentity(), StringComparison.Ordinal))
            throw new HubException("The authenticated account changed. Reconnect and sign in again.");

        return await next(invocationContext);
    }

    private async Task ValidateAsync(HubCallerContext context, CancellationToken cancellationToken)
    {
        var subjects = context.User?.FindAll("sub").Select(claim => claim.Value).ToArray() ?? [];
        var issuedAt = context.User?.FindAll("iat").Select(claim => claim.Value).ToArray() ?? [];
        if (subjects.Length != 1 || issuedAt.Length > 1 ||
            !await validator.ValidateAndPinAsync(subjects[0], issuedAt.SingleOrDefault(), false, cancellationToken))
            throw new HubException("The authenticated account is no longer valid. Sign in again.");
    }

    private string GetPinnedIdentity() =>
        identityContext.UserId?.ToString("N") ?? $"no-profile:{identityContext.Subject}";
}
