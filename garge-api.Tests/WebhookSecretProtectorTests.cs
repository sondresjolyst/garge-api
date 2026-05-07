using garge_api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace garge_api.Tests;

public class WebhookSecretProtectorTests
{
    private static IWebhookSecretProtector CreateProtector()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        var provider = services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
        return new WebhookSecretProtector(provider);
    }

    [Fact]
    public void RoundTrip_RestoresOriginalSecret()
    {
        var p = CreateProtector();
        var secret = "vipps-webhook-secret-abc-123";
        var protectedSecret = p.Protect(secret);

        Assert.NotEqual(secret, protectedSecret);
        Assert.StartsWith("enc:v1:", protectedSecret);
        Assert.Equal(secret, p.Unprotect(protectedSecret));
    }

    [Fact]
    public void Unprotect_PlaintextLegacyValue_ReturnsAsIs()
    {
        var p = CreateProtector();
        var legacy = "old-plaintext-secret";

        Assert.Equal(legacy, p.Unprotect(legacy));
    }

    [Fact]
    public void Protect_EmptyString_ReturnsEmpty()
    {
        var p = CreateProtector();
        Assert.Equal(string.Empty, p.Protect(string.Empty));
    }

    [Fact]
    public void Unprotect_EmptyString_ReturnsEmpty()
    {
        var p = CreateProtector();
        Assert.Equal(string.Empty, p.Unprotect(string.Empty));
    }
}
