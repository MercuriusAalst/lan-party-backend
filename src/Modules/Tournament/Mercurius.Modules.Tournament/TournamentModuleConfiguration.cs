using Mercurius.Modules.Tournament.Application;
using Mercurius.Modules.Tournament.Application.Services;
using Mercurius.Modules.Tournament.Application.Services.BracketTypes;
using Mercurius.Modules.Tournament.Contracts;
using Mercurius.Modules.Tournament.Endpoints;
using Mercurius.Modules.Tournament.Infrastructure;
using Mercurius.Modules.Teams.Contracts;
using Mercurius.Modules.Identity.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Extensions;

namespace Mercurius.Modules.Tournament;

public static class TournamentModuleConfiguration
{
    public static IServiceCollection AddTournamentModule<TDbContext>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TDbContext : DbContext
    {
        services.TryAddScoped<ITournamentDbContext, TournamentDbContextAdapter<TDbContext>>();
        services.AddTransient<ITournamentModule, TournamentModuleFacade>();
        services.AddTransient<TournamentEligibilityEvaluator>();
        services.AddTransient<TournamentDtoMapper>();
        services.AddTransient<RegistrationMappingContextBuilder>();
        services.AddTransient<TournamentRegistrationPersistenceCoordinator>();
        services.AddTransient<TournamentRegistrationReadModelService>();
        services.AddTransient<PublicProfileMatchSummaryReadService>();
        services.AddTransient<ITournamentQueries, TournamentService>();
        services.AddTransient<IFeaturedHomepageTournamentService, FeaturedHomepageTournamentService>();
        services.AddTransient<ITournamentManagementCommands, TournamentService>();
        services.AddTransient<ITournamentLifecycleCommands, TournamentService>();
        services.AddTransient<ILeaderboardService, LeaderboardService>();
        services.AddTransient<MatchBracketImpactAnalyzer>();
        services.AddTransient<IMatchService, MatchService>();
        services.AddModuleEventHandler<MatchResolutionRequiredIntegrationEvent, MatchResolutionNotificationHandler>();
        services.AddTransient<DeletedUserTournamentCleanupService>();
        services.AddModuleEventHandler<UserDeletedIntegrationEvent, DeletedUserTournamentCleanupHandler>();
        services.AddModuleEventHandler<TeamCaptainSucceededIntegrationEvent, DeletedUserTournamentCleanupHandler>();
        services.AddHostedService<MatchDeadlineProcessor>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<AssignedAdminAvailabilityCache>();
        services.AddOptions<MatchResolutionOptions>()
            .Bind(configuration.GetSection(MatchResolutionOptions.SectionName))
            .Validate(
                options => options.EscalationAfterMinutes is > 0 and <= 10080,
                "Tournament:MatchResolution:EscalationAfterMinutes must be between 1 and 10080.")
            .ValidateOnStart();
        services.AddTransient<ITournamentRegistrationService, TournamentRegistrationService>();
        services.AddTransient<IMatchModeratorFactory, MatchModeratorFactory>();
        services.AddTransient<SingleEliminationMatchModerator>();
        services.AddTransient<DoubleEliminationMatchModerator>();
        services.AddTransient<RoundRobinMatchModerator>();
        services.AddTransient<LeaderboardMatchModerator>();
        services.AddTransient<ITournamentRealtimePublisher, TournamentRealtimePublisher>();
        services.AddTransient<ITeamTournamentReadService, TournamentTeamReadService>();

        return services;
    }

    public static ModelBuilder ApplyTournamentModelConfiguration(this ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new Infrastructure.TournamentConfiguration());
        modelBuilder.ApplyConfiguration(new Infrastructure.FeaturedHomepageTournamentSelectionConfiguration());
        modelBuilder.ApplyConfiguration(new MatchConfiguration());
        modelBuilder.ApplyConfiguration(new MatchResolutionNotificationConfiguration());
        modelBuilder.ApplyConfiguration(new TournamentRegistrationConfiguration());
        modelBuilder.ApplyConfiguration(new TournamentRegistrationRosterMemberConfiguration());
        modelBuilder.ApplyConfiguration(new PlacementConfiguration());
        modelBuilder.ApplyConfiguration(new PlacementUserConfiguration());
        modelBuilder.ApplyConfiguration(new PlacementTeamConfiguration());
        modelBuilder.ApplyConfiguration(new LeaderboardParticipantConfiguration());
        modelBuilder.ApplyConfiguration(new LeaderboardAttemptConfiguration());
        modelBuilder.ApplyConfiguration(new PlacementLeaderboardParticipantConfiguration());

        return modelBuilder;
    }

    public static IEndpointRouteBuilder MapTournamentModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapTournamentEndpoints();
        endpoints.MapFeaturedHomepageTournamentEndpoints();
        endpoints.MapTournamentRegistrationEndpoints();
        endpoints.MapMatchEndpoints();
        endpoints.MapPublicProfileMatchSummaryEndpoints();
        endpoints.MapLeaderboardEndpoints();

        return endpoints;
    }
}
