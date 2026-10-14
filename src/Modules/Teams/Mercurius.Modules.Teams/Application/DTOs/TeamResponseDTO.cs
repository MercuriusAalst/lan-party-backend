namespace Mercurius.Modules.Teams.Application.DTOs;

internal sealed class TeamResponseDTO
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid CaptainUserId { get; set; }
    public string? LogoUrl { get; set; }
    public IReadOnlyList<TeamParticipantResponseDTO> Members { get; set; } = [];
}
