using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Teams.Contracts;
using Mercurius.Modules.Teams.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mercurius.Modules.Teams.Application.Services;

internal sealed class EfTeamRealtimeAuthorizer : ITeamRealtimeAuthorizer
{
    private readonly ITeamsDbContext _dbContext;
    private readonly IIdentityModule _identityModule;

    public EfTeamRealtimeAuthorizer(ITeamsDbContext dbContext, IIdentityModule identityModule)
    {
        _dbContext = dbContext;
        _identityModule = identityModule;
    }

    public async Task<bool> CanSubscribeToTeamAsync(
        TeamId teamId,
        UserId userId,
        CancellationToken cancellationToken = default)
    {
        // A deleted account keeps no team access, even before deletion cleanup removed its membership.
        var user = await _identityModule.GetUserProfileAsync(userId, cancellationToken);
        if (user is not { IsDeleted: false })
            return false;

        return await _dbContext.Teams
            .AsNoTracking()
            .AnyAsync(team =>
                !team.IsDeleted &&
                team.Id == teamId.Value &&
                (team.CaptainUserId == userId.Value || team.Members.Any(member => member.UserId == userId.Value)),
                cancellationToken);
    }
}
