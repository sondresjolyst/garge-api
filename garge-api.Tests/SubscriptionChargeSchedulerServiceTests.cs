using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Models.Subscription;
using garge_api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace garge_api.Tests;

public class SubscriptionChargeSchedulerServiceTests
{
    private Mock<ISubscriptionEmailService> Email { get; } = new();

    private (SubscriptionChargeSchedulerService svc, Mock<IVippsService> vipps, ApplicationDbContext db)
        BuildHarness(bool testMode = false, bool testConfigured = true)
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));

        var vipps = new Mock<IVippsService>();
        services.AddSingleton(vipps.Object);
        services.AddSingleton(Email.Object);

        var settings = new Mock<IAppSettingsCache>();
        settings.Setup(s => s.GetAsync()).ReturnsAsync(new AppSettings { Id = 1, VippsTestMode = testMode });
        services.AddSingleton(settings.Object);

        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var svc = new SubscriptionChargeSchedulerService(scopeFactory,
            Options.Create(new VippsOptions
            {
                BaseUrl = "https://api.vipps.no",
                ClientId = "id", ClientSecret = "s", SubscriptionKey = "k", MerchantSerialNumber = "msn-prod",
                TestClientId = testConfigured ? "tid" : "", TestClientSecret = "ts", TestSubscriptionKey = "tk", TestMerchantSerialNumber = "msn-test"
            }),
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
        await db.Products.AddAsync(MakePrimaryProduct(), TestContext.Current.CancellationToken);
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_due", Status = SubscriptionStatus.Active,
            NextChargeDate = dueDate
        };
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        vipps.Verify(v => v.CreateChargeAsync(
            "agr_due", 29900, dueDate, "Garge Basic",
            $"charge-{sub.Id}-{dueDate.Ticks}", false), Times.Once);
    }

    [Fact]
    public async Task Scheduler_ActiveSubBeyondLookahead_NotCharged()
    {
        var (svc, vipps, db) = BuildHarness();
        await db.Products.AddAsync(MakePrimaryProduct(), TestContext.Current.CancellationToken);
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_far", Status = SubscriptionStatus.Active,
            NextChargeDate = DateTime.UtcNow.AddDays(20)
        };
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        vipps.Verify(v => v.CreateChargeAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTime>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()), Times.Never);
    }

    [Theory]
    [InlineData(SubscriptionStatus.Pending)]
    [InlineData(SubscriptionStatus.Stopped)]
    [InlineData(SubscriptionStatus.Expired)]
    public async Task Scheduler_NonActiveStatuses_Skipped(SubscriptionStatus status)
    {
        var (svc, vipps, db) = BuildHarness();
        await db.Products.AddAsync(MakePrimaryProduct(), TestContext.Current.CancellationToken);
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_inactive", Status = status,
            NextChargeDate = DateTime.UtcNow.AddDays(1)
        };
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        vipps.Verify(v => v.CreateChargeAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTime>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()), Times.Never);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Scheduler_ChargesEachSubscriptionInItsOwnEnvironment_WhateverTheTestMode(bool testMode, bool subIsTest)
    {
        // Switching test mode must not pause live billing, or charge a live agreement with test credentials.
        var (svc, vipps, db) = BuildHarness(testMode: testMode);
        await db.Products.AddAsync(MakePrimaryProduct(), TestContext.Current.CancellationToken);
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_env", Status = SubscriptionStatus.Active,
            NextChargeDate = DateTime.UtcNow.AddDays(1),
            IsTest = subIsTest
        };
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        vipps.Verify(v => v.CreateChargeAsync(
            "agr_env", It.IsAny<int>(), It.IsAny<DateTime>(),
            It.IsAny<string>(), It.IsAny<string>(), subIsTest), Times.Once);
        vipps.Verify(v => v.CreateChargeAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTime>(),
            It.IsAny<string>(), It.IsAny<string>(), !subIsTest), Times.Never);
    }

    [Fact]
    public async Task Scheduler_OneSubThrows_OthersStillProcessed()
    {
        var (svc, vipps, db) = BuildHarness();
        await db.Products.AddAsync(MakePrimaryProduct(), TestContext.Current.CancellationToken);
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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        vipps.Setup(v => v.CreateChargeAsync("agr_a", It.IsAny<int>(), It.IsAny<DateTime>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()))
            .ThrowsAsync(new HttpRequestException("Vipps boom"));
        vipps.Setup(v => v.CreateChargeAsync("agr_b", It.IsAny<int>(), It.IsAny<DateTime>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()))
            .ReturnsAsync(new VippsCreateChargeResponse { ChargeId = "chg_b" });

        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        vipps.Verify(v => v.CreateChargeAsync("agr_a", It.IsAny<int>(), It.IsAny<DateTime>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()), Times.Once);
        vipps.Verify(v => v.CreateChargeAsync("agr_b", It.IsAny<int>(), It.IsAny<DateTime>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()), Times.Once);
    }

    [Fact]
    public async Task Scheduler_NextChargeDateNull_Skipped()
    {
        var (svc, vipps, db) = BuildHarness();
        await db.Products.AddAsync(MakePrimaryProduct(), TestContext.Current.CancellationToken);
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_null", Status = SubscriptionStatus.Active,
            NextChargeDate = null
        };
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        vipps.Verify(v => v.CreateChargeAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTime>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()), Times.Never);
    }

    private static async Task<Subscription> AddDueSubAsync(ApplicationDbContext db, bool isTest = false, int failedAttempts = 0)
    {
        await db.Products.AddAsync(MakePrimaryProduct(), TestContext.Current.CancellationToken);
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1, VippsAgreementId = "agr_sched", Status = SubscriptionStatus.Active,
            NextChargeDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(3), DateTimeKind.Utc), IsTest = isTest,
            FailedChargeAttempts = failedAttempts
        };
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return sub;
    }

    [Fact]
    public async Task Scheduler_PostsEachChargeOnce()
    {
        var (svc, vipps, db) = BuildHarness();
        var sub = await AddDueSubAsync(db);

        await svc.ScheduleDueChargesAsync(CancellationToken.None);
        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        var key = SubscriptionCharges.Key(sub.Id, sub.NextChargeDate!.Value, 0);
        vipps.Verify(v => v.CreateChargeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTime>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()), Times.Once);
        vipps.Verify(v => v.CreateChargeAsync("agr_sched", 29900, sub.NextChargeDate!.Value, "Garge Basic", key, false), Times.Once);
        await db.Entry(sub).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(key, sub.LastChargeKey);
    }

    [Fact]
    public async Task Scheduler_AfterAFailedAttempt_PostsANewChargeWithItsOwnKey()
    {
        var (svc, vipps, db) = BuildHarness();
        var sub = await AddDueSubAsync(db, failedAttempts: 1);
        sub.LastChargeKey = SubscriptionCharges.Key(sub.Id, sub.NextChargeDate!.Value, 0);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        vipps.Verify(v => v.CreateChargeAsync("agr_sched", It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<string>(),
            $"charge-{sub.Id}-{sub.NextChargeDate!.Value.Ticks}-r1", false), Times.Once);
    }

    [Fact]
    public async Task Scheduler_PostFails_IsTriedAgainOnTheNextSweep()
    {
        var (svc, vipps, db) = BuildHarness();
        var sub = await AddDueSubAsync(db);
        vipps.SetupSequence(v => v.CreateChargeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTime>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()))
            .ThrowsAsync(new HttpRequestException("down"))
            .ReturnsAsync(new VippsCreateChargeResponse());

        await svc.ScheduleDueChargesAsync(CancellationToken.None);
        await db.Entry(sub).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Null(sub.LastChargeKey);
        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        vipps.Verify(v => v.CreateChargeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTime>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Scheduler_EnvironmentWithoutCredentials_IsSkipped()
    {
        var (svc, vipps, db) = BuildHarness(testConfigured: false);
        await AddDueSubAsync(db, isTest: true);

        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        vipps.Verify(v => v.CreateChargeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTime>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scheduler_SubscriptionWhoseLastAttemptFailed_IsStoppedNotCharged(bool isTest)
    {
        var (svc, vipps, db) = BuildHarness();
        var sub = await AddDueSubAsync(db, isTest: isTest, failedAttempts: SubscriptionCharges.MaxAttempts);

        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        vipps.Verify(v => v.CancelAgreementAsync("agr_sched", $"cancel-{sub.Id}", isTest), Times.Once);
        vipps.Verify(v => v.CreateChargeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTime>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()), Times.Never);
        await db.Entry(sub).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SubscriptionStatus.Stopped, sub.Status);
        Email.Verify(e => e.SendStoppedForNonPaymentAsync(sub.Id), Times.Once);
    }

    [Fact]
    public async Task Scheduler_CancelOfAnUnpaidSubscriptionFails_IsTriedAgainOnTheNextSweep()
    {
        var (svc, vipps, db) = BuildHarness();
        var sub = await AddDueSubAsync(db, failedAttempts: SubscriptionCharges.MaxAttempts);
        vipps.SetupSequence(v => v.CancelAgreementAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()))
            .ThrowsAsync(new HttpRequestException("down"))
            .Returns(Task.CompletedTask);

        await svc.ScheduleDueChargesAsync(CancellationToken.None);
        await db.Entry(sub).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Email.Verify(e => e.SendStoppedForNonPaymentAsync(It.IsAny<int>()), Times.Never);

        await svc.ScheduleDueChargesAsync(CancellationToken.None);
        await db.Entry(sub).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SubscriptionStatus.Stopped, sub.Status);
        vipps.Verify(v => v.CreateChargeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTime>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()), Times.Never);
    }

    [Fact]
    public async Task Scheduler_ChargesThePriceTimesTheQuantity()
    {
        var (svc, vipps, db) = BuildHarness();
        await AddDueSubAsync(db);
        var sub = await db.Subscriptions.SingleAsync(TestContext.Current.CancellationToken);
        sub.Quantity = 2;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        vipps.Verify(v => v.CreateChargeAsync("agr_sched", 59800, It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()), Times.Once);
    }
}
