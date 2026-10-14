using Imageflow.Bindings;
using Imageflow.Fluent;
using Mercurius.Modules.Media.Contracts;
using Mercurius.Modules.Shared.Exceptions;
using Microsoft.Extensions.Configuration;

namespace Mercurius.Modules.Media.Infrastructure;

internal sealed class FileSystemMediaModule : IMediaModule
{
    private const string ImagesPathPrefix = "images/";
    private const float WebPQuality = 82;
    private static readonly FrameSizeLimit MaxImageSize = new(8000, 8000, 40);
    private static readonly SecurityOptions SecurityLimits = new SecurityOptions()
        .SetMaxDecodeSize(MaxImageSize)
        .SetMaxFrameSize(MaxImageSize)
        .SetMaxEncodeSize(MaxImageSize);

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/gif"
    };

    private readonly IConfiguration _configuration;
    private readonly int _maxFileSizeInMB;

    public FileSystemMediaModule(IConfiguration configuration)
    {
        _configuration = configuration;
        _maxFileSizeInMB = configuration.GetValue<int>("FileStorage:MaxFileSizeInMB");
    }

    public async Task<StoredMediaAsset> SaveImageAsync(
        MediaUpload upload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateImage(upload);
        if (upload.Content.CanSeek)
            upload.Content.Position = 0;

        var storageRoot = GetRequiredStorageRoot();
        Directory.CreateDirectory(storageRoot);

        var fileName = $"{Guid.NewGuid():N}.webp";
        var filePath = Path.Combine(storageRoot, fileName);

        try
        {
            await using (var outputStream = new FileStream(
                             filePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None))
            {
                await new ImageJob()
                    .Decode(upload.Content, true)
                    .Encode(new StreamDestination(outputStream, true), new WebPLossyEncoder(WebPQuality))
                    .Finish()
                    .SetSecurityOptions(SecurityLimits)
                    .InProcessAsync();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new StoredMediaAsset($"{ImagesPathPrefix}{fileName}");
        }
        catch (ImageflowException)
        {
            DeleteFileQuietly(filePath);
            throw new ValidationException("The image could not be processed or exceeds the maximum dimensions.");
        }
        catch
        {
            DeleteFileQuietly(filePath);
            throw;
        }
    }

    public Task DeleteImageAsync(
        string? mediaUrl,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var filePath = GetSafeFilePath(mediaUrl);
        if (filePath is not null && File.Exists(filePath))
            File.Delete(filePath);

        return Task.CompletedTask;
    }

    private void ValidateImage(MediaUpload upload)
    {
        if (upload is null || upload.Content is null)
            throw new ValidationException("No file provided");
        if (upload.Length == 0)
            throw new ValidationException("Empty file provided");
        if (upload.Length > _maxFileSizeInMB * 1024L * 1024L)
            throw new ValidationException($"File too big, maximum file size is {_maxFileSizeInMB}MB");
        if (string.IsNullOrWhiteSpace(upload.ContentType) || !AllowedContentTypes.Contains(upload.ContentType))
            throw new ValidationException("Unsupported image type.");
    }

    private string GetRequiredStorageRoot()
    {
        var folderPath = _configuration["FileStorage:Location"];
        if (string.IsNullOrWhiteSpace(folderPath))
            throw new InvalidOperationException("File storage location is not configured.");

        return Path.GetFullPath(folderPath);
    }

    private string? GetSafeFilePath(string? mediaUrl)
    {
        if (string.IsNullOrWhiteSpace(mediaUrl))
            return null;

        var normalizedUrl = mediaUrl.Replace("\\", "/", StringComparison.Ordinal);
        if (!normalizedUrl.StartsWith(ImagesPathPrefix, StringComparison.Ordinal) ||
            normalizedUrl.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var fileName = normalizedUrl[ImagesPathPrefix.Length..];
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.Contains('/', StringComparison.Ordinal) ||
            !IsGeneratedStorageFileName(fileName))
        {
            return null;
        }

        var folderPath = _configuration["FileStorage:Location"];
        if (string.IsNullOrWhiteSpace(folderPath))
            return null;

        var storageRoot = Path.GetFullPath(folderPath);
        var filePath = Path.GetFullPath(Path.Combine(storageRoot, fileName));
        var storageRootWithSeparator = storageRoot.EndsWith(Path.DirectorySeparatorChar)
            ? storageRoot
            : storageRoot + Path.DirectorySeparatorChar;

        return filePath.StartsWith(storageRootWithSeparator, StringComparison.OrdinalIgnoreCase)
            ? filePath
            : null;
    }

    private static bool IsGeneratedStorageFileName(string fileName)
    {
        const string webpExtension = ".webp";
        if (!fileName.EndsWith(webpExtension, StringComparison.OrdinalIgnoreCase))
            return false;

        var key = fileName[..^webpExtension.Length];
        if (Guid.TryParseExact(key, "N", out _))
            return true;

        return key.Length == 12 &&
               key[8] == '.' &&
               key.Where((_, index) => index != 8)
                   .All(char.IsAsciiLetterOrDigit);
    }

    private static void DeleteFileQuietly(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
