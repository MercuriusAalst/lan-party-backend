using System.Diagnostics.Metrics;
using Mercurius.LAN.API.Data;
using Mercurius.LAN.API.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Platform.Eventing;
using Mercurius.TestInfrastructure;
using Platform.Eventing.Persistence;
using Platform.Extensions;

namespace Platform.Tests;

public sealed class ModuleEventingReliabilityTests
{
    [Fact]
    public async Task Dispatcher_PersistsLaterSuccessAfterEarlierFailureAndReleasesClaims()
    {
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var state = new ReliabilityState();
        await using var provider = CreateProvider(state, new MutableTimeProvider(now));
        var poisonId = await PublishAsync(provider, "poison", now.UtcDateTime.AddMinutes(-1), isPoison: true);
        var healthyId = await PublishAsync(provider, "healthy", now.UtcDateTime.AddSeconds(-59));

        await using var scope = provider.CreateAsyncScope();
        var processed = await scope.ServiceProvider
            .GetRequiredService<IModuleEventDispatcher>()
            .DispatchPendingAsync(2);

        Assert.Equal(1, processed);
        Assert.Equal(["poison", "healthy"], state.Attempts);
        var poison = await LoadMessageAsync(provider, poisonId);
        var healthy = await LoadMessageAsync(provider, healthyId);
        Assert.Equal(1, poison.RetryCount);
        Assert.Null(poison.ProcessedAtUtc);
        Assert.Null(poison.ClaimToken);
        Assert.Null(poison.ClaimExpiresAtUtc);
        Assert.NotNull(healthy.ProcessedAtUtc);
        Assert.Null(healthy.ClaimToken);
        Assert.Null(healthy.ClaimExpiresAtUtc);
    }

    [Fact]
    public async Task Dispatcher_UsesFreshLeaseTimestampForEachMessage()
    {
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new MutableTimeProvider(now);
        var state = new FreshLeaseState(timeProvider);
        await using var provider = CreateFreshLeaseProvider(state, timeProvider);
        await PublishAsync(provider, "first", now.UtcDateTime.AddMinutes(-1));
        var secondId = await PublishAsync(provider, "second", now.UtcDateTime.AddSeconds(-59));

        await using var scope = provider.CreateAsyncScope();
        var dispatch = scope.ServiceProvider
            .GetRequiredService<IModuleEventDispatcher>()
            .DispatchPendingAsync(2);
        await state.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = await LoadMessageAsync(provider, secondId);
        Assert.Equal(now.UtcDateTime.AddMinutes(6), second.ClaimExpiresAtUtc);

        state.ReleaseSecond.TrySetResult();
        Assert.Equal(2, await dispatch);
    }

    [Fact]
    public async Task Dispatcher_DefersRetriesAndDeadLettersAfterFifthFailure()
    {
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new MutableTimeProvider(now);
        var state = new ReliabilityState();
        await using var provider = CreateProvider(state, timeProvider);
        var messageId = await PublishAsync(provider, "poison", now.UtcDateTime, isPoison: true);
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IModuleEventDispatcher>();
        var deadLetterEventTypes = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Mercurius.Eventing" && instrument.Name == "mercurius.outbox.dead_lettered")
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "event_type")
                    deadLetterEventTypes.Enqueue(tag.Value as string);
            }
        });
        meterListener.Start();

        Assert.Equal(0, await dispatcher.DispatchPendingAsync(1));
        var message = await LoadMessageAsync(provider, messageId);
        Assert.Equal(now.UtcDateTime.AddSeconds(5), message.NextAttemptAtUtc);

        Assert.Equal(0, await dispatcher.DispatchPendingAsync(1));
        Assert.Single(state.Attempts);

        foreach (var delay in new[] { 5, 10, 20 })
        {
            timeProvider.Advance(TimeSpan.FromSeconds(delay));
            Assert.Equal(0, await dispatcher.DispatchPendingAsync(1));
        }

        message = await LoadMessageAsync(provider, messageId);
        Assert.Equal(4, message.RetryCount);
        Assert.Equal(now.UtcDateTime.AddSeconds(75), message.NextAttemptAtUtc);
        Assert.DoesNotContain(message.EventType, deadLetterEventTypes);

        timeProvider.Advance(TimeSpan.FromSeconds(40));
        Assert.Equal(0, await dispatcher.DispatchPendingAsync(1));
        message = await LoadMessageAsync(provider, messageId);
        Assert.Equal(5, message.RetryCount);
        Assert.Equal(now.UtcDateTime.AddSeconds(75), message.LastAttemptAtUtc);
        Assert.Equal(now.UtcDateTime.AddSeconds(75), message.DeadLetteredAtUtc);
        Assert.Null(message.NextAttemptAtUtc);
        Assert.Null(message.ClaimToken);
        Assert.Null(message.ClaimExpiresAtUtc);
        Assert.Contains("planned poison failure", message.LastError, StringComparison.Ordinal);
        Assert.Single(deadLetterEventTypes, eventType => eventType == message.EventType);

        timeProvider.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, await dispatcher.DispatchPendingAsync(1));
        Assert.Equal(5, state.Attempts.Count);
    }

    [Fact]
    public async Task Dispatcher_PoisonMessagesAcrossBatchBoundaryDoNotStarveHealthyMessage()
    {
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var state = new ReliabilityState();
        await using var provider = CreateProvider(state, new MutableTimeProvider(now));

        for (var index = 0; index < 5; index++)
        {
            await PublishAsync(
                provider,
                $"poison-{index}",
                now.UtcDateTime.AddMinutes(-1).AddSeconds(index),
                isPoison: true);
        }

        var healthyId = await PublishAsync(provider, "healthy", now.UtcDateTime.AddSeconds(-55));
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IModuleEventDispatcher>();

        Assert.Equal(0, await dispatcher.DispatchPendingAsync(3));
        Assert.Equal(1, await dispatcher.DispatchPendingAsync(3));

        Assert.Equal(
            ["poison-0", "poison-1", "poison-2", "poison-3", "poison-4", "healthy"],
            state.Attempts);
        Assert.NotNull((await LoadMessageAsync(provider, healthyId)).ProcessedAtUtc);
    }

    [Fact]
    public async Task Dispatcher_CancellationPropagatesWithoutRecordingFailure()
    {
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var state = new ReliabilityState { Block = true };
        await using var provider = CreateProvider(state, new MutableTimeProvider(now));
        var messageId = await PublishAsync(provider, "blocked", now.UtcDateTime);
        await using var scope = provider.CreateAsyncScope();
        using var cancellation = new CancellationTokenSource();

        var dispatch = scope.ServiceProvider
            .GetRequiredService<IModuleEventDispatcher>()
            .DispatchPendingAsync(1, cancellation.Token);
        await state.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);
        var message = await LoadMessageAsync(provider, messageId);
        Assert.Equal(0, message.RetryCount);
        Assert.Null(message.LastAttemptAtUtc);
        Assert.Null(message.LastError);
        Assert.Null(message.ProcessedAtUtc);
        Assert.Null(message.DeadLetteredAtUtc);
        Assert.NotNull(message.ClaimToken);
        Assert.True(message.ClaimExpiresAtUtc > now.UtcDateTime);
    }

    [Fact]
    public async Task Dispatcher_ReclaimsMessageAfterClaimLeaseExpires()
    {
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new MutableTimeProvider(now);
        var state = new ReliabilityState();
        await using var provider = CreateProvider(state, timeProvider);
        var messageId = await PublishAsync(provider, "recoverable", now.UtcDateTime);

        await using (var claimScope = provider.CreateAsyncScope())
        {
            var dbContext = claimScope.ServiceProvider.GetRequiredService<MercuriusDBContext>();
            var message = await dbContext.OutboxMessages.SingleAsync(outbox => outbox.Id == messageId);
            message.ClaimToken = Guid.NewGuid();
            message.ClaimExpiresAtUtc = now.UtcDateTime.AddMinutes(1);
            await dbContext.SaveChangesAsync();
        }

        await using (var activeScope = provider.CreateAsyncScope())
        {
            var processed = await activeScope.ServiceProvider
                .GetRequiredService<IModuleEventDispatcher>()
                .DispatchPendingAsync(1);

            Assert.Equal(0, processed);
        }

        timeProvider.Advance(TimeSpan.FromMinutes(1).Add(TimeSpan.FromMilliseconds(1)));
        await using var dispatchScope = provider.CreateAsyncScope();
        var processedAfterExpiry = await dispatchScope.ServiceProvider
            .GetRequiredService<IModuleEventDispatcher>()
            .DispatchPendingAsync(1);

        var recovered = await LoadMessageAsync(provider, messageId);
        Assert.Equal(1, processedAfterExpiry);
        Assert.Single(state.Attempts);
        Assert.NotNull(recovered.ProcessedAtUtc);
        Assert.Null(recovered.ClaimToken);
        Assert.Null(recovered.ClaimExpiresAtUtc);
    }

    [Fact]
    public async Task OverlappingDispatchers_InvokeHandlerOnlyOnceForOneActiveClaim()
    {
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var state = new BlockingDispatchState();
        await using var provider = CreateBlockingProvider(state, new MutableTimeProvider(now));
        var messageId = await PublishAsync(provider, "concurrent", now.UtcDateTime);

        await using var firstScope = provider.CreateAsyncScope();
        await using var secondScope = provider.CreateAsyncScope();
        var firstDispatch = firstScope.ServiceProvider
            .GetRequiredService<IModuleEventDispatcher>()
            .DispatchPendingAsync(1);
        await state.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var secondProcessed = await secondScope.ServiceProvider
            .GetRequiredService<IModuleEventDispatcher>()
            .DispatchPendingAsync(1);

        state.Release.TrySetResult();
        var firstProcessed = await firstDispatch;
        var message = await LoadMessageAsync(provider, messageId);

        Assert.Equal(0, secondProcessed);
        Assert.Equal(1, firstProcessed);
        Assert.Equal(1, state.HandlerCalls);
        Assert.NotNull(message.ProcessedAtUtc);
        Assert.Null(message.ClaimToken);
        Assert.Null(message.ClaimExpiresAtUtc);
    }

    [Fact]
    public async Task ExpiredOwner_CannotWriteTerminalStateAfterMessageIsReclaimed()
    {
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var firstTimeProvider = new MutableTimeProvider(now);
        var secondTimeProvider = new MutableTimeProvider(now.AddMinutes(5).AddMilliseconds(1));
        var state = new StaleOwnerState();
        var database = PostgresTestDatabase.Create();
        await using var firstProvider = CreateStaleOwnerProvider(
            state,
            firstTimeProvider,
            database,
            "stale-owner-first");
        await using var secondProvider = CreateStaleOwnerProvider(
            state,
            secondTimeProvider,
            database,
            "stale-owner-second",
            initializeDatabase: false,
            registerDatabaseLease: false);
        var messageId = await PublishAsync(firstProvider, "stale-owner", now.UtcDateTime);

        await using var firstScope = firstProvider.CreateAsyncScope();
        var firstDispatch = firstScope.ServiceProvider
            .GetRequiredService<IModuleEventDispatcher>()
            .DispatchPendingAsync(1);
        await state.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await using var secondScope = secondProvider.CreateAsyncScope();
        var secondProcessed = await secondScope.ServiceProvider
            .GetRequiredService<IModuleEventDispatcher>()
            .DispatchPendingAsync(1);

        state.ReleaseFirst.TrySetResult();
        var firstProcessed = await firstDispatch;
        var finalMessage = await LoadMessageAsync(firstProvider, messageId);

        Assert.Equal(1, secondProcessed);
        Assert.Equal(0, firstProcessed);
        Assert.Equal(2, state.HandlerCalls);
        Assert.NotNull(finalMessage.ProcessedAtUtc);
        Assert.Equal(0, finalMessage.RetryCount);
        Assert.Null(finalMessage.LastError);
        Assert.Null(finalMessage.ClaimToken);
        Assert.Null(finalMessage.ClaimExpiresAtUtc);
    }

    [Fact]
    public async Task ExpiredOwner_CannotWriteFailureStateAfterMessageIsReclaimed()
    {
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var state = new StaleOwnerState { FailFirst = true };
        var database = PostgresTestDatabase.Create();
        await using var firstProvider = CreateStaleOwnerProvider(
            state,
            new MutableTimeProvider(now),
            database,
            "stale-failure-first");
        await using var secondProvider = CreateStaleOwnerProvider(
            state,
            new MutableTimeProvider(now.AddMinutes(5).AddMilliseconds(1)),
            database,
            "stale-failure-second",
            initializeDatabase: false,
            registerDatabaseLease: false);
        var messageId = await PublishAsync(firstProvider, "stale-failure", now.UtcDateTime);

        await using var firstScope = firstProvider.CreateAsyncScope();
        var firstDispatch = firstScope.ServiceProvider
            .GetRequiredService<IModuleEventDispatcher>()
            .DispatchPendingAsync(1);
        await state.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await using var secondScope = secondProvider.CreateAsyncScope();
        var secondProcessed = await secondScope.ServiceProvider
            .GetRequiredService<IModuleEventDispatcher>()
            .DispatchPendingAsync(1);

        state.ReleaseFirst.TrySetResult();
        var firstProcessed = await firstDispatch;
        var finalMessage = await LoadMessageAsync(firstProvider, messageId);

        Assert.Equal(1, secondProcessed);
        Assert.Equal(0, firstProcessed);
        Assert.Equal(2, state.HandlerCalls);
        Assert.NotNull(finalMessage.ProcessedAtUtc);
        Assert.Equal(0, finalMessage.RetryCount);
        Assert.Null(finalMessage.LastError);
        Assert.Null(finalMessage.ClaimToken);
        Assert.Null(finalMessage.ClaimExpiresAtUtc);
    }

    [Fact]
    public void ClaimMigrationAndModels_ContainClaimLifecycleSchema()
    {
        var migration = new ClaimOutboxDispatchClaims();
        var operations = migration.UpOperations.ToList();
        var addedColumns = operations
            .OfType<AddColumnOperation>()
            .Where(operation => operation.Schema == "platform" && operation.Table == "outbox_messages")
            .Select(operation => operation.Name)
            .ToArray();

        Assert.Equal(["claim_expires_at_utc", "claim_token"], addedColumns);
        var index = Assert.Single(operations.OfType<CreateIndexOperation>());
        Assert.Equal("IX_outbox_messages_pending_dispatch", index.Name);
        Assert.Equal(
            ["next_attempt_at_utc", "claim_expires_at_utc", "occurred_at_utc", "id"],
            index.Columns);
        Assert.Equal("processed_at_utc IS NULL AND dead_lettered_at_utc IS NULL", index.Filter);
        AssertClaimLifecycleModel(migration.TargetModel);

        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseNpgsql("Host=localhost;Database=model-only")
            .Options;
        using var dbContext = new MercuriusDBContext(options);
        AssertClaimLifecycleModel(dbContext.Model);
    }

    private static void AssertClaimLifecycleModel(IModel model)
    {
        var outbox = Assert.IsAssignableFrom<IReadOnlyEntityType>(
            model.FindEntityType(typeof(OutboxMessage)));
        Assert.NotNull(outbox.FindProperty(nameof(OutboxMessage.NextAttemptAtUtc)));
        Assert.NotNull(outbox.FindProperty(nameof(OutboxMessage.DeadLetteredAtUtc)));
        Assert.NotNull(outbox.FindProperty(nameof(OutboxMessage.ClaimToken)));
        Assert.NotNull(outbox.FindProperty(nameof(OutboxMessage.ClaimExpiresAtUtc)));
        Assert.True(outbox.FindProperty(nameof(OutboxMessage.ClaimToken))!.IsConcurrencyToken);
        Assert.True(outbox.FindProperty(nameof(OutboxMessage.ClaimExpiresAtUtc))!.IsConcurrencyToken);

        var index = Assert.Single(outbox.GetIndexes());
        Assert.Equal("IX_outbox_messages_pending_dispatch", index.GetDatabaseName());
        Assert.Equal(
            [
                nameof(OutboxMessage.NextAttemptAtUtc),
                nameof(OutboxMessage.ClaimExpiresAtUtc),
                nameof(OutboxMessage.OccurredAtUtc),
                nameof(OutboxMessage.Id)
            ],
            index.Properties.Select(property => property.Name));
        Assert.Equal("processed_at_utc IS NULL AND dead_lettered_at_utc IS NULL", index.GetFilter());

        var inbox = Assert.IsAssignableFrom<IReadOnlyEntityType>(
            model.FindEntityType(typeof(InboxMessage)));
        Assert.Empty(inbox.GetIndexes());
    }

    private static async Task<Guid> PublishAsync(
        ServiceProvider provider,
        string name,
        DateTime occurredAtUtc,
        bool isPoison = false)
    {
        await using var scope = provider.CreateAsyncScope();
        var messageId = scope.ServiceProvider
            .GetRequiredService<IModuleEventPublisher>()
            .Publish(new ReliabilityEvent(name, isPoison), occurredAtUtc);
        await scope.ServiceProvider.GetRequiredService<MercuriusDBContext>().SaveChangesAsync();
        return messageId;
    }

    private static async Task<OutboxMessage> LoadMessageAsync(ServiceProvider provider, Guid messageId)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<MercuriusDBContext>()
            .OutboxMessages
            .AsNoTracking()
            .SingleAsync(message => message.Id == messageId);
    }

    private static ServiceProvider CreateProvider(ReliabilityState state, TimeProvider timeProvider)
    {
        var services = new ServiceCollection();
        var database = PostgresTestDatabase.Create();
        services.AddLogging();
        services.AddDbContext<MercuriusDBContext>(options =>
            options.UseNpgsql(database.ConnectionString));
        services.AddSingleton<PostgresTestDatabaseLease>(_ => database);
        services.AddSingleton(state);
        services.AddSingleton(timeProvider);
        services.AddModuleEventing<MercuriusDBContext>();
        services.AddModuleEventHandler<ReliabilityEvent, ReliabilityHandler>();
        return BuildProvider(services);
    }

    private static ServiceProvider CreateBlockingProvider(BlockingDispatchState state, TimeProvider timeProvider)
    {
        var services = new ServiceCollection();
        var database = PostgresTestDatabase.Create();
        services.AddLogging();
        services.AddDbContext<MercuriusDBContext>(options =>
            options.UseNpgsql(database.ConnectionString));
        services.AddSingleton<PostgresTestDatabaseLease>(_ => database);
        services.AddSingleton(state);
        services.AddSingleton(timeProvider);
        services.AddModuleEventing<MercuriusDBContext>();
        services.AddModuleEventHandler<ReliabilityEvent, BlockingDispatchHandler>();
        return BuildProvider(services);
    }

    private static ServiceProvider CreateFreshLeaseProvider(FreshLeaseState state, TimeProvider timeProvider)
    {
        var services = new ServiceCollection();
        var database = PostgresTestDatabase.Create();
        services.AddLogging();
        services.AddDbContext<MercuriusDBContext>(options =>
            options.UseNpgsql(database.ConnectionString));
        services.AddSingleton<PostgresTestDatabaseLease>(_ => database);
        services.AddSingleton(state);
        services.AddSingleton(timeProvider);
        services.AddModuleEventing<MercuriusDBContext>();
        services.AddModuleEventHandler<ReliabilityEvent, FreshLeaseHandler>();
        return BuildProvider(services);
    }

    private static ServiceProvider CreateStaleOwnerProvider(
        StaleOwnerState state,
        TimeProvider timeProvider,
        PostgresTestDatabaseLease database,
        string consumerName,
        bool initializeDatabase = true,
        bool registerDatabaseLease = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MercuriusDBContext>(options =>
            options.UseNpgsql(database.ConnectionString));
        if (registerDatabaseLease)
            services.AddSingleton<PostgresTestDatabaseLease>(_ => database);
        services.AddSingleton(state);
        services.AddSingleton(timeProvider);
        services.AddModuleEventing<MercuriusDBContext>();
        services.AddSingleton(new StaleOwnerHandler(state, consumerName));
        services.AddSingleton<IModuleEventHandler<ReliabilityEvent>>(provider =>
            provider.GetRequiredService<StaleOwnerHandler>());
        return BuildProvider(services, initializeDatabase, registerDatabaseLease);
    }

    private static ServiceProvider BuildProvider(
        ServiceCollection services,
        bool initializeDatabase = true,
        bool resolveDatabaseLease = true)
    {
        var provider = services.BuildServiceProvider();
        if (resolveDatabaseLease)
            _ = provider.GetRequiredService<PostgresTestDatabaseLease>();

        if (initializeDatabase)
        {
            using var scope = provider.CreateScope();
            PostgresTestDatabase.Initialize(scope.ServiceProvider.GetRequiredService<MercuriusDBContext>());
        }
        return provider;
    }

    private sealed record ReliabilityEvent(string Name, bool IsPoison);

    private sealed class ReliabilityState
    {
        public List<string> Attempts { get; } = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Block { get; init; }
    }

    private sealed class BlockingDispatchState
    {
        public int HandlerCalls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FreshLeaseState
    {
        public FreshLeaseState(MutableTimeProvider timeProvider)
        {
            TimeProvider = timeProvider;
        }

        public int HandlerCalls;
        public MutableTimeProvider TimeProvider { get; }
        public TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class StaleOwnerState
    {
        public int HandlerCalls;
        public bool FailFirst { get; init; }
        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ReliabilityHandler : IModuleEventHandler<ReliabilityEvent>
    {
        private readonly ReliabilityState _state;

        public ReliabilityHandler(ReliabilityState state)
        {
            _state = state;
        }

        public string ConsumerName => "reliability-handler";

        public async Task HandleAsync(
            ReliabilityEvent payload,
            ModuleEventContext context,
            CancellationToken cancellationToken = default)
        {
            _state.Attempts.Add(payload.Name);
            if (payload.IsPoison)
                throw new InvalidOperationException("planned poison failure");

            if (_state.Block)
            {
                _state.Entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }
    }

    private sealed class BlockingDispatchHandler : IModuleEventHandler<ReliabilityEvent>
    {
        private readonly BlockingDispatchState _state;

        public BlockingDispatchHandler(BlockingDispatchState state)
        {
            _state = state;
        }

        public string ConsumerName => "blocking-dispatch-handler";

        public async Task HandleAsync(
            ReliabilityEvent payload,
            ModuleEventContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _state.HandlerCalls);
            _state.Entered.TrySetResult();
            await _state.Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class FreshLeaseHandler : IModuleEventHandler<ReliabilityEvent>
    {
        private readonly FreshLeaseState _state;

        public FreshLeaseHandler(FreshLeaseState state)
        {
            _state = state;
        }

        public string ConsumerName => "fresh-lease-handler";

        public async Task HandleAsync(
            ReliabilityEvent payload,
            ModuleEventContext context,
            CancellationToken cancellationToken = default)
        {
            var callNumber = Interlocked.Increment(ref _state.HandlerCalls);
            if (callNumber == 1)
            {
                _state.TimeProvider.Advance(TimeSpan.FromMinutes(1));
                return;
            }

            _state.SecondEntered.TrySetResult();
            await _state.ReleaseSecond.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class StaleOwnerHandler : IModuleEventHandler<ReliabilityEvent>
    {
        private readonly StaleOwnerState _state;
        private readonly string _consumerName;

        public StaleOwnerHandler(StaleOwnerState state, string consumerName)
        {
            _state = state;
            _consumerName = consumerName;
        }

        public string ConsumerName => _consumerName;

        public async Task HandleAsync(
            ReliabilityEvent payload,
            ModuleEventContext context,
            CancellationToken cancellationToken = default)
        {
            var callNumber = Interlocked.Increment(ref _state.HandlerCalls);
            if (callNumber == 1)
            {
                _state.FirstEntered.TrySetResult();
                await _state.ReleaseFirst.Task.WaitAsync(cancellationToken);
                if (_state.FailFirst)
                    throw new InvalidOperationException("planned stale-owner failure");
            }
        }
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public MutableTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed) => _utcNow += elapsed;
    }
}
