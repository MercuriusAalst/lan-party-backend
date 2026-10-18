using System.Text;
using System.Text.Json;
using System.Security.Claims;
using Mercurius.Modules.Identity;
using Mercurius.Modules.Identity.Application.DTOs;
using Mercurius.Modules.Identity.Application.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Platform.Extensions;

namespace Mercurius.Api.Tests;

public class AdminUserPickerPagingEndpointTests
{
    private const string AdminPickerRoute = "v{version:apiVersion}/lan/users/admins";
    private const string CurrentUserPictureRoute = "v{version:apiVersion}/lan/users/me/picture";

    [Fact]
    public async Task CurrentUserPictureUpload_BindsMultipartFileAndUsesAuthenticatedSubject()
    {
        var userService = new RecordingUserService();
        await using var app = CreateApp(userService);
        var endpoint = GetEndpoint(app, CurrentUserPictureRoute, "PUT");

        Assert.Contains(endpoint.Metadata, metadata => metadata is Microsoft.AspNetCore.Authorization.IAuthorizeData);
        Assert.Contains(endpoint.Metadata, metadata => metadata.GetType().Name.Contains("Accepts", StringComparison.Ordinal));

        var file = new FormFile(new MemoryStream([1, 2, 3]), 0, 3, "picture", "avatar.png")
        {
            Headers = new HeaderDictionary { ["Content-Type"] = "image/png" }
        };
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = "PUT";
        context.Request.ContentType = "multipart/form-data; boundary=test";
        context.Features.Set<IFormFeature>(new FormFeature(new FormCollection([], new FormFileCollection { file })));
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "auth0|picture-owner")], "test"));
        context.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(context);

        Assert.Equal("auth0|picture-owner", userService.LastPictureAuth0UserId);
        Assert.Equal("picture", userService.LastPicture?.Name);
        Assert.Equal("avatar.png", userService.LastPicture?.FileName);
        Assert.Equal(3, userService.LastPicture?.Length);
    }

    [Fact]
    public async Task CurrentUserPictureDelete_UsesAuthenticatedSubjectAndRequiresAuthorization()
    {
        var userService = new RecordingUserService();
        await using var app = CreateApp(userService);
        var endpoint = GetEndpoint(app, CurrentUserPictureRoute, "DELETE");
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = "DELETE";
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "auth0|picture-owner")], "test"));
        context.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(context);

        Assert.Contains(endpoint.Metadata, metadata => metadata is Microsoft.AspNetCore.Authorization.IAuthorizeData);
        Assert.Equal("auth0|picture-owner", userService.LastPictureAuth0UserId);
        Assert.Equal(1, userService.RemovePictureCallCount);
    }

    [Theory]
    [InlineData("", null, null, null)]
    [InlineData("?page=2&pageSize=51", 2, 51, null)]
    [InlineData("?query=%20ALPHA%20&page=3", 3, null, " ALPHA ")]
    [InlineData("?query=", null, null, "")]
    [InlineData("?query=%20%20&pageSize=7", null, 7, "  ")]
    public async Task AdminPicker_DelegatesRawQueryAndPagingToUserService(
        string queryString,
        int? expectedPage,
        int? expectedPageSize,
        string? expectedQuery)
    {
        var userService = new RecordingUserService();
        await using var app = CreateApp(userService);

        var response = await InvokeGetAsync(app, queryString);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal(expectedPage, userService.LastPage);
        Assert.Equal(expectedPageSize, userService.LastPageSize);
        Assert.Equal(expectedQuery, userService.LastQuery);
        Assert.Equal(1, userService.CallCount);
    }

    [Theory]
    [InlineData("?page=0", "page")]
    [InlineData("?page=-1", "page")]
    [InlineData("?pageSize=0", "pageSize")]
    [InlineData("?pageSize=-1", "pageSize")]
    public async Task AdminPicker_RejectsNonPositivePagingWithValidationProblemBeforeServiceInvocation(
        string queryString,
        string expectedField)
    {
        var userService = new RecordingUserService();
        await using var app = CreateApp(userService);

        var response = await InvokeGetAsync(app, queryString);

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Equal(0, userService.CallCount);
        using var json = JsonDocument.Parse(response.Body);
        var fieldErrors = json.RootElement.GetProperty("errors").GetProperty(expectedField);
        Assert.StartsWith(expectedField, fieldErrors[0].GetString());
    }

    [Fact]
    public async Task AdminPicker_ReturnsServiceOptionsAsRawArray()
    {
        var userService = new RecordingUserService
        {
            Options =
            [
                new AdminUserOptionDTO(Guid.NewGuid(), "picker-user", "Picker Person"),
                new AdminUserOptionDTO(Guid.NewGuid(), "Incomplete profile", "Incomplete profile")
            ]
        };
        await using var app = CreateApp(userService);

        var response = await InvokeGetAsync(app, "");

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        using var json = JsonDocument.Parse(response.Body);
        var options = json.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, options.Count);
        Assert.Equal("picker-user", options[0].GetProperty("username").GetString());
        Assert.Equal("Picker Person", options[0].GetProperty("displayName").GetString());
        Assert.Equal("Incomplete profile", options[1].GetProperty("username").GetString());
    }

    private static WebApplication CreateApp(IUserService userService)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddApiVersioning();
        builder.Services.AddHttpConventions();
        builder.Services.AddSingleton(userService);

        var app = builder.Build();
        app.MapIdentityModule();
        return app;
    }

    private static async Task<(int StatusCode, string Body)> InvokeGetAsync(WebApplication app, string queryString)
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == AdminPickerRoute);
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = "GET";
        context.Request.QueryString = new QueryString(queryString);
        context.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private static RouteEndpoint GetEndpoint(WebApplication app, string route, string method) => ((IEndpointRouteBuilder)app).DataSources
        .SelectMany(source => source.Endpoints)
        .OfType<RouteEndpoint>()
        .Single(endpoint => endpoint.RoutePattern.RawText == route &&
            endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(method, StringComparer.OrdinalIgnoreCase) == true);

    private sealed class RecordingUserService : IUserService
    {
        public int CallCount { get; private set; }
        public string? LastQuery { get; private set; }
        public int? LastPage { get; private set; }
        public int? LastPageSize { get; private set; }
        public IReadOnlyList<AdminUserOptionDTO> Options { get; init; } = [];
        public string? LastPictureAuth0UserId { get; private set; }
        public IFormFile? LastPicture { get; private set; }
        public int RemovePictureCallCount { get; private set; }

        public Task<IReadOnlyList<AdminUserOptionDTO>> GetAdminUsersAsync(
            string? query,
            int? page,
            int? pageSize,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastQuery = query;
            LastPage = page;
            LastPageSize = pageSize;
            return Task.FromResult(Options);
        }

        public Task<GetUserDTO> CreateUserAsync(CreateUserProfileRequest request) => throw new NotSupportedException();
        public Task<GetUserDTO> CreateCurrentUserAsync(string auth0UserId, CompleteUserProfileRequest request) => throw new NotSupportedException();
        public Task<GetUserDTO> CompleteProfileAsync(string auth0UserId, CompleteUserProfileRequest request) => throw new NotSupportedException();
        public Task<CurrentUserProfileResponse> GetCurrentUserAsync(string auth0UserId) => throw new NotSupportedException();
        public Task<PublicUserProfileDTO> GetPublicUserProfileByUsernameAsync(string username) => throw new NotSupportedException();
        public Task<GetUserDTO> GetUserByUsernameAsync(string username) => throw new NotSupportedException();
        public Task<UserSearchResponseDTO> SearchUsersAsync(string? query, string? cursor, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GetUserDTO> UpdateCurrentUserAsync(string auth0UserId, UpdateUserProfileRequest request) => throw new NotSupportedException();
        public Task<CurrentUserProfileResponse> UploadCurrentUserPictureAsync(string auth0UserId, IFormFile picture, CancellationToken cancellationToken = default)
        {
            LastPictureAuth0UserId = auth0UserId;
            LastPicture = picture;
            return Task.FromResult<CurrentUserProfileResponse>(null!);
        }
        public Task<CurrentUserProfileResponse> RemoveCurrentUserPictureAsync(string auth0UserId, CancellationToken cancellationToken = default)
        {
            LastPictureAuth0UserId = auth0UserId;
            RemovePictureCallCount++;
            return Task.FromResult<CurrentUserProfileResponse>(null!);
        }
        public Task<UsernameAvailabilityResponse> CheckUsernameAvailabilityAsync(string auth0UserId, string username) => throw new NotSupportedException();
        public Task<UserActionResponse> ResendVerificationEmailAsync(string auth0UserId) => throw new NotSupportedException();
        public Task<UserActionResponse> SendPasswordResetEmailAsync(string auth0UserId) => throw new NotSupportedException();
        public Task<UserActionResponse> AnonymizeCurrentUserAsync(string auth0UserId) => throw new NotSupportedException();
        public Task DeleteUserAsync(string username) => throw new NotSupportedException();
        public Task DeleteUserByIdAsync(Guid id) => throw new NotSupportedException();
        public Task<IReadOnlyList<GetUserDTO>> GetAllUsersAsync(int page, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GetUserDTO> GetUserByIdAsync(Guid id) => throw new NotSupportedException();
        public Task<GetUserDTO> UpdateUserAsync(Guid id, UpdateUserProfileRequest request) => throw new NotSupportedException();
    }
}
