namespace Mercurius.Modules.Teams.Domain;

internal sealed class TeamMember
{
    public Guid TeamId { get; set; }
    public Team Team { get; set; } = null!;
    public Guid UserId { get; set; }

    // Null marks a membership created before join times were recorded (legacy seniority cohort).
    public DateTime? JoinedAtUtc { get; set; }
}
