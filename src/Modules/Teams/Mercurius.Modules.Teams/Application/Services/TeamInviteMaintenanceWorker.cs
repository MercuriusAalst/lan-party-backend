using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mercurius.Modules.Teams.Application.Services;

internal sealed class TeamInviteMaintenanceWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TeamInviteMaintenanceWorker> _logger;
    private readonly TeamInviteMaintenanceOptions _options;

    public TeamInviteMaintenanceWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<TeamInviteMaintenanceWorker> logger,
        IOptions<TeamInviteMaintenanceOptions> options)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var maintenanceService = scope.ServiceProvider.GetRequiredService<TeamInviteMaintenanceService>();
                await maintenanceService.RunBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Team invite maintenance cycle failed.");
            }

            try
            {
                await Task.Delay(_options.MaintenanceInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
