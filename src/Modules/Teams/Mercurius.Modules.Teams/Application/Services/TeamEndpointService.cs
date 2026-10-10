using Mercurius.Modules.Teams.Application.DTOs;
using Microsoft.AspNetCore.Http;

namespace Mercurius.Modules.Teams.Application.Services;

internal sealed class TeamEndpointService : ITeamEndpointService
{
    private readonly ITeamQueries _queries;
    private readonly ITeamManagementCommands _managementCommands;
    private readonly ITeamInviteWorkflows _inviteWorkflows;
    private readonly ITeamLogoCommands _logoCommands;

    public TeamEndpointService(
        ITeamQueries queries,
        ITeamManagementCommands managementCommands,
        ITeamInviteWorkflows inviteWorkflows,
        ITeamLogoCommands logoCommands)
    {
        _queries = queries;
        _managementCommands = managementCommands;
        _inviteWorkflows = inviteWorkflows;
        _logoCommands = logoCommands;
    }

    public async Task<IReadOnlyList<TeamResponseDTO>> GetAllTeamsAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var teams = await _queries.GetAllTeamsAsync(page, pageSize, cancellationToken);
        return teams.Select(MapTeam).ToList();
    }

    public async Task<TeamResponseDTO> GetTeamByIdAsync(Guid teamId, CancellationToken cancellationToken = default)
    {
        return MapTeam(await _queries.GetTeamByIdAsync(teamId, cancellationToken));
    }

    public async Task<TeamManagementSummaryResponseDTO> CreateCurrentUserTeamAsync(
        string auth0UserId,
        CreateTeamRequestDTO request,
        CancellationToken cancellationToken = default)
    {
        var team = await _managementCommands.CreateCurrentUserTeamAsync(
            auth0UserId,
            new CreateTeamDTO
            {
                Name = request.Name,
                CaptainUserId = request.CaptainUserId
            },
            cancellationToken);

        return MapManagementSummary(team);
    }

    public async Task<CurrentUserTeamSummaryResponseDTO> GetCurrentUserTeamSummaryAsync(
        string auth0UserId,
        CancellationToken cancellationToken = default)
    {
        var summary = await _inviteWorkflows.GetCurrentUserTeamSummaryAsync(auth0UserId, cancellationToken);

        return new CurrentUserTeamSummaryResponseDTO
        {
            CaptainedTeams = summary.CaptainedTeams.Select(MapManagementSummary).ToList(),
            MemberTeams = summary.MemberTeams.Select(MapManagementSummary).ToList(),
            ReceivedPendingInvites = summary.ReceivedPendingInvites.Select(MapInviteSummary).ToList(),
            SentPendingInvites = summary.SentPendingInvites.Select(MapInviteSummary).ToList()
        };
    }

    public async Task<IReadOnlyList<TeamInviteSummaryResponseDTO>> GetCurrentUserInvitesAsync(
        string auth0UserId,
        CancellationToken cancellationToken = default)
    {
        var invites = await _inviteWorkflows.GetCurrentUserInvitesAsync(auth0UserId, cancellationToken);
        return invites.Select(MapInviteSummary).ToList();
    }

    public async Task<IReadOnlyList<TeamInviteSummaryResponseDTO>> GetCurrentUserSentInvitesAsync(
        string auth0UserId,
        CancellationToken cancellationToken = default)
    {
        var invites = await _inviteWorkflows.GetCurrentUserSentInvitesAsync(auth0UserId, cancellationToken);
        return invites.Select(MapInviteSummary).ToList();
    }

    public async Task<TeamManagementSummaryResponseDTO> LeaveTeamAsync(
        string auth0UserId,
        Guid teamId,
        CancellationToken cancellationToken = default)
    {
        return MapManagementSummary(await _managementCommands.LeaveTeamAsync(auth0UserId, teamId, cancellationToken));
    }

    public async Task<TeamManagementSummaryResponseDTO> RemoveMemberAsync(
        string auth0UserId,
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return MapManagementSummary(await _managementCommands.RemoveMemberAsync(auth0UserId, teamId, userId, cancellationToken));
    }

    public Task DeleteTeamAsync(string auth0UserId, Guid teamId, CancellationToken cancellationToken = default)
    {
        return _managementCommands.DeleteTeamAsync(auth0UserId, teamId, cancellationToken);
    }

    public async Task<TeamInviteResponseDTO> InviteUserAsync(
        string auth0UserId,
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return MapInvite(await _inviteWorkflows.InviteUserAsync(auth0UserId, teamId, userId, cancellationToken));
    }

    public async Task<TeamInviteResponseDTO> CancelInviteAsync(
        string auth0UserId,
        Guid teamId,
        Guid inviteId,
        CancellationToken cancellationToken = default)
    {
        return MapInvite(await _inviteWorkflows.CancelInviteAsync(auth0UserId, teamId, inviteId, cancellationToken));
    }

    public async Task<TeamInviteResponseDTO> RespondToInviteAsync(
        string auth0UserId,
        Guid inviteId,
        bool accept,
        CancellationToken cancellationToken = default)
    {
        return MapInvite(await _inviteWorkflows.RespondToInviteAsync(auth0UserId, inviteId, accept, cancellationToken));
    }

    public async Task<TeamManagementSummaryResponseDTO> TransferCaptainAsync(
        string auth0UserId,
        Guid teamId,
        Guid newCaptainUserId,
        CancellationToken cancellationToken = default)
    {
        return MapManagementSummary(await _managementCommands.TransferCaptainAsync(auth0UserId, teamId, newCaptainUserId, cancellationToken));
    }

    public async Task<TeamLogoResponseDTO> UploadTeamLogoAsync(
        string auth0UserId,
        Guid teamId,
        IFormFile logo,
        CancellationToken cancellationToken = default)
    {
        var logoResponse = await _logoCommands.UploadTeamLogoAsync(auth0UserId, teamId, logo, cancellationToken);
        return new TeamLogoResponseDTO(logoResponse.TeamId, logoResponse.LogoUrl);
    }

    public async Task<TeamLogoResponseDTO> RemoveTeamLogoAsync(
        string auth0UserId,
        Guid teamId,
        CancellationToken cancellationToken = default)
    {
        var logoResponse = await _logoCommands.RemoveTeamLogoAsync(auth0UserId, teamId, cancellationToken);
        return new TeamLogoResponseDTO(logoResponse.TeamId, logoResponse.LogoUrl);
    }

    public async Task<PublicTeamProfileResponseDTO> GetPublicTeamProfileAsync(
        string teamName,
        CancellationToken cancellationToken = default)
    {
        var profile = await _queries.GetPublicTeamProfileAsync(teamName, cancellationToken);

        return new PublicTeamProfileResponseDTO
        {
            TeamName = profile.TeamName,
            CaptainUsername = profile.CaptainUsername,
            LogoUrl = profile.LogoUrl,
            Members = profile.Members.Select(member => new PublicTeamMemberResponseDTO
            {
                Username = member.Username
            }).ToList(),
            Tournaments = profile.Tournaments.Select(tournament => new PublicTeamTournamentResponseDTO
            {
                TournamentId = tournament.TournamentId,
                Name = tournament.Name
            }).ToList()
        };
    }

    private static TeamResponseDTO MapTeam(GetTeamDTO team)
    {
        return new TeamResponseDTO
        {
            Id = team.Id,
            Name = team.Name,
            CaptainUserId = team.CaptainUserId,
            LogoUrl = team.LogoUrl,
            Members = team.Members.Select(MapPublicTeamUser).ToList()
        };
    }

    private static TeamManagementSummaryResponseDTO MapManagementSummary(TeamManagementSummaryDTO team)
    {
        return new TeamManagementSummaryResponseDTO
        {
            Id = team.Id,
            Name = team.Name,
            CaptainUserId = team.CaptainUserId,
            CaptainUsername = team.CaptainUsername,
            LogoUrl = team.LogoUrl,
            Members = team.Members.Select(MapManagementUser).ToList()
        };
    }

    private static TeamInviteResponseDTO MapInvite(TeamInviteDTO invite)
    {
        return new TeamInviteResponseDTO
        {
            Id = invite.Id,
            TeamId = invite.TeamId,
            UserId = invite.UserId,
            Status = invite.Status,
            CreatedAt = invite.CreatedAt,
            ExpiresAt = invite.ExpiresAt,
            RespondedAt = invite.RespondedAt,
            CancelledAt = invite.CancelledAt,
            ExpiredAt = invite.ExpiredAt
        };
    }

    private static TeamInviteSummaryResponseDTO MapInviteSummary(TeamInviteSummaryDTO invite)
    {
        return new TeamInviteSummaryResponseDTO
        {
            Id = invite.Id,
            TeamId = invite.TeamId,
            TeamName = invite.TeamName,
            TeamLogoUrl = invite.TeamLogoUrl,
            UserId = invite.UserId,
            Username = invite.Username,
            Status = invite.Status,
            CreatedAt = invite.CreatedAt,
            ExpiresAt = invite.ExpiresAt
        };
    }

    private static TeamParticipantResponseDTO MapPublicTeamUser(TeamPublicUserDTO user)
    {
        return new TeamParticipantResponseDTO
        {
            Id = user.Id,
            Username = user.Username,
            DisplayName = user.DisplayName
        };
    }

    private static PublicUserResponseDTO MapManagementUser(TeamPublicUserDTO user)
    {
        return new PublicUserResponseDTO
        {
            Id = user.Id,
            Username = user.Username,
            DisplayName = user.DisplayName,
            DiscordId = user.DiscordId,
            SteamId = user.SteamId,
            RiotId = user.RiotId
        };
    }
}
