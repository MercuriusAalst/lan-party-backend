using System.Globalization;
using System.Text;
using Imageflow.Fluent;
using Mercurius.Modules.Media.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mercurius.Modules.Media.Tests;

public sealed class ImagePolicyBenchmarkHelper
{
    private static readonly IReadOnlyDictionary<char, string[]> Glyphs =
        new Dictionary<char, string[]>
        {
            [' '] = ["00000", "00000", "00000", "00000", "00000", "00000", "00000"],
            ['0'] = ["01110", "10001", "10011", "10101", "11001", "10001", "01110"],
            ['2'] = ["01110", "10001", "00001", "00010", "00100", "01000", "11111"],
            ['6'] = ["00110", "01000", "10000", "11110", "10001", "10001", "01110"],
            ['A'] = ["01110", "10001", "10001", "11111", "10001", "10001", "10001"],
            ['L'] = ["10000", "10000", "10000", "10000", "10000", "10000", "11111"],
            ['N'] = ["10001", "11001", "10101", "10011", "10001", "10001", "10001"],
            ['P'] = ["11110", "10001", "10001", "11110", "10000", "10000", "10000"],
            ['R'] = ["11110", "10001", "10001", "11110", "10100", "10010", "10001"],
            ['T'] = ["11111", "00100", "00100", "00100", "00100", "00100", "00100"],
            ['Y'] = ["10001", "10001", "01010", "00100", "00100", "00100", "00100"]
        };

    private readonly IMediaModule _mediaModule;
    private readonly string _storagePath;

    public ImagePolicyBenchmarkHelper(string outputDirectory)
    {
        OutputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(Path.Combine(OutputDirectory, "inputs"));
        Directory.CreateDirectory(Path.Combine(OutputDirectory, "baseline"));
        Directory.CreateDirectory(Path.Combine(OutputDirectory, "policy"));
        Directory.CreateDirectory(Path.Combine(OutputDirectory, "logos"));
        Directory.CreateDirectory(Path.Combine(OutputDirectory, "previews"));

        _storagePath = Path.Combine(Path.GetTempPath(), $"media-benchmark-{Guid.NewGuid():N}");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:Location"] = _storagePath,
                ["FileStorage:MaxFileSizeInMB"] = "5"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddMediaModule(configuration);
        _mediaModule = services.BuildServiceProvider().GetRequiredService<IMediaModule>();
    }

    public string OutputDirectory { get; }

    public async Task RunAsync()
    {
        var fixtures = new[]
        {
            await CreateBannerArtworkAsync(),
            await CreatePhotoBannerAsync(),
            await CreateTextLogoAsync(),
            await CreateAlphaLogoAsync(),
            await CreateSmallBannerAsync()
        };
        var rows = new List<BenchmarkRow>();

        try
        {
            foreach (var fixture in fixtures.Where(candidate => !candidate.IsLogo))
            {
                await RecordPurposePolicyAsync(fixture, rows);
            }

            foreach (var fixture in fixtures.Where(candidate => candidate.IsLogo))
            {
                await RecordLogoComparisonAsync(fixture, rows);
            }

            await WriteMeasurementsAsync(rows);
        }
        finally
        {
            if (Directory.Exists(_storagePath))
                Directory.Delete(_storagePath, recursive: true);
        }
    }

    private async Task RecordPurposePolicyAsync(Fixture fixture, List<BenchmarkRow> rows)
    {
        if (fixture.WriteInput)
        {
            var inputPath = Path.Combine(OutputDirectory, "inputs", fixture.FileName);
            await File.WriteAllBytesAsync(inputPath, fixture.Bytes);
        }

        var baseline = await EncodeAsync(fixture.Bytes, null, null, 82, false);
        var baselinePath = Path.Combine(OutputDirectory, "baseline", $"{fixture.Name}.old-q82.webp");
        await File.WriteAllBytesAsync(baselinePath, baseline);

        var optimized = await SaveThroughProductionAsync(fixture);
        var optimizedPath = Path.Combine(OutputDirectory, "policy", $"{fixture.Name}.new.webp");
        await File.WriteAllBytesAsync(optimizedPath, optimized);
        if (fixture.Name == "banner-photo-detail")
        {
            await WritePreviewAsync("banner-photo-detail-baseline.png", baseline);
            await WritePreviewAsync("banner-photo-detail-policy.png", optimized);
        }

        var (baselineWidth, baselineHeight) = await GetDimensionsAsync(baseline);
        var (optimizedWidth, optimizedHeight) = await GetDimensionsAsync(optimized);
        Assert.True(optimizedWidth <= fixture.MaxWidth);
        Assert.True(optimizedHeight <= fixture.MaxHeight);
        Assert.True(optimized.Length < baseline.Length);

        rows.Add(new BenchmarkRow(
            fixture.Name,
            fixture.Size,
            baseline.Length,
            $"{baselineWidth}x{baselineHeight}",
            optimized.Length,
            $"{optimizedWidth}x{optimizedHeight}",
            ReductionPercent(baseline.Length, optimized.Length)));
    }

    private async Task RecordLogoComparisonAsync(Fixture fixture, List<BenchmarkRow> rows)
    {
        var inputPath = Path.Combine(OutputDirectory, "inputs", $"{fixture.Name}.png");
        await File.WriteAllBytesAsync(inputPath, fixture.Bytes);

        var lossy = await EncodeAsync(fixture.Bytes, fixture.MaxWidth, fixture.MaxHeight, 86, false);
        var lossyPath = Path.Combine(OutputDirectory, "logos", $"{fixture.Name}.lossy-q86.webp");
        await File.WriteAllBytesAsync(lossyPath, lossy);

        var lossless = await EncodeAsync(fixture.Bytes, fixture.MaxWidth, fixture.MaxHeight, 86, true);
        var losslessPath = Path.Combine(OutputDirectory, "logos", $"{fixture.Name}.lossless.webp");
        await File.WriteAllBytesAsync(losslessPath, lossless);

        var (lossyWidth, lossyHeight) = await GetDimensionsAsync(lossy);
        var (losslessWidth, losslessHeight) = await GetDimensionsAsync(lossless);
        Assert.True(lossyWidth <= fixture.MaxWidth && lossyHeight <= fixture.MaxHeight);
        Assert.True(losslessWidth <= fixture.MaxWidth && losslessHeight <= fixture.MaxHeight);

        rows.Add(new BenchmarkRow(
            $"{fixture.Name}-lossy-q86",
            fixture.Size,
            lossy.Length,
            $"{lossyWidth}x{lossyHeight}",
            lossy.Length,
            $"{lossyWidth}x{lossyHeight}",
            "0.0"));
        rows.Add(new BenchmarkRow(
            $"{fixture.Name}-lossless",
            fixture.Size,
            lossless.Length,
            $"{losslessWidth}x{losslessHeight}",
            lossless.Length,
            $"{losslessWidth}x{losslessHeight}",
            DifferencePercent(lossy.Length, lossless.Length)));
    }

    private async Task<byte[]> SaveThroughProductionAsync(Fixture fixture)
    {
        await using var source = new MemoryStream(fixture.Bytes);
        var asset = await _mediaModule.SaveImageAsync(
            new MediaUpload(source, fixture.FileName, fixture.ContentType, fixture.Bytes.Length, fixture.Purpose));
        return await File.ReadAllBytesAsync(Path.Combine(_storagePath, Path.GetFileName(asset.Url)));
    }

    private static async Task<byte[]> EncodeAsync(
        byte[] image,
        uint? maxWidth,
        uint? maxHeight,
        float quality,
        bool lossless)
    {
        var node = new ImageJob().Decode(image);
        if (maxWidth.HasValue && maxHeight.HasValue)
            node = node.ConstrainWithin(maxWidth.Value, maxHeight.Value);

        IEncoderPreset encoder = lossless
            ? new WebPLosslessEncoder()
            : new WebPLossyEncoder(quality);
        var result = await node.EncodeToBytes(encoder).Finish().InProcessAsync();
        return result.First!.TryGetBytes()!.Value.ToArray();
    }

    private static async Task<(uint Width, uint Height)> GetDimensionsAsync(byte[] image)
    {
        var result = await new ImageJob()
            .Decode(image)
            .EncodeToBytes(new LodePngEncoder())
            .Finish()
            .InProcessAsync();
        return ((uint)result.First!.Width, (uint)result.First.Height);
    }

    private async Task WritePreviewAsync(string fileName, byte[] image)
    {
        var result = await new ImageJob()
            .Decode(image)
            .ConstrainWithin(960, 960)
            .EncodeToBytes(new LodePngEncoder())
            .Finish()
            .InProcessAsync();
        await File.WriteAllBytesAsync(
            Path.Combine(OutputDirectory, "previews", fileName),
            result.First!.TryGetBytes()!.Value.ToArray());
    }

    private async Task WriteMeasurementsAsync(List<BenchmarkRow> rows)
    {
        var csv = new StringBuilder(
            "fixture,input_dimensions,baseline_bytes,baseline_dimensions,output_bytes,output_dimensions,reduction_percent\n");
        foreach (var row in rows)
        {
            csv.Append(CultureInfo.InvariantCulture,
                $"{row.Name},{row.InputDimensions},{row.BaselineBytes},{row.BaselineDimensions},{row.OutputBytes},{row.OutputDimensions},{row.ReductionPercent}\n");
        }

        await File.WriteAllTextAsync(Path.Combine(OutputDirectory, "measurements.csv"), csv.ToString());
    }

    private static string ReductionPercent(long baselineBytes, long outputBytes) =>
        ((baselineBytes - outputBytes) * 100d / baselineBytes).ToString("0.0", CultureInfo.InvariantCulture);

    private static string DifferencePercent(long lossyBytes, long losslessBytes) =>
        ((losslessBytes - lossyBytes) * 100d / lossyBytes).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);

    private static async Task<Fixture> CreateBannerArtworkAsync()
    {
        var node = new ImageJob()
            .CreateCanvasBgra32(5000, 2813, AnyColor.FromHexSrgb("#111B2E"))
            .FillRectangle(0, 0, 5000, 520, AnyColor.FromHexSrgb("#172A48"))
            .FillRectangle(0, 2500, 5000, 2813, AnyColor.FromHexSrgb("#0B1220"))
            .FillRectangle(480, 850, 4520, 2120, AnyColor.FromHexSrgb("#E84A36"))
            .FillRectangle(560, 930, 4440, 2040, AnyColor.FromHexSrgb("#F3B51B"))
            .FillRectangle(640, 1010, 4360, 1960, AnyColor.FromHexSrgb("#14233B"));
        node = DrawText(node, "LAN", 1000, 1150, 90, AnyColor.FromHexSrgb("#F7F2E7"));
        node = DrawText(node, "2026", 1000, 1900, 12, AnyColor.FromHexSrgb("#F7F2E7"))
            .FillRectangle(820, 1700, 4180, 1730, AnyColor.FromHexSrgb("#55D6BE"))
            .FillRectangle(1050, 1850, 3950, 1880, AnyColor.FromHexSrgb("#4D7CFE"));
        var bytes = await ExportPngAsync(node);
        return new Fixture(
            "banner-artwork-text",
            bytes,
            "5000x2813",
            MediaPurpose.TournamentBanner,
            2560,
            1440,
            false,
            "banner-artwork-text.png",
            "image/png",
            true);
    }

    private async Task<Fixture> CreatePhotoBannerAsync()
    {
        var fileName = "lan-party-lyon.jpg";
        var bytes = await File.ReadAllBytesAsync(Path.Combine(OutputDirectory, "source", fileName));
        Assert.True(bytes.Length <= 5L * 1024 * 1024);
        var (width, height) = await GetDimensionsAsync(bytes);
        Assert.True(width <= 8000 && height <= 8000);
        Assert.True((long)width * height <= 40_000_000);
        return new Fixture(
            "banner-photo-detail",
            bytes,
            $"{width}x{height}",
            MediaPurpose.TournamentBanner,
            2560,
            1440,
            false,
            fileName,
            "image/jpeg",
            false);
    }

    private static async Task<Fixture> CreateTextLogoAsync()
    {
        var node = new ImageJob()
            .CreateCanvasBgra32(1600, 800, AnyColor.Transparent)
            .FillRectangle(64, 64, 1536, 736, AnyColor.FromHexSrgb("#13233B"))
            .FillRectangle(112, 112, 1488, 688, AnyColor.FromHexSrgb("#55D6BE"));
        node = DrawText(node, "LAN", 270, 245, 50, AnyColor.FromHexSrgb("#13233B"));
        var bytes = await ExportPngAsync(node);
        return new Fixture(
            "logo-text-alpha",
            bytes,
            "1600x800",
            MediaPurpose.TeamLogo,
            1024,
            1024,
            true,
            "logo-text-alpha.png",
            "image/png",
            true);
    }

    private static async Task<Fixture> CreateAlphaLogoAsync()
    {
        var node = new ImageJob()
            .CreateCanvasBgra32(1200, 1200, AnyColor.Transparent)
            .FillRectangle(80, 80, 1120, 1120, AnyColor.FromHexSrgb("#E84A36"))
            .FillRectangle(160, 160, 1040, 1040, AnyColor.FromHexSrgb("#F3B51B"))
            .FillRectangle(240, 240, 540, 540, AnyColor.FromHexSrgb("#14233B"))
            .FillRectangle(660, 240, 960, 540, AnyColor.FromHexSrgb("#55D6BE"))
            .FillRectangle(240, 660, 960, 960, AnyColor.FromHexSrgb("#4D7CFE"));
        var bytes = await ExportPngAsync(node);
        return new Fixture(
            "logo-flat-alpha",
            bytes,
            "1200x1200",
            MediaPurpose.SponsorLogo,
            1024,
            1024,
            true,
            "logo-flat-alpha.png",
            "image/png",
            true);
    }

    private static async Task<Fixture> CreateSmallBannerAsync()
    {
        var node = new ImageJob()
            .CreateCanvasBgra32(320, 180, AnyColor.FromHexSrgb("#14233B"))
            .FillRectangle(12, 12, 308, 168, AnyColor.FromHexSrgb("#E84A36"));
        node = DrawText(node, "LAN", 91, 55, 7, AnyColor.FromHexSrgb("#F7F2E7"));
        var bytes = await ExportPngAsync(node);
        return new Fixture(
            "banner-small",
            bytes,
            "320x180",
            MediaPurpose.TournamentBanner,
            2560,
            1440,
            false,
            "banner-small.png",
            "image/png",
            true);
    }

    private static async Task<byte[]> ExportPngAsync(BuildNode node)
    {
        var result = await node.EncodeToBytes(new LodePngEncoder()).Finish().InProcessAsync();
        return result.First!.TryGetBytes()!.Value.ToArray();
    }

    private static BuildNode DrawText(BuildNode image, string text, int left, int top, int scale, AnyColor color)
    {
        var cursor = left;
        foreach (var character in text)
        {
            var glyph = Glyphs[character];
            for (var row = 0; row < glyph.Length; row++)
            {
                for (var column = 0; column < glyph[row].Length; column++)
                {
                    if (glyph[row][column] == '1')
                    {
                        var x = cursor + column * scale;
                        var y = top + row * scale;
                        image = image.FillRectangle(x, y, x + scale, y + scale, color);
                    }
                }
            }

            cursor += 6 * scale;
        }

        return image;
    }

    private sealed record Fixture(
        string Name,
        byte[] Bytes,
        string Size,
        MediaPurpose Purpose,
        uint MaxWidth,
        uint MaxHeight,
        bool IsLogo,
        string FileName,
        string ContentType,
        bool WriteInput);

    private sealed record BenchmarkRow(
        string Name,
        string InputDimensions,
        long BaselineBytes,
        string BaselineDimensions,
        long OutputBytes,
        string OutputDimensions,
        string ReductionPercent);
}
