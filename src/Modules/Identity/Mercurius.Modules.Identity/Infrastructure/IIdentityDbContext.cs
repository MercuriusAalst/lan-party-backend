using Mercurius.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Mercurius.Modules.Identity.Infrastructure;

internal interface IIdentityDbContext
{
    DbSet<User> Users { get; }
    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
