using System.Security.Claims;
using Asp.Versioning;
using Mercurius.Modules.Teams.Application.DTOs;
using Mercurius.Modules.Teams.Application.Services;
using Mercurius.Modules.Shared.Search;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Platform.Extensions;

namespace Mercurius.Modules.Teams.Endpoints;

internal static class TeamEndpoints
{
    public static RouteGroupBuilder MapTeamEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var apiVersionSet = endpoints.NewApiVersionSet()
            .HasApiVersion(new ApiVersion(1, 0))
            .ReportApiVersions()
            .Build();

        var group = endpoints.MapGroup("v{version:apiVersion}/lan/teams")
            .WithApiVersionSet(apiVersionSet)
            .MapToApiVersion(new ApiVersion(1, 0))
            .WithTags("Teams");

        var publicGroup = endpoints.MapGroup("v{version:apiVersion}/lan/public/teams")
            .WithApiVersionSet(apiVersionSet)
            .MapToApiVersion(new ApiVersion(1, 0))
            .WithTags("Public Teams");

        var inviteGroup = endpoints.MapGroup("v{version:apiVersion}/lan/team-invites")
            .WithApiVersionSet(apiVersionSet)
            .MapToApiVersion(new ApiVersion(1, 0))
            .WithTags("Team Invites");

        group.MapGet("/", async Task<IResult> (int? page, int? pageSize, [FromServices] ITeamEndpointService teamService, CancellationToken cancellationToken) =>
        {
            var validationProblem = ValidatePaging(page, pageSize);
            if (validationProblem is not null)
                return validationProblem;

            var normalizedPage = page ?? 1;
            var normalizedPageSize = SearchRequest.BoundPageSize(pageSize);
            return Results.Ok(await teamService.GetAllTeamsAsync(normalizedPage, normalizedPageSize, cancellationToken));
        })
        .AllowAnonymous()
        .Produces<IReadOnlyList<TeamResponseDTO>>()
        .ProducesValidationProblem();

        group.MapGet("/{id:guid}", async (Guid id, [FromServices] ITeamEndpointService teamService, CancellationToken cancellationToken) =>
        {
            return await teamService.GetTeamByIdAsync(id, cancellationToken);
        })
        .AllowAnonymous();

        group.MapPost("/", async (CreateTeamRequestDTO request, ClaimsPrincipal user, [FromServices] ITeamEndpointService teamService, CancellationToken cancellationToken) =>
        {
            return await teamService.CreateCurrentUserTeamAsync(user.GetAuth0UserId(), request, cancellationToken);
        })
        .RequireAuthorization();

        group.MapGet("/me/summary", async (ClaimsPrincipal user, [FromServices] ITeamEndpointService teamService, CancellationToken cancellationToken) =>
        {
            return await teamService.GetCurrentUserTeamSummaryAsync(user.GetAuth0UserId(), cancellationToken);
        })
        .RequireAuthorization();

        group.MapGet("/me/invites", async (ClaimsPrincipal user, [FromServices] ITeamEndpointService teamService, CancellationToken cancellationToken) =>
        {
            return await teamService.GetCurrentUserInvitesAsync(user.GetAuth0UserId(), cancellationToken);
        })
        .RequireAuthorization();

        group.MapGet("/me/sent-invites", async (ClaimsPrincipal user, [FromServices] ITeamEndpointService teamService, CancellationToken cancellationToken) =>
        {
            return await teamService.GetCurrentUserSentInvitesAsync(user.GetAuth0UserId(), cancellationToken);
        })
        .RequireAuthorization();

        group.MapDelete("/{id:guid}/members/me", async (Guid id, ClaimsPrincipal user, [FromServices] ITeamEndpointService teamService, CancellationToken cancellationToken) =>
        {
            return await teamService.LeaveTeamAsync(user.GetAuth0UserId(), id, cancellationToken);
        })
        .RequireAuthorization();

        group.MapDelete("/{id:guid}/members/{userId:guid}", async (Guid id, Guid userId, ClaimsPrincipal user, [FromServices] ITeamEndpointService teamService, CancellationToken cancellationToken) =>
        {
            return await teamService.RemoveMemberAsync(user.GetAuth0UserId(), id, userId, cancellationToken);
        })
        .RequireAuthorization();

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, [FromServices] ITeamEndpointService teamService, CancellationToken cancellationToken) =>
        {
            await teamService.DeleteTeamAsync(user.GetAuth0UserId(), id, cancellationToken);
            return Results.NoContent();
        })
        .RequireAuthorization();

        group.MapPost("/{id:guid}/invites", async Task<IResult> (Guid id, CreateTeamInviteRequestDTO request, ClaimsPrincipal user, [FromServices] ITeamEndpointService teamService, CancellationToken cancellationToken) =>
        {
            if (request.UserId is not { } userId || userId == Guid.Empty)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["userId"] = ["A recipient user id is required."] });

            return Results.Ok(await teamService.InviteUserAsync(user.GetAuth0UserId(), id, userId, cancellationToken));
        })
        .RequireAuthorization();

        group.MapDelete("/{id}/invites/{inviteId}", async (Guid id, Guid inviteId, ClaimsPrincipal user, [FromServices] ITeamEndpointService teamService, CancellationToken cancellationToken) =>
        {
            return await teamService.CancelInviteAsync(user.GetAuth0UserId(), id, inviteId, cancellationToken);
        })
        .RequireAuthorization();

        inviteGroup.MapPatch("/{inviteId:guid}", async (Guid inviteId, RespondTeamInviteRequestDTO request, ClaimsPrincipal user, [FromServices] ITeamEndpointService teamService, CancellationToken cancellationToken) =>
        {
            return await teamService.RespondToInviteAsync(user.GetAuth0UserId(), inviteId, request.Accept, cancellationToken);
        })
        .RequireAuthorization();

        group.MapPut("/{id}/captain", async (Guid id, TransferCaptainRequestDTO request, ClaimsPrincipal user, [FromServices] ITeamEndpointService teamService, CancellationToken cancellationToken) =>
        {
            return await teamService.TransferCaptainAsync(user.GetAuth0UserId(), id, request.NewCaptainUserId, cancellationToken);
        })
        .RequireAuthorization();

        group.MapPut("/{id}/logo", async (Guid id, IFormFile logo, ClaimsPrincipal user, [FromServices] ITeamEndpointService teamService, CancellationToken cancellationToken) =>
        {
            return await teamService.UploadTeamLogoAsync(user.GetAuth0UserId(), id, logo, cancellationToken);
        })
        .Accepts<IFormFile>("multipart/form-data")
        .DisableAntiforgery()
        .RequireAuthorization();

        group.MapDelete("/{id}/logo", async (Guid id, ClaimsPrincipal user, [FromServices] ITeamEndpointService teamService, CancellationToken cancellationToken) =>
        {
            return await teamService.RemoveTeamLogoAsync(user.GetAuth0UserId(), id, cancellationToken);
        })
        .RequireAuthorization();

        publicGroup.MapGet("/{teamName}", async (string teamName, [FromServices] ITeamEndpointService teamService, CancellationToken cancellationToken) =>
        {
            return await teamService.GetPublicTeamProfileAsync(teamName, cancellationToken);
        })
        .AllowAnonymous();

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
