using System.ComponentModel.DataAnnotations;

namespace Mercurius.Modules.Teams.Application.DTOs;

internal sealed class RespondTeamInviteRequestDTO
{
    [Required]
    public bool Accept { get; set; }
}
