using System.Text;
using Mercurius.LAN.API.Data;
using Mercurius.Modules.Media.Contracts;
using Mercurius.Modules.Sponsorship.Application;
using Mercurius.Modules.Sponsorship.Application.DTOs;
using Mercurius.Modules.Sponsorship.Application.Services;
using Mercurius.Modules.Sponsorship.Contracts;
using Mercurius.Modules.Sponsorship.Contracts.V1;
using Mercurius.Modules.Sponsorship.Domain;
using Mercurius.Modules.Sponsorship.Infrastructure;
using Mercurius.Modules.Shared.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Mercurius.TestInfrastructure;
using Platform.Eventing;

namespace Mercurius.Modules.Sponsorship.Tests;

public class SponsorMediaLifecycleTests
{
    private const string OriginalLogoUrl = "/images/original.webp";
    private const string OriginalLightLogoUrl = "/images/original-light.webp";
    private const string NewLogoUrl = "/images/new.webp";
    private const string NewLightLogoUrl = "/images/new-light.webp";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CreateSponsorAsync_WhenStateOrOutboxPersistenceFails_CompensatesOnlyNewLogo(
        int failingSaveCall)
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var operations = new List<string>();
        var failure = new InvalidOperationException($"save {failingSaveCall} failed");
        var mediaModule = new RecordingMediaModule(NewLogoUrl, operations);
        var service = CreateSponsorService(
            dbContext,
            mediaModule,
            new RecordingLogger<SponsorService>(),
            operations,
            failingSaveCall,
            failure);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateSponsorAsync(CreateSponsorDto()));

        Assert.Same(failure, actual);
        Assert.Equal(MediaPurpose.SponsorLogo, mediaModule.SavedPurpose);
        var deletedImage = Assert.Single(mediaModule.DeletedImages);
        Assert.Equal(NewLogoUrl, deletedImage.ImageUrl);
        Assert.Equal(CancellationToken.None, deletedImage.CancellationToken);
        Assert.Equal($"save-{failingSaveCall}-failed", operations[^2]);
        Assert.Equal($"delete:{NewLogoUrl}", operations[^1]);
        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Set<Sponsor>().AnyAsync());
        Assert.False(await dbContext.OutboxMessages.AnyAsync());
    }

    [Fact]
    public async Task CreateSponsorAsync_WithBothLogosAndFailedCommit_CompensatesBothLogos()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var operations = new List<string>();
        var failure = new InvalidOperationException("create persistence failed");
        var mediaModule = new RecordingMediaModule(
            NewLogoUrl,
            operations,
            [NewLogoUrl, NewLightLogoUrl]);
        var service = CreateSponsorService(
            dbContext,
            mediaModule,
            new RecordingLogger<SponsorService>(),
            operations,
            failingSaveCall: 1,
            failure);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateSponsorAsync(CreateSponsorDto(CreateFormFile())));

        Assert.Same(failure, actual);
        Assert.Equal(
            [NewLogoUrl, NewLightLogoUrl],
            mediaModule.DeletedImages.Select(deletion => deletion.ImageUrl));
        Assert.All(
            mediaModule.DeletedImages,
            deletion => Assert.Equal(CancellationToken.None, deletion.CancellationToken));
        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Set<Sponsor>().AnyAsync());
        Assert.False(await dbContext.OutboxMessages.AnyAsync());
    }

    [Fact]
    public async Task CreateSponsorAsync_WhenLightLogoStorageFails_CompensatesDefaultLogo()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var operations = new List<string>();
        var saveFailure = new IOException("light logo storage failed");
        var mediaModule = new RecordingMediaModule(NewLogoUrl, operations)
        {
            FailingSaveCall = 2,
            SaveException = saveFailure
        };
        var service = CreateSponsorService(
            dbContext,
            mediaModule,
            new RecordingLogger<SponsorService>(),
            operations);

        var actual = await Assert.ThrowsAsync<IOException>(() =>
            service.CreateSponsorAsync(CreateSponsorDto(CreateFormFile())));

        Assert.Same(saveFailure, actual);
        var deletion = Assert.Single(mediaModule.DeletedImages);
        Assert.Equal(NewLogoUrl, deletion.ImageUrl);
        Assert.Equal(CancellationToken.None, deletion.CancellationToken);
        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Set<Sponsor>().AnyAsync());
    }

    [Fact]
    public async Task CreateSponsorAsync_WhenCancelledAfterStorage_PreservesCancellationWhenCompensationFails()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        using var cancellationSource = new CancellationTokenSource();
        var operations = new List<string>();
        var cancellation = new OperationCanceledException("Sponsor creation was cancelled.", cancellationSource.Token);
        var cleanupFailure = new IOException("cleanup failed");
        var logger = new RecordingLogger<SponsorService>();
        var mediaModule = new RecordingMediaModule(NewLogoUrl, operations)
        {
            DeleteException = cleanupFailure
        };
        var service = CreateSponsorService(
            dbContext,
            mediaModule,
            logger,
            operations,
            failingSaveCall: 1,
            cancellation,
            beforeSave: cancellationSource.Cancel);

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.CreateSponsorAsync(CreateSponsorDto(), cancellationSource.Token));

        Assert.Same(cancellation, actual);
        var deletedImage = Assert.Single(mediaModule.DeletedImages);
        Assert.Equal(NewLogoUrl, deletedImage.ImageUrl);
        Assert.Equal(CancellationToken.None, deletedImage.CancellationToken);
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Same(cleanupFailure, warning.Exception);
        Assert.Contains("compensate an uncommitted Sponsor logo", warning.Message);
        Assert.Contains(NewLogoUrl, warning.Message);
        dbContext.ChangeTracker.Clear();
        Assert.Empty(await dbContext.Set<Sponsor>().ToListAsync());
        Assert.Empty(await dbContext.OutboxMessages.ToListAsync());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UpdateSponsorAsync_WhenStateOrOutboxPersistenceFails_CompensatesNewLogoAndNeverDeletesPreviousLogo(
        int failingSaveCall)
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var sponsor = await SeedSponsorAsync(dbContext);
        var operations = new List<string>();
        var failure = new InvalidOperationException("update persistence failed");
        var mediaModule = new RecordingMediaModule(NewLogoUrl, operations);
        var service = CreateSponsorService(
            dbContext,
            mediaModule,
            new RecordingLogger<SponsorService>(),
            operations,
            failingSaveCall,
            failure);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateSponsorAsync(sponsor.Id, CreateUpdateDto(CreateFormFile())));

        Assert.Same(failure, actual);
        var deletedImage = Assert.Single(mediaModule.DeletedImages);
        Assert.Equal(NewLogoUrl, deletedImage.ImageUrl);
        Assert.Equal(CancellationToken.None, deletedImage.CancellationToken);
        Assert.DoesNotContain(
            mediaModule.DeletedImages,
            deletion => deletion.ImageUrl == OriginalLogoUrl);
        await AssertSponsorStateWasRolledBackAsync(dbContext, sponsor.Id);
    }

    [Fact]
    public async Task UpdateSponsorAsync_WhenReplacementCommits_RetiresPreviousLogoAfterOutboxPersistence()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var sponsor = await SeedSponsorAsync(dbContext);
        var operations = new List<string>();
        var cleanupFailure = new IOException("replacement cleanup failed");
        var logger = new RecordingLogger<SponsorService>();
        var mediaModule = new RecordingMediaModule(NewLogoUrl, operations)
        {
            DeleteException = cleanupFailure
        };
        var service = CreateSponsorService(dbContext, mediaModule, logger, operations);

        var result = await service.UpdateSponsorAsync(
            sponsor.Id,
            CreateUpdateDto(CreateFormFile()));

        Assert.Equal(NewLogoUrl, result.LogoUrl);
        Assert.Equal(
            [
                "media-save",
                "save-1-started",
                "save-1-completed",
                "publish-started",
                "publish-completed",
                "save-2-started",
                "save-2-completed",
                $"delete:{OriginalLogoUrl}"
            ],
            operations);
        var deletedImage = Assert.Single(mediaModule.DeletedImages);
        Assert.Equal(OriginalLogoUrl, deletedImage.ImageUrl);
        Assert.Equal(CancellationToken.None, deletedImage.CancellationToken);
        Assert.Equal(
            NewLogoUrl,
            (await dbContext.Set<Sponsor>().SingleAsync(candidate => candidate.Id == sponsor.Id)).LogoUrl);
        Assert.Contains(
            await dbContext.OutboxMessages.ToListAsync(),
            message => message.EventType == typeof(SponsorUpdated).FullName);

        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Same(cleanupFailure, warning.Exception);
        Assert.Contains("retire a replaced Sponsor logo", warning.Message);
        Assert.Contains(OriginalLogoUrl, warning.Message);
    }

    [Fact]
    public async Task DeleteSponsorAsync_WhenDeletionCommits_RetiresCurrentLogoAfterOutboxPersistence()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var sponsor = await SeedSponsorAsync(dbContext, OriginalLightLogoUrl);
        var operations = new List<string>();
        var mediaModule = new RecordingMediaModule(NewLogoUrl, operations);
        var service = CreateSponsorService(
            dbContext,
            mediaModule,
            new RecordingLogger<SponsorService>(),
            operations);

        await service.DeleteSponsorAsync(sponsor.Id);

        Assert.Equal(
            [
                "save-1-started",
                "save-1-completed",
                "publish-started",
                "publish-completed",
                "save-2-started",
                "save-2-completed",
                $"delete:{OriginalLogoUrl}",
                $"delete:{OriginalLightLogoUrl}"
            ],
            operations);
        Assert.Equal(
            [OriginalLogoUrl, OriginalLightLogoUrl],
            mediaModule.DeletedImages.Select(deletion => deletion.ImageUrl));
        Assert.All(
            mediaModule.DeletedImages,
            deletion => Assert.Equal(CancellationToken.None, deletion.CancellationToken));
        Assert.False(await dbContext.Set<Sponsor>().AnyAsync(candidate => candidate.Id == sponsor.Id));
        Assert.Contains(
            await dbContext.OutboxMessages.ToListAsync(),
            message => message.EventType == typeof(SponsorDeleted).FullName);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DeleteSponsorAsync_WhenStateOrOutboxPersistenceFails_NeverDeletesCurrentLogo(
        int failingSaveCall)
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var sponsor = await SeedSponsorAsync(dbContext);
        var operations = new List<string>();
        var failure = new InvalidOperationException($"delete save {failingSaveCall} failed");
        var mediaModule = new RecordingMediaModule(NewLogoUrl, operations);
        var service = CreateSponsorService(
            dbContext,
            mediaModule,
            new RecordingLogger<SponsorService>(),
            operations,
            failingSaveCall,
            failure);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteSponsorAsync(sponsor.Id));

        Assert.Same(failure, actual);
        Assert.Empty(mediaModule.DeletedImages);
        await AssertSponsorStateWasRolledBackAsync(dbContext, sponsor.Id);
    }

    [Fact]
    public async Task UpdateSponsorAsync_WhenStoredReferenceIsUnchanged_DoesNotDeleteIt()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var sponsor = await SeedSponsorAsync(dbContext);
        var operations = new List<string>();
        var mediaModule = new RecordingMediaModule(OriginalLogoUrl, operations);
        var service = CreateSponsorService(
            dbContext,
            mediaModule,
            new RecordingLogger<SponsorService>(),
            operations);

        var result = await service.UpdateSponsorAsync(
            sponsor.Id,
            CreateUpdateDto(CreateFormFile()));

        Assert.Equal(OriginalLogoUrl, result.LogoUrl);
        Assert.Empty(mediaModule.DeletedImages);
        Assert.DoesNotContain(operations, operation => operation.StartsWith("delete:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateSponsorAsync_WhenLightLogoIsReplaced_RetainsDefaultAndDeletesPreviousLightLogo()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var sponsor = await SeedSponsorAsync(dbContext, OriginalLightLogoUrl);
        var operations = new List<string>();
        var mediaModule = new RecordingMediaModule(NewLightLogoUrl, operations);
        var service = CreateSponsorService(
            dbContext,
            mediaModule,
            new RecordingLogger<SponsorService>(),
            operations);

        var result = await service.UpdateSponsorAsync(
            sponsor.Id,
            CreateUpdateDto(null, CreateFormFile("light-logo.png")));

        Assert.Equal(OriginalLogoUrl, result.LogoUrl);
        Assert.Equal(NewLightLogoUrl, result.LightLogoUrl);
        var deletion = Assert.Single(mediaModule.DeletedImages);
        Assert.Equal(OriginalLightLogoUrl, deletion.ImageUrl);
        dbContext.ChangeTracker.Clear();
        var storedSponsor = await dbContext.Set<Sponsor>().SingleAsync();
        Assert.Equal(OriginalLogoUrl, storedSponsor.LogoUrl);
        Assert.Equal(NewLightLogoUrl, storedSponsor.LightLogoUrl);
    }

    [Fact]
    public async Task UpdateSponsorAsync_WhenLightLogoIsRemoved_ClearsAndDeletesOnlyLightLogo()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var sponsor = await SeedSponsorAsync(dbContext, OriginalLightLogoUrl);
        var operations = new List<string>();
        var mediaModule = new RecordingMediaModule(NewLogoUrl, operations);
        var service = CreateSponsorService(
            dbContext,
            mediaModule,
            new RecordingLogger<SponsorService>(),
            operations);

        var result = await service.UpdateSponsorAsync(
            sponsor.Id,
            CreateUpdateDto(null, removeLightLogo: true));

        Assert.Equal(OriginalLogoUrl, result.LogoUrl);
        Assert.Null(result.LightLogoUrl);
        Assert.Empty(mediaModule.SavedPurposes);
        var deletion = Assert.Single(mediaModule.DeletedImages);
        Assert.Equal(OriginalLightLogoUrl, deletion.ImageUrl);
    }

    [Fact]
    public async Task UpdateSponsorAsync_WhenLightLogoIsReplacedAndRemoved_RejectsBeforeMediaOrMutation()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var sponsor = await SeedSponsorAsync(dbContext, OriginalLightLogoUrl);
        var operations = new List<string>();
        var mediaModule = new RecordingMediaModule(NewLightLogoUrl, operations);
        var service = CreateSponsorService(
            dbContext,
            mediaModule,
            new RecordingLogger<SponsorService>(),
            operations);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            service.UpdateSponsorAsync(
                sponsor.Id,
                CreateUpdateDto(
                    CreateFormFile(),
                    CreateFormFile("light-logo.png"),
                    removeLightLogo: true)));

        Assert.Equal("The light logo cannot be replaced and removed in the same update.", exception.Message);
        Assert.Empty(mediaModule.SavedPurposes);
        Assert.Empty(mediaModule.DeletedImages);
        dbContext.ChangeTracker.Clear();
        var storedSponsor = await dbContext.Set<Sponsor>().SingleAsync();
        Assert.Equal(OriginalLogoUrl, storedSponsor.LogoUrl);
        Assert.Equal(OriginalLightLogoUrl, storedSponsor.LightLogoUrl);
        Assert.Empty(await dbContext.OutboxMessages.ToListAsync());
    }

    private static async Task<Sponsor> SeedSponsorAsync(
        MercuriusDBContext dbContext,
        string? lightLogoUrl = null)
    {
        var sponsor = new Sponsor
        {
            Name = "Original sponsor",
            SponsorTier = SponsorTier.Silver,
            LogoUrl = OriginalLogoUrl,
            LightLogoUrl = lightLogoUrl,
            InfoUrl = "https://example.test/original",
            Description = "Original description"
        };
        dbContext.Set<Sponsor>().Add(sponsor);
        await dbContext.SaveChangesAsync();
        return sponsor;
    }

    private static async Task AssertSponsorStateWasRolledBackAsync(
        MercuriusDBContext dbContext,
        int sponsorId)
    {
        dbContext.ChangeTracker.Clear();
        var persistedSponsor = await dbContext.Set<Sponsor>()
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == sponsorId);

        Assert.Equal("Original sponsor", persistedSponsor.Name);
        Assert.Equal(SponsorTier.Silver, persistedSponsor.SponsorTier);
        Assert.Equal(OriginalLogoUrl, persistedSponsor.LogoUrl);
        Assert.Equal("https://example.test/original", persistedSponsor.InfoUrl);
        Assert.Equal("Original description", persistedSponsor.Description);
        Assert.Empty(await dbContext.OutboxMessages.AsNoTracking().ToListAsync());
    }

    private static CreateSponsorDTO CreateSponsorDto(IFormFile? lightLogo = null)
    {
        return new CreateSponsorDTO
        {
            Name = "New sponsor",
            SponsorTier = SponsorTier.Gold,
            Logo = CreateFormFile(),
            LightLogo = lightLogo,
            InfoUrl = "https://example.test/new",
            Description = "New description"
        };
    }

    private static UpdateSponsorDTO CreateUpdateDto(
        IFormFile? logo,
        IFormFile? lightLogo = null,
        bool removeLightLogo = false)
    {
        return new UpdateSponsorDTO
        {
            Name = "Updated sponsor",
            SponsorTier = SponsorTier.Gold,
            Logo = logo,
            LightLogo = lightLogo,
            RemoveLightLogo = removeLightLogo,
            InfoUrl = "https://example.test/updated",
            Description = "Updated description"
        };
    }

    private static IFormFile CreateFormFile(string fileName = "logo.png")
    {
        var bytes = Encoding.UTF8.GetBytes("logo");
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "logo", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };
    }

    private static SponsorService CreateSponsorService(
        MercuriusDBContext dbContext,
        RecordingMediaModule mediaModule,
        RecordingLogger<SponsorService> logger,
        List<string> operations,
        int? failingSaveCall = null,
        Exception? saveFailure = null,
        Action? beforeSave = null)
    {
        ISponsorshipDbContext adapter = new SponsorshipDbContextAdapter<MercuriusDBContext>(dbContext);
        var recordingDbContext = new RecordingSponsorshipDbContext(
            adapter,
            operations,
            failingSaveCall,
            saveFailure,
            beforeSave);
        IModuleEventPublisher eventPublisher = new RecordingModuleEventPublisher(
            new ModuleEventPublisher(dbContext),
            operations);

        return new SponsorService(
            recordingDbContext,
            mediaModule,
            new SponsorshipOutboxWriter(recordingDbContext, eventPublisher),
            logger);
    }

    private sealed class RecordingSponsorshipDbContext(
        ISponsorshipDbContext inner,
        List<string> operations,
        int? failingSaveCall,
        Exception? saveFailure,
        Action? beforeSave) : ISponsorshipDbContext
    {
        private int _saveCallCount;

        public DbSet<Sponsor> Sponsors => inner.Sponsors;

        public DbSet<TournamentSponsorPlacement> TournamentSponsorPlacements => inner.TournamentSponsorPlacements;

        public DatabaseFacade Database => inner.Database;

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var saveCall = ++_saveCallCount;
            operations.Add($"save-{saveCall}-started");
            beforeSave?.Invoke();
            if (saveCall == failingSaveCall)
            {
                operations.Add($"save-{saveCall}-failed");
                throw saveFailure ?? new InvalidOperationException("Configured save failure was missing.");
            }

            var result = await inner.SaveChangesAsync(cancellationToken);
            operations.Add($"save-{saveCall}-completed");
            return result;
        }
    }

    private sealed class RecordingModuleEventPublisher(
        IModuleEventPublisher inner,
        List<string> operations) : IModuleEventPublisher
    {
        public Guid Publish<TPayload>(TPayload payload, DateTime? occurredAtUtc = null)
            where TPayload : notnull
        {
            operations.Add("publish-started");
            var eventId = inner.Publish(payload, occurredAtUtc);
            operations.Add("publish-completed");
            return eventId;
        }
    }

    private sealed class RecordingMediaModule(
        string savedImageUrl,
        List<string> operations,
        IReadOnlyList<string>? savedImageUrls = null) : IMediaModule
    {
        private readonly string[] _savedImageUrls = savedImageUrls?.ToArray() ?? [savedImageUrl];
        private int _saveCallCount;

        public Exception? DeleteException { get; init; }

        public Exception? SaveException { get; init; }

        public int? FailingSaveCall { get; init; }

        public List<(string? ImageUrl, CancellationToken CancellationToken)> DeletedImages { get; } = [];

        public List<MediaPurpose> SavedPurposes { get; } = [];

        public MediaPurpose? SavedPurpose { get; private set; }

        public Task<StoredMediaAsset> SaveImageAsync(
            MediaUpload upload,
            CancellationToken cancellationToken = default)
        {
            var saveCall = ++_saveCallCount;
            SavedPurpose = upload.Purpose;
            SavedPurposes.Add(upload.Purpose);
            operations.Add("media-save");
            if (saveCall == FailingSaveCall)
            {
                return Task.FromException<StoredMediaAsset>(
                    SaveException ?? new InvalidOperationException("Configured media save failure was missing."));
            }

            var imageIndex = Math.Min(saveCall - 1, _savedImageUrls.Length - 1);
            return Task.FromResult(new StoredMediaAsset(_savedImageUrls[imageIndex]));
        }

        public Task DeleteImageAsync(
            string? imageUrl,
            CancellationToken cancellationToken = default)
        {
            DeletedImages.Add((imageUrl, cancellationToken));
            operations.Add($"delete:{imageUrl}");
            return DeleteException is null
                ? Task.CompletedTask
                : Task.FromException(DeleteException);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, exception, formatter(state, exception)));
        }
    }
}
