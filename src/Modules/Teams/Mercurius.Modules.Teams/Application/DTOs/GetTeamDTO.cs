namespace Mercurius.Modules.Teams.Application.DTOs;

internal class GetTeamDTO
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid CaptainUserId { get; set; }
    public string? LogoUrl { get; set; }
    public IEnumerable<TeamPublicUserDTO> Members { get; set; } = [];

    public GetTeamDTO()
    {
    }

}

