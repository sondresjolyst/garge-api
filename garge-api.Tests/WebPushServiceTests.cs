using garge_api.Models;
using garge_api.Models.Push;
using garge_api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using System.Net;
using Xunit;

namespace garge_api.Tests;

public class WebPushServiceTests : ControllerTestBase
{
    private static IConfiguration MakeConfig(string? publicKey = null, string? privateKey = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vapid:PublicKey"] = publicKey,
                ["Vapid:PrivateKey"] = privateKey,
                ["Vapid:Subject"] = "https://garge.no"
            })
            .Build();

    private static IServiceScopeFactory MakeScopeFactory(ApplicationDbContext db)
    {
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(ApplicationDbContext))).Returns(db);

        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(serviceProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);
        return scopeFactory.Object;
    }

    private static Mock<IHttpClientFactory> MakeFactory(HttpStatusCode responseCode = HttpStatusCode.Created)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(responseCode));

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("webpush")).Returns(new HttpClient(handler.Object));
        return factory;
    }

    private static Mock<IHttpClientFactory> MakeFactoryThrowing(HttpStatusCode statusCode)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("expired", null, statusCode));

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("webpush")).Returns(new HttpClient(handler.Object));
        return factory;
    }

    [Fact]
    public async Task SendAsync_OpensFreshScopePerCall()
    {
        var db = CreateDbContext();
        var (publicKey, privateKey) = GenerateVapidKeyPair();

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(ApplicationDbContext))).Returns(db);

        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(serviceProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var factory = MakeFactory(HttpStatusCode.Created);
        var svc = new WebPushService(scopeFactory.Object, factory.Object,
            MakeConfig(publicKey, privateKey),
            NullLogger<WebPushService>.Instance);

        await svc.SendAsync("u-1", "title", "body", TestContext.Current.CancellationToken);
        await svc.SendAsync("u-2", "title", "body", TestContext.Current.CancellationToken);

        scopeFactory.Verify(f => f.CreateScope(), Times.Exactly(2));
    }

    [Fact]
    public async Task SendAsync_VapidKeysNotConfigured_SkipsWithoutError()
    {
        var db = CreateDbContext();
        var factory = MakeFactory();
        var svc = new WebPushService(MakeScopeFactory(db), factory.Object, MakeConfig(), NullLogger<WebPushService>.Instance);

        // Should not throw, and should not attempt any HTTP call
        await svc.SendAsync("u1", "title", "body", TestContext.Current.CancellationToken);

        factory.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SendAsync_NoSubscriptions_SkipsWithoutError()
    {
        var db = CreateDbContext();
        var factory = MakeFactory();
        var svc = new WebPushService(MakeScopeFactory(db), factory.Object,
            MakeConfig("fake-pub-key", "fake-priv-key"),
            NullLogger<WebPushService>.Instance);

        await svc.SendAsync("u1", "title", "body", TestContext.Current.CancellationToken);

        factory.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SendAsync_SubscriptionReturns410Gone_RemovesSubscription()
    {
        var db = CreateDbContext();

        // Use real VAPID keys so encryption doesn't fail before HTTP call
        var (publicKey, privateKey) = GenerateVapidKeyPair();

        db.PushSubscriptions.Add(new PushSubscription
        {
            UserId = "u1",
            Endpoint = "https://push.example.com/sub/1",
            P256dh = FakeP256dhKey(),
            Auth = FakeAuthSecret()
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var factory = MakeFactoryThrowing(HttpStatusCode.Gone);
        var svc = new WebPushService(MakeScopeFactory(db), factory.Object,
            MakeConfig(publicKey, privateKey),
            NullLogger<WebPushService>.Instance);

        await svc.SendAsync("u1", "title", "body", TestContext.Current.CancellationToken);

        Assert.Empty(db.PushSubscriptions);
    }

    [Fact]
    public async Task SendAsync_SubscriptionReturns404NotFound_RemovesSubscription()
    {
        var db = CreateDbContext();
        var (publicKey, privateKey) = GenerateVapidKeyPair();

        db.PushSubscriptions.Add(new PushSubscription
        {
            UserId = "u1",
            Endpoint = "https://push.example.com/sub/1",
            P256dh = FakeP256dhKey(),
            Auth = FakeAuthSecret()
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var factory = MakeFactoryThrowing(HttpStatusCode.NotFound);
        var svc = new WebPushService(MakeScopeFactory(db), factory.Object,
            MakeConfig(publicKey, privateKey),
            NullLogger<WebPushService>.Instance);

        await svc.SendAsync("u1", "title", "body", TestContext.Current.CancellationToken);

        Assert.Empty(db.PushSubscriptions);
    }

    // Generate a real P-256 VAPID key pair for tests that reach the encryption path
    private static (string publicKey, string privateKey) GenerateVapidKeyPair()
    {
        using var ecdh = System.Security.Cryptography.ECDiffieHellman.Create(
            System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var p = ecdh.ExportParameters(true);

        var pub = new byte[65];
        pub[0] = 0x04;
        p.Q.X!.CopyTo(pub, 1);
        p.Q.Y!.CopyTo(pub, 33);

        return (Base64UrlEncode(pub), Base64UrlEncode(p.D!));
    }

    // A syntactically valid (but fake) browser P-256 DH public key (65 bytes, uncompressed point)
    private static string FakeP256dhKey()
    {
        using var ecdh = System.Security.Cryptography.ECDiffieHellman.Create(
            System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var p = ecdh.ExportParameters(false);
        var key = new byte[65];
        key[0] = 0x04;
        p.Q.X!.CopyTo(key, 1);
        p.Q.Y!.CopyTo(key, 33);
        return Base64UrlEncode(key);
    }

    private static string FakeAuthSecret()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
