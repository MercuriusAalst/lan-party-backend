using Mercurius.Modules.Tournament.Contracts;

namespace Mercurius.Modules.Tournament.Application.DTOs.Matches;

/// <summary>
/// A match that needs an administrator to decide its result. Reported scores are the participants' unconfirmed claims, never a final result.
/// </summary>
internal sealed class PendingMatchResolutionDTO
{
    public Guid MatchId { get; set; }
    public Guid TournamentId { get; set; }
    public string TournamentName { get; set; } = string.Empty;
    public string Participant1Name { get; set; } = string.Empty;
    public string Participant2Name { get; set; } = string.Empty;
    public int? Participant1ReportedScore1 { get; set; }
    public int? Participant1ReportedScore2 { get; set; }
    public int? Participant2ReportedScore1 { get; set; }
    public int? Participant2ReportedScore2 { get; set; }
    public MatchResolutionReason Reason { get; set; }
    // Null only while the notification for the transition has not been written yet (outbox lag) or for matches that predate it.
    public DateTime? RequiredSinceUtc { get; set; }
}
