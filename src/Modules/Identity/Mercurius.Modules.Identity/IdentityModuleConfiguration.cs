using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mercurius.Modules.Identity.Endpoints;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Identity.Infrastructure;
using Mercurius.Modules.Identity.Options;
using Mercurius.Modules.Identity.Application.Services;
using Mercurius.Modules.Identity.Application.Services.Auth0;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mercurius.Modules.Identity;

public static class IdentityModuleConfiguration
{
    public static IServiceCollection AddIdentityModule<TDbContext>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TDbContext : DbContext
    {
        services.TryAddScoped<IIdentityDbContext, IdentityDbContextAdapter<TDbContext>>();
        services.Configure<Auth0ManagementOptions>(configuration.GetSection(Auth0ManagementOptions.SectionName));
        services.AddTransient<IIdentityModule, IdentityModuleFacade>();
        services.AddHttpClient<IAuth0ManagementService, Auth0ManagementService>();
        services.AddTransient<IUserService, UserService>();
        services.Decorate<IUserService, UserIntegrationEventPublishingService>();
        services.Decorate<IUserService, UserValidationService>();

        return services;
    }

    public static ModelBuilder ApplyIdentityModelConfiguration(this ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new UserConfiguration());

        return modelBuilder;
    }

    public static IEndpointRouteBuilder MapIdentityModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapUserEndpoints();
        return endpoints;
    }
}
