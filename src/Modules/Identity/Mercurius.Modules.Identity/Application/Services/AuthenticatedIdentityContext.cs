using Mercurius.Modules.Identity.Contracts;

namespace Mercurius.Modules.Identity.Application.Services;

internal sealed class AuthenticatedIdentityContext : IAuthenticatedIdentityContext
{
    public bool IsInitialized { get; private set; }
    public string? Subject { get; private set; }
    public Guid? UserId { get; private set; }
    public bool ShouldReturnDeletedSession { get; private set; }
    public string? IssuedAtUnixSeconds { get; private set; }

    public void Pin(string subject, Guid? userId, bool shouldReturnDeletedSession = false, string? issuedAtUnixSeconds = null)
    {
        Subject = subject;
        UserId = userId;
        ShouldReturnDeletedSession = shouldReturnDeletedSession;
        IssuedAtUnixSeconds = issuedAtUnixSeconds;
        IsInitialized = true;
    }

    public void Clear()
    {
        Subject = null;
        UserId = null;
        ShouldReturnDeletedSession = false;
        IssuedAtUnixSeconds = null;
        IsInitialized = false;
    }
}
