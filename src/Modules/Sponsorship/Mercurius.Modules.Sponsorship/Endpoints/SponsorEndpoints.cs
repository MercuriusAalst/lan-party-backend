using Asp.Versioning;
using Mercurius.Modules.Sponsorship.Application.DTOs;
using Mercurius.Modules.Sponsorship.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Mercurius.Modules.Sponsorship.Endpoints;

internal static class SponsorEndpoints
{
    internal static RouteGroupBuilder MapSponsorEndpoints(
        this IEndpointRouteBuilder endpoints,
        long? sponsorLogoRequestBodySize)
    {
        var apiVersionSet = endpoints.NewApiVersionSet()
            .HasApiVersion(new ApiVersion(1, 0))
            .ReportApiVersions()
            .Build();

        var group = endpoints.MapGroup("v{version:apiVersion}/lan/sponsors")
            .WithApiVersionSet(apiVersionSet)
            .MapToApiVersion(new ApiVersion(1, 0))
            .WithTags("Sponsors")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "admin" });

        group.MapGet("/", async (ISponsorService sponsorService, CancellationToken cancellationToken) =>
        {
            return await sponsorService.GetSponsorsAsync(cancellationToken);
        })
        .AllowAnonymous();

        group.MapGet("/{id}", async (int id, ISponsorService sponsorService, CancellationToken cancellationToken) =>
        {
            return await sponsorService.GetSponsorByIdAsync(id, cancellationToken);
        })
        .AllowAnonymous();

        var createEndpoint = group.MapPost("/", async (
            [FromForm] CreateSponsorDTO sponsorDTO,
            ISponsorService sponsorService,
            CancellationToken cancellationToken) =>
        {
            return await sponsorService.CreateSponsorAsync(sponsorDTO, cancellationToken);
        }).DisableAntiforgery();

        var updateEndpoint = group.MapPatch("/{id}", async (
            int id,
            [FromForm] UpdateSponsorDTO value,
            ISponsorService sponsorService,
            CancellationToken cancellationToken) =>
        {
            return await sponsorService.UpdateSponsorAsync(id, value, cancellationToken);
        }).DisableAntiforgery();

        if (sponsorLogoRequestBodySize is { } maxRequestBodySize)
        {
            var requestSizeLimit = new RequestSizeLimitAttribute(maxRequestBodySize);
            createEndpoint.WithMetadata(requestSizeLimit);
            updateEndpoint.WithMetadata(requestSizeLimit);
        }

        group.MapDelete("/{id}", async (int id, ISponsorService sponsorService, CancellationToken cancellationToken) =>
        {
            await sponsorService.DeleteSponsorAsync(id, cancellationToken);
        });

        return group;
    }
}
