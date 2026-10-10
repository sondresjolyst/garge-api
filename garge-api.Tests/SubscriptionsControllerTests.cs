using garge_api.Constants;
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
                       && (string.IsNullOrEmpty(req.Headers["X-Test-Secret"]) || req.Headers["X-Test-Secret"] == secret)
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

    private static IOptions<VippsOptions> VippsOpts() => Options.Create(new VippsOptions
    {
        ClientId = "id", ClientSecret = "s", SubscriptionKey = "k", BaseUrl = "https://api.vipps.no",
        MerchantSerialNumber = "msn-prod",
        TestMerchantSerialNumber = "msn-test"
    });

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
        push.Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var ctrl = new SubscriptionsController(
            db, (vipps ?? MockVipps()).Object,
            invoice ?? new Mock<IInvoiceService>().Object,
            subEmail ?? new Mock<ISubscriptionEmailService>().Object,
            MockSettingsCache(settings).Object,
            MockProtector().Object,
            push.Object,
            AppOpts(),
            VippsOpts(),
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
        await db.AppSettings.AddAsync(new AppSettings { Id = 1, VippsSubscriptionWebhookSecret = "secret" }, TestContext.Current.CancellationToken);
        var sub = new Subscription
        {
            UserId = "user-1",
            ProductId = 1,
            VippsAgreementId = "agr_test",
            Status = SubscriptionStatus.Pending
        };
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var payload = new VippsAgreementWebhookDto
        {
            AgreementId = "agr_test",
            Msn = "msn-prod",
            EventId = $"evt-{eventType}",
            EventType = eventType,
            Occurred = DateTime.UtcNow
        };
        var body = JsonSerializer.Serialize(payload);

        var ctrl = CreateController(db, settings: new AppSettings { Id = 1, VippsSubscriptionWebhookSecret = "secret" });
        SetupValidWebhookRequest(ctrl, body);

        var result = await ctrl.Webhook();

        Assert.IsType<OkResult>(result);
        var updated = await db.Subscriptions.FirstAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expected, updated.Status);
    }

    [Fact]
    public async Task Webhook_InvalidHmac_Returns401()
    {
        using var db = CreateDbContext();
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

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
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var payload = new VippsAgreementWebhookDto
        {
            AgreementId = "agr_race",
            Msn = "msn-prod",
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

        Assert.Equal(1, await db.ProcessedWebhookEvents.CountAsync(e => e.Id == "evt-race", TestContext.Current.CancellationToken));
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
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var payload = new VippsAgreementWebhookDto
        {
            AgreementId = "agr_dup",
            Msn = "msn-prod",
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

        Assert.Equal(1, await db.ProcessedWebhookEvents.CountAsync(e => e.Id == "evt-1", TestContext.Current.CancellationToken));
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
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var payload = new
        {
            agreementId = "agr_start",
            msn = "msn-prod",
            eventId = "evt-start",
            eventType = "recurring.agreement-activated.v1",
            occurred
        };
        var body = JsonSerializer.Serialize(payload);

        var ctrl = CreateController(db, settings: new AppSettings { Id = 1, VippsSubscriptionWebhookSecret = "secret" });
        SetupValidWebhookRequest(ctrl, body);
        await ctrl.Webhook();

        var updated = await db.Subscriptions.FirstAsync(TestContext.Current.CancellationToken);
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
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var vipps = MockVipps();
        vipps.Setup(v => v.GetAgreementAsync("agr_addr1", false))
            .ReturnsAsync(new VippsAgreementResponse { Id = "agr_addr1", Sub = "sub-x" });
        vipps.Setup(v => v.GetUserInfoAsync("sub-x", false))
            .ReturnsAsync(new VippsUserInfo
            {
                Address = new VippsAddress { Formatted = "Mårvegen 21a, 4347 Lye, Norway" }
            });

        var payload = new
        {
            agreementId = "agr_addr1",
            msn = "msn-prod",
            eventId = "evt-addr1",
            eventType = "recurring.agreement-activated.v1",
            occurred = DateTime.UtcNow
        };
        var ctrl = CreateController(db,
            settings: new AppSettings { Id = 1, VippsSubscriptionWebhookSecret = "secret" },
            vipps: vipps);
        SetupValidWebhookRequest(ctrl, JsonSerializer.Serialize(payload));
        await ctrl.Webhook();

        var updated = await db.Subscriptions.FirstAsync(TestContext.Current.CancellationToken);
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
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var vipps = MockVipps();
        vipps.Setup(v => v.GetAgreementAsync("agr_addr2", false))
            .ReturnsAsync(new VippsAgreementResponse { Id = "agr_addr2", Sub = null });

        var payload = new
        {
            agreementId = "agr_addr2",
            msn = "msn-prod",
            eventId = "evt-addr2",
            eventType = "recurring.agreement-activated.v1",
            occurred = DateTime.UtcNow
        };
        var ctrl = CreateController(db,
            settings: new AppSettings { Id = 1, VippsSubscriptionWebhookSecret = "secret" },
            vipps: vipps);
        SetupValidWebhookRequest(ctrl, JsonSerializer.Serialize(payload));
        await ctrl.Webhook();

        var updated = await db.Subscriptions.FirstAsync(TestContext.Current.CancellationToken);
        Assert.Null(updated.BillingAddress);
        Assert.Equal(SubscriptionStatus.Active, updated.Status);
        vipps.Verify(v => v.GetUserInfoAsync(It.IsAny<string>(), It.IsAny<bool?>()), Times.Never);
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
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var vipps = MockVipps();
        vipps.Setup(v => v.GetAgreementAsync("agr_addr3", false))
            .ReturnsAsync(new VippsAgreementResponse { Id = "agr_addr3", Sub = "sub-y" });
        vipps.Setup(v => v.GetUserInfoAsync("sub-y", false))
            .ThrowsAsync(new HttpRequestException("Vipps userinfo down"));

        var payload = new
        {
            agreementId = "agr_addr3",
            msn = "msn-prod",
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
        var updated = await db.Subscriptions.FirstAsync(TestContext.Current.CancellationToken);
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
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var vipps = MockVipps();
        var payload = new
        {
            agreementId = "agr_addr4",
            msn = "msn-prod",
            eventId = "evt-addr4",
            eventType = "recurring.agreement-activated.v1",
            occurred = DateTime.UtcNow
        };
        var ctrl = CreateController(db,
            settings: new AppSettings { Id = 1, VippsSubscriptionWebhookSecret = "secret" },
            vipps: vipps);
        SetupValidWebhookRequest(ctrl, JsonSerializer.Serialize(payload));
        await ctrl.Webhook();

        var updated = await db.Subscriptions.FirstAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Existing address", updated.BillingAddress);
        vipps.Verify(v => v.GetAgreementAsync(It.IsAny<string>(), It.IsAny<bool?>()), Times.Never);
    }

    [Fact]
    public async Task Webhook_ChargeCaptured_SetsNextChargeDateByInterval()
    {
        using var db = CreateDbContext();
        var occurred = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        await db.Products.AddAsync(MakePrimaryProduct(), TestContext.Current.CancellationToken);
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_charge", Status = SubscriptionStatus.Active
        };
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var payload = new
        {
            agreementId = "agr_charge",
            msn = "msn-prod",
            eventId = "evt-charge",
            eventType = "recurring.charge-captured.v1",
            occurred
        };
        var body = JsonSerializer.Serialize(payload);

        var ctrl = CreateController(db, settings: new AppSettings { Id = 1, VippsSubscriptionWebhookSecret = "secret" });
        SetupValidWebhookRequest(ctrl, body);
        await ctrl.Webhook();

        var updated = await db.Subscriptions.FirstAsync(TestContext.Current.CancellationToken);
        Assert.Equal(occurred.AddMonths(1), updated.NextChargeDate);
    }

    [Fact]
    public async Task Webhook_ChargeCaptured_DoesNotCallCreateChargeAsync_SchedulerIsSoleSource()
    {
        using var db = CreateDbContext();
        var occurred = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        await db.Products.AddAsync(MakePrimaryProduct(), TestContext.Current.CancellationToken);
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_no_post", Status = SubscriptionStatus.Active
        };
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var vipps = MockVipps();

        var payload = new
        {
            agreementId = "agr_no_post",
            msn = "msn-prod",
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
        var updated = await db.Subscriptions.FirstAsync(TestContext.Current.CancellationToken);
        Assert.Equal(occurred.AddMonths(1), updated.NextChargeDate);
    }

    [Fact]
    public async Task Webhook_ChargeCaptured_NullOccurred_LeavesNextChargeDateNull()
    {
        using var db = CreateDbContext();
        await db.Products.AddAsync(MakePrimaryProduct(), TestContext.Current.CancellationToken);
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_noocc", Status = SubscriptionStatus.Active
        };
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var payload = new
        {
            agreementId = "agr_noocc",
            msn = "msn-prod",
            eventId = "evt-noocc",
            eventType = "recurring.charge-captured.v1",
            chargeId = "chg_n"
        };
        var ctrl = CreateController(db,
            settings: new AppSettings { Id = 1, VippsSubscriptionWebhookSecret = "secret" });
        SetupValidWebhookRequest(ctrl, JsonSerializer.Serialize(payload));
        await ctrl.Webhook();

        var updated = await db.Subscriptions.FirstAsync(TestContext.Current.CancellationToken);
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
        }, TestContext.Current.CancellationToken);
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_yr", Status = SubscriptionStatus.Active
        };
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var payload = new
        {
            agreementId = "agr_yr",
            msn = "msn-prod",
            eventId = "evt-yr",
            eventType = "recurring.charge-captured.v1",
            chargeId = "chg_yr",
            occurred
        };
        var ctrl = CreateController(db,
            settings: new AppSettings { Id = 1, VippsSubscriptionWebhookSecret = "secret" });
        SetupValidWebhookRequest(ctrl, JsonSerializer.Serialize(payload));
        await ctrl.Webhook();

        var updated = await db.Subscriptions.FirstAsync(TestContext.Current.CancellationToken);
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
        await db.Products.AddAsync(MakePrimaryProduct(), TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var vipps = MockVipps();
        vipps.Setup(v => v.CreateAgreementAsync(It.IsAny<Product>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
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
        await db.Products.AddAsync(MakePrimaryProduct(), TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ctrl = CreateController(db);

        var result = await ctrl.InitiateSubscription(
            new InitiateSubscriptionDto { ProductId = 1, PhoneNumber = "4791234567", ConsentToWaiveWithdrawal = false });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task InitiatePrimary_ExistingActivePrimary_Returns409()
    {
        using var db = CreateDbContext();
        await db.Products.AddAsync(MakePrimaryProduct(), TestContext.Current.CancellationToken);
        await db.Subscriptions.AddAsync(new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "existing", Status = SubscriptionStatus.Active
        }, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

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
        }, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var vipps = MockVipps();
        vipps.Setup(v => v.CreateAgreementAsync(It.IsAny<Product>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(new VippsCreateAgreementResponse { AgreementId = "addon_agr", VippsConfirmationUrl = "https://vipps.no/confirm/addon" });

        var ctrl = CreateController(db, vipps: vipps);

        var result = await ctrl.InitiateSubscription(
            new InitiateSubscriptionDto { ProductId = 2, PhoneNumber = "4791234567", ConsentToWaiveWithdrawal = true });

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(2, await db.Subscriptions.CountAsync(s => s.UserId == "user-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InitiateAddOn_WithoutPrimary_Returns400()
    {
        using var db = CreateDbContext();
        await db.Products.AddAsync(MakeAddOnProduct(), TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

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
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var vipps = MockVipps();
        vipps.Setup(v => v.CancelAgreementAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);

        var ctrl = CreateController(db, vipps: vipps);

        var result = await ctrl.CancelSubscription(sub.Id);

        Assert.IsType<OkResult>(result);
        var updated = await db.Subscriptions.FirstAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SubscriptionStatus.Stopped, updated.Status);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CancelById_CancelsEachAgreementInItsOwnEnvironment(bool primaryIsTest, bool addOnIsTest)
    {
        using var db = CreateDbContext();
        await db.Products.AddRangeAsync(MakePrimaryProduct(), MakeAddOnProduct());
        var primary = new Subscription { UserId = "user-1", ProductId = 1, VippsAgreementId = "agr_primary", Status = SubscriptionStatus.Active, IsTest = primaryIsTest };
        var addOn = new Subscription { UserId = "user-1", ProductId = 2, VippsAgreementId = "agr_addon", Status = SubscriptionStatus.Active, IsTest = addOnIsTest };
        await db.Subscriptions.AddRangeAsync([primary, addOn], TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var vipps = MockVipps();
        var ctrl = CreateController(db, vipps: vipps);

        Assert.IsType<OkResult>(await ctrl.CancelSubscription(primary.Id));
        vipps.Verify(v => v.CancelAgreementAsync("agr_primary", $"cancel-{primary.Id}", primaryIsTest), Times.Once);
        vipps.Verify(v => v.CancelAgreementAsync("agr_addon", $"cancel-{addOn.Id}", addOnIsTest), Times.Once);
    }

    [Fact]
    public async Task UpdateQuantity_TestAgreement_PatchesTheCeilingInTheTestEnvironment()
    {
        using var db = CreateDbContext();
        await db.Products.AddRangeAsync(MakePrimaryProduct(), MakeAddOnProduct());
        var sub = new Subscription { UserId = "user-1", ProductId = 2, VippsAgreementId = "addon_test", Status = SubscriptionStatus.Active, Quantity = 1, IsTest = true };
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var vipps = MockVipps();
        var ctrl = CreateController(db, vipps: vipps);

        Assert.IsType<OkObjectResult>(await ctrl.UpdateSubscriptionQuantity(sub.Id, new UpdateSubscriptionQuantityDto { Quantity = 2 }));
        vipps.Verify(v => v.UpdateAgreementMaxAmountAsync("addon_test", It.IsAny<int>(), It.IsAny<string>(), true), Times.Once);
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
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ctrl = CreateController(db, userId: "user-1");
        var result = await ctrl.CancelSubscription(sub.Id);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task InitiateAddOn_WithQuantity_SendsPriceTimesQuantityToVipps()
    {
        using var db = CreateDbContext();
        await db.Products.AddRangeAsync(MakePrimaryProduct(), MakeAddOnProduct());
        await db.Subscriptions.AddAsync(new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "primary_agr", Status = SubscriptionStatus.Active
        }, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var vipps = MockVipps();
        int? capturedUnit = null;
        int? capturedQty = null;
        vipps.Setup(v => v.CreateAgreementAsync(It.IsAny<Product>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
            .Callback<Product, string, string, string, int, int, string>((_, _, _, _, unit, qty, _) =>
            {
                capturedUnit = unit;
                capturedQty = qty;
            })
            .ReturnsAsync(new VippsCreateAgreementResponse { AgreementId = "addon_agr", VippsConfirmationUrl = "https://vipps.no/x" });

        var ctrl = CreateController(db, vipps: vipps);

        await ctrl.InitiateSubscription(new InitiateSubscriptionDto
        {
            ProductId = 2, PhoneNumber = "4791234567", ConsentToWaiveWithdrawal = true, Quantity = 5
        });

        Assert.Equal(4900, capturedUnit);
        Assert.Equal(5, capturedQty);
    }

    [Fact]
    public async Task InitiateAddOn_WithQuantity_StoresQuantityOnSubscription()
    {
        using var db = CreateDbContext();
        await db.Products.AddRangeAsync(MakePrimaryProduct(), MakeAddOnProduct());
        await db.Subscriptions.AddAsync(new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "primary_agr", Status = SubscriptionStatus.Active
        }, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var vipps = MockVipps();
        vipps.Setup(v => v.CreateAgreementAsync(It.IsAny<Product>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(new VippsCreateAgreementResponse { AgreementId = "addon_agr", VippsConfirmationUrl = "https://vipps.no/x" });

        var ctrl = CreateController(db, vipps: vipps);

        await ctrl.InitiateSubscription(new InitiateSubscriptionDto
        {
            ProductId = 2, PhoneNumber = "4791234567", ConsentToWaiveWithdrawal = true, Quantity = 3
        });

        var addon = await db.Subscriptions.SingleAsync(s => s.ProductId == 2, TestContext.Current.CancellationToken);
        Assert.Equal(3, addon.Quantity);
    }

    [Fact]
    public async Task InitiatePrimary_WithQuantityAbove1_Returns400()
    {
        using var db = CreateDbContext();
        await db.Products.AddAsync(MakePrimaryProduct(), TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ctrl = CreateController(db);
        var result = await ctrl.InitiateSubscription(new InitiateSubscriptionDto
        {
            ProductId = 1, PhoneNumber = "4791234567", ConsentToWaiveWithdrawal = true, Quantity = 2
        });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateQuantity_Increase_PatchesVippsCeilingAndUpdatesRow()
    {
        using var db = CreateDbContext();
        await db.Products.AddRangeAsync(MakePrimaryProduct(), MakeAddOnProduct());
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 2,
            VippsAgreementId = "addon_agr", Status = SubscriptionStatus.Active, Quantity = 3
        };
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var vipps = MockVipps();
        int? capturedCeiling = null;
        vipps.Setup(v => v.UpdateAgreementMaxAmountAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool?>()))
            .Callback<string, int, string, bool?>((_, amount, _, _) => capturedCeiling = amount)
            .Returns(Task.CompletedTask);

        var ctrl = CreateController(db, vipps: vipps);
        var result = await ctrl.UpdateSubscriptionQuantity(sub.Id, new UpdateSubscriptionQuantityDto { Quantity = 5 });

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(4900 * 5, capturedCeiling);
        var updated = await db.Subscriptions.FindAsync(new object?[] { sub.Id }, TestContext.Current.CancellationToken);
        Assert.Equal(5, updated!.Quantity);
    }

    [Fact]
    public async Task UpdateQuantity_Decrease_DbOnlyNoVippsCall()
    {
        using var db = CreateDbContext();
        await db.Products.AddRangeAsync(MakePrimaryProduct(), MakeAddOnProduct());
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 2,
            VippsAgreementId = "addon_agr", Status = SubscriptionStatus.Active, Quantity = 5
        };
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var vipps = MockVipps();
        vipps.Setup(v => v.UpdateAgreementMaxAmountAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool?>()))
            .Returns(Task.CompletedTask);

        var ctrl = CreateController(db, vipps: vipps);
        var result = await ctrl.UpdateSubscriptionQuantity(sub.Id, new UpdateSubscriptionQuantityDto { Quantity = 3 });

        Assert.IsType<OkObjectResult>(result);
        vipps.Verify(v => v.UpdateAgreementMaxAmountAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool?>()), Times.Never);
        var updated = await db.Subscriptions.FindAsync(new object?[] { sub.Id }, TestContext.Current.CancellationToken);
        Assert.Equal(3, updated!.Quantity);
    }

    [Fact]
    public async Task UpdateQuantity_NotOwner_Returns404()
    {
        using var db = CreateDbContext();
        await db.Products.AddAsync(MakeAddOnProduct(), TestContext.Current.CancellationToken);
        var sub = new Subscription
        {
            UserId = "other-user", ProductId = 2,
            VippsAgreementId = "addon_agr", Status = SubscriptionStatus.Active, Quantity = 1
        };
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ctrl = CreateController(db, userId: "user-1");
        var result = await ctrl.UpdateSubscriptionQuantity(sub.Id, new UpdateSubscriptionQuantityDto { Quantity = 5 });

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task UpdateQuantity_Primary_Returns400()
    {
        using var db = CreateDbContext();
        await db.Products.AddAsync(MakePrimaryProduct(), TestContext.Current.CancellationToken);
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "primary_agr", Status = SubscriptionStatus.Active, Quantity = 1
        };
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ctrl = CreateController(db);
        var result = await ctrl.UpdateSubscriptionQuantity(sub.Id, new UpdateSubscriptionQuantityDto { Quantity = 2 });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateQuantity_NotActive_Returns400()
    {
        using var db = CreateDbContext();
        await db.Products.AddAsync(MakeAddOnProduct(), TestContext.Current.CancellationToken);
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 2,
            VippsAgreementId = "addon_agr", Status = SubscriptionStatus.Pending, Quantity = 1
        };
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ctrl = CreateController(db);
        var result = await ctrl.UpdateSubscriptionQuantity(sub.Id, new UpdateSubscriptionQuantityDto { Quantity = 3 });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    private static Subscription MoneySub(string agreementId, int quantity = 1, bool isTest = false, SubscriptionStatus status = SubscriptionStatus.Active) => new()
    {
        UserId = "user-1", ProductId = 1, VippsAgreementId = agreementId,
        Status = status, Quantity = quantity, IsTest = isTest
    };

    private static AppSettings WebhookSettings(bool vat = false) =>
        new() { Id = 1, VippsSubscriptionWebhookSecret = "secret", VatEnabled = vat };

    [Fact]
    public async Task Webhook_ChargeCaptured_InvoicesTheAmountVippsCharged()
    {
        using var db = CreateDbContext();
        await db.Products.AddAsync(MakePrimaryProduct(), TestContext.Current.CancellationToken);
        var sub = MoneySub("agr_amount", quantity: 3);
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var invoice = new Mock<IInvoiceService>();
        var payload = new
        {
            agreementId = "agr_amount", msn = "msn-prod", eventId = "evt-amount",
            eventType = "recurring.charge-captured.v1", chargeId = "chg_amount",
            amount = 112125, occurred = DateTime.UtcNow
        };
        var ctrl = CreateController(db, settings: WebhookSettings(vat: true), invoice: invoice.Object);
        SetupValidWebhookRequest(ctrl, JsonSerializer.Serialize(payload));

        Assert.IsType<OkResult>(await ctrl.Webhook());
        invoice.Verify(i => i.GenerateForSubscriptionChargeAsync(sub.Id, "chg_amount", 112125, It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public async Task Webhook_ChargeCapturedWithoutAmount_InvoicesPriceTimesQuantity()
    {
        using var db = CreateDbContext();
        await db.Products.AddAsync(MakePrimaryProduct(), TestContext.Current.CancellationToken);
        var sub = MoneySub("agr_fallback", quantity: 3);
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var invoice = new Mock<IInvoiceService>();
        var payload = new
        {
            agreementId = "agr_fallback", msn = "msn-prod", eventId = "evt-fallback",
            eventType = "recurring.charge-captured.v1", chargeId = "chg_fallback", occurred = DateTime.UtcNow
        };
        var ctrl = CreateController(db, settings: WebhookSettings(vat: true), invoice: invoice.Object);
        SetupValidWebhookRequest(ctrl, JsonSerializer.Serialize(payload));

        await ctrl.Webhook();

        // 299.00 NOK three times is 897.00 NOK.
        invoice.Verify(i => i.GenerateForSubscriptionChargeAsync(sub.Id, "chg_fallback", 89700, It.IsAny<DateTime>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Webhook_WithoutMerchantNumber_IsApplied_AsRecurringEventsMayLeaveItOut(string? msn)
    {
        using var db = CreateDbContext();
        await db.Subscriptions.AddAsync(MoneySub("agr_nomsn", status: SubscriptionStatus.Pending), TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var payload = new { agreementId = "agr_nomsn", msn, eventType = "recurring.agreement-activated.v1", occurred = DateTime.UtcNow };
        var ctrl = CreateController(db, settings: WebhookSettings());
        SetupValidWebhookRequest(ctrl, JsonSerializer.Serialize(payload));

        Assert.IsType<OkResult>(await ctrl.Webhook());
        Assert.Equal(SubscriptionStatus.Active, (await db.Subscriptions.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Theory]
    [InlineData("msn-other")]
    [InlineData("msn-test")]
    public async Task Webhook_OtherMerchantNumber_IsAcknowledgedButNotApplied(string msn)
    {
        using var db = CreateDbContext();
        await db.Subscriptions.AddAsync(MoneySub("agr_msn", status: SubscriptionStatus.Pending), TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var payload = new { agreementId = "agr_msn", msn, eventType = "recurring.agreement-activated.v1", occurred = DateTime.UtcNow };
        var ctrl = CreateController(db, settings: WebhookSettings());
        SetupValidWebhookRequest(ctrl, JsonSerializer.Serialize(payload));

        Assert.IsType<OkResult>(await ctrl.Webhook());
        Assert.Equal(1, await db.ProcessedWebhookEvents.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(SubscriptionStatus.Pending, (await db.Subscriptions.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Webhook_ChargeCaptured_InvoicesTheCapturedAmountOverTheChargeAmount()
    {
        using var db = CreateDbContext();
        await db.Products.AddAsync(MakePrimaryProduct(), TestContext.Current.CancellationToken);
        var sub = MoneySub("agr_captured");
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var invoice = new Mock<IInvoiceService>();
        var payload = new
        {
            agreementId = "agr_captured", eventType = "recurring.charge-captured.v1", chargeId = "chg_captured",
            amount = 50000, amountCaptured = 37375, occurred = DateTime.UtcNow
        };
        var ctrl = CreateController(db, settings: WebhookSettings(vat: true), invoice: invoice.Object);
        SetupValidWebhookRequest(ctrl, JsonSerializer.Serialize(payload));

        await ctrl.Webhook();

        invoice.Verify(i => i.GenerateForSubscriptionChargeAsync(sub.Id, "chg_captured", 37375, It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public async Task Webhook_TwoChargesAtTheSameMoment_AreBothHandled()
    {
        using var db = CreateDbContext();
        await db.Products.AddAsync(MakePrimaryProduct(), TestContext.Current.CancellationToken);
        var sub = MoneySub("agr_two");
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var invoice = new Mock<IInvoiceService>();
        var occurred = DateTime.UtcNow;
        foreach (var chargeId in new[] { "chg_a", "chg_b" })
        {
            var payload = new { agreementId = "agr_two", eventType = "recurring.charge-captured.v1", chargeId, amountCaptured = 37375, occurred };
            var ctrl = CreateController(db, settings: WebhookSettings(vat: true), invoice: invoice.Object);
            SetupValidWebhookRequest(ctrl, JsonSerializer.Serialize(payload));
            await ctrl.Webhook();
        }

        invoice.Verify(i => i.GenerateForSubscriptionChargeAsync(sub.Id, "chg_a", 37375, It.IsAny<DateTime>()), Times.Once);
        invoice.Verify(i => i.GenerateForSubscriptionChargeAsync(sub.Id, "chg_b", 37375, It.IsAny<DateTime>()), Times.Once);
    }

    private static AppSettings BothEnvironmentsSettings() => new()
    {
        Id = 1,
        VippsSubscriptionWebhookId = "wh-live", VippsSubscriptionWebhookSecret = "live-secret",
        VippsTestSubscriptionWebhookId = "wh-test", VippsTestSubscriptionWebhookSecret = "test-secret"
    };

    private static string ActivatedEvent(string agreementId, string msn, string eventId) => JsonSerializer.Serialize(new
    {
        agreementId, msn, eventId,
        eventType = "recurring.agreement-activated.v1", occurred = DateTime.UtcNow
    });

    [Fact]
    public async Task Webhook_TestSubscription_NeedsTheTestMerchantNumber()
    {
        using var db = CreateDbContext();
        await db.Subscriptions.AddAsync(MoneySub("agr_test", isTest: true, status: SubscriptionStatus.Pending), TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ctrl = CreateController(db, settings: BothEnvironmentsSettings());
        SetupValidWebhookRequest(ctrl, ActivatedEvent("agr_test", "msn-test", "evt-test"));
        ctrl.ControllerContext.HttpContext.Request.Headers["X-Test-Secret"] = "test-secret";

        Assert.IsType<OkResult>(await ctrl.Webhook());
        Assert.Equal(SubscriptionStatus.Active, (await db.Subscriptions.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Theory]
    [InlineData(true, "live-secret", "msn-test")]
    [InlineData(false, "test-secret", "msn-prod")]
    public async Task Webhook_SignedByTheOtherEnvironment_IsAcknowledgedButNotApplied(bool subIsTest, string signedWith, string msn)
    {
        using var db = CreateDbContext();
        await db.Subscriptions.AddAsync(MoneySub("agr_env", isTest: subIsTest, status: SubscriptionStatus.Pending), TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ctrl = CreateController(db, settings: BothEnvironmentsSettings());
        SetupValidWebhookRequest(ctrl, ActivatedEvent("agr_env", msn, "evt-env"));
        ctrl.ControllerContext.HttpContext.Request.Headers["X-Test-Secret"] = signedWith;

        Assert.IsType<OkResult>(await ctrl.Webhook());
        Assert.Equal(SubscriptionStatus.Pending, (await db.Subscriptions.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(signedWith == "test-secret" ? "test:evt-env" : "evt-env", await db.ProcessedWebhookEvents.Select(e => e.Id).SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Webhook_SameEventIdInBothEnvironments_IsHandledInEach()
    {
        // A test event must not mark the production event with the same id as handled.
        using var db = CreateDbContext();
        await db.Subscriptions.AddRangeAsync([
            MoneySub("agr_live", status: SubscriptionStatus.Pending),
            MoneySub("agr_test", isTest: true, status: SubscriptionStatus.Pending)], TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var fromTest = CreateController(db, settings: BothEnvironmentsSettings());
        SetupValidWebhookRequest(fromTest, ActivatedEvent("agr_test", "msn-test", "evt-shared"));
        fromTest.ControllerContext.HttpContext.Request.Headers["X-Test-Secret"] = "test-secret";
        Assert.IsType<OkResult>(await fromTest.Webhook());

        var fromLive = CreateController(db, settings: BothEnvironmentsSettings());
        SetupValidWebhookRequest(fromLive, ActivatedEvent("agr_live", "msn-prod", "evt-shared"));
        fromLive.ControllerContext.HttpContext.Request.Headers["X-Test-Secret"] = "live-secret";
        Assert.IsType<OkResult>(await fromLive.Webhook());

        var subs = await db.Subscriptions.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        Assert.All(subs, x => Assert.Equal(SubscriptionStatus.Active, x.Status));
        var keys = await db.ProcessedWebhookEvents.Select(e => e.Id).OrderBy(id => id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["evt-shared", "test:evt-shared"], keys);
    }

    [Fact]
    public async Task Webhook_FailedSave_IsNotMarkedProcessed_AndTheRedeliveryIsApplied()
    {
        var (failing, healthy) = FailingSaveInterceptor.Contexts();
        using (failing)
        using (healthy)
        {
            await healthy.Subscriptions.AddAsync(MoneySub("agr_retry", status: SubscriptionStatus.Pending), TestContext.Current.CancellationToken);
            await healthy.SaveChangesAsync(TestContext.Current.CancellationToken);

            var body = JsonSerializer.Serialize(new
            {
                agreementId = "agr_retry", msn = "msn-prod", eventId = "evt-retry",
                eventType = "recurring.agreement-activated.v1", occurred = DateTime.UtcNow
            });

            var first = CreateController(failing, settings: WebhookSettings());
            SetupValidWebhookRequest(first, body);
            await Assert.ThrowsAsync<DbUpdateException>(() => first.Webhook());
            Assert.Equal(0, await healthy.ProcessedWebhookEvents.CountAsync(TestContext.Current.CancellationToken));

            var redelivery = CreateController(healthy, settings: WebhookSettings());
            SetupValidWebhookRequest(redelivery, body);
            Assert.IsType<OkResult>(await redelivery.Webhook());

            Assert.Equal(1, await healthy.ProcessedWebhookEvents.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(SubscriptionStatus.Active, (await healthy.Subscriptions.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken)).Status);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Initiate_TakesTheEnvironmentFromTheAgreement(bool createdInTest)
    {
        using var db = CreateDbContext();
        await db.Products.AddAsync(MakePrimaryProduct(), TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var vipps = MockVipps();
        vipps.Setup(v => v.CreateAgreementAsync(It.IsAny<Product>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(new VippsCreateAgreementResponse { AgreementId = "agr_env", VippsConfirmationUrl = "https://vipps.no/confirm", IsTest = createdInTest });

        // The settings say the opposite, as right after an admin switches test mode.
        var ctrl = CreateController(db, vipps: vipps, settings: new AppSettings { Id = 1, VippsTestMode = !createdInTest });
        await ctrl.InitiateSubscription(new InitiateSubscriptionDto { ProductId = 1, PhoneNumber = "4791234567", ConsentToWaiveWithdrawal = true });

        Assert.Equal(createdInTest, (await db.Subscriptions.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken)).IsTest);
    }

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private static string ChargeEvent(string agreementId, string eventType, string chargeId, DateTime occurred, string eventId) =>
        JsonSerializer.Serialize(new { agreementId, msn = "msn-prod", eventId, eventType, chargeId, chargeType = "RECURRING", amount = 29900, amountCaptured = 29900, occurred });

    private async Task<IActionResult> SendChargeEventAsync(ApplicationDbContext db, string body,
        Mock<IVippsService>? vipps = null, ISubscriptionEmailService? subEmail = null)
    {
        var ctrl = CreateController(db, vipps: vipps, settings: WebhookSettings(), subEmail: subEmail);
        SetupValidWebhookRequest(ctrl, body);
        return await ctrl.Webhook();
    }

    private static async Task<Subscription> ChargingSubAsync(ApplicationDbContext db, DateTime start, DateTime next, int failedAttempts = 0)
    {
        await db.Products.AddAsync(MakePrimaryProduct(), TestContext.Current.CancellationToken);
        var sub = MoneySub("agr_charge");
        sub.StartDate = start;
        sub.NextChargeDate = next;
        sub.FailedChargeAttempts = failedAttempts;
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return sub;
    }

    [Fact]
    public async Task Webhook_ScheduledChargeCapturedLate_NextChargeKeepsTheBillingDay()
    {
        using var db = CreateDbContext();
        var due = Utc(2026, 3, 5);
        var sub = await ChargingSubAsync(db, start: Utc(2026, 1, 5), next: due, failedAttempts: 1);

        var body = ChargeEvent("agr_charge", VippsEvents.ChargeCaptured, SubscriptionCharges.Key(sub.Id, due, 1), Utc(2026, 3, 10), "evt-late");
        Assert.IsType<OkResult>(await SendChargeEventAsync(db, body));

        var saved = await db.Subscriptions.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Utc(2026, 4, 5), saved.NextChargeDate);
        Assert.Equal(0, saved.FailedChargeAttempts);
    }

    [Fact]
    public async Task Webhook_OlderChargeCapturedAgain_DoesNotMoveBillingBack()
    {
        using var db = CreateDbContext();
        var sub = await ChargingSubAsync(db, start: Utc(2026, 1, 5), next: Utc(2026, 5, 5));

        var body = ChargeEvent("agr_charge", VippsEvents.ChargeCaptured, SubscriptionCharges.Key(sub.Id, Utc(2026, 3, 5), 0), Utc(2026, 3, 5), "evt-old");
        Assert.IsType<OkResult>(await SendChargeEventAsync(db, body));

        Assert.Equal(Utc(2026, 5, 5), (await db.Subscriptions.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).NextChargeDate);
    }

    [Fact]
    public async Task Webhook_ChargeOfAnotherSubscription_DoesNotMoveBilling()
    {
        using var db = CreateDbContext();
        var sub = await ChargingSubAsync(db, start: Utc(2026, 1, 5), next: Utc(2026, 3, 5));

        var body = ChargeEvent("agr_charge", VippsEvents.ChargeCaptured, SubscriptionCharges.Key(sub.Id + 1, Utc(2026, 3, 5), 0), Utc(2026, 3, 5), "evt-other");
        Assert.IsType<OkResult>(await SendChargeEventAsync(db, body));

        Assert.Equal(Utc(2026, 3, 5), (await db.Subscriptions.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).NextChargeDate);
    }

    [Fact]
    public async Task Webhook_InitialChargeCaptured_StartsBillingOnce()
    {
        using var db = CreateDbContext();
        await db.Products.AddAsync(MakePrimaryProduct(), TestContext.Current.CancellationToken);
        var sub = MoneySub("agr_initial");
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.IsType<OkResult>(await SendChargeEventAsync(db, ChargeEvent("agr_initial", VippsEvents.ChargeCaptured, "vipps-initial-id", Utc(2026, 1, 31), "evt-initial")));
        Assert.Equal(Utc(2026, 2, 28), (await db.Subscriptions.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).NextChargeDate);

        // A redelivery with a new event id, months later, leaves billing alone.
        Assert.IsType<OkResult>(await SendChargeEventAsync(db, ChargeEvent("agr_initial", VippsEvents.ChargeCaptured, "vipps-initial-id", Utc(2026, 6, 1), "evt-initial-2")));
        Assert.Equal(Utc(2026, 2, 28), (await db.Subscriptions.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).NextChargeDate);
    }

    [Fact]
    public async Task Webhook_CurrentAttemptFails_IsCountedAndTheCustomerTold()
    {
        using var db = CreateDbContext();
        var due = Utc(2026, 3, 5);
        var sub = await ChargingSubAsync(db, start: Utc(2026, 1, 5), next: due);
        var email = new Mock<ISubscriptionEmailService>();
        var vipps = MockVipps();

        var body = ChargeEvent("agr_charge", VippsEvents.ChargeFailed, SubscriptionCharges.Key(sub.Id, due, 0), Utc(2026, 3, 10), "evt-fail-1");
        Assert.IsType<OkResult>(await SendChargeEventAsync(db, body, vipps, email.Object));

        var saved = await db.Subscriptions.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, saved.FailedChargeAttempts);
        Assert.Equal(SubscriptionStatus.Active, saved.Status);
        Assert.Equal(due, saved.NextChargeDate);
        email.Verify(e => e.SendChargeFailedAsync(sub.Id), Times.Once);
        email.Verify(e => e.SendStoppedForNonPaymentAsync(It.IsAny<int>()), Times.Never);
        vipps.Verify(v => v.CancelAgreementAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()), Times.Never);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Webhook_FailureOfAnotherAttempt_IsNotCounted(int attemptInKey)
    {
        // One attempt has failed, so attempt 1 is current. Attempt 0 failing again, or an attempt 2
        // that was never posted, is not counted.
        using var db = CreateDbContext();
        var due = Utc(2026, 3, 5);
        var sub = await ChargingSubAsync(db, start: Utc(2026, 1, 5), next: due, failedAttempts: 1);
        var email = new Mock<ISubscriptionEmailService>();

        var key = SubscriptionCharges.Key(sub.Id, due, attemptInKey);
        Assert.IsType<OkResult>(await SendChargeEventAsync(db, ChargeEvent("agr_charge", VippsEvents.ChargeFailed, key, Utc(2026, 3, 10), "evt-stale"), subEmail: email.Object));

        Assert.Equal(1, (await db.Subscriptions.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).FailedChargeAttempts);
        email.Verify(e => e.SendChargeFailedAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Webhook_FailureOfAnotherSubscriptionsCharge_IsNotCounted()
    {
        using var db = CreateDbContext();
        var due = Utc(2026, 3, 5);
        var sub = await ChargingSubAsync(db, start: Utc(2026, 1, 5), next: due);

        var key = SubscriptionCharges.Key(sub.Id + 1, due, 0);
        Assert.IsType<OkResult>(await SendChargeEventAsync(db, ChargeEvent("agr_charge", VippsEvents.ChargeFailed, key, Utc(2026, 3, 10), "evt-other-sub")));

        Assert.Equal(0, (await db.Subscriptions.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).FailedChargeAttempts);
    }

    [Theory]
    [InlineData("charge_amount_too_high", "charge_amount_too_high")]
    [InlineData("user_action_required", "user_action_required")]
    [InlineData(null, null)]
    [InlineData("a_reason_longer_than_fifty_characters_is_not_stored_at_all", null)]
    public async Task Webhook_CountedFailure_StoresTheVippsFailureReason(string? sent, string? stored)
    {
        using var db = CreateDbContext();
        var due = Utc(2026, 3, 5);
        var sub = await ChargingSubAsync(db, start: Utc(2026, 1, 5), next: due);

        var body = JsonSerializer.Serialize(new
        {
            agreementId = "agr_charge", msn = "msn-prod", eventId = "evt-reason", eventType = VippsEvents.ChargeFailed,
            chargeId = SubscriptionCharges.Key(sub.Id, due, 0), occurred = Utc(2026, 3, 10), failureReason = sent
        });
        Assert.IsType<OkResult>(await SendChargeEventAsync(db, body));

        Assert.Equal(stored, (await db.Subscriptions.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).LastChargeFailureReason);
    }

    [Fact]
    public async Task Webhook_Capture_ClearsTheLastFailureReason()
    {
        using var db = CreateDbContext();
        var due = Utc(2026, 3, 5);
        var sub = await ChargingSubAsync(db, start: Utc(2026, 1, 5), next: due, failedAttempts: 1);
        sub.LastChargeFailureReason = SubscriptionCharges.AmountTooHigh;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var body = ChargeEvent("agr_charge", VippsEvents.ChargeCaptured, SubscriptionCharges.Key(sub.Id, due, 1), Utc(2026, 3, 12), "evt-clear");
        Assert.IsType<OkResult>(await SendChargeEventAsync(db, body));

        Assert.Null((await db.Subscriptions.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).LastChargeFailureReason);
    }

    [Fact]
    public async Task Webhook_FailureOfAnOldPeriod_IsNotCounted()
    {
        using var db = CreateDbContext();
        var sub = await ChargingSubAsync(db, start: Utc(2026, 1, 5), next: Utc(2026, 4, 5));

        var key = SubscriptionCharges.Key(sub.Id, Utc(2026, 3, 5), 0);
        Assert.IsType<OkResult>(await SendChargeEventAsync(db, ChargeEvent("agr_charge", VippsEvents.ChargeFailed, key, Utc(2026, 3, 10), "evt-old-period")));

        Assert.Equal(0, (await db.Subscriptions.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).FailedChargeAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Webhook_LastAttemptFails_StopsTheAgreementInItsEnvironment(bool isTest)
    {
        using var db = CreateDbContext();
        var due = Utc(2026, 3, 5);
        var sub = await ChargingSubAsync(db, start: Utc(2026, 1, 5), next: due, failedAttempts: SubscriptionCharges.MaxAttempts - 1);
        sub.IsTest = isTest;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var email = new Mock<ISubscriptionEmailService>();
        var vipps = MockVipps();

        var body = JsonSerializer.Serialize(new
        {
            agreementId = "agr_charge", msn = isTest ? "msn-test" : "msn-prod", eventId = "evt-final", eventType = VippsEvents.ChargeFailed,
            chargeId = SubscriptionCharges.Key(sub.Id, due, SubscriptionCharges.MaxAttempts - 1), occurred = Utc(2026, 3, 20)
        });
        var ctrl = CreateController(db, vipps: vipps, subEmail: email.Object, settings: new AppSettings
        {
            Id = 1, VippsSubscriptionWebhookSecret = "live-secret", VippsTestSubscriptionWebhookSecret = "test-secret"
        });
        SetupValidWebhookRequest(ctrl, body);
        ctrl.ControllerContext.HttpContext.Request.Headers["X-Test-Secret"] = isTest ? "test-secret" : "live-secret";
        Assert.IsType<OkResult>(await ctrl.Webhook());

        vipps.Verify(v => v.CancelAgreementAsync("agr_charge", $"cancel-{sub.Id}", isTest), Times.Once);
        var saved = await db.Subscriptions.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SubscriptionStatus.Stopped, saved.Status);
        Assert.Equal(SubscriptionCharges.MaxAttempts, saved.FailedChargeAttempts);
        email.Verify(e => e.SendStoppedForNonPaymentAsync(sub.Id), Times.Once);
        email.Verify(e => e.SendChargeFailedAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Webhook_LastAttemptFails_AndTheCancelFails_TheFailureIsSavedForTheScheduler()
    {
        using var db = CreateDbContext();
        var due = Utc(2026, 3, 5);
        var sub = await ChargingSubAsync(db, start: Utc(2026, 1, 5), next: due, failedAttempts: SubscriptionCharges.MaxAttempts - 1);
        var vipps = MockVipps();
        vipps.Setup(v => v.CancelAgreementAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()))
            .ThrowsAsync(new HttpRequestException("down"));
        var email = new Mock<ISubscriptionEmailService>();

        var body = ChargeEvent("agr_charge", VippsEvents.ChargeFailed, SubscriptionCharges.Key(sub.Id, due, SubscriptionCharges.MaxAttempts - 1), Utc(2026, 3, 20), "evt-final");
        Assert.IsType<OkResult>(await SendChargeEventAsync(db, body, vipps, email.Object));

        var saved = await db.Subscriptions.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SubscriptionStatus.Active, saved.Status);
        Assert.Equal(SubscriptionCharges.MaxAttempts, saved.FailedChargeAttempts);
        Assert.Equal(1, await db.ProcessedWebhookEvents.CountAsync(TestContext.Current.CancellationToken));
        email.Verify(e => e.SendStoppedForNonPaymentAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Webhook_StartedJustAfterMidnightInNorway_IsBilledOnTheNorwegianDay()
    {
        // 23:30 UTC on 31 January is 00:30 on 1 February in Norway.
        using var db = CreateDbContext();
        var due = Utc(2026, 3, 1);
        var sub = await ChargingSubAsync(db, start: new DateTime(2026, 1, 31, 23, 30, 0, DateTimeKind.Utc), next: due);

        var body = ChargeEvent("agr_charge", VippsEvents.ChargeCaptured, SubscriptionCharges.Key(sub.Id, due, 0), Utc(2026, 3, 1), "evt-oslo");
        Assert.IsType<OkResult>(await SendChargeEventAsync(db, body));

        Assert.Equal(Utc(2026, 4, 1), (await db.Subscriptions.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).NextChargeDate);
    }

    [Fact]
    public async Task CancelById_AddOnCancelFails_NothingMoreIsStopped()
    {
        using var db = CreateDbContext();
        await db.Products.AddRangeAsync(MakePrimaryProduct(), MakeAddOnProduct());
        var primary = new Subscription { UserId = "user-1", ProductId = 1, VippsAgreementId = "agr_primary", Status = SubscriptionStatus.Active };
        var addOn = new Subscription { UserId = "user-1", ProductId = 2, VippsAgreementId = "agr_addon", Status = SubscriptionStatus.Active };
        await db.Subscriptions.AddRangeAsync([primary, addOn], TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var vipps = MockVipps();
        vipps.Setup(v => v.CancelAgreementAsync("agr_addon", It.IsAny<string>(), It.IsAny<bool?>()))
            .ThrowsAsync(new HttpRequestException("down"));
        var ctrl = CreateController(db, vipps: vipps);

        var result = await ctrl.CancelSubscription(primary.Id);

        Assert.Equal(502, Assert.IsType<ObjectResult>(result).StatusCode);
        vipps.Verify(v => v.CancelAgreementAsync("agr_primary", It.IsAny<string>(), It.IsAny<bool?>()), Times.Never);
        var subs = await db.Subscriptions.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        Assert.All(subs, x => Assert.Equal(SubscriptionStatus.Active, x.Status));
    }

    [Fact]
    public async Task CancelById_PrimaryCancelFails_CancelledAddOnsStayStopped()
    {
        using var db = CreateDbContext();
        await db.Products.AddRangeAsync(MakePrimaryProduct(), MakeAddOnProduct());
        var primary = new Subscription { UserId = "user-1", ProductId = 1, VippsAgreementId = "agr_primary", Status = SubscriptionStatus.Active };
        var addOn = new Subscription { UserId = "user-1", ProductId = 2, VippsAgreementId = "agr_addon", Status = SubscriptionStatus.Active };
        await db.Subscriptions.AddRangeAsync([primary, addOn], TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var vipps = MockVipps();
        vipps.Setup(v => v.CancelAgreementAsync("agr_primary", It.IsAny<string>(), It.IsAny<bool?>()))
            .ThrowsAsync(new HttpRequestException("down"));
        var ctrl = CreateController(db, vipps: vipps);

        Assert.Equal(502, Assert.IsType<ObjectResult>(await ctrl.CancelSubscription(primary.Id)).StatusCode);

        var saved = await db.Subscriptions.AsNoTracking().ToDictionaryAsync(x => x.VippsAgreementId, x => x.Status, TestContext.Current.CancellationToken);
        Assert.Equal(SubscriptionStatus.Active, saved["agr_primary"]);
        Assert.Equal(SubscriptionStatus.Stopped, saved["agr_addon"]);
    }

    [Fact]
    public async Task Initiate_TotalAboveTheVippsAgreementLimit_IsRefusedBeforeVipps()
    {
        using var db = CreateDbContext();
        await db.Products.AddRangeAsync(MakePrimaryProduct(), new Product
        {
            Id = 2, Name = "Big add-on", PriceInOre = 40_001, Interval = BillingInterval.Monthly, Type = ProductType.AddOn, IsActive = true
        });
        await db.Subscriptions.AddAsync(new Subscription { UserId = "user-1", ProductId = 1, VippsAgreementId = "agr_p", Status = SubscriptionStatus.Active }, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var vipps = MockVipps();
        var ctrl = CreateController(db, vipps: vipps);

        var result = await ctrl.InitiateSubscription(new InitiateSubscriptionDto
        {
            ProductId = 2, PhoneNumber = "4791234567", ConsentToWaiveWithdrawal = true, Quantity = 50
        });

        Assert.IsType<BadRequestObjectResult>(result);
        vipps.Verify(v => v.CreateAgreementAsync(It.IsAny<Product>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
        Assert.Equal(1, await db.Subscriptions.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Initiate_TotalExactlyAtTheVippsAgreementLimit_IsAllowed()
    {
        using var db = CreateDbContext();
        await db.Products.AddRangeAsync(MakePrimaryProduct(), new Product
        {
            Id = 2, Name = "Big add-on", PriceInOre = 40_000, Interval = BillingInterval.Monthly, Type = ProductType.AddOn, IsActive = true
        });
        await db.Subscriptions.AddAsync(new Subscription { UserId = "user-1", ProductId = 1, VippsAgreementId = "agr_p", Status = SubscriptionStatus.Active }, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var vipps = MockVipps();
        vipps.Setup(v => v.CreateAgreementAsync(It.IsAny<Product>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(new VippsCreateAgreementResponse { AgreementId = "agr_big", VippsConfirmationUrl = "https://landing.vipps.no/x" });
        var ctrl = CreateController(db, vipps: vipps);

        var result = await ctrl.InitiateSubscription(new InitiateSubscriptionDto
        {
            ProductId = 2, PhoneNumber = "4791234567", ConsentToWaiveWithdrawal = true, Quantity = 50
        });

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task UpdateQuantity_TotalAboveTheVippsAgreementLimit_IsRefusedBeforeVipps()
    {
        using var db = CreateDbContext();
        await db.Products.AddRangeAsync(MakePrimaryProduct(), new Product
        {
            Id = 2, Name = "Big add-on", PriceInOre = 40_001, Interval = BillingInterval.Monthly, Type = ProductType.AddOn, IsActive = true
        });
        var sub = new Subscription { UserId = "user-1", ProductId = 2, VippsAgreementId = "agr_big", Status = SubscriptionStatus.Active, Quantity = 1 };
        await db.Subscriptions.AddAsync(sub, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var vipps = MockVipps();
        var ctrl = CreateController(db, vipps: vipps);

        Assert.IsType<BadRequestObjectResult>(await ctrl.UpdateSubscriptionQuantity(sub.Id, new UpdateSubscriptionQuantityDto { Quantity = 50 }));

        vipps.Verify(v => v.UpdateAgreementMaxAmountAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool?>()), Times.Never);
        Assert.Equal(1, (await db.Subscriptions.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).Quantity);
    }
}
