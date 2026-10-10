using System.Buffers.Binary;
using Mercurius.Modules.Teams.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mercurius.Modules.Teams.Infrastructure;

internal static class TeamMutationLock
{
    public static async Task AcquireAsync(ITeamsDbContext dbContext, Guid teamId, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Team mutation locks require an active database transaction.");

        var bytes = teamId.ToByteArray();
        var key1 = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, 4)) ^
                   BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4));
        var key2 = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4)) ^
                   BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12, 4));

        // ponytail: Guid locks share PostgreSQL's 64-bit key space; a collision only serializes unrelated teams.
        await dbContext.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0}, {1})",
            [key1, key2],
            cancellationToken);
    }
}
