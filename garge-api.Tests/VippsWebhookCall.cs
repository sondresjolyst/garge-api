using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// One delivery to a garge webhook endpoint. It goes through the real AppKit.Vipps endpoint with garge's
/// wiring, handler and registration store, over the test's DbContext, and is signed the way Vipps signs.
/// </summary>
public sealed class VippsWebhookCall(ApplicationDbContext db, string slot, AppSettings? settings, Action<IServiceCollection> services)
{
    public const string Host = "garge-api.test";

    public string Body { get; set; } = string.Empty;

    /// <summary>Whether the request carries a signature made with one of the slot's secrets.</summary>
    public bool Valid { get; set; } = true;

    /// <summary>The secret the request is signed with. Defaults to the slot's live secret, else its test secret.</summary>
    public string? SignedWith { get; set; }

    private string Path => slot == GargeVippsWebhooks.ShopSlot ? GargeVippsWebhooks.ShopPath : GargeVippsWebhooks.SubscriptionPath;

    /// <summary>Sends the request and returns the status code the endpoint answered with.</summary>
    public async Task<int> Webhook()
    {
        await StoreSecretsAsync();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<AppOptions>(o =>
        {
            o.FrontendBaseUrl = "https://www.garge.no";
            o.ApiBaseUrl = "https://garge-api.prod.tumogroup.com";
        });
        builder.Services.AddSingleton(db);
        builder.Services.AddAppKitVipps(o =>
        {
            o.Live.MerchantSerialNumber = "msn-prod";
            o.Test.MerchantSerialNumber = "msn-test";
        });
        builder.Services.AddGargeVippsWebhooks();
        builder.Services.AddSingleton(Mock.Of<IAppSettingsCache>());
        services(builder.Services);

        await using var app = builder.Build();
        app.MapGargeVippsWebhooks();
        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == Path);

        await using var scope = app.Services.CreateAsyncScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        Sign(http.Request);
        await endpoint.RequestDelegate!(http);
        return http.Response.StatusCode;
    }

    /// <summary>Signs the request as Vipps does: an HMAC-SHA256 over the method, path, date, host and body hash.</summary>
    private void Sign(HttpRequest request)
    {
        var secret = Valid ? SignedWith ?? DefaultSecret() ?? "no-secret-stored" : "not-the-secret";
        var bytes = Encoding.UTF8.GetBytes(Body);
        var hash = Convert.ToBase64String(SHA256.HashData(bytes));
        var date = DateTimeOffset.UtcNow.ToString("R");
        var canonical = $"POST\n{Path}\n{date};{Host};{hash}";
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical)));

        request.Method = HttpMethods.Post;
        request.Path = Path;
        request.Headers.Host = Host;
        request.Headers["x-ms-date"] = date;
        request.Headers["x-ms-content-sha256"] = hash;
        request.Headers.Authorization = $"HMAC-SHA256 SignedHeaders=x-ms-date;host;x-ms-content-sha256&Signature={signature}";
        request.ContentType = "application/json";
        request.ContentLength = bytes.Length;
        request.Body = new MemoryStream(bytes);
    }

    private string? DefaultSecret()
    {
        if (settings == null) return null;
        var (live, test) = slot == GargeVippsWebhooks.ShopSlot
            ? (settings.VippsShopWebhookSecret, settings.VippsTestShopWebhookSecret)
            : (settings.VippsSubscriptionWebhookSecret, settings.VippsTestSubscriptionWebhookSecret);
        return !string.IsNullOrEmpty(live) ? live : !string.IsNullOrEmpty(test) ? test : null;
    }

    /// <summary>Stores the test's webhook registrations in the AppSettings row, where the registration store reads them.</summary>
    private async Task StoreSecretsAsync()
    {
        if (settings == null) return;
        var row = await db.AppSettings.FindAsync([1], TestContext.Current.CancellationToken);
        if (row == null)
        {
            row = new AppSettings { Id = 1 };
            db.AppSettings.Add(row);
        }
        row.VippsShopWebhookId = settings.VippsShopWebhookId;
        row.VippsShopWebhookSecret = settings.VippsShopWebhookSecret;
        row.VippsTestShopWebhookId = settings.VippsTestShopWebhookId;
        row.VippsTestShopWebhookSecret = settings.VippsTestShopWebhookSecret;
        row.VippsSubscriptionWebhookId = settings.VippsSubscriptionWebhookId;
        row.VippsSubscriptionWebhookSecret = settings.VippsSubscriptionWebhookSecret;
        row.VippsTestSubscriptionWebhookId = settings.VippsTestSubscriptionWebhookId;
        row.VippsTestSubscriptionWebhookSecret = settings.VippsTestSubscriptionWebhookSecret;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
