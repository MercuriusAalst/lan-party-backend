using Mercurius.Modules.Identity.Contracts;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace Mercurius.LAN.API.Middleware;

public sealed class IdentityTokenValidationMiddleware(RequestDelegate next)
{
    private const string CurrentUserRoute = "/v1/lan/users/me";

    public async Task InvokeAsync(HttpContext context, IIdentityTokenValidator validator)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        var subjects = context.User.FindAll("sub").Select(claim => claim.Value).ToArray();
        var issuedAtClaims = context.User.FindAll("iat").Select(claim => claim.Value).ToArray();
        var allowDeletedSelfRead = string.Equals(context.Request.Path.Value, CurrentUserRoute, StringComparison.Ordinal) &&
                                   (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsPost(context.Request.Method));
        var valid = subjects.Length == 1 &&
                    issuedAtClaims.Length <= 1 &&
                    await validator.ValidateAndPinAsync(
                        subjects.FirstOrDefault() ?? string.Empty,
                        issuedAtClaims.SingleOrDefault(),
                        allowDeletedSelfRead,
                        context.RequestAborted);
        if (!valid)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(context);
    }
}
