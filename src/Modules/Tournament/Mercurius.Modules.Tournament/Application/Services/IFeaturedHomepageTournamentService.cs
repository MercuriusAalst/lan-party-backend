using Mercurius.Modules.Tournament.Application.DTOs.Tournaments;

namespace Mercurius.Modules.Tournament.Application.Services;

internal interface IFeaturedHomepageTournamentService
{
    Task<FeaturedHomepageTournamentsDTO> GetFeaturedTournamentsAsync(CancellationToken cancellationToken = default);

    Task<Dictionary<string, string[]>?> ReplaceFeaturedTournamentsAsync(
        Guid[]? tournamentIds,
        CancellationToken cancellationToken = default);
}
