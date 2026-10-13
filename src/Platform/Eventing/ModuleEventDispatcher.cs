using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Eventing.Persistence;

namespace Platform.Eventing;

internal sealed class ModuleEventDispatcher : IModuleEventDispatcher
{
    private const int LastErrorMaxLength = 4000;
    private const int MaxAttempts = 5;
    private static readonly TimeSpan ClaimLeaseDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryMaxDelay = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IModuleEventDbContext _dbContext;
    private readonly IServiceProvider _serviceProvider;
    private readonly TimeProvider _timeProvider;

    public ModuleEventDispatcher(
        IModuleEventDbContext dbContext,
        IServiceProvider serviceProvider,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _serviceProvider = serviceProvider;
        _timeProvider = timeProvider;
    }

    public async Task<int> DispatchPendingAsync(int batchSize = 50, CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be greater than zero.");

        var now = GetUtcNow();
        var messageIds = await _dbContext.OutboxMessages
            .AsNoTracking()
            .Where(message =>
                message.ProcessedAtUtc == null &&
                message.DeadLetteredAtUtc == null &&
                (message.NextAttemptAtUtc == null || message.NextAttemptAtUtc <= now) &&
                (message.ClaimExpiresAtUtc == null || message.ClaimExpiresAtUtc <= now))
            .OrderBy(message => message.OccurredAtUtc)
            .ThenBy(message => message.Id)
            .Take(batchSize)
            .Select(message => message.Id)
            .ToListAsync(cancellationToken);

        var processedCount = 0;
        foreach (var messageId in messageIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var claimToken = Guid.NewGuid();
            var claimNow = GetUtcNow();
            var claimExpiresAtUtc = claimNow + ClaimLeaseDuration;
            if (!await TryClaimMessageAsync(messageId, claimNow, claimToken, claimExpiresAtUtc, cancellationToken))
                continue;

            var message = await _dbContext.OutboxMessages
                .AsNoTracking()
                .SingleAsync(
                    candidate => candidate.Id == messageId && candidate.ClaimToken == claimToken,
                    cancellationToken);
            if (await DispatchMessageAsync(message, claimToken, cancellationToken))
                processedCount++;
        }

        return processedCount;
    }

    private async Task<bool> DispatchMessageAsync(
        OutboxMessage message,
        Guid claimToken,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!ModuleEventTypeNames.IsRetired(message.EventType))
                await DispatchToHandlersAsync(message, cancellationToken);

            var processedAtUtc = GetUtcNow();
            if (message.ClaimExpiresAtUtc <= processedAtUtc)
            {
                ClearTrackedState();
                return false;
            }

            var updated = await _dbContext.OutboxMessages
                .Where(outbox =>
                    outbox.Id == message.Id &&
                    outbox.ClaimToken == claimToken &&
                    outbox.ClaimExpiresAtUtc > processedAtUtc)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(outbox => outbox.ProcessedAtUtc, processedAtUtc)
                    .SetProperty(outbox => outbox.LastAttemptAtUtc, processedAtUtc)
                    .SetProperty(outbox => outbox.NextAttemptAtUtc, (DateTime?)null)
                    .SetProperty(outbox => outbox.LastError, (string?)null)
                    .SetProperty(outbox => outbox.ClaimToken, (Guid?)null)
                    .SetProperty(outbox => outbox.ClaimExpiresAtUtc, (DateTime?)null),
                    cancellationToken);

            ClearTrackedState();
            return updated == 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ClearTrackedState();
            throw;
        }
        catch (Exception exception)
        {
            await SaveFailedDispatchStateAsync(
                message,
                claimToken,
                TruncateError(exception.ToString()),
                cancellationToken);
            return false;
        }
    }

    private async Task DispatchToHandlersAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var payloadType = ModuleEventTypeNames.Resolve(message.EventType);
        var payloadJson = ModuleEventTypeNames.IsLegacy(message.EventType)
            ? NormalizeLegacyPayload(message.Payload)
            : message.Payload;
        var payload = JsonSerializer.Deserialize(payloadJson, payloadType, SerializerOptions)
            ?? throw new InvalidOperationException($"Module event payload '{message.EventType}' deserialized to null.");

        var context = new ModuleEventContext(message.Id, message.EventType, message.OccurredAtUtc);
        // Each handler commits its own inbox marker, so retries skip already completed consumers.
        foreach (var handler in ResolveHandlers(payloadType))
            await DispatchToHandlerAsync(handler, payloadType, payload, context, cancellationToken);
    }

    private async Task SaveFailedDispatchStateAsync(
        OutboxMessage message,
        Guid claimToken,
        string lastError,
        CancellationToken cancellationToken)
    {
        ClearTrackedState();

        var attemptedAtUtc = GetUtcNow();
        var retryCount = message.RetryCount + 1;
        DateTime? deadLetteredAtUtc = retryCount >= MaxAttempts ? attemptedAtUtc : null;
        DateTime? nextAttemptAtUtc = deadLetteredAtUtc is null
            ? attemptedAtUtc + CalculateRetryDelay(retryCount)
            : null;

        var updated = await _dbContext.OutboxMessages
            .Where(outbox =>
                outbox.Id == message.Id &&
                outbox.ClaimToken == claimToken &&
                outbox.ClaimExpiresAtUtc > attemptedAtUtc)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(outbox => outbox.RetryCount, retryCount)
                .SetProperty(outbox => outbox.LastAttemptAtUtc, attemptedAtUtc)
                .SetProperty(outbox => outbox.LastError, lastError)
                .SetProperty(outbox => outbox.NextAttemptAtUtc, nextAttemptAtUtc)
                .SetProperty(outbox => outbox.DeadLetteredAtUtc, deadLetteredAtUtc)
                .SetProperty(outbox => outbox.ClaimToken, (Guid?)null)
                .SetProperty(outbox => outbox.ClaimExpiresAtUtc, (DateTime?)null),
                cancellationToken);
        if (updated == 1 && deadLetteredAtUtc is not null)
            EventingMetrics.DeadLetteredMessages.Add(1, new KeyValuePair<string, object?>("event_type", message.EventType));

        ClearTrackedState();
    }

    private async Task<bool> TryClaimMessageAsync(
        Guid messageId,
        DateTime now,
        Guid claimToken,
        DateTime claimExpiresAtUtc,
        CancellationToken cancellationToken)
    {
        var updated = await _dbContext.OutboxMessages
            .Where(message =>
                message.Id == messageId &&
                message.ProcessedAtUtc == null &&
                message.DeadLetteredAtUtc == null &&
                (message.NextAttemptAtUtc == null || message.NextAttemptAtUtc <= now) &&
                (message.ClaimExpiresAtUtc == null || message.ClaimExpiresAtUtc <= now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(message => message.ClaimToken, claimToken)
                .SetProperty(message => message.ClaimExpiresAtUtc, claimExpiresAtUtc),
                cancellationToken);

        return updated == 1;
    }

    private IReadOnlyCollection<object> ResolveHandlers(Type payloadType)
    {
        var handlerInterface = typeof(IModuleEventHandler<>).MakeGenericType(payloadType);
        var enumerableType = typeof(IEnumerable<>).MakeGenericType(handlerInterface);
        var handlers = (IEnumerable)_serviceProvider.GetRequiredService(enumerableType);

        return handlers.Cast<object>().ToArray();
    }

    private async Task DispatchToHandlerAsync(
        object handler,
        Type payloadType,
        object payload,
        ModuleEventContext context,
        CancellationToken cancellationToken)
    {
        var handlerInterface = typeof(IModuleEventHandler<>).MakeGenericType(payloadType);
        var consumerName = (string?)handlerInterface.GetProperty(nameof(IModuleEventHandler<object>.ConsumerName))?.GetValue(handler);
        if (string.IsNullOrWhiteSpace(consumerName))
            throw new InvalidOperationException($"Module event handler '{handler.GetType().FullName}' must provide a consumer name.");

        if (await _dbContext.InboxMessages.AnyAsync(
            inbox => inbox.ConsumerName == consumerName && inbox.MessageId == context.MessageId,
            cancellationToken))
        {
            return;
        }

        var handleMethod = handlerInterface.GetMethod(nameof(IModuleEventHandler<object>.HandleAsync), BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Module event handler '{handler.GetType().FullName}' is missing HandleAsync.");
        var handleTask = (Task?)handleMethod.Invoke(handler, [payload, context, cancellationToken])
            ?? throw new InvalidOperationException($"Module event handler '{handler.GetType().FullName}' returned null.");

        await handleTask;
        _dbContext.InboxMessages.Add(new InboxMessage
        {
            ConsumerName = consumerName,
            MessageId = context.MessageId,
            ProcessedAtUtc = GetUtcNow()
        });
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private void ClearTrackedState()
    {
        if (_dbContext is DbContext dbContext)
            dbContext.ChangeTracker.Clear();
    }

    private static TimeSpan CalculateRetryDelay(int retryCount)
    {
        var delayTicks = RetryBaseDelay.Ticks * (1L << (retryCount - 1));
        return TimeSpan.FromTicks(Math.Min(delayTicks, RetryMaxDelay.Ticks));
    }

    private DateTime GetUtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private static string NormalizeLegacyPayload(string payload)
    {
        var node = JsonNode.Parse(payload);
        if (node is not JsonObject jsonObject || !jsonObject.ContainsKey("gameId") || jsonObject.ContainsKey("tournamentId"))
            return payload;

        jsonObject["tournamentId"] = jsonObject["gameId"]!.DeepClone();
        jsonObject.Remove("gameId");
        return jsonObject.ToJsonString(SerializerOptions);
    }

    private static string TruncateError(string error) =>
        error.Length <= LastErrorMaxLength
            ? error
            : error[..LastErrorMaxLength];
}
