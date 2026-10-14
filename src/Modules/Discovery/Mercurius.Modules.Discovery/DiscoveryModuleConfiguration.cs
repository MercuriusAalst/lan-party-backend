using Mercurius.Modules.Discovery.Application;
using Mercurius.Modules.Discovery.Contracts;
using Mercurius.Modules.Discovery.Endpoints;
using Mercurius.Modules.Discovery.Infrastructure;
using Mercurius.Modules.Tournament.Contracts;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Sponsorship.Contracts.V1;
using Mercurius.Modules.Teams.Contracts;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Extensions;

namespace Mercurius.Modules.Discovery;

public static class DiscoveryModuleConfiguration
{
    public static IServiceCollection AddDiscoveryModule<TDbContext>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TDbContext : DbContext
    {
        services.TryAddScoped<IDiscoveryDbContext, DiscoveryDbContextAdapter<TDbContext>>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IDiscoveryModule, DiscoveryModuleFacade>();
        services.AddScoped<SearchDocumentProjector>();
        services.AddScoped<DiscoveryRebuildOwnership>();
        services.AddScoped<SearchIndexRebuildService>();
        services.AddHostedService<SearchIndexRebuildWorker>();
        services.AddModuleEventHandler<UserProfileChangedIntegrationEvent, IdentitySearchProjectionHandler>();
        services.AddModuleEventHandler<UserDeletedIntegrationEvent, IdentitySearchProjectionHandler>();
        services.AddModuleEventHandler<TeamCreatedIntegrationEvent, TeamSearchProjectionHandler>();
        services.AddModuleEventHandler<TeamRenamedIntegrationEvent, TeamSearchProjectionHandler>();
        services.AddModuleEventHandler<TeamDeletedIntegrationEvent, TeamSearchProjectionHandler>();
        services.AddModuleEventHandler<TournamentCreatedIntegrationEvent, TournamentSearchProjectionHandler>();
        services.AddModuleEventHandler<TournamentUpdatedIntegrationEvent, TournamentSearchProjectionHandler>();
        services.AddModuleEventHandler<TournamentCanceledIntegrationEvent, TournamentSearchProjectionHandler>();
        services.AddModuleEventHandler<TournamentDeletedIntegrationEvent, TournamentSearchProjectionHandler>();
        services.AddModuleEventHandler<SponsorCreated, SponsorshipSearchProjectionHandler>();
        services.AddModuleEventHandler<SponsorUpdated, SponsorshipSearchProjectionHandler>();
        services.AddModuleEventHandler<SponsorDeleted, SponsorshipSearchProjectionHandler>();

        return services;
    }

    public static ModelBuilder ApplyDiscoveryModelConfiguration(this ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new SearchDocumentConfiguration());
        modelBuilder.ApplyConfiguration(new SearchIndexRebuildDocumentConfiguration());
        modelBuilder.ApplyConfiguration(new SearchIndexRebuildJobConfiguration());

        return modelBuilder;
    }

    public static IEndpointRouteBuilder MapDiscoveryModule(this IEndpointRouteBuilder endpoints)
    {
        DiscoveryEndpoints.Map(endpoints);
        return endpoints;
    }
}
