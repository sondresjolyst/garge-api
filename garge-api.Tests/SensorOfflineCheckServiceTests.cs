using garge_api.Models;
using garge_api.Models.Push;
using garge_api.Models.Sensor;
using garge_api.Services;
using Microsoft.EntityFrameworkCore;
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

    private static (TestableService service, Mock<IWebPushService> push) BuildService(ApplicationDbContext db)
    {
        var push = new Mock<IWebPushService>();

        var sp = new Mock<IServiceProvider>();
        sp.Setup(x => x.GetService(typeof(ApplicationDbContext))).Returns(db);
        sp.Setup(x => x.GetService(typeof(IWebPushService))).Returns(push.Object);

        var scope = new Mock<IServiceScope>();
        scope.Setup(x => x.ServiceProvider).Returns(sp.Object);

        var factory = new Mock<IServiceScopeFactory>();
        factory.Setup(x => x.CreateScope()).Returns(scope.Object);

        return (new TestableService(factory.Object), push);
    }

    private static UserProfile MakeProfile(string id, bool pushEnabled = true, int thresholdHours = 4) =>
        new()
        {
            Id = id,
            FirstName = "Test",
            LastName = "User",
            Email = $"{id}@example.com",
            PushNotificationsEnabled = pushEnabled,
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
        await db.SaveChangesAsync();

        var (svc, push) = BuildService(db);
        await svc.RunCheckAsync();

        push.Verify(p => p.SendAsync("u1", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
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
        await db.SaveChangesAsync();

        var (svc, push) = BuildService(db);
        await svc.RunCheckAsync();

        push.Verify(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
        await db.SaveChangesAsync();

        var (svc, push) = BuildService(db);
        await svc.RunCheckAsync();

        push.Verify(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var resolved = await db.SensorOfflineNotifications.FindAsync(notification.Id);
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
        await db.SaveChangesAsync();

        var (svc, push) = BuildService(db);
        await svc.RunCheckAsync();

        push.Verify(p => p.SendAsync("u1", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckAsync_PushDisabled_SkipsAllSensors()
    {
        var db = CreateDbContext();
        var profile = MakeProfile("u1", pushEnabled: false);
        db.UserProfiles.Add(profile);
        db.UserSensors.Add(new UserSensor { UserId = "u1", SensorId = 1 });
        db.SensorData.Add(new SensorData { SensorId = 1, Value = "20", Timestamp = DateTime.UtcNow.AddHours(-10) });
        await db.SaveChangesAsync();

        var (svc, push) = BuildService(db);
        await svc.RunCheckAsync();

        push.Verify(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
        await db.SaveChangesAsync();

        var (svc, push) = BuildService(db);
        await svc.RunCheckAsync();

        push.Verify(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(db.SensorOfflineNotifications);
    }
}
