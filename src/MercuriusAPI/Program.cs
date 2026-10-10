using Mercurius.LAN.API.Data;
using Mercurius.LAN.API.Configuration;
using Mercurius.LAN.API.Hubs;
using Mercurius.LAN.API.Middleware;
using Mercurius.Modules.Tournament;
using Mercurius.Modules.Discovery;
using Mercurius.Modules.Identity;
using Mercurius.Modules.Media;
using Mercurius.Modules.Sponsorship;
using Mercurius.Modules.Teams;
using Platform;
using Platform.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;

namespace Mercurius.LAN.API;

public class Program
{
    private const string CorsPolicyName = "AllowMercuriusAalst";

    public static void Main(string[] args) => CreateApp(args).Run();

    public static WebApplication CreateApp(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddEnvironmentVariables("Mercurius.LAN.API_");
        if (!builder.Environment.IsDevelopment())
        {
            foreach (var requiredKey in new[] { "FileStorage:Location", "Auth0:Audience" })
            {
                if (string.IsNullOrWhiteSpace(builder.Configuration[requiredKey]))
                    throw new InvalidOperationException($"{requiredKey} must be configured outside Development.");
            }
        }

        var mediaUploadRequestLimits = MediaUploadRequestLimits.FromConfiguration(builder.Configuration);

        builder.WebHost.ConfigureKestrel(options =>
            options.Limits.MaxRequestBodySize = mediaUploadRequestLimits.MaxRequestBodySize);
        builder.Services.Configure<FormOptions>(options =>
            options.MultipartBodyLengthLimit = mediaUploadRequestLimits.MaxFileSizeInBytes);

        builder.Services.AddDbContext<MercuriusDBContext>(options =>
            options.UseNpgsql(builder.Configuration.GetConnectionString("MercuriusDB")));
        builder.Services.AddModuleEventing<MercuriusDBContext>();
        builder.Services.AddMediaModule(builder.Configuration);

        builder.Services.AddValidation();
        builder.Services.AddVersionedSwagger(
            builder.Environment,
            documentTitle: "Mercurius API",
            includeXmlComments: true,
            useEnumSchemaFilter: true);
        builder.Services.AddIdentityModule<MercuriusDBContext>(builder.Configuration);
        builder.Services.AddTeamsModule<MercuriusDBContext>(builder.Configuration);
        builder.Services.AddSponsorshipModule<MercuriusDBContext>(builder.Configuration);
        builder.Services.AddTournamentModule<MercuriusDBContext>(builder.Configuration);
        builder.Services.AddDiscoveryModule<MercuriusDBContext>(builder.Configuration);
        builder.Services.AddApiProblemDetails<ApiExceptionHandler>();
        builder.Services.AddHttpConventions();
        builder.Services.AddAuth0JwtAuthentication(
            builder.Configuration.GetSection("Auth0"),
            TeamManagementHub.Route);
        builder.Services.AddSingleton<TeamManagementHubInvocationRateLimitFilter>();
        builder.Services.AddRealtimeNotificationServices<TeamManagementHub>(options =>
            options.AddFilter<TeamManagementHubInvocationRateLimitFilter>());
        var rateLimitingSection = builder.Configuration.GetSection("RateLimiting");
        builder.Services.AddFixedWindowRateLimiting(new FixedWindowRateLimitingOptions
        {
            GlobalPermitLimit = rateLimitingSection.GetValue("GlobalPermitLimit", 120),
            PolicyPermitLimit = rateLimitingSection.GetValue("SearchPermitLimit", 30),
            Window = TimeSpan.FromSeconds(rateLimitingSection.GetValue("WindowSeconds", 60)),
            UnconditionalPolicyName = RateLimitPolicies.AnonymousSearch,
            ConditionalPolicyName = RateLimitPolicies.AuthenticatedSearch,
            ConditionalQueryParameterName = "query"
        });
        builder.Services.AddWildcardSubdomainCors(
            CorsPolicyName,
            allowedOrigin: "https://*.mercurius-aalst.be");

        var app = builder.Build();
        app.UseTransportSecurity(app.Environment);
        app.UseCors(CorsPolicyName);
        app.ApplyMigrations<MercuriusDBContext>();
        app.UseApiExceptionHandling();
        app.UseSecurityPipeline();
        app.UseImageflowWithCaching(
            requestPath: "/images",
            storagePath: app.Configuration["FileStorage:Location"],
            cacheControl: "public, max-age=31536000");
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(Path.Combine(AppContext.BaseDirectory, "staticfiles")),
            RequestPath = "/staticfiles"
        });
        app.UseVersionedSwaggerUI(customJavascriptPath: "/staticfiles/swagger-custom.js");

        app.MapTournamentModule();
        app.MapIdentityModule();
        app.MapTeamsModule();
        app.MapSponsorshipModule();
        app.MapDiscoveryModule();
        app.MapHub<TeamManagementHub>(
                TeamManagementHub.Route,
                options => options.CloseOnAuthenticationExpiration = true)
            .RequireAuthorization();

        return app;
    }
}
