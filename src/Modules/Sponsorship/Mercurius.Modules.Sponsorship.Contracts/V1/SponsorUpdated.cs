using Mercurius.Modules.Shared;

namespace Mercurius.Modules.Sponsorship.Contracts.V1;

public sealed record SponsorUpdated(
    SponsorId SponsorId,
    string Name,
    SponsorTier SponsorTier,
    string LogoUrl,
    string InfoUrl,
    string? Description,
    string? LightLogoUrl = null);
