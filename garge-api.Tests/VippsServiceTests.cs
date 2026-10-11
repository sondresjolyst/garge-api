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
        var appOpts = Options.Create(new AppOptions
        {
            FrontendBaseUrl = "https://www.garge.no",
            ApiBaseUrl = "https://garge-api.prod.tumogroup.com"
        });
        var client = new VippsClient(new HttpClient(), Options.Create(new VippsOptions()), new FixedVippsEnvironment(false), NullLogger<VippsClient>.Instance);
        return new VippsService(client, appOpts, TimeProvider.System, NullLogger<VippsService>.Instance);
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

        Assert.Equal(VippsWebhookResult.Valid, svc.VerifyWebhookSignature(req, body, "my-secret"));
    }

    [Fact]
    public void Verify_WrongSecret_ReturnsBadSignature()
    {
        var svc = CreateService();
        var body = """{"reference":"42","name":"AUTHORIZED"}""";
        var req = BuildRequest(body, "correct-secret");

        Assert.Equal(VippsWebhookResult.BadSignature, svc.VerifyWebhookSignature(req, body, "wrong-secret"));
    }

    [Fact]
    public void Verify_TamperedBody_ReturnsBadContentHash()
    {
        var svc = CreateService();
        var original = """{"reference":"42","name":"AUTHORIZED"}""";
        var tampered = """{"reference":"99","name":"AUTHORIZED"}""";
        var req = BuildRequest(original, "secret");

        Assert.Equal(VippsWebhookResult.BadContentHash,
            svc.VerifyWebhookSignature(req, tampered, "secret"));
    }

    [Fact]
    public void Verify_MissingAuthHeader_ReturnsMissingHeader()
    {
        var svc = CreateService();
        var body = """{"reference":"42"}""";
        var req = BuildRequest(body, "secret", overrideAuth: "");

        Assert.Equal(VippsWebhookResult.MissingHeader, svc.VerifyWebhookSignature(req, body, "secret"));
    }

    [Fact]
    public void Verify_EmptySecret_ReturnsMissingSecret()
    {
        var svc = CreateService();
        var body = """{"reference":"42"}""";
        var req = BuildRequest(body, "real");

        Assert.Equal(VippsWebhookResult.MissingSecret, svc.VerifyWebhookSignature(req, body, string.Empty));
    }

    [Theory]
    [InlineData(-30 * 24 * 60)]
    [InlineData(-8 * 24 * 60 - 1)]
    [InlineData(10)]
    public void Verify_DateOutsideTheRetryPeriod_ReturnsStale(int minutesFromNow)
    {
        var svc = CreateService();
        var body = """{"reference":"42"}""";
        var req = BuildRequest(body, "secret", when: DateTimeOffset.UtcNow.AddMinutes(minutesFromNow));

        Assert.Equal(VippsWebhookResult.Stale, svc.VerifyWebhookSignature(req, body, "secret"));
    }

    [Fact]
    public void ProcessedEvents_AreKeptLongerThanASignatureStaysValid()
    {
        // A replayed webhook is stopped by its processed-event marker, so the marker must outlive the signature.
        Assert.True(ProcessedWebhookEventCleanupService.Retention > VippsWebhookSignature.MaxAge);
    }

    [Theory]
    [InlineData(-30)]
    [InlineData(-6 * 60)]
    [InlineData(-7 * 24 * 60)]
    [InlineData(3)]
    public void Verify_RetrySignedEarlierInTheRetryPeriod_IsValid(int minutesFromNow)
    {
        // Vipps retries for up to 7 days, and a retry may carry the date it was first signed with.
        var svc = CreateService();
        var body = """{"reference":"42"}""";
        var req = BuildRequest(body, "secret", when: DateTimeOffset.UtcNow.AddMinutes(minutesFromNow));

        Assert.Equal(VippsWebhookResult.Valid, svc.VerifyWebhookSignature(req, body, "secret"));
    }

    [Fact]
    public void Verify_BadDate_ReturnsBadDate()
    {
        var svc = CreateService();
        var body = """{"reference":"42"}""";
        var req = BuildRequest(body, "secret", overrideDate: "not-a-date");

        Assert.Equal(VippsWebhookResult.BadDate, svc.VerifyWebhookSignature(req, body, "secret"));
    }

    [Fact]
    public void Verify_TamperedContentHashHeader_ReturnsBadContentHash()
    {
        var svc = CreateService();
        var body = """{"reference":"42"}""";
        var bogus = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("other")));
        var req = BuildRequest(body, "secret", overrideContentHash: bogus);

        Assert.Equal(VippsWebhookResult.BadContentHash,
            svc.VerifyWebhookSignature(req, body, "secret"));
    }
}
