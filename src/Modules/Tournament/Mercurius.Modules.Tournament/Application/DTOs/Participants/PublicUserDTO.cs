using Mercurius.Modules.Identity.Contracts;

namespace Mercurius.Modules.Tournament.Application.DTOs.Participants;

internal class PublicUserDTO
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? ProfilePictureUrl { get; set; }

    public PublicUserDTO()
    {
    }

    public PublicUserDTO(UserProfileSummary user)
    {
        Id = user.Id.Value;
        Username = string.IsNullOrWhiteSpace(user.Username) ? "Incomplete profile" : user.Username;
        DisplayName = Username;
        ProfilePictureUrl = user.ProfilePictureUrl;
    }
}
