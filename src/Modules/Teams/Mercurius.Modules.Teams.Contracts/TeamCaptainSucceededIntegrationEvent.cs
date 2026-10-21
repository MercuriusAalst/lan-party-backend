using Mercurius.Modules.Shared;

namespace Mercurius.Modules.Teams.Contracts;

// Published when the captain's account was deleted and the team appointed a successor, or none was eligible.
public sealed record TeamCaptainSucceededIntegrationEvent(
    TeamId TeamId,
    long Version,
    UserId DeletedCaptainUserId,
    UserId? NewCaptainUserId);
