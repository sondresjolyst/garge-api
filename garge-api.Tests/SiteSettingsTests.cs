using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Web;
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
using Moq;
using SjolystInnovation.AppKit.SiteSettings;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// The company settings served by the site settings package, with garge's hooks: a save keeps the VAT
/// registration date in step like <c>PUT /api/admin/settings</c> does, and clears the settings cache.
/// </summary>
public class SiteSettingsTests
{
    private const string Address = "Mårvegen 21a, 4347 Lye";

    [Fact]
    public void Defaults_FormatToTheOneLineAddressGargeShowedBefore()
    {
        var s = new AppSettings();

        Assert.Equal("Mårvegen 21a", s.StreetAddress);
        Assert.Equal("4347", s.PostalCode);
        Assert.Equal("Lye", s.AddressLocality);
        Assert.Equal(Address, s.FormattedAddress);
        Assert.Equal(s.PublicEmail, s.ContactRecipientEmail);
        Assert.Equal("", s.PublicPhone);
        Assert.True(NorwegianOrgNumber.IsValid(s.OrgNumber));
    }

    [Fact]
    public void VatRegistered_IsVatEnabled()
    {
        var s = new AppSettings();
        ICompanySettings company = s;

        company.VatRegistered = true;
        Assert.True(s.VatEnabled);

        s.VatEnabled = false;
        Assert.False(company.VatRegistered);
    }

    [Fact]
    public void EmailLayout_ShowsTheAddressAsOneLine()
    {
        var html = EmailLayout.Render(new AppSettings(), null, "<p>Hi</p>");

        Assert.Contains(HttpUtility.HtmlEncode(Address), html);
        Assert.Contains("sondresjoelyst@gmail.com", html);
    }

    [Fact]
    public async Task AuthEmailBrand_ShowsTheAddressAsOneLine()
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.AppSettings.Add(new AppSettings { PublicEmail = "post@garge.no" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var brand = await new AuthEmailBrand(db).GetBrandAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Address, brand.Address);
        Assert.Equal("post@garge.no", brand.Email);
        Assert.Equal("938 517 789", brand.OrgNumber);
        Assert.False(brand.VatRegistered);
    }

    [Fact]
    public async Task AuthEmailBrand_PassesTheRawNumberAndTheVatFlag()
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.AppSettings.Add(new AppSettings { CompanyName = "Garge", CompanyLegalName = "Garge AS", OrgNumber = "999888777", VatEnabled = true });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var brand = await new AuthEmailBrand(db).GetBrandAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Garge", brand.Name);
        Assert.Equal("Garge AS", brand.LegalName);
        Assert.Equal("999888777", brand.OrgNumber);
        Assert.True(brand.VatRegistered);
    }

    [Fact]
    public async Task PutSettings_TurningVatOn_StartsTheRegistrationNow()
    {
        await using var app = await HookApp.StartAsync();
        await app.SeedAsync(new AppSettings());
        var before = DateTime.UtcNow;

        var response = await app.PutSettingsAsync(Body(vatRegistered: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = await app.RowAsync();
        Assert.True(row.VatEnabled);
        Assert.NotNull(row.VatRegisteredFrom);
        Assert.InRange(row.VatRegisteredFrom!.Value, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task PutSettings_VatStaysOn_KeepsTheRegistrationDate()
    {
        var registered = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        await using var app = await HookApp.StartAsync();
        await app.SeedAsync(new AppSettings { VatEnabled = true, VatRegisteredFrom = registered });

        var response = await app.PutSettingsAsync(Body(vatRegistered: true) with { CompanyName = "Garge AS" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = await app.RowAsync();
        Assert.Equal("Garge AS", row.CompanyName);
        Assert.Equal(registered, row.VatRegisteredFrom);
    }

    [Fact]
    public async Task PutSettings_VatStaysOff_LeavesNoRegistrationDate()
    {
        await using var app = await HookApp.StartAsync();
        await app.SeedAsync(new AppSettings());

        await app.PutSettingsAsync(Body(vatRegistered: false));

        var row = await app.RowAsync();
        Assert.False(row.VatEnabled);
        Assert.Null(row.VatRegisteredFrom);
    }

    [Fact]
    public async Task PutSettings_TurningVatOff_ClearsTheRegistrationDate()
    {
        await using var app = await HookApp.StartAsync();
        await app.SeedAsync(new AppSettings { VatEnabled = true, VatRegisteredFrom = DateTime.UtcNow.AddDays(-3) });

        await app.PutSettingsAsync(Body(vatRegistered: false));

        var row = await app.RowAsync();
        Assert.False(row.VatEnabled);
        Assert.Null(row.VatRegisteredFrom);
    }

    [Fact]
    public async Task PutSettings_ClearsTheSettingsCache()
    {
        await using var app = await HookApp.StartAsync();
        await app.SeedAsync(new AppSettings());

        await app.PutSettingsAsync(Body(vatRegistered: false));

        app.Cache.Verify(c => c.Invalidate(), Times.Once);
    }

    [Fact]
    public async Task PutBranding_ClearsTheSettingsCache()
    {
        await using var app = await HookApp.StartAsync();
        await app.SeedAsync(new AppSettings());

        var form = new MultipartFormDataContent { { new StringContent("true"), "removeLogo" } };
        var response = await app.SendAsync(HttpMethod.Put, "/api/branding", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        app.Cache.Verify(c => c.Invalidate(), Times.Once);
    }

    [Fact]
    public async Task PutSettings_Refused_SavesNothingAndKeepsTheCache()
    {
        await using var app = await HookApp.StartAsync();
        await app.SeedAsync(new AppSettings());

        // The package refuses an organization number that fails the checksum.
        var response = await app.PutSettingsAsync(Body(vatRegistered: true) with { OrgNumber = "938 517 788" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("OrgNumber", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var row = await app.RowAsync();
        Assert.False(row.VatEnabled);
        Assert.Null(row.VatRegisteredFrom);
        app.Cache.Verify(c => c.Invalidate(), Times.Never);
    }

    [Fact]
    public async Task GetCompany_ShowsTheSeededDetails()
    {
        await using var app = await HookApp.StartAsync();
        await app.SeedAsync(new AppSettings());

        var company = await app.Client.GetFromJsonAsync<JsonElement>("/api/company", TestContext.Current.CancellationToken);

        Assert.Equal(Address, company.GetProperty("address").GetString());
        Assert.Equal("Sjølyst Innovation AS", company.GetProperty("legalName").GetString());
        Assert.Equal("938 517 789", company.GetProperty("orgNumber").GetString());
        Assert.False(company.TryGetProperty("contactRecipientEmail", out _));
    }

    [Fact]
    public async Task PutSettings_NeedsTheAdminPolicy()
    {
        await using var app = await HookApp.StartAsync();

        var response = await app.SendAsync(HttpMethod.Put, "/api/settings", JsonContent.Create(Body(vatRegistered: false)), roles: "User");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static SiteSettingsBody Body(bool vatRegistered) => new(
        ContactRecipientEmail: "sondresjoelyst@gmail.com",
        CompanyName: "Garge",
        CompanyLegalName: "Sjølyst Innovation AS",
        OrgNumber: "938 517 789",
        VatRegistered: vatRegistered,
        StreetAddress: "Mårvegen 21a",
        PostalCode: "4347",
        AddressLocality: "Lye",
        AddressRegion: "",
        PublicEmail: "sondresjoelyst@gmail.com",
        PublicPhone: "+47 400 00 000");

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

    /// <summary>A test server with the package's endpoints mapped the way Program.cs maps them.</summary>
    private sealed class HookApp : IAsyncDisposable
    {
        private readonly string _database = Guid.NewGuid().ToString();
        private IHost _host = default!;
        public HttpClient Client { get; private set; } = default!;
        public Mock<IAppSettingsCache> Cache { get; } = new();

        public static async Task<HookApp> StartAsync()
        {
            var app = new HookApp();
            app._host = new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseTestServer()
                    .ConfigureServices(s =>
                    {
                        s.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(app._database));
                        s.AddSingleton(app.Cache.Object);
                        s.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                        s.AddAuthorization(o => o.AddPolicy("Admin", p => p.RequireRole("Admin")));
                        s.AddRouting();
                    })
                    .Configure(web =>
                    {
                        web.UseRouting();
                        web.UseAuthentication();
                        web.UseAuthorization();
                        web.UseEndpoints(e => e.MapAppKitSiteSettings<ApplicationDbContext, AppSettings>("/api", SiteSettingsHooks.Configure));
                    }))
                .Build();
            await app._host.StartAsync();
            app.Client = app._host.GetTestClient();
            return app;
        }

        public async Task SeedAsync(AppSettings settings)
        {
            using var scope = _host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.AppSettings.Add(settings);
            await db.SaveChangesAsync();
        }

        public async Task<AppSettings> RowAsync()
        {
            using var scope = _host.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().AppSettings.AsNoTracking().SingleAsync();
        }

        public Task<HttpResponseMessage> PutSettingsAsync(SiteSettingsBody body) =>
            SendAsync(HttpMethod.Put, "/api/settings", JsonContent.Create(body));

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null, string roles = "Admin")
        {
            var request = new HttpRequestMessage(method, path) { Content = content };
            request.Headers.Add("X-Test-Roles", roles);
            return Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
