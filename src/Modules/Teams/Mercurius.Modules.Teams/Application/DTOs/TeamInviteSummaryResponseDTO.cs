namespace Mercurius.Modules.Teams.Application.DTOs;

internal sealed class TeamInviteSummaryResponseDTO
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string? TeamLogoUrl { get; set; }
    public Guid UserId { get; set; }
    public string? Username { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
