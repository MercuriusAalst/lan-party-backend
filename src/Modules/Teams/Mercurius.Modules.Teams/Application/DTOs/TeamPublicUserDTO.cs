using Mercurius.Modules.Identity.Contracts;

namespace Mercurius.Modules.Teams.Application.DTOs;

internal class TeamPublicUserDTO
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? DiscordId { get; set; }
    public string? SteamId { get; set; }
    public string? RiotId { get; set; }

    public TeamPublicUserDTO()
    {
    }

    public TeamPublicUserDTO(UserProfileSummary user)
    {
        Id = user.Id.Value;
        Username = string.IsNullOrWhiteSpace(user.Username) ? "Incomplete profile" : user.Username;
        DisplayName = Username;
        DiscordId = user.DiscordId;
        SteamId = user.SteamId;
        RiotId = user.RiotId;
    }
}
