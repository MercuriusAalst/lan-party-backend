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
            var asset = await mediaModule.SaveImageAsync(
                new MediaUpload(stream, "untrusted.gif", "image/gif", image.Length, MediaPurpose.TournamentBanner));

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
                mediaModule.SaveImageAsync(
                    new MediaUpload(stream, "wide.png", "image/png", image.Length, MediaPurpose.TournamentBanner)));

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
    public async Task SaveImageAsync_RejectsImagesBeyondTheMegapixelLimitWithoutLeavingAFile()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var mediaModule = CreateMediaModule(storagePath);
        var image = await CreatePngAsync(7000, 6000);

        try
        {
            await using var stream = new MemoryStream(image);
            var exception = await Assert.ThrowsAsync<ValidationException>(() =>
                mediaModule.SaveImageAsync(
                    new MediaUpload(stream, "forty-two-megapixels.png", "image/png", image.Length, MediaPurpose.TournamentBanner)));

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

            var asset = await mediaModule.SaveImageAsync(
                new MediaUpload(stream, "logo.gif", "image/gif", image.Length, MediaPurpose.TeamLogo));

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
            5L * 1024 * 1024 + 1,
            MediaPurpose.ProfilePicture);

        var exception = await Assert.ThrowsAsync<ValidationException>(() => mediaModule.SaveImageAsync(oversizedUpload));

        Assert.Equal("File too big, maximum file size is 5MB", exception.Message);
    }

    [Theory]
    [InlineData(MediaPurpose.TournamentBanner, 5000u, 2500u, 2560u, 1280u)]
    [InlineData(MediaPurpose.TournamentBanner, 2000u, 5000u, 576u, 1440u)]
    [InlineData(MediaPurpose.SponsorLogo, 2000u, 4000u, 512u, 1024u)]
    [InlineData(MediaPurpose.TeamLogo, 4000u, 2000u, 1024u, 512u)]
    [InlineData(MediaPurpose.ProfilePicture, 1000u, 2000u, 256u, 512u)]
    [InlineData(MediaPurpose.ProfilePicture, 2000u, 1000u, 512u, 256u)]
    public async Task SaveImageAsync_ConstrainsLandscapeAndPortraitImagesWithinPolicy(
        MediaPurpose purpose,
        uint sourceWidth,
        uint sourceHeight,
        uint expectedWidth,
        uint expectedHeight)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var mediaModule = CreateMediaModule(storagePath);
        var image = await CreatePngAsync(sourceWidth, sourceHeight);

        try
        {
            await using var stream = new MemoryStream(image);
            var asset = await mediaModule.SaveImageAsync(
                new MediaUpload(stream, "source.png", "image/png", image.Length, purpose));
            var stored = await File.ReadAllBytesAsync(Path.Combine(storagePath, Path.GetFileName(asset.Url)));
            var (width, height, _) = await DecodeToPngAsync(stored);

            Assert.Equal(expectedWidth, width);
            Assert.Equal(expectedHeight, height);
        }
        finally
        {
            DeleteDirectoryIfPresent(storagePath);
        }
    }

    [Theory]
    [InlineData(MediaPurpose.TournamentBanner, 320u, 180u)]
    [InlineData(MediaPurpose.SponsorLogo, 320u, 180u)]
    [InlineData(MediaPurpose.TeamLogo, 320u, 180u)]
    [InlineData(MediaPurpose.ProfilePicture, 320u, 180u)]
    public async Task SaveImageAsync_DoesNotUpscaleSmallImages(
        MediaPurpose purpose,
        uint width,
        uint height)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var mediaModule = CreateMediaModule(storagePath);
        var image = await CreatePngAsync(width, height);

        try
        {
            await using var stream = new MemoryStream(image);
            var asset = await mediaModule.SaveImageAsync(
                new MediaUpload(stream, "small.png", "image/png", image.Length, purpose));
            var stored = await File.ReadAllBytesAsync(Path.Combine(storagePath, Path.GetFileName(asset.Url)));
            var (storedWidth, storedHeight, _) = await DecodeToPngAsync(stored);

            Assert.Equal(width, storedWidth);
            Assert.Equal(height, storedHeight);
        }
        finally
        {
            DeleteDirectoryIfPresent(storagePath);
        }
    }

    [Fact]
    public async Task SaveImageAsync_PreservesTransparency()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var mediaModule = CreateMediaModule(storagePath);
        var image = await CreatePngAsync(
            64,
            64,
            AnyColor.Transparent,
            image => image.FillRectangle(16, 16, 48, 48, AnyColor.Black));

        try
        {
            await using var stream = new MemoryStream(image);
            var asset = await mediaModule.SaveImageAsync(
                new MediaUpload(stream, "transparent.png", "image/png", image.Length, MediaPurpose.TeamLogo));
            var stored = await File.ReadAllBytesAsync(Path.Combine(storagePath, Path.GetFileName(asset.Url)));
            var (_, _, png) = await DecodeToPngAsync(stored);

            Assert.Contains("tRNS", System.Text.Encoding.ASCII.GetString(png), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryIfPresent(storagePath);
        }
    }

    [Fact]
    public async Task SaveImageAsync_AppliesExifOrientationBeforeConstraining()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var mediaModule = CreateMediaModule(storagePath);
        var jpeg = await CreateJpegAsync(400, 200);
        var orientedJpeg = AddExifOrientation6(jpeg);

        try
        {
            await using var stream = new MemoryStream(orientedJpeg);
            var asset = await mediaModule.SaveImageAsync(
                new MediaUpload(stream, "oriented.jpg", "image/jpeg", orientedJpeg.Length, MediaPurpose.ProfilePicture));
            var stored = await File.ReadAllBytesAsync(Path.Combine(storagePath, Path.GetFileName(asset.Url)));
            var (width, height, _) = await DecodeToPngAsync(stored);

            Assert.Equal(200u, width);
            Assert.Equal(400u, height);
        }
        finally
        {
            DeleteDirectoryIfPresent(storagePath);
        }
    }

    [Fact]
    public async Task SaveImageAsync_RejectsUndecodableContentWithoutLeavingAFile()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var mediaModule = CreateMediaModule(storagePath);

        try
        {
            await using var stream = new MemoryStream([1, 2, 3, 4]);

            await Assert.ThrowsAsync<ValidationException>(() =>
                mediaModule.SaveImageAsync(
                    new MediaUpload(stream, "broken.png", "image/png", stream.Length, MediaPurpose.SponsorLogo)));

            Assert.Empty(Directory.EnumerateFiles(storagePath));
        }
        finally
        {
            DeleteDirectoryIfPresent(storagePath);
        }
    }

    [Fact]
    public async Task SaveImageAsync_RejectsAnUndeclaredPurpose()
    {
        var mediaModule = CreateMediaModule(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        await using var stream = new MemoryStream([1]);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            mediaModule.SaveImageAsync(
                new MediaUpload(stream, "source.png", "image/png", 1, (MediaPurpose)int.MaxValue)));

        Assert.Equal("Unsupported image purpose.", exception.Message);
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
        [new MediaUpload(null!, "missing.png", "image/png", 1, MediaPurpose.TournamentBanner), "No file provided"],
        [new MediaUpload(new MemoryStream(), "empty.png", "image/png", 0, MediaPurpose.TournamentBanner), "Empty file provided"],
        [new MediaUpload(new MemoryStream([1]), "large.png", "image/png", 1_048_577, MediaPurpose.TournamentBanner), "File too big, maximum file size is 1MB"],
        [new MediaUpload(new MemoryStream([1]), "not-image.txt", "text/plain", 1, MediaPurpose.TournamentBanner), "Unsupported image type."]
    ];

    private static async Task<byte[]> CreatePngAsync(
        uint width,
        uint height,
        AnyColor? color = null,
        Func<BuildNode, BuildNode>? draw = null)
    {
        var job = new ImageJob();
        var node = job.CreateCanvasBgra32(width, height, color ?? AnyColor.Black);
        if (draw is not null)
            node = draw(node);
        var result = await node.EncodeToBytes(new LodePngEncoder()).Finish().InProcessAsync();
        return result.First!.TryGetBytes()!.Value.ToArray();
    }

    private static async Task<byte[]> CreateJpegAsync(uint width, uint height)
    {
        var result = await new ImageJob()
            .CreateCanvasBgr32(width, height, AnyColor.Black)
            .EncodeToBytes(new MozJpegEncoder(90))
            .Finish()
            .InProcessAsync();
        return result.First!.TryGetBytes()!.Value.ToArray();
    }

    private static async Task<(uint Width, uint Height, byte[] Png)> DecodeToPngAsync(byte[] image)
    {
        var result = await new ImageJob()
            .Decode(image)
            .EncodeToBytes(new LodePngEncoder())
            .Finish()
            .InProcessAsync();
        return ((uint)result.First!.Width, (uint)result.First.Height, result.First.TryGetBytes()!.Value.ToArray());
    }

    private static byte[] AddExifOrientation6(byte[] jpeg)
    {
        byte[] exifSegment =
        [
            0xFF, 0xE1, 0x00, 0x22,
            0x45, 0x78, 0x69, 0x66, 0x00, 0x00,
            0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,
            0x01, 0x00,
            0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x06, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00
        ];
        var oriented = new byte[jpeg.Length + exifSegment.Length];
        Buffer.BlockCopy(jpeg, 0, oriented, 0, 2);
        Buffer.BlockCopy(exifSegment, 0, oriented, 2, exifSegment.Length);
        Buffer.BlockCopy(jpeg, 2, oriented, 2 + exifSegment.Length, jpeg.Length - 2);
        return oriented;
    }

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
