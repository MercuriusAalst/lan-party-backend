using Microsoft.AspNetCore.Http;
using Mercurius.Modules.Teams.Application.DTOs;

namespace Mercurius.Modules.Teams.Application.Services;

internal interface ITeamEndpointService
{
    Task<IReadOnlyList<TeamResponseDTO>> GetAllTeamsAsync(int page, int pageSize, CancellationToken cancellationToken = default);
    Task<TeamResponseDTO> GetTeamByIdAsync(Guid teamId, CancellationToken cancellationToken = default);
    Task<TeamManagementSummaryResponseDTO> CreateCurrentUserTeamAsync(string auth0UserId, CreateTeamRequestDTO request, CancellationToken cancellationToken = default);
    Task<CurrentUserTeamSummaryResponseDTO> GetCurrentUserTeamSummaryAsync(string auth0UserId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TeamInviteSummaryResponseDTO>> GetCurrentUserInvitesAsync(string auth0UserId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TeamInviteSummaryResponseDTO>> GetCurrentUserSentInvitesAsync(string auth0UserId, CancellationToken cancellationToken = default);
    Task<TeamManagementSummaryResponseDTO> LeaveTeamAsync(string auth0UserId, Guid teamId, CancellationToken cancellationToken = default);
    Task<TeamManagementSummaryResponseDTO> RemoveMemberAsync(string auth0UserId, Guid teamId, Guid userId, CancellationToken cancellationToken = default);
    Task DeleteTeamAsync(string auth0UserId, Guid teamId, CancellationToken cancellationToken = default);
    Task<TeamInviteResponseDTO> InviteUserAsync(string auth0UserId, Guid teamId, Guid userId, CancellationToken cancellationToken = default);
    Task<TeamInviteResponseDTO> CancelInviteAsync(string auth0UserId, Guid teamId, Guid inviteId, CancellationToken cancellationToken = default);
    Task<TeamInviteResponseDTO> RespondToInviteAsync(string auth0UserId, Guid inviteId, bool accept, CancellationToken cancellationToken = default);
    Task<TeamManagementSummaryResponseDTO> TransferCaptainAsync(string auth0UserId, Guid teamId, Guid newCaptainUserId, CancellationToken cancellationToken = default);
    Task<TeamLogoResponseDTO> UploadTeamLogoAsync(string auth0UserId, Guid teamId, IFormFile logo, CancellationToken cancellationToken = default);
    Task<TeamLogoResponseDTO> RemoveTeamLogoAsync(string auth0UserId, Guid teamId, CancellationToken cancellationToken = default);
    Task<PublicTeamProfileResponseDTO> GetPublicTeamProfileAsync(string teamName, CancellationToken cancellationToken = default);
}
