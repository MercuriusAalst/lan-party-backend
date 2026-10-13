namespace Platform.Extensions;

public static class CorsExtensions
{
    public static IServiceCollection AddWildcardSubdomainCors(
        this IServiceCollection services,
        string policyName,
        params string[] allowedOrigins)
    {
        services.AddCors(options =>
        {
            options.AddPolicy(policyName, policy =>
            {
                policy.WithOrigins(allowedOrigins)
                    .SetIsOriginAllowedToAllowWildcardSubdomains()
                    .AllowAnyHeader()
                    .AllowAnyMethod();
            });
        });

        return services;
    }
}
