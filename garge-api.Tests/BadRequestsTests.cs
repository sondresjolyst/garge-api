using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SjolystInnovation.AppKit.Hosting;
using SjolystInnovation.AppKit.SiteSettings;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// An unreadable JSON body answers 400 through <c>UseAppKitBadRequests</c>, after the 401 for a caller who
/// is not signed in. It runs in Development, where ASP.NET Core throws for a body that is not valid JSON.
/// </summary>
public class BadRequestsTests
{
    private const string ValidSettings = """
        {"contactRecipientEmail":"post@example.com","companyName":"Garge","streetAddress":"Gata 1","publicEmail":"post@example.com"}
        """;

    [Fact]
    public async Task SignedOut_InvalidUtf8_Answers401()
    {
        await using var app = await BadRequestsApp.StartAsync();

        var response = await app.PutSettingsAsync(InvalidUtf8(), roles: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_InvalidUtf8_Answers400()
    {
        await using var app = await BadRequestsApp.StartAsync();

        var response = await app.PutSettingsAsync(InvalidUtf8());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Admin_MalformedJson_Answers400()
    {
        await using var app = await BadRequestsApp.StartAsync();

        var response = await app.PutSettingsAsync(Encoding.UTF8.GetBytes("{\"companyName\":"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Admin_ValidBody_IsSaved()
    {
        await using var app = await BadRequestsApp.StartAsync();

        var response = await app.PutSettingsAsync(Encoding.UTF8.GetBytes(ValidSettings));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"companyName\":\"Garge\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>The valid settings with a lone continuation byte in the company name.</summary>
    private static byte[] InvalidUtf8()
    {
        var bytes = Encoding.UTF8.GetBytes(ValidSettings.Replace("\"Garge\"", "\"Ga?ge\""));
        bytes[Array.IndexOf(bytes, (byte)'?')] = 0x80;
        return bytes;
    }

    /// <summary>Signs a request in with the roles in <c>X-Test-Roles</c>.</summary>
    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Roles", out var roles))
                return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "u1") };
            claims.AddRange(roles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => new Claim(ClaimTypes.Role, r)));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), "Test")));
        }
    }

    /// <summary>A test server with the middleware in the order Program.cs uses and the site settings endpoints.</summary>
    private sealed class BadRequestsApp : IAsyncDisposable
    {
        private readonly string _database = Guid.NewGuid().ToString();
        private IHost _host = default!;
        private HttpClient _client = default!;

        public static async Task<BadRequestsApp> StartAsync()
        {
            var app = new BadRequestsApp();
            app._host = new HostBuilder()
                .UseEnvironment(Environments.Development)
                .ConfigureWebHost(web => web
                    .UseTestServer()
                    .ConfigureServices(s =>
                    {
                        s.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(app._database));
                        s.AddSingleton(new Mock<IAppSettingsCache>().Object);
                        s.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                        s.AddAuthorization(o => o.AddPolicy("Admin", p => p.RequireRole("Admin")));
                        s.AddAppKitRateLimiting(new ConfigurationBuilder().Build().GetSection("RateLimiting"));
                        s.AddRouting();
                        s.AddProblemDetails();
                    })
                    .Configure(web =>
                    {
                        web.UseRouting();
                        web.UseAuthentication();
                        web.UseAuthorization();
                        web.UseAppKitRateLimiting();
                        web.UseAppKitBadRequests();
                        web.UseEndpoints(e => e.MapAppKitSiteSettings<ApplicationDbContext, AppSettings>("/api", SiteSettingsHooks.Configure));
                    }))
                .Build();
            await app._host.StartAsync();
            app._client = app._host.GetTestClient();
            return app;
        }

        public Task<HttpResponseMessage> PutSettingsAsync(byte[] body, string? roles = "Admin")
        {
            var request = new HttpRequestMessage(HttpMethod.Put, "/api/settings")
            {
                Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } }
            };
            if (roles != null) request.Headers.Add("X-Test-Roles", roles);
            return _client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
