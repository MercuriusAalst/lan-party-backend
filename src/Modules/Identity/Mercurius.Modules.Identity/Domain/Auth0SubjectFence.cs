namespace Mercurius.Modules.Identity.Domain;

internal sealed class Auth0SubjectFence
{
    public string SubjectHash { get; set; } = string.Empty;
    public Guid? PendingCleanupJobId { get; set; }
    public long? RejectTokensBeforeUnixSeconds { get; set; }
}
