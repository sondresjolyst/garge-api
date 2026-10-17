using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using garge_api.Constants;
using garge_api.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SjolystInnovation.AppKit.SiteSettings;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// The legal pages from the site settings package: seeded with the text garge-app showed before, in
/// English only, open to read and editable by admins.
/// </summary>
public class LegalPagesTests
{
    [Fact]
    public void Seed_HasTheThreePagesInEnglish()
    {
        Assert.Equal(
            [("terms", "en", "Terms of Service"), ("privacy", "en", "Privacy Policy"), ("cookies", "en", "Cookie Policy")],
            LegalSeedText.All.Select(p => (p.Key, p.Locale, p.Title)));
    }

    [Theory]
    [InlineData("terms", 19)]
    [InlineData("privacy", 7)]
    [InlineData("cookies", 5)]
    public void Seed_KeepsEverySection(string key, int sections)
    {
        var body = LegalSeedText.All.Single(p => p.Key == key).BodyMarkdown;

        Assert.Equal(sections, body.Split('\n').Count(l => l.StartsWith("## ")));
    }

    [Fact]
    public void Seed_IsPlainMarkdown()
    {
        foreach (var page in LegalSeedText.All)
        {
            // No JSX, HTML entities or interpolation left over from the React pages.
            Assert.DoesNotContain("<", page.BodyMarkdown);
            Assert.DoesNotContain("&amp;", page.BodyMarkdown);
            Assert.DoesNotContain("&apos;", page.BodyMarkdown);
            Assert.DoesNotContain("{", page.BodyMarkdown);
        }
    }

    [Fact]
    public void Seed_WritesTheCompanyDetails()
    {
        var terms = LegalSeedText.All.Single(p => p.Key == "terms").BodyMarkdown;

        Assert.Contains("**Company name:** Sjølyst Innovation AS", terms);
        Assert.Contains("**Organisation number:** 938 517 789", terms);
        Assert.Contains("**Address:** Mårvegen 21a, 4347 Lye", terms);
        Assert.Contains("**Email:** sondresjoelyst@gmail.com", terms);
        Assert.Contains("[sondresjoelyst@gmail.com](mailto:sondresjoelyst@gmail.com)", LegalSeedText.All.Single(p => p.Key == "privacy").BodyMarkdown);
    }

    [Fact]
    public async Task Seeding_KeepsAPageAnAdminEdited()
    {
        await using var app = await LegalApp.StartAsync();
        await app.SeedAsync();

        var saved = await app.SendAsync(HttpMethod.Put, "/api/content/legal/terms/en", JsonContent.Create(new LegalPageBody("Terms", "Edited.")));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        await app.SeedAsync();

        var page = await app.GetAsync("terms");
        Assert.Equal("Terms", page.GetProperty("title").GetString());
        Assert.Equal("Edited.", page.GetProperty("bodyMarkdown").GetString());
    }

    [Fact]
    public async Task Get_IsOpenAndServesTheSeededText()
    {
        await using var app = await LegalApp.StartAsync();
        await app.SeedAsync();

        var page = await app.GetAsync("cookies");

        Assert.Equal("Cookie Policy", page.GetProperty("title").GetString());
        Assert.Equal("en", page.GetProperty("locale").GetString());
        Assert.Contains("`next-auth.session-token`", page.GetProperty("bodyMarkdown").GetString());
    }

    [Fact]
    public async Task Get_AnotherLanguage_FallsBackToEnglish()
    {
        await using var app = await LegalApp.StartAsync();
        await app.SeedAsync();

        var page = await app.GetAsync("privacy", "?locale=nb-NO");

        Assert.Equal("en", page.GetProperty("locale").GetString());
        Assert.Equal("Privacy Policy", page.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Put_NeedsTheAdminPolicy()
    {
        await using var app = await LegalApp.StartAsync();

        var response = await app.SendAsync(HttpMethod.Put, "/api/content/legal/terms/en", JsonContent.Create(new LegalPageBody("T", "B")), roles: "User");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Put_Norwegian_IsRefused()
    {
        await using var app = await LegalApp.StartAsync();

        var response = await app.SendAsync(HttpMethod.Put, "/api/content/legal/terms/no", JsonContent.Create(new LegalPageBody("Vilkår", "Tekst.")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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

    /// <summary>A test server with the legal page endpoints mapped and seeded the way Program.cs does it.</summary>
    private sealed class LegalApp : IAsyncDisposable
    {
        private readonly string _database = Guid.NewGuid().ToString();
        private IHost _host = default!;
        private HttpClient _client = default!;

        public static async Task<LegalApp> StartAsync()
        {
            var app = new LegalApp();
            app._host = new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseTestServer()
                    .ConfigureServices(s =>
                    {
                        s.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(app._database));
                        s.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                        s.AddAuthorization(o => o.AddPolicy(RoleNames.Admin, p => p.RequireRole(RoleNames.Admin)));
                        s.AddRouting();
                    })
                    .Configure(web =>
                    {
                        web.UseRouting();
                        web.UseAuthentication();
                        web.UseAuthorization();
                        web.UseEndpoints(e => e.MapAppKitLegalPages<ApplicationDbContext>(configure: LegalSeedText.Configure));
                    }))
                .Build();
            await app._host.StartAsync();
            app._client = app._host.GetTestClient();
            return app;
        }

        public Task SeedAsync() => _host.Services.SeedAppKitLegalPagesAsync<ApplicationDbContext>(LegalSeedText.All);

        public Task<JsonElement> GetAsync(string key, string query = "") =>
            _client.GetFromJsonAsync<JsonElement>($"/api/content/legal/{key}{query}");

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null, string roles = "Admin")
        {
            var request = new HttpRequestMessage(method, path) { Content = content };
            request.Headers.Add("X-Test-Roles", roles);
            return _client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
