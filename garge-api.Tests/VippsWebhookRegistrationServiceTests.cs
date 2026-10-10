using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace garge_api.Tests;

public class VippsWebhookRegistrationServiceTests
{
    private const string ShopUrl = "https://api.example.invalid/api/shop/webhook";
    private const string SubscriptionUrl = "https://api.example.invalid/api/subscriptions/webhook";

    private sealed class Harness
    {
        public required VippsWebhookRegistrationService Service { get; init; }
        public required Mock<IVippsService> Vipps { get; init; }
        public required Mock<IAppSettingsCache> Cache { get; init; }
        public required IServiceProvider Provider { get; init; }

        public async Task<AppSettings> SettingsAsync()
        {
            using var scope = Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.AppSettings.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken);
        }
    }

    private static Harness Build(
        AppSettings? settings,
        List<VippsWebhookInfo>? live,
        List<VippsWebhookInfo>? test)
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(dbName));

        var vipps = new Mock<IVippsService>();
        if (live != null) vipps.Setup(v => v.ListWebhooksAsync(false)).ReturnsAsync(live);
        if (test != null) vipps.Setup(v => v.ListWebhooksAsync(true)).ReturnsAsync(test);
        var seq = 0;
        vipps.Setup(v => v.RegisterWebhookAsync(It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<bool>()))
            .ReturnsAsync((string url, string[] _, bool isTest) =>
            {
                seq++;
                return ($"new-{(isTest ? "test" : "live")}-{seq}", $"secret-{seq}");
            });
        services.AddSingleton(vipps.Object);

        var protector = new Mock<IWebhookSecretProtector>();
        protector.Setup(p => p.Protect(It.IsAny<string>())).Returns<string>(s => $"protected:{s}");
        services.AddSingleton(protector.Object);

        var cache = new Mock<IAppSettingsCache>();
        services.AddSingleton(cache.Object);

        var provider = services.BuildServiceProvider();
        if (settings != null)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.AppSettings.Add(settings);
            db.SaveChanges();
        }

        var vippsOpts = new VippsOptions
        {
            BaseUrl = "https://api.vipps.no",
            ClientId = live != null ? "id" : "", ClientSecret = "s", SubscriptionKey = "k", MerchantSerialNumber = "msn-prod",
            TestClientId = test != null ? "tid" : "", TestClientSecret = "ts", TestSubscriptionKey = "tk", TestMerchantSerialNumber = "msn-test"
        };
        var svc = new VippsWebhookRegistrationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AppOptions { FrontendBaseUrl = "https://www.example.invalid", ApiBaseUrl = "https://api.example.invalid/" }),
            Options.Create(vippsOpts),
            NullLogger<VippsWebhookRegistrationService>.Instance);
        return new Harness { Service = svc, Vipps = vipps, Cache = cache, Provider = provider };
    }

    private static VippsWebhookInfo Hook(string id, string url) => new() { Id = id, Url = url };

    [Fact]
    public async Task NothingStored_RegistersBothWebhooksInEachConfiguredEnvironment()
    {
        var h = Build(null, live: [], test: []);

        await h.Service.StartAsync(CancellationToken.None);

        h.Vipps.Verify(v => v.RegisterWebhookAsync(ShopUrl, It.IsAny<string[]>(), false), Times.Once);
        h.Vipps.Verify(v => v.RegisterWebhookAsync(SubscriptionUrl, It.IsAny<string[]>(), false), Times.Once);
        h.Vipps.Verify(v => v.RegisterWebhookAsync(ShopUrl, It.IsAny<string[]>(), true), Times.Once);
        h.Vipps.Verify(v => v.RegisterWebhookAsync(SubscriptionUrl, It.IsAny<string[]>(), true), Times.Once);
        var s = await h.SettingsAsync();
        Assert.StartsWith("new-live-", s.VippsShopWebhookId);
        Assert.StartsWith("new-live-", s.VippsSubscriptionWebhookId);
        Assert.StartsWith("new-test-", s.VippsTestShopWebhookId);
        Assert.StartsWith("new-test-", s.VippsTestSubscriptionWebhookId);
        Assert.StartsWith("protected:secret-", s.VippsShopWebhookSecret);
        Assert.StartsWith("protected:secret-", s.VippsTestSubscriptionWebhookSecret);
        Assert.NotEqual(s.VippsShopWebhookSecret, s.VippsTestShopWebhookSecret);
        h.Cache.Verify(c => c.Invalidate(), Times.Once);
    }

    [Fact]
    public async Task OnlyProductionConfigured_TestEnvironmentIsLeftAlone()
    {
        var h = Build(new AppSettings { Id = 1 }, live: [], test: null);

        await h.Service.StartAsync(CancellationToken.None);

        h.Vipps.Verify(v => v.ListWebhooksAsync(true), Times.Never);
        h.Vipps.Verify(v => v.RegisterWebhookAsync(It.IsAny<string>(), It.IsAny<string[]>(), true), Times.Never);
        var s = await h.SettingsAsync();
        Assert.NotNull(s.VippsShopWebhookId);
        Assert.Null(s.VippsTestShopWebhookId);
    }

    [Fact]
    public async Task StoredRegistrationsStillListed_AreKept()
    {
        var settings = new AppSettings
        {
            Id = 1,
            VippsShopWebhookId = "live-shop", VippsShopWebhookSecret = "a",
            VippsSubscriptionWebhookId = "live-sub", VippsSubscriptionWebhookSecret = "b",
            VippsTestShopWebhookId = "test-shop", VippsTestShopWebhookSecret = "c",
            VippsTestSubscriptionWebhookId = "test-sub", VippsTestSubscriptionWebhookSecret = "d"
        };
        var h = Build(settings,
            live: [Hook("live-shop", ShopUrl), Hook("live-sub", SubscriptionUrl)],
            test: [Hook("test-shop", ShopUrl), Hook("test-sub", SubscriptionUrl)]);

        await h.Service.StartAsync(CancellationToken.None);

        h.Vipps.Verify(v => v.RegisterWebhookAsync(It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<bool>()), Times.Never);
        h.Vipps.Verify(v => v.DeleteWebhookAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        h.Cache.Verify(c => c.Invalidate(), Times.Never);
        var s = await h.SettingsAsync();
        Assert.Equal("live-shop", s.VippsShopWebhookId);
        Assert.Equal("d", s.VippsTestSubscriptionWebhookSecret);
    }

    [Fact]
    public async Task LegacyTestRegistrationInTheProductionSlot_MovesToTheTestSlot()
    {
        var settings = new AppSettings
        {
            Id = 1,
            VippsShopWebhookId = "old-shop", VippsShopWebhookSecret = "shop-secret",
            VippsSubscriptionWebhookId = "old-sub", VippsSubscriptionWebhookSecret = "sub-secret"
        };
        var h = Build(settings, live: [], test: [Hook("old-shop", ShopUrl), Hook("old-sub", SubscriptionUrl)]);

        await h.Service.StartAsync(CancellationToken.None);

        var s = await h.SettingsAsync();
        Assert.Equal("old-shop", s.VippsTestShopWebhookId);
        Assert.Equal("shop-secret", s.VippsTestShopWebhookSecret);
        Assert.Equal("old-sub", s.VippsTestSubscriptionWebhookId);
        Assert.Equal("sub-secret", s.VippsTestSubscriptionWebhookSecret);
        Assert.StartsWith("new-live-", s.VippsShopWebhookId);
        Assert.StartsWith("new-live-", s.VippsSubscriptionWebhookId);
        h.Vipps.Verify(v => v.RegisterWebhookAsync(It.IsAny<string>(), It.IsAny<string[]>(), true), Times.Never);
        h.Vipps.Verify(v => v.DeleteWebhookAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task StoredRegistrationGoneAtVipps_OrphansForTheUrlAreDeletedAndItIsReplaced()
    {
        var settings = new AppSettings
        {
            Id = 1,
            VippsShopWebhookId = "live-shop", VippsShopWebhookSecret = "a",
            VippsSubscriptionWebhookId = "gone", VippsSubscriptionWebhookSecret = "b"
        };
        var h = Build(settings,
            live: [Hook("live-shop", ShopUrl), Hook("orphan", SubscriptionUrl), Hook("someone-else", "https://other.example.invalid/hook")],
            test: null);
        var order = new List<string>();
        h.Vipps.Setup(v => v.RegisterWebhookAsync(SubscriptionUrl, It.IsAny<string[]>(), false))
            .Callback(() => order.Add("register")).ReturnsAsync(("new-live-sub", "secret-sub"));
        h.Vipps.Setup(v => v.DeleteWebhookAsync("orphan", false)).Callback(() => order.Add("delete")).Returns(Task.CompletedTask);

        await h.Service.StartAsync(CancellationToken.None);

        h.Vipps.Verify(v => v.DeleteWebhookAsync("orphan", false), Times.Once);
        Assert.True(order.IndexOf("register") < order.IndexOf("delete"), string.Join(",", order));
        h.Vipps.Verify(v => v.DeleteWebhookAsync("someone-else", It.IsAny<bool>()), Times.Never);
        h.Vipps.Verify(v => v.DeleteWebhookAsync("live-shop", It.IsAny<bool>()), Times.Never);
        h.Vipps.Verify(v => v.RegisterWebhookAsync(SubscriptionUrl, It.IsAny<string[]>(), false), Times.Once);
        var s = await h.SettingsAsync();
        Assert.Equal("live-shop", s.VippsShopWebhookId);
        Assert.StartsWith("new-live-", s.VippsSubscriptionWebhookId);
    }

    [Fact]
    public async Task RegistrationFails_TheOldRegistrationsForTheUrlAreKept()
    {
        // Deleting first would leave Vipps with nowhere to send events until the next start.
        var settings = new AppSettings { Id = 1, VippsShopWebhookId = "gone", VippsShopWebhookSecret = "a" };
        var h = Build(settings, live: [Hook("orphan", ShopUrl)], test: null);
        h.Vipps.Setup(v => v.RegisterWebhookAsync(ShopUrl, It.IsAny<string[]>(), false)).ThrowsAsync(new HttpRequestException("down"));

        await h.Service.StartAsync(CancellationToken.None);

        h.Vipps.Verify(v => v.DeleteWebhookAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        Assert.Equal("gone", (await h.SettingsAsync()).VippsShopWebhookId);
    }

    [Fact]
    public async Task DeleteFails_TheNewRegistrationIsStillStored()
    {
        var h = Build(new AppSettings { Id = 1 }, live: [Hook("orphan", ShopUrl)], test: null);
        h.Vipps.Setup(v => v.DeleteWebhookAsync("orphan", false)).ThrowsAsync(new HttpRequestException("down"));

        await h.Service.StartAsync(CancellationToken.None);

        Assert.StartsWith("new-live-", (await h.SettingsAsync()).VippsShopWebhookId);
    }

    [Fact]
    public async Task OrphanUrlWithATrailingSlashOrOtherCase_IsDeleted()
    {
        var h = Build(new AppSettings { Id = 1 }, live: [Hook("slash", ShopUrl + "/"), Hook("upper", ShopUrl.ToUpperInvariant())], test: null);

        await h.Service.StartAsync(CancellationToken.None);

        h.Vipps.Verify(v => v.DeleteWebhookAsync("slash", false), Times.Once);
        h.Vipps.Verify(v => v.DeleteWebhookAsync("upper", false), Times.Once);
    }

    [Fact]
    public async Task TestListingFails_AProductionSlotThatMayHoldATestRegistrationIsLeftAlone()
    {
        var settings = new AppSettings { Id = 1, VippsShopWebhookId = "maybe-test", VippsShopWebhookSecret = "a" };
        var h = Build(settings, live: [], test: []);
        h.Vipps.Setup(v => v.ListWebhooksAsync(true)).ThrowsAsync(new HttpRequestException("down"));

        await h.Service.StartAsync(CancellationToken.None);

        h.Vipps.Verify(v => v.RegisterWebhookAsync(ShopUrl, It.IsAny<string[]>(), false), Times.Never);
        h.Vipps.Verify(v => v.RegisterWebhookAsync(SubscriptionUrl, It.IsAny<string[]>(), false), Times.Once);
        var s = await h.SettingsAsync();
        Assert.Equal("maybe-test", s.VippsShopWebhookId);
        Assert.Equal("a", s.VippsShopWebhookSecret);
    }

    [Fact]
    public async Task ListingFails_NothingIsRegisteredOrDeletedInThatEnvironment()
    {
        var settings = new AppSettings { Id = 1, VippsShopWebhookId = "live-shop", VippsShopWebhookSecret = "a" };
        var h = Build(settings, live: [], test: []);
        h.Vipps.Setup(v => v.ListWebhooksAsync(false)).ThrowsAsync(new HttpRequestException("down"));

        await h.Service.StartAsync(CancellationToken.None);

        h.Vipps.Verify(v => v.RegisterWebhookAsync(It.IsAny<string>(), It.IsAny<string[]>(), false), Times.Never);
        h.Vipps.Verify(v => v.DeleteWebhookAsync(It.IsAny<string>(), false), Times.Never);
        h.Vipps.Verify(v => v.RegisterWebhookAsync(ShopUrl, It.IsAny<string[]>(), true), Times.Once);
        Assert.Equal("live-shop", (await h.SettingsAsync()).VippsShopWebhookId);
    }

    [Fact]
    public async Task RegistrationFails_KeepsTheStoredValuesForThatSlot()
    {
        var h = Build(new AppSettings { Id = 1 }, live: [], test: null);
        h.Vipps.Setup(v => v.RegisterWebhookAsync(ShopUrl, It.IsAny<string[]>(), false)).ThrowsAsync(new HttpRequestException("down"));

        await h.Service.StartAsync(CancellationToken.None);

        var s = await h.SettingsAsync();
        Assert.Null(s.VippsShopWebhookId);
        Assert.Null(s.VippsShopWebhookSecret);
        Assert.StartsWith("new-live-", s.VippsSubscriptionWebhookId);
    }
}
