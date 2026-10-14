namespace Mercurius.Modules.Teams.Application.DTOs;

internal sealed class TeamParticipantResponseDTO
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}
