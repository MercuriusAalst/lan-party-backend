using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Mercurius.LAN.API.Configuration;
using Mercurius.LAN.API.Middleware;
using Mercurius.Modules.Shared.Exceptions;
using Mercurius.Modules.Sponsorship.Application.DTOs;
using Mercurius.Modules.Sponsorship.Application.Services;
using Mercurius.Modules.Sponsorship.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Extensions;

namespace Mercurius.Modules.Sponsorship.Tests;

public class SponsorUploadRequestLimitTests
{
    [Fact]
    public async Task SponsorCreateAndUpdate_AcceptTwoValidFilesAboveOldTotalLimit()
    {
        var limits = CreateLimits();
        var sponsorService = new RecordingSponsorService(limits.MaxFileSizeInBytes);
        await using var app = CreateApp(limits, sponsorService);
        await app.StartAsync();
        using var client = CreateClient(app);
        var fileLength = 3L * 1024 * 1024;
        Assert.True((fileLength * 2) > limits.MaxRequestBodySize);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/lan/sponsors/")
        {
            Content = CreateSponsorUploadContent(fileLength, fileLength)
        };
        createRequest.Headers.ExpectContinue = true;
        using var createResponse = await client.SendAsync(createRequest);

        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);

        using var updateRequest = new HttpRequestMessage(HttpMethod.Patch, "/v1/lan/sponsors/7")
        {
            Content = CreateSponsorUploadContent(fileLength, fileLength)
        };
        updateRequest.Headers.ExpectContinue = true;
        using var updateResponse = await client.SendAsync(updateRequest);

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        Assert.Equal(2, sponsorService.CallCount);
        Assert.Equal([fileLength, fileLength, fileLength, fileLength], sponsorService.FileLengths);
    }

    [Fact]
    public async Task SponsorCreate_RejectsIndividuallyOversizeFileWithinAggregateBodyLimit()
    {
        var limits = CreateLimits();
        var sponsorService = new RecordingSponsorService(limits.MaxFileSizeInBytes);
        await using var app = CreateApp(limits, sponsorService);
        await app.StartAsync();
        using var client = CreateClient(app);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/lan/sponsors/")
        {
            Content = CreateSponsorUploadContent(limits.MaxFileSizeInBytes + 1, 1)
        };
        request.Headers.ExpectContinue = true;
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, sponsorService.CallCount);
        Assert.Contains("File too big", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonSponsorEndpoint_RemainsBoundedByHostSingleFileLimit()
    {
        var limits = CreateLimits();
        var sponsorService = new RecordingSponsorService(limits.MaxFileSizeInBytes);
        var probe = new RequestSizeProbe();
        await using var app = CreateApp(limits, sponsorService, probe);
        await app.StartAsync();
        using var client = CreateClient(app);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/request-size-probe")
        {
            Content = new StringContent(
                new string('x', checked((int)limits.MaxRequestBodySize + 1)),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.ExpectContinue = true;
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, probe.CallCount);
    }

    private static MediaUploadRequestLimits CreateLimits() =>
        MediaUploadRequestLimits.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FileStorage:MaxFileSizeInMB"] = "5"
                })
                .Build());

    private static WebApplication CreateApp(
        MediaUploadRequestLimits limits,
        RecordingSponsorService sponsorService,
        RequestSizeProbe? probe = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options =>
            options.Limits.MaxRequestBodySize = limits.MaxRequestBodySize);
        builder.Services.Configure<FormOptions>(options =>
            options.MultipartBodyLengthLimit = limits.MaxSponsorLogoRequestBodySize);
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, AdminAuthenticationHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddApiVersioning();
        builder.Services.AddHttpConventions();
        builder.Services.AddApiProblemDetails<ApiExceptionHandler>();
        builder.Services.AddSingleton<ISponsorService>(sponsorService);
        builder.Services.AddSingleton(probe ?? new RequestSizeProbe());

        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UseRouting();
        app.UseMiddleware<EndpointRequestSizeLimitMiddleware>();
        app.UseApiExceptionHandling();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapSponsorshipModule(limits.MaxSponsorLogoRequestBodySize);
        app.MapPost("/request-size-probe", (JsonDocument _, RequestSizeProbe state) =>
        {
            state.CallCount++;
            return Results.NoContent();
        });
        return app;
    }

    private static HttpClient CreateClient(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!;
        var handler = new SocketsHttpHandler { Expect100ContinueTimeout = TimeSpan.FromSeconds(30) };
        return new HttpClient(handler) { BaseAddress = new Uri(Assert.Single(addresses.Addresses)) };
    }

    private static MultipartFormDataContent CreateSponsorUploadContent(
        long logoLength,
        long lightLogoLength)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("Request limit sponsor"), "name");
        content.Add(new StringContent(SponsorTier.Gold.ToString()), "sponsorTier");
        content.Add(new StringContent("https://example.test/sponsor"), "infoUrl");
        content.Add(CreateFileContent(logoLength), "Logo", "logo.png");
        content.Add(CreateFileContent(lightLogoLength), "LightLogo", "light-logo.png");
        return content;
    }

    private static ByteArrayContent CreateFileContent(long length)
    {
        var content = new ByteArrayContent(new byte[checked((int)length)]);
        content.Headers.ContentType = new("image/png");
        return content;
    }

    private sealed class RecordingSponsorService(long maxFileSizeInBytes) : ISponsorService
    {
        public int CallCount { get; private set; }
        public List<long> FileLengths { get; } = [];

        public Task<GetSponsorDTO> CreateSponsorAsync(
            CreateSponsorDTO sponsorDTO,
            CancellationToken cancellationToken = default)
        {
            Record(sponsorDTO.Logo!, sponsorDTO.LightLogo);
            return Task.FromResult(CreateResponse());
        }

        public Task<GetSponsorDTO> UpdateSponsorAsync(
            int id,
            UpdateSponsorDTO sponsorDTO,
            CancellationToken cancellationToken = default)
        {
            Record(sponsorDTO.Logo!, sponsorDTO.LightLogo);
            return Task.FromResult(CreateResponse(id));
        }

        public Task DeleteSponsorAsync(int id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GetSponsorDTO> GetSponsorByIdAsync(
            int id,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<GetSponsorDTO>> GetSponsorsAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private void Record(IFormFile logo, IFormFile? lightLogo)
        {
            CallCount++;
            Validate(logo);
            if (lightLogo is not null)
                Validate(lightLogo);
            FileLengths.Add(logo.Length);
            if (lightLogo is not null)
                FileLengths.Add(lightLogo.Length);
        }

        private void Validate(IFormFile file)
        {
            if (file.Length > maxFileSizeInBytes)
                throw new ValidationException("File too big, maximum file size is 5MB");
        }

        private static GetSponsorDTO CreateResponse(int id = 7) => new()
        {
            Id = id,
            Name = "Request limit sponsor",
            SponsorTier = SponsorTier.Gold,
            LogoUrl = "/images/logo.webp",
            LightLogoUrl = "/images/light-logo.webp",
            InfoUrl = "https://example.test/sponsor"
        };
    }

    private sealed class AdminAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "auth0|sponsor-upload"),
                new Claim(ClaimTypes.Role, "admin")
            ], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private sealed class RequestSizeProbe
    {
        public int CallCount { get; set; }
    }
}
