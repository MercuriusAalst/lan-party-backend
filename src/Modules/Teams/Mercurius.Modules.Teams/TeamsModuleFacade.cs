using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Teams.Contracts;
using Mercurius.Modules.Teams.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mercurius.Modules.Teams;

internal sealed class TeamsModuleFacade : ITeamsModule
{
    private readonly ITeamsDbContext _dbContext;
    private readonly IIdentityModule _identityModule;
    private readonly ITeamTournamentReadService _tournamentReadService;

    public TeamsModuleFacade(
        ITeamsDbContext dbContext,
        IIdentityModule identityModule,
        ITeamTournamentReadService tournamentReadService)
    {
        _dbContext = dbContext;
        _identityModule = identityModule;
        _tournamentReadService = tournamentReadService;
    }

    public async Task<TeamSummary?> GetTeamSummaryAsync(
        TeamId teamId,
        CancellationToken cancellationToken = default)
    {
        var team = await _dbContext.Teams
            .AsNoTracking()
            .Where(team => team.Id == teamId.Value)
            .Select(team => new TeamSummary(
                new TeamId(team.Id),
                team.Name,
                team.CaptainUserId.HasValue ? new UserId(team.CaptainUserId.Value) : null,
                team.LogoUrl,
                team.IsDeleted))
            .SingleOrDefaultAsync(cancellationToken);
        if (team?.CaptainUserId is not { } captainUserId || await IsActiveUserAsync(captainUserId, cancellationToken))
            return team;

        return team with { CaptainUserId = null };
    }

    public async Task<IReadOnlyList<TeamId>> GetCaptainedTeamIdsAsync(
        UserId userId,
        CancellationToken cancellationToken = default)
    {
        if (!await IsActiveUserAsync(userId, cancellationToken))
            return [];

        return await _dbContext.Teams
            .AsNoTracking()
            .Where(team => !team.IsDeleted && team.CaptainUserId == userId.Value)
            .OrderBy(team => team.Id)
            .Select(team => new TeamId(team.Id))
            .ToListAsync(cancellationToken);
    }

    public async Task<TeamRosterSnapshot?> GetTeamRosterSnapshotAsync(
        TeamId teamId,
        CancellationToken cancellationToken = default)
    {
        var teams = await GetTeamRosterSnapshotsAsync([teamId], cancellationToken);
        return teams.GetValueOrDefault(teamId);
    }

    public async Task<IReadOnlyDictionary<TeamId, TeamRosterSnapshot>> GetTeamRosterSnapshotsAsync(
        IReadOnlyCollection<TeamId> teamIds,
        CancellationToken cancellationToken = default)
    {
        if (teamIds.Count == 0)
            return new Dictionary<TeamId, TeamRosterSnapshot>();

        var ids = teamIds.Select(teamId => teamId.Value).Distinct().ToArray();
        var teams = await _dbContext.Teams
            .AsNoTracking()
            .Where(team => ids.Contains(team.Id))
            .Select(team => new
            {
                team.Id,
                team.Name,
                team.CaptainUserId,
                team.LogoUrl,
                team.IsDeleted,
                MemberUserIds = team.Members
                    .Select(member => member.UserId)
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        var users = await GetUserProfilesAsync(
            teams.SelectMany(team => team.CaptainUserId.HasValue
                ? team.MemberUserIds.Append(team.CaptainUserId.Value)
                : team.MemberUserIds),
            cancellationToken);

        return teams.ToDictionary(
            team => new TeamId(team.Id),
            team => new TeamRosterSnapshot(
                new TeamId(team.Id),
                team.Name,
                GetActiveCaptainUserId(team.CaptainUserId, users),
                team.LogoUrl,
                team.IsDeleted,
                team.MemberUserIds
                    .Select(userId => users.GetValueOrDefault(new UserId(userId)))
                    .Where(user => user is not null && !user.IsDeleted)
                    .OrderBy(user => user!.Username, StringComparer.Ordinal)
                    .Select(user => new TeamMemberSnapshot(
                        user!.Id,
                        user.Username,
                        user.DisplayName,
                        team.CaptainUserId == user.Id.Value,
                        user.ProfilePictureUrl))
                    .ToList()));
    }

    public async Task<IReadOnlyDictionary<TeamId, string>> GetPublicTeamNamesByIdsAsync(
        IReadOnlyCollection<TeamId> teamIds,
        CancellationToken cancellationToken = default)
    {
        if (teamIds.Count == 0)
            return new Dictionary<TeamId, string>();

        var ids = teamIds.Select(teamId => teamId.Value).Distinct().ToArray();
        var teams = await _dbContext.Teams
            .AsNoTracking()
            .Where(team =>
                ids.Contains(team.Id) &&
                !team.IsDeleted &&
                !string.IsNullOrWhiteSpace(team.Name))
            .Select(team => new { team.Id, team.Name })
            .ToListAsync(cancellationToken);

        return teams.ToDictionary(team => new TeamId(team.Id), team => team.Name);
    }

    public async Task<PublicTeamProfile?> GetPublicTeamProfileAsync(
        string teamName,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = Domain.Team.NormalizeName(teamName);

        var team = await _dbContext.Teams
            .AsNoTracking()
            .Where(team => team.NormalizedName == normalizedName && !team.IsDeleted)
            .Select(team => new
            {
                team.Id,
                team.Name,
                team.CaptainUserId,
                team.LogoUrl,
                MemberUserIds = team.Members
                    .Select(member => member.UserId)
                    .ToList()
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (team is null)
            return null;

        var users = await GetUserProfilesAsync(
            team.MemberUserIds.Concat(
                team.CaptainUserId.HasValue ? [team.CaptainUserId.Value] : []),
            cancellationToken);
        var tournaments = await _tournamentReadService.GetPublicTeamTournamentsAsync(team.Id, cancellationToken);

        var members = team.MemberUserIds
            .Select(userId => users.GetValueOrDefault(new UserId(userId)))
            .Where(user => user is not null && !user.IsDeleted)
            .Select(user => user!.Username)
            .Where(IsValidPublicUsername)
            .Select(username => username!)
            .OrderBy(username => username, StringComparer.OrdinalIgnoreCase)
            .ThenBy(username => username, StringComparer.Ordinal)
            .Select(username => new PublicTeamMemberSummary(username))
            .ToList();

        var captainUserId = GetActiveCaptainUserId(team.CaptainUserId, users);
        var captainUsername = captainUserId.HasValue
            ? users[captainUserId.Value].Username
            : null;

        return new PublicTeamProfile(
            team.Name,
            IsValidPublicUsername(captainUsername) ? captainUsername : null,
            team.LogoUrl,
            members,
            tournaments);
    }

    public async Task<TeamId?> GetPublicTeamIdByNameAsync(
        string teamName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(teamName))
            return null;

        var normalizedName = Domain.Team.NormalizeName(teamName);
        var teamId = await _dbContext.Teams
            .AsNoTracking()
            .Where(team => team.NormalizedName == normalizedName && !team.IsDeleted)
            .Select(team => (Guid?)team.Id)
            .SingleOrDefaultAsync(cancellationToken);

        return teamId.HasValue ? new TeamId(teamId.Value) : null;
    }

    public async Task<TeamRegistrationEligibility> GetRegistrationEligibilityAsync(
        TeamId teamId,
        UserId requestedBy,
        TournamentId tournamentId,
        CancellationToken cancellationToken = default)
    {
        var team = await _dbContext.Teams
            .AsNoTracking()
            .Where(team => team.Id == teamId.Value)
            .Select(team => new
            {
                team.IsDeleted,
                team.CaptainUserId
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (team is null)
            return new TeamRegistrationEligibility(false, ["team_not_found"]);

        var reasons = new List<string>();
        if (team.IsDeleted)
            reasons.Add("team_deleted");
        if (team.CaptainUserId != requestedBy.Value || !await IsActiveUserAsync(requestedBy, cancellationToken))
            reasons.Add("captain_required");

        return new TeamRegistrationEligibility(reasons.Count == 0, reasons);
    }

    public async Task<MembershipMutationGuard> CanMutateMembershipAsync(
        TeamId teamId,
        UserId userId,
        CancellationToken cancellationToken = default)
    {
        var team = await _dbContext.Teams
            .AsNoTracking()
            .Where(team => team.Id == teamId.Value)
            .Select(team => new
            {
                team.IsDeleted,
                team.CaptainUserId
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (team is null)
            return new MembershipMutationGuard(false, ["team_not_found"]);

        var reasons = new List<string>();
        if (team.IsDeleted)
            reasons.Add("team_deleted");
        if (team.CaptainUserId != userId.Value || !await IsActiveUserAsync(userId, cancellationToken))
            reasons.Add("captain_required");

        return new MembershipMutationGuard(reasons.Count == 0, reasons);
    }

    public async Task<IReadOnlyList<PublicTeamSearchDocument>> GetPublicTeamSearchDocumentsPageAsync(
        TeamId? afterId,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var afterValue = afterId?.Value;
        return await _dbContext.Teams
            .AsNoTracking()
            .Where(team =>
                !team.IsDeleted &&
                !string.IsNullOrWhiteSpace(team.Name) &&
                (!afterValue.HasValue || team.Id > afterValue.Value))
            .OrderBy(team => team.Id)
            .Select(team => new PublicTeamSearchDocument(
                new TeamId(team.Id),
                team.Name))
            .Take(Math.Clamp(pageSize, 1, 1000))
            .ToListAsync(cancellationToken);
    }

    private static bool IsValidPublicUsername(string? username)
    {
        return !string.IsNullOrWhiteSpace(username);
    }

    private async Task<bool> IsActiveUserAsync(UserId userId, CancellationToken cancellationToken)
    {
        var user = await _identityModule.GetUserProfileAsync(userId, cancellationToken);
        return user is { IsDeleted: false };
    }

    // Live snapshots never report a deleted account as captain, even before deletion cleanup has run.
    private static UserId? GetActiveCaptainUserId(
        Guid? captainUserId,
        IReadOnlyDictionary<UserId, UserProfileSummary> users)
    {
        if (!captainUserId.HasValue)
            return null;

        var captain = new UserId(captainUserId.Value);
        return users.TryGetValue(captain, out var user) && !user.IsDeleted ? captain : null;
    }

    private async Task<IReadOnlyDictionary<UserId, UserProfileSummary>> GetUserProfilesAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken)
    {
        var ids = userIds
            .Distinct()
            .Select(userId => new UserId(userId))
            .ToArray();

        return await _identityModule.GetUsersByIdsAsync(ids, cancellationToken);
    }
}
