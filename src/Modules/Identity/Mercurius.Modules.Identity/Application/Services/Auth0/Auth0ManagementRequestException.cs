using Mercurius.Modules.Identity.Application.Services;

namespace Mercurius.Modules.Identity.Application.Services.Auth0;

internal sealed class Auth0ManagementRequestException(string message) : IdentityProviderRequestException(message);
