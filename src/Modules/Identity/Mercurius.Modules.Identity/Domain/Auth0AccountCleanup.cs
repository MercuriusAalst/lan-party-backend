namespace Mercurius.Modules.Identity.Domain;

internal sealed class Auth0AccountCleanup
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string SubjectHash { get; set; } = string.Empty;
    public string EncryptedSubject { get; set; } = string.Empty;
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? ProviderCreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? SanitizedOutcome { get; set; }
    public Guid OutboxMessageId { get; set; }
}
