using Mercurius.Modules.Shared.Exceptions;

namespace Mercurius.Modules.Identity.Contracts;

public static class IdentityModuleExtensions
{
    public static async Task<UserProfileSummary> GetRequiredCurrentUserAsync(
        this IIdentityModule identityModule,
        string auth0UserId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(auth0UserId))
            throw new UnauthorizedAccessException("Authenticated user id is missing.");

        var user = await identityModule.GetUserProfileByAuth0IdAsync(auth0UserId.Trim(), cancellationToken);
        if (user is null || user.IsDeleted)
            throw new NotFoundException("Current user profile was not found.");

        return user;
    }
}
