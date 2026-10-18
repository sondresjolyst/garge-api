using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using garge_api.Authorization;
using garge_api.Controllers;
using garge_api.Hubs;
using garge_api.Models;
using garge_api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SjolystInnovation.AppKit.Auth;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// The device hub and the ticket endpoint with the authentication Program.cs uses: a ticket in the query
/// string or the header opens the hub, a session token opens it only from the header.
/// </summary>
public class DeviceHubAuthenticationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TicketEndpoint_NeedsASignedInUser()
    {
        await using var app = await HubApp.StartAsync();

        var response = await app.Client.PostAsync("/api/hub-tickets", null, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TicketEndpoint_IssuesAnOpaqueTicket()
    {
        await using var app = await HubApp.StartAsync();

        var response = await app.SendAsync(HttpMethod.Post, "/api/hub-tickets", app.OlaJwt);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        var ticket = json.GetProperty("ticket").GetString()!;
        Assert.DoesNotContain('.', ticket);
        Assert.DoesNotContain("eyJ", ticket);
        Assert.Equal(60, json.GetProperty("expiresIn").GetInt32());
    }

    [Fact]
    public async Task Ticket_DoesNotWorkOutsideTheHub()
    {
        await using var app = await HubApp.StartAsync();
        var ticket = await app.TicketAsync(app.OlaJwt);

        var response = await app.SendAsync(HttpMethod.Post, "/api/hub-tickets", ticket);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ticket_ConnectsAsItsUser_AndJoinsTheirGroup()
    {
        await using var app = await HubApp.StartAsync();
        var ticket = await app.TicketAsync(app.OlaJwt);
        await using var connection = app.Connect(() => ticket);
        var received = new List<string>();
        var ownEvent = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string>("switch", value =>
        {
            lock (received) received.Add(value);
            if (value == "ola") ownEvent.TrySetResult(value);
        });

        await connection.StartAsync(Ct);
        await app.WaitForTrackedAsync(app.OlaId);
        await app.Hub.Clients.Group(DeviceHub.UserGroup(app.KariId)).SendAsync("switch", "kari", Ct);
        await app.Hub.Clients.Group(DeviceHub.UserGroup(app.OlaId)).SendAsync("switch", "ola", Ct);

        Assert.Equal("ola", await ownEvent.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        lock (received) Assert.Equal(["ola"], received);
        Assert.Single(app.Tracker.GetConnectionIds(app.OlaId));
        Assert.Empty(app.Tracker.GetConnectionIds(app.KariId));
    }

    [Fact]
    public async Task Ticket_InTheQueryString_OpensTheTransport()
    {
        await using var app = await HubApp.StartAsync();
        var ticket = await app.TicketAsync(app.OlaJwt);
        var id = await app.NegotiateAsync(ticket);

        var response = await app.SendAsync(HttpMethod.Get, $"/hubs/devices?id={id}&access_token={ticket}", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ticket_OpensOnlyOneConnection()
    {
        await using var app = await HubApp.StartAsync();
        var ticket = await app.TicketAsync(app.OlaJwt);
        var first = await app.NegotiateAsync(ticket);
        var second = await app.NegotiateAsync(ticket);

        var opened = await app.SendAsync(HttpMethod.Get, $"/hubs/devices?id={first}&access_token={ticket}", null);
        var reused = await app.SendAsync(HttpMethod.Get, $"/hubs/devices?id={second}&access_token={ticket}", null);

        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
    }

    [Theory]
    [InlineData("not-a-ticket")]
    [InlineData("")]
    public async Task WrongTicket_IsRefused(string ticket)
    {
        await using var app = await HubApp.StartAsync();
        await app.TicketAsync(app.OlaJwt);
        var id = await app.NegotiateAsync(app.OlaJwt);

        var inQuery = await app.SendAsync(HttpMethod.Get, $"/hubs/devices?id={id}&access_token={ticket}", null);
        var inHeader = await app.SendAsync(HttpMethod.Post, "/hubs/devices/negotiate?negotiateVersion=1", ticket == "" ? "x" : ticket);

        Assert.Equal(HttpStatusCode.Unauthorized, inQuery.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, inHeader.StatusCode);
    }

    [Fact]
    public async Task ExpiredTicket_IsRefused()
    {
        await using var app = await HubApp.StartAsync();
        var ticket = await app.TicketAsync(app.OlaJwt);
        app.Clock.Advance(TimeSpan.FromSeconds(61));

        var response = await app.SendAsync(HttpMethod.Post, $"/hubs/devices/negotiate?negotiateVersion=1&access_token={ticket}", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ticket_ForADeletedUser_IsRefused()
    {
        await using var app = await HubApp.StartAsync();
        var ticket = app.Tickets.Issue(HubApp.PrincipalFor(app.KariId));
        await app.DeleteUserAsync(app.KariId);

        var response = await app.SendAsync(HttpMethod.Post, "/hubs/devices/negotiate?negotiateVersion=1", ticket);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ticket_UsesTheUsersCurrentRoles()
    {
        await using var app = await HubApp.StartAsync();
        // The ticket claims the bridge role, which kari does not have.
        var ticket = app.Tickets.Issue(HubApp.PrincipalFor(app.KariId, DeviceHub.BridgeRole));
        await using var connection = app.Connect(() => ticket);
        var received = new List<string>();
        var ownEvent = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string>("deviceSettings", value => { lock (received) received.Add(value); });
        connection.On<string>("switch", value => ownEvent.TrySetResult(value));

        await connection.StartAsync(Ct);
        await app.WaitForTrackedAsync(app.KariId);
        await app.Hub.Clients.Group(DeviceHub.BridgeGroup).SendAsync("deviceSettings", "bridged", Ct);
        await app.Hub.Clients.Group(DeviceHub.UserGroup(app.KariId)).SendAsync("switch", "kari", Ct);

        Assert.Equal("kari", await ownEvent.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        lock (received) Assert.Empty(received);
    }

    [Fact]
    public async Task SessionToken_InTheQueryString_IsRefused()
    {
        await using var app = await HubApp.StartAsync();
        var id = await app.NegotiateAsync(app.OlaJwt);

        var negotiate = await app.SendAsync(HttpMethod.Post, $"/hubs/devices/negotiate?negotiateVersion=1&access_token={app.OlaJwt}", null);
        var transport = await app.SendAsync(HttpMethod.Get, $"/hubs/devices?id={id}&access_token={app.OlaJwt}", null);

        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, transport.StatusCode);
    }

    [Fact]
    public async Task SessionToken_InTheHeader_StillConnects_LikeTheOperator()
    {
        await using var app = await HubApp.StartAsync();
        await using var connection = app.Connect(() => app.BridgeJwt);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string>("deviceSettings", value => received.TrySetResult(value));

        await connection.StartAsync(Ct);
        // The bridge joins its group in OnConnectedAsync, which can finish just after the handshake.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!received.Task.IsCompleted && DateTime.UtcNow < deadline)
        {
            await app.Hub.Clients.Group(DeviceHub.BridgeGroup).SendAsync("deviceSettings", "bridged", Ct);
            await Task.WhenAny(received.Task, Task.Delay(100, Ct));
        }

        Assert.Equal("bridged", await received.Task.WaitAsync(TimeSpan.FromSeconds(1), Ct));
    }

    [Fact]
    public async Task NoToken_IsRefused()
    {
        await using var app = await HubApp.StartAsync();

        var response = await app.SendAsync(HttpMethod.Post, "/hubs/devices/negotiate?negotiateVersion=1", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>A clock the test moves by hand, starting at the real time so session tokens stay valid.</summary>
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private sealed class OnlyTicketController : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) => typeInfo.AsType() == typeof(HubTicketController);
    }

    private sealed class HubApp : IAsyncDisposable
    {
        private IHost _host = default!;
        private TestServer _server = default!;

        public HttpClient Client { get; private set; } = default!;
        public TestClock Clock { get; } = new();
        public IHubContext<DeviceHub> Hub => _host.Services.GetRequiredService<IHubContext<DeviceHub>>();
        public IHubConnectionTracker Tracker => _host.Services.GetRequiredService<IHubConnectionTracker>();
        public string OlaId { get; private set; } = "";
        public string KariId { get; private set; } = "";
        public string OlaJwt { get; private set; } = "";
        public string BridgeJwt { get; private set; } = "";

        public static async Task<HubApp> StartAsync()
        {
            var app = new HubApp();
            var database = Guid.NewGuid().ToString();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "a-test-signing-key-that-is-long-enough-for-hs256",
                ["Jwt:Issuer"] = "garge-test",
            }).Build();

            app._host = new HostBuilder()
                .UseEnvironment(Environments.Development)
                .ConfigureWebHost(web => web
                    .UseTestServer()
                    .ConfigureServices(s =>
                    {
                        s.AddSingleton<TimeProvider>(app.Clock);
                        s.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(database));
                        s.AddAppKitIdentity<User, ApplicationDbContext>();
                        s.AddAppKitAuth<ApplicationDbContext>(config.GetSection("Jwt"));
                        // The refresh token cleanup needs Postgres, and nothing here uses refresh tokens.
                        s.RemoveAll<IHostedService>();
                        s.AddGargeJwtBearer();
                        s.AddAuthorization();
                        s.AddCors(o => o.AddPolicy("AllowAllOrigins", p => p.AllowAnyOrigin()));
                        s.AddSignalR();
                        s.AddSingleton<IHubConnectionTracker, HubConnectionTracker>();
                        s.AddControllers().ConfigureApplicationPartManager(m =>
                        {
                            m.ApplicationParts.Clear();
                            m.ApplicationParts.Add(new AssemblyPart(typeof(HubTicketController).Assembly));
                            foreach (var provider in m.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                                m.FeatureProviders.Remove(provider);
                            m.FeatureProviders.Add(new OnlyTicketController());
                        });
                    })
                    .Configure(web =>
                    {
                        web.UseRouting();
                        web.UseCors("AllowAllOrigins");
                        web.UseAuthentication();
                        web.UseAuthorization();
                        web.UseEndpoints(e =>
                        {
                            e.MapControllers();
                            e.MapHub<DeviceHub>(GargeAuthentication.DeviceHubPath);
                        });
                    }))
                .Build();
            await app._host.StartAsync();
            app._server = app._host.GetTestServer();
            app.Client = app._server.CreateClient();
            await app.SeedAsync();
            return app;
        }

        private async Task SeedAsync()
        {
            using var scope = _host.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            var tokens = scope.ServiceProvider.GetRequiredService<IJwtTokenService>();
            await roles.CreateAsync(new IdentityRole(DeviceHub.BridgeRole));

            async Task<User> Add(string name, params string[] userRoles)
            {
                var user = new User { UserName = name, Email = $"{name}@example.no", FirstName = name, LastName = "Test" };
                Assert.True((await users.CreateAsync(user)).Succeeded);
                foreach (var role in userRoles) await users.AddToRoleAsync(user, role);
                return user;
            }

            var ola = await Add("ola");
            var kari = await Add("kari");
            var bridge = await Add("operator", DeviceHub.BridgeRole);
            OlaId = ola.Id;
            KariId = kari.Id;
            OlaJwt = tokens.Build(ola, []);
            BridgeJwt = tokens.Build(bridge, [DeviceHub.BridgeRole]);
        }

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? bearer)
        {
            var request = new HttpRequestMessage(method, path);
            if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            return Client.SendAsync(request, Ct);
        }

        public IHubTicketService Tickets => _host.Services.GetRequiredService<IHubTicketService>();

        /// <summary>A signed-in principal for a user who has not called the API yet, so no user check is cached.</summary>
        public static ClaimsPrincipal PrincipalFor(string userId, params string[] roles) =>
            new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId), .. roles.Select(r => new Claim(ClaimTypes.Role, r))], "Test"));

        public async Task DeleteUserAsync(string userId)
        {
            using var scope = _host.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = await users.FindByIdAsync(userId);
            user!.IsDeleted = true;
            Assert.True((await users.UpdateAsync(user)).Succeeded);
        }

        public async Task<string> TicketAsync(string jwt)
        {
            var response = await SendAsync(HttpMethod.Post, "/api/hub-tickets", jwt);
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("ticket").GetString()!;
        }

        public async Task<string> NegotiateAsync(string bearer)
        {
            var response = await SendAsync(HttpMethod.Post, "/hubs/devices/negotiate?negotiateVersion=1", bearer);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("connectionToken").GetString()!;
        }

        /// <summary>A SignalR client over long polling, which sends the token in the header like the operator does.</summary>
        public HubConnection Connect(Func<string> token) => new HubConnectionBuilder()
            .WithUrl(new Uri(_server.BaseAddress, GargeAuthentication.DeviceHubPath), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => _server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult<string?>(token());
            })
            .Build();

        public async Task WaitForTrackedAsync(string userId)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Tracker.GetConnectionIds(userId).Count == 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "The hub did not track the connection in time.");
                await Task.Delay(20, Ct);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
