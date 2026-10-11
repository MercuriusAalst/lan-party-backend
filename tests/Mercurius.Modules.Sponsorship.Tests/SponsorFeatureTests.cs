using System.Text;
using System.Text.Json;
using Mercurius.LAN.API.Data;
using Mercurius.Modules.Tournament.Application.DTOs.Tournaments;
using Mercurius.Modules.Tournament.Infrastructure;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Shared.Exceptions;
using Mercurius.Modules.Media.Contracts;
using Mercurius.Modules.Tournament.Application.Services;
using Mercurius.Modules.Sponsorship;
using Mercurius.Modules.Sponsorship.Application;
using Mercurius.Modules.Sponsorship.Application.DTOs;
using Mercurius.Modules.Sponsorship.Application.Services;
using Mercurius.Modules.Sponsorship.Domain;
using Mercurius.Modules.Sponsorship.Infrastructure;
using SponsorContractContext = Mercurius.Modules.Sponsorship.Contracts.SponsorContext;
using SponsorContractTier = Mercurius.Modules.Sponsorship.Contracts.SponsorTier;
using Mercurius.Modules.Sponsorship.Contracts;
using Mercurius.Modules.Sponsorship.Contracts.V1;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Mercurius.TestInfrastructure;
using Platform.Eventing;

namespace Mercurius.Modules.Sponsorship.Tests;

public class SponsorFeatureTests
{
    [Fact]
    public async Task CreateSponsorAsync_PersistsTierAndDescription()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var sponsorService = CreateSponsorService(dbContext);

        var sponsor = await sponsorService.CreateSponsorAsync(new CreateSponsorDTO
        {
            Name = "Mercurius Tech",
            SponsorTier = SponsorTier.Presenting,
            InfoUrl = "https://example.test/mercurius-tech",
            Description = "Primary event partner.",
            Logo = CreateFormFile()
        });

        Assert.Equal(SponsorTier.Presenting, sponsor.SponsorTier);
        Assert.Equal("Primary event partner.", sponsor.Description);

        var storedSponsor = await dbContext.Set<Sponsor>().SingleAsync();
        Assert.Equal(SponsorTier.Presenting, storedSponsor.SponsorTier);
        Assert.Equal("Primary event partner.", storedSponsor.Description);
    }

    [Fact]
    public async Task UpdateSponsorAsync_UpdatesDescriptionAndTier()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var sponsorService = CreateSponsorService(dbContext);
        var sponsor = new Sponsor
        {
            Name = "Campus Fiber",
            SponsorTier = SponsorTier.Silver,
            InfoUrl = "https://example.test/campus-fiber",
            LogoUrl = "/images/campus-fiber.png"
        };
        dbContext.Set<Sponsor>().Add(sponsor);
        await dbContext.SaveChangesAsync();

        var updatedSponsor = await sponsorService.UpdateSponsorAsync(sponsor.Id, new UpdateSponsorDTO
        {
            Name = "Campus Fiber",
            SponsorTier = SponsorTier.Gold,
            InfoUrl = "https://example.test/campus-fiber",
            Description = "Network backbone partner."
        });

        Assert.Equal(SponsorTier.Gold, updatedSponsor.SponsorTier);
        Assert.Equal("Network backbone partner.", updatedSponsor.Description);
    }

    [Fact]
    public async Task SponsorLifecycleMutations_PersistMatchingOutboxEvents()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var sponsorService = CreateSponsorService(dbContext);

        var created = await sponsorService.CreateSponsorAsync(new CreateSponsorDTO
        {
            Name = "Mercurius Tech",
            SponsorTier = SponsorTier.Presenting,
            InfoUrl = "https://example.test/mercurius-tech",
            Description = "Primary event partner.",
            Logo = CreateFormFile()
        });
        await sponsorService.UpdateSponsorAsync(created.Id, new UpdateSponsorDTO
        {
            Name = "Mercurius Technology",
            SponsorTier = SponsorTier.Gold,
            InfoUrl = "https://example.test/mercurius-technology",
            Description = "Updated event partner."
        });
        await sponsorService.DeleteSponsorAsync(created.Id);

        var outbox = await dbContext.OutboxMessages.ToListAsync();
        Assert.Contains(outbox, message => message.EventType == typeof(SponsorCreated).FullName);
        Assert.Contains(outbox, message => message.EventType == typeof(SponsorUpdated).FullName);
        Assert.Contains(outbox, message => message.EventType == typeof(SponsorDeleted).FullName);

        var createdPayload = outbox.Single(message => message.EventType == typeof(SponsorCreated).FullName).Payload;
        using var document = JsonDocument.Parse(createdPayload);
        Assert.Equal(created.Id, document.RootElement.GetProperty("sponsorId").GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task SponsorshipModule_ReplacesAndRemovesPlacementWithMatchingOutboxEvents()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var tournament = CreateTournament();
        var sponsor = CreateSponsor(1, "Mercurius Tech", SponsorTier.Presenting);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        dbContext.Set<Sponsor>().Add(sponsor);
        await dbContext.SaveChangesAsync();
        var sponsorshipModule = CreateSponsorshipModule(dbContext);

        await sponsorshipModule.ReplaceSponsorPlacementAsync(
            new TournamentId(tournament.Id),
            new SponsorPlacementInput(
                new SponsorId(sponsor.Id),
                SponsorContractContext.TournamentPartner,
                "Presented by Mercurius Tech",
                "Main stage and stream support",
                1));

        var placement = await sponsorshipModule.GetSponsorPlacementAsync(new TournamentId(tournament.Id));
        Assert.NotNull(placement);
        Assert.Equal(sponsor.Id, placement.Sponsor.Id.Value);
        Assert.Equal(SponsorContractContext.TournamentPartner, placement.Context);

        await sponsorshipModule.ReplaceSponsorPlacementAsync(new TournamentId(tournament.Id), null);

        Assert.Null(await sponsorshipModule.GetSponsorPlacementAsync(new TournamentId(tournament.Id)));
        var placementEvents = await dbContext.OutboxMessages
            .Where(message => message.EventType == typeof(TournamentSponsorPlacementChanged).FullName)
            .ToListAsync();
        Assert.Equal(2, placementEvents.Count);
        Assert.Contains(placementEvents, message => JsonDocument.Parse(message.Payload).RootElement
            .GetProperty("sponsorId").GetProperty("value").GetInt32() == sponsor.Id);
        Assert.Contains(placementEvents, message => JsonDocument.Parse(message.Payload).RootElement
            .GetProperty("placementId").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public void SponsorshipModel_PreservesExistingTablesAndCascadeRelationships()
    {
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseNpgsql("Host=localhost;Database=translation-only")
            .Options;
        using var dbContext = new MercuriusDBContext(options);
        var sponsorType = dbContext.Model.FindEntityType(typeof(Sponsor));
        var placementType = dbContext.Model.FindEntityType(typeof(TournamentSponsorPlacement));

        Assert.NotNull(sponsorType);
        Assert.NotNull(placementType);
        Assert.Equal("sponsorship", sponsorType.GetSchema());
        Assert.Equal("sponsors", sponsorType.GetTableName());
        Assert.Equal("sponsorship", placementType.GetSchema());
        Assert.Equal("tournament_sponsor_placements", placementType.GetTableName());
        Assert.Contains(
            placementType.GetForeignKeys(),
            foreignKey =>
                foreignKey.PrincipalEntityType.ClrType == typeof(Sponsor) &&
                foreignKey.DeleteBehavior == DeleteBehavior.Cascade);
        Assert.Contains(
            placementType.GetForeignKeys(),
            foreignKey =>
                foreignKey.PrincipalEntityType.ClrType == typeof(TournamentAggregate) &&
                foreignKey.DeleteBehavior == DeleteBehavior.Cascade);
        Assert.Contains(placementType.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Single().Name == nameof(TournamentSponsorPlacement.TournamentId));
    }

    [Fact]
    public async Task ReplaceSponsorPlacementsAsync_ReplacesExistingPlacementAndReturnsSponsorData()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var tournament = CreateTournament();
        var presentingSponsor = CreateSponsor(1, "Mercurius Tech", SponsorTier.Presenting);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        dbContext.Set<Sponsor>().Add(presentingSponsor);
        await dbContext.SaveChangesAsync();

        var sponsorPlacement = CreateSponsorPlacementSummary(tournament.Id, presentingSponsor, SponsorContractContext.CateringPartner, null, null, 99);
        var sponsorshipModule = new RecordingSponsorshipModule(
            [presentingSponsor.Id],
            sponsorPlacement);
        var identityModule = SponsorshipTournamentTestDoubles.CreateIdentityModule();
        var teamsModule = SponsorshipTournamentTestDoubles.CreateTeamsModule();
        var service = new TournamentService(
            new TournamentDbContextAdapter<MercuriusDBContext>(dbContext),
            new StubMatchModeratorFactory(),
            new StubMediaModule(),
            identityModule,
            sponsorshipModule,
            new TournamentDtoMapper(
                new RegistrationMappingContextBuilder(identityModule, teamsModule),
                sponsorshipModule),
            SponsorshipTournamentTestDoubles.CreateModuleEventPublisher(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TournamentService>.Instance);
        var updatedTournament = await service.ReplaceSponsorPlacementsAsync(tournament.Id, new ReplaceTournamentSponsorsDTO
        {
            SponsorPlacements =
            [
                new TournamentSponsorPlacementInputDTO
                {
                    SponsorId = presentingSponsor.Id,
                    Context = SponsorContractContext.TournamentPartner,
                    Headline = "Presented by Mercurius Tech",
                    SupportLine = "Main stage and stream support",
                    DisplayOrder = 1
                }
            ]
        });

        Assert.NotNull(updatedTournament.SponsorPlacement);
        Assert.Equal(SponsorContractContext.TournamentPartner, updatedTournament.SponsorPlacement.Context);
        Assert.Equal("Mercurius Tech", updatedTournament.SponsorPlacement.SponsorName);
        Assert.Equal(SponsorContractTier.Presenting, updatedTournament.SponsorPlacement.SponsorTier);
        Assert.Equal("Presented by Mercurius Tech", updatedTournament.SponsorPlacement.Headline);
        Assert.NotNull(sponsorshipModule.ReplacedPlacement);
        Assert.Equal(SponsorContractContext.TournamentPartner, sponsorshipModule.ReplacedPlacement!.Context);
    }

    [Fact]
    public async Task ReplaceSponsorPlacementsAsync_ThrowsWhenMoreThanOneSponsorIsProvided()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var tournament = CreateTournament();
        var presentingSponsor = CreateSponsor(1, "Mercurius Tech", SponsorTier.Presenting);
        var prizeSponsor = CreateSponsor(2, "Campus Fiber", SponsorTier.Gold);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        dbContext.Set<Sponsor>().AddRange(presentingSponsor, prizeSponsor);
        await dbContext.SaveChangesAsync();

        var service = new TournamentService(
            new TournamentDbContextAdapter<MercuriusDBContext>(dbContext),
            new StubMatchModeratorFactory(),
            new StubMediaModule(),
            SponsorshipTournamentTestDoubles.CreateIdentityModule(),
            new RecordingSponsorshipModule([presentingSponsor.Id, prizeSponsor.Id]),
            new TournamentDtoMapper(
                new RegistrationMappingContextBuilder(
                    SponsorshipTournamentTestDoubles.CreateIdentityModule(),
                    SponsorshipTournamentTestDoubles.CreateTeamsModule()),
                new RecordingSponsorshipModule([presentingSponsor.Id, prizeSponsor.Id])),
            SponsorshipTournamentTestDoubles.CreateModuleEventPublisher(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TournamentService>.Instance);

        var exception = await Assert.ThrowsAsync<ValidationException>(() => service.ReplaceSponsorPlacementsAsync(tournament.Id, new ReplaceTournamentSponsorsDTO
        {
            SponsorPlacements =
            [
                new TournamentSponsorPlacementInputDTO
                {
                    SponsorId = presentingSponsor.Id,
                    Context = SponsorContractContext.TournamentPartner,
                    DisplayOrder = 1
                },
                new TournamentSponsorPlacementInputDTO
                {
                    SponsorId = prizeSponsor.Id,
                    Context = SponsorContractContext.PrizePartner,
                    DisplayOrder = 2
                }
            ]
        }));

        Assert.Equal("A tournament can only have one sponsor.", exception.Message);
    }

    [Fact]
    public async Task ReplaceSponsorPlacementsAsync_ThrowsWhenSponsorDoesNotExist()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        dbContext.Set<TournamentAggregate>().Add(CreateTournament());
        await dbContext.SaveChangesAsync();

        var service = new TournamentService(
            new TournamentDbContextAdapter<MercuriusDBContext>(dbContext),
            new StubMatchModeratorFactory(),
            new StubMediaModule(),
            SponsorshipTournamentTestDoubles.CreateIdentityModule(),
            new RecordingSponsorshipModule([]),
            new TournamentDtoMapper(
                new RegistrationMappingContextBuilder(
                    SponsorshipTournamentTestDoubles.CreateIdentityModule(),
                    SponsorshipTournamentTestDoubles.CreateTeamsModule()),
                new RecordingSponsorshipModule([])),
            SponsorshipTournamentTestDoubles.CreateModuleEventPublisher(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TournamentService>.Instance);
        var tournamentId = await dbContext.Set<TournamentAggregate>().Select(tournament => tournament.Id).SingleAsync();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => service.ReplaceSponsorPlacementsAsync(tournamentId, new ReplaceTournamentSponsorsDTO
        {
            SponsorPlacements =
            [
                new TournamentSponsorPlacementInputDTO
                {
                    SponsorId = 404,
                    Context = SponsorContractContext.TournamentPartner,
                    DisplayOrder = 1
                }
            ]
        }));

        Assert.Equal("Sponsor with ID 404 not found", exception.Message);
    }

    private static IFormFile CreateFormFile()
    {
        var bytes = Encoding.UTF8.GetBytes("logo");
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "logo", "logo.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };
    }

    private static SponsorService CreateSponsorService(MercuriusDBContext dbContext)
    {
        var sponsorshipDbContext = new SponsorshipDbContextAdapter<MercuriusDBContext>(dbContext);
        return new SponsorService(
            sponsorshipDbContext,
            new StubMediaModule(),
            new SponsorshipOutboxWriter(sponsorshipDbContext, new ModuleEventPublisher(dbContext)),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SponsorService>.Instance);
    }

    private static SponsorshipModuleFacade CreateSponsorshipModule(MercuriusDBContext dbContext)
    {
        var sponsorshipDbContext = new SponsorshipDbContextAdapter<MercuriusDBContext>(dbContext);
        return new SponsorshipModuleFacade(
            sponsorshipDbContext,
            new SponsorshipOutboxWriter(sponsorshipDbContext, new ModuleEventPublisher(dbContext)));
    }

    private static TournamentAggregate CreateTournament()
    {
        return new TournamentAggregate("Counter-Strike 2", BracketType.SingleElimination, GameFormat.BestOf3, GameFormat.BestOf5, ParticipationMode.Team, 5)
        {
            Id = Guid.NewGuid()
        };
    }

    private static Sponsor CreateSponsor(int id, string name, SponsorTier tier)
    {
        return new Sponsor
        {
            Id = id,
            Name = name,
            SponsorTier = tier,
            LogoUrl = $"/images/{name.ToLowerInvariant().Replace(' ', '-')}.png",
            InfoUrl = $"https://example.test/{name.ToLowerInvariant().Replace(' ', '-')}",
            Description = $"{name} description"
        };
    }

    private static SponsorPlacementSummary CreateSponsorPlacementSummary(
        Guid tournamentId,
        Sponsor sponsor,
        SponsorContractContext context,
        string? headline,
        string? supportLine,
        int displayOrder)
    {
        return new SponsorPlacementSummary(
            new SponsorPlacementId(1),
            new TournamentId(tournamentId),
            new SponsorSummary(
                new SponsorId(sponsor.Id),
                sponsor.Name,
                (SponsorContractTier)sponsor.SponsorTier,
                sponsor.LogoUrl,
                sponsor.InfoUrl,
                sponsor.Description),
            context,
            headline,
            supportLine,
            displayOrder);
    }

    private sealed class StubMediaModule : IMediaModule
    {
        public Task<StoredMediaAsset> SaveImageAsync(MediaUpload upload, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new StoredMediaAsset("/images/mock-upload.png"));
        }

        public Task DeleteImageAsync(string? imageUrl, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class StubMatchModeratorFactory : IMatchModeratorFactory
    {
        public IMatchModerator GetMatchModerator(BracketType bracketType)
        {
            return new StubMatchModerator();
        }
    }

    private sealed class StubMatchModerator : IMatchModerator
    {
        public IEnumerable<Match> GenerateMatchesForTournament(TournamentAggregate tournament)
        {
            return [];
        }

        public void EnsureCanComplete(TournamentAggregate tournament)
        {
        }

        public void DeterminePlacements(TournamentAggregate tournament)
        {
        }
    }

    private sealed class RecordingSponsorshipModule(
        IReadOnlyCollection<int> knownSponsorIds,
        SponsorPlacementSummary? currentPlacement = null) : ISponsorshipModule
    {
        private SponsorPlacementSummary? _currentPlacement = currentPlacement;

        public SponsorPlacementInput? ReplacedPlacement { get; private set; }

        public Task<SponsorSummary?> GetSponsorSummaryAsync(SponsorId sponsorId, CancellationToken cancellationToken = default)
            => Task.FromResult<SponsorSummary?>(null);

        public Task<IReadOnlyList<SponsorSearchDocument>> GetSponsorSearchDocumentsPageAsync(
            SponsorId? afterId,
            int pageSize,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SponsorSearchDocument>>([]);

        public Task<IReadOnlyDictionary<TournamentId, SponsorPlacementSummary>> GetSponsorPlacementsAsync(
            IReadOnlyCollection<TournamentId> tournamentIds,
            CancellationToken cancellationToken = default)
        {
            if (_currentPlacement is null)
                return Task.FromResult<IReadOnlyDictionary<TournamentId, SponsorPlacementSummary>>(new Dictionary<TournamentId, SponsorPlacementSummary>());

            return Task.FromResult<IReadOnlyDictionary<TournamentId, SponsorPlacementSummary>>(
                new Dictionary<TournamentId, SponsorPlacementSummary> { [_currentPlacement.TournamentId] = _currentPlacement });
        }

        public Task<SponsorPlacementSummary?> GetSponsorPlacementAsync(TournamentId tournamentId, CancellationToken cancellationToken = default)
            => Task.FromResult(_currentPlacement?.TournamentId == tournamentId ? _currentPlacement : null);

        public Task ReplaceSponsorPlacementAsync(TournamentId tournamentId, SponsorPlacementInput? placement, CancellationToken cancellationToken = default)
        {
            if (placement is not null && !knownSponsorIds.Contains(placement.SponsorId.Value))
                throw new NotFoundException($"Sponsor with ID {placement.SponsorId.Value} not found");

            ReplacedPlacement = placement;
            _currentPlacement = placement is null
                ? null
                : new SponsorPlacementSummary(
                    new SponsorPlacementId(_currentPlacement?.Id.Value ?? 1),
                    tournamentId,
                    _currentPlacement?.Sponsor ?? new SponsorSummary(
                        placement.SponsorId,
                        string.Empty,
                        SponsorContractTier.Bronze,
                        string.Empty,
                        string.Empty,
                        string.Empty),
                    placement.Context,
                    placement.Headline,
                    placement.SupportLine,
                    placement.DisplayOrder);
            return Task.CompletedTask;
        }
    }
}
