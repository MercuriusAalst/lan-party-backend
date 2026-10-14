using Mercurius.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Mercurius.Modules.Identity.Infrastructure;

internal sealed class IdentityDbContextAdapter<TDbContext> : IIdentityDbContext
    where TDbContext : DbContext
{
    private readonly TDbContext _dbContext;

    public IdentityDbContextAdapter(TDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public DbSet<User> Users => _dbContext.Set<User>();
    public DatabaseFacade Database => _dbContext.Database;

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _dbContext.SaveChangesAsync(cancellationToken);
}
