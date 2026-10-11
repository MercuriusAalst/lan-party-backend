using Mercurius.Modules.Shared.Exceptions;
using Microsoft.AspNetCore.Diagnostics;

namespace Mercurius.LAN.API.Middleware;

public sealed class ApiExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var statusCode = exception switch
        {
            ForbiddenException => StatusCodes.Status403Forbidden,
            ConflictException => StatusCodes.Status409Conflict,
            NotFoundException => StatusCodes.Status404NotFound,
            ValidationException => StatusCodes.Status400BadRequest,
            ServiceUnavailableException => StatusCodes.Status503ServiceUnavailable,
            DeletedAccountException => StatusCodes.Status410Gone,
            InvalidCredentialsException => StatusCodes.Status401Unauthorized,
            LockoutException => StatusCodes.Status423Locked,
            UnauthorizedAccessException => StatusCodes.Status401Unauthorized,
            _ => (int?)null
        };

        if (!statusCode.HasValue)
            return false;

        httpContext.Response.StatusCode = statusCode.Value;
        if (exception is ConflictException conflict)
            await httpContext.Response.WriteAsJsonAsync(new { code = conflict.Code, message = conflict.Message }, cancellationToken);
        else if (exception is ForbiddenException forbidden)
            await httpContext.Response.WriteAsJsonAsync(new { code = forbidden.Code, message = forbidden.Message }, cancellationToken);
        else
            await httpContext.Response.WriteAsJsonAsync(exception.Message, cancellationToken);
        return true;
    }
}
