using Asp.Versioning;
using Mercurius.Modules.Tournament.Application.DTOs.Matches;
using Mercurius.Modules.Tournament.Application.Services;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace Mercurius.Modules.Tournament.Endpoints;

internal static class MatchEndpoints
{
    internal static RouteGroupBuilder MapMatchEndpoints(this IEndpointRouteBuilder app)
    {
        var apiVersionSet = app.NewApiVersionSet()
            .HasApiVersion(new ApiVersion(1, 0))
            .ReportApiVersions()
            .Build();
        var group = app.MapGroup("v{version:apiVersion}/lan/matches")
            .WithApiVersionSet(apiVersionSet)
            .MapToApiVersion(new ApiVersion(1, 0))
            .WithTags("Matches");

        group.MapGet("/{id:guid}", async (Guid id, IMatchService matchService, CancellationToken cancellationToken) =>
        {
            return await matchService.GetMatchByIdAsync(id, cancellationToken);
        })
        .AllowAnonymous();

        group.MapGet("/{id:guid}/opponent-profile", async (
            Guid id,
            ClaimsPrincipal user,
            IMatchService matchService,
            CancellationToken cancellationToken) =>
        {
            return await matchService.GetOpponentUserProfileAsync(id, GetAuth0UserId(user), cancellationToken);
        })
        .RequireAuthorization();

        group.MapGet("/{id:guid}/me", async (
            Guid id,
            ClaimsPrincipal user,
            IMatchService matchService,
            CancellationToken cancellationToken) =>
        {
            return await matchService.GetMatchActionStateAsync(
                id,
                GetAuth0UserId(user),
                user.IsInRole("admin"),
                cancellationToken);
        })
        .RequireAuthorization();

        group.MapPost("/{id:guid}/confirm-ended", async (
            Guid id,
            ClaimsPrincipal user,
            IMatchService matchService,
            CancellationToken cancellationToken) =>
        {
            return await matchService.ConfirmEndedAsync(id, GetAuth0UserId(user), cancellationToken);
        })
        .RequireAuthorization();

        group.MapPut("/{id:guid}/score", async (
            Guid id,
            SubmitMatchScoreDTO request,
            ClaimsPrincipal user,
            IMatchService matchService,
            CancellationToken cancellationToken) =>
        {
            return await matchService.SubmitScoreAsync(id, GetAuth0UserId(user), request, cancellationToken);
        })
        .RequireAuthorization();

        group.MapPost("/{id:guid}/forfeit", async (
            Guid id,
            ForfeitMatchDTO request,
            ClaimsPrincipal user,
            IMatchService matchService,
            CancellationToken cancellationToken) =>
        {
            return await matchService.ForfeitAsync(
                id,
                GetAuth0UserId(user),
                request,
                user.IsInRole("admin"),
                cancellationToken);
        })
        .RequireAuthorization();

        var adminGroup = group.MapGroup(string.Empty)
            .RequireAuthorization(new AuthorizeAttribute { Roles = "admin" });

        adminGroup.MapPost("/{id:guid}/resolve", async (
            Guid id,
            ResolveMatchDTO request,
            ClaimsPrincipal user,
            IMatchService matchService,
            CancellationToken cancellationToken) =>
        {
            return await matchService.ResolveAsync(id, GetAuth0UserId(user), request, cancellationToken);
        });

        adminGroup.MapPost("/{id:guid}/reverse", async (
            Guid id,
            ClaimsPrincipal user,
            IMatchService matchService,
            CancellationToken cancellationToken) =>
        {
            return await matchService.ReverseAsync(id, GetAuth0UserId(user), cancellationToken);
        });

        adminGroup.MapPost("/{id:guid}/admin/forfeit", async (
            Guid id,
            ForfeitMatchDTO request,
            ClaimsPrincipal user,
            IMatchService matchService,
            CancellationToken cancellationToken) =>
        {
            return await matchService.ForfeitAsync(
                id,
                GetAuth0UserId(user),
                request,
                true,
                cancellationToken);
        });

        return group;
    }

    private static string GetAuth0UserId(ClaimsPrincipal user)
    {
        var subject = user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(subject))
            throw new UnauthorizedAccessException("Authenticated user id is missing.");
        return subject;
    }
}
