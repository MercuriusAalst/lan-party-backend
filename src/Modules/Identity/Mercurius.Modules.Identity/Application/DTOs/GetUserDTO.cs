using Mercurius.Modules.Identity.Domain;

namespace Mercurius.Modules.Identity.Application.DTOs;

internal class GetUserDTO
{
    public Guid Id { get; set; }
    public string? Username { get; set; }
    public string? Firstname { get; set; }
    public string? Lastname { get; set; }
    public string? Email { get; set; }
    public bool EmailVerified { get; set; }
    public string? DiscordId { get; set; }
    public string? SteamId { get; set; }
    public string? RiotId { get; set; }
    public string DisplayName { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public GetUserDTO(User user)
    {
        Id = user.Id;
        Username = user.Username;
        Firstname = user.Firstname;
        Lastname = user.Lastname;
        Email = user.Email;
        EmailVerified = user.EmailVerified;
        DiscordId = user.DiscordId;
        SteamId = user.SteamId;
        RiotId = user.RiotId;
        DisplayName = user.DisplayName;
        IsDeleted = user.IsDeleted;
        CreatedAtUtc = user.CreatedAtUtc;
        UpdatedAtUtc = user.UpdatedAtUtc;
    }
}
