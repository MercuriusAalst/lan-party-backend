using Mercurius.Modules.Shared.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

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

        // The frontend reads "code" and "message" (falling back to "detail"), so both stay as extensions.
        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = ReasonPhrases.GetReasonPhrase(statusCode.Value),
            Detail = exception.Message,
            Extensions = { ["message"] = exception.Message }
        };
        var code = exception switch
        {
            ConflictException conflict => conflict.Code,
            ForbiddenException forbidden => forbidden.Code,
            _ => null
        };
        if (code is not null)
            problem.Extensions["code"] = code;

        httpContext.Response.StatusCode = statusCode.Value;
        await httpContext.Response.WriteAsJsonAsync(problem, options: null, "application/problem+json", cancellationToken);
        return true;
    }
}
