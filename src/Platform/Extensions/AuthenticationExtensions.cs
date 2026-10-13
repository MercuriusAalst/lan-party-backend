using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;

namespace Platform.Extensions;

public static class AuthenticationExtensions
{
    public static IServiceCollection AddAuth0JwtAuthentication(
        this IServiceCollection services,
        IConfigurationSection auth0Settings,
        string realtimeHubPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realtimeHubPath);

        var auth0Authority = auth0Settings["Authority"]?.TrimEnd('/');

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.Authority = auth0Authority;
            options.Audience = auth0Settings["Audience"];
            options.MapInboundClaims = false;
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    if (context.Result is not null ||
                        !string.IsNullOrEmpty(context.Token) ||
                        context.Request.Headers.ContainsKey(HeaderNames.Authorization) ||
                        !string.Equals(context.Request.Path.Value, realtimeHubPath, StringComparison.Ordinal) ||
                        !context.Request.Query.TryGetValue("access_token", out var accessTokens) ||
                        accessTokens.Count != 1 ||
                        string.IsNullOrWhiteSpace(accessTokens[0]))
                    {
                        return Task.CompletedTask;
                    }

                    context.Token = accessTokens[0];
                    return Task.CompletedTask;
                }
            };
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                RequireSignedTokens = true,
                NameClaimType = "sub",
                RoleClaimType = auth0Settings["RoleClaimType"],
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256]
            };
        });
        services.AddAuthorization(options =>
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        return services;
    }
}
