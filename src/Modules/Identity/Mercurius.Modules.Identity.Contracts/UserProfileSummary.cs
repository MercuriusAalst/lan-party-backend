using Mercurius.Modules.Shared;

namespace Mercurius.Modules.Identity.Contracts;

public sealed record UserProfileSummary(
    UserId Id,
    string? Username,
    string DisplayName,
    bool IsDeleted,
    string? DiscordId,
    string? SteamId,
    string? RiotId,
    string? ProfilePictureUrl = null);
