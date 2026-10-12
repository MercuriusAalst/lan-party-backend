using Mercurius.LAN.API.Data;
using Mercurius.Modules.Tournament.Application.Services;
using Mercurius.Modules.Tournament.Infrastructure;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Sponsorship.Contracts;
using Mercurius.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Serialization;
using TournamentDomain = Mercurius.Modules.Tournament.Domain;

namespace Mercurius.Modules.Tournament.Tests;

public sealed class FeaturedHomepageTournamentTests
{
    private static readonly DateTime PlannedStart = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task PublicReadUsesExistingOrderAndOnlyHomepageCardFields()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = CreateOptions(database);
        await using var dbContext = new MercuriusDBContext(options);
        await dbContext.Database.MigrateAsync();

        var tournaments = Enumerable.Range(0, 5).Select(CreateTournament).ToArray();
        dbContext.Set<TournamentAggregate>().AddRange(tournaments);
        dbContext.Set<TournamentAggregate>().Add(CreateTournament(5, TournamentDomain.TournamentStatus.Canceled));
        await dbContext.SaveChangesAsync();

        var response = await CreateService(dbContext).GetFeaturedTournamentsAsync();

        Assert.Equal(tournaments.Take(4).Select(tournament => tournament.Id), response.TournamentIds);
        Assert.Equal(response.TournamentIds, response.Tournaments.Select(tournament => tournament.Id));
        var optionsJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        optionsJson.Converters.Add(new JsonStringEnumConverter());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response, optionsJson));
        var cardProperties = json.RootElement.GetProperty("tournaments")[0]
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "bracketType", "format", "id", "imageUrl", "name", "sponsorPlacement", "status" }, cardProperties);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("tournaments")[0].GetProperty("sponsorPlacement").ValueKind);
        Assert.Equal("SingleElimination", json.RootElement.GetProperty("tournaments")[0].GetProperty("bracketType").GetString());
    }

    [Fact]
    public async Task PublicReadExposesPublicSponsorAttributionOnlyForSponsoredCards()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = CreateOptions(database);
        await using var dbContext = new MercuriusDBContext(options);
        await dbContext.Database.MigrateAsync();

        var tournaments = Enumerable.Range(0, 4).Select(CreateTournament).ToArray();
        dbContext.Set<TournamentAggregate>().AddRange(tournaments);
        await dbContext.SaveChangesAsync();

        var sponsored = tournaments[1];
        var sponsorshipModule = TournamentTestSupport.CreateSponsorshipModule(new SponsorPlacementSummary(
            new SponsorPlacementId(7),
            new TournamentId(sponsored.Id),
            new SponsorSummary(
                new SponsorId(3),
                "Acme Esports",
                SponsorTier.Presenting,
                "logos/acme.png",
                "https://acme.example",
                "Acme powers the main stage"),
            SponsorContext.TournamentPartner,
            "Headline",
            "Support line",
            0));

        var response = await CreateService(dbContext, sponsorshipModule).GetFeaturedTournamentsAsync();

        var sponsoredCard = response.Tournaments.Single(card => card.Id == sponsored.Id);
        Assert.NotNull(sponsoredCard.SponsorPlacement);
        Assert.Equal("Acme Esports", sponsoredCard.SponsorPlacement!.SponsorName);
        Assert.Equal(SponsorTier.Presenting, sponsoredCard.SponsorPlacement.SponsorTier);
        Assert.Equal(SponsorContext.TournamentPartner, sponsoredCard.SponsorPlacement.Context);
        Assert.All(
            response.Tournaments.Where(card => card.Id != sponsored.Id),
            card => Assert.Null(card.SponsorPlacement));

        var optionsJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        optionsJson.Converters.Add(new JsonStringEnumConverter());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response, optionsJson));
        var cards = json.RootElement.GetProperty("tournaments").EnumerateArray().ToArray();
        var sponsoredJson = cards.Single(card => card.GetProperty("id").GetGuid() == sponsored.Id);
        var sponsorJson = sponsoredJson.GetProperty("sponsorPlacement");
        Assert.Equal("Acme Esports", sponsorJson.GetProperty("sponsorName").GetString());
        Assert.Equal("Presenting", sponsorJson.GetProperty("sponsorTier").GetString());
        Assert.Equal("TournamentPartner", sponsorJson.GetProperty("context").GetString());
        Assert.All(
            cards.Where(card => card.GetProperty("id").GetGuid() != sponsored.Id),
            card => Assert.Equal(JsonValueKind.Null, card.GetProperty("sponsorPlacement").ValueKind));
    }

    [Fact]
    public async Task ReplacementAcceptsOneToFourItemsAndRejectsInvalidValuesAcrossContexts()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = CreateOptions(database);
        Guid[] selection;
        await using (var dbContext = new MercuriusDBContext(options))
        {
            await dbContext.Database.MigrateAsync();
            var tournaments = Enumerable.Range(0, 5).Select(CreateTournament).ToArray();
            var canceled = CreateTournament(5, TournamentDomain.TournamentStatus.Canceled);
            dbContext.Set<TournamentAggregate>().AddRange(tournaments);
            dbContext.Set<TournamentAggregate>().Add(canceled);
            await dbContext.SaveChangesAsync();

            var service = CreateService(dbContext);
            selection = tournaments.Take(4).Select(tournament => tournament.Id).Reverse().ToArray();

            var one = selection[..1];
            var three = selection[..3];

            Assert.Null(await service.ReplaceFeaturedTournamentsAsync(one));
            Assert.Equal(one, (await service.GetFeaturedTournamentsAsync()).TournamentIds);
            Assert.Null(await service.ReplaceFeaturedTournamentsAsync(three));
            Assert.Equal(three, (await service.GetFeaturedTournamentsAsync()).TournamentIds);
            Assert.Null(await service.ReplaceFeaturedTournamentsAsync(selection));

            Assert.NotNull(await service.ReplaceFeaturedTournamentsAsync([]));
            Assert.NotNull(await service.ReplaceFeaturedTournamentsAsync([.. selection, tournaments[4].Id]));
            Assert.NotNull(await service.ReplaceFeaturedTournamentsAsync([selection[0], selection[1], selection[2], selection[2]]));
            Assert.NotNull(await service.ReplaceFeaturedTournamentsAsync([selection[0], selection[1], selection[2], Guid.NewGuid()]));
            Assert.NotNull(await service.ReplaceFeaturedTournamentsAsync([selection[0], selection[1], selection[2], canceled.Id]));
        }

        await using var reopenedContext = new MercuriusDBContext(options);
        var response = await CreateService(reopenedContext).GetFeaturedTournamentsAsync();

        Assert.Equal(selection, response.TournamentIds);
    }

    [Fact]
    public async Task ReplacementAllowsOneSelectionWhenItIsTheOnlyEligibleTournament()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = CreateOptions(database);
        await using var dbContext = new MercuriusDBContext(options);
        await dbContext.Database.MigrateAsync();
        var tournament = CreateTournament(0);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext);

        Assert.Null(await service.ReplaceFeaturedTournamentsAsync([tournament.Id]));
        Assert.Equal([tournament.Id], (await service.GetFeaturedTournamentsAsync()).TournamentIds);
    }

    [Fact]
    public async Task PublicReadBackfillsSavedPartialSelectionOnlyToSavedCount()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = CreateOptions(database);
        await using var dbContext = new MercuriusDBContext(options);
        await dbContext.Database.MigrateAsync();
        var tournaments = Enumerable.Range(0, 6).Select(CreateTournament).ToArray();
        dbContext.Set<TournamentAggregate>().AddRange(tournaments);
        await dbContext.SaveChangesAsync();

        var selection = new[] { tournaments[5].Id, tournaments[3].Id, tournaments[4].Id };
        var service = CreateService(dbContext);
        Assert.Null(await service.ReplaceFeaturedTournamentsAsync(selection));

        tournaments[3].Status = TournamentDomain.TournamentStatus.Canceled;
        await dbContext.SaveChangesAsync();

        var response = await service.GetFeaturedTournamentsAsync();

        Assert.Equal(new[] { tournaments[5].Id, tournaments[4].Id, tournaments[0].Id }, response.TournamentIds);
        Assert.Equal(response.TournamentIds, response.Tournaments.Select(tournament => tournament.Id));
    }

    [Fact]
    public async Task PublicReadPreservesValidOrderAndBackfillsDeletedOrCanceledSelections()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = CreateOptions(database);
        await using var dbContext = new MercuriusDBContext(options);
        await dbContext.Database.MigrateAsync();
        var tournaments = Enumerable.Range(0, 6).Select(CreateTournament).ToArray();
        dbContext.Set<TournamentAggregate>().AddRange(tournaments);
        await dbContext.SaveChangesAsync();

        var selection = tournaments.Skip(2).Take(4).Select(tournament => tournament.Id).Reverse().ToArray();
        var service = CreateService(dbContext);
        Assert.Null(await service.ReplaceFeaturedTournamentsAsync(selection));
        dbContext.Set<TournamentAggregate>().Remove(tournaments[5]);
        tournaments[4].Status = TournamentDomain.TournamentStatus.Canceled;
        await dbContext.SaveChangesAsync();

        var response = await service.GetFeaturedTournamentsAsync();

        Assert.Equal(new[] { tournaments[3].Id, tournaments[2].Id, tournaments[0].Id, tournaments[1].Id }, response.TournamentIds);
        Assert.Equal(4, response.TournamentIds.Distinct().Count());

        tournaments[3].Status = TournamentDomain.TournamentStatus.Canceled;
        tournaments[0].Status = TournamentDomain.TournamentStatus.Canceled;
        tournaments[1].Status = TournamentDomain.TournamentStatus.Canceled;
        await dbContext.SaveChangesAsync();
        response = await service.GetFeaturedTournamentsAsync();

        Assert.Equal(new[] { tournaments[2].Id }, response.TournamentIds);
    }

    [Fact]
    public async Task PublicReadDeduplicatesMalformedPersistedIdsBeforeBackfilling()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = CreateOptions(database);
        await using var dbContext = new MercuriusDBContext(options);
        await dbContext.Database.MigrateAsync();
        var tournaments = Enumerable.Range(0, 4).Select(CreateTournament).ToArray();
        dbContext.Set<TournamentAggregate>().AddRange(tournaments);
        await dbContext.SaveChangesAsync();

        var duplicated = new[] { tournaments[0].Id, tournaments[0].Id, tournaments[1].Id, tournaments[2].Id };
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO tournament.featured_homepage_tournaments ("Id", "TournamentIds")
            VALUES (1, {duplicated})
            """);

        var response = await CreateService(dbContext).GetFeaturedTournamentsAsync();

        Assert.Equal(
            new[] { tournaments[0].Id, tournaments[1].Id, tournaments[2].Id, tournaments[3].Id },
            response.TournamentIds);
        Assert.Equal(4, response.TournamentIds.Distinct().Count());
        Assert.Equal(response.TournamentIds, response.Tournaments.Select(tournament => tournament.Id));
    }

    [Fact]
    public async Task ConcurrentReplacementsLeaveOneCompleteOrderedSelection()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = CreateOptions(database);
        Guid[] firstSelection;
        Guid[] secondSelection;
        await using (var dbContext = new MercuriusDBContext(options))
        {
            await dbContext.Database.MigrateAsync();
            var tournaments = Enumerable.Range(0, 8).Select(CreateTournament).ToArray();
            dbContext.Set<TournamentAggregate>().AddRange(tournaments);
            await dbContext.SaveChangesAsync();
            firstSelection = tournaments.Take(4).Select(tournament => tournament.Id).ToArray();
            secondSelection = tournaments.Skip(4).Select(tournament => tournament.Id).ToArray();
        }

        await using var firstContext = new MercuriusDBContext(options);
        await using var secondContext = new MercuriusDBContext(options);
        var results = await Task.WhenAll(
            CreateService(firstContext).ReplaceFeaturedTournamentsAsync(firstSelection),
            CreateService(secondContext).ReplaceFeaturedTournamentsAsync(secondSelection));

        Assert.All(results, result => Assert.Null(result));
        await using var verifyContext = new MercuriusDBContext(options);
        var storedOrder = (await CreateService(verifyContext).GetFeaturedTournamentsAsync()).TournamentIds;
        Assert.True(storedOrder.SequenceEqual(firstSelection) || storedOrder.SequenceEqual(secondSelection));
    }

    private static DbContextOptions<MercuriusDBContext> CreateOptions(PostgresTestDatabaseLease database) =>
        new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseNpgsql(database.ConnectionString)
            .Options;

    private static FeaturedHomepageTournamentService CreateService(
        MercuriusDBContext dbContext,
        ISponsorshipModule? sponsorshipModule = null) =>
        new(
            new TournamentDbContextAdapter<MercuriusDBContext>(dbContext),
            sponsorshipModule ?? TournamentTestSupport.CreateSponsorshipModule());

    private static TournamentAggregate CreateTournament(int order) => CreateTournament(order, TournamentDomain.TournamentStatus.Scheduled);

    private static TournamentAggregate CreateTournament(int order, TournamentDomain.TournamentStatus status) =>
        new($"Featured {order}", TournamentDomain.BracketType.SingleElimination, TournamentDomain.GameFormat.BestOf1,
            TournamentDomain.GameFormat.BestOf3, TournamentDomain.ParticipationMode.Individual, null,
            PlannedStart.AddDays(order), 30, 10)
        {
            Id = Guid.NewGuid(),
            ImageUrl = $"images/{order}.webp",
            Status = status
        };
}
