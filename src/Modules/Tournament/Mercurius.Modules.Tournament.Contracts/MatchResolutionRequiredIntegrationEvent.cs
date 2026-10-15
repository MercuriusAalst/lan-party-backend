using Mercurius.Modules.Shared;

namespace Mercurius.Modules.Tournament.Contracts;

public sealed record MatchResolutionRequiredIntegrationEvent(
    MatchId MatchId,
    TournamentId TournamentId,
    Guid? AssignedAdminUserId = null,
    // Additive: payloads published before this field existed deserialize as Disputed.
    MatchResolutionReason Reason = MatchResolutionReason.Disputed)
{
    public MatchResolutionNotificationRecipient GetRecipient() =>
        AssignedAdminUserId is { } userId
            ? new(MatchResolutionNotificationRecipientKind.AssignedAdmin, new UserId(userId))
            : new(MatchResolutionNotificationRecipientKind.GlobalAdmin, null);
}
