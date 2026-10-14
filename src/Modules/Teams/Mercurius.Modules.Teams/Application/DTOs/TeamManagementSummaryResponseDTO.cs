namespace Mercurius.Modules.Teams.Application.DTOs;

internal sealed class TeamManagementSummaryResponseDTO
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid CaptainUserId { get; set; }
    public string? CaptainUsername { get; set; }
    public string? LogoUrl { get; set; }
    public IReadOnlyList<PublicUserResponseDTO> Members { get; set; } = [];
}
