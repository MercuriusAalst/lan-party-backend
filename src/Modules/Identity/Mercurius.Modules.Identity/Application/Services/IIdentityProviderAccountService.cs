namespace Mercurius.Modules.Identity.Application.Services;

internal interface IIdentityProviderAccountService
{
    // Returns the account creation time in UTC, or null when the account is absent.
    Task<DateTime?> GetAccountCreatedAtAsync(string subject, CancellationToken cancellationToken = default);

    // Already-absent accounts are success. Failures must surface sanitized messages through IdentityProviderRequestException.
    Task DeleteAccountAsync(string subject, CancellationToken cancellationToken = default);
}
