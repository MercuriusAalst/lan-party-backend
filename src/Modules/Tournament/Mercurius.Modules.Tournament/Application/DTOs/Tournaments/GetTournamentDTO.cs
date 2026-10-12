using Mercurius.Modules.Tournament.Application.DTOs.Matches;
using Mercurius.Modules.Tournament.Application.DTOs.Placements;
using Mercurius.Modules.Tournament.Application.DTOs.Registrations;
using Mercurius.Modules.Tournament.Application.DTOs.Participants;
using Mercurius.Modules.Tournament.Contracts;

namespace Mercurius.Modules.Tournament.Application.DTOs.Tournaments;

internal class GetTournamentDTO
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public DateTime PlannedStartTime { get; set; }
    public int AverageGameDurationMinutes { get; set; }
    public int RoundBreakDurationMinutes { get; set; }
    public DateTime? EstimatedEndTime { get; set; }
    public TournamentStatus Status { get; set; }
    public BracketType BracketType { get; set; }
    public LeaderboardRankingMetric? LeaderboardRankingMetric { get; set; }
    public GameFormat Format { get; set; }
    public GameFormat FinalsFormat { get; set; }
    public ParticipationMode ParticipationMode { get; set; }
    public int? TeamSize { get; set; }
    public string? ImageUrl { get; set; }
    public string? FirstPlacePrize { get; set; }
    public string? SecondPlacePrize { get; set; }
    public string? ThirdPlacePrize { get; set; }
    public PublicUserDTO? ContactAdmin { get; set; }

    public IEnumerable<GetPlacementDTO> Placements { get; set; } = [];
    public GetTournamentSponsorPlacementDTO? SponsorPlacement { get; set; }

    public IEnumerable<GetMatchDTO> Matches { get; set; } = [];
    public IEnumerable<PublicTournamentRegistrationDTO> Registrations { get; set; } = [];

    public GetTournamentDTO()
    {
    }
}

