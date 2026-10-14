using System.Text;
using System.Text.Json;
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
        yield return [new UnauthorizedAccessException("Denied."), StatusCodes.Status401Unauthorized];
        yield return [new ForbiddenException("admin_not_assigned", "Assigned administrator required."), StatusCodes.Status403Forbidden];
    }

    [Fact]
    public async Task TryHandleAsync_ForbiddenException_WritesProblemDetailsWithCodeAndMessage()
    {
        // Arrange
        var handler = new ApiExceptionHandler();
        var httpContext = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() }
        };

        // Act
        var handled = await handler.TryHandleAsync(
            httpContext,
            new ForbiddenException("team_captain_required", "Only the team captain can perform this action."),
            CancellationToken.None);

        // Assert
        Assert.True(handled);
        Assert.Equal(StatusCodes.Status403Forbidden, httpContext.Response.StatusCode);
        Assert.StartsWith("application/problem+json", httpContext.Response.ContentType, StringComparison.Ordinal);
        var body = await ReadJsonAsync(httpContext);
        Assert.Equal(403, body.GetProperty("status").GetInt32());
        Assert.Equal("Forbidden", body.GetProperty("title").GetString());
        Assert.Equal("Only the team captain can perform this action.", body.GetProperty("detail").GetString());
        Assert.Equal("Only the team captain can perform this action.", body.GetProperty("message").GetString());
        Assert.Equal("team_captain_required", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task TryHandleAsync_UncodedException_WritesProblemDetailsWithoutCode()
    {
        // Arrange
        var handler = new ApiExceptionHandler();
        var httpContext = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() }
        };

        // Act
        await handler.TryHandleAsync(httpContext, new ValidationException("Validation failed."), CancellationToken.None);

        // Assert
        var body = await ReadJsonAsync(httpContext);
        Assert.Equal("Validation failed.", body.GetProperty("detail").GetString());
        Assert.Equal("Validation failed.", body.GetProperty("message").GetString());
        Assert.False(body.TryGetProperty("code", out _));
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpContext httpContext)
    {
        httpContext.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(httpContext.Response.Body);
        return document.RootElement.Clone();
    }
}
