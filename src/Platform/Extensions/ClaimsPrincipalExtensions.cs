using System.Security.Claims;

namespace Platform.Extensions;

public static class ClaimsPrincipalExtensions
{
    // Auth0 issues the user id as "sub"; inbound claim mapping may expose it as NameIdentifier instead.
    public static string? FindAuth0UserId(this ClaimsPrincipal user) =>
        user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);

    public static string GetAuth0UserId(this ClaimsPrincipal user)
    {
        var subject = user.FindAuth0UserId();
        if (string.IsNullOrWhiteSpace(subject))
            throw new UnauthorizedAccessException("Authenticated user id is missing.");

        return subject;
    }
}
