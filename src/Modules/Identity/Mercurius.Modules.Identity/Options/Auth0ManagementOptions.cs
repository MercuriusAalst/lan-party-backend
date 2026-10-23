namespace Mercurius.Modules.Identity.Options;

internal sealed class Auth0ManagementOptions
{
    public const string SectionName = "Auth0";

    public string? Authority { get; set; }
    public string? ManagementAudience { get; set; }
    public string? ManagementClientId { get; set; }
    public string? ManagementClientSecret { get; set; }
    public string? DatabaseConnection { get; set; }
    public string? PasswordResetClientId { get; set; }

    public bool HasManagementApiConfiguration =>
        !string.IsNullOrWhiteSpace(Authority) &&
        !string.IsNullOrWhiteSpace(ManagementAudience) &&
        !string.IsNullOrWhiteSpace(ManagementClientId) &&
        !string.IsNullOrWhiteSpace(ManagementClientSecret);

    public bool HasPasswordResetConfiguration =>
        !string.IsNullOrWhiteSpace(Authority) &&
        !string.IsNullOrWhiteSpace(DatabaseConnection) &&
        !string.IsNullOrWhiteSpace(PasswordResetClientId);

    public string AuthorityBaseUri => (Authority ?? string.Empty).TrimEnd('/') + "/";
}
