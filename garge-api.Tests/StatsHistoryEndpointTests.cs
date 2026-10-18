using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SjolystInnovation.AppKit.AdminStats;
using Xunit;

namespace garge_api.Tests;

/// <summary>The admin stats history from AppKit.AdminStats, on the route, policy and keys garge-app uses.</summary>
public class StatsHistoryEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Admin_GetsEveryDayWithGargesKeys_AndDeletionsSubtractOnTheirDay()
    {
        // Three users sign up on d1 and one deletes on d2. The running total climbs to 3, then dips to 2.
        var today = DateTime.UtcNow.Date;
        var d1 = today.AddDays(-3);
        var d2 = today.AddDays(-2);
        await using var app = await HistoryApp.StartAsync(db => db.Users.AddRange(
            MakeUser("u1", d1), MakeUser("u2", d1), MakeUser("u3", d1, deletedAt: d2)));

        var response = await app.GetAsync("/api/admin/stats/history", "Admin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.EnumerateArray().ToList();
        Assert.All(rows, r => Assert.Equal(
            ["date", "totalUsers", "totalSensors", "totalSwitches", "totalAutomations"],
            r.EnumerateObject().Select(p => p.Name).ToArray()));
        Assert.Equal(3, TotalUsersOn(rows, d1));
        Assert.Equal(2, TotalUsersOn(rows, d2));
        Assert.Equal(2, TotalUsersOn(rows, today));
        Assert.Equal(today.ToString("yyyy-MM-dd"), rows[^1].GetProperty("date").GetString());
    }

    [Fact]
    public async Task From_StartsTheHistoryOnThatDay()
    {
        var today = DateTime.UtcNow.Date;
        await using var app = await HistoryApp.StartAsync(db => db.Users.Add(MakeUser("u1", today.AddDays(-5))));

        var response = await app.GetAsync($"/api/admin/stats/history?from={today.AddDays(-1):yyyy-MM-dd}", "Admin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dates = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement
            .EnumerateArray().Select(r => r.GetProperty("date").GetString()).ToArray();
        Assert.Equal([today.AddDays(-1).ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd")], dates);
    }

    [Theory]
    [InlineData("SensorAdmin")]
    [InlineData("Default")]
    public async Task NonAdmin_IsForbidden(string role)
    {
        await using var app = await HistoryApp.StartAsync(_ => { });

        var response = await app.GetAsync("/api/admin/stats/history", role);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_IsUnauthorized()
    {
        await using var app = await HistoryApp.StartAsync(_ => { });

        var response = await app.GetAsync("/api/admin/stats/history", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static User MakeUser(string id, DateTime createdAt, DateTime? deletedAt = null) => new()
    {
        Id = id, UserName = id, Email = $"{id}@test.com", FirstName = "Test", LastName = "User",
        CreatedAt = createdAt, IsDeleted = deletedAt != null, DeletedAt = deletedAt,
    };

    private static int TotalUsersOn(List<JsonElement> rows, DateTime date) =>
        rows.Single(r => r.GetProperty("date").GetString() == date.ToString("yyyy-MM-dd")).GetProperty("totalUsers").GetInt32();

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

    /// <summary>A test server with garge's samplers and the endpoint mapped the way Program.cs maps it.</summary>
    private sealed class HistoryApp : IAsyncDisposable
    {
        private IHost _host = default!;
        private HttpClient _client = default!;

        public static async Task<HistoryApp> StartAsync(Action<ApplicationDbContext> seed)
        {
            var dbName = Guid.NewGuid().ToString();
            var options = new AppKitDailyStatsOptions<ApplicationDbContext, DailyStatSnapshot>();
            DailyStatsSamplers.Configure(options);
            var stats = new AppKitDailyStats<ApplicationDbContext, DailyStatSnapshot>(Options.Create(options), TimeProvider.System);

            var app = new HistoryApp();
            app._host = new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseTestServer()
                    .ConfigureServices(s =>
                    {
                        s.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(dbName));
                        s.AddSingleton(stats);
                        s.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                        s.AddAuthorization(o =>
                        {
                            o.AddPolicy("Admin", p => p.RequireRole("Admin"));
                            o.AddPolicy("SensorAdmin", p => p.RequireRole("SensorAdmin"));
                        });
                        s.AddRouting();
                    })
                    .Configure(web =>
                    {
                        web.UseRouting();
                        web.UseAuthentication();
                        web.UseAuthorization();
                        web.UseEndpoints(e => e.MapAppKitDailyStats<ApplicationDbContext, DailyStatSnapshot>(
                            StatsHistoryEndpoint.Prefix, StatsHistoryEndpoint.Configure));
                    }))
                .Build();
            await app._host.StartAsync(Ct);

            using (var scope = app._host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                seed(db);
                await db.SaveChangesAsync(Ct);
                await stats.FreezeMissingDaysAsync(db, Ct);
            }

            app._client = app._host.GetTestClient();
            return app;
        }

        public Task<HttpResponseMessage> GetAsync(string path, string? roles)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (roles != null) request.Headers.Add("X-Test-Roles", roles);
            return _client.SendAsync(request, Ct);
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
