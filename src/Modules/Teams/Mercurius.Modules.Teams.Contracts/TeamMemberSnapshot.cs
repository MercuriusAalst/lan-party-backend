using Mercurius.Modules.Shared;

namespace Mercurius.Modules.Teams.Contracts;

public sealed record TeamMemberSnapshot(
    UserId UserId,
    string? Username,
    string DisplayName,
    bool IsCaptain,
    string? ProfilePictureUrl = null);
