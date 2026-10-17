using Mercurius.Modules.Shared;

namespace Mercurius.Modules.Identity.Contracts;

public sealed record PublicUserSearchDocument(
    UserId UserId,
    string Username,
    string? ProfilePictureUrl = null);
