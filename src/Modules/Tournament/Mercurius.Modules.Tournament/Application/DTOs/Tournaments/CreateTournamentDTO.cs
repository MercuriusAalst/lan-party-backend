using Mercurius.Modules.Tournament.Contracts;
using System.ComponentModel.DataAnnotations;

namespace Mercurius.Modules.Tournament.Application.DTOs.Tournaments;

internal class CreateTournamentDTO
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string Name { get; set; } = null!;
    public BracketType BracketType { get; set; }
    public LeaderboardRankingMetric? LeaderboardRankingMetric { get; set; }
    public GameFormat Format { get; set; }
    public GameFormat FinalsFormat { get; set; }
    [Required]
    public ParticipationMode? ParticipationMode { get; set; }
    [Required]
    public IFormFile Image { get; set; } = null!;
    public int? TeamSize { get; set; }
    public string? AssignedAdminUserId { get; set; }
    public string? FirstPlacePrize { get; set; }
    public string? SecondPlacePrize { get; set; }
    public string? ThirdPlacePrize { get; set; }
    [Required]
    public DateTime PlannedStartTime { get; set; }
    public int AverageGameDurationMinutes { get; set; }
    public int RoundBreakDurationMinutes { get; set; }
}

