using Mercurius.Modules.Shared;

namespace Mercurius.Modules.Identity.Contracts;

public interface IIdentityModule
{
    Task<UserProfileSummary?> GetUserProfileAsync(
        UserId userId,
        CancellationToken cancellationToken = default);

    // Takes the account's mutation lock inside the caller's active database transaction before reading it,
    // so a write that depends on the account being active is serialized with account deletion.
    Task<UserProfileSummary?> GetUserProfileForUpdateAsync(
        UserId userId,
        CancellationToken cancellationToken = default) =>
        GetUserProfileAsync(userId, cancellationToken);

    Task<UserProfileSummary?> GetUserProfileByAuth0IdAsync(
        string auth0UserId,
        CancellationToken cancellationToken = default);

    Task<PublicUserProfileSummary?> GetPublicProfileByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default);

    Task<PublicUserProfileSummary?> GetPublicProfileByIdAsync(
        UserId userId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<UserId, UserProfileSummary>> GetUsersByIdsAsync(
        IReadOnlyCollection<UserId> userIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<UserId, string>> GetPublicUsernamesByIdsAsync(
        IReadOnlyCollection<UserId> userIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PublicUserSearchDocument>> GetPublicUserSearchDocumentsPageAsync(
        UserId? afterId,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<bool> IsAdminUserAsync(UserId userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    Task<IReadOnlyList<UserProfileSummary>> GetAdminUsersAsync(
        string normalizedQuery,
        int pageSize,
        CancellationToken cancellationToken = default,
        int page = 1) =>
        Task.FromResult<IReadOnlyList<UserProfileSummary>>([]);
}
