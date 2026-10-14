using Mercurius.Modules.Shared.Exceptions;
using Mercurius.Modules.Teams.Contracts;

namespace Mercurius.Modules.Teams.Domain;

public class Team
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public Guid? CaptainUserId { get; set; }
    public string? LogoUrl { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public long Version { get; set; }

    internal IList<TeamMember> Members { get; set; } = new List<TeamMember>();
    internal IList<TeamInvite> TeamInvites { get; set; } = new List<TeamInvite>();

    public Team()
    {

    }

    public Team(string name, Guid captainUserId)
    {
        UpdateName(name);
        CaptainUserId = captainUserId;
    }

    public void UpdateName(string name)
    {
        var displayName = NormalizeDisplayName(name);
        Name = displayName;
        NormalizedName = displayName.ToLowerInvariant();
    }

    public static string NormalizeName(string name)
    {
        return NormalizeDisplayName(name).ToLowerInvariant();
    }

    private static string NormalizeDisplayName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ValidationException("Team name is required.");

        var trimmedName = name.Trim();
        if (trimmedName.Length > 100)
            throw new ValidationException("Team name cannot exceed 100 characters.");
        if (trimmedName.Any(char.IsControl))
            throw new ValidationException("Team name cannot contain control characters.");

        return trimmedName;
    }

    public void ChangeCaptain(Guid captainUserId)
    {
        if (!Members.Any(member => member.UserId == captainUserId))
            throw new ValidationException($"New captain must be part of the team.");

        CaptainUserId = captainUserId;
    }

    public void RemoveMember(Guid userId)
    {
        var member = Members.FirstOrDefault(teamMember => teamMember.UserId == userId);
        if (member is null)
            throw new NotFoundException($"User not found in {Name}");
        if (member.UserId == CaptainUserId)
            throw new ValidationException("The captain cannot be removed from a team");
        Members.Remove(member);
    }

    internal void AddMember(Guid userId)
    {
        Members.Add(new TeamMember
        {
            Team = this,
            TeamId = Id,
            UserId = userId
        });
    }

    public void Delete(DateTime deletedAtUtc)
    {
        if (IsDeleted)
            return;

        var deletedName = $"deleted-team-{Id:N}";
        Name = deletedName;
        NormalizedName = deletedName;
        CaptainUserId = null;
        LogoUrl = null;
        Members.Clear();
        TeamInvites.Clear();
        IsDeleted = true;
        DeletedAtUtc = deletedAtUtc;
    }

    internal TeamInvite InviteUser(Guid userId, DateTime nowUtc, int inviteResendCooldownDays, int inviteExpirationDays = 14, int declinedInviteResendLimit = 3)
    {
        if (Members.Any(member => member.UserId == userId))
            throw new ValidationException("User is already in the team");
        if (TeamInvites.Any(i => i.UserId == userId && i.Status == TeamInviteStatus.Pending))
            throw new ValidationException("User already has a pending invite to this team");
        var cooldownStart = nowUtc.AddDays(-inviteResendCooldownDays);
        var declinedInvitesInCooldown = TeamInvites
            .Where(i =>
                i.UserId == userId &&
                i.Status == TeamInviteStatus.Declined &&
                i.RespondedAt.HasValue &&
                i.RespondedAt.Value >= cooldownStart)
            .OrderByDescending(i => i.RespondedAt)
            .ToList();

        if (declinedInvitesInCooldown.Count >= declinedInviteResendLimit)
        {
            var lastDeclinedInvite = declinedInvitesInCooldown[0];
            var daysSinceDeclined = (nowUtc - lastDeclinedInvite.RespondedAt!.Value).TotalDays;
            if (daysSinceDeclined < inviteResendCooldownDays)
            {
                throw new ValidationException($"User declined {declinedInviteResendLimit} invites in the cooldown window. Please wait {inviteResendCooldownDays - (int)daysSinceDeclined} more day(s).");
            }
        }
        var invite = new TeamInvite
        {
            TeamId = Id,
            UserId = userId,
            CreatedAt = nowUtc,
            ExpiresAt = nowUtc.AddDays(inviteExpirationDays)
        };
        TeamInvites.Add(invite);
        return invite;
    }
}

