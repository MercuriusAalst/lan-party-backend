using Asp.Versioning;
using Mercurius.Modules.Tournament.Application.DTOs.Tournaments;
using Mercurius.Modules.Tournament.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Mercurius.Modules.Tournament.Endpoints;

internal static class FeaturedHomepageTournamentEndpoints
{
    internal static IEndpointRouteBuilder MapFeaturedHomepageTournamentEndpoints(this IEndpointRouteBuilder app)
    {
        var apiVersionSet = app.NewApiVersionSet()
            .HasApiVersion(new ApiVersion(1, 0))
            .ReportApiVersions()
            .Build();

        var group = app.MapGroup("v{version:apiVersion}/lan/featured-tournaments")
            .WithApiVersionSet(apiVersionSet)
            .MapToApiVersion(new ApiVersion(1, 0))
            .WithTags("Featured Tournaments");

        group.MapGet("", async Task<IResult> (
            [FromServices] IFeaturedHomepageTournamentService service,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await service.GetFeaturedTournamentsAsync(cancellationToken));
        })
        .AllowAnonymous()
        .Produces<FeaturedHomepageTournamentsDTO>();

        group.MapPut("", async Task<IResult> (
            FeaturedHomepageTournamentIdsDTO request,
            [FromServices] IFeaturedHomepageTournamentService service,
            CancellationToken cancellationToken) =>
        {
            var errors = await service.ReplaceFeaturedTournamentsAsync(request.TournamentIds, cancellationToken);
            if (errors is not null)
                return Results.ValidationProblem(errors);

            return Results.Ok(new FeaturedHomepageTournamentIdsDTO(request.TournamentIds));
        })
        .RequireAuthorization(new AuthorizeAttribute { Roles = "admin" })
        .Produces<FeaturedHomepageTournamentIdsDTO>()
        .ProducesValidationProblem();

        return app;
    }
}
