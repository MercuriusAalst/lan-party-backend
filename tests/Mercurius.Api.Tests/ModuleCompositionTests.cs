using Mercurius.LAN.API.Data;
using Mercurius.Modules.Identity;
using Mercurius.Modules.Teams;
using Mercurius.Modules.Tournament;
using Mercurius.Modules.Tournament.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mercurius.Api.Tests;

public sealed class ModuleCompositionTests
{
    [Fact]
    public void AddIdentityTeamsAndTournamentModules_ResolvesTournamentModule()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddDbContext<MercuriusDBContext>(options =>
            options.UseNpgsql());
        services.AddIdentityModule<MercuriusDBContext>(configuration);
        services.AddTeamsModule<MercuriusDBContext>(configuration);
        services.AddTournamentModule<MercuriusDBContext>(configuration);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<TournamentModuleFacade>(
            scope.ServiceProvider.GetRequiredService<ITournamentModule>());
    }
}
