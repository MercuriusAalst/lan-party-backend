using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Teams.Application.Services;
using Platform.Eventing;

namespace Mercurius.Modules.Teams.Application;

// Teams owns its rows: the durable user-deleted event triggers removal of the account from every live team.
internal sealed class UserDeletedTeamCleanupHandler : IModuleEventHandler<UserDeletedIntegrationEvent>
{
    private readonly DeletedUserTeamCleanupService _cleanupService;

    public UserDeletedTeamCleanupHandler(DeletedUserTeamCleanupService cleanupService)
    {
        _cleanupService = cleanupService;
    }

    public string ConsumerName => "teams-deleted-user-cleanup-v1";

    public Task HandleAsync(
        UserDeletedIntegrationEvent payload,
        ModuleEventContext context,
        CancellationToken cancellationToken = default)
    {
        return _cleanupService.RemoveDeletedUserAsync(payload.UserId.Value, cancellationToken);
    }
}
