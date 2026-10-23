namespace Mercurius.Modules.Identity.Contracts;

public interface IIdentityTokenValidator
{
    Task<bool> ValidateAndPinAsync(
        string subject,
        string? issuedAtUnixSeconds,
        bool allowDeletedSelfRead,
        CancellationToken cancellationToken = default);
}
