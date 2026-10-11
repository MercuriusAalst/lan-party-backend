using Mercurius.LAN.API.Data;
using Mercurius.Modules.Tournament.Application.DTOs.Tournaments;
using Mercurius.Modules.Tournament.Infrastructure;
using Mercurius.Modules.Media.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Platform.Eventing;

namespace Mercurius.Modules.Tournament.Tests;

public sealed class TournamentMediaLifecycleTests
{
    private const string PreviousImageUrl = "images/previous-tournament.webp";
    private const string NewImageUrl = "images/new-tournament.webp";

    [Fact]
    public async Task CreateTournamentAsync_WhenPersistenceFails_CompensatesOnlyNewImageAndPreservesOriginalFailure()
    {
        await using var dbContext = CreateDbContext();
        var persistenceFailure = new DbUpdateException("Tournament persistence failed.");
        var cleanupFailure = new IOException("Image cleanup failed.");
        var tournamentDbContext = new InstrumentedTournamentDbContext(
            dbContext,
            _ => Task.FromException<int>(persistenceFailure));
        var mediaModule = new RecordingMediaModule(NewImageUrl, cleanupFailure);
        var logger = new RecordingLogger<TournamentService>();
        var service = CreateService(tournamentDbContext, mediaModule, logger: logger);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            service.CreateTournamentAsync(CreateTournamentDto()));

        Assert.Same(persistenceFailure, exception);
        var cleanup = Assert.Single(mediaModule.DeleteCalls);
        Assert.Equal(NewImageUrl, cleanup.ImageUrl);
        Assert.Equal(CancellationToken.None, cleanup.CancellationToken);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            ReferenceEquals(entry.Exception, cleanupFailure));
        Assert.False(await dbContext.Set<TournamentAggregate>().AnyAsync());
    }

    [Fact]
    public async Task CreateTournamentAsync_WhenPersistenceIsCanceled_CompensatesWithNonCanceledTokenAndPreservesCancellation()
    {
        await using var dbContext = CreateDbContext();
        using var cancellationSource = new CancellationTokenSource();
        var cancellation = new OperationCanceledException(
            "Tournament persistence was canceled.",
            cancellationSource.Token);
        var tournamentDbContext = new InstrumentedTournamentDbContext(
            dbContext,
            _ =>
            {
                cancellationSource.Cancel();
                return Task.FromException<int>(cancellation);
            });
        var mediaModule = new RecordingMediaModule(NewImageUrl);
        var service = CreateService(tournamentDbContext, mediaModule);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.CreateTournamentAsync(CreateTournamentDto(), cancellationSource.Token));

        Assert.Same(cancellation, exception);
        var cleanup = Assert.Single(mediaModule.DeleteCalls);
        Assert.Equal(NewImageUrl, cleanup.ImageUrl);
        Assert.Equal(CancellationToken.None, cleanup.CancellationToken);
        Assert.False(cleanup.CancellationToken.CanBeCanceled);
        Assert.False(await dbContext.Set<TournamentAggregate>().AnyAsync());
    }

    [Theory]
    [InlineData(FailureStage.OutboxStaging)]
    [InlineData(FailureStage.Persistence)]
    public async Task UpdateTournamentAsync_WhenCommitFails_CompensatesNewImageButNeverPreviousImage(
        FailureStage failureStage)
    {
        await using var dbContext = CreateDbContext();
        var tournament = CreateStoredTournament();
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var commitFailure = new InvalidOperationException($"{failureStage} failed.");
        var tournamentDbContext = new InstrumentedTournamentDbContext(
            dbContext,
            failureStage == FailureStage.Persistence
                ? _ => Task.FromException<int>(commitFailure)
                : null);
        IModuleEventPublisher publisher = failureStage == FailureStage.OutboxStaging
            ? new ThrowingModuleEventPublisher(commitFailure)
            : TournamentTestSupport.CreateModuleEventPublisher();
        var mediaModule = new RecordingMediaModule(NewImageUrl);
        var service = CreateService(tournamentDbContext, mediaModule, publisher);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateTournamentAsync(tournament.Id, CreateUpdateDto()));

        Assert.Same(commitFailure, exception);
        var cleanup = Assert.Single(mediaModule.DeleteCalls);
        Assert.Equal(NewImageUrl, cleanup.ImageUrl);
        Assert.Equal(CancellationToken.None, cleanup.CancellationToken);
        Assert.DoesNotContain(mediaModule.DeleteCalls, call => call.ImageUrl == PreviousImageUrl);
        Assert.Equal(
            failureStage == FailureStage.OutboxStaging ? 0 : 1,
            tournamentDbContext.SaveChangesCallCount);

        dbContext.ChangeTracker.Clear();
        var storedTournament = await dbContext.Set<TournamentAggregate>().SingleAsync(candidate => candidate.Id == tournament.Id);
        Assert.Equal(PreviousImageUrl, storedTournament.ImageUrl);
    }

    [Fact]
    public async Task UpdateTournamentAsync_WhenReplacementCommits_RetiresPreviousImageAfterPersistence()
    {
        await using var dbContext = CreateDbContext();
        var tournament = CreateStoredTournament();
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var operations = new List<string>();
        var tournamentDbContext = new InstrumentedTournamentDbContext(dbContext, operations: operations);
        var mediaModule = new RecordingMediaModule(NewImageUrl, operations: operations);
        var service = CreateService(tournamentDbContext, mediaModule);

        var result = await service.UpdateTournamentAsync(tournament.Id, CreateUpdateDto());

        Assert.Equal(NewImageUrl, result.ImageUrl);
        Assert.Equal(
            ["persistence-complete", $"delete:{PreviousImageUrl}"],
            operations);
        var cleanup = Assert.Single(mediaModule.DeleteCalls);
        Assert.Equal(PreviousImageUrl, cleanup.ImageUrl);
        Assert.Equal(CancellationToken.None, cleanup.CancellationToken);
    }

    [Fact]
    public async Task DeleteTournamentAsync_WhenDeletionCommits_RetiresCurrentImageAfterPersistence()
    {
        await using var dbContext = CreateDbContext();
        var tournament = CreateStoredTournament();
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var operations = new List<string>();
        var tournamentDbContext = new InstrumentedTournamentDbContext(dbContext, operations: operations);
        var mediaModule = new RecordingMediaModule(NewImageUrl, operations: operations);
        var service = CreateService(tournamentDbContext, mediaModule);

        await service.DeleteTournamentAsync(tournament.Id);

        Assert.Equal(
            ["persistence-complete", $"delete:{PreviousImageUrl}"],
            operations);
        var cleanup = Assert.Single(mediaModule.DeleteCalls);
        Assert.Equal(PreviousImageUrl, cleanup.ImageUrl);
        Assert.Equal(CancellationToken.None, cleanup.CancellationToken);
        Assert.False(await dbContext.Set<TournamentAggregate>().AnyAsync(candidate => candidate.Id == tournament.Id));
    }

    [Fact]
    public async Task DeleteTournamentAsync_WhenPersistenceFails_NeverDeletesCurrentImage()
    {
        await using var dbContext = CreateDbContext();
        var tournament = CreateStoredTournament();
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var persistenceFailure = new DbUpdateException("Tournament deletion failed.");
        var tournamentDbContext = new InstrumentedTournamentDbContext(
            dbContext,
            _ => Task.FromException<int>(persistenceFailure));
        var mediaModule = new RecordingMediaModule(NewImageUrl);
        var service = CreateService(tournamentDbContext, mediaModule);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            service.DeleteTournamentAsync(tournament.Id));

        Assert.Same(persistenceFailure, exception);
        Assert.Empty(mediaModule.DeleteCalls);
        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.Set<TournamentAggregate>().AnyAsync(candidate => candidate.Id == tournament.Id));
    }

    [Fact]
    public async Task UpdateTournamentAsync_WhenPostCommitCleanupFails_LogsWarningAndReturnsCommittedReplacement()
    {
        await using var dbContext = CreateDbContext();
        var tournament = CreateStoredTournament();
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var cleanupFailure = new IOException("Previous image could not be deleted.");
        var mediaModule = new RecordingMediaModule(NewImageUrl, cleanupFailure);
        var logger = new RecordingLogger<TournamentService>();
        var service = CreateService(
            new InstrumentedTournamentDbContext(dbContext),
            mediaModule,
            logger: logger);

        var result = await service.UpdateTournamentAsync(tournament.Id, CreateUpdateDto());

        Assert.Equal(NewImageUrl, result.ImageUrl);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            ReferenceEquals(entry.Exception, cleanupFailure));
        dbContext.ChangeTracker.Clear();
        Assert.Equal(
            NewImageUrl,
            (await dbContext.Set<TournamentAggregate>().SingleAsync(candidate => candidate.Id == tournament.Id)).ImageUrl);
    }

    [Fact]
    public async Task DeleteTournamentAsync_WhenPostCommitCleanupFails_LogsWarningAndLeavesTournamentDeleted()
    {
        await using var dbContext = CreateDbContext();
        var tournament = CreateStoredTournament();
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var cleanupFailure = new IOException("Current image could not be deleted.");
        var mediaModule = new RecordingMediaModule(NewImageUrl, cleanupFailure);
        var logger = new RecordingLogger<TournamentService>();
        var service = CreateService(
            new InstrumentedTournamentDbContext(dbContext),
            mediaModule,
            logger: logger);

        await service.DeleteTournamentAsync(tournament.Id);

        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            ReferenceEquals(entry.Exception, cleanupFailure));
        Assert.False(await dbContext.Set<TournamentAggregate>().AnyAsync(candidate => candidate.Id == tournament.Id));
    }

    private static TournamentService CreateService(
        ITournamentDbContext dbContext,
        IMediaModule mediaModule,
        IModuleEventPublisher? moduleEventPublisher = null,
        ILogger<TournamentService>? logger = null)
    {
        return new TournamentService(
            dbContext,
            new UnsupportedMatchModeratorFactory(),
            mediaModule,
            TournamentTestSupport.CreateIdentityModule(),
            TournamentTestSupport.CreateSponsorshipModule(),
            TournamentTestSupport.CreateMapper(),
            moduleEventPublisher ?? TournamentTestSupport.CreateModuleEventPublisher(),
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TournamentService>.Instance);
    }

    private static MercuriusDBContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new MercuriusDBContext(options);
    }

    private static TournamentAggregate CreateStoredTournament()
    {
        return new TournamentAggregate(
            "Lifecycle Cup",
            BracketType.SingleElimination,
            GameFormat.BestOf1,
            GameFormat.BestOf3,
            ParticipationMode.Individual)
        {
            Id = Guid.NewGuid(),
            ImageUrl = PreviousImageUrl
        };
    }

    private static CreateTournamentDTO CreateTournamentDto()
    {
        return new CreateTournamentDTO
        {
            Name = "Lifecycle Cup",
            BracketType = Mercurius.Modules.Tournament.Contracts.BracketType.SingleElimination,
            Format = Mercurius.Modules.Tournament.Contracts.GameFormat.BestOf1,
            FinalsFormat = Mercurius.Modules.Tournament.Contracts.GameFormat.BestOf3,
            ParticipationMode = Mercurius.Modules.Tournament.Contracts.ParticipationMode.Individual,
            Image = CreateImage(),
            PlannedStartTime = DateTime.UtcNow.AddHours(1),
            AverageGameDurationMinutes = 30,
            RoundBreakDurationMinutes = 10
        };
    }

    private static UpdateTournamentDTO CreateUpdateDto()
    {
        return new UpdateTournamentDTO
        {
            Name = "Updated Lifecycle Cup",
            BracketType = Mercurius.Modules.Tournament.Contracts.BracketType.SingleElimination,
            Format = Mercurius.Modules.Tournament.Contracts.GameFormat.BestOf1,
            FinalsFormat = Mercurius.Modules.Tournament.Contracts.GameFormat.BestOf3,
            ParticipationMode = Mercurius.Modules.Tournament.Contracts.ParticipationMode.Individual,
            Image = CreateImage(),
            PlannedStartTime = DateTime.UtcNow.AddHours(2),
            AverageGameDurationMinutes = 30,
            RoundBreakDurationMinutes = 10
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

    public enum FailureStage
    {
        OutboxStaging,
        Persistence
    }

    private sealed class InstrumentedTournamentDbContext : ITournamentDbContext
    {
        private readonly MercuriusDBContext _inner;
        private readonly Func<CancellationToken, Task<int>>? _saveChanges;
        private readonly List<string>? _operations;

        public InstrumentedTournamentDbContext(
            MercuriusDBContext inner,
            Func<CancellationToken, Task<int>>? saveChanges = null,
            List<string>? operations = null)
        {
            _inner = inner;
            _saveChanges = saveChanges;
            _operations = operations;
        }

        public DbSet<TournamentAggregate> Tournaments => _inner.Set<TournamentAggregate>();
        public DbSet<Match> Matches => _inner.Set<Match>();
        public DbSet<Placement> Placements => _inner.Set<Placement>();
        public DbSet<MatchResolutionNotification> MatchResolutionNotifications =>
            _inner.Set<MatchResolutionNotification>();
        public DbSet<TournamentRegistration> TournamentRegistrations => _inner.Set<TournamentRegistration>();
        public DbSet<TournamentRegistrationRosterMember> TournamentRegistrationRosterMembers =>
            _inner.Set<TournamentRegistrationRosterMember>();
        public DbSet<LeaderboardParticipant> LeaderboardParticipants => _inner.Set<LeaderboardParticipant>();
        public DbSet<LeaderboardAttempt> LeaderboardAttempts => _inner.Set<LeaderboardAttempt>();
        public DatabaseFacade Database => _inner.Database;
        public int SaveChangesCallCount { get; private set; }

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesCallCount++;
            var result = _saveChanges is null
                ? await _inner.SaveChangesAsync(cancellationToken)
                : await _saveChanges(cancellationToken);
            _operations?.Add("persistence-complete");
            return result;
        }
    }

    private sealed class RecordingMediaModule : IMediaModule
    {
        private readonly string _savedUrl;
        private readonly Exception? _deleteFailure;
        private readonly List<string>? _operations;

        public RecordingMediaModule(
            string savedUrl,
            Exception? deleteFailure = null,
            List<string>? operations = null)
        {
            _savedUrl = savedUrl;
            _deleteFailure = deleteFailure;
            _operations = operations;
        }

        public List<DeleteCall> DeleteCalls { get; } = [];

        public Task<StoredMediaAsset> SaveImageAsync(
            MediaUpload upload,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new StoredMediaAsset(_savedUrl));
        }

        public Task DeleteImageAsync(string? imageUrl, CancellationToken cancellationToken = default)
        {
            DeleteCalls.Add(new DeleteCall(imageUrl, cancellationToken));
            _operations?.Add($"delete:{imageUrl}");
            return _deleteFailure is null
                ? Task.CompletedTask
                : Task.FromException(_deleteFailure);
        }
    }

    private sealed record DeleteCall(string? ImageUrl, CancellationToken CancellationToken);

    private sealed class ThrowingModuleEventPublisher(Exception exception) : IModuleEventPublisher
    {
        public Guid Publish<TPayload>(TPayload payload, DateTime? occurredAtUtc = null)
            where TPayload : notnull
        {
            throw exception;
        }
    }

    private sealed class UnsupportedMatchModeratorFactory : IMatchModeratorFactory
    {
        public IMatchModerator GetMatchModerator(BracketType bracketType)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, exception, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel Level, Exception? Exception, string Message);
}
