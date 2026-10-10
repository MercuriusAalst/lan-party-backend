using System.Net;
using Mercurius.LAN.API;
using Mercurius.TestInfrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Mercurius.Api.Tests;

public sealed class HostCompositionTests
{
    [Theory]
    [InlineData("FileStorage:Location")]
    [InlineData("Auth0:Audience")]
    public void CreateApp_OutsideDevelopment_FailsWhenRequiredSettingIsMissing(string missingKey)
    {
        // Arrange
        var args = CreateProductionArgs("Host=localhost")
            .Where(arg => !arg.StartsWith($"--{missingKey}=", StringComparison.Ordinal))
            .ToArray();

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => Program.CreateApp(args));

        // Assert
        Assert.Contains(missingKey, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublicSurface_IsAnonymousAndOperationalEndpointsAreNotRateLimited()
    {
        // Arrange
        Directory.CreateDirectory(StorageLocation);
        var imageName = $"{Guid.NewGuid():N}.gif";
        await File.WriteAllBytesAsync(
            Path.Combine(StorageLocation, imageName),
            Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw=="));
        await using var database = PostgresTestDatabase.Create();
        await using var app = Program.CreateApp(
        [
            .. CreateProductionArgs(database.ConnectionString),
            "--RateLimiting:GlobalPermitLimit=2"
        ]);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

            // Act
            var responses = new List<HttpResponseMessage>();
            foreach (var path in new[] { "/health/live", "/health/ready", $"/images/{imageName}", "/staticfiles/swagger-custom.js" })
            {
                for (var attempt = 0; attempt < 3; attempt++)
                    responses.Add(await client.GetAsync(path));
            }

            var swaggerUi = await client.GetAsync("/swagger/index.html");
            var swaggerDocument = await client.GetAsync("/swagger/v1/swagger.json");
            var unmappedPath = await client.GetAsync("/not-mapped");

            // Assert
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            Assert.Equal(HttpStatusCode.OK, swaggerUi.StatusCode);
            Assert.Equal(HttpStatusCode.OK, swaggerDocument.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, unmappedPath.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task CreateApp_EveryEndpointDeclaresAuthorizeOrAllowAnonymous()
    {
        // Arrange
        await using var app = Program.CreateApp(
        [
            .. CreateProductionArgs("Host=localhost"),
            "--Database:ApplyMigrationsOnStartup=false"
        ]);

        // Act
        var unmarkedEndpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .Where(endpoint =>
                endpoint.Metadata.GetMetadata<IAuthorizeData>() is null &&
                endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Select(endpoint => endpoint.DisplayName)
            .ToList();

        // Assert
        Assert.NotEmpty(((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints));
        Assert.Empty(unmarkedEndpoints);
        Assert.Contains(
            app.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value.FallbackPolicy!.Requirements,
            requirement => requirement is DenyAnonymousAuthorizationRequirement);
    }

    [Theory]
    [InlineData(null, "https://*.mercurius-aalst.be")]
    [InlineData("https://*.example.test", "https://*.example.test")]
    public async Task CreateApp_ReadsCorsOriginsFromConfiguration(string? configuredOrigin, string expectedOrigin)
    {
        // Arrange
        string[] args =
        [
            .. CreateProductionArgs("Host=localhost"),
            "--Database:ApplyMigrationsOnStartup=false",
            .. configuredOrigin is null ? [] : new[] { $"--Cors:AllowedOrigins:0={configuredOrigin}" }
        ];
        await using var app = Program.CreateApp(args);

        // Act
        var policy = app.Services.GetRequiredService<IOptions<CorsOptions>>().Value.GetPolicy("AllowMercuriusAalst");

        // Assert
        Assert.Equal([expectedOrigin], policy!.Origins);
    }

    private static readonly string StorageLocation = Path.Combine(Path.GetTempPath(), "mercurius-host-tests");

    private static string[] CreateProductionArgs(string connectionString) =>
    [
        "--environment=Production",
        "--Logging:LogLevel:Default=Warning",
        $"--ConnectionStrings:MercuriusDB={connectionString}",
        $"--FileStorage:Location={StorageLocation}",
        "--Auth0:Audience=https://api.example.test"
    ];
}
