using Mercurius.Modules.Sponsorship.Contracts;
using Microsoft.AspNetCore.Http;

namespace Mercurius.Modules.Sponsorship.Application.DTOs;

internal sealed class UpdateSponsorDTO
{
    public string Name { get; set; } = null!;
    public SponsorTier SponsorTier { get; set; }
    public IFormFile? Logo { get; set; }
    public IFormFile? LightLogo { get; set; }
    public bool RemoveLightLogo { get; set; }
    public string InfoUrl { get; set; } = null!;
    public string? Description { get; set; }
}
