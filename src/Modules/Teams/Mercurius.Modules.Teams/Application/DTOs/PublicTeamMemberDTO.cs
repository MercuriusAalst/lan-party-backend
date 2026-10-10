namespace Mercurius.Modules.Teams.Application.DTOs;

internal class PublicTeamMemberDTO
{
    public string Username { get; set; } = string.Empty;

    public PublicTeamMemberDTO()
    {
    }

    public PublicTeamMemberDTO(string username)
    {
        Username = username;
    }
}
