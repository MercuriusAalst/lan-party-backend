using System.Text;
using System.Text.Json;
using Mercurius.Modules.Tournament;
using Mercurius.Modules.Tournament.Application.DTOs.Tournaments;
using Mercurius.Modules.Tournament.Application.Services;
using Mercurius.Modules.Teams;
using Mercurius.Modules.Teams.DTOs;
using Mercurius.Modules.Teams.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Mercurius.LAN.API.Configuration;
using Platform.Extensions;

namespace Mercurius.Api.Tests;

public class PublicCollectionPagingEndpointTests
{
    private const string TournamentCollectionRoute = "v{version:apiVersion}/lan/tournaments/";
    private const string TeamCollectionRoute = "v{version:apiVersion}/lan/teams/";

    [Theory]
    [InlineData("", 1, 20)]
    [InlineData("?page=2&pageSize=7", 2, 7)]
    [InlineData("?page=3&pageSize=51", 3, 50)]
    public async Task CollectionEndpoints_NormalizePagingAndReturnRawArrays(
        string queryString,
        int expectedPage,
        int expectedPageSize)
    {
        var tournamentQueries = new RecordingTournamentQueries();
        var teamService = new RecordingTeamEndpointService();
        await using var app = CreateApp(tournamentQueries, teamService);

        var tournamentResponse = await InvokeGetAsync(app, TournamentCollectionRoute, queryString);
        var teamResponse = await InvokeGetAsync(app, TeamCollectionRoute, queryString);

        Assert.Equal(StatusCodes.Status200OK, tournamentResponse.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, teamResponse.StatusCode);
        Assert.Equal((expectedPage, expectedPageSize), tournamentQueries.LastPaging);
        Assert.Equal((expectedPage, expectedPageSize), teamService.LastPaging);
        Assert.Equal(JsonValueKind.Array, JsonDocument.Parse(tournamentResponse.Body).RootElement.ValueKind);
        Assert.Equal(JsonValueKind.Array, JsonDocument.Parse(teamResponse.Body).RootElement.ValueKind);
    }

    [Theory]
    [InlineData("?page=0")]
    [InlineData("?page=-1")]
    [InlineData("?pageSize=0")]
    [InlineData("?pageSize=-1")]
    public async Task CollectionEndpoints_RejectNonPositivePagingBeforeServiceInvocation(string queryString)
    {
        var tournamentQueries = new RecordingTournamentQueries();
        var teamService = new RecordingTeamEndpointService();
        await using var app = CreateApp(tournamentQueries, teamService);

        var tournamentResponse = await InvokeGetAsync(app, TournamentCollectionRoute, queryString);
        var teamResponse = await InvokeGetAsync(app, TeamCollectionRoute, queryString);

        Assert.Equal(StatusCodes.Status400BadRequest, tournamentResponse.StatusCode);
        Assert.Equal(StatusCodes.Status400BadRequest, teamResponse.StatusCode);
        Assert.Equal(0, tournamentQueries.CallCount);
        Assert.Equal(0, teamService.CallCount);
    }

    [Fact]
    public async Task TournamentItem_WithMalformedId_ReachesGuidBindingAndReturnsBadRequest()
    {
        var tournamentQueries = new RecordingTournamentQueries();
        await using var app = CreateApp(tournamentQueries, new RecordingTeamEndpointService());
        await app.StartAsync();
        using var client = CreateClient(app);

        using var response = await client.GetAsync("v1/lan/tournaments/not-a-guid");

        Assert.Equal(StatusCodes.Status400BadRequest, (int)response.StatusCode);
        Assert.Equal(0, tournamentQueries.CallCount);
    }

    [Fact]
    public async Task TeamLogoUpload_AcceptsExactFileBoundary_AndKestrelRejectsOverEnvelopeBeforeServiceInvocation()
    {
        var limits = MediaUploadRequestLimits.FromConfiguration(CreateFileStorageConfiguration(5));
        var teamService = new RecordingTeamEndpointService();
        await using var app = CreateKestrelUploadApp(teamService, limits);
        await app.StartAsync();
        using var client = CreateClient(app);

        using var acceptedContent = CreateLogoUploadContent(limits.MaxFileSizeInBytes);
        using var acceptedResponse = await client.PutAsync($"v1/lan/teams/{Guid.NewGuid()}/logo", acceptedContent);

        Assert.Equal(StatusCodes.Status200OK, (int)acceptedResponse.StatusCode);
        Assert.Equal(1, teamService.UploadLogoCallCount);
        Assert.Equal(limits.MaxFileSizeInBytes, teamService.LastLogoLength);

        using var rejectedRequest = new HttpRequestMessage(HttpMethod.Put, $"v1/lan/teams/{Guid.NewGuid()}/logo")
        {
            Content = CreateLogoUploadContent(limits.MaxRequestBodySize)
        };
        rejectedRequest.Headers.ExpectContinue = true;
        using var rejectedResponse = await client.SendAsync(rejectedRequest);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, (int)rejectedResponse.StatusCode);
        Assert.Equal(1, teamService.UploadLogoCallCount);
    }

    [Fact]
    public void MediaUploadRequestLimits_DeriveByteLimitsFromFileStorage()
    {
        var limits = MediaUploadRequestLimits.FromConfiguration(CreateFileStorageConfiguration(5));

        Assert.Equal(5L * 1024 * 1024, limits.MaxFileSizeInBytes);
        Assert.Equal(64L * 1024, MediaUploadRequestLimits.MultipartEnvelopeSizeInBytes);
        Assert.Equal(5_308_416L, limits.MaxRequestBodySize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MediaUploadRequestLimits_RejectNonPositiveConfiguredFileSizes(int maxFileSizeInMegabytes)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            MediaUploadRequestLimits.FromConfiguration(CreateFileStorageConfiguration(maxFileSizeInMegabytes)));

        Assert.Equal("FileStorage:MaxFileSizeInMB must be a positive number of mebibytes.", exception.Message);
    }

    [Fact]
    public void MediaUploadRequestLimits_ConvertsLargestSupportedConfiguredValueWithoutOverflow()
    {
        var limits = MediaUploadRequestLimits.FromConfiguration(CreateFileStorageConfiguration(int.MaxValue));

        Assert.Equal((long)int.MaxValue * 1024 * 1024, limits.MaxFileSizeInBytes);
        Assert.Equal(limits.MaxFileSizeInBytes + MediaUploadRequestLimits.MultipartEnvelopeSizeInBytes, limits.MaxRequestBodySize);
    }

    [Fact]
    public async Task KestrelLimit_AppliesToNonMediaJsonRequestBodies()
    {
        var limits = MediaUploadRequestLimits.FromConfiguration(CreateFileStorageConfiguration(5));
        await using var app = CreateKestrelUploadApp(new RecordingTeamEndpointService(), limits);
        await app.StartAsync();
        using var client = CreateClient(app);
        var probe = app.Services.GetRequiredService<RequestSizeProbe>();

        using var acceptedResponse = await client.PostAsync(
            "request-size-probe",
            new StringContent("{\"value\":\"current JSON body\"}", Encoding.UTF8, "application/json"));

        Assert.Equal(StatusCodes.Status204NoContent, (int)acceptedResponse.StatusCode);
        Assert.Equal(1, probe.CallCount);

        using var rejectedRequest = new HttpRequestMessage(HttpMethod.Post, "request-size-probe")
        {
            Content = new StringContent(
                new string('x', checked((int)limits.MaxRequestBodySize + 1)),
                Encoding.UTF8,
                "application/json")
        };
        rejectedRequest.Headers.ExpectContinue = true;
        using var rejectedResponse = await client.SendAsync(rejectedRequest);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, (int)rejectedResponse.StatusCode);
        Assert.Equal(1, probe.CallCount);
    }

    private static WebApplication CreateApp(
        ITournamentQueries tournamentQueries,
        ITeamEndpointService teamEndpointService)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddApiVersioning();
        builder.Services.AddHttpConventions();
        builder.Services.AddSingleton(tournamentQueries);
        builder.Services.AddScoped<ITournamentManagementCommands>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<ITournamentLifecycleCommands>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<ITournamentRegistrationService>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<IMatchService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton(teamEndpointService);

        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapTournamentModule();
        app.MapTeamsModule();
        return app;
    }

    private static WebApplication CreateKestrelUploadApp(
        RecordingTeamEndpointService teamEndpointService,
        MediaUploadRequestLimits limits)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = limits.MaxRequestBodySize);
        builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = limits.MaxFileSizeInBytes);
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddApiVersioning();
        builder.Services.AddSingleton<ITeamEndpointService>(teamEndpointService);
        builder.Services.AddSingleton<RequestSizeProbe>();

        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapTeamsModule();
        app.MapPost("/request-size-probe", (JsonDocument _, RequestSizeProbe probe) =>
        {
            probe.CallCount++;
            return Results.NoContent();
        });
        return app;
    }

    private static HttpClient CreateClient(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!;
        // Wait for Kestrel's early 413 instead of streaming the oversized body after the default 1s,
        // which races the server closing the connection on slow CI runners.
        var handler = new SocketsHttpHandler { Expect100ContinueTimeout = TimeSpan.FromSeconds(30) };
        return new HttpClient(handler) { BaseAddress = new Uri(Assert.Single(addresses.Addresses)) };
    }

    private static MultipartFormDataContent CreateLogoUploadContent(long fileLength)
    {
        var fileContent = new ByteArrayContent(new byte[checked((int)fileLength)]);
        fileContent.Headers.ContentType = new("image/png");
        var content = new MultipartFormDataContent();
        content.Add(fileContent, "logo", "logo.png");
        return content;
    }

    private static IConfiguration CreateFileStorageConfiguration(int maxFileSizeInMegabytes)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:MaxFileSizeInMB"] = maxFileSizeInMegabytes.ToString()
            })
            .Build();
    }

    private static async Task<(int StatusCode, string Body)> InvokeGetAsync(
        WebApplication app,
        string routePattern,
        string queryString)
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint =>
                endpoint.RoutePattern.RawText == routePattern &&
                endpoint.Metadata.OfType<IHttpMethodMetadata>()
                    .Any(metadata => metadata.HttpMethods.Contains("GET")));
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = "GET";
        context.Request.QueryString = new QueryString(queryString);
        context.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private sealed class RecordingTournamentQueries : ITournamentQueries
    {
        public int CallCount { get; private set; }
        public (int Page, int PageSize) LastPaging { get; private set; }

        public Task<IReadOnlyList<GetTournamentDTO>> GetAllTournamentsAsync(
            int page,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastPaging = (page, pageSize);
            return Task.FromResult<IReadOnlyList<GetTournamentDTO>>([new GetTournamentDTO { Id = Guid.NewGuid(), Name = "Paged tournament" }]);
        }

        public Task<GetTournamentDTO> GetTournamentByIdAsync(Guid tournamentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingTeamEndpointService : ITeamEndpointService
    {
        public int CallCount { get; private set; }
        public (int Page, int PageSize) LastPaging { get; private set; }
        public int UploadLogoCallCount { get; private set; }
        public long? LastLogoLength { get; private set; }

        public Task<IReadOnlyList<TeamResponseDTO>> GetAllTeamsAsync(
            int page,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastPaging = (page, pageSize);
            return Task.FromResult<IReadOnlyList<TeamResponseDTO>>([new TeamResponseDTO { Id = Guid.NewGuid(), Name = "Paged team" }]);
        }

        public Task<TeamResponseDTO> GetTeamByIdAsync(Guid teamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamManagementSummaryResponseDTO> CreateCurrentUserTeamAsync(string auth0UserId, CreateTeamRequestDTO request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CurrentUserTeamSummaryResponseDTO> GetCurrentUserTeamSummaryAsync(string auth0UserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TeamInviteSummaryResponseDTO>> GetCurrentUserInvitesAsync(string auth0UserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TeamInviteSummaryResponseDTO>> GetCurrentUserSentInvitesAsync(string auth0UserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamManagementSummaryResponseDTO> LeaveTeamAsync(string auth0UserId, Guid teamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamManagementSummaryResponseDTO> RemoveMemberAsync(string auth0UserId, Guid teamId, Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteTeamAsync(string auth0UserId, Guid teamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamInviteResponseDTO> InviteUserAsync(string auth0UserId, Guid teamId, Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamInviteResponseDTO> CancelInviteAsync(string auth0UserId, Guid teamId, Guid inviteId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamInviteResponseDTO> RespondToInviteAsync(string auth0UserId, Guid inviteId, bool accept, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamManagementSummaryResponseDTO> TransferCaptainAsync(string auth0UserId, Guid teamId, Guid newCaptainUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamLogoResponseDTO> UploadTeamLogoAsync(string auth0UserId, Guid teamId, IFormFile logo, CancellationToken cancellationToken = default)
        {
            UploadLogoCallCount++;
            LastLogoLength = logo.Length;
            return Task.FromResult(new TeamLogoResponseDTO(teamId, "images/test.webp"));
        }
        public Task<TeamLogoResponseDTO> RemoveTeamLogoAsync(string auth0UserId, Guid teamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PublicTeamProfileResponseDTO> GetPublicTeamProfileAsync(string teamName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TestAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "auth0|upload-test")
            ], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private sealed class RequestSizeProbe
    {
        public int CallCount { get; set; }
    }
}
