namespace Mercurius.Modules.Identity.Contracts;

public sealed record Auth0AccountCleanupRequestedIntegrationEvent(Guid CleanupId, Guid UserId);
