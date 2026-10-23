namespace Mercurius.Modules.Media.Tests;

public sealed class ImagePolicyBenchmarkTests
{
    [Fact]
    public async Task GeneratesReproduciblePolicyEvidenceWhenOutputPathIsConfigured()
    {
        var outputDirectory = Environment.GetEnvironmentVariable("MEDIA_BENCHMARK_OUTPUT");
        if (string.IsNullOrWhiteSpace(outputDirectory))
            return;

        await new ImagePolicyBenchmarkHelper(outputDirectory).RunAsync();
    }
}
