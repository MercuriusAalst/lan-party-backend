using Mercurius.Modules.Shared.Exceptions;
using Mercurius.Modules.Teams.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Mercurius.Modules.Teams.Infrastructure;

internal sealed class TeamsDbContextAdapter<TDbContext> : ITeamsDbContext
    where TDbContext : DbContext
{
    private readonly TDbContext _dbContext;

    public TeamsDbContextAdapter(TDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public DbSet<Team> Teams => _dbContext.Set<Team>();
    public DatabaseFacade Database => _dbContext.Database;

    public DbSet<TEntity> Set<TEntity>()
        where TEntity : class
    {
        return _dbContext.Set<TEntity>();
    }

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("team_changed", "The team changed. Refresh and try again.");
        }
    }
}
