using Mercurius.Modules.Identity.Application.DTOs;
using Mercurius.Modules.Identity.Application.Services;

namespace Mercurius.Modules.Identity.Application.Services.Auth0;

internal interface IAuth0ManagementService : IIdentityProviderAccountService
{
    Task<Auth0ProfileSnapshot> GetUserProfileAsync(string auth0UserId, CancellationToken cancellationToken = default);
    Task<bool> HasAdminRoleAsync(string auth0UserId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
    Task<IReadOnlyList<string>> GetAdminUserIdsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
    Task SendVerificationEmailAsync(string auth0UserId, CancellationToken cancellationToken = default);
    Task SendPasswordResetEmailAsync(string email, CancellationToken cancellationToken = default);
}
