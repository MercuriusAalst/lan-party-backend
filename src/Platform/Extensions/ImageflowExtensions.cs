using Imageflow.Fluent;
using Imageflow.Server;

namespace Platform.Extensions;

public static class ImageflowExtensions
{
    public static IApplicationBuilder UseImageflowWithCaching(
        this IApplicationBuilder app,
        string requestPath,
        string? storagePath,
        string cacheControl)
    {
        // No IStreamCache/IClassicDiskCache is registered, so server-side caching flags would be a no-op;
        // processed responses are cached by clients/proxies through the Cache-Control header instead.
        var maxImageSize = new FrameSizeLimit(8000, 8000, 40);
        var imageflowOptions = new ImageflowMiddlewareOptions
        {
            DefaultCacheControlString = cacheControl
        }
            .SetJobSecurityOptions(new SecurityOptions()
                .SetMaxDecodeSize(maxImageSize)
                .SetMaxFrameSize(maxImageSize)
                .SetMaxEncodeSize(maxImageSize))
            .MapPath(requestPath, storagePath);

        app.UseImageflow(imageflowOptions);
        app.UseStaticFiles();

        return app;
    }
}
