using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Teams.Contracts;
using Mercurius.Modules.Tournament.Application.Services;
using Platform.Eventing;

namespace Mercurius.Modules.Tournament.Application;

// Tournament owns its rows: account deletion and the resulting team captain succession arrive as durable events,
// in either order, and both converge on the same scheduled-roster state.
internal sealed class DeletedUserTournamentCleanupHandler :
    IModuleEventHandler<UserDeletedIntegrationEvent>,
    IModuleEventHandler<TeamCaptainSucceededIntegrationEvent>
{
    private readonly DeletedUserTournamentCleanupService _cleanupService;

    public DeletedUserTournamentCleanupHandler(DeletedUserTournamentCleanupService cleanupService)
    {
        _cleanupService = cleanupService;
    }

    public string ConsumerName => "tournament-deleted-user-cleanup-v1";

    public Task HandleAsync(
        UserDeletedIntegrationEvent payload,
        ModuleEventContext context,
        CancellationToken cancellationToken = default)
    {
        return _cleanupService.RemoveDeletedUserAsync(payload.UserId.Value, cancellationToken);
    }

    public Task HandleAsync(
        TeamCaptainSucceededIntegrationEvent payload,
        ModuleEventContext context,
        CancellationToken cancellationToken = default)
    {
        return _cleanupService.ApplyCaptainSuccessionAsync(
            payload.TeamId.Value,
            payload.DeletedCaptainUserId.Value,
            payload.NewCaptainUserId?.Value,
            cancellationToken);
    }
}
