using Mercurius.Modules.Shared;

namespace Mercurius.Modules.Sponsorship.Contracts;

public sealed record SponsorSummary(
    SponsorId Id,
    string Name,
    SponsorTier SponsorTier,
    string LogoUrl,
    string InfoUrl,
    string? Description,
    string? LightLogoUrl = null);
