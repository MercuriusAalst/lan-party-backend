using Mercurius.LAN.API.Data;
using Mercurius.LAN.API.Migrations;
using Mercurius.Modules.Shared.Exceptions;
using Mercurius.Modules.Tournament.Application.DTOs.Tournaments;
using Mercurius.Modules.Tournament.Application.Services;
using Mercurius.Modules.Tournament.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using TournamentBracketType = Mercurius.Modules.Tournament.Contracts.BracketType;
using TournamentGameFormat = Mercurius.Modules.Tournament.Contracts.GameFormat;
using TournamentParticipationMode = Mercurius.Modules.Tournament.Contracts.ParticipationMode;

namespace Mercurius.Modules.Tournament.Tests;

public class TournamentPrizeContactTests
{
    private static readonly DateTime PlannedStart = new(2026, 10, 15, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task CreateTournamentAsync_TrimsPrizeTextAndPersistsValidatedContact()
    {
        await using var dbContext = CreateDbContext();
        var contact = CreateUser("contact-admin");
        var service = CreateService(dbContext, [contact], [contact.Id]);

        var created = await service.CreateTournamentAsync(new CreateTournamentDTO
        {
            Name = "Prize Cup",
            BracketType = TournamentBracketType.SingleElimination,
            Format = TournamentGameFormat.BestOf1,
            FinalsFormat = TournamentGameFormat.BestOf3,
            ParticipationMode = TournamentParticipationMode.Individual,
            Image = CreateImage(),
            PlannedStartTime = PlannedStart,
            AverageGameDurationMinutes = 30,
            RoundBreakDurationMinutes = 10,
            AssignedAdminUserId = contact.Id.ToString(),
            FirstPlacePrize = "  Gold trophy  ",
            SecondPlacePrize = "  ",
            ThirdPlacePrize = null
        });

        var saved = await dbContext.Set<TournamentAggregate>().SingleAsync(tournament => tournament.Id == created.Id);
        Assert.Equal("Gold trophy", saved.FirstPlacePrize);
        Assert.Null(saved.SecondPlacePrize);
        Assert.Null(saved.ThirdPlacePrize);
        Assert.Equal(contact.Id, saved.AssignedAdminUserId);
        Assert.Equal("Gold trophy", created.FirstPlacePrize);
        Assert.Equal(contact.Id, created.ContactAdmin?.Id);
        Assert.Equal("contact-admin", created.ContactAdmin?.Username);
    }

    [Fact]
    public async Task UpdateTournamentAsync_PreservesOmittedMetadataAndClearsExplicitBlankPrize()
    {
        await using var dbContext = CreateDbContext();
        var contact = CreateUser("contact-admin");
        var tournament = CreateTournament(contact.Id);
        tournament.FirstPlacePrize = "Original first";
        tournament.SecondPlacePrize = "Original second";
        tournament.ThirdPlacePrize = "Original third";
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext, [contact], [contact.Id]);
        var update = CreateUpdate(tournament);
        update.FirstPlacePrizeSpecified = true;
        update.FirstPlacePrize = "   ";
        update.ThirdPlacePrizeSpecified = true;
        update.ThirdPlacePrize = "  Bronze medals  ";

        var result = await service.UpdateTournamentAsync(tournament.Id, update);

        Assert.Null(tournament.FirstPlacePrize);
        Assert.Equal("Original second", tournament.SecondPlacePrize);
        Assert.Equal("Bronze medals", tournament.ThirdPlacePrize);
        Assert.Equal(contact.Id, tournament.AssignedAdminUserId);
        Assert.Equal(contact.Id, result.ContactAdmin?.Id);
    }

    [Fact]
    public async Task UpdateTournamentAsync_RejectsInvalidAdminAndOverlongPrizeWithoutSaving()
    {
        await using var dbContext = CreateDbContext();
        var existingContact = CreateUser("existing-admin");
        var tournament = CreateTournament(existingContact.Id);
        tournament.FirstPlacePrize = "Existing prize";
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();

        var invalidAdminService = CreateService(dbContext, [existingContact], []);
        var invalidAdminUpdate = CreateUpdate(tournament);
        invalidAdminUpdate.AssignedAdminUserIdSpecified = true;
        invalidAdminUpdate.AssignedAdminUserId = Guid.NewGuid().ToString();
        await Assert.ThrowsAsync<ValidationException>(() => invalidAdminService.UpdateTournamentAsync(tournament.Id, invalidAdminUpdate));
        Assert.Equal(existingContact.Id, tournament.AssignedAdminUserId);
        Assert.Equal("Existing prize", tournament.FirstPlacePrize);

        var longPrizeService = CreateService(dbContext, [existingContact], [existingContact.Id]);
        var longPrizeUpdate = CreateUpdate(tournament);
        longPrizeUpdate.FirstPlacePrizeSpecified = true;
        longPrizeUpdate.FirstPlacePrize = new string('x', 201);
        await Assert.ThrowsAsync<ValidationException>(() => longPrizeService.UpdateTournamentAsync(tournament.Id, longPrizeUpdate));
        Assert.Equal("Existing prize", tournament.FirstPlacePrize);
    }

    [Fact]
    public async Task UpdateTournamentAsync_PropagatesAuth0UnavailableWithoutSaving()
    {
        await using var dbContext = CreateDbContext();
        var contact = CreateUser("old-admin");
        var tournament = CreateTournament(contact.Id);
        tournament.FirstPlacePrize = "Existing prize";
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext, [contact], [], new ServiceUnavailableException("Auth0 is unavailable."));
        var update = CreateUpdate(tournament);
        update.AssignedAdminUserIdSpecified = true;
        update.AssignedAdminUserId = Guid.NewGuid().ToString();
        update.FirstPlacePrizeSpecified = true;
        update.FirstPlacePrize = "Replacement prize";

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => service.UpdateTournamentAsync(tournament.Id, update));
        Assert.Equal(contact.Id, tournament.AssignedAdminUserId);
        Assert.Equal("Existing prize", tournament.FirstPlacePrize);
    }

    [Fact]
    public async Task UpdateTournamentAsync_ClearsContactWhenExplicitlySubmittedNull()
    {
        await using var dbContext = CreateDbContext();
        var contact = CreateUser("contact-admin");
        var tournament = CreateTournament(contact.Id);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext, [contact], []);
        var update = CreateUpdate(tournament);
        update.AssignedAdminUserIdSpecified = true;
        update.AssignedAdminUserId = null;

        var result = await service.UpdateTournamentAsync(tournament.Id, update);

        Assert.Null(tournament.AssignedAdminUserId);
        Assert.Null(result.ContactAdmin);
    }

    [Fact]
    public async Task CreateTournamentAsync_TreatsBlankContactTextAsNoAssignment()
    {
        await using var dbContext = CreateDbContext();
        var contact = CreateUser("contact-admin");
        var service = CreateService(dbContext, [contact], [contact.Id]);

        var created = await service.CreateTournamentAsync(new CreateTournamentDTO
        {
            Name = "Blank Contact Cup",
            BracketType = TournamentBracketType.SingleElimination,
            Format = TournamentGameFormat.BestOf1,
            FinalsFormat = TournamentGameFormat.BestOf3,
            ParticipationMode = TournamentParticipationMode.Individual,
            Image = CreateImage(),
            PlannedStartTime = PlannedStart,
            AverageGameDurationMinutes = 30,
            RoundBreakDurationMinutes = 10,
            AssignedAdminUserId = "   "
        });

        Assert.Null(created.ContactAdmin);
        var saved = await dbContext.Set<TournamentAggregate>().SingleAsync(tournament => tournament.Id == created.Id);
        Assert.Null(saved.AssignedAdminUserId);
    }

    [Fact]
    public async Task CreateTournamentAsync_RejectsMalformedContactWithoutSaving()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext, [], []);

        var exception = await Assert.ThrowsAsync<ValidationException>(() => service.CreateTournamentAsync(new CreateTournamentDTO
        {
            Name = "Malformed Contact Cup",
            BracketType = TournamentBracketType.SingleElimination,
            Format = TournamentGameFormat.BestOf1,
            FinalsFormat = TournamentGameFormat.BestOf3,
            ParticipationMode = TournamentParticipationMode.Individual,
            Image = CreateImage(),
            PlannedStartTime = PlannedStart,
            AverageGameDurationMinutes = 30,
            RoundBreakDurationMinutes = 10,
            AssignedAdminUserId = "not-a-guid"
        }));

        Assert.Contains("AssignedAdminUserId", exception.Message);
        Assert.Empty(dbContext.Set<TournamentAggregate>());
    }

    [Fact]
    public async Task UpdateTournamentAsync_RejectsMalformedContactWithoutSaving()
    {
        await using var dbContext = CreateDbContext();
        var contact = CreateUser("contact-admin");
        var tournament = CreateTournament(contact.Id);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext, [contact], [contact.Id]);
        var update = CreateUpdate(tournament);
        update.AssignedAdminUserIdSpecified = true;
        update.AssignedAdminUserId = "not-a-guid";

        var exception = await Assert.ThrowsAsync<ValidationException>(() => service.UpdateTournamentAsync(tournament.Id, update));

        Assert.Contains("AssignedAdminUserId", exception.Message);
        Assert.Equal(contact.Id, tournament.AssignedAdminUserId);
    }

    [Fact]
    public void TournamentResponse_ContactContainsOnlyPublicSummaryFields()
    {
        var contact = CreateUser("public-admin");
        contact.DiscordId = "private-discord";
        contact.SteamId = "private-steam";
        contact.RiotId = "private-riot";
        var tournament = CreateTournament(contact.Id);
        tournament.FirstPlacePrize = "Prize";

        var response = tournament.ToGetTournamentDTO([contact]);
        var serialized = System.Text.Json.JsonSerializer.Serialize(response);
        using var json = System.Text.Json.JsonDocument.Parse(serialized);
        var contactJson = json.RootElement.GetProperty("ContactAdmin");

        Assert.Equal(3, contactJson.EnumerateObject().Count());
        Assert.True(contactJson.TryGetProperty("Id", out _));
        Assert.True(contactJson.TryGetProperty("Username", out _));
        Assert.True(contactJson.TryGetProperty("DisplayName", out _));
        Assert.Equal("public-admin", contactJson.GetProperty("Username").GetString());
        Assert.Equal("Contact Admin", contactJson.GetProperty("DisplayName").GetString());
        Assert.False(contactJson.TryGetProperty("DiscordId", out _));
        Assert.False(contactJson.TryGetProperty("SteamId", out _));
        Assert.False(contactJson.TryGetProperty("RiotId", out _));
        Assert.False(json.RootElement.TryGetProperty("AssignedAdminUserId", out _));
        Assert.Equal("Prize", json.RootElement.GetProperty("FirstPlacePrize").GetString());
    }

    [Fact]
    public void TournamentResponse_UsesIncompleteProfileFallbackForContactWithoutPublicProfile()
    {
        var contact = CreateUser("hidden-admin");
        contact.Username = null;
        contact.NormalizedUsername = null;
        contact.Firstname = null;
        contact.Lastname = null;
        var tournament = CreateTournament(contact.Id);

        var response = tournament.ToGetTournamentDTO([contact]);
        var serialized = System.Text.Json.JsonSerializer.Serialize(response);

        Assert.Equal("Incomplete profile", response.ContactAdmin?.Username);
        Assert.Equal("Incomplete profile", response.ContactAdmin?.DisplayName);
        Assert.DoesNotContain("hidden-admin@example.test", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("auth0|hidden-admin", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TournamentPrizeMigrationAddsNullableColumnsAndModelMatches()
    {
        var migration = new TournamentPrizesAndContactAdmin();
        var columns = migration.UpOperations.OfType<AddColumnOperation>()
            .Where(operation => operation.Table == "tournaments")
            .ToDictionary(operation => operation.Name);

        Assert.Equal(3, columns.Count);
        foreach (var property in new[] { "FirstPlacePrize", "SecondPlacePrize", "ThirdPlacePrize" })
        {
            Assert.True(columns[property].IsNullable);
            Assert.Equal("character varying(200)", columns[property].ColumnType);
            Assert.Equal(200, columns[property].MaxLength);
        }

        using var dbContext = CreateDbContext();
        var entity = dbContext.Model.FindEntityType(typeof(TournamentAggregate))!;
        Assert.All(new[] { "FirstPlacePrize", "SecondPlacePrize", "ThirdPlacePrize" }, propertyName =>
        {
            var property = entity.FindProperty(propertyName)!;
            Assert.True(property.IsNullable);
            Assert.Equal(200, property.GetMaxLength());
        });
    }

    private static TournamentService CreateService(
        MercuriusDBContext dbContext,
        IReadOnlyCollection<User> users,
        IReadOnlyCollection<Guid> adminUserIds,
        Exception? adminLookupFailure = null)
    {
        var identityModule = TournamentTestSupport.CreateIdentityModule(
            users,
            adminUserIds: adminUserIds,
            adminLookupFailure: adminLookupFailure);
        return new TournamentService(
            new TournamentDbContextAdapter<MercuriusDBContext>(dbContext),
            new NoopMatchModeratorFactory(),
            TournamentTestSupport.CreateMediaModule(),
            identityModule,
            TournamentTestSupport.CreateSponsorshipModule(),
            TournamentTestSupport.CreateMapper(users),
            TournamentTestSupport.CreateModuleEventPublisher(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TournamentService>.Instance);
    }

    private static TournamentAggregate CreateTournament(Guid? contactAdminUserId = null)
    {
        return new TournamentAggregate(
            "Prize Cup",
            Mercurius.Modules.Tournament.Domain.BracketType.SingleElimination,
            Mercurius.Modules.Tournament.Domain.GameFormat.BestOf1,
            Mercurius.Modules.Tournament.Domain.GameFormat.BestOf3,
            Mercurius.Modules.Tournament.Domain.ParticipationMode.Individual,
            null,
            PlannedStart,
            30,
            10)
        {
            Id = Guid.NewGuid(),
            AssignedAdminUserId = contactAdminUserId
        };
    }

    private static UpdateTournamentDTO CreateUpdate(TournamentAggregate tournament)
    {
        return new UpdateTournamentDTO
        {
            Name = tournament.Name,
            BracketType = (TournamentBracketType)tournament.BracketType,
            Format = (TournamentGameFormat)tournament.Format,
            FinalsFormat = (TournamentGameFormat)tournament.FinalsFormat,
            ParticipationMode = (TournamentParticipationMode)tournament.ParticipationMode,
            TeamSize = tournament.TeamSize,
            PlannedStartTime = tournament.PlannedStartTime,
            AverageGameDurationMinutes = tournament.AverageGameDurationMinutes,
            RoundBreakDurationMinutes = tournament.RoundBreakDurationMinutes
        };
    }

    private static User CreateUser(string username)
    {
        return new User
        {
            Id = Guid.NewGuid(),
            Auth0UserId = $"auth0|{username}",
            Username = username,
            NormalizedUsername = username.ToLowerInvariant(),
            Firstname = "Contact",
            Lastname = "Admin",
            Email = $"{username}@example.test"
        };
    }

    private static FormFile CreateImage()
    {
        var bytes = new byte[] { 1, 2, 3 };
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "image", "tournament.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };
    }

    private static MercuriusDBContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new MercuriusDBContext(options);
    }

    private sealed class NoopMatchModeratorFactory : IMatchModeratorFactory
    {
        public IMatchModerator GetMatchModerator(Mercurius.Modules.Tournament.Domain.BracketType bracketType) =>
            throw new NotSupportedException("Tournament prize tests do not start matches.");
    }
}
