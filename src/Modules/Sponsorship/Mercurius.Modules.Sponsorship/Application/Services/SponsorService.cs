using Mercurius.Modules.Media.Contracts;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Shared.Exceptions;
using Mercurius.Modules.Sponsorship.Application.DTOs;
using Mercurius.Modules.Sponsorship.Contracts;
using Mercurius.Modules.Sponsorship.Contracts.V1;
using Mercurius.Modules.Sponsorship.Domain;
using Mercurius.Modules.Sponsorship.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Mercurius.Modules.Sponsorship.Application.Services;

internal sealed class SponsorService : ISponsorService
{
    private readonly ISponsorshipDbContext _dbContext;
    private readonly IMediaModule _mediaModule;
    private readonly SponsorshipOutboxWriter _outboxWriter;
    private readonly ILogger<SponsorService> _logger;

    public SponsorService(
        ISponsorshipDbContext dbContext,
        IMediaModule mediaModule,
        SponsorshipOutboxWriter outboxWriter,
        ILogger<SponsorService> logger)
    {
        _dbContext = dbContext;
        _mediaModule = mediaModule;
        _outboxWriter = outboxWriter;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GetSponsorDTO>> GetSponsorsAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.Sponsors
            .AsNoTracking()
            .Select(sponsor => new GetSponsorDTO
            {
                Id = sponsor.Id,
                Name = sponsor.Name,
                SponsorTier = sponsor.SponsorTier,
                LogoUrl = sponsor.LogoUrl,
                LightLogoUrl = sponsor.LightLogoUrl,
                InfoUrl = sponsor.InfoUrl,
                Description = sponsor.Description
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<GetSponsorDTO> GetSponsorByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var sponsor = await _dbContext.Sponsors
            .AsNoTracking()
            .Where(candidate => candidate.Id == id)
            .Select(candidate => new GetSponsorDTO
            {
                Id = candidate.Id,
                Name = candidate.Name,
                SponsorTier = candidate.SponsorTier,
                LogoUrl = candidate.LogoUrl,
                LightLogoUrl = candidate.LightLogoUrl,
                InfoUrl = candidate.InfoUrl,
                Description = candidate.Description
            })
            .SingleOrDefaultAsync(cancellationToken);
        return sponsor ?? throw new NotFoundException($"Sponsor with ID {id} not found");
    }

    public async Task<GetSponsorDTO> CreateSponsorAsync(
        CreateSponsorDTO sponsorDTO,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sponsorDTO.Name))
            throw new ValidationException("Sponsor name cannot be empty");
        if (sponsorDTO.Logo is null)
            throw new ValidationException("A sponsor logo is required.");

        var storedLogoUrls = new List<string>(2);
        var committed = false;
        try
        {
            var logoUrl = await StoreSponsorLogoAsync(sponsorDTO.Logo, cancellationToken);
            storedLogoUrls.Add(logoUrl);
            string? lightLogoUrl = null;
            if (sponsorDTO.LightLogo is not null)
            {
                lightLogoUrl = await StoreSponsorLogoAsync(sponsorDTO.LightLogo, cancellationToken);
                storedLogoUrls.Add(lightLogoUrl);
            }

            var sponsor = new Sponsor
            {
                Name = sponsorDTO.Name,
                SponsorTier = sponsorDTO.SponsorTier,
                LogoUrl = logoUrl,
                LightLogoUrl = lightLogoUrl,
                InfoUrl = sponsorDTO.InfoUrl,
                Description = sponsorDTO.Description
            };

            _dbContext.Sponsors.Add(sponsor);
            await _outboxWriter.SaveAndPublishAsync(
                () => new SponsorCreated(
                    new SponsorId(sponsor.Id),
                    sponsor.Name,
                    sponsor.SponsorTier,
                    sponsor.LogoUrl,
                    sponsor.InfoUrl,
                    sponsor.Description,
                    sponsor.LightLogoUrl),
                cancellationToken);
            committed = true;
            return GetSponsorDTO.From(sponsor);
        }
        catch
        {
            if (!committed)
                await DeleteImagesBestEffortAsync(
                    storedLogoUrls,
                    "compensate an uncommitted Sponsor logo");
            throw;
        }
    }

    public async Task<GetSponsorDTO> UpdateSponsorAsync(
        int id,
        UpdateSponsorDTO sponsorDTO,
        CancellationToken cancellationToken = default)
    {
        if (sponsorDTO.LightLogo is not null && sponsorDTO.RemoveLightLogo)
        {
            throw new ValidationException(
                "The light logo cannot be replaced and removed in the same update.");
        }

        var sponsor = await GetSponsorForMutationAsync(id, cancellationToken);
        var previousLogoUrl = sponsor.LogoUrl;
        var previousLightLogoUrl = sponsor.LightLogoUrl;
        string? newLogoUrl = null;
        string? newLightLogoUrl = null;
        var storedLogoUrls = new List<string>(2);
        var committed = false;
        try
        {
            if (sponsorDTO.Logo is not null)
            {
                newLogoUrl = await StoreSponsorLogoAsync(sponsorDTO.Logo, cancellationToken);
                storedLogoUrls.Add(newLogoUrl);
            }

            if (sponsorDTO.LightLogo is not null)
            {
                newLightLogoUrl = await StoreSponsorLogoAsync(sponsorDTO.LightLogo, cancellationToken);
                storedLogoUrls.Add(newLightLogoUrl);
            }

            if (!string.IsNullOrWhiteSpace(sponsorDTO.Name))
                sponsor.Name = sponsorDTO.Name;
            if (newLogoUrl is not null)
                sponsor.LogoUrl = newLogoUrl;
            if (newLightLogoUrl is not null)
                sponsor.LightLogoUrl = newLightLogoUrl;
            else if (sponsorDTO.RemoveLightLogo)
                sponsor.LightLogoUrl = null;

            sponsor.InfoUrl = sponsorDTO.InfoUrl;
            sponsor.SponsorTier = sponsorDTO.SponsorTier;
            sponsor.Description = sponsorDTO.Description;
            await _outboxWriter.SaveAndPublishAsync(
                () => new SponsorUpdated(
                    new SponsorId(sponsor.Id),
                    sponsor.Name,
                    sponsor.SponsorTier,
                    sponsor.LogoUrl,
                    sponsor.InfoUrl,
                    sponsor.Description,
                    sponsor.LightLogoUrl),
                cancellationToken);
            committed = true;
        }
        catch
        {
            if (!committed)
                await DeleteImagesBestEffortAsync(
                    storedLogoUrls.Where(imageUrl =>
                        !string.Equals(imageUrl, previousLogoUrl, StringComparison.Ordinal) &&
                        !string.Equals(imageUrl, previousLightLogoUrl, StringComparison.Ordinal)),
                    "compensate an uncommitted Sponsor logo replacement");
            throw;
        }

        var currentLogoUrls = new HashSet<string>(StringComparer.Ordinal)
        {
            sponsor.LogoUrl
        };
        if (!string.IsNullOrWhiteSpace(sponsor.LightLogoUrl))
            currentLogoUrls.Add(sponsor.LightLogoUrl);

        var retiredLogoUrls = new List<string?>(2);
        if (newLogoUrl is not null &&
            !string.Equals(previousLogoUrl, newLogoUrl, StringComparison.Ordinal))
        {
            retiredLogoUrls.Add(previousLogoUrl);
        }

        if ((newLightLogoUrl is not null || sponsorDTO.RemoveLightLogo) &&
            !string.Equals(previousLightLogoUrl, sponsor.LightLogoUrl, StringComparison.Ordinal))
        {
            retiredLogoUrls.Add(previousLightLogoUrl);
        }

        await DeleteImagesBestEffortAsync(
            retiredLogoUrls.Where(imageUrl => imageUrl is null || !currentLogoUrls.Contains(imageUrl)),
            "retire a replaced Sponsor logo");

        return GetSponsorDTO.From(sponsor);
    }

    public async Task DeleteSponsorAsync(int id, CancellationToken cancellationToken = default)
    {
        var sponsor = await GetSponsorForMutationAsync(id, cancellationToken);
        var logoUrls = new[] { sponsor.LogoUrl, sponsor.LightLogoUrl };
        _dbContext.Sponsors.Remove(sponsor);
        await _outboxWriter.SaveAndPublishAsync(
            () => new SponsorDeleted(new SponsorId(id)),
            cancellationToken);
        await DeleteImagesBestEffortAsync(logoUrls, "retire a deleted Sponsor logo");
    }

    private async Task<Sponsor> GetSponsorForMutationAsync(int id, CancellationToken cancellationToken)
    {
        var sponsor = await _dbContext.Sponsors
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        return sponsor ?? throw new NotFoundException($"Sponsor with ID {id} not found");
    }

    private async Task<string> StoreSponsorLogoAsync(
        IFormFile logo,
        CancellationToken cancellationToken)
    {
        await using var imageStream = logo.OpenReadStream();
        var storedLogo = await _mediaModule.SaveImageAsync(
            new MediaUpload(
                imageStream,
                logo.FileName,
                logo.ContentType,
                logo.Length,
                MediaPurpose.SponsorLogo),
            cancellationToken);
        return storedLogo.Url;
    }

    private async Task DeleteImagesBestEffortAsync(
        IEnumerable<string?> imageUrls,
        string action)
    {
        foreach (var imageUrl in imageUrls
                     .Where(imageUrl => !string.IsNullOrWhiteSpace(imageUrl))
                     .Distinct(StringComparer.Ordinal))
        {
            await DeleteImageBestEffortAsync(imageUrl, action);
        }
    }

    private async Task DeleteImageBestEffortAsync(string? imageUrl, string action)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
            return;

        try
        {
            await _mediaModule.DeleteImageAsync(imageUrl, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to {MediaCleanupAction} at {MediaUrl}.", action, imageUrl);
        }
    }
}
