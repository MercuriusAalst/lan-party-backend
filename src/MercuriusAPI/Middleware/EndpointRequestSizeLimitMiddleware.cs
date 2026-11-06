using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;

namespace Mercurius.LAN.API.Middleware;

public sealed class EndpointRequestSizeLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var requestSizeLimit = context.GetEndpoint()
            ?.Metadata
            .GetMetadata<IRequestSizeLimitMetadata>();
        if (requestSizeLimit?.MaxRequestBodySize is { } maxRequestBodySize &&
            context.Features.Get<IHttpMaxRequestBodySizeFeature>() is
            { IsReadOnly: false } maxRequestBodySizeFeature)
        {
            maxRequestBodySizeFeature.MaxRequestBodySize = maxRequestBodySize;
        }

        await next(context);
    }
}
