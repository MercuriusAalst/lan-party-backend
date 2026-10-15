using Mercurius.Modules.Tournament.Contracts;

namespace Mercurius.Modules.Tournament.Application.DTOs.Matches;

internal sealed class GetMatchActionStateDTO
{
    public GetMatchDTO Match { get; set; } = null!;
    public MatchParticipantSide? AuthorizedParticipant { get; set; }
    public bool CanSubmitScore { get; set; }
    public bool CanForfeit { get; set; }
    public bool CanResolve { get; set; }
    public string? ResolveBlockedReason { get; set; }
    public bool CanForceForfeit { get; set; }
    public string? ForceForfeitBlockedReason { get; set; }
    public bool CanReverse { get; set; }
    public string? ReverseBlockedReason { get; set; }
    public int? Participant1ReportedScore1 { get; set; }
    public int? Participant1ReportedScore2 { get; set; }
    public int? Participant2ReportedScore1 { get; set; }
    public int? Participant2ReportedScore2 { get; set; }
}
