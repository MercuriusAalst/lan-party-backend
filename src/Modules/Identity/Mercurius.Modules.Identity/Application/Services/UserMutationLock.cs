using System.Buffers.Binary;
using Mercurius.Modules.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mercurius.Modules.Identity.Application.Services;

internal static class UserMutationLock
{
    public static async Task AcquireAsync(IIdentityDbContext dbContext, Guid userId, CancellationToken cancellationToken = default)
    {
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("User mutation locks require an active database transaction.");
        if (dbContext.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL")
            return;

        var bytes = userId.ToByteArray();
        var key1 = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, 4)) ^ BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4));
        var key2 = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4)) ^ BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12, 4));
        await dbContext.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0}, {1})", [key1, key2], cancellationToken);
    }
}
