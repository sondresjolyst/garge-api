using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Models.Subscription;
using garge_api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace garge_api.Tests;

public class SubscriptionChargeSchedulerServiceTests
{
    private static (SubscriptionChargeSchedulerService svc, Mock<IVippsService> vipps, ApplicationDbContext db)
        BuildHarness(bool testMode = false)
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));

        var vipps = new Mock<IVippsService>();
        services.AddSingleton(vipps.Object);

        var settings = new Mock<IAppSettingsCache>();
        settings.Setup(s => s.GetAsync()).ReturnsAsync(new AppSettings { Id = 1, VippsTestMode = testMode });
        services.AddSingleton(settings.Object);

        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var svc = new SubscriptionChargeSchedulerService(scopeFactory,
            NullLogger<SubscriptionChargeSchedulerService>.Instance);
        var db = provider.CreateScope().ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return (svc, vipps, db);
    }

    private static Product MakePrimaryProduct(int id = 1) => new()
    {
        Id = id, Name = "Garge Basic", PriceInOre = 29900,
        Interval = BillingInterval.Monthly, Type = ProductType.Primary, IsActive = true
    };

    [Fact]
    public async Task Scheduler_ActiveSubDueWithinLookahead_PostsChargeWithIdempotencyKey()
    {
        var (svc, vipps, db) = BuildHarness();
        var dueDate = DateTime.UtcNow.AddDays(3);
        await db.Products.AddAsync(MakePrimaryProduct());
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_due", Status = SubscriptionStatus.Active,
            NextChargeDate = dueDate
        };
        await db.Subscriptions.AddAsync(sub);
        await db.SaveChangesAsync();

        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        vipps.Verify(v => v.CreateChargeAsync(
            "agr_due", 29900, dueDate, "Garge Basic",
            $"charge-{sub.Id}-{dueDate.Ticks}"), Times.Once);
    }

    [Fact]
    public async Task Scheduler_ActiveSubBeyondLookahead_NotCharged()
    {
        var (svc, vipps, db) = BuildHarness();
        await db.Products.AddAsync(MakePrimaryProduct());
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_far", Status = SubscriptionStatus.Active,
            NextChargeDate = DateTime.UtcNow.AddDays(20)
        };
        await db.Subscriptions.AddAsync(sub);
        await db.SaveChangesAsync();

        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        vipps.Verify(v => v.CreateChargeAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTime>(),
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(SubscriptionStatus.Pending)]
    [InlineData(SubscriptionStatus.Stopped)]
    [InlineData(SubscriptionStatus.Expired)]
    public async Task Scheduler_NonActiveStatuses_Skipped(SubscriptionStatus status)
    {
        var (svc, vipps, db) = BuildHarness();
        await db.Products.AddAsync(MakePrimaryProduct());
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_inactive", Status = status,
            NextChargeDate = DateTime.UtcNow.AddDays(1)
        };
        await db.Subscriptions.AddAsync(sub);
        await db.SaveChangesAsync();

        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        vipps.Verify(v => v.CreateChargeAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTime>(),
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Scheduler_TestSubInLiveMode_NotCharged()
    {
        var (svc, vipps, db) = BuildHarness(testMode: false);
        await db.Products.AddAsync(MakePrimaryProduct());
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_test", Status = SubscriptionStatus.Active,
            NextChargeDate = DateTime.UtcNow.AddDays(1),
            IsTest = true
        };
        await db.Subscriptions.AddAsync(sub);
        await db.SaveChangesAsync();

        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        vipps.Verify(v => v.CreateChargeAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTime>(),
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Scheduler_LiveSubInTestMode_NotCharged()
    {
        var (svc, vipps, db) = BuildHarness(testMode: true);
        await db.Products.AddAsync(MakePrimaryProduct());
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_live", Status = SubscriptionStatus.Active,
            NextChargeDate = DateTime.UtcNow.AddDays(1),
            IsTest = false
        };
        await db.Subscriptions.AddAsync(sub);
        await db.SaveChangesAsync();

        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        vipps.Verify(v => v.CreateChargeAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTime>(),
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Scheduler_OneSubThrows_OthersStillProcessed()
    {
        var (svc, vipps, db) = BuildHarness();
        await db.Products.AddAsync(MakePrimaryProduct());
        var sub1 = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_a", Status = SubscriptionStatus.Active,
            NextChargeDate = DateTime.UtcNow.AddDays(1)
        };
        var sub2 = new Subscription
        {
            UserId = "user-2", ProductId = 1,
            VippsAgreementId = "agr_b", Status = SubscriptionStatus.Active,
            NextChargeDate = DateTime.UtcNow.AddDays(2)
        };
        await db.Subscriptions.AddRangeAsync(sub1, sub2);
        await db.SaveChangesAsync();

        vipps.Setup(v => v.CreateChargeAsync("agr_a", It.IsAny<int>(), It.IsAny<DateTime>(),
                It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new HttpRequestException("Vipps boom"));
        vipps.Setup(v => v.CreateChargeAsync("agr_b", It.IsAny<int>(), It.IsAny<DateTime>(),
                It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new VippsCreateChargeResponse { ChargeId = "chg_b" });

        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        vipps.Verify(v => v.CreateChargeAsync("agr_a", It.IsAny<int>(), It.IsAny<DateTime>(),
            It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        vipps.Verify(v => v.CreateChargeAsync("agr_b", It.IsAny<int>(), It.IsAny<DateTime>(),
            It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Scheduler_NextChargeDateNull_Skipped()
    {
        var (svc, vipps, db) = BuildHarness();
        await db.Products.AddAsync(MakePrimaryProduct());
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_null", Status = SubscriptionStatus.Active,
            NextChargeDate = null
        };
        await db.Subscriptions.AddAsync(sub);
        await db.SaveChangesAsync();

        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        vipps.Verify(v => v.CreateChargeAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTime>(),
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
