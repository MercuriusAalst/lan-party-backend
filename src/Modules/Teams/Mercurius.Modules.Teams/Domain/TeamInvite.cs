using Mercurius.Modules.Shared.Exceptions;
using Mercurius.Modules.Teams.Contracts;

namespace Mercurius.Modules.Teams.Domain;

internal class TeamInvite
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Team Team { get; set; } = null!;
    public Guid UserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public TeamInviteStatus Status { get; set; } = TeamInviteStatus.Pending;
    public DateTime? RespondedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public DateTime? ExpiredAt { get; set; }

    public void Respond(bool accept, DateTime nowUtc)
    {
        if (Status != TeamInviteStatus.Pending)
            throw new ValidationException("Cannot respond to an invite that is not pending.");
        if (ExpiresAt <= nowUtc)
        {
            Expire(nowUtc);
            throw new ValidationException("Cannot respond to an expired invite.");
        }

        Status = accept ? TeamInviteStatus.Accepted : TeamInviteStatus.Declined;
        if (accept)
            Team.AddMember(UserId);
        RespondedAt = nowUtc;
    }

    public void Cancel(DateTime nowUtc)
    {
        if (Status != TeamInviteStatus.Pending)
            throw new ValidationException("Cannot cancel an invite that is not pending.");

        Status = TeamInviteStatus.Cancelled;
        CancelledAt = nowUtc;
    }

    public void Expire(DateTime nowUtc)
    {
        if (Status != TeamInviteStatus.Pending)
            return;

        Status = TeamInviteStatus.Expired;
        ExpiredAt = nowUtc;
    }
}
