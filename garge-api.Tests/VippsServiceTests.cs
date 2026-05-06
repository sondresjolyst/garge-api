using garge_api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace garge_api.Tests;

public class VippsServiceTests
{
    private static VippsService CreateService()
    {
        var opts = Options.Create(new VippsOptions
        {
            ClientId = "id",
            ClientSecret = "secret",
            MerchantSerialNumber = "msn",
            SubscriptionKey = "key",
            BaseUrl = "https://apitest.vipps.no"
        });
        var appOpts = Options.Create(new AppOptions
        {
            FrontendBaseUrl = "https://www.garge.no",
            ApiBaseUrl = "https://garge-api.prod.tumogroup.com"
        });
        var http = new HttpClient();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var scopeFactory = new Mock<IServiceScopeFactory>().Object;
        return new VippsService(http, opts, appOpts, cache, NullLogger<VippsService>.Instance, scopeFactory);
    }

    private static HttpRequest BuildRequest(string body, string secret, DateTimeOffset? when = null,
        string method = "POST", string path = "/api/shop/webhook", string host = "garge-api.prod.tumogroup.com",
        string? overrideAuth = null, string? overrideContentHash = null, string? overrideDate = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.Host = new HostString(host);
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        ctx.Request.ContentLength = Encoding.UTF8.GetByteCount(body);

        var date = overrideDate ?? (when ?? DateTimeOffset.UtcNow).ToString("R");
        var contentHash = overrideContentHash ?? Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        ctx.Request.Headers["x-ms-date"] = date;
        ctx.Request.Headers["x-ms-content-sha256"] = contentHash;
        ctx.Request.Headers["Host"] = host;

        if (overrideAuth != null)
        {
            ctx.Request.Headers["Authorization"] = overrideAuth;
        }
        else
        {
            var canonical = $"{method}\n{path}\n{date};{host};{contentHash}";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var sig = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
            ctx.Request.Headers["Authorization"] =
                $"HMAC-SHA256 SignedHeaders=x-ms-date;host;x-ms-content-sha256&Signature={sig}";
        }
        return ctx.Request;
    }

    [Fact]
    public void Verify_ValidHmac_ReturnsValid()
    {
        var svc = CreateService();
        var body = """{"reference":"42","name":"AUTHORIZED"}""";
        var req = BuildRequest(body, "my-secret");

        Assert.Equal(WebhookVerifyResult.Valid, svc.VerifyWebhookSignature(req, body, "my-secret"));
    }

    [Fact]
    public void Verify_WrongSecret_ReturnsBadSignature()
    {
        var svc = CreateService();
        var body = """{"reference":"42","name":"AUTHORIZED"}""";
        var req = BuildRequest(body, "correct-secret");

        Assert.Equal(WebhookVerifyResult.BadSignature, svc.VerifyWebhookSignature(req, body, "wrong-secret"));
    }

    [Fact]
    public void Verify_TamperedBody_ReturnsBadContentHash()
    {
        var svc = CreateService();
        var original = """{"reference":"42","name":"AUTHORIZED"}""";
        var tampered = """{"reference":"99","name":"AUTHORIZED"}""";
        var req = BuildRequest(original, "secret");

        Assert.Equal(WebhookVerifyResult.BadContentHash,
            svc.VerifyWebhookSignature(req, tampered, "secret"));
    }

    [Fact]
    public void Verify_MissingAuthHeader_ReturnsMissingHeader()
    {
        var svc = CreateService();
        var body = """{"reference":"42"}""";
        var req = BuildRequest(body, "secret", overrideAuth: "");

        Assert.Equal(WebhookVerifyResult.MissingHeader, svc.VerifyWebhookSignature(req, body, "secret"));
    }

    [Fact]
    public void Verify_EmptySecret_ReturnsMissingSecret()
    {
        var svc = CreateService();
        var body = """{"reference":"42"}""";
        var req = BuildRequest(body, "real");

        Assert.Equal(WebhookVerifyResult.MissingSecret, svc.VerifyWebhookSignature(req, body, string.Empty));
    }

    [Fact]
    public void Verify_StaleDate_ReturnsStale()
    {
        var svc = CreateService();
        var body = """{"reference":"42"}""";
        var req = BuildRequest(body, "secret", when: DateTimeOffset.UtcNow.AddMinutes(-30));

        Assert.Equal(WebhookVerifyResult.Stale, svc.VerifyWebhookSignature(req, body, "secret"));
    }

    [Fact]
    public void Verify_BadDate_ReturnsBadDate()
    {
        var svc = CreateService();
        var body = """{"reference":"42"}""";
        var req = BuildRequest(body, "secret", overrideDate: "not-a-date");

        Assert.Equal(WebhookVerifyResult.BadDate, svc.VerifyWebhookSignature(req, body, "secret"));
    }

    [Fact]
    public void Verify_TamperedContentHashHeader_ReturnsBadContentHash()
    {
        var svc = CreateService();
        var body = """{"reference":"42"}""";
        var bogus = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("other")));
        var req = BuildRequest(body, "secret", overrideContentHash: bogus);

        Assert.Equal(WebhookVerifyResult.BadContentHash,
            svc.VerifyWebhookSignature(req, body, "secret"));
    }
}
