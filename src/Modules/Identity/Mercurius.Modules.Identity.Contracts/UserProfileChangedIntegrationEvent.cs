using Mercurius.Modules.Shared;

namespace Mercurius.Modules.Identity.Contracts;

public sealed record UserProfileChangedIntegrationEvent(
    UserId UserId,
    string? Username,
    string DisplayName,
    bool IsDeleted,
    bool IsSearchable,
    DateTime UpdatedAtUtc,
    string? ProfilePictureUrl = null);
