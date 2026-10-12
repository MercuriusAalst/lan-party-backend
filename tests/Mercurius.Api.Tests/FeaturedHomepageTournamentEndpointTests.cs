using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Asp.Versioning;
using Mercurius.Modules.Tournament.Application.DTOs.Tournaments;
using Mercurius.Modules.Tournament.Application.Services;
using Mercurius.Modules.Tournament.Contracts;
using Mercurius.Modules.Tournament.Endpoints;
using Mercurius.Modules.Sponsorship.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Extensions;

namespace Mercurius.Api.Tests;

public sealed class FeaturedHomepageTournamentEndpointTests
{
    private static readonly Guid[] TournamentIds =
    [
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Guid.Parse("44444444-4444-4444-4444-444444444444")
    ];

    [Fact]
    public async Task PublicReadAndAdminReplacementUseTheExpectedHttpContract()
    {
        var service = new StubFeaturedHomepageTournamentService();
        await using var app = CreateApp(service);
        await app.StartAsync();
        using var client = CreateClient(app);

        using var publicResponse = await client.GetAsync("/v1/lan/featured-tournaments");
        Assert.Equal(HttpStatusCode.OK, publicResponse.StatusCode);
        using (var publicJson = JsonDocument.Parse(await publicResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(TournamentIds, publicJson.RootElement.GetProperty("tournamentIds")
                .EnumerateArray().Select(value => value.GetGuid()));
            var card = publicJson.RootElement.GetProperty("tournaments")[0];
            Assert.Equal("Featured Cup", card.GetProperty("name").GetString());
            Assert.Equal("images/featured.webp", card.GetProperty("imageUrl").GetString());
            Assert.Equal("Scheduled", card.GetProperty("status").GetString());
            Assert.Equal("SingleElimination", card.GetProperty("bracketType").GetString());
            Assert.Equal("BestOf1", card.GetProperty("format").GetString());
            var sponsorJson = card.GetProperty("sponsorPlacement");
            Assert.Equal("Acme Esports", sponsorJson.GetProperty("sponsorName").GetString());
            Assert.Equal("Presenting", sponsorJson.GetProperty("sponsorTier").GetString());
            Assert.Equal("TournamentPartner", sponsorJson.GetProperty("context").GetString());
            Assert.Equal(JsonValueKind.Null, publicJson.RootElement.GetProperty("tournaments")[1]
                .GetProperty("sponsorPlacement").ValueKind);
        }

        using var unauthenticatedResponse = await PutAsync(client, TournamentIds);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticatedResponse.StatusCode);

        using var regularUserRequest = CreatePutRequest(TournamentIds);
        regularUserRequest.Headers.Add("X-Test-Role", "user");
        using var forbiddenResponse = await client.SendAsync(regularUserRequest);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode);

        using var invalidRequest = CreatePutRequest([]);
        invalidRequest.Headers.Add("X-Test-Role", "admin");
        using var validationResponse = await client.SendAsync(invalidRequest);
        Assert.Equal(HttpStatusCode.BadRequest, validationResponse.StatusCode);
        using (var validationJson = JsonDocument.Parse(await validationResponse.Content.ReadAsStringAsync()))
            Assert.True(validationJson.RootElement.TryGetProperty("errors", out _));

        using var tooManyRequest = CreatePutRequest([.. TournamentIds, Guid.NewGuid()]);
        tooManyRequest.Headers.Add("X-Test-Role", "admin");
        using var tooManyResponse = await client.SendAsync(tooManyRequest);
        Assert.Equal(HttpStatusCode.BadRequest, tooManyResponse.StatusCode);

        using var duplicateRequest = CreatePutRequest([TournamentIds[0], TournamentIds[0]]);
        duplicateRequest.Headers.Add("X-Test-Role", "admin");
        using var duplicateResponse = await client.SendAsync(duplicateRequest);
        Assert.Equal(HttpStatusCode.BadRequest, duplicateResponse.StatusCode);

        using var malformedRequest = new HttpRequestMessage(HttpMethod.Put, "/v1/lan/featured-tournaments")
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json")
        };
        malformedRequest.Headers.Add("X-Test-Role", "admin");
        using var malformedResponse = await client.SendAsync(malformedRequest);
        Assert.Equal(HttpStatusCode.BadRequest, malformedResponse.StatusCode);

        foreach (var selection in new[] { TournamentIds[..1], TournamentIds[..3], TournamentIds })
        {
            using var successRequest = CreatePutRequest(selection);
            successRequest.Headers.Add("X-Test-Role", "admin");
            using var successResponse = await client.SendAsync(successRequest);
            Assert.Equal(HttpStatusCode.OK, successResponse.StatusCode);
            using var successJson = JsonDocument.Parse(await successResponse.Content.ReadAsStringAsync());
            Assert.Equal(selection, successJson.RootElement.GetProperty("tournamentIds")
                .EnumerateArray().Select(value => value.GetGuid()));
        }

        Assert.Equal(6, service.ReplaceCallCount);
    }

    private static WebApplication CreateApp(StubFeaturedHomepageTournamentService service)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, HeaderRoleAuthenticationHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddApiVersioning();
        builder.Services.AddHttpConventions();
        builder.Services.AddSingleton<IFeaturedHomepageTournamentService>(service);

        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapFeaturedHomepageTournamentEndpoints();
        return app;
    }

    private static HttpClient CreateClient(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!;
        return new HttpClient { BaseAddress = new Uri(Assert.Single(addresses.Addresses)) };
    }

    private static HttpRequestMessage CreatePutRequest(Guid[] tournamentIds) => new(HttpMethod.Put, "/v1/lan/featured-tournaments")
    {
        Content = new StringContent(JsonSerializer.Serialize(new { tournamentIds }), Encoding.UTF8, "application/json")
    };

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid[] tournamentIds) =>
        client.SendAsync(CreatePutRequest(tournamentIds));

    private sealed class StubFeaturedHomepageTournamentService : IFeaturedHomepageTournamentService
    {
        public int ReplaceCallCount { get; private set; }

        public Task<FeaturedHomepageTournamentsDTO> GetFeaturedTournamentsAsync(CancellationToken cancellationToken = default)
        {
            var card = new FeaturedHomepageTournamentCardDTO(
                TournamentIds[0],
                "Featured Cup",
                "images/featured.webp",
                TournamentStatus.Scheduled,
                BracketType.SingleElimination,
                GameFormat.BestOf1)
            {
                SponsorPlacement = new GetTournamentSponsorPlacementDTO
                {
                    Id = 7,
                    SponsorId = 3,
                    SponsorName = "Acme Esports",
                    SponsorTier = SponsorTier.Presenting,
                    SponsorLogoUrl = "logos/acme.png",
                    SponsorInfoUrl = "https://acme.example",
                    SponsorDescription = "Acme powers the main stage",
                    Context = SponsorContext.TournamentPartner,
                    Headline = "Headline",
                    SupportLine = null,
                    DisplayOrder = 0
                }
            };
            var unsponsored = new FeaturedHomepageTournamentCardDTO(
                TournamentIds[1],
                "Second Cup",
                null,
                TournamentStatus.Scheduled,
                BracketType.SingleElimination,
                GameFormat.BestOf1);
            return Task.FromResult(new FeaturedHomepageTournamentsDTO(TournamentIds, [card, unsponsored]));
        }

        public Task<Dictionary<string, string[]>?> ReplaceFeaturedTournamentsAsync(
            Guid[]? tournamentIds,
            CancellationToken cancellationToken = default)
        {
            ReplaceCallCount++;
            if (tournamentIds is null || tournamentIds.Length is < 1 or > 4 || tournamentIds.Distinct().Count() != tournamentIds.Length)
                return Task.FromResult<Dictionary<string, string[]>?>(new Dictionary<string, string[]>
                {
                    ["tournamentIds"] = ["Choose between one and four distinct tournaments."]
                });

            return Task.FromResult<Dictionary<string, string[]>?>(null);
        }
    }

    private sealed class HeaderRoleAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Role", out var role))
                return Task.FromResult(AuthenticateResult.NoResult());

            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Role, role.ToString())],
                Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
