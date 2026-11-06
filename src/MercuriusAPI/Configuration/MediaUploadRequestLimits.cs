using Microsoft.Extensions.Configuration;

namespace Mercurius.LAN.API.Configuration;

public sealed record MediaUploadRequestLimits(long MaxFileSizeInBytes, long MaxRequestBodySize)
{
    public const long MultipartEnvelopeSizeInBytes = 64 * 1024;
    private const long BytesPerMegabyte = 1024 * 1024;

    public long MaxSponsorLogoRequestBodySize =>
        checked((MaxFileSizeInBytes * 2) + MultipartEnvelopeSizeInBytes);

    public static MediaUploadRequestLimits FromConfiguration(IConfiguration configuration)
    {
        var maxFileSizeInMegabytes = configuration.GetValue<int>("FileStorage:MaxFileSizeInMB");
        if (maxFileSizeInMegabytes <= 0)
        {
            throw new InvalidOperationException(
                "FileStorage:MaxFileSizeInMB must be a positive number of mebibytes.");
        }

        var maxFileSizeInBytes = checked(maxFileSizeInMegabytes * BytesPerMegabyte);
        return new MediaUploadRequestLimits(
            maxFileSizeInBytes,
            checked(maxFileSizeInBytes + MultipartEnvelopeSizeInBytes));
    }
}
