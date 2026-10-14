namespace Mercurius.Modules.Teams.Application.DTOs;

internal sealed class PublicTeamTournamentResponseDTO
{
    public Guid TournamentId { get; set; }
    public string Name { get; set; } = string.Empty;
}
