using garge_api.Controllers;
using garge_api.Dtos.Mqtt;
using garge_api.Models;
using garge_api.Models.Mqtt;
using garge_api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// Verifies what registering a discovered device does beyond storing the row: it reports the
/// gateway as seeing the target, which takes or renews that device's lease, and grants the broker
/// ACL rows only to the gateway holding it. Several gateways can see the same device, so a standby
/// must be recorded as a candidate without being given access.
/// </summary>
public class MqttControllerDiscoveryTests : ControllerTestBase
{
    private const string GatewayA = "garge_aaaaaaaaaaaa";
    private const string GatewayB = "garge_bbbbbbbbbbbb";
    private const string Target = "wiz_SOCKET_6c2990a96cde";

    private static MqttController CreateController(ApplicationDbContext db)
    {
        var leases = new DeviceLeaseService(db, NullLogger<DeviceLeaseService>.Instance);
        var acls = new MqttAclService(db, NullLogger<MqttAclService>.Instance);
        var commands = new DeviceCommandService(db, NullLogger<DeviceCommandService>.Instance);

        return new MqttController(db, NullLogger<MqttController>.Instance, acls, leases, commands)
        {
            ControllerContext = MakeControllerContext("operator")
        };
    }

    private static CreateDiscoveredDeviceDto Discovery(string by, string target = Target) => new()
    {
        DiscoveredBy = by,
        Target = target,
        Type = "switch",
        Timestamp = DateTime.UtcNow
    };

    [Fact]
    public async Task FirstGatewayToReport_TakesTheLeaseAndGetsTheAcls()
    {
        using var db = CreateDbContext();

        var result = await CreateController(db).PostDiscoveredDevice(Discovery(GatewayA));

        Assert.IsType<OkObjectResult>(result);
        var lease = db.DeviceControllers.Single();
        Assert.Equal(GatewayA, lease.ControllerDeviceName);
        Assert.Equal(2, db.EMQXMqttAcls.Count());
        Assert.All(db.EMQXMqttAcls, a =>
        {
            Assert.Equal(GatewayA, a.Username);
            Assert.Equal($"garge/devices/{Target}/#", a.Topic);
        });
    }

    [Fact]
    public async Task SecondGatewayReportingTheSameTarget_IsRecordedButGrantedNothing()
    {
        using var db = CreateDbContext();
        await CreateController(db).PostDiscoveredDevice(Discovery(GatewayA));

        var result = await CreateController(db).PostDiscoveredDevice(Discovery(GatewayB));

        Assert.IsType<OkObjectResult>(result);
        // Both are candidates for a later handover.
        Assert.Equal(2, db.DiscoveredDevices.Count());
        // The lease and the access stay with the first.
        Assert.Equal(GatewayA, db.DeviceControllers.Single().ControllerDeviceName);
        Assert.All(db.EMQXMqttAcls, a => Assert.Equal(GatewayA, a.Username));
        Assert.DoesNotContain(db.EMQXMqttAcls, a => a.Username == GatewayB);
    }

    [Fact]
    public async Task HolderReportingAgain_RenewsTheLeaseWithoutDuplicatingAcls()
    {
        using var db = CreateDbContext();
        await CreateController(db).PostDiscoveredDevice(Discovery(GatewayA));
        var firstExpiry = db.DeviceControllers.Single().LeaseExpiresAt;

        // The in-memory provider does not enforce the unique index, so this exercises the
        // renewal path rather than the duplicate-row conflict the real database returns.
        await CreateController(db).PostDiscoveredDevice(Discovery(GatewayA));

        Assert.True(db.DeviceControllers.Single().LeaseExpiresAt >= firstExpiry);
        Assert.Equal(2, db.EMQXMqttAcls.Count());
    }

    [Fact]
    public async Task ReportingATargetOwnedByAnotherUser_GrantsNothing()
    {
        using var db = CreateDbContext();
        db.Sensors.Add(new Models.Sensor.Sensor
        {
            Id = 1, Name = $"{GatewayA}_temperature", Type = "temperature", Role = "sensor",
            RegistrationCode = "rc1", DefaultName = "Temperature", ParentName = GatewayA
        });
        db.UserSensors.Add(new Models.Sensor.UserSensor { UserId = "gateway-owner", SensorId = 1, IsOwner = true });
        db.Switches.Add(new Models.Switch.Switch { Id = 1, Name = Target, Type = "switch", Role = "switch", RegistrationCode = "sw1" });
        db.UserSwitches.Add(new Models.Switch.UserSwitch { UserId = "someone-else", SwitchId = 1, IsOwner = true });
        await db.SaveChangesAsync();

        var result = await CreateController(db).PostDiscoveredDevice(Discovery(GatewayA));

        Assert.IsType<OkObjectResult>(result);
        // The lease is still recorded, since the gateway can see the device, but naming someone
        // else's switch as a target must not hand over access to it.
        Assert.Equal(GatewayA, db.DeviceControllers.Single().ControllerDeviceName);
        Assert.Empty(db.EMQXMqttAcls);
    }

    [Fact]
    public async Task ControlsEndpoint_ListsOnlyLiveLeases()
    {
        using var db = CreateDbContext();
        await CreateController(db).PostDiscoveredDevice(Discovery(GatewayA));
        db.DeviceControllers.Add(new DeviceController
        {
            Target = "wiz_SOCKET_ffffffffffff",
            ControllerDeviceName = GatewayB,
            LeaseExpiresAt = DateTime.UtcNow.AddMinutes(-1),
            LastSeenFromTarget = DateTime.UtcNow.AddMinutes(-10)
        });
        await db.SaveChangesAsync();

        var result = await CreateController(db).GetDeviceControls();

        var ok = Assert.IsType<OkObjectResult>(result);
        var lists = Assert.IsAssignableFrom<List<DeviceControlListDto>>(ok.Value);
        // The lapsed lease is left out, so its holder is told it controls nothing.
        var list = Assert.Single(lists);
        Assert.Equal(GatewayA, list.GatewayDeviceName);
        Assert.Equal([Target], list.Targets);
    }
}
