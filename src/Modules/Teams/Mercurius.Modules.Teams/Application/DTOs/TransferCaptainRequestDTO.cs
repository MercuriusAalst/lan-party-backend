using System.ComponentModel.DataAnnotations;

namespace Mercurius.Modules.Teams.Application.DTOs;

internal sealed class TransferCaptainRequestDTO
{
    [Required]
    public Guid NewCaptainUserId { get; set; }
}
