using System.Text;
using Mercurius.Modules.Shared.Exceptions;
using Mercurius.LAN.API.Middleware;
using Microsoft.AspNetCore.Http;

namespace Platform.Tests;

public class ApiExceptionHandlerTests
{
    [Theory]
    [MemberData(nameof(KnownExceptions))]
    public async Task TryHandleAsync_MapsKnownExceptions(Exception exception, int expectedStatusCode)
    {
        var handler = new ApiExceptionHandler();
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();

        var handled = await handler.TryHandleAsync(httpContext, exception, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(expectedStatusCode, httpContext.Response.StatusCode);
        httpContext.Response.Body.Position = 0;
        var responseBody = await new StreamReader(httpContext.Response.Body, Encoding.UTF8).ReadToEndAsync();
        Assert.Contains(exception.Message, responseBody);
    }

    public static IEnumerable<object[]> KnownExceptions()
    {
        yield return [new ValidationException("Validation failed."), StatusCodes.Status400BadRequest];
        yield return [new ConflictException("conflict", "Conflict."), StatusCodes.Status409Conflict];
        yield return [new NotFoundException("Missing."), StatusCodes.Status404NotFound];
        yield return [new ServiceUnavailableException("Upstream unavailable."), StatusCodes.Status503ServiceUnavailable];
        yield return [new InvalidCredentialsException("Nope."), StatusCodes.Status401Unauthorized];
        yield return [new LockoutException(), StatusCodes.Status423Locked];
        yield return [new UnauthorizedAccessException("Denied."), StatusCodes.Status401Unauthorized];
        yield return [new ForbiddenException("admin_not_assigned", "Assigned administrator required."), StatusCodes.Status403Forbidden];
    }

    [Fact]
    public async Task TryHandleAsync_ForbiddenException_WritesStableCodeAndMessage()
    {
        var handler = new ApiExceptionHandler();
        var httpContext = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() }
        };

        var handled = await handler.TryHandleAsync(
            httpContext,
            new ForbiddenException("admin_not_assigned", "Assigned administrator required."),
            CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status403Forbidden, httpContext.Response.StatusCode);
        httpContext.Response.Body.Position = 0;
        var responseBody = await new StreamReader(httpContext.Response.Body, Encoding.UTF8).ReadToEndAsync();
        Assert.Contains("\"code\":\"admin_not_assigned\"", responseBody, StringComparison.Ordinal);
        Assert.Contains("Assigned administrator required.", responseBody, StringComparison.Ordinal);
    }
}
