using Mercurius.Modules.Tournament.Application.DTOs.Matches;

namespace Mercurius.Modules.Tournament.Application.Services;

internal interface IMatchService
{
    Task<GetMatchDTO> GetMatchByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<OpponentUserProfileDTO> GetOpponentUserProfileAsync(
        Guid id,
        string auth0UserId,
        CancellationToken cancellationToken = default);
    Task<GetMatchActionStateDTO> GetMatchActionStateAsync(
        Guid id,
        string auth0UserId,
        bool isAdmin = false,
        CancellationToken cancellationToken = default);
    Task<GetMatchDTO> ConfirmEndedAsync(Guid id, string auth0UserId, CancellationToken cancellationToken = default);
    Task<GetMatchDTO> SubmitScoreAsync(Guid id, string auth0UserId, SubmitMatchScoreDTO request, CancellationToken cancellationToken = default);
    Task<GetMatchDTO> ForfeitAsync(Guid id, string auth0UserId, ForfeitMatchDTO request, bool isAdmin, CancellationToken cancellationToken = default);
    Task<GetMatchDTO> ResolveAsync(Guid id, string auth0UserId, ResolveMatchDTO request, CancellationToken cancellationToken = default);
    Task<GetMatchDTO> ReverseAsync(Guid id, string auth0UserId, CancellationToken cancellationToken = default);
}
