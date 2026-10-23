using garge_api.Constants;
using garge_api.Models;
using garge_api.Models.Push;
using garge_api.Models.Sensor;
using garge_api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace garge_api.Tests;

public class SensorOfflineCheckServiceTests : ControllerTestBase
{
    private sealed class TestableService(IServiceScopeFactory scopeFactory)
        : SensorOfflineCheckService(scopeFactory, NullLogger<SensorOfflineCheckService>.Instance)
    {
        public Task RunCheckAsync(CancellationToken ct = default) => CheckAsync(ct);
    }

    private static (TestableService service, Mock<IWebPushService> push, Mock<IEmailService> email) BuildService(ApplicationDbContext db, bool pushDelivers = true, bool emailDelivers = true, bool emailThrows = false)
    {
        var push = new Mock<IWebPushService>();
        push.Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pushDelivers);

        var email = new Mock<IEmailService>();
        var emailSetup = email.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>?>()));
        if (emailThrows) emailSetup.ThrowsAsync(new InvalidOperationException("boom"));
        else emailSetup.ReturnsAsync(emailDelivers);

        var sp = new Mock<IServiceProvider>();
        sp.Setup(x => x.GetService(typeof(ApplicationDbContext))).Returns(db);
        sp.Setup(x => x.GetService(typeof(IWebPushService))).Returns(push.Object);
        sp.Setup(x => x.GetService(typeof(IEmailService))).Returns(email.Object);

        var scope = new Mock<IServiceScope>();
        scope.Setup(x => x.ServiceProvider).Returns(sp.Object);

        var factory = new Mock<IServiceScopeFactory>();
        factory.Setup(x => x.CreateScope()).Returns(scope.Object);

        return (new TestableService(factory.Object), push, email);
    }

    private static UserProfile MakeProfile(string id, bool pushEnabled = true, int thresholdHours = 4, bool emailEnabled = false) =>
        new()
        {
            Id = id,
            PushNotificationsEnabled = pushEnabled,
            EmailNotificationsEnabled = emailEnabled,
            OfflineAlertThresholdHours = thresholdHours,
            User = new User { Id = id, UserName = id, Email = $"{id}@example.com", FirstName = "Test", LastName = "User" }
        };

    [Fact]
    public async Task CheckAsync_SensorOffline_SendsPushAndCreatesNotification()
    {
        var db = CreateDbContext();
        var profile = MakeProfile("u1");
        db.UserProfiles.Add(profile);
        db.UserSensors.Add(new UserSensor { UserId = "u1", SensorId = 1 });
        // Last data 10 hours ago, threshold is 4 hours → offline
        db.SensorData.Add(new SensorData { SensorId = 1, Value = "20", Timestamp = DateTime.UtcNow.AddHours(-10) });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (svc, push, _) = BuildService(db);
        await svc.RunCheckAsync(TestContext.Current.CancellationToken);

        push.Verify(p => p.SendAsync("u1", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single(db.SensorOfflineNotifications);
        Assert.Null(db.SensorOfflineNotifications.First().ResolvedAt);
    }

    [Fact]
    public async Task CheckAsync_SensorOfflineAlreadyNotified_SkipsPush()
    {
        var db = CreateDbContext();
        var profile = MakeProfile("u1");
        db.UserProfiles.Add(profile);
        db.UserSensors.Add(new UserSensor { UserId = "u1", SensorId = 1 });
        db.SensorData.Add(new SensorData { SensorId = 1, Value = "20", Timestamp = DateTime.UtcNow.AddHours(-10) });
        db.SensorOfflineNotifications.Add(new SensorOfflineNotification
        {
            UserId = "u1", SensorId = 1, NotifiedAt = DateTime.UtcNow.AddHours(-1)
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (svc, push, _) = BuildService(db);
        await svc.RunCheckAsync(TestContext.Current.CancellationToken);

        push.Verify(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_SensorBackOnline_ResolvesActiveNotification()
    {
        var db = CreateDbContext();
        var profile = MakeProfile("u1");
        db.UserProfiles.Add(profile);
        db.UserSensors.Add(new UserSensor { UserId = "u1", SensorId = 1 });
        // Last data 1 hour ago, threshold is 4 hours → online
        db.SensorData.Add(new SensorData { SensorId = 1, Value = "20", Timestamp = DateTime.UtcNow.AddHours(-1) });
        var notification = new SensorOfflineNotification
        {
            UserId = "u1", SensorId = 1, NotifiedAt = DateTime.UtcNow.AddHours(-5)
        };
        db.SensorOfflineNotifications.Add(notification);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (svc, push, _) = BuildService(db);
        await svc.RunCheckAsync(TestContext.Current.CancellationToken);

        push.Verify(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        var resolved = await db.SensorOfflineNotifications.FindAsync([notification.Id], TestContext.Current.CancellationToken);
        Assert.NotNull(resolved!.ResolvedAt);
    }

    [Fact]
    public async Task CheckAsync_SensorNeverReported_SendsPush()
    {
        var db = CreateDbContext();
        var profile = MakeProfile("u1");
        db.UserProfiles.Add(profile);
        db.UserSensors.Add(new UserSensor { UserId = "u1", SensorId = 1 });
        // No SensorData at all
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (svc, push, _) = BuildService(db);
        await svc.RunCheckAsync(TestContext.Current.CancellationToken);

        push.Verify(p => p.SendAsync("u1", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckAsync_PushAndEmailDisabled_SkipsAllSensors()
    {
        var db = CreateDbContext();
        var profile = MakeProfile("u1", pushEnabled: false);
        db.UserProfiles.Add(profile);
        db.UserSensors.Add(new UserSensor { UserId = "u1", SensorId = 1 });
        db.SensorData.Add(new SensorData { SensorId = 1, Value = "20", Timestamp = DateTime.UtcNow.AddHours(-10) });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (svc, push, _) = BuildService(db);
        await svc.RunCheckAsync(TestContext.Current.CancellationToken);

        push.Verify(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(db.SensorOfflineNotifications);
    }

    [Fact]
    public async Task CheckAsync_SensorOnlineWithNoActiveNotification_DoesNothing()
    {
        var db = CreateDbContext();
        var profile = MakeProfile("u1");
        db.UserProfiles.Add(profile);
        db.UserSensors.Add(new UserSensor { UserId = "u1", SensorId = 1 });
        db.SensorData.Add(new SensorData { SensorId = 1, Value = "20", Timestamp = DateTime.UtcNow.AddMinutes(-30) });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (svc, push, _) = BuildService(db);
        await svc.RunCheckAsync(TestContext.Current.CancellationToken);

        push.Verify(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(db.SensorOfflineNotifications);
    }
    [Fact]
    public async Task CheckAsync_OpenGargeSecurityAlert_SuppressesOfflineAlert()
    {
        var db = CreateDbContext();
        db.UserProfiles.Add(MakeProfile("u1"));
        db.UserSensors.Add(new UserSensor { UserId = "u1", SensorId = 1 });
        db.SensorData.Add(new SensorData { SensorId = 1, Value = "20", Timestamp = DateTime.UtcNow.AddHours(-10) });
        db.SensorOfflineNotifications.Add(new SensorOfflineNotification
        {
            UserId = "u1", SensorId = 1, Kind = NotificationKinds.Security, NotifiedAt = DateTime.UtcNow.AddHours(-9)
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (svc, push, _) = BuildService(db);
        await svc.RunCheckAsync(TestContext.Current.CancellationToken);

        push.Verify(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Single(db.SensorOfflineNotifications);
    }

    [Fact]
    public async Task CheckAsync_BackOnline_DoesNotResolveGargeSecurityAlert()
    {
        var db = CreateDbContext();
        db.UserProfiles.Add(MakeProfile("u1"));
        db.UserSensors.Add(new UserSensor { UserId = "u1", SensorId = 1 });
        db.SensorData.Add(new SensorData { SensorId = 1, Value = "20", Timestamp = DateTime.UtcNow.AddMinutes(-5) });
        db.SensorOfflineNotifications.Add(new SensorOfflineNotification
        {
            UserId = "u1", SensorId = 1, Kind = NotificationKinds.Security, NotifiedAt = DateTime.UtcNow.AddHours(-1)
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (svc, _, _) = BuildService(db);
        await svc.RunCheckAsync(TestContext.Current.CancellationToken);

        Assert.Null(db.SensorOfflineNotifications.Single().ResolvedAt);
    }

    private async Task<ApplicationDbContext> SeedOfflineAsync(bool push, bool email)
    {
        var db = CreateDbContext();
        db.UserProfiles.Add(MakeProfile("u1", pushEnabled: push, emailEnabled: email));
        db.UserSensors.Add(new UserSensor { UserId = "u1", SensorId = 1 });
        db.SensorData.Add(new SensorData { SensorId = 1, Value = "20", Timestamp = DateTime.UtcNow.AddHours(-10) });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return db;
    }

    private static void VerifyEmails(Mock<IEmailService> email, Times times) =>
        email.Verify(e => e.SendEmailAsync("u1@example.com", "Sensor offline", It.Is<string>(h => h.Contains("has not reported in over 4h")), It.IsAny<IReadOnlyList<EmailAttachment>?>()), times);

    private static void VerifyPushes(Mock<IWebPushService> push, Times times) =>
        push.Verify(p => p.SendAsync("u1", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task CheckAsync_EmailOnly_SendsEmailAndRecordsNotification()
    {
        var db = await SeedOfflineAsync(push: false, email: true);
        var (svc, push, email) = BuildService(db);
        await svc.RunCheckAsync(TestContext.Current.CancellationToken);

        VerifyEmails(email, Times.Once());
        VerifyPushes(push, Times.Never());
        Assert.Single(db.SensorOfflineNotifications);
    }

    [Fact]
    public async Task CheckAsync_PushOnly_SendsNoEmail()
    {
        var db = await SeedOfflineAsync(push: true, email: false);
        var (svc, push, email) = BuildService(db);
        await svc.RunCheckAsync(TestContext.Current.CancellationToken);

        VerifyPushes(push, Times.Once());
        VerifyEmails(email, Times.Never());
        Assert.Single(db.SensorOfflineNotifications);
    }

    [Fact]
    public async Task CheckAsync_PushAndEmail_SendsBothAndRecordsOnce()
    {
        var db = await SeedOfflineAsync(push: true, email: true);
        var (svc, push, email) = BuildService(db);
        await svc.RunCheckAsync(TestContext.Current.CancellationToken);

        VerifyPushes(push, Times.Once());
        VerifyEmails(email, Times.Once());
        Assert.Single(db.SensorOfflineNotifications);
    }

    [Fact]
    public async Task CheckAsync_NeitherChannel_SendsNothing()
    {
        var db = await SeedOfflineAsync(push: false, email: false);
        var (svc, push, email) = BuildService(db);
        await svc.RunCheckAsync(TestContext.Current.CancellationToken);

        VerifyPushes(push, Times.Never());
        VerifyEmails(email, Times.Never());
        Assert.Empty(db.SensorOfflineNotifications);
    }

    [Fact]
    public async Task CheckAsync_EmailThrows_PushStillSentAndNotificationRecorded()
    {
        var db = await SeedOfflineAsync(push: true, email: true);
        var (svc, push, _) = BuildService(db, emailThrows: true);
        await svc.RunCheckAsync(TestContext.Current.CancellationToken);

        VerifyPushes(push, Times.Once());
        Assert.Single(db.SensorOfflineNotifications);
    }

    [Fact]
    public async Task CheckAsync_EmailThrowsAndNoPush_RecordsNothingSoItRetries()
    {
        var db = await SeedOfflineAsync(push: false, email: true);
        var (svc, _, _) = BuildService(db, emailThrows: true);
        await svc.RunCheckAsync(TestContext.Current.CancellationToken);

        Assert.Empty(db.SensorOfflineNotifications);
    }

    [Fact]
    public async Task CheckAsync_PushThrows_EmailStillSent()
    {
        var db = await SeedOfflineAsync(push: true, email: true);
        var (svc, push, email) = BuildService(db);
        push.Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        await svc.RunCheckAsync(TestContext.Current.CancellationToken);

        VerifyEmails(email, Times.Once());
        Assert.Single(db.SensorOfflineNotifications);
    }

    [Fact]
    public async Task CheckAsync_RunTwice_SendsOneEmailPerOfflineEvent()
    {
        var db = await SeedOfflineAsync(push: false, email: true);
        var (svc, _, email) = BuildService(db);
        await svc.RunCheckAsync(TestContext.Current.CancellationToken);
        await svc.RunCheckAsync(TestContext.Current.CancellationToken);

        VerifyEmails(email, Times.Once());
        Assert.Single(db.SensorOfflineNotifications);
    }
}
