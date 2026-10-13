using Mercurius.Modules.Teams.Domain;
using Mercurius.Modules.Teams.Contracts;
using System.ComponentModel.DataAnnotations;

namespace Mercurius.Modules.Teams.Application.DTOs;

internal class TeamInviteDTO
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid UserId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RespondedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public DateTime? ExpiredAt { get; set; }

    public TeamInviteDTO()
    {

    }

    public TeamInviteDTO(TeamInvite teamInvite)
    {
        Id = teamInvite.Id;
        TeamId = teamInvite.TeamId;
        UserId = teamInvite.UserId;
        Status = teamInvite.Status.ToString();
        CreatedAt = teamInvite.CreatedAt;
        ExpiresAt = teamInvite.ExpiresAt;
        RespondedAt = teamInvite.RespondedAt;
        CancelledAt = teamInvite.CancelledAt;
        ExpiredAt = teamInvite.ExpiredAt;
    }
}

internal class CreateTeamInviteDTO
{
    [Required]
    public Guid TeamId { get; set; }
    [Required]
    public Guid UserId { get; set; }
}

internal class RespondTeamInviteDTO
{
    [Required]
    public bool Accept { get; set; }
}

internal class TransferCaptainDTO
{
    [Required]
    public Guid NewCaptainUserId { get; set; }
}
