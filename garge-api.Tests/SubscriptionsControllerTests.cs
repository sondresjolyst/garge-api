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
using Moq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace garge_api.Tests;

public class SubscriptionsControllerTests : ControllerTestBase
{
    private static Mock<IVippsService> MockVipps()
    {
        var mock = new Mock<IVippsService>();
        mock.Setup(v => v.VerifyWebhookSignature(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string body, string sig, string secret) =>
            {
                if (string.IsNullOrEmpty(secret) || !sig.StartsWith("sha256=")) return false;
                var computed = "sha256=" + Convert.ToHexString(
                    HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))
                ).ToLowerInvariant();
                return CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(computed),
                    Encoding.ASCII.GetBytes(sig.ToLowerInvariant()));
            });
        return mock;
    }

    private SubscriptionsController CreateController(ApplicationDbContext db, string userId = "user-1")
    {
        var ctrl = new SubscriptionsController(
            db, MockVipps().Object, MockMapper.Object,
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
            EventType = eventType,
            Occurred = DateTime.UtcNow
        };
        var body = JsonSerializer.Serialize(payload);
        var sig = "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret"), Encoding.UTF8.GetBytes(body))
        ).ToLowerInvariant();

        var ctrl = CreateController(db);
        SetupWebhookRequest(ctrl, body, sig);

        var result = await ctrl.Webhook();

        Assert.IsType<OkResult>(result);
        var updated = await db.Subscriptions.FirstAsync();
        Assert.Equal(expected, updated.Status);
    }

    [Fact]
    public async Task Webhook_InvalidHmac_Returns401()
    {
        using var db = CreateDbContext();
        await db.AppSettings.AddAsync(new AppSettings { Id = 1, VippsSubscriptionWebhookSecret = "correct" });
        await db.SaveChangesAsync();

        var body = """{"agreementId":"agr_test","eventType":"recurring.agreement-activated.v1"}""";
        var ctrl = CreateController(db);
        SetupWebhookRequest(ctrl, body, "sha256=badhash");

        var result = await ctrl.Webhook();

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task Webhook_ActivatedEvent_SetsStartDate()
    {
        using var db = CreateDbContext();
        var occurred = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);
        await db.AppSettings.AddAsync(new AppSettings { Id = 1, VippsSubscriptionWebhookSecret = "secret" });
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr_start",
            Status = SubscriptionStatus.Pending
        };
        await db.Subscriptions.AddAsync(sub);
        await db.SaveChangesAsync();

        var payload = new { agreementId = "agr_start", eventType = "recurring.agreement-activated.v1", occurred };
        var body = JsonSerializer.Serialize(payload);
        var sig = "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret"), Encoding.UTF8.GetBytes(body))
        ).ToLowerInvariant();

        var ctrl = CreateController(db);
        SetupWebhookRequest(ctrl, body, sig);
        await ctrl.Webhook();

        var updated = await db.Subscriptions.FirstAsync();
        Assert.Equal(occurred, updated.StartDate);
    }

    private static void SetupWebhookRequest(SubscriptionsController ctrl, string body, string signature)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctrl.ControllerContext.HttpContext.Request.Body = new MemoryStream(bytes);
        ctrl.ControllerContext.HttpContext.Request.ContentLength = bytes.Length;
        ctrl.ControllerContext.HttpContext.Request.Headers["X-Vipps-Signature"] = signature;
    }
}
