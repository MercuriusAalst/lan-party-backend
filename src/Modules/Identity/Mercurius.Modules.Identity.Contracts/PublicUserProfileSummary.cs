using Mercurius.Modules.Shared;

namespace Mercurius.Modules.Identity.Contracts;

public sealed record PublicUserProfileSummary(
    UserId Id,
    string Username,
    string Firstname,
    string Lastname,
    string? DiscordId,
    string? SteamId,
    string? RiotId,
    string? ProfilePictureUrl = null);
