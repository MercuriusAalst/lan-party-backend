namespace Mercurius.Modules.Identity.Application.DTOs;

internal sealed record Auth0ProfileSnapshot(string? Email, bool? EmailVerified, bool HasPasswordResetIdentity);
