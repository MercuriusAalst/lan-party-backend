using Mercurius.Modules.Tournament.Contracts;

namespace Mercurius.Modules.Tournament.Application.DTOs.Tournaments;

internal sealed record FeaturedHomepageTournamentCardDTO(
    Guid Id,
    string Name,
    string? ImageUrl,
    TournamentStatus Status,
    BracketType BracketType,
    GameFormat Format)
{
    public GetTournamentSponsorPlacementDTO? SponsorPlacement { get; init; }
}

internal sealed record FeaturedHomepageTournamentsDTO(
    IReadOnlyList<Guid> TournamentIds,
    IReadOnlyList<FeaturedHomepageTournamentCardDTO> Tournaments);

internal sealed record FeaturedHomepageTournamentIdsDTO(Guid[]? TournamentIds);
