namespace Mercurius.Modules.Teams.Application.DTOs;

internal class PublicTeamMemberDTO
{
    public string Username { get; set; } = string.Empty;
    public string? ProfilePictureUrl { get; set; }

    public PublicTeamMemberDTO()
    {
    }

    public PublicTeamMemberDTO(string username, string? profilePictureUrl = null)
    {
        Username = username;
        ProfilePictureUrl = profilePictureUrl;
    }
}
