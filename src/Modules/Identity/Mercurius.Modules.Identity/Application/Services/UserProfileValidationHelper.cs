using System.Text.RegularExpressions;
using Mercurius.Modules.Shared.Exceptions;

namespace Mercurius.Modules.Identity.Application.Services;

internal static class UserProfileValidationHelper
{
    private static readonly Regex UsernameRegex = new("^[a-zA-Z0-9]{3,32}$", RegexOptions.Compiled);
    private static readonly HashSet<string> ReservedUsernames = new(StringComparer.Ordinal)
    {
        "account",
        "accounts",
        "admin",
        "administrator",
        "api",
        "auth",
        "completeprofile",
        "deleted",
        "deleteduser",
        "login",
        "logout",
        "me",
        "mercurius",
        "profile",
        "root",
        "support",
        "system",
        "user",
        "users"
    };

    public static bool IsUsernameValid(string username)
    {
        var normalizedUsername = (username ?? string.Empty).Trim();
        return UsernameRegex.IsMatch(normalizedUsername) &&
            !Guid.TryParse(normalizedUsername, out _);
    }

    public static bool IsReservedUsername(string username)
    {
        var normalizedUsername = NormalizeUsername(username);
        return ReservedUsernames.Contains(normalizedUsername);
    }

    public static string NormalizeUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return string.Empty;

        return username.Trim().ToLowerInvariant();
    }

    public static string NormalizeRequiredText(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ValidationException($"{fieldName} is required.");

        var normalized = value.Trim();
        if (ContainsControlCharacters(normalized))
            throw new ValidationException($"{fieldName} cannot contain control characters.");

        return normalized;
    }

    public static string? NormalizeOptionalPlatformId(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        if (normalized.Length > 100)
            throw new ValidationException($"{fieldName} cannot exceed 100 characters.");

        if (ContainsControlCharacters(normalized))
            throw new ValidationException($"{fieldName} cannot contain control characters.");

        return normalized;
    }

    private static bool ContainsControlCharacters(string value)
    {
        return value.Any(char.IsControl);
    }
}
