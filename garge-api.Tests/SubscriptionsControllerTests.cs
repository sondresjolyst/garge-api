using garge_api.Controllers;
using garge_api.Dtos.Subscription;
using Microsoft.AspNetCore.Http;
using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Models.Subscription;
using garge_api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Text.Json;
using Xunit;

namespace garge_api.Tests;

public class SubscriptionsControllerTests : ControllerTestBase
{
    private static Mock<IVippsService> MockVipps()
    {
        var mock = new Mock<IVippsService>();
        mock.Setup(v => v.VerifyWebhookSignature(
                It.IsAny<HttpRequest>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((HttpRequest req, string body, string secret) =>
                string.IsNullOrEmpty(secret)
                    ? WebhookVerifyResult.MissingSecret
                    : (req.Headers["X-Test-Valid"] == "1"
                        ? WebhookVerifyResult.Valid
                        : WebhookVerifyResult.BadSignature));
        return mock;
    }

    private static Mock<IWebhookSecretProtector> MockProtector()
    {
        var m = new Mock<IWebhookSecretProtector>();
        m.Setup(p => p.Protect(It.IsAny<string>())).Returns<string>(s => s);
        m.Setup(p => p.Unprotect(It.IsAny<string>())).Returns<string>(s => s);
        return m;
    }

    private static Mock<IAppSettingsCache> MockSettingsCache(AppSettings? settings = null)
    {
        var m = new Mock<IAppSettingsCache>();
        m.Setup(c => c.GetAsync()).ReturnsAsync(settings ?? new AppSettings { Id = 1 });
        return m;
    }

    private static IOptions<AppOptions> AppOpts() => Options.Create(new AppOptions
    {
        FrontendBaseUrl = "https://www.garge.no",
        ApiBaseUrl = "https://garge-api.prod.tumogroup.com"
    });

    private SubscriptionsController CreateController(
        ApplicationDbContext db, string userId = "user-1",
        Mock<IVippsService>? vipps = null, AppSettings? settings = null,
        IInvoiceService? invoice = null, ISubscriptionEmailService? subEmail = null)
    {
        var push = new Mock<IWebPushService>();
        push.Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var ctrl = new SubscriptionsController(
            db, (vipps ?? MockVipps()).Object,
            invoice ?? new Mock<IInvoiceService>().Object,
            subEmail ?? new Mock<ISubscriptionEmailService>().Object,
            MockSettingsCache(settings).Object,
            MockProtector().Object,
            push.Object,
            AppOpts(),
            MockMapper.Object,
            NullLogger<SubscriptionsController>.Instance);
        ctrl.ControllerContext = MakeControllerContext(userId);
        return ctrl;
    }

    [Theory]
    [InlineData("recurring.agreement-activated.v1", SubscriptionStatus.Active)]
    [InlineData("recurring.agreement-stopped.v1",   SubscriptionStatus.Stopped)]
    [InlineData("recurring.agreement-expired.v1",   SubscriptionStatus.Expired)]
    [InlineData("recurring.agreement-rejected.v1",  SubscriptionStatus.Stopped)]
    [InlineData("unknown.event.v1",                 SubscriptionStatus.Pending)]
    public async Task Webhook_KnownEventTypes_UpdatesStatusCorrectly(
        string eventType, SubscriptionStatus expected)
    {
        using var db = CreateDbContext();
        await db.AppSettings.AddAsync(new AppSettings { Id = 1, VippsSubscriptionWebhookSecret = "secret" });
        var sub = new Subscription
        {
            UserId = "user-1",
            ProductId = 1,
            VippsAgreementId = "agr_test",
            Status = SubscriptionStatus.Pending
        };
        await db.Subscriptions.AddAsync(sub);
        await db.SaveChangesAsync();

        var payload = new VippsAgreementWebhookDto
        {
            AgreementId = "agr_test",
            EventId = $"evt-{eventType}",
            EventType = eventType,
            Occurred = DateTime.UtcNow
        };
        var body = JsonSerializer.Serialize(payload);

        var ctrl = CreateController(db, settings: new AppSettings { Id = 1, VippsSubscriptionWebhookSecret = "secret" });
        SetupValidWebhookRequest(ctrl, body);

        var result = await ctrl.Webhook();

        Assert.IsType<OkResult>(result);
        var updated = await db.Subscriptions.FirstAsync();
        Assert.Equal(expected, updated.Status);
    }

    [Fact]
    public async Task Webhook_InvalidHmac_Returns401()
    {
        using var db = CreateDbContext();
        await db.SaveChangesAsync();

        var body = """{"agreementId":"agr_test","eventType":"recurring.agreement-activated.v1"}""";
        var ctrl = CreateController(db, settings: new AppSettings { Id = 1, VippsSubscriptionWebhookSecret = "correct" });
        SetupInvalidWebhookRequest(ctrl, body);

        var result = await ctrl.Webhook();

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task Webhook_ConcurrentDuplicates_OnlyOneRecorded()
    {
        using var db = CreateDbContext();
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_race", Status = SubscriptionStatus.Pending
        };
        await db.Subscriptions.AddAsync(sub);
        await db.SaveChangesAsync();

        var payload = new VippsAgreementWebhookDto
        {
            AgreementId = "agr_race",
            EventId = "evt-race",
            EventType = "recurring.agreement-activated.v1",
            Occurred = DateTime.UtcNow
        };
        var body = JsonSerializer.Serialize(payload);

        var settings = new AppSettings { Id = 1, VippsSubscriptionWebhookSecret = "secret" };
        var ctrl1 = CreateController(db, settings: settings);
        var ctrl2 = CreateController(db, settings: settings);
        SetupValidWebhookRequest(ctrl1, body);
        SetupValidWebhookRequest(ctrl2, body);

        var t1 = ctrl1.Webhook();
        var t2 = ctrl2.Webhook();
        await Task.WhenAll(t1, t2);

        Assert.Equal(1, await db.ProcessedWebhookEvents.CountAsync(e => e.Id == "evt-race"));
    }

    [Fact]
    public async Task Webhook_DuplicateEvent_SkipsSecondProcessing()
    {
        using var db = CreateDbContext();
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_dup", Status = SubscriptionStatus.Pending
        };
        await db.Subscriptions.AddAsync(sub);
        await db.SaveChangesAsync();

        var payload = new VippsAgreementWebhookDto
        {
            AgreementId = "agr_dup",
            EventId = "evt-1",
            EventType = "recurring.agreement-activated.v1",
            Occurred = DateTime.UtcNow
        };
        var body = JsonSerializer.Serialize(payload);

        var settings = new AppSettings { Id = 1, VippsSubscriptionWebhookSecret = "secret" };
        var ctrl1 = CreateController(db, settings: settings);
        SetupValidWebhookRequest(ctrl1, body);
        await ctrl1.Webhook();

        var ctrl2 = CreateController(db, settings: settings);
        SetupValidWebhookRequest(ctrl2, body);
        await ctrl2.Webhook();

        Assert.Equal(1, await db.ProcessedWebhookEvents.CountAsync(e => e.Id == "evt-1"));
    }

    [Fact]
    public async Task Webhook_ActivatedEvent_SetsStartDate()
    {
        using var db = CreateDbContext();
        var occurred = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_start",
            Status = SubscriptionStatus.Pending
        };
        await db.Subscriptions.AddAsync(sub);
        await db.SaveChangesAsync();

        var payload = new
        {
            agreementId = "agr_start",
            eventId = "evt-start",
            eventType = "recurring.agreement-activated.v1",
            occurred
        };
        var body = JsonSerializer.Serialize(payload);

        var ctrl = CreateController(db, settings: new AppSettings { Id = 1, VippsSubscriptionWebhookSecret = "secret" });
        SetupValidWebhookRequest(ctrl, body);
        await ctrl.Webhook();

        var updated = await db.Subscriptions.FirstAsync();
        Assert.Equal(occurred, updated.StartDate);
    }

    [Fact]
    public async Task Webhook_Activated_VippsReturnsAddress_PopulatesBillingAddress()
    {
        using var db = CreateDbContext();
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_addr1", Status = SubscriptionStatus.Pending
        };
        await db.Subscriptions.AddAsync(sub);
        await db.SaveChangesAsync();

        var vipps = MockVipps();
        vipps.Setup(v => v.GetAgreementAsync("agr_addr1"))
            .ReturnsAsync(new VippsAgreementResponse { Id = "agr_addr1", Sub = "sub-x" });
        vipps.Setup(v => v.GetUserInfoAsync("sub-x"))
            .ReturnsAsync(new VippsUserInfo
            {
                Address = new VippsAddress { Formatted = "Mårvegen 21a, 4347 Lye, Norway" }
            });

        var payload = new
        {
            agreementId = "agr_addr1",
            eventId = "evt-addr1",
            eventType = "recurring.agreement-activated.v1",
            occurred = DateTime.UtcNow
        };
        var ctrl = CreateController(db,
            settings: new AppSettings { Id = 1, VippsSubscriptionWebhookSecret = "secret" },
            vipps: vipps);
        SetupValidWebhookRequest(ctrl, JsonSerializer.Serialize(payload));
        await ctrl.Webhook();

        var updated = await db.Subscriptions.FirstAsync();
        Assert.Equal("Mårvegen 21a, 4347 Lye, Norway", updated.BillingAddress);
        Assert.Equal(SubscriptionStatus.Active, updated.Status);
    }

    [Fact]
    public async Task Webhook_Activated_NoSubReturned_LeavesBillingAddressNull()
    {
        using var db = CreateDbContext();
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_addr2", Status = SubscriptionStatus.Pending
        };
        await db.Subscriptions.AddAsync(sub);
        await db.SaveChangesAsync();

        var vipps = MockVipps();
        vipps.Setup(v => v.GetAgreementAsync("agr_addr2"))
            .ReturnsAsync(new VippsAgreementResponse { Id = "agr_addr2", Sub = null });

        var payload = new
        {
            agreementId = "agr_addr2",
            eventId = "evt-addr2",
            eventType = "recurring.agreement-activated.v1",
            occurred = DateTime.UtcNow
        };
        var ctrl = CreateController(db,
            settings: new AppSettings { Id = 1, VippsSubscriptionWebhookSecret = "secret" },
            vipps: vipps);
        SetupValidWebhookRequest(ctrl, JsonSerializer.Serialize(payload));
        await ctrl.Webhook();

        var updated = await db.Subscriptions.FirstAsync();
        Assert.Null(updated.BillingAddress);
        Assert.Equal(SubscriptionStatus.Active, updated.Status);
        vipps.Verify(v => v.GetUserInfoAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Webhook_Activated_GetUserInfoThrows_Swallowed_StatusStillActive()
    {
        using var db = CreateDbContext();
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_addr3", Status = SubscriptionStatus.Pending
        };
        await db.Subscriptions.AddAsync(sub);
        await db.SaveChangesAsync();

        var vipps = MockVipps();
        vipps.Setup(v => v.GetAgreementAsync("agr_addr3"))
            .ReturnsAsync(new VippsAgreementResponse { Id = "agr_addr3", Sub = "sub-y" });
        vipps.Setup(v => v.GetUserInfoAsync("sub-y"))
            .ThrowsAsync(new HttpRequestException("Vipps userinfo down"));

        var payload = new
        {
            agreementId = "agr_addr3",
            eventId = "evt-addr3",
            eventType = "recurring.agreement-activated.v1",
            occurred = DateTime.UtcNow
        };
        var ctrl = CreateController(db,
            settings: new AppSettings { Id = 1, VippsSubscriptionWebhookSecret = "secret" },
            vipps: vipps);
        SetupValidWebhookRequest(ctrl, JsonSerializer.Serialize(payload));
        var result = await ctrl.Webhook();

        Assert.IsType<OkResult>(result);
        var updated = await db.Subscriptions.FirstAsync();
        Assert.Null(updated.BillingAddress);
        Assert.Equal(SubscriptionStatus.Active, updated.Status);
    }

    [Fact]
    public async Task Webhook_Activated_BillingAddressAlreadySet_NotOverwritten()
    {
        using var db = CreateDbContext();
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_addr4",
            Status = SubscriptionStatus.Pending,
            BillingAddress = "Existing address"
        };
        await db.Subscriptions.AddAsync(sub);
        await db.SaveChangesAsync();

        var vipps = MockVipps();
        var payload = new
        {
            agreementId = "agr_addr4",
            eventId = "evt-addr4",
            eventType = "recurring.agreement-activated.v1",
            occurred = DateTime.UtcNow
        };
        var ctrl = CreateController(db,
            settings: new AppSettings { Id = 1, VippsSubscriptionWebhookSecret = "secret" },
            vipps: vipps);
        SetupValidWebhookRequest(ctrl, JsonSerializer.Serialize(payload));
        await ctrl.Webhook();

        var updated = await db.Subscriptions.FirstAsync();
        Assert.Equal("Existing address", updated.BillingAddress);
        vipps.Verify(v => v.GetAgreementAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Webhook_ChargeCaptured_SetsNextChargeDateByInterval()
    {
        using var db = CreateDbContext();
        var occurred = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        await db.Products.AddAsync(MakePrimaryProduct());
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_charge", Status = SubscriptionStatus.Active
        };
        await db.Subscriptions.AddAsync(sub);
        await db.SaveChangesAsync();

        var payload = new
        {
            agreementId = "agr_charge",
            eventId = "evt-charge",
            eventType = "recurring.charge-captured.v1",
            occurred
        };
        var body = JsonSerializer.Serialize(payload);

        var ctrl = CreateController(db, settings: new AppSettings { Id = 1, VippsSubscriptionWebhookSecret = "secret" });
        SetupValidWebhookRequest(ctrl, body);
        await ctrl.Webhook();

        var updated = await db.Subscriptions.FirstAsync();
        Assert.Equal(occurred.AddMonths(1), updated.NextChargeDate);
    }

    [Fact]
    public async Task Webhook_ChargeCaptured_DoesNotCallCreateChargeAsync_SchedulerIsSoleSource()
    {
        using var db = CreateDbContext();
        var occurred = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        await db.Products.AddAsync(MakePrimaryProduct());
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_no_post", Status = SubscriptionStatus.Active
        };
        await db.Subscriptions.AddAsync(sub);
        await db.SaveChangesAsync();

        var vipps = MockVipps();

        var payload = new
        {
            agreementId = "agr_no_post",
            eventId = "evt-no-post",
            eventType = "recurring.charge-captured.v1",
            chargeId = "chg_p",
            occurred
        };
        var ctrl = CreateController(db,
            settings: new AppSettings { Id = 1, VippsSubscriptionWebhookSecret = "secret" },
            vipps: vipps);
        SetupValidWebhookRequest(ctrl, JsonSerializer.Serialize(payload));
        await ctrl.Webhook();

        vipps.Verify(v => v.CreateChargeAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTime>(),
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        var updated = await db.Subscriptions.FirstAsync();
        Assert.Equal(occurred.AddMonths(1), updated.NextChargeDate);
    }

    [Fact]
    public async Task Webhook_ChargeCaptured_NullOccurred_LeavesNextChargeDateNull()
    {
        using var db = CreateDbContext();
        await db.Products.AddAsync(MakePrimaryProduct());
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_noocc", Status = SubscriptionStatus.Active
        };
        await db.Subscriptions.AddAsync(sub);
        await db.SaveChangesAsync();

        var payload = new
        {
            agreementId = "agr_noocc",
            eventId = "evt-noocc",
            eventType = "recurring.charge-captured.v1",
            chargeId = "chg_n"
        };
        var ctrl = CreateController(db,
            settings: new AppSettings { Id = 1, VippsSubscriptionWebhookSecret = "secret" });
        SetupValidWebhookRequest(ctrl, JsonSerializer.Serialize(payload));
        await ctrl.Webhook();

        var updated = await db.Subscriptions.FirstAsync();
        Assert.Null(updated.NextChargeDate);
    }

    [Fact]
    public async Task Webhook_ChargeCaptured_YearlyProduct_NextChargeDateIsPlusOneYear()
    {
        using var db = CreateDbContext();
        var occurred = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        await db.Products.AddAsync(new Product
        {
            Id = 1, Name = "Garge Yearly", PriceInOre = 99900,
            Interval = BillingInterval.Yearly, Type = ProductType.Primary, IsActive = true
        });
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_yr", Status = SubscriptionStatus.Active
        };
        await db.Subscriptions.AddAsync(sub);
        await db.SaveChangesAsync();

        var payload = new
        {
            agreementId = "agr_yr",
            eventId = "evt-yr",
            eventType = "recurring.charge-captured.v1",
            chargeId = "chg_yr",
            occurred
        };
        var ctrl = CreateController(db,
            settings: new AppSettings { Id = 1, VippsSubscriptionWebhookSecret = "secret" });
        SetupValidWebhookRequest(ctrl, JsonSerializer.Serialize(payload));
        await ctrl.Webhook();

        var updated = await db.Subscriptions.FirstAsync();
        Assert.Equal(occurred.AddYears(1), updated.NextChargeDate);
    }

    private static void SetupValidWebhookRequest(SubscriptionsController ctrl, string body) =>
        SetupWebhookRequest(ctrl, body, valid: true);

    private static void SetupInvalidWebhookRequest(SubscriptionsController ctrl, string body) =>
        SetupWebhookRequest(ctrl, body, valid: false);

    private static void SetupWebhookRequest(SubscriptionsController ctrl, string body, bool valid)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        ctrl.ControllerContext.HttpContext.Request.Body = new MemoryStream(bytes);
        ctrl.ControllerContext.HttpContext.Request.ContentLength = bytes.Length;
        if (valid)
            ctrl.ControllerContext.HttpContext.Request.Headers["X-Test-Valid"] = "1";
    }

    private static Product MakePrimaryProduct(int id = 1) => new()
    {
        Id = id, Name = "Garge Basic", PriceInOre = 29900,
        Interval = BillingInterval.Monthly, Type = ProductType.Primary, IsActive = true
    };

    private static Product MakeAddOnProduct(int id = 2) => new()
    {
        Id = id, Name = "Garge Extra Sensor", PriceInOre = 4900,
        Interval = BillingInterval.Monthly, Type = ProductType.AddOn, IsActive = true
    };

    [Fact]
    public async Task InitiatePrimary_NoExisting_Returns200WithConfirmationUrl()
    {
        using var db = CreateDbContext();
        await db.Products.AddAsync(MakePrimaryProduct());
        await db.SaveChangesAsync();

        var vipps = MockVipps();
        vipps.Setup(v => v.CreateAgreementAsync(It.IsAny<Product>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(new VippsCreateAgreementResponse
            {
                AgreementId = "agr_new",
                VippsConfirmationUrl = "https://vipps.no/confirm/agr_new"
            });

        var ctrl = CreateController(db, vipps: vipps);

        var result = await ctrl.InitiateSubscription(
            new InitiateSubscriptionDto { ProductId = 1, PhoneNumber = "47912345678".Substring(0, 10), ConsentToWaiveWithdrawal = true });

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<InitiateSubscriptionResponseDto>(ok.Value);
        Assert.Equal("agr_new", dto.VippsAgreementId);
        Assert.Equal("https://vipps.no/confirm/agr_new", dto.VippsConfirmationUrl);
    }

    [Fact]
    public async Task InitiatePrimary_WithoutConsent_Returns400()
    {
        using var db = CreateDbContext();
        await db.Products.AddAsync(MakePrimaryProduct());
        await db.SaveChangesAsync();

        var ctrl = CreateController(db);

        var result = await ctrl.InitiateSubscription(
            new InitiateSubscriptionDto { ProductId = 1, PhoneNumber = "4791234567", ConsentToWaiveWithdrawal = false });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task InitiatePrimary_ExistingActivePrimary_Returns409()
    {
        using var db = CreateDbContext();
        await db.Products.AddAsync(MakePrimaryProduct());
        await db.Subscriptions.AddAsync(new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "existing", Status = SubscriptionStatus.Active
        });
        await db.SaveChangesAsync();

        var ctrl = CreateController(db);
        var result = await ctrl.InitiateSubscription(
            new InitiateSubscriptionDto { ProductId = 1, PhoneNumber = "4791234567", ConsentToWaiveWithdrawal = true });

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task InitiateAddOn_WithActivePrimary_Returns200()
    {
        using var db = CreateDbContext();
        await db.Products.AddRangeAsync(MakePrimaryProduct(), MakeAddOnProduct());
        await db.Subscriptions.AddAsync(new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "primary_agr", Status = SubscriptionStatus.Active
        });
        await db.SaveChangesAsync();

        var vipps = MockVipps();
        vipps.Setup(v => v.CreateAgreementAsync(It.IsAny<Product>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(new VippsCreateAgreementResponse { AgreementId = "addon_agr", VippsConfirmationUrl = "https://vipps.no/confirm/addon" });

        var ctrl = CreateController(db, vipps: vipps);

        var result = await ctrl.InitiateSubscription(
            new InitiateSubscriptionDto { ProductId = 2, PhoneNumber = "4791234567", ConsentToWaiveWithdrawal = true });

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(2, await db.Subscriptions.CountAsync(s => s.UserId == "user-1"));
    }

    [Fact]
    public async Task InitiateAddOn_WithoutPrimary_Returns400()
    {
        using var db = CreateDbContext();
        await db.Products.AddAsync(MakeAddOnProduct());
        await db.SaveChangesAsync();

        var ctrl = CreateController(db);
        var result = await ctrl.InitiateSubscription(
            new InitiateSubscriptionDto { ProductId = 2, PhoneNumber = "4791234567", ConsentToWaiveWithdrawal = true });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CancelById_OwnActiveSubscription_Returns200()
    {
        using var db = CreateDbContext();
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_cancel", Status = SubscriptionStatus.Active
        };
        await db.Subscriptions.AddAsync(sub);
        await db.SaveChangesAsync();

        var vipps = MockVipps();
        vipps.Setup(v => v.CancelAgreementAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);

        var ctrl = CreateController(db, vipps: vipps);

        var result = await ctrl.CancelSubscription(sub.Id);

        Assert.IsType<OkResult>(result);
        var updated = await db.Subscriptions.FirstAsync();
        Assert.Equal(SubscriptionStatus.Stopped, updated.Status);
    }

    [Fact]
    public async Task CancelById_OtherUsersSubscription_Returns404()
    {
        using var db = CreateDbContext();
        var sub = new Subscription
        {
            UserId = "other-user", ProductId = 1,
            VippsAgreementId = "agr_other", Status = SubscriptionStatus.Active
        };
        await db.Subscriptions.AddAsync(sub);
        await db.SaveChangesAsync();

        var ctrl = CreateController(db, userId: "user-1");
        var result = await ctrl.CancelSubscription(sub.Id);

        Assert.IsType<NotFoundObjectResult>(result);
    }
}
