namespace Mercurius.Modules.Identity.Contracts;

public interface IAuthenticatedIdentityContext
{
    bool IsInitialized { get; }
    string? Subject { get; }
    Guid? UserId { get; }
    bool ShouldReturnDeletedSession { get; }
    string? IssuedAtUnixSeconds { get; }
    void Pin(string subject, Guid? userId, bool shouldReturnDeletedSession = false, string? issuedAtUnixSeconds = null);
    void Clear();
}
