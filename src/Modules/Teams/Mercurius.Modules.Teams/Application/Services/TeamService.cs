using Mercurius.Modules.Teams.DTOs;
using Mercurius.Modules.Shared.Exceptions;
using Mercurius.Modules.Teams.Contracts;
using Mercurius.Modules.Teams.Domain;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Media.Contracts;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Teams.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;

namespace Mercurius.Modules.Teams.Services;

internal sealed class TeamService : ITeamQueries, ITeamManagementCommands, ITeamInviteWorkflows, ITeamLogoCommands
{
    private const int MaxCaptainedTeams = 2;
    private const int MaxTeamSearchResults = 25;
    private readonly ITeamsDbContext _dbContext;
    private readonly IMediaModule _mediaModule;
    private readonly IIdentityModule _identityModule;
    private readonly ITeamTournamentReadService _tournamentReadService;
    private readonly ILogger<TeamService> _logger;
    private readonly int _inviteResendCooldownDays;
    private readonly int _inviteExpirationDays;
    private readonly int _declinedInviteResendLimit;
    private DbSet<TeamInvite> TeamInvites => _dbContext.Set<TeamInvite>();

    public TeamService(
        ITeamsDbContext dbContext,
        IConfiguration configuration,
        IIdentityModule identityModule,
        IMediaModule mediaModule,
        ITeamTournamentReadService tournamentReadService,
        ILogger<TeamService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _mediaModule = mediaModule ?? throw new ArgumentNullException(nameof(mediaModule));
        _identityModule = identityModule ?? throw new ArgumentNullException(nameof(identityModule));
        _tournamentReadService = tournamentReadService ?? throw new ArgumentNullException(nameof(tournamentReadService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _inviteResendCooldownDays = configuration.GetSection("TeamInvite:ResendCooldownDays").Get<int>();
        _inviteExpirationDays = configuration.GetSection("TeamInvite:ExpirationDays").Get<int?>() ?? 14;
        _declinedInviteResendLimit = configuration.GetSection("TeamInvite:DeclinedResendLimit").Get<int?>() ?? 3;
    }

    public async Task<GetTeamDTO> CreateTeamAsync(CreateTeamDTO teamDTO, CancellationToken cancellationToken = default)
    {
        var normalizedTeamName = Team.NormalizeName(teamDTO.Name);
        if (await CheckIfTeamNameExistsAsync(normalizedTeamName, cancellationToken: cancellationToken))
            throw new ValidationException($"Teamname {teamDTO.Name} already in use");
        var captain = await GetUserProfileAsync(teamDTO.CaptainUserId, cancellationToken);
        if (captain is null || captain.IsDeleted)
            throw new NotFoundException("User not found");
        var team = new Team(teamDTO.Name, captain.Id.Value);
        team.AddMember(captain.Id.Value);
        _dbContext.Teams.Add(team);
        await SaveTeamChangesAsync(teamDTO.Name, cancellationToken);
        return MapTeam(team, CreateUserLookup(captain));
    }

    public async Task<TeamManagementSummaryDTO> CreateCurrentUserTeamAsync(string auth0UserId, CreateTeamDTO teamDTO, CancellationToken cancellationToken = default)
    {
        var currentUser = await GetCurrentUserAsync(auth0UserId, cancellationToken);
        await EnsureCaptainLimitAsync(currentUser.Id.Value, cancellationToken: cancellationToken);

        var normalizedTeamName = Team.NormalizeName(teamDTO.Name);
        if (await CheckIfTeamNameExistsAsync(normalizedTeamName, cancellationToken: cancellationToken))
            throw new ValidationException($"Teamname {teamDTO.Name} already in use");

        var team = new Team(teamDTO.Name, currentUser.Id.Value)
        {
            Id = Guid.NewGuid()
        };
        team.AddMember(currentUser.Id.Value);
        _dbContext.Teams.Add(team);
        await SaveTeamChangesAsync(teamDTO.Name, cancellationToken);

        return MapTeamManagementSummary(team, CreateUserLookup(currentUser));
    }

    public async Task DeleteTeamAsync(Guid teamId, CancellationToken cancellationToken = default)
    {
        await DeleteTeamAndGetStateAsync(teamId, cancellationToken);
    }

    internal async Task<TeamDeletionState?> DeleteTeamAndGetStateAsync(Guid teamId, CancellationToken cancellationToken = default)
    {
        var team = await _dbContext.Teams
            .AsSplitQuery()
            .Include(t => t.Members)
            .Include(t => t.TeamInvites)
            .FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
        if (team is null)
            throw new NotFoundException($"{nameof(Team)} not found");
        if (team.IsDeleted)
            return null;

        var now = DateTime.UtcNow;
        var deletionState = GetTeamDeletionState(team, now);
        team.Delete(now);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return deletionState;
    }

    public async Task DeleteTeamAsync(string auth0UserId, Guid teamId, CancellationToken cancellationToken = default)
    {
        await DeleteTeamAndGetStateAsync(auth0UserId, teamId, cancellationToken);
    }

    internal async Task<TeamDeletionState> DeleteTeamAndGetStateAsync(
        string auth0UserId,
        Guid teamId,
        CancellationToken cancellationToken = default)
    {
        var currentUser = await GetCurrentUserAsync(auth0UserId, cancellationToken);
        var team = await GetActiveTeamsQuery()
            .AsSplitQuery()
            .Include(t => t.Members)
            .Include(t => t.TeamInvites)
            .FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
        if (team is null)
            throw new NotFoundException($"{nameof(Team)} not found");

        EnsureCaptain(team, currentUser.Id.Value);
        if (await _tournamentReadService.IsTeamInDeleteBlockingTournamentAsync(teamId, cancellationToken))
            throw new ValidationException("Cannot delete a team that is actively participating in a tournament.");

        var now = DateTime.UtcNow;
        var deletionState = GetTeamDeletionState(team, now);
        team.Delete(now);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return deletionState;
    }

    private static TeamDeletionState GetTeamDeletionState(Team team, DateTime now)
    {
        var memberUserIds = team.Members.Select(member => member.UserId);
        if (team.CaptainUserId is Guid captainUserId)
            memberUserIds = memberUserIds.Append(captainUserId);

        return new TeamDeletionState(
            team.LogoUrl,
            memberUserIds.Distinct().ToArray(),
            team.TeamInvites
                .Where(invite => invite.Status == TeamInviteStatus.Pending && invite.ExpiresAt > now)
                .Select(invite => new DeletedTeamInvite(invite.Id, invite.UserId))
                .ToArray());
    }

    internal sealed record TeamDeletionState(
        string? LogoUrl,
        IReadOnlyList<Guid> MemberUserIds,
        IReadOnlyList<DeletedTeamInvite> PendingInvites);

    internal sealed record DeletedTeamInvite(Guid InviteId, Guid UserId);

    public async Task<IReadOnlyList<GetTeamDTO>> GetAllTeamsAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var offset = ((long)page - 1) * pageSize;
        if (offset > int.MaxValue)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return [];
        }

        var teams = await GetTeamReadRowsQuery()
            .OrderBy(team => team.Name)
            .ThenBy(team => team.Id)
            .Skip((int)offset)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        var users = await GetUserProfilesAsync(
            teams.SelectMany(team => team.MemberUserIds),
            cancellationToken);

        return teams.Select(team => MapTeam(team, users)).ToList();
    }

    public async Task<GetTeamDTO> GetTeamByIdAsync(Guid teamId, CancellationToken cancellationToken = default)
    {
        var team = await ProjectTeamReadRows(GetActiveTeamsQuery().Where(t => t.Id == teamId))
            .FirstOrDefaultAsync(cancellationToken);
        if (team is null)
            throw new NotFoundException($"{nameof(Team)} not found");
        var users = await GetUserProfilesAsync(team.MemberUserIds, cancellationToken);
        return MapTeam(team, users);
    }

    public async Task<PublicTeamProfileDTO> GetPublicTeamProfileAsync(string teamName, CancellationToken cancellationToken = default)
    {
        var normalizedTeamName = Team.NormalizeName(teamName);
        var team = await _dbContext.Teams
            .AsNoTracking()
            .Where(t => !t.IsDeleted)
            .Where(t => t.NormalizedName == normalizedTeamName)
            .Select(t => new
            {
                t.Id,
                t.Name,
                t.CaptainUserId,
                t.LogoUrl,
                MemberUserIds = t.Members
                    .Select(member => member.UserId)
                    .ToList()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (team is null)
            throw new NotFoundException($"{nameof(Team)} not found");

        var users = await GetUserProfilesAsync(
            team.MemberUserIds.Append(team.CaptainUserId ?? Guid.Empty).Where(userId => userId != Guid.Empty),
            cancellationToken);
        var tournaments = await _tournamentReadService.GetPublicTeamTournamentsAsync(team.Id, cancellationToken);

        var members = team.MemberUserIds
            .Select(userId => users.GetValueOrDefault(new UserId(userId))?.Username)
            .Where(IsValidPublicUsername)
            .Select(username => username!)
            .OrderBy(username => username, StringComparer.OrdinalIgnoreCase)
            .ThenBy(username => username, StringComparer.Ordinal)
            .Select(username => new PublicTeamMemberDTO(username))
            .ToList();

        var captainUsername = team.CaptainUserId.HasValue
            ? users.GetValueOrDefault(new UserId(team.CaptainUserId.Value))?.Username
            : null;

        return new PublicTeamProfileDTO
        {
            TeamName = team.Name,
            CaptainUsername = IsValidPublicUsername(captainUsername) ? captainUsername : null,
            LogoUrl = team.LogoUrl,
            Members = members,
            Tournaments = tournaments
                .Select(tournament => new PublicTeamTournamentDTO
                {
                    TournamentId = tournament.TournamentId.Value,
                    Name = tournament.Name
                })
                .ToList()
        };
    }

    public async Task<GetTeamDTO> GetTeamByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var normalizedName = Team.NormalizeName(name);
        var team = await ProjectTeamReadRows(GetActiveTeamsQuery().Where(t => t.NormalizedName == normalizedName))
            .FirstOrDefaultAsync(cancellationToken);
        if (team is null)
            throw new NotFoundException($"{nameof(Team)} not found");
        var users = await GetUserProfilesAsync(team.MemberUserIds, cancellationToken);
        return MapTeam(team, users);
    }

    public async Task<GetTeamDTO> RemoveMemberAsync(Guid id, Guid userId, CancellationToken cancellationToken = default)
    {
        var team = await _dbContext.Teams.Include(t => t.Members).FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (team is null)
            throw new NotFoundException($"{nameof(Team)} not found");
        team.RemoveMember(userId);
        await _dbContext.SaveChangesAsync(cancellationToken);
        var users = await GetUserProfilesAsync(
            team.Members.Select(member => member.UserId),
            cancellationToken);
        return MapTeam(team, users);
    }

    public async Task<TeamManagementSummaryDTO> RemoveMemberAsync(string auth0UserId, Guid teamId, Guid userId, CancellationToken cancellationToken = default)
    {
        var currentUser = await GetCurrentUserAsync(auth0UserId, cancellationToken);
        var team = await GetTeamWithMembersQuery().FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
        if (team is null)
            throw new NotFoundException($"{nameof(Team)} not found");

        EnsureCaptain(team, currentUser.Id.Value);
        if (await _tournamentReadService.IsUserInProtectedTournamentRosterAsync(teamId, userId, cancellationToken))
            throw new ValidationException("Cannot remove a member from a team that is part of an in-progress tournament roster.");

        team.RemoveMember(userId);
        await _dbContext.SaveChangesAsync(cancellationToken);
        var users = await GetUserProfilesAsync(
            team.Members.Select(member => member.UserId).Append(team.CaptainUserId ?? Guid.Empty),
            cancellationToken);
        return MapTeamManagementSummary(team, users);
    }

    public async Task<GetTeamDTO> UpdateTeamAsync(Guid id, UpdateTeamDTO teamDTO, CancellationToken cancellationToken = default)
    {
        var team = await GetTeamWithMembersQuery()
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (team is null)
            throw new NotFoundException($"{nameof(Team)} not found");

        if (teamDTO.Name != null)
        {
            var normalizedTeamName = Team.NormalizeName(teamDTO.Name);
            var nameChanged = !string.Equals(team.NormalizedName, normalizedTeamName, StringComparison.Ordinal);
            if (nameChanged &&
                await CheckIfTeamNameExistsAsync(normalizedTeamName, id, cancellationToken))
            {
                throw new ValidationException($"Teamname {teamDTO.Name} already in use");
            }

            team.UpdateName(teamDTO.Name);
        }

        if (teamDTO.CaptainUserId.HasValue && teamDTO.CaptainUserId.Value != team.CaptainUserId)
        {
            team.ChangeCaptain(teamDTO.CaptainUserId.Value);
        }

        _dbContext.Teams.Update(team);
        await SaveTeamChangesAsync(team.Name, cancellationToken);
        var users = await GetUserProfilesAsync(
            team.Members.Select(member => member.UserId),
            cancellationToken);
        return MapTeam(team, users);
    }

    public async Task<IEnumerable<GetTeamDTO>> SearchTeamsByNameAsync(string query, int? limit = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var normalizedQuery = query.Trim().ToLowerInvariant();
        var resultLimit = Math.Clamp(limit.GetValueOrDefault(MaxTeamSearchResults), 1, MaxTeamSearchResults);

        var teams = await ProjectTeamReadRows(GetActiveTeamsQuery().Where(t => t.NormalizedName.StartsWith(normalizedQuery)))
            .OrderBy(t => t.Name)
            .Take(resultLimit)
            .ToListAsync(cancellationToken);
        var users = await GetUserProfilesAsync(
            teams.SelectMany(team => team.MemberUserIds),
            cancellationToken);

        return teams.Select(team => MapTeam(team, users)).ToList();
    }

    public async Task<TeamInviteDTO> InviteUserAsync(Guid teamId, Guid userId, CancellationToken cancellationToken = default)
    {
        var team = await GetTeamForInviteMutationAsync(teamId, cancellationToken);
        if (await GetUserProfileAsync(userId, cancellationToken) is null)
            throw new NotFoundException("User not found");
        await ExpirePendingInviteAsync(teamId, userId, cancellationToken);
        var invite = team.InviteUser(userId, _inviteResendCooldownDays, _inviteExpirationDays, _declinedInviteResendLimit);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return new TeamInviteDTO(invite);
    }

    public async Task<TeamInviteDTO> InviteUserAsync(string auth0UserId, Guid teamId, Guid userId, CancellationToken cancellationToken = default)
    {
        var currentUser = await GetCurrentUserAsync(auth0UserId, cancellationToken);
        var team = await GetTeamForInviteMutationAsync(teamId, cancellationToken);
        EnsureCaptain(team, currentUser.Id.Value);

        var invitedUser = await GetUserProfileAsync(userId, cancellationToken);
        if (invitedUser is null || invitedUser.IsDeleted)
            throw new NotFoundException("User not found");

        await ExpirePendingInviteAsync(teamId, userId, cancellationToken);
        var invite = team.InviteUser(userId, _inviteResendCooldownDays, _inviteExpirationDays, _declinedInviteResendLimit);
        await SaveInviteChangesAsync(cancellationToken);
        return new TeamInviteDTO(invite);
    }

    public async Task<TeamInviteDTO> CancelInviteAsync(string auth0UserId, Guid teamId, Guid inviteId, CancellationToken cancellationToken = default)
    {
        var currentUser = await GetCurrentUserAsync(auth0UserId, cancellationToken);
        var invite = await TeamInvites
            .Include(i => i.Team)
            .FirstOrDefaultAsync(i => i.Id == inviteId && i.TeamId == teamId, cancellationToken);
        if (invite is null)
            throw new NotFoundException("Invite not found");
        if (invite.Team.IsDeleted)
            throw new NotFoundException("Invite not found");

        EnsureCaptain(invite.Team, currentUser.Id.Value);
        invite.Cancel();
        await _dbContext.SaveChangesAsync(cancellationToken);
        return new TeamInviteDTO(invite);
    }

    public async Task<TeamInviteDTO> RespondToInviteAsync(Guid teamId, Guid userId, bool accept, CancellationToken cancellationToken = default)
    {
        var invite = await TeamInvites
            .Include(invite => invite.Team)
                .ThenInclude(team => team.Members)
            .FirstOrDefaultAsync(i => i.TeamId == teamId && i.UserId == userId && i.Status == TeamInviteStatus.Pending, cancellationToken);
        if (invite == null)
            throw new NotFoundException("No pending invite found");
        if (invite.Team.IsDeleted)
            throw new NotFoundException("No pending invite found");
        invite.Respond(accept);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return new TeamInviteDTO(invite);
    }

    public async Task<TeamInviteDTO> RespondToInviteAsync(string auth0UserId, Guid inviteId, bool accept, CancellationToken cancellationToken = default)
    {
        var currentUser = await GetCurrentUserAsync(auth0UserId, cancellationToken);
        var invite = await TeamInvites
            .Include(teamInvite => teamInvite.Team)
                .ThenInclude(team => team.Members)
            .FirstOrDefaultAsync(i => i.Id == inviteId && i.UserId == currentUser.Id.Value, cancellationToken);
        if (invite == null)
            throw new NotFoundException("No pending invite found");
        if (invite.Team.IsDeleted)
            throw new NotFoundException("No pending invite found");

        invite.Respond(accept);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new TeamInviteDTO(invite);
    }

    public async Task<IEnumerable<TeamInviteDTO>> GetUserInvitesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var invites = await TeamInvites
            .AsNoTracking()
            .Where(i => i.UserId == userId && i.Status == TeamInviteStatus.Pending && i.ExpiresAt > now)
            .Select(invite => new TeamInviteDTO
            {
                Id = invite.Id,
                TeamId = invite.TeamId,
                UserId = invite.UserId,
                Status = nameof(TeamInviteStatus.Pending),
                CreatedAt = invite.CreatedAt,
                ExpiresAt = invite.ExpiresAt
            })
            .ToListAsync(cancellationToken);
        return invites;
    }

    public async Task<CurrentUserTeamSummaryDTO> GetCurrentUserTeamSummaryAsync(string auth0UserId, CancellationToken cancellationToken = default)
    {
        var currentUser = await GetCurrentUserAsync(auth0UserId, cancellationToken);
        var now = DateTime.UtcNow;

        var teams = await ProjectTeamReadRows(GetActiveTeamsQuery()
                .Where(team =>
                    team.CaptainUserId == currentUser.Id.Value ||
                    team.Members.Any(member => member.UserId == currentUser.Id.Value)))
            .OrderBy(team => team.Name)
            .ToListAsync(cancellationToken);

        var invites = await GetPendingInviteSummariesQuery(now)
            .Where(invite =>
                invite.UserId == currentUser.Id.Value ||
                invite.CaptainUserId == currentUser.Id.Value)
            .OrderBy(invite => invite.CreatedAt)
            .ToListAsync(cancellationToken);

        var users = await GetUserProfilesAsync(
            teams.SelectMany(team => team.MemberUserIds)
                .Concat(teams.Where(team => team.CaptainUserId.HasValue).Select(team => team.CaptainUserId!.Value))
                .Concat(invites.Select(invite => invite.UserId)),
            cancellationToken);

        return new CurrentUserTeamSummaryDTO
        {
            CaptainedTeams = teams
                .Where(team => team.CaptainUserId == currentUser.Id.Value)
                .Select(team => MapTeamManagementSummary(team, users))
                .ToList(),
            MemberTeams = teams
                .Where(team => team.MemberUserIds.Contains(currentUser.Id.Value))
                .Select(team => MapTeamManagementSummary(team, users))
                .ToList(),
            ReceivedPendingInvites = invites
                .Where(invite => invite.UserId == currentUser.Id.Value)
                .Select(invite => MapInviteSummary(invite, users))
                .ToList(),
            SentPendingInvites = invites
                .Where(invite => invite.CaptainUserId == currentUser.Id.Value)
                .Select(invite => MapInviteSummary(invite, users))
                .ToList()
        };
    }

    public async Task<IEnumerable<TeamInviteSummaryDTO>> GetCurrentUserInvitesAsync(string auth0UserId, CancellationToken cancellationToken = default)
    {
        var currentUser = await GetCurrentUserAsync(auth0UserId, cancellationToken);
        var invites = await GetPendingInviteSummariesQuery(DateTime.UtcNow)
            .Where(invite => invite.UserId == currentUser.Id.Value)
            .OrderBy(invite => invite.CreatedAt)
            .ToListAsync(cancellationToken);
        var users = await GetUserProfilesAsync(invites.Select(invite => invite.UserId), cancellationToken);
        return invites.Select(invite => MapInviteSummary(invite, users)).ToList();
    }

    public async Task<IEnumerable<TeamInviteSummaryDTO>> GetCurrentUserSentInvitesAsync(string auth0UserId, CancellationToken cancellationToken = default)
    {
        var currentUser = await GetCurrentUserAsync(auth0UserId, cancellationToken);
        var invites = await GetPendingInviteSummariesQuery(DateTime.UtcNow)
            .Where(invite => invite.CaptainUserId == currentUser.Id.Value)
            .OrderBy(invite => invite.CreatedAt)
            .ToListAsync(cancellationToken);
        var users = await GetUserProfilesAsync(invites.Select(invite => invite.UserId), cancellationToken);
        return invites.Select(invite => MapInviteSummary(invite, users)).ToList();
    }

    public async Task<TeamManagementSummaryDTO> LeaveTeamAsync(string auth0UserId, Guid teamId, CancellationToken cancellationToken = default)
    {
        var currentUser = await GetCurrentUserAsync(auth0UserId, cancellationToken);
        var team = await GetTeamWithMembersQuery().FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
        if (team is null)
            throw new NotFoundException($"{nameof(Team)} not found");
        if (team.CaptainUserId == currentUser.Id.Value)
            throw new ValidationException("The captain cannot leave a team without transferring captainship.");
        if (!team.Members.Any(member => member.UserId == currentUser.Id.Value))
            throw new NotFoundException($"User not found in {team.Name}");
        if (await _tournamentReadService.IsUserInProtectedTournamentRosterAsync(teamId, currentUser.Id.Value, cancellationToken))
            throw new ValidationException("Cannot leave a team that is part of a protected tournament roster.");

        team.RemoveMember(currentUser.Id.Value);
        await _dbContext.SaveChangesAsync(cancellationToken);
        var users = await GetUserProfilesAsync(
            team.Members.Select(member => member.UserId).Append(team.CaptainUserId ?? Guid.Empty),
            cancellationToken);
        return MapTeamManagementSummary(team, users);
    }

    public async Task<TeamManagementSummaryDTO> TransferCaptainAsync(string auth0UserId, Guid teamId, Guid newCaptainUserId, CancellationToken cancellationToken = default)
    {
        var currentUser = await GetCurrentUserAsync(auth0UserId, cancellationToken);
        var team = await GetTeamWithMembersQuery()
            .FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
        if (team is null)
            throw new NotFoundException($"{nameof(Team)} not found");

        EnsureCaptain(team, currentUser.Id.Value);
        await EnsureCaptainLimitAsync(newCaptainUserId, teamId, cancellationToken);
        team.ChangeCaptain(newCaptainUserId);
        await _dbContext.SaveChangesAsync(cancellationToken);
        var users = await GetUserProfilesAsync(
            team.Members.Select(member => member.UserId).Append(newCaptainUserId),
            cancellationToken);
        return MapTeamManagementSummary(team, users);
    }

    public async Task<TeamLogoResponseDTO> UploadTeamLogoAsync(string auth0UserId, Guid teamId, IFormFile logo, CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginTeamMutationTransactionAsync(teamId, cancellationToken);
        var currentUser = await GetCurrentUserAsync(auth0UserId, cancellationToken);
        var team = await GetTeamWithMembersQuery().FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
        if (team is null)
            throw new NotFoundException($"{nameof(Team)} not found");

        EnsureCaptain(team, currentUser.Id.Value);
        var previousLogo = team.LogoUrl;
        await using var imageStream = logo.OpenReadStream();
        var asset = await _mediaModule.SaveImageAsync(
            new MediaUpload(imageStream, logo.FileName, logo.ContentType, logo.Length),
            cancellationToken);
        var committed = false;
        try
        {
            team.LogoUrl = asset.Url;
            cancellationToken.ThrowIfCancellationRequested();
            await _dbContext.SaveChangesAsync(cancellationToken);
            // Once SaveChanges succeeds, the new asset may be referenced even if CommitAsync fails ambiguously.
            committed = true;
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None);
            if (!committed && !string.Equals(asset.Url, previousLogo, StringComparison.Ordinal))
                await DeleteImageBestEffortAsync(asset.Url, "compensate an uncommitted Team logo replacement");
            throw;
        }

        await RetireLogoIfUnreferencedAsync(previousLogo, team.LogoUrl);
        return new TeamLogoResponseDTO(team.Id, team.LogoUrl);
    }

    public async Task<TeamLogoResponseDTO> RemoveTeamLogoAsync(string auth0UserId, Guid teamId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginTeamMutationTransactionAsync(teamId, cancellationToken);
        var currentUser = await GetCurrentUserAsync(auth0UserId, cancellationToken);
        var team = await GetTeamWithMembersQuery().FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
        if (team is null)
            throw new NotFoundException($"{nameof(Team)} not found");

        EnsureCaptain(team, currentUser.Id.Value);
        var previousLogo = team.LogoUrl;
        team.LogoUrl = null;
        cancellationToken.ThrowIfCancellationRequested();
        await _dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        await RetireLogoIfUnreferencedAsync(previousLogo, null);
        return new TeamLogoResponseDTO(team.Id, null);
    }

    private async Task<IDbContextTransaction?> BeginTeamMutationTransactionAsync(
        Guid teamId,
        CancellationToken cancellationToken)
    {
        if (!_dbContext.Database.IsRelational())
            return null;

        var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await TeamMutationLock.AcquireAsync(_dbContext, teamId, cancellationToken);
            return transaction;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            await transaction.DisposeAsync();
            throw;
        }
    }

    internal async Task RetireLogoIfUnreferencedAsync(string? logoUrl, string? currentLogoUrl)
    {
        if (string.IsNullOrWhiteSpace(logoUrl) ||
            string.Equals(logoUrl, currentLogoUrl, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var hasCurrentReference = await _dbContext.Teams
                .AsNoTracking()
                .AnyAsync(
                    team => !team.IsDeleted && team.LogoUrl == logoUrl,
                    CancellationToken.None);
            if (hasCurrentReference ||
                await _tournamentReadService.IsTeamLogoReferencedAsync(logoUrl, CancellationToken.None))
            {
                return;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to verify references for Team logo at {MediaUrl}; the file was retained.",
                logoUrl);
            return;
        }

        await DeleteImageBestEffortAsync(logoUrl, "retire an unreferenced Team logo");
    }

    private async Task DeleteImageBestEffortAsync(string? imageUrl, string action)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
            return;

        try
        {
            await _mediaModule.DeleteImageAsync(imageUrl, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to {MediaCleanupAction} at {MediaUrl}.", action, imageUrl);
        }
    }

    private static bool IsValidPublicUsername(string? username)
    {
        return !string.IsNullOrWhiteSpace(username);
    }

    private async Task<UserProfileSummary> GetCurrentUserAsync(string auth0UserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(auth0UserId))
            throw new UnauthorizedAccessException("Authenticated user id is missing.");

        var user = await _identityModule.GetUserProfileByAuth0IdAsync(auth0UserId, cancellationToken);

        if (user is null || user.IsDeleted)
            throw new NotFoundException("Current user profile was not found.");

        return user;
    }

    private async Task<UserProfileSummary?> GetUserProfileAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await _identityModule.GetUserProfileAsync(new UserId(userId), cancellationToken);
    }

    private async Task<IReadOnlyDictionary<UserId, UserProfileSummary>> GetUserProfilesAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken)
    {
        var ids = userIds
            .Where(userId => userId != Guid.Empty)
            .Distinct()
            .Select(userId => new UserId(userId))
            .ToArray();

        return await _identityModule.GetUsersByIdsAsync(ids, cancellationToken);
    }

    private static IReadOnlyDictionary<UserId, UserProfileSummary> CreateUserLookup(
        params UserProfileSummary[] users)
    {
        return users.ToDictionary(user => user.Id);
    }

    private async Task EnsureCaptainLimitAsync(Guid captainUserId, Guid? excludedTeamId = null, CancellationToken cancellationToken = default)
    {
        var captainedTeamCount = await _dbContext.Teams.CountAsync(team =>
            !team.IsDeleted &&
            team.CaptainUserId == captainUserId &&
            (!excludedTeamId.HasValue || team.Id != excludedTeamId.Value),
            cancellationToken);
        if (captainedTeamCount >= MaxCaptainedTeams)
            throw new ValidationException($"A user can captain at most {MaxCaptainedTeams} teams.");
    }

    private static void EnsureCaptain(Team team, Guid userId)
    {
        if (team.CaptainUserId != userId)
            throw new ForbiddenException("team_captain_required", "Only the team captain can perform this action.");
    }

    private async Task ExpirePendingInviteAsync(Guid teamId, Guid userId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var invites = await TeamInvites
            .Where(invite =>
                invite.TeamId == teamId &&
                invite.UserId == userId &&
                invite.Status == TeamInviteStatus.Pending &&
                invite.ExpiresAt <= now)
            .ToListAsync(cancellationToken);
        foreach (var invite in invites)
            invite.Expire();

        if (invites.Count > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private Task<bool> CheckIfTeamNameExistsAsync(string normalizedName, Guid? excludedTeamId = null, CancellationToken cancellationToken = default)
    {
        return _dbContext.Teams.AnyAsync(t =>
            !t.IsDeleted &&
            t.NormalizedName == normalizedName &&
            (!excludedTeamId.HasValue || t.Id != excludedTeamId.Value),
            cancellationToken);
    }

    private async Task<Team> GetTeamForInviteMutationAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var team = await GetTeamDetailsQuery()
            .FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
        if (team is null)
            throw new NotFoundException($"{nameof(Team)} not found");

        return team;
    }

    private IQueryable<Team> GetTeamDetailsQuery()
    {
        return GetTeamWithMembersQuery()
            .AsSplitQuery()
            .Include(t => t.TeamInvites);
    }

    private IQueryable<Team> GetTeamWithMembersQuery()
    {
        return GetActiveTeamsQuery()
            .Include(t => t.Members);
    }

    private IQueryable<TeamReadRow> GetTeamReadRowsQuery()
    {
        return ProjectTeamReadRows(GetActiveTeamsQuery());
    }

    private static IQueryable<TeamReadRow> ProjectTeamReadRows(IQueryable<Team> query)
    {
        return query
            .AsNoTracking()
            .Select(team => new TeamReadRow
            {
                Id = team.Id,
                Name = team.Name,
                CaptainUserId = team.CaptainUserId,
                LogoUrl = team.LogoUrl,
                MemberUserIds = team.Members
                    .Select(member => member.UserId)
                    .ToList()
            });
    }

    private static GetTeamDTO MapTeam(
        Team team,
        IReadOnlyDictionary<UserId, UserProfileSummary> users)
    {
        return new GetTeamDTO
        {
            Id = team.Id,
            Name = team.Name,
            CaptainUserId = team.CaptainUserId ?? Guid.Empty,
            LogoUrl = team.LogoUrl,
            Members = MapMembers(team.Members.Select(member => member.UserId), users)
        };
    }

    private static GetTeamDTO MapTeam(
        TeamReadRow team,
        IReadOnlyDictionary<UserId, UserProfileSummary> users)
    {
        return new GetTeamDTO
        {
            Id = team.Id,
            Name = team.Name,
            CaptainUserId = team.CaptainUserId ?? Guid.Empty,
            LogoUrl = team.LogoUrl,
            Members = MapMembers(team.MemberUserIds, users)
        };
    }

    private static TeamManagementSummaryDTO MapTeamManagementSummary(
        Team team,
        IReadOnlyDictionary<UserId, UserProfileSummary> users)
    {
        return new TeamManagementSummaryDTO
        {
            Id = team.Id,
            Name = team.Name,
            CaptainUserId = team.CaptainUserId ?? Guid.Empty,
            CaptainUsername = GetUsername(team.CaptainUserId, users),
            LogoUrl = team.LogoUrl,
            Members = MapMembers(team.Members.Select(member => member.UserId), users)
        };
    }

    private static TeamManagementSummaryDTO MapTeamManagementSummary(
        TeamReadRow team,
        IReadOnlyDictionary<UserId, UserProfileSummary> users)
    {
        return new TeamManagementSummaryDTO
        {
            Id = team.Id,
            Name = team.Name,
            CaptainUserId = team.CaptainUserId ?? Guid.Empty,
            CaptainUsername = GetUsername(team.CaptainUserId, users),
            LogoUrl = team.LogoUrl,
            Members = MapMembers(team.MemberUserIds, users)
        };
    }

    private static List<TeamPublicUserDTO> MapMembers(
        IEnumerable<Guid> memberUserIds,
        IReadOnlyDictionary<UserId, UserProfileSummary> users)
    {
        return memberUserIds
            .Select(userId => users.GetValueOrDefault(new UserId(userId)))
            .Where(user => user is not null)
            .Select(user => new TeamPublicUserDTO(user!))
            .ToList();
    }

    private static string? GetUsername(
        Guid? userId,
        IReadOnlyDictionary<UserId, UserProfileSummary> users)
    {
        return userId.HasValue
            ? users.GetValueOrDefault(new UserId(userId.Value))?.Username
            : null;
    }

    private IQueryable<Team> GetActiveTeamsQuery()
    {
        return _dbContext.Teams.Where(team => !team.IsDeleted);
    }

    private IQueryable<TeamInviteSummaryQueryRow> GetPendingInviteSummariesQuery(DateTime now)
    {
        return TeamInvites
            .AsNoTracking()
            .Where(invite => !invite.Team.IsDeleted && invite.Status == TeamInviteStatus.Pending && invite.ExpiresAt > now)
            .Select(invite => new TeamInviteSummaryQueryRow
            {
                Id = invite.Id,
                TeamId = invite.TeamId,
                TeamName = invite.Team.Name,
                TeamLogoUrl = invite.Team.LogoUrl,
                CaptainUserId = invite.Team.CaptainUserId,
                UserId = invite.UserId,
                CreatedAt = invite.CreatedAt,
                ExpiresAt = invite.ExpiresAt
            });
    }

    private static TeamInviteSummaryDTO MapInviteSummary(
        TeamInviteSummaryQueryRow invite,
        IReadOnlyDictionary<UserId, UserProfileSummary> users)
    {
        return new TeamInviteSummaryDTO
        {
            Id = invite.Id,
            TeamId = invite.TeamId,
            TeamName = invite.TeamName,
            TeamLogoUrl = invite.TeamLogoUrl,
            UserId = invite.UserId,
            Username = GetUsername(invite.UserId, users),
            Status = nameof(TeamInviteStatus.Pending),
            CreatedAt = invite.CreatedAt,
            ExpiresAt = invite.ExpiresAt
        };
    }

    private async Task SaveTeamChangesAsync(string teamName, CancellationToken cancellationToken)
    {
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsTeamNameUniqueConstraintViolation(exception))
        {
            throw new ValidationException($"Teamname {teamName} already in use");
        }
    }

    private static bool IsTeamNameUniqueConstraintViolation(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("IX_Teams_NormalizedName", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private async Task SaveInviteChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsPendingInviteUniqueConstraintViolation(exception))
        {
            throw new ValidationException("User already has a pending invite to this team");
        }
    }

    private static bool IsPendingInviteUniqueConstraintViolation(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("IX_TeamInvites_TeamId_UserId_Pending", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private sealed class TeamReadRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public Guid? CaptainUserId { get; set; }
        public string? LogoUrl { get; set; }
        public List<Guid> MemberUserIds { get; set; } = [];
    }

    private sealed class TeamInviteSummaryQueryRow
    {
        public Guid Id { get; set; }
        public Guid TeamId { get; set; }
        public string TeamName { get; set; } = string.Empty;
        public string? TeamLogoUrl { get; set; }
        public Guid? CaptainUserId { get; set; }
        public Guid UserId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
    }
}

