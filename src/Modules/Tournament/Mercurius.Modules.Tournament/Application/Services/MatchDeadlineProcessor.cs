using Mercurius.Modules.Tournament.Domain;
using Mercurius.Modules.Tournament.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Eventing;
using MatchResolutionRequiredIntegrationEvent = Mercurius.Modules.Tournament.Contracts.MatchResolutionRequiredIntegrationEvent;

namespace Mercurius.Modules.Tournament.Application.Services;

internal sealed class MatchDeadlineProcessor : BackgroundService
{
    internal const long ProcessorLockKey = 0x4D41544348444C4E;
    private const int BatchSize = 100;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MatchDeadlineProcessor> _logger;

    public MatchDeadlineProcessor(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<MatchDeadlineProcessor> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessExpiredMatchesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Unable to process expired tournament match result windows.");
            }

            try
            {
                await Task.Delay(PollInterval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ProcessExpiredMatchesAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ITournamentDbContext>();
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        // The lock lives as long as this transaction, so only one instance works through a batch at a time.
        await using var lockTransaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (!await TryAcquireProcessorLockAsync(dbContext, cancellationToken))
            return;

        var matchIds = await CreateExpiredDeadlineQuery(dbContext, nowUtc)
            .OrderBy(match => match.ScoreConfirmationDeadlineUtc ?? match.CorrectionDeadlineUtc)
            .ThenBy(match => match.Id)
            .Select(match => match.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var matchId in matchIds)
            await ProcessExpiredMatchAsync(matchId, nowUtc, cancellationToken);

        await lockTransaction.CommitAsync(cancellationToken);
    }

    // Each match is saved in its own scope, so a match or tournament that changed concurrently
    // only skips that match instead of discarding the whole batch.
    private async Task ProcessExpiredMatchAsync(Guid matchId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ITournamentDbContext>();
        var eventPublisher = scope.ServiceProvider.GetRequiredService<IModuleEventPublisher>();
        var match = await CreateExpiredDeadlineQuery(dbContext, nowUtc)
            .SingleOrDefaultAsync(candidate => candidate.Id == matchId, cancellationToken);
        if (match is null || !await ApplyDeadlineAsync(dbContext, eventPublisher, match, nowUtc, cancellationToken))
            return;

        dbContext.Tournaments.Entry(match.Tournament).Property(candidate => candidate.Status).IsModified = true;
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            _logger.LogWarning(exception, "Skipped expired deadline for match {MatchId} because it changed concurrently.", matchId);
        }
    }

    private static async Task<bool> ApplyDeadlineAsync(
        ITournamentDbContext dbContext,
        IModuleEventPublisher eventPublisher,
        Match match,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var beforeState = match.LifecycleState;
        var beforeResult = match.HasResult;
        match.ApplyDeadline(nowUtc);
        if (beforeState == match.LifecycleState && beforeResult == match.HasResult)
            return false;

        if (!beforeResult && match.HasResult && match.GetWinnerId() is not null)
        {
            await LoadDirectNextMatchesAsync(dbContext, match, cancellationToken);
            match.UpdateParticipantsNextMatch();
        }
        else if (beforeState != MatchLifecycleState.AdminResolutionRequired &&
                match.LifecycleState == MatchLifecycleState.AdminResolutionRequired)
        {
            eventPublisher.Publish(new MatchResolutionRequiredIntegrationEvent(
                new Mercurius.Modules.Shared.MatchId(match.Id),
                new Mercurius.Modules.Shared.TournamentId(match.TournamentId),
                match.Tournament.AssignedAdminUserId),
                nowUtc);
        }

        return true;
    }

    private static async Task<bool> TryAcquireProcessorLockAsync(
        ITournamentDbContext dbContext,
        CancellationToken cancellationToken) =>
        await dbContext.Database
            .SqlQueryRaw<bool>($"SELECT pg_try_advisory_xact_lock({ProcessorLockKey}) AS \"Value\"")
            .SingleAsync(cancellationToken);

    private static IQueryable<Match> CreateExpiredDeadlineQuery(
        ITournamentDbContext dbContext,
        DateTime nowUtc) =>
        dbContext.Matches
            .Include(match => match.Tournament)
            .Where(match =>
                match.Tournament.Status == TournamentStatus.InProgress &&
                ((match.LifecycleState == MatchLifecycleState.ScoreConfirmation &&
                  match.ScoreConfirmationDeadlineUtc <= nowUtc) ||
                 (match.LifecycleState == MatchLifecycleState.Disputed &&
                  match.CorrectionDeadlineUtc <= nowUtc)));

    private static async Task LoadDirectNextMatchesAsync(
        ITournamentDbContext dbContext,
        Match match,
        CancellationToken cancellationToken)
    {
        var nextMatchIds = new[] { match.WinnerNextMatchId, match.LoserNextMatchId }
            .Where(nextMatchId => nextMatchId.HasValue)
            .Select(nextMatchId => nextMatchId!.Value)
            .Distinct()
            .ToArray();
        if (nextMatchIds.Length == 0)
            return;

        var nextMatches = await dbContext.Matches
            .Where(candidate =>
                candidate.TournamentId == match.TournamentId &&
                nextMatchIds.Contains(candidate.Id))
            .ToListAsync(cancellationToken);
        var nextMatchesById = nextMatches.ToDictionary(candidate => candidate.Id);
        if (match.WinnerNextMatchId is { } winnerNextMatchId)
            match.WinnerNextMatch = nextMatchesById.GetValueOrDefault(winnerNextMatchId);
        if (match.LoserNextMatchId is { } loserNextMatchId)
            match.LoserNextMatch = nextMatchesById.GetValueOrDefault(loserNextMatchId);
    }
}
