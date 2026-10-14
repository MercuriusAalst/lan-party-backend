using Mercurius.Modules.Discovery.Domain;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;

namespace Mercurius.Modules.Discovery.Infrastructure;

internal interface IDiscoveryDbContext
{
    DbSet<SearchDocument> SearchDocuments { get; }
    DbSet<SearchIndexRebuildJob> SearchIndexRebuildJobs { get; }
    DbSet<SearchIndexRebuildDocument> SearchIndexRebuildDocuments { get; }

    bool RetriesOnFailure { get; }
    DbConnection Connection { get; }
    DbTransaction? CurrentTransaction { get; }

    Task OpenConnectionAsync(CancellationToken cancellationToken = default);

    Task CloseConnectionAsync();

    void DiscardConnection();

    EntityEntry Entry(object entity);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);

    Task<int> ExecuteSqlInterpolatedAsync(FormattableString sql, CancellationToken cancellationToken = default);
}
