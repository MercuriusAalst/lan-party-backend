using Asp.Versioning;
using Asp.Versioning.ApiExplorer;
using Platform.Swagger;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.OpenApi.Models;

namespace Platform.Extensions;

public static class SwaggerExtensions
{
    public static IServiceCollection AddVersionedSwagger(
        this IServiceCollection services,
        IHostEnvironment environment,
        string documentTitle,
        bool includeXmlComments,
        bool useEnumSchemaFilter)
    {
        services.AddEndpointsApiExplorer();
        services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(1, 0);
            options.AssumeDefaultVersionWhenUnspecified = true;
            options.ReportApiVersions = true;
            options.ApiVersionReader = ApiVersionReader.Combine(new UrlSegmentApiVersionReader());
        }).AddApiExplorer(options =>
        {
            options.GroupNameFormat = "'v'VVV";
            options.SubstituteApiVersionInUrl = true;
        });

        services.ConfigureOptions<ConfigureSwaggerOptions>();
        services.Configure<SwaggerDocumentOptions>(options => options.Title = documentTitle);
        services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();

        services.ConfigureSwaggerGen(options =>
        {
            if (useEnumSchemaFilter)
                options.SchemaFilter<EnumSchemaFilter>();

            if (!includeXmlComments)
                return;

            var xmlDocFile = Path.Combine(AppContext.BaseDirectory, $"{environment.ApplicationName}.xml");
            if (File.Exists(xmlDocFile))
                options.IncludeXmlComments(xmlDocFile);
        });

        services.AddSwaggerGen(options =>
        {
            options.DocumentFilter<SecurityTrimming>();
            options.AddSecurityDefinition("BearerDefinition", new OpenApiSecurityScheme
            {
                Name = environment.ApplicationName,
                Type = SecuritySchemeType.ApiKey,
                Scheme = "Bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description = "Enter your security token."
            });
            options.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = "BearerDefinition"
                        }
                    },
                    []
                }
            });
        });

        return services;
    }

    public static WebApplication UseVersionedSwaggerUI(
        this WebApplication app,
        string? customJavascriptPath = null)
    {
        var apiVersionProvider = app.Services.GetRequiredService<IApiVersionDescriptionProvider>();

        // Endpoint (not middleware) so the document is generated after authentication, keeping SecurityTrimming user-aware.
        app.MapSwagger().AllowAnonymous();
        app.UseSwaggerUI(options =>
        {
            foreach (var description in apiVersionProvider.ApiVersionDescriptions)
            {
                options.SwaggerEndpoint(
                    $"/swagger/{description.GroupName}/swagger.json",
                    description.GroupName.ToUpperInvariant());
            }

            if (!string.IsNullOrWhiteSpace(customJavascriptPath))
                options.InjectJavascript(customJavascriptPath);
        });

        return app;
    }
}
