using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using garge_api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SjolystInnovation.AppKit.Email;
using Xunit;

namespace garge_api.Tests;

/// <summary>The admin email stats from AppKit.Email, on the route and policy garge-app uses.</summary>
public class EmailStatsEndpointTests
{
    [Fact]
    public async Task Admin_GetsTheStatsWithTheSameKeys()
    {
        await using var app = await StatsApp.StartAsync();

        var response = await app.GetAsync("/api/admin/email-stats?days=7", "Admin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal(7, json.GetProperty("days").GetInt32());
        Assert.Equal(42, json.GetProperty("requests").GetInt32());
        Assert.Equal(40, json.GetProperty("delivered").GetInt32());
        Assert.Equal(1, json.GetProperty("hardBounces").GetInt32());
        Assert.Equal(2, json.GetProperty("softBounces").GetInt32());
        Assert.Equal(3, json.GetProperty("spamReports").GetInt32());
        Assert.Equal(4, json.GetProperty("blocked").GetInt32());
        Assert.Equal(5, json.GetProperty("invalid").GetInt32());
        Assert.Equal([7], app.Stats.Days);
    }

    [Fact]
    public async Task WithoutDays_AsksForThirty()
    {
        await using var app = await StatsApp.StartAsync();

        var response = await app.GetAsync("/api/admin/email-stats", "Admin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([30], app.Stats.Days);
    }

    [Theory]
    [InlineData("SensorAdmin")]
    [InlineData("Default")]
    public async Task NonAdmin_IsForbidden(string role)
    {
        await using var app = await StatsApp.StartAsync();

        var response = await app.GetAsync("/api/admin/email-stats", role);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(app.Stats.Days);
    }

    [Fact]
    public async Task Anonymous_IsUnauthorized()
    {
        await using var app = await StatsApp.StartAsync();

        var response = await app.GetAsync("/api/admin/email-stats", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task BrevoDown_Answers502()
    {
        await using var app = await StatsApp.StartAsync();
        app.Stats.Fail = true;

        var response = await app.GetAsync("/api/admin/email-stats", "Admin");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    private sealed class FakeStats : IEmailStats
    {
        public List<int> Days { get; } = [];
        public bool Fail { get; set; }

        public Task<EmailStats> GetAsync(int days = 30, CancellationToken cancellationToken = default)
        {
            Days.Add(days);
            if (Fail) throw new HttpRequestException("Brevo is down");
            return Task.FromResult(new EmailStats(days, 42, 40, 1, 2, 3, 4, 5));
        }
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

    /// <summary>A test server with the endpoint mapped the way Program.cs maps it.</summary>
    private sealed class StatsApp : IAsyncDisposable
    {
        private IHost _host = default!;
        private HttpClient _client = default!;
        public FakeStats Stats { get; } = new();

        public static async Task<StatsApp> StartAsync()
        {
            var app = new StatsApp();
            app._host = new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseTestServer()
                    .ConfigureServices(s =>
                    {
                        s.AddSingleton<IEmailStats>(app.Stats);
                        s.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                        s.AddAuthorization(o =>
                        {
                            o.AddPolicy("Admin", p => p.RequireRole("Admin"));
                            o.AddPolicy("SensorAdmin", p => p.RequireRole("SensorAdmin"));
                        });
                        s.AddRouting();
                        s.AddProblemDetails();
                    })
                    .Configure(web =>
                    {
                        web.UseRouting();
                        web.UseAuthentication();
                        web.UseAuthorization();
                        web.UseEndpoints(e => e.MapAppKitEmailStats(EmailStatsEndpoint.Path, EmailStatsEndpoint.Configure));
                    }))
                .Build();
            await app._host.StartAsync();
            app._client = app._host.GetTestClient();
            return app;
        }

        public Task<HttpResponseMessage> GetAsync(string path, string? roles)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
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
