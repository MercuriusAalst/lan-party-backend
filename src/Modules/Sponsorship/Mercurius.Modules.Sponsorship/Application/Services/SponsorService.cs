using Mercurius.Modules.Media.Contracts;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Shared.Exceptions;
using Mercurius.Modules.Sponsorship.Application.DTOs;
using Mercurius.Modules.Sponsorship.Contracts;
using Mercurius.Modules.Sponsorship.Contracts.V1;
using Mercurius.Modules.Sponsorship.Domain;
using Mercurius.Modules.Sponsorship.Infrastructure;
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

        await using var imageStream = sponsorDTO.Logo.OpenReadStream();
        var logo = await _mediaModule.SaveImageAsync(
            new MediaUpload(
                imageStream,
                sponsorDTO.Logo.FileName,
                sponsorDTO.Logo.ContentType,
                sponsorDTO.Logo.Length,
                MediaPurpose.SponsorLogo),
            cancellationToken);
        var committed = false;
        try
        {
            var sponsor = new Sponsor
            {
                Name = sponsorDTO.Name,
                SponsorTier = sponsorDTO.SponsorTier,
                LogoUrl = logo.Url,
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
                    sponsor.Description),
                cancellationToken);
            committed = true;
            return GetSponsorDTO.From(sponsor);
        }
        catch
        {
            if (!committed)
                await DeleteImageBestEffortAsync(logo.Url, "compensate an uncommitted Sponsor logo");
            throw;
        }
    }

    public async Task<GetSponsorDTO> UpdateSponsorAsync(
        int id,
        UpdateSponsorDTO sponsorDTO,
        CancellationToken cancellationToken = default)
    {
        var sponsor = await GetSponsorForMutationAsync(id, cancellationToken);
        var previousLogoUrl = sponsor.LogoUrl;
        string? newLogoUrl = null;
        if (sponsorDTO.Logo is not null)
        {
            await using var imageStream = sponsorDTO.Logo.OpenReadStream();
            var logo = await _mediaModule.SaveImageAsync(
                new MediaUpload(
                    imageStream,
                    sponsorDTO.Logo.FileName,
                    sponsorDTO.Logo.ContentType,
                    sponsorDTO.Logo.Length,
                    MediaPurpose.SponsorLogo),
                cancellationToken);
            newLogoUrl = logo.Url;
        }

        var committed = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(sponsorDTO.Name))
                sponsor.Name = sponsorDTO.Name;
            if (newLogoUrl is not null)
                sponsor.LogoUrl = newLogoUrl;

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
                    sponsor.Description),
                cancellationToken);
            committed = true;
        }
        catch
        {
            if (!committed && !string.Equals(newLogoUrl, previousLogoUrl, StringComparison.Ordinal))
                await DeleteImageBestEffortAsync(newLogoUrl, "compensate an uncommitted Sponsor logo replacement");
            throw;
        }

        if (newLogoUrl is not null && !string.Equals(previousLogoUrl, newLogoUrl, StringComparison.Ordinal))
            await DeleteImageBestEffortAsync(previousLogoUrl, "retire a replaced Sponsor logo");

        return GetSponsorDTO.From(sponsor);
    }

    public async Task DeleteSponsorAsync(int id, CancellationToken cancellationToken = default)
    {
        var sponsor = await GetSponsorForMutationAsync(id, cancellationToken);
        var logoUrl = sponsor.LogoUrl;
        _dbContext.Sponsors.Remove(sponsor);
        await _outboxWriter.SaveAndPublishAsync(
            () => new SponsorDeleted(new SponsorId(id)),
            cancellationToken);
        await DeleteImageBestEffortAsync(logoUrl, "retire a deleted Sponsor logo");
    }

    private async Task<Sponsor> GetSponsorForMutationAsync(int id, CancellationToken cancellationToken)
    {
        var sponsor = await _dbContext.Sponsors
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        return sponsor ?? throw new NotFoundException($"Sponsor with ID {id} not found");
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
