using System.ComponentModel.DataAnnotations;

namespace Mercurius.Modules.Identity.Application.DTOs;

internal class CompleteUserProfileRequest
{
    [Required]
    [StringLength(32, MinimumLength = 3)]
    public string Username { get; set; } = string.Empty;

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string Firstname { get; set; } = string.Empty;

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string Lastname { get; set; } = string.Empty;

    [StringLength(100)]
    public string? DiscordId { get; set; }

    [StringLength(100)]
    public string? SteamId { get; set; }

    [StringLength(100)]
    public string? RiotId { get; set; }
}
