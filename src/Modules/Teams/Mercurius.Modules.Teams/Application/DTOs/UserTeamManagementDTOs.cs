namespace Mercurius.Modules.Teams.Application.DTOs;

internal class CurrentUserTeamSummaryDTO
{
    public IEnumerable<TeamManagementSummaryDTO> CaptainedTeams { get; set; } = [];
    public IEnumerable<TeamManagementSummaryDTO> MemberTeams { get; set; } = [];
    public IEnumerable<TeamInviteSummaryDTO> ReceivedPendingInvites { get; set; } = [];
    public IEnumerable<TeamInviteSummaryDTO> SentPendingInvites { get; set; } = [];
}

internal class TeamManagementSummaryDTO
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid CaptainUserId { get; set; }
    public string? CaptainUsername { get; set; }
    public string? LogoUrl { get; set; }
    public IEnumerable<TeamPublicUserDTO> Members { get; set; } = [];

    public TeamManagementSummaryDTO()
    {
    }

}

internal class TeamInviteSummaryDTO
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

    public TeamInviteSummaryDTO()
    {
    }

}

