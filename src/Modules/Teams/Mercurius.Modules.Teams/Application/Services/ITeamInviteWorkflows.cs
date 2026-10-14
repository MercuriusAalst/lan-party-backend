using Mercurius.Modules.Teams.Application.DTOs;

namespace Mercurius.Modules.Teams.Application.Services;

internal interface ITeamInviteWorkflows
{
    Task<CurrentUserTeamSummaryDTO> GetCurrentUserTeamSummaryAsync(string auth0UserId, CancellationToken cancellationToken = default);
    Task<IEnumerable<TeamInviteSummaryDTO>> GetCurrentUserInvitesAsync(string auth0UserId, CancellationToken cancellationToken = default);
    Task<IEnumerable<TeamInviteSummaryDTO>> GetCurrentUserSentInvitesAsync(string auth0UserId, CancellationToken cancellationToken = default);
    Task<TeamInviteDTO> InviteUserAsync(string auth0UserId, Guid teamId, Guid userId, CancellationToken cancellationToken = default);
    Task<TeamInviteDTO> CancelInviteAsync(string auth0UserId, Guid teamId, Guid inviteId, CancellationToken cancellationToken = default);
    Task<TeamInviteDTO> RespondToInviteAsync(string auth0UserId, Guid inviteId, bool accept, CancellationToken cancellationToken = default);
}
