using Mercurius.Modules.Sponsorship.Application;
using Mercurius.Modules.Sponsorship.Application.Services;
using Mercurius.Modules.Sponsorship.Endpoints;
using Mercurius.Modules.Sponsorship.Infrastructure;
using Mercurius.Modules.Sponsorship.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mercurius.Modules.Sponsorship;

public static class SponsorshipModuleConfiguration
{
    public static IServiceCollection AddSponsorshipModule<TDbContext>(
        this IServiceCollection services,
        IConfiguration _)
        where TDbContext : DbContext
    {
        services.TryAddScoped<ISponsorshipDbContext, SponsorshipDbContextAdapter<TDbContext>>();
        services.AddTransient<SponsorshipOutboxWriter>();
        services.AddTransient<ISponsorService, SponsorService>();
        services.AddTransient<ISponsorshipModule, SponsorshipModuleFacade>();

        return services;
    }

    public static ModelBuilder ApplySponsorshipModelConfiguration(this ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new SponsorConfiguration());
        modelBuilder.ApplyConfiguration(new TournamentSponsorPlacementConfiguration());

        return modelBuilder;
    }

    public static IEndpointRouteBuilder MapSponsorshipModule(
        this IEndpointRouteBuilder endpoints,
        long? sponsorLogoRequestBodySize = null)
    {
        endpoints.MapSponsorEndpoints(sponsorLogoRequestBodySize);

        return endpoints;
    }
}
