namespace Mercurius.Modules.Identity.Domain;

public class User
{
    public Guid Id { get; set; }
    public string Auth0UserId { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? NormalizedUsername { get; set; }
    public string? Firstname { get; set; }
    public string? Lastname { get; set; }
    public string? Email { get; set; }
    public bool EmailVerified { get; set; }
    public string? DiscordId { get; set; } = string.Empty;
    public string? SteamId { get; set; } = string.Empty;
    public string? RiotId { get; set; } = string.Empty;
    public string? ProfilePictureUrl { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public bool IsComplete =>
        !IsDeleted &&
        !string.IsNullOrWhiteSpace(Username) &&
        !string.IsNullOrWhiteSpace(NormalizedUsername) &&
        !string.IsNullOrWhiteSpace(Firstname) &&
        !string.IsNullOrWhiteSpace(Lastname);

    public string DisplayName
    {
        get
        {
            if (IsDeleted)
                return "Deleted user";

            var fullName = $"{Firstname} {Lastname}".Trim();
            if (!string.IsNullOrWhiteSpace(fullName))
                return fullName;

            return string.IsNullOrWhiteSpace(Username) ? "Incomplete profile" : Username;
        }
    }

    public void UpdateLocalProfile(
        string username,
        string normalizedUsername,
        string firstname,
        string lastname,
        string? discordId,
        string? steamId,
        string? riotId,
        DateTime updatedAtUtc)
    {
        Username = username;
        NormalizedUsername = normalizedUsername;
        Firstname = firstname;
        Lastname = lastname;
        DiscordId = discordId;
        SteamId = steamId;
        RiotId = riotId;
        UpdatedAtUtc = updatedAtUtc;
    }

    public void SyncAuth0Profile(string? email, bool? emailVerified, DateTime updatedAtUtc)
    {
        var changed = false;

        if (!string.Equals(Email, email, StringComparison.OrdinalIgnoreCase))
        {
            Email = email;
            changed = true;
        }

        if (emailVerified.HasValue && EmailVerified != emailVerified.Value)
        {
            EmailVerified = emailVerified.Value;
            changed = true;
        }

        if (changed)
            UpdatedAtUtc = updatedAtUtc;
    }

    public void Anonymize(DateTime deletedAtUtc)
    {
        var deletedUsername = $"deleted-user-{Id:N}"[..21];

        Auth0UserId = $"tombstone:{Id:N}";
        Username = deletedUsername;
        NormalizedUsername = deletedUsername;
        Firstname = null;
        Lastname = null;
        Email = null;
        EmailVerified = false;
        DiscordId = null;
        SteamId = null;
        RiotId = null;
        ProfilePictureUrl = null;
        IsDeleted = true;
        DeletedAtUtc = deletedAtUtc;
        UpdatedAtUtc = deletedAtUtc;
    }
}
