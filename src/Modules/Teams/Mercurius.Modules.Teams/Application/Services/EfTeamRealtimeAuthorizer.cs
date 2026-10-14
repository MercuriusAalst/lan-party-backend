using Mercurius.Modules.Shared;
using Mercurius.Modules.Teams.Contracts;
using Mercurius.Modules.Teams.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mercurius.Modules.Teams.Application.Services;

internal sealed class EfTeamRealtimeAuthorizer : ITeamRealtimeAuthorizer
{
    private readonly ITeamsDbContext _dbContext;

    public EfTeamRealtimeAuthorizer(ITeamsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<bool> CanSubscribeToTeamAsync(
        TeamId teamId,
        UserId userId,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.Teams
            .AsNoTracking()
            .AnyAsync(team =>
                !team.IsDeleted &&
                team.Id == teamId.Value &&
                (team.CaptainUserId == userId.Value || team.Members.Any(member => member.UserId == userId.Value)),
                cancellationToken);
    }
}
