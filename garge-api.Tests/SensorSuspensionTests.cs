using garge_api.Controllers;
using garge_api.Hubs;
using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Models.Sensor;
using garge_api.Models.Subscription;
using garge_api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// Verifies suspension enforcement: suspended sensors block data reads (403 locked) and drop out of
/// multi-reads, the suspend/activate toggle works, and activating is rejected when over plan capacity.
/// </summary>
public class SensorSuspensionTests : ControllerTestBase
{
    private SensorController CreateController(ApplicationDbContext db, string userId, bool isAdmin = false)
    {
        var ownership = new Mock<IDeviceOwnershipService>();
        ownership.Setup(o => o.CanUserAccessSensorAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        var hub = new Mock<IHubContext<DeviceHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);

        var capacity = new SubscriptionCapacityService(db, new MemoryCache(new MemoryCacheOptions()));
        var controller = new SensorController(db, MockMapper.Object, NullLogger<SensorController>.Instance, ownership.Object, hub.Object, capacity);
        controller.ControllerContext = MakeControllerContext(userId, isAdmin);
        return controller;
    }

    private static Sensor MakeSensor(int id) => new()
    {
        Id = id, Name = $"garge_volt_{id}", Type = "voltage", Role = "sensor",
        RegistrationCode = $"rc{id}", DefaultName = "Battery", ParentName = "gw"
    };

    private static readonly DateTime Epoch = SensorOwnershipPeriod.FirstOwnerStart;

    private static int TotalCount(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return (int)ok.Value!.GetType().GetProperty("TotalCount")!.GetValue(ok.Value)!;
    }

    [Fact]
    public async Task GetSensorData_SuspendedSensor_Returns403Locked()
    {
        using var db = CreateDbContext();
        db.Sensors.Add(MakeSensor(1));
        db.UserSensors.Add(new UserSensor { UserId = "u", SensorId = 1, IsOwner = true, SuspendedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var result = await CreateController(db, "u").GetSensorData(1, null, null, null, groupBy: "");

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, obj.StatusCode);
    }

    [Fact]
    public async Task GetMultipleSensorsData_ExcludesSuspendedSensors()
    {
        using var db = CreateDbContext();
        db.Sensors.AddRange(MakeSensor(1), MakeSensor(2));
        db.UserSensors.AddRange(
            new UserSensor { UserId = "u", SensorId = 1, IsOwner = true },
            new UserSensor { UserId = "u", SensorId = 2, IsOwner = true, SuspendedAt = DateTime.UtcNow });
        db.SensorOwnershipPeriods.AddRange(
            new SensorOwnershipPeriod { UserId = "u", SensorId = 1, StartedAt = Epoch, EndedAt = null },
            new SensorOwnershipPeriod { UserId = "u", SensorId = 2, StartedAt = Epoch, EndedAt = null });
        db.SensorData.AddRange(
            new SensorData { SensorId = 1, Value = "1", Timestamp = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new SensorData { SensorId = 1, Value = "2", Timestamp = new DateTime(2021, 1, 2, 0, 0, 0, DateTimeKind.Utc) },
            new SensorData { SensorId = 2, Value = "9", Timestamp = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        await db.SaveChangesAsync();

        var result = await CreateController(db, "u").GetMultipleSensorsData(new List<int> { 1, 2 }, null, null, null, groupBy: "");

        Assert.Equal(2, TotalCount(result)); // only sensor 1's rows; suspended sensor 2 excluded
    }

    [Fact]
    public async Task SuspendSensor_SetsSuspendedAt()
    {
        using var db = CreateDbContext();
        db.Sensors.Add(MakeSensor(1));
        db.UserSensors.Add(new UserSensor { UserId = "u", SensorId = 1, IsOwner = true });
        await db.SaveChangesAsync();

        var result = await CreateController(db, "u").SuspendSensor(1);

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(db.UserSensors.Single().SuspendedAt);
    }

    [Fact]
    public async Task SuspendSensor_NotOwner_Returns404()
    {
        using var db = CreateDbContext();
        var result = await CreateController(db, "u").SuspendSensor(99);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task ActivateSensor_WithinCapacity_ClearsSuspendedAt()
    {
        using var db = CreateDbContext();
        SeedPrimary(db, "u");
        db.Sensors.Add(MakeSensor(1));
        db.UserSensors.Add(new UserSensor { UserId = "u", SensorId = 1, IsOwner = true, SuspendedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var result = await CreateController(db, "u").ActivateSensor(1);

        Assert.IsType<OkObjectResult>(result);
        Assert.Null(db.UserSensors.Single().SuspendedAt);
    }

    [Fact]
    public async Task ActivateSensor_OverCapacity_Returns400_AndStaysSuspended()
    {
        using var db = CreateDbContext();
        SeedPrimary(db, "u"); // capacity = 1
        db.Sensors.AddRange(MakeSensor(1), MakeSensor(2));
        db.UserSensors.AddRange(
            new UserSensor { UserId = "u", SensorId = 2, IsOwner = true },                              // active -> uses the 1 slot
            new UserSensor { UserId = "u", SensorId = 1, IsOwner = true, SuspendedAt = DateTime.UtcNow }); // wants to come back on
        await db.SaveChangesAsync();

        var result = await CreateController(db, "u").ActivateSensor(1);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(db.UserSensors.Single(x => x.SensorId == 1).SuspendedAt); // still off
    }

    private static void SeedPrimary(ApplicationDbContext db, string userId)
    {
        db.AppSettings.Add(new AppSettings { Id = 1 });
        db.Products.Add(new Product { Id = 1, Name = "Primary", PriceInOre = 0, Interval = BillingInterval.Monthly, Type = ProductType.Primary });
        db.Subscriptions.Add(new Subscription { UserId = userId, ProductId = 1, VippsAgreementId = "a", Status = SubscriptionStatus.Active, Quantity = 1 });
    }
}
