namespace Mercurius.Modules.Identity.Contracts;

public sealed record AccountCleanupRequestedIntegrationEvent(Guid CleanupId, Guid UserId);
