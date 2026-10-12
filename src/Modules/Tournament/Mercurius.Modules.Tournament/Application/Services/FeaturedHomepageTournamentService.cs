using Mercurius.Modules.Tournament.Application.DTOs.Tournaments;
using Mercurius.Modules.Tournament.Domain;
using Mercurius.Modules.Tournament.Infrastructure;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Sponsorship.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Mercurius.Modules.Tournament.Application.Services;

internal sealed class FeaturedHomepageTournamentService(
    ITournamentDbContext dbContext,
    ISponsorshipModule sponsorshipModule) : IFeaturedHomepageTournamentService
{
    private const int MaximumFeaturedTournamentCount = 4;

    public async Task<FeaturedHomepageTournamentsDTO> GetFeaturedTournamentsAsync(
        CancellationToken cancellationToken = default)
    {
        var tournamentIds = await dbContext.FeaturedHomepageTournamentSelections
            .AsNoTracking()
            .Where(selection => selection.Id == FeaturedHomepageTournamentSelection.SingletonId)
            .Select(selection => selection.TournamentIds)
            .SingleOrDefaultAsync(cancellationToken);

        if (tournamentIds is null)
        {
            var fallback = await GetEligibleTournamentQuery()
                .OrderBy(tournament => tournament.PlannedStartTime)
                .ThenBy(tournament => tournament.Name)
                .ThenBy(tournament => tournament.Id)
                .Take(MaximumFeaturedTournamentCount)
                .Select(ToCard())
                .ToListAsync(cancellationToken);
            return await CreateResponseAsync(fallback, cancellationToken);
        }

        var selected = await GetEligibleTournamentQuery()
            .Where(tournament => tournamentIds.Contains(tournament.Id))
            .Select(ToCard())
            .ToListAsync(cancellationToken);
        var selectedById = selected.ToDictionary(tournament => tournament.Id);
        var ordered = tournamentIds
            .Distinct()
            .Where(selectedById.ContainsKey)
            .Select(id => selectedById[id])
            .ToList();
        var vacancyCount = tournamentIds.Length - ordered.Count;

        if (vacancyCount > 0)
        {
            ordered.AddRange(await GetEligibleTournamentQuery()
                .Where(tournament => !tournamentIds.Contains(tournament.Id))
                .OrderBy(tournament => tournament.PlannedStartTime)
                .ThenBy(tournament => tournament.Name)
                .ThenBy(tournament => tournament.Id)
                .Take(vacancyCount)
                .Select(ToCard())
                .ToListAsync(cancellationToken));
        }

        return await CreateResponseAsync(ordered, cancellationToken);
    }

    public async Task<Dictionary<string, string[]>?> ReplaceFeaturedTournamentsAsync(
        Guid[]? tournamentIds,
        CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string[]>();
        if (tournamentIds is null || tournamentIds.Length is < 1 or > MaximumFeaturedTournamentCount)
        {
            errors["tournamentIds"] = ["Choose between one and four tournament IDs."];
            return errors;
        }

        if (tournamentIds.Distinct().Count() != tournamentIds.Length)
        {
            errors["tournamentIds"] = ["Tournament IDs must be distinct."];
            return errors;
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var eligibleIds = await dbContext.Tournaments
            .FromSqlInterpolated($"""
                SELECT *
                FROM tournament.tournaments
                WHERE "Id" = ANY ({tournamentIds})
                  AND "Status" <> {(int)TournamentStatus.Canceled}
                ORDER BY "Id"
                FOR SHARE
                """)
            .AsNoTracking()
            .Select(tournament => tournament.Id)
            .ToListAsync(cancellationToken);
        if (eligibleIds.Count != tournamentIds.Length)
        {
            var invalidIds = tournamentIds.Except(eligibleIds);
            errors["tournamentIds"] = [$"Every ID must identify an existing non-canceled tournament. Invalid IDs: {string.Join(", ", invalidIds)}."];
            return errors;
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO tournament.featured_homepage_tournaments ("Id", "TournamentIds")
            VALUES ({FeaturedHomepageTournamentSelection.SingletonId}, {tournamentIds})
            ON CONFLICT ("Id")
            DO UPDATE SET "TournamentIds" = EXCLUDED."TournamentIds"
            """, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return null;
    }

    private IQueryable<TournamentAggregate> GetEligibleTournamentQuery() => dbContext.Tournaments
        .AsNoTracking()
        .Where(tournament => tournament.Status != TournamentStatus.Canceled);

    private static System.Linq.Expressions.Expression<Func<TournamentAggregate, FeaturedHomepageTournamentCardDTO>> ToCard() =>
        tournament => new FeaturedHomepageTournamentCardDTO(
            tournament.Id,
            tournament.Name,
            tournament.ImageUrl,
            (Contracts.TournamentStatus)tournament.Status,
            (Contracts.BracketType)tournament.BracketType,
            (Contracts.GameFormat)tournament.Format);

    private async Task<FeaturedHomepageTournamentsDTO> CreateResponseAsync(
        IReadOnlyList<FeaturedHomepageTournamentCardDTO> tournaments,
        CancellationToken cancellationToken)
    {
        var tournamentsWithSponsors = await AttachSponsorPlacementsAsync(tournaments, cancellationToken);
        return new(
            tournamentsWithSponsors.Select(tournament => tournament.Id).ToArray(),
            tournamentsWithSponsors);
    }

    private async Task<IReadOnlyList<FeaturedHomepageTournamentCardDTO>> AttachSponsorPlacementsAsync(
        IReadOnlyList<FeaturedHomepageTournamentCardDTO> tournaments,
        CancellationToken cancellationToken)
    {
        if (tournaments.Count == 0)
            return tournaments;

        var placements = await sponsorshipModule.GetSponsorPlacementsAsync(
            tournaments.Select(tournament => new TournamentId(tournament.Id)).ToArray(),
            cancellationToken);
        if (placements.Count == 0)
            return tournaments;

        return tournaments
            .Select(tournament => placements.TryGetValue(new TournamentId(tournament.Id), out var placement)
                ? tournament with { SponsorPlacement = TournamentDtoMapper.ToSponsorPlacementDto(placement) }
                : tournament)
            .ToList();
    }
}
