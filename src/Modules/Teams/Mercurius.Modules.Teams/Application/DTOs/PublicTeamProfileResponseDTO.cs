namespace Mercurius.Modules.Teams.Application.DTOs;

internal sealed class PublicTeamProfileResponseDTO
{
    public string TeamName { get; set; } = string.Empty;
    public string? CaptainUsername { get; set; }
    public string? LogoUrl { get; set; }
    public IReadOnlyList<PublicTeamMemberResponseDTO> Members { get; set; } = [];
    public IReadOnlyList<PublicTeamTournamentResponseDTO> Tournaments { get; set; } = [];
}
