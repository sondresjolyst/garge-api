using garge_api.Controllers;
using garge_api.Dtos.Sensor;
using garge_api.Hubs;
using garge_api.Models;
using garge_api.Models.Sensor;
using garge_api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// Verifies the ownership-window read filter: a user only sees telemetry recorded inside their own
/// ownership period(s), so a new owner of a re-claimed/resold sensor never sees the previous owner's
/// history. Also covers period lifecycle on claim/unclaim.
/// </summary>
public class SensorOwnershipWindowTests : ControllerTestBase
{
    private const int SensorId = 1;
    private static readonly DateTime ACancel = new(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private SensorController CreateController(ApplicationDbContext db, string userId, bool isAdmin)
    {
        var ownership = new Mock<IDeviceOwnershipService>();
        ownership.Setup(o => o.CanUserAccessSensorAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        var hub = new Mock<IHubContext<DeviceHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);

        var capacity = new SubscriptionCapacityService(db, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));

        var controller = new SensorController(
            db, MockMapper.Object, NullLogger<SensorController>.Instance, ownership.Object, hub.Object, capacity);
        controller.ControllerContext = MakeControllerContext(userId, isAdmin);
        return controller;
    }

    /// <summary>Sensor owned first by A [..,ACancel), then resold to C [ACancel,..). 3 readings in A's window, 2 in C's.</summary>
    private static void SeedResaleHistory(ApplicationDbContext db)
    {
        db.Sensors.Add(new Sensor
        {
            Id = SensorId, Name = "garge_volt", Type = "voltage", Role = "sensor",
            RegistrationCode = "rc-1", DefaultName = "Battery", ParentName = "garge_test"
        });

        db.SensorOwnershipPeriods.Add(new SensorOwnershipPeriod
        {
            UserId = "user-A", SensorId = SensorId,
            StartedAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), EndedAt = ACancel
        });
        db.SensorOwnershipPeriods.Add(new SensorOwnershipPeriod
        {
            UserId = "user-C", SensorId = SensorId, StartedAt = ACancel, EndedAt = null
        });

        db.SensorData.AddRange(
            new SensorData { SensorId = SensorId, Value = "10", Timestamp = new DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc) },
            new SensorData { SensorId = SensorId, Value = "11", Timestamp = new DateTime(2020, 3, 1, 0, 0, 0, DateTimeKind.Utc) },
            new SensorData { SensorId = SensorId, Value = "12", Timestamp = new DateTime(2020, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
            new SensorData { SensorId = SensorId, Value = "13", Timestamp = new DateTime(2020, 7, 1, 0, 0, 0, DateTimeKind.Utc) },
            new SensorData { SensorId = SensorId, Value = "14", Timestamp = new DateTime(2020, 8, 1, 0, 0, 0, DateTimeKind.Utc) });
        db.SaveChanges();
    }

    private static int TotalCount(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return (int)ok.Value!.GetType().GetProperty("TotalCount")!.GetValue(ok.Value)!;
    }

    [Fact]
    public async Task GetSensorData_ResaleOwner_SeesOnlyOwnWindow()
    {
        using var db = CreateDbContext();
        SeedResaleHistory(db);
        var controller = CreateController(db, "user-C", isAdmin: false);

        var result = await controller.GetSensorData(SensorId, null, null, null, groupBy: "");

        // C must NOT see A's 3 pre-resale readings — only their own 2.
        Assert.Equal(2, TotalCount(result));
    }

    [Fact]
    public async Task GetSensorData_Admin_SeesAllWindows()
    {
        using var db = CreateDbContext();
        SeedResaleHistory(db);
        var controller = CreateController(db, "admin-1", isAdmin: true);

        var result = await controller.GetSensorData(SensorId, null, null, null, groupBy: "");

        Assert.Equal(5, TotalCount(result));
    }

    [Fact]
    public async Task GetLatestSensorData_ResaleOwner_GetsLatestInOwnWindow()
    {
        using var db = CreateDbContext();
        SeedResaleHistory(db);
        MockMapper.Setup(m => m.Map<SensorDataDto>(It.IsAny<SensorData>()))
            .Returns((SensorData s) => new SensorDataDto { Id = s.Id, SensorId = s.SensorId, Value = s.Value, Timestamp = s.Timestamp });
        var controller = CreateController(db, "user-C", isAdmin: false);

        var result = await controller.GetLatestSensorData(SensorId);

        var dto = Assert.IsType<SensorDataDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("14", dto.Value); // C's latest, not A's old readings
    }

    [Fact]
    public async Task ClaimSensor_FirstEverOwner_OpensPeriodAtEpochSentinel()
    {
        using var db = CreateDbContext();
        db.Sensors.Add(new Sensor
        {
            Id = SensorId, Name = "garge_volt", Type = "voltage", Role = "sensor",
            RegistrationCode = "rc-1", DefaultName = "Battery", ParentName = "garge_test"
        });
        db.Users.Add(MakeUser("user-A"));
        await db.SaveChangesAsync();
        var controller = CreateController(db, "user-A", isAdmin: false);

        await controller.ClaimSensor(new ClaimSensorDto { RegistrationCode = "rc-1" });

        var period = Assert.Single(db.SensorOwnershipPeriods.Where(p => p.UserId == "user-A"));
        Assert.Equal(SensorOwnershipPeriod.FirstOwnerStart, period.StartedAt);
        Assert.Null(period.EndedAt);
    }

    [Fact]
    public async Task ClaimAfterResale_NewOwner_StartsAtClaimTimeNotEpoch()
    {
        using var db = CreateDbContext();
        db.Sensors.Add(new Sensor
        {
            Id = SensorId, Name = "garge_volt", Type = "voltage", Role = "sensor",
            RegistrationCode = "rc-1", DefaultName = "Battery", ParentName = "garge_test"
        });
        db.Users.Add(MakeUser("user-A"));
        db.Users.Add(MakeUser("user-B", "b@example.com"));
        await db.SaveChangesAsync();

        var before = DateTime.UtcNow;
        await CreateController(db, "user-A", isAdmin: false).ClaimSensor(new ClaimSensorDto { RegistrationCode = "rc-1" });
        await CreateController(db, "user-A", isAdmin: false).UnclaimSensor(SensorId);
        await CreateController(db, "user-B", isAdmin: false).ClaimSensor(new ClaimSensorDto { RegistrationCode = "rc-1" });

        var aPeriod = Assert.Single(db.SensorOwnershipPeriods.Where(p => p.UserId == "user-A"));
        Assert.NotNull(aPeriod.EndedAt); // closed on unclaim

        var bPeriod = Assert.Single(db.SensorOwnershipPeriods.Where(p => p.UserId == "user-B"));
        Assert.NotEqual(SensorOwnershipPeriod.FirstOwnerStart, bPeriod.StartedAt);
        Assert.True(bPeriod.StartedAt >= before); // resale owner starts at claim time
        Assert.Null(bPeriod.EndedAt);
    }
}
