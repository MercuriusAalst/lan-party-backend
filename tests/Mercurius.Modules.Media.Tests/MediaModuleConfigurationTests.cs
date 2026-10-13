using Imageflow.Fluent;
using Mercurius.Modules.Media;
using Mercurius.Modules.Media.Contracts;
using Mercurius.Modules.Shared.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mercurius.Modules.Media.Tests;

public class MediaModuleConfigurationTests
{
    [Fact]
    public void AddMediaModule_RegistersItsOwnImplementation()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        services.AddSingleton<IConfiguration>(configuration);
        services.AddMediaModule(configuration);

        using var provider = services.BuildServiceProvider();
        var mediaModule = provider.GetRequiredService<IMediaModule>();

        Assert.Equal("Mercurius.Modules.Media", mediaModule.GetType().Assembly.GetName().Name);
        Assert.Equal("FileSystemMediaModule", mediaModule.GetType().Name);
    }

    [Theory]
    [MemberData(nameof(InvalidUploads))]
    public async Task SaveImageAsync_RejectsInvalidUploadMetadata(MediaUpload upload, string message)
    {
        var mediaModule = CreateMediaModule(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        var exception = await Assert.ThrowsAsync<ValidationException>(() => mediaModule.SaveImageAsync(upload));

        Assert.Equal(message, exception.Message);
    }

    [Fact]
    public async Task SaveImageAsync_StoresWebPWithSafeRelativeReference()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var mediaModule = CreateMediaModule(storagePath);
        var image = Convert.FromBase64String(
            "R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==");

        try
        {
            await using var stream = new MemoryStream(image);
            var asset = await mediaModule.SaveImageAsync(new MediaUpload(stream, "untrusted.gif", "image/gif", image.Length));

            Assert.Matches("^images/[0-9a-f]{32}\\.webp$", asset.Url);
            var stored = await File.ReadAllBytesAsync(Path.Combine(storagePath, Path.GetFileName(asset.Url)));
            Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(stored, 8, 4));
            Assert.DoesNotContain("VP8L", System.Text.Encoding.ASCII.GetString(stored), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryIfPresent(storagePath);
        }
    }

    [Fact]
    public async Task SaveImageAsync_RejectsImagesBeyondTheDimensionLimitWithoutLeavingAFile()
    {
        // Arrange
        var storagePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var mediaModule = CreateMediaModule(storagePath);
        var canvas = await new ImageJob()
            .CreateCanvasBgra32(9000, 10, AnyColor.Black)
            .EncodeToBytes(new LodePngEncoder())
            .Finish()
            .InProcessAsync();
        var image = canvas.First!.TryGetBytes()!.Value.ToArray();

        try
        {
            // Act
            await using var stream = new MemoryStream(image);
            var exception = await Assert.ThrowsAsync<ValidationException>(() =>
                mediaModule.SaveImageAsync(new MediaUpload(stream, "wide.png", "image/png", image.Length)));

            // Assert
            Assert.Equal("The image could not be processed or exceeds the maximum dimensions.", exception.Message);
            Assert.Empty(Directory.EnumerateFiles(storagePath));
        }
        finally
        {
            DeleteDirectoryIfPresent(storagePath);
        }
    }

    [Fact]
    public async Task SaveImageAsync_RewindsSeekableContentBeforeEncoding()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var mediaModule = CreateMediaModule(storagePath);
        var image = Convert.FromBase64String(
            "R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==");

        try
        {
            await using var stream = new MemoryStream(image);
            stream.Position = image.Length / 2;

            var asset = await mediaModule.SaveImageAsync(new MediaUpload(stream, "logo.gif", "image/gif", image.Length));

            Assert.True(File.Exists(Path.Combine(storagePath, Path.GetFileName(asset.Url))));
        }
        finally
        {
            DeleteDirectoryIfPresent(storagePath);
        }
    }

    [Fact]
    public async Task SaveImageAsync_RetainsConfiguredFiveMegabyteValidationAfterBinding()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var mediaModule = CreateMediaModule(storagePath, maxFileSizeInMegabytes: 5);
        var oversizedUpload = new MediaUpload(
            new MemoryStream([1]),
            "large.png",
            "image/png",
            5L * 1024 * 1024 + 1);

        var exception = await Assert.ThrowsAsync<ValidationException>(() => mediaModule.SaveImageAsync(oversizedUpload));

        Assert.Equal("File too big, maximum file size is 5MB", exception.Message);
    }

    [Fact]
    public async Task DeleteImageAsync_IsIdempotentAndCannotEscapeStorage()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var outsidePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(storagePath);
        var storedFile = Path.Combine(storagePath, "0123456789abcdef0123456789abcdef.webp");
        var legacyStoredFile = Path.Combine(storagePath, "legacy01.abc.webp");
        var externalUrlTarget = Path.Combine(storagePath, "fedcba9876543210fedcba9876543210.webp");
        var defaultFile = Path.Combine(storagePath, "default-team-logo.webp");
        var nonMediaFile = Path.Combine(storagePath, "readme.txt");
        await File.WriteAllTextAsync(storedFile, "stored");
        await File.WriteAllTextAsync(legacyStoredFile, "legacy stored");
        await File.WriteAllTextAsync(externalUrlTarget, "external target");
        await File.WriteAllTextAsync(defaultFile, "default asset");
        await File.WriteAllTextAsync(nonMediaFile, "not media");
        await File.WriteAllTextAsync(outsidePath, "outside");
        var mediaModule = CreateMediaModule(storagePath);

        try
        {
            await mediaModule.DeleteImageAsync("images/0123456789abcdef0123456789abcdef.webp");
            await mediaModule.DeleteImageAsync("images/0123456789abcdef0123456789abcdef.webp");
            await mediaModule.DeleteImageAsync("images/legacy01.abc.webp");
            await mediaModule.DeleteImageAsync("images/readme.txt");
            await mediaModule.DeleteImageAsync($"images/../{Path.GetFileName(outsidePath)}");
            await mediaModule.DeleteImageAsync(null);
            await mediaModule.DeleteImageAsync(" ");
            await mediaModule.DeleteImageAsync("https://cdn.example.test/images/fedcba9876543210fedcba9876543210.webp");
            await mediaModule.DeleteImageAsync("/images/fedcba9876543210fedcba9876543210.webp");
            await mediaModule.DeleteImageAsync("images/default-team-logo.webp");

            Assert.False(File.Exists(storedFile));
            Assert.False(File.Exists(legacyStoredFile));
            Assert.True(File.Exists(externalUrlTarget));
            Assert.True(File.Exists(defaultFile));
            Assert.True(File.Exists(nonMediaFile));
            Assert.True(File.Exists(outsidePath));
        }
        finally
        {
            DeleteDirectoryIfPresent(storagePath);
            if (File.Exists(outsidePath))
                File.Delete(outsidePath);
        }
    }

    public static IEnumerable<object[]> InvalidUploads =>
    [
        [new MediaUpload(null!, "missing.png", "image/png", 1), "No file provided"],
        [new MediaUpload(new MemoryStream(), "empty.png", "image/png", 0), "Empty file provided"],
        [new MediaUpload(new MemoryStream([1]), "large.png", "image/png", 1_048_577), "File too big, maximum file size is 1MB"],
        [new MediaUpload(new MemoryStream([1]), "not-image.txt", "text/plain", 1), "Unsupported image type."]
    ];

    private static IMediaModule CreateMediaModule(string storagePath, int maxFileSizeInMegabytes = 1)
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(storagePath, maxFileSizeInMegabytes);
        services.AddSingleton<IConfiguration>(configuration);
        services.AddMediaModule(configuration);

        return services.BuildServiceProvider().GetRequiredService<IMediaModule>();
    }

    private static IConfiguration CreateConfiguration(string storagePath, int maxFileSizeInMegabytes = 1)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:Location"] = storagePath,
                ["FileStorage:MaxFileSizeInMB"] = maxFileSizeInMegabytes.ToString()
            })
            .Build();
    }

    private static void DeleteDirectoryIfPresent(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
