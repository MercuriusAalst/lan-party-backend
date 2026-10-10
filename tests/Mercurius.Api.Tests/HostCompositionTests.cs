using Mercurius.LAN.API;

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

    private static string[] CreateProductionArgs(string connectionString) =>
    [
        "--environment=Production",
        $"--ConnectionStrings:MercuriusDB={connectionString}",
        $"--FileStorage:Location={Path.Combine(Path.GetTempPath(), "mercurius-host-tests")}",
        "--Auth0:Audience=https://api.example.test"
    ];
}
