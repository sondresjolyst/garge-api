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
    private Mock<ISecurityNotifier> Notifier { get; } = new();

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
        services.AddSingleton(Notifier.Object);

        var settings = new Mock<IAppSettingsCache>();
        settings.Setup(s => s.GetAsync()).ReturnsAsync(new AppSettings { Id = 1, VippsTestMode = testMode });
        services.AddSingleton(settings.Object);

        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var svc = new SubscriptionChargeSchedulerService(scopeFactory,
            Options.Create(new VippsSettings
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
            UserId = "user-1", ProductId = 1, UnitPriceInOre = 29900,
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
            UserId = "user-1", ProductId = 1, UnitPriceInOre = 29900, VippsAgreementId = "agr_sched", Status = SubscriptionStatus.Active,
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

    private static async Task AddAdminAsync(ApplicationDbContext db, string userId = "admin-1")
    {
        if (!await db.Roles.AnyAsync(r => r.Name == "Admin"))
            db.Roles.Add(new Microsoft.AspNetCore.Identity.IdentityRole { Id = "role-admin", Name = "Admin", NormalizedName = "ADMIN" });
        db.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<string> { UserId = userId, RoleId = "role-admin" });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Scheduler_PostRefused_TellsEveryAdmin()
    {
        var (svc, vipps, db) = BuildHarness();
        await AddAdminAsync(db, "admin-1");
        await AddAdminAsync(db, "admin-2");
        // A user with another role is not an admin.
        db.Roles.Add(new Microsoft.AspNetCore.Identity.IdentityRole { Id = "role-default", Name = "Default", NormalizedName = "DEFAULT" });
        db.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<string> { UserId = "user-1", RoleId = "role-default" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var sub = await AddDueSubAsync(db);
        vipps.Setup(v => v.CreateChargeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTime>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()))
            .ThrowsAsync(new HttpRequestException("refused"));
        Notifier.Setup(n => n.NotifyUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        foreach (var admin in new[] { "admin-1", "admin-2" })
            Notifier.Verify(n => n.NotifyUserAsync(admin, "Vipps refused a charge",
                It.Is<string>(m => m.Contains($"subscription #{sub.Id} (Garge Basic)") && m.Contains("299.00 kr")
                    && m.Contains(LocalTime.Format(sub.NextChargeDate!.Value, "yyyy-MM-dd"))),
                $"garge-charge-refused-{sub.Id}", It.IsAny<CancellationToken>()), Times.Once);
        Notifier.Verify(n => n.NotifyUserAsync("user-1", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Scheduler_PostAccepted_TellsNobody()
    {
        var (svc, vipps, db) = BuildHarness();
        await AddAdminAsync(db);
        await AddDueSubAsync(db);
        vipps.Setup(v => v.CreateChargeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTime>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()))
            .ReturnsAsync(new VippsCreateChargeResponse());

        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        Notifier.Verify(n => n.NotifyUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Scheduler_AlertThatFails_DoesNotStopTheOtherCharges()
    {
        var (svc, vipps, db) = BuildHarness();
        await AddAdminAsync(db);
        await db.Products.AddAsync(MakePrimaryProduct(), TestContext.Current.CancellationToken);
        var due = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(3), DateTimeKind.Utc);
        db.Subscriptions.AddRange(
            new Subscription { UserId = "u1", ProductId = 1, UnitPriceInOre = 29900, VippsAgreementId = "agr_bad", Status = SubscriptionStatus.Active, NextChargeDate = due },
            new Subscription { UserId = "u2", ProductId = 1, UnitPriceInOre = 29900, VippsAgreementId = "agr_good", Status = SubscriptionStatus.Active, NextChargeDate = due });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        vipps.Setup(v => v.CreateChargeAsync("agr_bad", It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()))
            .ThrowsAsync(new HttpRequestException("refused"));
        Notifier.Setup(n => n.NotifyUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("push down"));

        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        vipps.Verify(v => v.CreateChargeAsync("agr_good", It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()), Times.Once);
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

    [Theory]
    [InlineData(0, 39900)]
    [InlineData(1, 29900)]
    public async Task Scheduler_ChargesANotifiedNewPrice_FromItsEffectiveDueDate(int daysAfterDue, int expected)
    {
        var (svc, vipps, db) = BuildHarness();
        var sub = await AddDueSubAsync(db);
        var change = new PriceChange { ProductId = 1, OldPriceInOre = 29900, NewPriceInOre = 39900, EffectiveFrom = sub.NextChargeDate!.Value };
        db.PriceChanges.Add(change);
        db.PriceChangeNotices.Add(new PriceChangeNotice
        {
            PriceChange = change, SubscriptionId = sub.Id, NotifiedAt = DateTime.UtcNow.AddDays(-30),
            EffectiveFrom = sub.NextChargeDate.Value.AddDays(daysAfterDue)
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        vipps.Verify(v => v.CreateChargeAsync("agr_sched", expected, sub.NextChargeDate.Value, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()), Times.Once);
    }

    private static HttpRequestException Refused() => new("refused", null, System.Net.HttpStatusCode.BadRequest);

    private void SetupCreate(Mock<IVippsService> vipps, Exception ex) =>
        vipps.Setup(v => v.CreateChargeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTime>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()))
            .ThrowsAsync(ex);

    // Moves the last refusal a day back, as if the next daily run had come.
    private static async Task ADayLaterAsync(ApplicationDbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries<Subscription>().ToList())
            await entry.ReloadAsync();
        foreach (var s in await db.Subscriptions.Where(s => s.LastChargeRefusedAt != null).ToListAsync())
            s.LastChargeRefusedAt = s.LastChargeRefusedAt!.Value.AddDays(-1);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Scheduler_RefusedCreate_CountsAsAFailedAttempt_AndTheNextRunUsesANewKey()
    {
        var (svc, vipps, db) = BuildHarness();
        var sub = await AddDueSubAsync(db);
        SetupCreate(vipps, Refused());

        await svc.ScheduleDueChargesAsync(CancellationToken.None);
        await db.Entry(sub).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, sub.FailedChargeAttempts);
        Assert.Null(sub.LastChargeKey);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.NotNull(sub.LastChargeRefusedAt);

        await ADayLaterAsync(db);
        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        var due = sub.NextChargeDate!.Value;
        vipps.Verify(v => v.CreateChargeAsync("agr_sched", It.IsAny<int>(), due, It.IsAny<string>(), SubscriptionCharges.Key(sub.Id, due, 0), false), Times.Once);
        vipps.Verify(v => v.CreateChargeAsync("agr_sched", It.IsAny<int>(), due, It.IsAny<string>(), SubscriptionCharges.Key(sub.Id, due, 1), false), Times.Once);
    }

    [Fact]
    public async Task Scheduler_RefusedCreates_StopTheSubscriptionAtTheLimit()
    {
        var (svc, vipps, db) = BuildHarness();
        var sub = await AddDueSubAsync(db);
        SetupCreate(vipps, Refused());

        for (var run = 0; run < SubscriptionCharges.MaxAttempts + 2; run++)
        {
            await svc.ScheduleDueChargesAsync(CancellationToken.None);
            await ADayLaterAsync(db);
        }

        vipps.Verify(v => v.CreateChargeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTime>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()), Times.Exactly(SubscriptionCharges.MaxAttempts));
        vipps.Verify(v => v.CancelAgreementAsync("agr_sched", $"cancel-{sub.Id}", false), Times.Once);
        await db.Entry(sub).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SubscriptionStatus.Stopped, sub.Status);
        Assert.Equal(SubscriptionCharges.MaxAttempts, sub.FailedChargeAttempts);
        Email.Verify(e => e.SendStoppedForNonPaymentAsync(sub.Id), Times.Once);
    }

    [Fact]
    public async Task Scheduler_TheLastRefusedAttempt_StopsTheSubscriptionInTheSameRun()
    {
        var (svc, vipps, db) = BuildHarness();
        var sub = await AddDueSubAsync(db, failedAttempts: SubscriptionCharges.MaxAttempts - 1);
        SetupCreate(vipps, Refused());

        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        await db.Entry(sub).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SubscriptionStatus.Stopped, sub.Status);
        vipps.Verify(v => v.CancelAgreementAsync("agr_sched", $"cancel-{sub.Id}", false), Times.Once);
    }

    [Fact]
    public async Task Scheduler_RefusedCreate_ClearsAnOlderFailureReason()
    {
        var (svc, vipps, db) = BuildHarness();
        var sub = await AddDueSubAsync(db, failedAttempts: 1);
        sub.LastChargeFailureReason = SubscriptionCharges.AmountTooHigh;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        SetupCreate(vipps, Refused());

        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        await db.Entry(sub).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, sub.FailedChargeAttempts);
        Assert.Null(sub.LastChargeFailureReason);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.InternalServerError)]
    [InlineData(System.Net.HttpStatusCode.Conflict)]
    [InlineData(System.Net.HttpStatusCode.TooManyRequests)]
    [InlineData(System.Net.HttpStatusCode.RequestTimeout)]
    public async Task Scheduler_CreateThatMayHaveGoneThrough_IsNotCounted_AndKeepsItsKey(System.Net.HttpStatusCode status)
    {
        var (svc, vipps, db) = BuildHarness();
        var sub = await AddDueSubAsync(db);
        SetupCreate(vipps, new HttpRequestException("maybe", null, status));

        await svc.ScheduleDueChargesAsync(CancellationToken.None);
        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        await db.Entry(sub).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, sub.FailedChargeAttempts);
        vipps.Verify(v => v.CreateChargeAsync("agr_sched", It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<string>(),
            SubscriptionCharges.Key(sub.Id, sub.NextChargeDate!.Value, 0), false), Times.Exactly(2));
    }

    [Fact]
    public async Task Scheduler_ACreateThatKeepsFailing_TellsTheAdminsOnce()
    {
        var (svc, vipps, db) = BuildHarness();
        await AddAdminAsync(db);
        await AddDueSubAsync(db);
        SetupCreate(vipps, new HttpRequestException("down", null, System.Net.HttpStatusCode.ServiceUnavailable));
        Notifier.Setup(n => n.NotifyUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        for (var run = 0; run < 4; run++)
        {
            await svc.ScheduleDueChargesAsync(CancellationToken.None);
            await ADayLaterAsync(db);
        }

        Notifier.Verify(n => n.NotifyUserAsync("admin-1", "Vipps refused a charge", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Scheduler_RefusedCreates_TellTheAdminsOncePerAttempt()
    {
        var (svc, vipps, db) = BuildHarness();
        await AddAdminAsync(db);
        var sub = await AddDueSubAsync(db);
        SetupCreate(vipps, Refused());
        var messages = new List<string>();
        Notifier.Setup(n => n.NotifyUserAsync("admin-1", "Vipps refused a charge", Capture.In(messages), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        for (var run = 0; run < SubscriptionCharges.MaxAttempts + 2; run++)
        {
            await svc.ScheduleDueChargesAsync(CancellationToken.None);
            await ADayLaterAsync(db);
        }

        Assert.Equal(SubscriptionCharges.MaxAttempts, messages.Count);
        Assert.Contains($"attempt 1 of {SubscriptionCharges.MaxAttempts}", messages[0]);
        Assert.Contains($"subscription #{sub.Id}", messages[0]);
        Assert.Contains($"last of {SubscriptionCharges.MaxAttempts} attempts", messages[^1]);
    }

    [Fact]
    public async Task Scheduler_OverdueCharge_IsPostedForItsOwnPeriod()
    {
        // Vipps wants a due date at least a day ahead. The Vipps client moves an earlier date up to the
        // earliest one Vipps takes, so the key and the period stay those of the date the charge was due.
        var (svc, vipps, db) = BuildHarness();
        await db.Products.AddAsync(MakePrimaryProduct(), TestContext.Current.CancellationToken);
        var overdue = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddMonths(-4), DateTimeKind.Utc);
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1, UnitPriceInOre = 5000, VippsAgreementId = "agr_overdue",
            Status = SubscriptionStatus.Active, NextChargeDate = overdue
        };
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        vipps.Verify(v => v.CreateChargeAsync("agr_overdue", 5000, overdue, "Garge Basic", SubscriptionCharges.Key(sub.Id, overdue, 0), false), Times.Once);
    }

    [Fact]
    public async Task Scheduler_SubscriptionsOfADeletedAccount_AreNotCharged_AndTheirAgreementsAreStopped()
    {
        var (svc, vipps, db) = BuildHarness();
        await db.Products.AddAsync(MakePrimaryProduct(), TestContext.Current.CancellationToken);
        await db.Products.AddAsync(new Product { Id = 2, Name = "Extra sensor", PriceInOre = 2000, Interval = BillingInterval.Monthly, Type = ProductType.AddOn, IsActive = true }, TestContext.Current.CancellationToken);
        var due = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(-1), DateTimeKind.Utc);
        var primary = new Subscription { UserId = "user-1", ProductId = 1, UnitPriceInOre = 5000, VippsAgreementId = "agr_primary", Status = SubscriptionStatus.Active, NextChargeDate = due, IsTest = true };
        var addOn = new Subscription { UserId = "user-1", ProductId = 2, UnitPriceInOre = 2000, VippsAgreementId = "agr_addon", Status = SubscriptionStatus.Active, NextChargeDate = due, IsTest = true };
        db.Subscriptions.AddRange(primary, addOn);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var user = new User { Id = "user-1", UserName = "u", Email = "u@test.com", FirstName = "F", LastName = "L" };
        await new AccountDataEraser(db, new AnonymizationService(db, NullLogger<AnonymizationService>.Instance), new Mock<IDeviceOwnershipService>().Object)
            .EraseAsync(user, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        // The stop after the commit failed, so the scheduler is the one to stop the agreements.

        await svc.ScheduleDueChargesAsync(CancellationToken.None);
        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        vipps.Verify(v => v.CreateChargeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTime>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()), Times.Never);
        vipps.Verify(v => v.CancelAgreementAsync("agr_primary", It.Is<string>(k => k.StartsWith($"stop-{primary.Id}-")), true), Times.Once);
        vipps.Verify(v => v.CancelAgreementAsync("agr_addon", It.Is<string>(k => k.StartsWith($"stop-{addOn.Id}-")), true), Times.Once);
        foreach (var sub in new[] { primary, addOn })
        {
            await db.Entry(sub).ReloadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(SubscriptionStatus.Stopped, sub.Status);
            Assert.False(sub.AgreementStopPending);
        }
        Email.Verify(e => e.SendStoppedForNonPaymentAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Scheduler_AgreementStopThatFails_IsTriedAgainOnTheNextRun()
    {
        var (svc, vipps, db) = BuildHarness();
        var sub = await AddDueSubAsync(db);
        sub.Status = SubscriptionStatus.Stopped;
        sub.AgreementStopPending = true;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        vipps.SetupSequence(v => v.CancelAgreementAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()))
            .ThrowsAsync(new HttpRequestException("down", null, System.Net.HttpStatusCode.BadGateway))
            .Returns(Task.CompletedTask);

        await svc.ScheduleDueChargesAsync(CancellationToken.None);
        await db.Entry(sub).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.True(sub.AgreementStopPending);
        await svc.ScheduleDueChargesAsync(CancellationToken.None);
        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        await db.Entry(sub).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.False(sub.AgreementStopPending);
        vipps.Verify(v => v.CancelAgreementAsync("agr_sched", It.Is<string>(k => k.StartsWith($"stop-{sub.Id}-")), false), Times.Exactly(2));
    }

    [Fact]
    public async Task Scheduler_RefusedCharge_WaitsADayBeforeItsNextAttempt()
    {
        // A restart runs a sweep at once, so restarts on one day must not use up the attempts.
        var (svc, vipps, db) = BuildHarness();
        var sub = await AddDueSubAsync(db);
        SetupCreate(vipps, Refused());

        await svc.ScheduleDueChargesAsync(CancellationToken.None);
        await svc.ScheduleDueChargesAsync(CancellationToken.None);
        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        vipps.Verify(v => v.CreateChargeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTime>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()), Times.Once);
        await db.Entry(sub).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, sub.FailedChargeAttempts);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Unauthorized)]
    [InlineData(System.Net.HttpStatusCode.Forbidden)]
    public async Task Scheduler_CredentialsVippsDoesNotAccept_AreNotCountedAgainstTheSubscriber(System.Net.HttpStatusCode status)
    {
        var (svc, vipps, db) = BuildHarness();
        var sub = await AddDueSubAsync(db);
        SetupCreate(vipps, new HttpRequestException("auth", null, status));

        for (var run = 0; run < SubscriptionCharges.MaxAttempts + 1; run++)
            await svc.ScheduleDueChargesAsync(CancellationToken.None);

        await db.Entry(sub).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, sub.FailedChargeAttempts);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Email.Verify(e => e.SendChargeFailedAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Scheduler_RefusedCharge_TellsTheSubscriber_UntilTheLastAttempt()
    {
        var (svc, vipps, db) = BuildHarness();
        var sub = await AddDueSubAsync(db);
        SetupCreate(vipps, Refused());

        for (var run = 0; run < SubscriptionCharges.MaxAttempts; run++)
        {
            await svc.ScheduleDueChargesAsync(CancellationToken.None);
            await ADayLaterAsync(db);
        }

        Email.Verify(e => e.SendChargeFailedAsync(sub.Id), Times.Exactly(SubscriptionCharges.MaxAttempts - 1));
        Email.Verify(e => e.SendStoppedForNonPaymentAsync(sub.Id), Times.Once);
    }

    [Fact]
    public async Task Scheduler_SubscriptionStoppedDuringTheRun_IsNotCharged()
    {
        var (svc, vipps, db) = BuildHarness();
        await db.Products.AddAsync(MakePrimaryProduct(), TestContext.Current.CancellationToken);
        var due = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(2), DateTimeKind.Utc);
        var first = new Subscription { UserId = "u1", ProductId = 1, UnitPriceInOre = 5000, VippsAgreementId = "agr_first", Status = SubscriptionStatus.Active, NextChargeDate = due };
        var second = new Subscription { UserId = "u2", ProductId = 1, UnitPriceInOre = 5000, VippsAgreementId = "agr_second", Status = SubscriptionStatus.Active, NextChargeDate = due };
        db.Subscriptions.AddRange(first, second);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        // The second account is deleted while the first charge is posted.
        vipps.Setup(v => v.CreateChargeAsync("agr_first", It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()))
            .Callback(() => { second.Status = SubscriptionStatus.Stopped; db.SaveChanges(); })
            .ReturnsAsync(new VippsCreateChargeResponse());

        await svc.ScheduleDueChargesAsync(CancellationToken.None);

        vipps.Verify(v => v.CreateChargeAsync("agr_second", It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()), Times.Never);
    }

    [Fact]
    public async Task Scheduler_AlertThatReachedNoAdmin_IsSentAgainOnTheNextRun()
    {
        var (svc, vipps, db) = BuildHarness();
        await AddAdminAsync(db);
        await AddDueSubAsync(db);
        SetupCreate(vipps, new HttpRequestException("down", null, System.Net.HttpStatusCode.ServiceUnavailable));
        Notifier.SetupSequence(n => n.NotifyUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false)
            .ReturnsAsync(true)
            .ReturnsAsync(true);

        for (var run = 0; run < 3; run++)
            await svc.ScheduleDueChargesAsync(CancellationToken.None);

        Notifier.Verify(n => n.NotifyUserAsync("admin-1", "Vipps refused a charge", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
