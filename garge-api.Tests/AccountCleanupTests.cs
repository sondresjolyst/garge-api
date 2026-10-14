using garge_api.Hubs;
using garge_api.Models;
using garge_api.Models.Push;
using garge_api.Models.Sensor;
using garge_api.Models.Shop;
using garge_api.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace garge_api.Tests;

public class AccountCleanupTests : ControllerTestBase
{
    private static User MakeDbUser(string id = "user-1") => new()
    {
        Id = id, UserName = "userone", Email = "user@test.com",
        FirstName = "First", LastName = "Last", TermsAcceptedIp = "203.0.113.7"
    };

    private static AccountDataEraser Eraser(ApplicationDbContext db) =>
        new(db, new AnonymizationService(db, NullLogger<AnonymizationService>.Instance), new Mock<IDeviceOwnershipService>().Object);

    [Fact]
    public async Task Erasing_clears_the_terms_ip_and_keeps_orders()
    {
        using var db = CreateDbContext();
        var user = MakeDbUser();
        db.Orders.Add(new Order { Id = 1, UserId = user.Id, VippsOrderId = "garge-order-000001", TotalInOre = 9900, Status = OrderStatus.Paid });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Eraser(db).EraseAsync(user, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Null(user.TermsAcceptedIp);
        Assert.Equal(OrderStatus.Paid, Assert.Single(db.Orders).Status);
    }

    [Fact]
    public async Task Erasing_removes_the_user_owned_rows()
    {
        using var db = CreateDbContext();
        var user = MakeDbUser();
        db.PushSubscriptions.Add(new PushSubscription { Id = 1, UserId = user.Id, Endpoint = "https://push", P256dh = "k", Auth = "a" });
        db.PushSubscriptions.Add(new PushSubscription { Id = 2, UserId = "someone-else", Endpoint = "https://push2", P256dh = "k", Auth = "a" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Eraser(db).EraseAsync(user, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("someone-else", Assert.Single(db.PushSubscriptions).UserId);
    }

    [Fact]
    public async Task Erasing_moves_telemetry_to_the_ml_store_and_leaves_no_orphans()
    {
        using var db = CreateDbContext();
        var user = MakeDbUser();
        db.Sensors.Add(new Sensor { Id = 1, Name = "garge_volt", Type = "voltage", Role = "sensor", RegistrationCode = "rc", DefaultName = "Battery", ParentName = "gw" });
        db.UserSensors.Add(new UserSensor { UserId = user.Id, SensorId = 1, IsOwner = true });
        db.SensorOwnershipPeriods.Add(new SensorOwnershipPeriod { UserId = user.Id, SensorId = 1, StartedAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), EndedAt = null });
        db.SensorData.AddRange(
            new SensorData { SensorId = 1, Value = "12.5", Timestamp = new DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc) },
            new SensorData { SensorId = 1, Value = "12.6", Timestamp = new DateTime(2020, 3, 1, 0, 0, 0, DateTimeKind.Utc) });
        db.BatteryHealthData.Add(new BatteryHealth { SensorId = 1, Status = "ok", Timestamp = new DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc) });
        db.SensorPhotos.Add(new SensorPhoto { UserId = user.Id, SensorId = 1, ContentType = "image/jpeg", Data = "AQID" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Eraser(db).EraseAsync(user, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, db.AnonymizedReadings.Count());
        Assert.Equal("voltage", db.AnonymizedSeries.Single().SourceType);
        Assert.Empty(db.SensorData);
        Assert.Empty(db.BatteryHealthData);
        Assert.Empty(db.SensorPhotos);
        Assert.Empty(db.SensorOwnershipPeriods);
        Assert.Empty(db.UserSensors);
    }

    [Fact]
    public async Task The_handler_tells_open_connections_to_sign_out()
    {
        var tracker = new Mock<IHubConnectionTracker>();
        tracker.Setup(t => t.GetConnectionIds("user-1")).Returns(new[] { "c1", "c2" });
        var proxy = new Mock<IClientProxy>();
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Clients(It.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "c1", "c2" })))).Returns(proxy.Object);
        var hub = new Mock<IHubContext<DeviceHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);

        await new AccountHubDisconnect(tracker.Object, hub.Object, NullLogger<AccountHubDisconnect>.Instance)
            .OnDeletedAsync(MakeDbUser(), TestContext.Current.CancellationToken);

        proxy.Verify(p => p.SendCoreAsync("forceLogout", It.Is<object?[]>(a => a.Length == 0), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task The_handler_does_nothing_without_connections()
    {
        var tracker = new Mock<IHubConnectionTracker>();
        tracker.Setup(t => t.GetConnectionIds("user-1")).Returns(Array.Empty<string>());
        var hub = new Mock<IHubContext<DeviceHub>>(MockBehavior.Strict);

        await new AccountHubDisconnect(tracker.Object, hub.Object, NullLogger<AccountHubDisconnect>.Instance)
            .OnDeletedAsync(MakeDbUser(), TestContext.Current.CancellationToken);

        hub.VerifyNoOtherCalls();
    }
}
