using Mercurius.Modules.Sponsorship.Contracts;
using Mercurius.Modules.Sponsorship.Domain;

namespace Mercurius.Modules.Sponsorship.Application.DTOs;

internal sealed class GetSponsorDTO
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public SponsorTier SponsorTier { get; set; }
    public string LogoUrl { get; set; } = null!;
    public string? LightLogoUrl { get; set; }
    public string InfoUrl { get; set; } = null!;
    public string? Description { get; set; }

    public static GetSponsorDTO From(Sponsor sponsor)
    {
        return new GetSponsorDTO
        {
            Id = sponsor.Id,
            Name = sponsor.Name,
            SponsorTier = sponsor.SponsorTier,
            LogoUrl = sponsor.LogoUrl,
            LightLogoUrl = sponsor.LightLogoUrl,
            InfoUrl = sponsor.InfoUrl,
            Description = sponsor.Description
        };
    }
}
