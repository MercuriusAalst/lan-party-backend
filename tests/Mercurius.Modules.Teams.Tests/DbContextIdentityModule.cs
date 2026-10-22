using Mercurius.LAN.API.Data;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Shared;
using Microsoft.EntityFrameworkCore;

namespace Mercurius.Modules.Teams.Tests;

internal sealed class DbContextIdentityModule : IIdentityModule
{
    private readonly MercuriusDBContext _dbContext;

    public int BatchCallCount { get; private set; }
    public IReadOnlyCollection<UserId> LastBatchUserIds { get; private set; } = [];
    public Func<UserId, CancellationToken, Task>? BeforeUserProfileByIdAsync { get; init; }

    public DbContextIdentityModule(MercuriusDBContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<UserProfileSummary?> GetUserProfileAsync(
        UserId userId,
        CancellationToken cancellationToken = default)
    {
        if (BeforeUserProfileByIdAsync is not null)
            await BeforeUserProfileByIdAsync(userId, cancellationToken);

        return await _dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == userId.Value)
            .Select(user => ToSummary(user))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<UserProfileSummary?> GetUserProfileByAuth0IdAsync(
        string auth0UserId,
        CancellationToken cancellationToken = default)
    {
        var normalizedAuth0UserId = auth0UserId.Trim();
        return _dbContext.Users
            .AsNoTracking()
            .Where(user => user.Auth0UserId == normalizedAuth0UserId)
            .Select(user => ToSummary(user))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<PublicUserProfileSummary?> GetPublicProfileByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<PublicUserProfileSummary?>(null);
    }

    public Task<PublicUserProfileSummary?> GetPublicProfileByIdAsync(
        UserId userId,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.Users
            .AsNoTracking()
            .Where(user =>
                user.Id == userId.Value &&
                !user.IsDeleted &&
                !string.IsNullOrWhiteSpace(user.Username) &&
                !string.IsNullOrWhiteSpace(user.Firstname) &&
                !string.IsNullOrWhiteSpace(user.Lastname))
            .Select(user => new PublicUserProfileSummary(
                new UserId(user.Id),
                user.Username!,
                user.Firstname!,
                user.Lastname!,
                user.DiscordId,
                user.SteamId,
                user.RiotId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<UserId, UserProfileSummary>> GetUsersByIdsAsync(
        IReadOnlyCollection<UserId> userIds,
        CancellationToken cancellationToken = default)
    {
        BatchCallCount++;
        LastBatchUserIds = userIds;
        var ids = userIds.Select(userId => userId.Value).Distinct().ToArray();
        var users = await _dbContext.Users
            .AsNoTracking()
            .Where(user => ids.Contains(user.Id))
            .Select(user => ToSummary(user))
            .ToListAsync(cancellationToken);

        return users.ToDictionary(user => user.Id);
    }

    public async Task<IReadOnlyDictionary<UserId, string>> GetPublicUsernamesByIdsAsync(
        IReadOnlyCollection<UserId> userIds,
        CancellationToken cancellationToken = default)
    {
        var ids = userIds.Select(userId => userId.Value).Distinct().ToArray();
        var users = await _dbContext.Users
            .AsNoTracking()
            .Where(user =>
                ids.Contains(user.Id) &&
                !user.IsDeleted &&
                !string.IsNullOrWhiteSpace(user.Username) &&
                !string.IsNullOrWhiteSpace(user.NormalizedUsername) &&
                !string.IsNullOrWhiteSpace(user.Firstname) &&
                !string.IsNullOrWhiteSpace(user.Lastname))
            .Select(user => new { user.Id, user.Username })
            .ToListAsync(cancellationToken);

        return users.ToDictionary(user => new UserId(user.Id), user => user.Username!);
    }

    public Task<IReadOnlyList<PublicUserSearchDocument>> GetPublicUserSearchDocumentsPageAsync(
        UserId? afterId,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<PublicUserSearchDocument>>([]);
    }

    private static UserProfileSummary ToSummary(Modules.Identity.Domain.User user)
    {
        return new UserProfileSummary(
            new UserId(user.Id),
            user.Username,
            user.DisplayName,
            user.IsDeleted,
            user.DiscordId,
            user.SteamId,
            user.RiotId,
            user.ProfilePictureUrl,
            user.CreatedAtUtc);
    }
}
