namespace Mercurius.Modules.Teams.Application.DTOs;

internal class PublicTeamProfileDTO
{
    public string TeamName { get; set; } = string.Empty;
    public string? CaptainUsername { get; set; }
    public string? LogoUrl { get; set; }
    public IEnumerable<PublicTeamMemberDTO> Members { get; set; } = [];
    public IEnumerable<PublicTeamTournamentDTO> Tournaments { get; set; } = [];
}
