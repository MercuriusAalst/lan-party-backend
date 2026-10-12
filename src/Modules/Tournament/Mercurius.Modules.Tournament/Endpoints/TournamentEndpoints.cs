using Asp.Versioning;
using Mercurius.Modules.Tournament.Application.DTOs.Tournaments;
using Mercurius.Modules.Tournament.Application.Services;
using Mercurius.Modules.Tournament.Contracts;
using Mercurius.Modules.Shared.Search;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Mercurius.Modules.Tournament.Endpoints;

internal static class TournamentEndpoints
{
    internal static RouteGroupBuilder MapTournamentEndpoints(this IEndpointRouteBuilder app)
    {
        var apiVersionSet = app.NewApiVersionSet()
                .HasApiVersion(new ApiVersion(1, 0))
                .ReportApiVersions()
                .Build();

        var group = app.MapGroup("v{version:apiVersion}/lan/tournaments")
            .WithApiVersionSet(apiVersionSet)
            .MapToApiVersion(new ApiVersion(1, 0))
            .WithTags("Tournaments")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "admin" });

        group.MapGet("/", async Task<IResult> (int? page, int? pageSize, ITournamentQueries tournamentQueries, CancellationToken cancellationToken) =>
        {
            var validationProblem = ValidatePaging(page, pageSize);
            if (validationProblem is not null)
                return validationProblem;

            var normalizedPage = page ?? 1;
            var normalizedPageSize = SearchRequest.BoundPageSize(pageSize);
            return Results.Ok(await tournamentQueries.GetAllTournamentsAsync(normalizedPage, normalizedPageSize, cancellationToken));
        })
        .AllowAnonymous()
        .Produces<IReadOnlyList<GetTournamentDTO>>()
        .ProducesValidationProblem();

        group.MapGet("/{tournamentId}", async (Guid tournamentId, ITournamentQueries tournamentQueries, CancellationToken cancellationToken) =>
        {
            return await tournamentQueries.GetTournamentByIdAsync(tournamentId, cancellationToken);
        })
        .AllowAnonymous();

        group.MapPost("/", async ([FromForm] CreateTournamentDTO createTournamentDTO, ITournamentManagementCommands tournamentManagementCommands, CancellationToken cancellationToken) =>
        {
            return await tournamentManagementCommands.CreateTournamentAsync(createTournamentDTO, cancellationToken);
        }).DisableAntiforgery();

        group.MapPatch("/{tournamentId}", async (Guid tournamentId, [FromForm] UpdateTournamentDTO updateTournamentDTO, HttpRequest request, ITournamentManagementCommands tournamentManagementCommands, CancellationToken cancellationToken) =>
        {
            var form = await request.ReadFormAsync(cancellationToken);
            updateTournamentDTO.AssignedAdminUserIdSpecified = form.ContainsKey(nameof(UpdateTournamentDTO.AssignedAdminUserId));
            updateTournamentDTO.FirstPlacePrizeSpecified = form.ContainsKey(nameof(UpdateTournamentDTO.FirstPlacePrize));
            updateTournamentDTO.SecondPlacePrizeSpecified = form.ContainsKey(nameof(UpdateTournamentDTO.SecondPlacePrize));
            updateTournamentDTO.ThirdPlacePrizeSpecified = form.ContainsKey(nameof(UpdateTournamentDTO.ThirdPlacePrize));
            return await tournamentManagementCommands.UpdateTournamentAsync(tournamentId, updateTournamentDTO, cancellationToken);
        }).DisableAntiforgery();

        group.MapDelete("/{tournamentId}", async (Guid tournamentId, ITournamentManagementCommands tournamentManagementCommands, CancellationToken cancellationToken) =>
        {
            await tournamentManagementCommands.DeleteTournamentAsync(tournamentId, cancellationToken);
        });

        group.MapPut("/{tournamentId}/sponsors", async (Guid tournamentId, ReplaceTournamentSponsorsDTO sponsorDTO, ITournamentManagementCommands tournamentManagementCommands, CancellationToken cancellationToken) =>
        {
            return await tournamentManagementCommands.ReplaceSponsorPlacementsAsync(tournamentId, sponsorDTO, cancellationToken);
        });

        group.MapPut("/{tournamentId}/lifecycle-state", async Task<IResult> (Guid tournamentId, UpdateTournamentLifecycleStateRequestDTO request, ITournamentLifecycleCommands tournamentLifecycleCommands, CancellationToken cancellationToken) =>
        {
            if (request.State is not { } state || !Enum.IsDefined(state))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["state"] = ["A supported tournament lifecycle state is required."] });

            switch (state)
            {
                case TournamentStatus.Scheduled:
                    await tournamentLifecycleCommands.ResetTournamentAsync(tournamentId, cancellationToken);
                    return Results.Ok();
                case TournamentStatus.InProgress:
                    await tournamentLifecycleCommands.StartTournamentAsync(tournamentId, cancellationToken);
                    return Results.Ok();
                case TournamentStatus.Completed:
                    return Results.Ok(await tournamentLifecycleCommands.CompleteTournamentAsync(tournamentId, cancellationToken));
                case TournamentStatus.Canceled:
                    await tournamentLifecycleCommands.CancelTournamentAsync(tournamentId, cancellationToken);
                    return Results.Ok();
                default:
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["state"] = ["A supported tournament lifecycle state is required."] });
            }
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
