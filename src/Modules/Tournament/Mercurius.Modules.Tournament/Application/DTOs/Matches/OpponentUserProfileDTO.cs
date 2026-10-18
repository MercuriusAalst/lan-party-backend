namespace Mercurius.Modules.Tournament.Application.DTOs.Matches;

internal sealed record OpponentUserProfileDTO(
    string Username,
    string Firstname,
    string Lastname,
    string? DiscordId,
    string? SteamId,
    string? RiotId,
    string? ProfilePictureUrl = null);
