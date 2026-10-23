using Asp.Versioning;
using Mercurius.Modules.Identity.Application.DTOs;
using Mercurius.Modules.Shared.Search;
using Mercurius.Modules.Identity.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Security.Claims;
using Platform.Extensions;
using Mercurius.Modules.Identity.Application.Services.Auth0;
using Mercurius.Modules.Identity.Contracts;

namespace Mercurius.Modules.Identity.Endpoints;

internal static class UserEndpoints
{
    public static RouteGroupBuilder MapUserEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var apiVersionSet = endpoints.NewApiVersionSet()
        .HasApiVersion(new ApiVersion(1, 0))
        .ReportApiVersions()
        .Build();

        var group = endpoints.MapGroup("v{version:apiVersion}/lan/users")
                .WithApiVersionSet(apiVersionSet)
                .MapToApiVersion(new ApiVersion(1, 0))
                .WithTags("Users");

        var publicGroup = endpoints.MapGroup("v{version:apiVersion}/lan/public/users")
            .WithApiVersionSet(apiVersionSet)
            .MapToApiVersion(new ApiVersion(1, 0))
            .WithTags("Users");

        publicGroup.MapGet("/{username}", async (string username, IUserService userService) =>
        {
            return await userService.GetPublicUserProfileByUsernameAsync(username);
        })
        .AllowAnonymous();

        group.MapGet("/me", async (ClaimsPrincipal user, IUserService userService) =>
        {
            return await userService.GetCurrentUserAsync(user.GetAuth0UserId());
        })
        .RequireAuthorization();

        group.MapPut("/me", async (CompleteUserProfileRequest request, ClaimsPrincipal user, IUserService userService) =>
        {
            return await userService.CreateCurrentUserAsync(user.GetAuth0UserId(), request);
        })
        .RequireAuthorization();

        group.MapPatch("/me", async (UpdateUserProfileRequest request, ClaimsPrincipal user, IUserService userService) =>
        {
            return await userService.UpdateCurrentUserAsync(user.GetAuth0UserId(), request);
        })
        .RequireAuthorization();

        group.MapPut("/me/picture", async (IFormFile picture, ClaimsPrincipal user, IUserService userService, CancellationToken cancellationToken) =>
        {
            return Results.Ok(await userService.UploadCurrentUserPictureAsync(user.GetAuth0UserId(), picture, cancellationToken));
        })
        .RequireAuthorization()
        .Accepts<IFormFile>("multipart/form-data")
        .DisableAntiforgery();

        group.MapDelete("/me/picture", async (ClaimsPrincipal user, IUserService userService, CancellationToken cancellationToken) =>
        {
            return Results.Ok(await userService.RemoveCurrentUserPictureAsync(user.GetAuth0UserId(), cancellationToken));
        })
        .RequireAuthorization();

        group.MapGet("/me/username-availability", async (string username, ClaimsPrincipal user, IUserService userService) =>
        {
            return await userService.CheckUsernameAvailabilityAsync(user.GetAuth0UserId(), username);
        })
        .RequireAuthorization();

        group.MapGet("/", async Task<IResult> (HttpRequest request, string? query, string? cursor, int? page, int? pageSize, ClaimsPrincipal user, IUserService userService, CancellationToken cancellationToken) =>
        {
            if (request.Query.ContainsKey("query"))
            {
                SearchRequest.ValidateQueryLength(SearchRequest.NormalizeQuery(query));
                SearchRequest.ValidatePageSize(pageSize);

                var boundedPageSize = SearchRequest.BoundPageSize(pageSize);
                return Results.Ok(await userService.SearchUsersAsync(query, cursor, boundedPageSize, cancellationToken));
            }

            if (!user.IsInRole("admin"))
                return Results.Forbid();

            var validationProblem = ValidatePaging(page, pageSize);
            if (validationProblem is not null)
                return validationProblem;

            return Results.Ok(await userService.GetAllUsersAsync(
                page ?? 1,
                SearchRequest.BoundPageSize(pageSize),
                cancellationToken));
        })
        .RequireAuthorization()
        .RequireRateLimiting(SearchRateLimitPolicyNames.Authenticated)
        .Produces<IReadOnlyList<GetUserDTO>>()
        .ProducesValidationProblem();

        group.MapPost("/me/resend-verification-email", async (ClaimsPrincipal user, IUserService userService) =>
        {
            return await userService.ResendVerificationEmailAsync(user.GetAuth0UserId());
        })
        .RequireAuthorization();

        group.MapPost("/me/password-reset", async (ClaimsPrincipal user, IUserService userService) =>
        {
            return await userService.SendPasswordResetEmailAsync(user.GetAuth0UserId());
        })
        .RequireAuthorization();

        group.MapDelete("/me", async (ClaimsPrincipal user, IUserService userService, [Microsoft.AspNetCore.Mvc.FromServices] IAuthenticatedIdentityContext identityContext, [Microsoft.AspNetCore.Mvc.FromServices] Auth0AccountLifecycleService lifecycleService, CancellationToken cancellationToken) =>
        {
            var response = await userService.AnonymizeCurrentUserAsync(user.GetAuth0UserId());
            var deletionPending = await lifecycleService.IsDeletionPendingAsync(identityContext.UserId, cancellationToken);
            return Results.Accepted(value: new { message = "Account deletion requested.", deletionPending });
        })
        .RequireAuthorization();

        var adminGroup = group.MapGroup("")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "admin" });

        adminGroup.MapGet("/admins", async Task<IResult> (string? query, int? page, int? pageSize, IUserService userService, CancellationToken cancellationToken) =>
        {
            var validationProblem = ValidatePaging(page, pageSize);
            if (validationProblem is not null)
                return validationProblem;

            return Results.Ok(await userService.GetAdminUsersAsync(query, page, pageSize, cancellationToken));
        })
        .RequireRateLimiting(SearchRateLimitPolicyNames.Anonymous)
        .Produces<IReadOnlyList<AdminUserOptionDTO>>()
        .ProducesValidationProblem();

        adminGroup.MapPost("/", async (CreateUserProfileRequest request, IUserService userService) =>
        {
            return await userService.CreateUserAsync(request);
        });

        adminGroup.MapGet("/{id:guid}", async (Guid id, IUserService userService) =>
        {
            return await userService.GetUserByIdAsync(id);
        });

        adminGroup.MapGet("/{username:nonguid}", async (string username, IUserService userService) =>
        {
            return await userService.GetUserByUsernameAsync(username);
        });

        adminGroup.MapPatch("/{id:guid}", async (Guid id, UpdateUserProfileRequest request, IUserService userService) =>
        {
            return await userService.UpdateUserAsync(id, request);
        });

        adminGroup.MapDelete("/{id:guid}", async (Guid id, IUserService userService, [Microsoft.AspNetCore.Mvc.FromServices] Auth0AccountLifecycleService lifecycleService, CancellationToken cancellationToken) =>
        {
            await userService.DeleteUserByIdAsync(id);
            var deletionPending = await lifecycleService.IsDeletionPendingAsync(id, cancellationToken);
            return Results.Accepted(value: new { message = "Account deletion requested.", deletionPending });
        });

        adminGroup.MapDelete("/{username:nonguid}", async (string username, IUserService userService, [Microsoft.AspNetCore.Mvc.FromServices] Auth0AccountLifecycleService lifecycleService, CancellationToken cancellationToken) =>
        {
            var target = await userService.GetUserByUsernameAsync(username);
            await userService.DeleteUserByIdAsync(target.Id);
            var deletionPending = await lifecycleService.IsDeletionPendingAsync(target.Id, cancellationToken);
            return Results.Accepted(value: new { message = "Account deletion requested.", deletionPending });
        });

        adminGroup.MapDelete("/{username}/account", async (string username, IUserService userService, [Microsoft.AspNetCore.Mvc.FromServices] Auth0AccountLifecycleService lifecycleService, CancellationToken cancellationToken) =>
        {
            var target = await userService.GetUserByUsernameAsync(username);
            await userService.DeleteUserByIdAsync(target.Id);
            var deletionPending = await lifecycleService.IsDeletionPendingAsync(target.Id, cancellationToken);
            return Results.Accepted(value: new { message = "Account deletion requested.", deletionPending });
        });

        return group;
    }

    private static IResult? ValidatePaging(int? page, int? pageSize)
    {
        var errors = new Dictionary<string, string[]>();
        if (page is <= 0)
            errors["page"] = ["page must be greater than 0."];
        if (pageSize is <= 0)
            errors["pageSize"] = ["pageSize must be greater than 0."];

        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }
}
