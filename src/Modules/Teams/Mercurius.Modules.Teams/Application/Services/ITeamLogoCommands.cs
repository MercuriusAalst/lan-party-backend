using Mercurius.Modules.Teams.Application.DTOs;
using Microsoft.AspNetCore.Http;

namespace Mercurius.Modules.Teams.Application.Services;

internal interface ITeamLogoCommands
{
    Task<TeamLogoResponseDTO> UploadTeamLogoAsync(
        string auth0UserId,
        Guid teamId,
        IFormFile logo,
        CancellationToken cancellationToken = default);

    Task<TeamLogoResponseDTO> RemoveTeamLogoAsync(
        string auth0UserId,
        Guid teamId,
        CancellationToken cancellationToken = default);
}
