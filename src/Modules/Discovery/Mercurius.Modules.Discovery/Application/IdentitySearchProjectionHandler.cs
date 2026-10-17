using Mercurius.Modules.Discovery.Domain;
using Mercurius.Modules.Identity.Contracts;
using Platform.Eventing;

namespace Mercurius.Modules.Discovery.Application;

internal sealed class IdentitySearchProjectionHandler :
    IModuleEventHandler<UserProfileChangedIntegrationEvent>,
    IModuleEventHandler<UserDeletedIntegrationEvent>
{
    private readonly SearchDocumentProjector _projector;

    public IdentitySearchProjectionHandler(SearchDocumentProjector projector)
    {
        _projector = projector;
    }

    public string ConsumerName => "discovery-search-identity-v1";

    public Task HandleAsync(
        UserDeletedIntegrationEvent payload,
        ModuleEventContext context,
        CancellationToken cancellationToken = default)
    {
        return _projector.MarkDeletedAsync(
            SearchDocumentTypes.User,
            payload.UserId.Value.ToString(),
            context.OccurredAtUtc.Ticks,
            context.OccurredAtUtc,
            cancellationToken);
    }

    public Task HandleAsync(
        UserProfileChangedIntegrationEvent payload,
        ModuleEventContext context,
        CancellationToken cancellationToken = default)
    {
        if (!payload.IsSearchable || string.IsNullOrWhiteSpace(payload.Username))
        {
            return _projector.MarkDeletedAsync(
                SearchDocumentTypes.User,
                payload.UserId.Value.ToString(),
                context.OccurredAtUtc.Ticks,
                context.OccurredAtUtc,
                cancellationToken);
        }

        return _projector.UpsertAsync(
            SearchDocumentTypes.User,
            payload.UserId.Value.ToString(),
            payload.Username,
            "User",
            imageUrl: payload.ProfilePictureUrl,
            $"/users/{Uri.EscapeDataString(payload.Username)}",
            context.OccurredAtUtc.Ticks,
            context.OccurredAtUtc,
            cancellationToken);
    }
}
