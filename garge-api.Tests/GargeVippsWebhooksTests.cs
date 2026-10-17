using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace garge_api.Tests;

public class GargeVippsWebhooksTests
{
    private static ApplicationDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static AppSettingsWebhookRegistrationStore Store(ApplicationDbContext db, IAppSettingsCache? cache = null) =>
        new(db, cache ?? Mock.Of<IAppSettingsCache>(), NullLogger<AppSettingsWebhookRegistrationStore>.Instance);

    private static ServiceProvider Services(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<AppOptions>(o =>
        {
            o.FrontendBaseUrl = "https://www.garge.no";
            o.ApiBaseUrl = "https://garge-api.prod.tumogroup.com";
        });
        configure?.Invoke(services);
        services.AddGargeVippsWebhooks();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Options_KeepTheRegisteredPathsTheLockAndTheSecretFormat()
    {
        using var sp = Services();
        var options = sp.GetRequiredService<IOptions<VippsWebhookOptions>>().Value;

        Assert.Collection(options.Slots,
            s => Assert.Equal(("shop", VippsWebhookKind.Payment, "/api/shop/webhook"), (s.Name, s.Kind, s.Path)),
            s => Assert.Equal(("subscription", VippsWebhookKind.Recurring, "/api/subscriptions/webhook"), (s.Name, s.Kind, s.Path)));
        Assert.Equal("https://garge-api.prod.tumogroup.com", options.PublicBaseUrl);
        Assert.Equal(7300114, options.AdvisoryLockKey);
        Assert.Equal("garge.vipps.webhook-secret.v1", options.SecretPurpose);
        Assert.Equal("enc:v1:", options.SecretPrefix);
        Assert.True(options.AllowMissingRecurringMsn);
    }

    [Fact]
    public void Protector_ReadsASecretStoredBeforeTheMove()
    {
        var provider = new EphemeralDataProtectionProvider();
        using var sp = Services(s => s.AddSingleton<IDataProtectionProvider>(provider));
        // What garge's own protector stored: its prefix and the protected value under its purpose.
        var stored = "enc:v1:" + provider.CreateProtector("garge.vipps.webhook-secret.v1").Protect("the-secret");

        var protector = sp.GetRequiredService<IVippsWebhookSecretProtector>();

        Assert.Equal("the-secret", protector.Unprotect(stored));
        Assert.Equal("plain-secret", protector.Unprotect("plain-secret"));
        Assert.Equal("the-secret", protector.Unprotect(protector.Protect("the-secret")));
    }

    [Fact]
    public void Handlers_AndTheStoreAreGarges()
    {
        var services = new ServiceCollection().AddGargeVippsWebhooks();

        Type? Implementation<T>() => services.Last(d => d.ServiceType == typeof(T)).ImplementationType;
        Assert.Equal(typeof(AppSettingsWebhookRegistrationStore), Implementation<IVippsWebhookRegistrationStore>());
        Assert.Equal(typeof(ShopWebhookHandler), Implementation<IVippsPaymentWebhookHandler>());
        Assert.Equal(typeof(SubscriptionWebhookHandler), Implementation<IVippsRecurringWebhookHandler>());
    }

    [Theory]
    [InlineData("shop", false)]
    [InlineData("shop", true)]
    [InlineData("subscription", false)]
    [InlineData("subscription", true)]
    public async Task Get_ReadsEachSlotFromItsOwnColumns(string slot, bool isTest)
    {
        using var db = CreateDbContext();
        db.AppSettings.Add(new AppSettings
        {
            Id = 1,
            VippsShopWebhookId = "shop-live", VippsShopWebhookSecret = "s-shop-live",
            VippsTestShopWebhookId = "shop-test", VippsTestShopWebhookSecret = "s-shop-test",
            VippsSubscriptionWebhookId = "subscription-live", VippsSubscriptionWebhookSecret = "s-subscription-live",
            VippsTestSubscriptionWebhookId = "subscription-test", VippsTestSubscriptionWebhookSecret = "s-subscription-test",
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var env = isTest ? "test" : "live";

        var registration = await Store(db).GetAsync(slot, isTest, TestContext.Current.CancellationToken);

        Assert.Equal(new VippsWebhookRegistration($"{slot}-{env}", $"s-{slot}-{env}"), registration);
    }

    [Fact]
    public async Task Get_WithoutARow_OrARegistration_IsNull()
    {
        using var db = CreateDbContext();
        Assert.Null(await Store(db).GetAsync("shop", false, TestContext.Current.CancellationToken));

        db.AppSettings.Add(new AppSettings { Id = 1 });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Null(await Store(db).GetAsync("subscription", true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Set_SeedsTheRowWhenItIsMissing_AndLeavesTheSaveToTheCaller()
    {
        using var db = CreateDbContext();
        var store = Store(db);

        await store.SetAsync("subscription", true, new VippsWebhookRegistration("wh-1", "enc:v1:x"), TestContext.Current.CancellationToken);

        Assert.Equal(0, await db.AppSettings.AsNoTracking().CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new VippsWebhookRegistration("wh-1", "enc:v1:x"), await store.GetAsync("subscription", true, TestContext.Current.CancellationToken));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var row = await db.AppSettings.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal((1, "wh-1", "enc:v1:x"), (row.Id, row.VippsTestSubscriptionWebhookId, row.VippsTestSubscriptionWebhookSecret));
        Assert.Null(row.VippsSubscriptionWebhookId);
    }

    [Fact]
    public async Task Set_Null_ClearsTheRegistration()
    {
        using var db = CreateDbContext();
        db.AppSettings.Add(new AppSettings { Id = 1, VippsShopWebhookId = "wh", VippsShopWebhookSecret = "s", VippsTestShopWebhookId = "keep" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Store(db).SetAsync("shop", false, null, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var row = await db.AppSettings.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal((null, null, "keep"), (row.VippsShopWebhookId, row.VippsShopWebhookSecret, row.VippsTestShopWebhookId));
    }

    [Fact]
    public async Task Saved_ClearsTheSettingsCache()
    {
        using var db = CreateDbContext();
        var cache = new Mock<IAppSettingsCache>();

        await Store(db, cache.Object).SavedAsync(TestContext.Current.CancellationToken);

        cache.Verify(c => c.Invalidate(), Times.Once);
    }
}
