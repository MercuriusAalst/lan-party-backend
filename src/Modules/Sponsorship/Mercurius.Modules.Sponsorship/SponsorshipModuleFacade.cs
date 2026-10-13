using Mercurius.Modules.Shared;
using Mercurius.Modules.Shared.Exceptions;
using Mercurius.Modules.Sponsorship.Contracts;
using Mercurius.Modules.Sponsorship.Domain;
using Mercurius.Modules.Sponsorship.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mercurius.Modules.Sponsorship;

internal sealed class SponsorshipModuleFacade : ISponsorshipModule
{
    private readonly ISponsorshipDbContext _dbContext;

    public SponsorshipModuleFacade(ISponsorshipDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<SponsorSummary?> GetSponsorSummaryAsync(
        SponsorId sponsorId,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.Sponsors
            .AsNoTracking()
            .Where(sponsor => sponsor.Id == sponsorId.Value)
            .Select(sponsor => new SponsorSummary(
                new SponsorId(sponsor.Id),
                sponsor.Name,
                sponsor.SponsorTier,
                sponsor.LogoUrl,
                sponsor.InfoUrl,
                sponsor.Description))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SponsorSearchDocument>> GetSponsorSearchDocumentsPageAsync(
        SponsorId? afterId,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var afterValue = afterId?.Value;
        return await _dbContext.Sponsors
            .AsNoTracking()
            .Where(sponsor => !afterValue.HasValue || sponsor.Id > afterValue.Value)
            .OrderBy(sponsor => sponsor.Id)
            .Select(sponsor => new SponsorSearchDocument(
                new SponsorId(sponsor.Id),
                sponsor.Name,
                sponsor.LogoUrl))
            .Take(Math.Clamp(pageSize, 1, 1000))
            .ToListAsync(cancellationToken);
    }

    public Task<SponsorPlacementSummary?> GetSponsorPlacementAsync(
        TournamentId tournamentId,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.TournamentSponsorPlacements
            .AsNoTracking()
            .Where(placement => placement.TournamentId == tournamentId.Value)
            .Select(ToPlacementSummary())
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<TournamentId, SponsorPlacementSummary>> GetSponsorPlacementsAsync(
        IReadOnlyCollection<TournamentId> tournamentIds,
        CancellationToken cancellationToken = default)
    {
        var distinctTournamentIds = tournamentIds
            .Select(tournamentId => tournamentId.Value)
            .Distinct()
            .ToArray();
        if (distinctTournamentIds.Length == 0)
            return new Dictionary<TournamentId, SponsorPlacementSummary>();

        var placements = await _dbContext.TournamentSponsorPlacements
            .AsNoTracking()
            .Where(placement => distinctTournamentIds.Contains(placement.TournamentId))
            .Select(ToPlacementSummary())
            .ToListAsync(cancellationToken);
        return placements.ToDictionary(placement => placement.TournamentId);
    }

    public async Task ReplaceSponsorPlacementAsync(
        TournamentId tournamentId,
        SponsorPlacementInput? placement,
        CancellationToken cancellationToken = default)
    {
        var current = await _dbContext.TournamentSponsorPlacements
            .SingleOrDefaultAsync(candidate => candidate.TournamentId == tournamentId.Value, cancellationToken);
        if (placement is null)
        {
            if (current is null)
                return;

            _dbContext.TournamentSponsorPlacements.Remove(current);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        if (!await _dbContext.Sponsors.AsNoTracking().AnyAsync(
                sponsor => sponsor.Id == placement.SponsorId.Value,
                cancellationToken))
        {
            throw new NotFoundException($"Sponsor with ID {placement.SponsorId.Value} not found");
        }

        if (current is null)
        {
            current = new TournamentSponsorPlacement { TournamentId = tournamentId.Value };
            _dbContext.TournamentSponsorPlacements.Add(current);
        }

        current.SponsorId = placement.SponsorId.Value;
        current.Context = placement.Context;
        current.Headline = placement.Headline;
        current.SupportLine = placement.SupportLine;
        current.DisplayOrder = placement.DisplayOrder;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static System.Linq.Expressions.Expression<Func<TournamentSponsorPlacement, SponsorPlacementSummary>> ToPlacementSummary()
    {
        return placement => new SponsorPlacementSummary(
            new SponsorPlacementId(placement.Id),
            new TournamentId(placement.TournamentId),
            new SponsorSummary(
                new SponsorId(placement.Sponsor.Id),
                placement.Sponsor.Name,
                placement.Sponsor.SponsorTier,
                placement.Sponsor.LogoUrl,
                placement.Sponsor.InfoUrl,
                placement.Sponsor.Description),
            placement.Context,
            placement.Headline,
            placement.SupportLine,
            placement.DisplayOrder);
    }
}
