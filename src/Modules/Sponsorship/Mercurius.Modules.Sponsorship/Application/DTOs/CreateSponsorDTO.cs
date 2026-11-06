using Mercurius.Modules.Sponsorship.Contracts;
using Microsoft.AspNetCore.Http;

namespace Mercurius.Modules.Sponsorship.Application.DTOs;

internal sealed class CreateSponsorDTO
{
    public string Name { get; set; } = null!;
    public SponsorTier SponsorTier { get; set; }
    public IFormFile Logo { get; set; } = null!;
    public IFormFile? LightLogo { get; set; }
    public string InfoUrl { get; set; } = null!;
    public string? Description { get; set; }
}
