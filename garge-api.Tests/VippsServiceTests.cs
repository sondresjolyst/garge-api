using garge_api.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace garge_api.Tests;

public class VippsServiceTests
{
    private static VippsService CreateService(string webhookSecret = "test-secret")
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
        return new VippsService(http, opts, appOpts, cache, NullLogger<VippsService>.Instance);
    }

    private static string BuildSignature(string body, string secret)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var computed = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexString(computed).ToLowerInvariant();
    }

    [Fact]
    public void VerifyWebhookSignature_ValidHmac_ReturnsTrue()
    {
        var svc = CreateService("my-secret");
        var body = """{"reference":"42","name":"AUTHORIZED"}""";
        var sig = BuildSignature(body, "my-secret");

        Assert.True(svc.VerifyWebhookSignature(body, sig, "my-secret"));
    }

    [Fact]
    public void VerifyWebhookSignature_WrongSecret_ReturnsFalse()
    {
        var svc = CreateService();
        var body = """{"reference":"42","name":"AUTHORIZED"}""";
        var sig = BuildSignature(body, "correct-secret");

        Assert.False(svc.VerifyWebhookSignature(body, sig, "wrong-secret"));
    }

    [Fact]
    public void VerifyWebhookSignature_TamperedBody_ReturnsFalse()
    {
        var svc = CreateService();
        var original = """{"reference":"42","name":"AUTHORIZED"}""";
        var tampered = """{"reference":"99","name":"AUTHORIZED"}""";
        var sig = BuildSignature(original, "my-secret");

        Assert.False(svc.VerifyWebhookSignature(tampered, sig, "my-secret"));
    }

    [Fact]
    public void VerifyWebhookSignature_MissingPrefix_ReturnsFalse()
    {
        var svc = CreateService();
        var body = """{"reference":"42"}""";
        var sig = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("secret"),
            Encoding.UTF8.GetBytes(body))).ToLowerInvariant(); // missing "sha256=" prefix

        Assert.False(svc.VerifyWebhookSignature(body, sig, "secret"));
    }

    [Fact]
    public void VerifyWebhookSignature_EmptySecret_ReturnsFalse()
    {
        var svc = CreateService();
        var body = """{"reference":"42"}""";
        var sig = BuildSignature(body, "real-secret");

        Assert.False(svc.VerifyWebhookSignature(body, sig, string.Empty));
    }
}
