namespace Mercurius.Modules.Teams.Application.DTOs;

internal sealed class PublicUserResponseDTO
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? DiscordId { get; set; }
    public string? SteamId { get; set; }
    public string? RiotId { get; set; }
}
