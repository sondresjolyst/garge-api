using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SjolystInnovation.AppKit.Hosting;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// An unhandled exception answers 500 with a problem details body and no exception details, through
/// <c>AddProblemDetails</c> and <c>UseExceptionHandler</c> in the order Program.cs uses. Responses the
/// endpoints write themselves are left alone.
/// </summary>
public class ExceptionHandlerTests
{
    private const string Secret = "Connection to db-secret.internal refused";

    public static TheoryData<string> EnvironmentNames() => new() { Environments.Production, Environments.Development };

    [Theory]
    [MemberData(nameof(EnvironmentNames))]
    public async Task UnhandledException_Answers500ProblemWithoutDetails(string environment)
    {
        await using var app = await ExceptionApp.StartAsync(environment);

        var response = await app.Client.GetAsync("/throws", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal(500, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("title").GetString()));
        Assert.False(problem.TryGetProperty("detail", out _));
        Assert.DoesNotContain("db-secret", body);
        Assert.DoesNotContain(nameof(InvalidOperationException), body);
        Assert.DoesNotContain(nameof(ExceptionHandlerTests), body);
    }

    [Theory]
    [MemberData(nameof(EnvironmentNames))]
    public async Task SignedOut_StillAnswers401WithoutABody(string environment)
    {
        await using var app = await ExceptionApp.StartAsync(environment);

        var response = await app.Client.GetAsync("/admin/throws", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [MemberData(nameof(EnvironmentNames))]
    public async Task OwnErrorResponses_AreLeftAlone(string environment)
    {
        await using var app = await ExceptionApp.StartAsync(environment);

        var own = await app.Client.GetAsync("/own", TestContext.Current.CancellationToken);
        var empty = await app.Client.GetAsync("/own-empty", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, own.StatusCode);
        Assert.Equal("application/json", own.Content.Headers.ContentType?.MediaType);
        Assert.Equal("{\"message\":\"Not this one.\"}", await own.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, empty.StatusCode);
        Assert.Equal("", await empty.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Signs a request in with the roles in <c>X-Test-Roles</c>.</summary>
    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Roles", out var roles))
                return Task.FromResult(AuthenticateResult.NoResult());
            var claims = roles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => new Claim(ClaimTypes.Role, r));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), "Test")));
        }
    }

    /// <summary>A test server with the middleware in the order Program.cs uses and endpoints that only tests map.</summary>
    private sealed class ExceptionApp : IAsyncDisposable
    {
        private IHost _host = default!;

        public HttpClient Client { get; private set; } = default!;

        public static async Task<ExceptionApp> StartAsync(string environment)
        {
            var app = new ExceptionApp();
            app._host = new HostBuilder()
                .UseEnvironment(environment)
                .ConfigureWebHost(web => web
                    .UseTestServer()
                    .ConfigureServices(s =>
                    {
                        s.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                        s.AddAuthorization(o => o.AddPolicy("Admin", p => p.RequireRole("Admin")));
                        s.AddAppKitRateLimiting(new ConfigurationBuilder().Build().GetSection("RateLimiting"));
                        s.AddRouting();
                        s.AddProblemDetails();
                    })
                    .Configure(web =>
                    {
                        web.UseExceptionHandler();
                        web.UseRouting();
                        web.UseAuthentication();
                        web.UseAuthorization();
                        web.UseAppKitRateLimiting();
                        web.UseAppKitBadRequests();
                        web.UseEndpoints(e =>
                        {
                            e.MapGet("/throws", IResult () => throw new InvalidOperationException(Secret));
                            e.MapGet("/admin/throws", IResult () => throw new InvalidOperationException(Secret)).RequireAuthorization("Admin");
                            e.MapGet("/own", () => Results.BadRequest(new { message = "Not this one." }));
                            e.MapGet("/own-empty", () => Results.NotFound());
                        });
                    }))
                .Build();
            await app._host.StartAsync();
            app.Client = app._host.GetTestClient();
            return app;
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
