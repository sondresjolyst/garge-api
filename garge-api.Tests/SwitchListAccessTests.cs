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
/// Guards the row set returned by GetAllSwitches after the access filter was moved from a per-row
/// ownership-service check to a single batched set query. The set must be identical for every caller
/// role: a direct owner, a shared viewer (Read/Edit), an indirect owner (via an owned sensor's
/// discovered-device chain), an admin, and an unauthorized stranger. Only SOCKET switches are listed.
/// </summary>
public class SwitchListAccessTests : ControllerTestBase
{
    private static SwitchesController CreateController(ApplicationDbContext db, string userId, bool isAdmin = false)
    {
        // The list endpoint no longer routes its access filter through the ownership service, so the
        // mock here only satisfies the other endpoints' contract; it does not gate GetAllSwitches.
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

        var controller = new SwitchesController(
            db, RealMapper, NullLogger<SwitchesController>.Instance, ownership.Object, hub.Object);
        controller.ControllerContext = MakeControllerContext(userId, isAdmin);
        return controller;
    }

    private static List<SwitchDto> List(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsAssignableFrom<IEnumerable<SwitchDto>>(ok.Value).ToList();
    }

    /// <summary>Seeds three sockets (1,2,3) and one non-socket (4). Socket 2 is reachable indirectly
    /// from sensor 100 via gateway "gw" discovering "socket-2".</summary>
    private static async Task SeedAsync(ApplicationDbContext db)
    {
        db.Switches.AddRange(
            new Switch { Id = 1, Name = "socket-1", Type = "socket", Role = "switch" },
            new Switch { Id = 2, Name = "socket-2", Type = "socket", Role = "switch" },
            new Switch { Id = 3, Name = "socket-3", Type = "socket", Role = "switch" },
            new Switch { Id = 4, Name = "relay-4", Type = "relay", Role = "switch" });

        db.Sensors.Add(new Sensor
        {
            Id = 100, Name = "garge_volt", Type = "voltage", Role = "sensor",
            RegistrationCode = "rc-s", DefaultName = "Battery", ParentName = "gw"
        });
        db.DiscoveredDevices.Add(new DiscoveredDevice { DiscoveredBy = "gw", Target = "socket-2", Type = "socket", Timestamp = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetAllSwitches_DirectOwner_SeesOnlyOwnedSocket_WithOwnerAccess()
    {
        using var db = CreateDbContext();
        await SeedAsync(db);
        db.UserSwitches.Add(new UserSwitch { UserId = "owner", SwitchId = 1, IsOwner = true });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var dtos = List(await CreateController(db, "owner").GetAllSwitches(TestContext.Current.CancellationToken));

        var dto = Assert.Single(dtos);
        Assert.Equal(1, dto.Id);
        Assert.Equal(DeviceAccess.Owner, dto.Access);
    }

    [Fact]
    public async Task GetAllSwitches_ReadViewer_SeesSharedSocket_WithReadAccess()
    {
        using var db = CreateDbContext();
        await SeedAsync(db);
        db.UserSwitches.Add(new UserSwitch { UserId = "viewer", SwitchId = 3, IsOwner = false, Permission = SharePermission.Read });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var dtos = List(await CreateController(db, "viewer").GetAllSwitches(TestContext.Current.CancellationToken));

        var dto = Assert.Single(dtos);
        Assert.Equal(3, dto.Id);
        Assert.Equal(DeviceAccess.Read, dto.Access);
    }

    [Fact]
    public async Task GetAllSwitches_EditViewer_SeesSharedSocket_WithEditAccess()
    {
        using var db = CreateDbContext();
        await SeedAsync(db);
        db.UserSwitches.Add(new UserSwitch { UserId = "viewer", SwitchId = 3, IsOwner = false, Permission = SharePermission.Edit });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var dtos = List(await CreateController(db, "viewer").GetAllSwitches(TestContext.Current.CancellationToken));

        var dto = Assert.Single(dtos);
        Assert.Equal(3, dto.Id);
        Assert.Equal(DeviceAccess.Edit, dto.Access);
    }

    [Fact]
    public async Task GetAllSwitches_IndirectOwnerViaSensorChain_SeesSocket_WithOwnerAccess()
    {
        using var db = CreateDbContext();
        await SeedAsync(db);
        // user owns the sensor whose gateway discovered socket-2; no direct UserSwitch row.
        db.UserSensors.Add(new UserSensor { UserId = "indirect", SensorId = 100, IsOwner = true });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var dtos = List(await CreateController(db, "indirect").GetAllSwitches(TestContext.Current.CancellationToken));

        var dto = Assert.Single(dtos);
        Assert.Equal(2, dto.Id);
        Assert.Equal(DeviceAccess.Owner, dto.Access);
    }

    [Fact]
    public async Task GetAllSwitches_SensorSharedNotOwned_DoesNotGrantIndirectSwitchAccess()
    {
        using var db = CreateDbContext();
        await SeedAsync(db);
        // A non-owner sensor share must NOT confer switch access (matches LoadSwitchOwnersAsync: IsOwner only).
        db.UserSensors.Add(new UserSensor { UserId = "sensor-viewer", SensorId = 100, IsOwner = false, Permission = SharePermission.Edit });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var dtos = List(await CreateController(db, "sensor-viewer").GetAllSwitches(TestContext.Current.CancellationToken));

        Assert.Empty(dtos);
    }

    [Fact]
    public async Task GetAllSwitches_Admin_SeesAllSockets_NotRelay()
    {
        using var db = CreateDbContext();
        await SeedAsync(db);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var dtos = List(await CreateController(db, "admin-1", isAdmin: true).GetAllSwitches(TestContext.Current.CancellationToken));

        Assert.Equal(new[] { 1, 2, 3 }, dtos.Select(d => d.Id).OrderBy(x => x));
        Assert.All(dtos, d => Assert.Equal(DeviceAccess.Owner, d.Access));
    }

    [Fact]
    public async Task GetAllSwitches_Stranger_SeesNothing()
    {
        using var db = CreateDbContext();
        await SeedAsync(db);
        db.UserSwitches.Add(new UserSwitch { UserId = "owner", SwitchId = 1, IsOwner = true });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var dtos = List(await CreateController(db, "stranger").GetAllSwitches(TestContext.Current.CancellationToken));

        Assert.Empty(dtos);
    }

    [Fact]
    public async Task GetAllSwitches_MixedAccess_ReturnsExactlyTheAccessibleSet()
    {
        using var db = CreateDbContext();
        await SeedAsync(db);
        db.UserSwitches.Add(new UserSwitch { UserId = "mix", SwitchId = 1, IsOwner = true });               // direct owner
        db.UserSwitches.Add(new UserSwitch { UserId = "mix", SwitchId = 3, IsOwner = false, Permission = SharePermission.Read }); // shared
        db.UserSensors.Add(new UserSensor { UserId = "mix", SensorId = 100, IsOwner = true });               // indirect -> socket 2
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var dtos = List(await CreateController(db, "mix").GetAllSwitches(TestContext.Current.CancellationToken));

        Assert.Equal(new[] { 1, 2, 3 }, dtos.Select(d => d.Id).OrderBy(x => x));
        Assert.Equal(DeviceAccess.Owner, dtos.Single(d => d.Id == 1).Access);
        Assert.Equal(DeviceAccess.Owner, dtos.Single(d => d.Id == 2).Access);
        Assert.Equal(DeviceAccess.Read, dtos.Single(d => d.Id == 3).Access);
    }
}
