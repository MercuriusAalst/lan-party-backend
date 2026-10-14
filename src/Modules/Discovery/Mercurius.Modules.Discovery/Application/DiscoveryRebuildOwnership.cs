using System.Data;
using System.Data.Common;
using Mercurius.Modules.Discovery.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Mercurius.Modules.Discovery.Application;

internal sealed class DiscoveryRebuildOwnership
{
    private const long LockKey = 0x4D45524355524955L;
    private readonly IDiscoveryDbContext _dbContext;
    private readonly ILogger<DiscoveryRebuildOwnership> _logger;
    private DbConnection? _ownedConnection;
    private int _backendPid;
    private volatile bool _ownershipLost;

    public DiscoveryRebuildOwnership(
        IDiscoveryDbContext dbContext,
        ILogger<DiscoveryRebuildOwnership> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<Lease?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        if (_dbContext.RetriesOnFailure)
            throw new InvalidOperationException("Discovery rebuild ownership cannot use a retrying database execution strategy.");

        DbConnection? connection = null;
        try
        {
            await _dbContext.OpenConnectionAsync(cancellationToken);
            connection = _dbContext.Connection;

            if (connection.State != ConnectionState.Open)
                throw new InvalidOperationException("The Discovery database connection did not remain open.");

            var acquired = await ExecuteBooleanAsync(connection, "SELECT pg_try_advisory_lock(@key);", LockKey, cancellationToken);
            if (!acquired)
            {
                await _dbContext.CloseConnectionAsync();
                return null;
            }

            _backendPid = await ReadBackendPidAsync(connection, cancellationToken);
            _ownedConnection = connection;
            _ownershipLost = false;
            connection.StateChange += OnConnectionStateChange;
            return new Lease(this);
        }
        catch
        {
            _ownershipLost = true;
            _dbContext.DiscardConnection();
            await TryCloseConnectionAsync();
            throw;
        }
    }

    public async Task EnsureOwnedAsync(CancellationToken cancellationToken)
    {
        var connection = _ownedConnection;
        if (_ownershipLost || connection is null ||
            !ReferenceEquals(connection, _dbContext.Connection) ||
            connection.State != ConnectionState.Open)
        {
            _ownershipLost = true;
            throw new DiscoveryRebuildOwnershipLostException();
        }

        int backendPid;
        try
        {
            backendPid = await ReadBackendPidAsync(connection, cancellationToken, _dbContext.CurrentTransaction);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _ownershipLost = true;
            throw new DiscoveryRebuildOwnershipLostException(exception);
        }

        if (backendPid != _backendPid)
        {
            _ownershipLost = true;
            throw new DiscoveryRebuildOwnershipLostException();
        }
    }

    private async ValueTask ReleaseAsync()
    {
        var connection = _ownedConnection;
        var safelyUnlocked = false;
        try
        {
            if (!_ownershipLost && connection is not null &&
                ReferenceEquals(connection, _dbContext.Connection) &&
                connection.State == ConnectionState.Open &&
                await ReadBackendPidAsync(connection, CancellationToken.None) == _backendPid)
            {
                safelyUnlocked = await ExecuteBooleanAsync(
                    connection,
                    "SELECT pg_advisory_unlock(@key);",
                    LockKey,
                    CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not confirm release of the Discovery rebuild advisory lock; discarding its connection.");
        }
        finally
        {
            if (connection is not null)
                connection.StateChange -= OnConnectionStateChange;
            if (!safelyUnlocked)
                _dbContext.DiscardConnection();

            await TryCloseConnectionAsync();
            _ownedConnection = null;
            _ownershipLost = false;
        }
    }

    private async Task TryCloseConnectionAsync()
    {
        try
        {
            await _dbContext.CloseConnectionAsync();
        }
        catch (Exception exception)
        {
            _dbContext.DiscardConnection();
            _logger.LogWarning(exception, "Could not close the Discovery rebuild database connection cleanly.");
        }
    }

    private void OnConnectionStateChange(object? sender, StateChangeEventArgs eventArgs)
    {
        if (_ownedConnection is null)
            return;

        if (eventArgs.OriginalState == ConnectionState.Open && eventArgs.CurrentState != ConnectionState.Open)
            _ownershipLost = true;

        if (eventArgs.OriginalState != ConnectionState.Open && eventArgs.CurrentState == ConnectionState.Open)
        {
            _ownershipLost = true;
            throw new DiscoveryRebuildOwnershipLostException();
        }
    }

    private static async Task<bool> ExecuteBooleanAsync(
        DbConnection connection,
        string sql,
        long key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "key";
        parameter.DbType = DbType.Int64;
        parameter.Value = key;
        command.Parameters.Add(parameter);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<int> ReadBackendPidAsync(
        DbConnection connection,
        CancellationToken cancellationToken,
        DbTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_backend_pid();";
        command.Transaction = transaction;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    internal sealed class Lease(DiscoveryRebuildOwnership owner) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref _disposed, 1) == 0 ? owner.ReleaseAsync() : ValueTask.CompletedTask;
    }
}

internal sealed class DiscoveryRebuildOwnershipLostException(Exception? innerException = null)
    : InvalidOperationException("The Discovery rebuild database session was lost; the worker must reacquire ownership before continuing.", innerException);
