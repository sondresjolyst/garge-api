using garge_api.Controllers;
using garge_api.Dtos.Switch;
using garge_api.Hubs;
using garge_api.Models;
using garge_api.Models.Mqtt;
using garge_api.Models.Sensor;
using garge_api.Models.Switch;
using garge_api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// Verifies the switch ownership-window read filter for both direct owners (SwitchOwnershipPeriod)
/// and indirect owners (access via an owned sensor's discovered-device chain, bounded by the sensor's
/// ownership period). A new owner of a re-claimed/resold switch never sees the previous owner's history.
/// </summary>
public class SwitchOwnershipWindowTests : ControllerTestBase
{
    private const int SwitchId = 1;
    private static readonly DateTime Cancel = new(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private SwitchesController CreateController(ApplicationDbContext db, string userId, bool isAdmin)
    {
        var ownership = new Mock<IDeviceOwnershipService>();
        ownership.Setup(o => o.CanUserAccessSwitchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        var hub = new Mock<IHubContext<DeviceHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);

        MockMapper.Setup(m => m.Map<IEnumerable<SwitchDataDto>>(It.IsAny<List<SwitchData>>()))
            .Returns((List<SwitchData> src) => src.Select(s => new SwitchDataDto { Id = s.Id, SwitchId = s.SwitchId, Value = s.Value, Timestamp = s.Timestamp }).ToList());

        var controller = new SwitchesController(
            db, MockMapper.Object, NullLogger<SwitchesController>.Instance, ownership.Object, hub.Object);
        controller.ControllerContext = MakeControllerContext(userId, isAdmin);
        return controller;
    }

    private static void AddData(ApplicationDbContext db) =>
        db.SwitchData.AddRange(
            new SwitchData { SwitchId = SwitchId, Value = "on", Timestamp = new DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc) },
            new SwitchData { SwitchId = SwitchId, Value = "off", Timestamp = new DateTime(2020, 3, 1, 0, 0, 0, DateTimeKind.Utc) },
            new SwitchData { SwitchId = SwitchId, Value = "on", Timestamp = new DateTime(2020, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
            new SwitchData { SwitchId = SwitchId, Value = "off", Timestamp = new DateTime(2020, 7, 1, 0, 0, 0, DateTimeKind.Utc) },
            new SwitchData { SwitchId = SwitchId, Value = "on", Timestamp = new DateTime(2020, 8, 1, 0, 0, 0, DateTimeKind.Utc) });

    private static Switch MakeSwitch() => new()
    {
        Id = SwitchId, Name = "garge_socket", Type = "socket", Role = "switch", RegistrationCode = "rc-1"
    };

    private static int Count(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsAssignableFrom<IEnumerable<SwitchDataDto>>(ok.Value).Count();
    }

    [Fact]
    public async Task GetSwitchData_DirectResaleOwner_SeesOnlyOwnWindow()
    {
        using var db = CreateDbContext();
        db.Switches.Add(MakeSwitch());
        db.SwitchOwnershipPeriods.Add(new SwitchOwnershipPeriod { UserId = "user-A", SwitchId = SwitchId, StartedAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), EndedAt = Cancel });
        db.SwitchOwnershipPeriods.Add(new SwitchOwnershipPeriod { UserId = "user-C", SwitchId = SwitchId, StartedAt = Cancel, EndedAt = null });
        AddData(db);
        await db.SaveChangesAsync();

        var result = await CreateController(db, "user-C", isAdmin: false).GetSwitchData(SwitchId, null, null, null);

        Assert.Equal(2, Count(result)); // not A's 3 pre-resale rows
    }

    [Fact]
    public async Task GetSwitchData_Admin_SeesAllWindows()
    {
        using var db = CreateDbContext();
        db.Switches.Add(MakeSwitch());
        db.SwitchOwnershipPeriods.Add(new SwitchOwnershipPeriod { UserId = "user-C", SwitchId = SwitchId, StartedAt = Cancel, EndedAt = null });
        AddData(db);
        await db.SaveChangesAsync();

        var result = await CreateController(db, "admin-1", isAdmin: true).GetSwitchData(SwitchId, null, null, null);

        Assert.Equal(5, Count(result));
    }

    [Fact]
    public async Task GetSwitchData_IndirectOwnerViaSensorChain_BoundedBySensorPeriod()
    {
        using var db = CreateDbContext();
        db.Switches.Add(MakeSwitch());
        // Indirect access: own sensor 10 whose ParentName -> DiscoveredDevice.Target = switch name.
        db.DiscoveredDevices.Add(new DiscoveredDevice { DiscoveredBy = "garge_gw", Target = "garge_socket", Type = "switch", Timestamp = Cancel });
        db.Sensors.Add(new Sensor { Id = 10, Name = "garge_volt", Type = "voltage", Role = "sensor", RegistrationCode = "rc-s", DefaultName = "Battery", ParentName = "garge_gw" });
        db.SensorOwnershipPeriods.Add(new SensorOwnershipPeriod { UserId = "user-D", SensorId = 10, StartedAt = Cancel, EndedAt = null });
        // user-D has NO SwitchOwnershipPeriod — access and window come purely from the sensor chain.
        AddData(db);
        await db.SaveChangesAsync();

        var result = await CreateController(db, "user-D", isAdmin: false).GetSwitchData(SwitchId, null, null, null);

        Assert.Equal(2, Count(result)); // only data within D's sensor ownership window
    }

    [Fact]
    public async Task ClaimSwitch_FirstEverOwner_OpensPeriodAtEpochSentinel()
    {
        using var db = CreateDbContext();
        db.Switches.Add(MakeSwitch());
        await db.SaveChangesAsync();

        await CreateController(db, "user-A", isAdmin: false).ClaimSwitch(new ClaimSwitchDto { RegistrationCode = "rc-1" });

        var period = Assert.Single(db.SwitchOwnershipPeriods.Where(p => p.UserId == "user-A"));
        Assert.Equal(SwitchOwnershipPeriod.FirstOwnerStart, period.StartedAt);
        Assert.Null(period.EndedAt);
    }

    [Fact]
    public async Task ClaimAfterResale_NewOwner_StartsAtClaimTimeNotEpoch()
    {
        using var db = CreateDbContext();
        db.Switches.Add(MakeSwitch());
        await db.SaveChangesAsync();

        var before = DateTime.UtcNow;
        await CreateController(db, "user-A", isAdmin: false).ClaimSwitch(new ClaimSwitchDto { RegistrationCode = "rc-1" });
        await CreateController(db, "user-A", isAdmin: false).UnclaimSwitch(SwitchId);
        await CreateController(db, "user-B", isAdmin: false).ClaimSwitch(new ClaimSwitchDto { RegistrationCode = "rc-1" });

        var aPeriod = Assert.Single(db.SwitchOwnershipPeriods.Where(p => p.UserId == "user-A"));
        Assert.NotNull(aPeriod.EndedAt); // closed on unclaim

        var bPeriod = Assert.Single(db.SwitchOwnershipPeriods.Where(p => p.UserId == "user-B"));
        Assert.NotEqual(SwitchOwnershipPeriod.FirstOwnerStart, bPeriod.StartedAt);
        Assert.True(bPeriod.StartedAt >= before);
        Assert.Null(bPeriod.EndedAt);
    }
}
