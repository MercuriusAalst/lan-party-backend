using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mercurius.Modules.Teams.Contracts;
using Mercurius.Modules.Teams.Endpoints;
using Mercurius.Modules.Teams.Infrastructure;
using Mercurius.Modules.Teams.Application.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mercurius.Modules.Teams;

public static class TeamsModuleConfiguration
{
    public static IServiceCollection AddTeamsModule<TDbContext>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TDbContext : DbContext
    {
        services.TryAddScoped<ITeamsDbContext, TeamsDbContextAdapter<TDbContext>>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddTransient<ITeamsModule, TeamsModuleFacade>();
        services.AddScoped<TeamService>();
        services.AddScoped<ITeamQueries>(serviceProvider => serviceProvider.GetRequiredService<TeamService>());
        services.AddScoped<ITeamLogoCommands>(serviceProvider => serviceProvider.GetRequiredService<TeamService>());
        services.AddScoped<TeamEventPublishingDecorator>();
        services.AddScoped<ITeamManagementCommands>(serviceProvider => serviceProvider.GetRequiredService<TeamEventPublishingDecorator>());
        services.AddScoped<ITeamInviteWorkflows>(serviceProvider => serviceProvider.GetRequiredService<TeamEventPublishingDecorator>());
        services.AddOptions<TeamInviteMaintenanceOptions>()
            .Bind(configuration.GetSection(TeamInviteMaintenanceOptions.SectionName))
            .Validate(options => options.RetentionDays is >= 1 and <= 3650, "TeamInvite:RetentionDays must be between 1 and 3650.")
            .Validate(options => options.MaintenanceBatchSize is >= 1 and <= 1000, "TeamInvite:MaintenanceBatchSize must be between 1 and 1000.")
            .Validate(options => options.MaintenanceIntervalSeconds is >= 1 and <= 86400, "TeamInvite:MaintenanceIntervalSeconds must be between 1 and 86400.")
            .Validate(options =>
                    options.MaintenanceEventConcurrency >= 1 &&
                    options.MaintenanceEventConcurrency <= Math.Min(64, options.MaintenanceBatchSize),
                "TeamInvite:MaintenanceEventConcurrency must be between 1 and the maintenance batch size, with a maximum of 64.")
            .ValidateOnStart();
        services.AddScoped<TeamInviteMaintenanceService>();
        services.AddHostedService<TeamInviteMaintenanceWorker>();
        services.AddTransient<ITeamEndpointService, TeamEndpointService>();
        services.AddTransient<ITeamEventPublisher, RealtimeTeamEventPublisher>();
        services.AddTransient<Contracts.ITeamRealtimeAuthorizer, EfTeamRealtimeAuthorizer>();

        return services;
    }

    public static ModelBuilder ApplyTeamsModelConfiguration(this ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new TeamConfiguration());
        modelBuilder.ApplyConfiguration(new TeamMemberConfiguration());
        modelBuilder.ApplyConfiguration(new TeamInviteConfiguration());

        return modelBuilder;
    }

    public static IEndpointRouteBuilder MapTeamsModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapTeamEndpoints();
        return endpoints;
    }
}
