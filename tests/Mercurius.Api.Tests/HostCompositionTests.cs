using System.Net;
using Mercurius.LAN.API;
using Mercurius.TestInfrastructure;
using Microsoft.AspNetCore.Builder;

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
    public async Task HealthEndpoints_AreAnonymousAndNotRateLimited()
    {
        // Arrange
        await using var database = PostgresTestDatabase.Create();
        await using var app = Program.CreateApp(
        [
            .. CreateProductionArgs(database.ConnectionString),
            "--RateLimiting:GlobalPermitLimit=1"
        ]);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

            // Act
            var responses = new List<HttpResponseMessage>();
            foreach (var path in new[] { "/health/live", "/health/ready" })
            {
                for (var attempt = 0; attempt < 3; attempt++)
                    responses.Add(await client.GetAsync(path));
            }

            // Assert
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static string[] CreateProductionArgs(string connectionString) =>
    [
        "--environment=Production",
        "--Logging:LogLevel:Default=Warning",
        $"--ConnectionStrings:MercuriusDB={connectionString}",
        $"--FileStorage:Location={Path.Combine(Path.GetTempPath(), "mercurius-host-tests")}",
        "--Auth0:Audience=https://api.example.test"
    ];
}
