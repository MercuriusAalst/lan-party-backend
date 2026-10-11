using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Mercurius.Modules.Tournament.Application.DTOs.Tournaments;
using Mercurius.Modules.Tournament.Application.Services;
using Mercurius.Modules.Tournament.Endpoints;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Extensions;

namespace Mercurius.Api.Tests;

public class TournamentContactFormBindingTests
{
    [Fact]
    public async Task CreateAndPatchTournament_BindOptionalAdminIdTextAndTrackPatchPresence()
    {
        var commands = new RecordingTournamentCommands();
        await using var app = CreateApp(commands);
        await app.StartAsync();
        using var client = CreateClient(app);

        using var createResponse = await client.PostAsync("v1/lan/tournaments", CreateTournamentForm(string.Empty));

        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        Assert.Equal(string.Empty, commands.LastCreate?.AssignedAdminUserId);

        var tournamentId = Guid.NewGuid();
        using var patchWithEmptyAdmin = await client.PatchAsync(
            $"v1/lan/tournaments/{tournamentId}",
            CreateTournamentForm(string.Empty, includeImage: false));

        Assert.Equal(HttpStatusCode.OK, patchWithEmptyAdmin.StatusCode);
        Assert.Equal(string.Empty, commands.LastUpdate?.AssignedAdminUserId);
        Assert.True(commands.LastUpdate?.AssignedAdminUserIdSpecified);

        using var patchWithoutAdmin = await client.PatchAsync(
            $"v1/lan/tournaments/{tournamentId}",
            CreateTournamentForm(includeImage: false));

        Assert.Equal(HttpStatusCode.OK, patchWithoutAdmin.StatusCode);
        Assert.Null(commands.LastUpdate?.AssignedAdminUserId);
        Assert.False(commands.LastUpdate?.AssignedAdminUserIdSpecified);
    }

    [Fact]
    public async Task CreateTournament_WithWhitespaceAdminId_DoesNotFailBinding()
    {
        var commands = new RecordingTournamentCommands();
        await using var app = CreateApp(commands);
        await app.StartAsync();
        using var client = CreateClient(app);

        using var response = await client.PostAsync("v1/lan/tournaments", CreateTournamentForm("   "));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("   ", commands.LastCreate?.AssignedAdminUserId);
    }

    private static WebApplication CreateApp(RecordingTournamentCommands commands)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, AdminAuthenticationHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddApiVersioning();
        builder.Services.AddHttpConventions();
        builder.Services.AddSingleton<ITournamentManagementCommands>(commands);
        builder.Services.AddSingleton<ITournamentQueries>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<ITournamentLifecycleCommands>(_ => throw new NotSupportedException());

        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapTournamentEndpoints();
        return app;
    }

    private static HttpClient CreateClient(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!;
        return new HttpClient { BaseAddress = new Uri(Assert.Single(addresses.Addresses)) };
    }

    private static MultipartFormDataContent CreateTournamentForm(string? assignedAdminUserId = null, bool includeImage = true)
    {
        var form = new MultipartFormDataContent();
        Add(form, "Name", "Tournament");
        Add(form, "BracketType", "SingleElimination");
        Add(form, "Format", "BestOf1");
        Add(form, "FinalsFormat", "BestOf3");
        Add(form, "ParticipationMode", "Individual");
        Add(form, "PlannedStartTime", "2026-10-15T18:00:00Z");
        Add(form, "AverageGameDurationMinutes", "30");
        Add(form, "RoundBreakDurationMinutes", "10");
        if (assignedAdminUserId is not null)
            Add(form, "AssignedAdminUserId", assignedAdminUserId);
        if (includeImage)
        {
            var image = new ByteArrayContent([1, 2, 3]);
            image.Headers.ContentType = new("image/png");
            form.Add(image, "Image", "tournament.png");
        }
        return form;
    }

    private static void Add(MultipartFormDataContent form, string name, string value) =>
        form.Add(new StringContent(value), name);

    private sealed class RecordingTournamentCommands : ITournamentManagementCommands
    {
        public CreateTournamentDTO? LastCreate { get; private set; }
        public UpdateTournamentDTO? LastUpdate { get; private set; }

        public Task<GetTournamentDTO> CreateTournamentAsync(CreateTournamentDTO createTournamentDTO, CancellationToken cancellationToken = default)
        {
            LastCreate = createTournamentDTO;
            return Task.FromResult(new GetTournamentDTO { Id = Guid.NewGuid(), Name = createTournamentDTO.Name });
        }

        public Task<GetTournamentDTO> UpdateTournamentAsync(Guid id, UpdateTournamentDTO tournamentDTO, CancellationToken cancellationToken = default)
        {
            LastUpdate = tournamentDTO;
            return Task.FromResult(new GetTournamentDTO { Id = id, Name = tournamentDTO.Name });
        }

        public Task DeleteTournamentAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<GetTournamentDTO> ReplaceSponsorPlacementsAsync(Guid id, ReplaceTournamentSponsorsDTO sponsorDTO, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GetTournamentDTO { Id = id, Name = "Tournament" });
    }

    private sealed class AdminAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public AdminAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "admin")],
            Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
