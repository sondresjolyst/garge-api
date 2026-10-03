using garge_api.Models;
using garge_api.Models.Mqtt;
using garge_api.Models.Sensor;
using garge_api.Models.Switch;
using garge_api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// Verifies the broker ACL rows a gateway needs for the devices it discovers. A gateway publishes
/// a discovered device's config and state, and subscribes to its command topic, and those topics
/// sit at the broker root rather than under the gateway's own prefix, so each target needs rows of
/// its own. Granting on the gateway's own discovery claim alone would let a device name another
/// user's switch as its target, so ownership is checked first.
/// </summary>
public class MqttAclServiceTests : ControllerTestBase
{
    private const string Gateway = "garge_48ca43597fd8";
    private const string Target = "wiz_SOCKET_6c2990a96cde";

    private static MqttAclService CreateService(ApplicationDbContext db) =>
        new(db, NullLogger<MqttAclService>.Instance);

    private static Sensor MakeGatewaySensor(int id = 1, string parentName = Gateway) => new()
    {
        Id = id, Name = $"{parentName}_temperature", Type = "temperature", Role = "sensor",
        RegistrationCode = $"rc{id}", DefaultName = "Temperature", ParentName = parentName
    };

    private static Switch MakeTargetSwitch(int id = 1, string name = Target) => new()
    {
        Id = id, Name = name, Type = "switch", Role = "switch", RegistrationCode = $"sw{id}"
    };

    [Fact]
    public async Task EnsureDiscoveredDeviceAcl_UnclaimedTarget_GrantsBothRetainRows()
    {
        using var db = CreateDbContext();

        var granted = await CreateService(db).EnsureDiscoveredDeviceAclAsync(Gateway, Target);
        await db.SaveChangesAsync();

        Assert.True(granted);
        var acls = db.EMQXMqttAcls.OrderBy(a => a.Retain).ToList();
        Assert.Equal(2, acls.Count);
        Assert.Equal(new short?[] { 0, 1 }, acls.Select(a => a.Retain));
        Assert.All(acls, a =>
        {
            Assert.Equal(Gateway, a.Username);
            Assert.Equal($"garge/devices/{Target}/#", a.Topic);
            Assert.Equal("all", a.Action);
            Assert.Equal("allow", a.Permission);
            Assert.Equal((short)0, a.Qos);
        });
    }

    [Fact]
    public async Task EnsureDiscoveredDeviceAcl_CalledTwice_DoesNotDuplicateRows()
    {
        using var db = CreateDbContext();
        var service = CreateService(db);

        await service.EnsureDiscoveredDeviceAclAsync(Gateway, Target);
        await db.SaveChangesAsync();
        await service.EnsureDiscoveredDeviceAclAsync(Gateway, Target);
        await db.SaveChangesAsync();

        Assert.Equal(2, db.EMQXMqttAcls.Count());
    }

    [Fact]
    public async Task EnsureTopicAcl_TwiceBeforeSaving_DoesNotStageDuplicates()
    {
        using var db = CreateDbContext();
        var service = CreateService(db);

        // Both calls happen before SaveChangesAsync, so the staged rows are only visible in the
        // change tracker; a check against the database alone would miss them.
        await service.EnsureTopicAclAsync(Gateway, MqttAclService.DeviceTopicFilter(Gateway));
        await service.EnsureTopicAclAsync(Gateway, MqttAclService.DeviceTopicFilter(Gateway));
        await db.SaveChangesAsync();

        Assert.Equal(2, db.EMQXMqttAcls.Count());
    }

    [Fact]
    public async Task EnsureDiscoveredDeviceAcl_TargetOwnedByAnotherUser_GrantsNothing()
    {
        using var db = CreateDbContext();
        db.Sensors.Add(MakeGatewaySensor());
        db.UserSensors.Add(new UserSensor { UserId = "gateway-owner", SensorId = 1, IsOwner = true });
        db.Switches.Add(MakeTargetSwitch());
        db.UserSwitches.Add(new UserSwitch { UserId = "someone-else", SwitchId = 1, IsOwner = true });
        await db.SaveChangesAsync();

        var granted = await CreateService(db).EnsureDiscoveredDeviceAclAsync(Gateway, Target);
        await db.SaveChangesAsync();

        Assert.False(granted);
        Assert.Empty(db.EMQXMqttAcls);
    }

    [Fact]
    public async Task EnsureDiscoveredDeviceAcl_TargetOwnedBySameUser_Grants()
    {
        using var db = CreateDbContext();
        db.Sensors.Add(MakeGatewaySensor());
        db.UserSensors.Add(new UserSensor { UserId = "shared-owner", SensorId = 1, IsOwner = true });
        db.Switches.Add(MakeTargetSwitch());
        db.UserSwitches.Add(new UserSwitch { UserId = "shared-owner", SwitchId = 1, IsOwner = true });
        await db.SaveChangesAsync();

        var granted = await CreateService(db).EnsureDiscoveredDeviceAclAsync(Gateway, Target);
        await db.SaveChangesAsync();

        Assert.True(granted);
        Assert.Equal(2, db.EMQXMqttAcls.Count());
    }

    [Fact]
    public async Task EnsureDiscoveredDeviceAcl_OwnedTargetAndUnclaimedGateway_GrantsNothing()
    {
        using var db = CreateDbContext();
        db.Switches.Add(MakeTargetSwitch());
        db.UserSwitches.Add(new UserSwitch { UserId = "someone-else", SwitchId = 1, IsOwner = true });
        await db.SaveChangesAsync();

        // Nothing ties the gateway to an owner yet, so it cannot be shown to share one with the
        // target. An owned target stays off limits until the gateway itself is claimed.
        var granted = await CreateService(db).EnsureDiscoveredDeviceAclAsync(Gateway, Target);
        await db.SaveChangesAsync();

        Assert.False(granted);
        Assert.Empty(db.EMQXMqttAcls);
    }

    [Fact]
    public async Task PruneAclsForOtherGateways_LeavesOnlyTheHoldersRows()
    {
        using var db = CreateDbContext();
        var other = "garge_ffffffffffff";
        var service = CreateService(db);
        // What the state looks like before the lease existed: every gateway that discovered the
        // device was granted its topics.
        await service.EnsureDiscoveredDeviceAclAsync(Gateway, Target);
        await service.EnsureDiscoveredDeviceAclAsync(other, Target);
        await db.SaveChangesAsync();
        Assert.Equal(4, db.EMQXMqttAcls.Count());

        var pruned = await service.PruneAclsForOtherGatewaysAsync(Target, Gateway);
        await db.SaveChangesAsync();

        Assert.Equal(2, pruned);
        Assert.Equal(2, db.EMQXMqttAcls.Count());
        Assert.All(db.EMQXMqttAcls, a => Assert.Equal(Gateway, a.Username));
    }

    [Fact]
    public async Task PruneAclsForOtherGateways_LeavesOtherTargetsAlone()
    {
        using var db = CreateDbContext();
        var otherTarget = "wiz_SHRGBC_d8a01127d90e";
        var service = CreateService(db);
        await service.EnsureDiscoveredDeviceAclAsync(Gateway, Target);
        await service.EnsureDiscoveredDeviceAclAsync("garge_ffffffffffff", otherTarget);
        await db.SaveChangesAsync();

        var pruned = await service.PruneAclsForOtherGatewaysAsync(Target, Gateway);
        await db.SaveChangesAsync();

        // A different device's lease is none of this target's business.
        Assert.Equal(0, pruned);
        Assert.Equal(4, db.EMQXMqttAcls.Count());
    }

    [Fact]
    public async Task PruneAclsForOtherGateways_DoesNotTouchTheGatewaysOwnPrefix()
    {
        using var db = CreateDbContext();
        var service = CreateService(db);
        await service.EnsureTopicAclAsync("garge_ffffffffffff", MqttAclService.DeviceTopicFilter("garge_ffffffffffff"));
        await service.EnsureDiscoveredDeviceAclAsync(Gateway, Target);
        await db.SaveChangesAsync();

        await service.PruneAclsForOtherGatewaysAsync(Target, Gateway);
        await db.SaveChangesAsync();

        // A gateway always keeps its own prefix; only the shared target's rows are at stake.
        Assert.Contains(db.EMQXMqttAcls, a => a.Username == "garge_ffffffffffff");
        Assert.Equal(4, db.EMQXMqttAcls.Count());
    }

    [Theory]
    [InlineData("", Target)]
    [InlineData(Gateway, "")]
    [InlineData("   ", "   ")]
    public async Task EnsureDiscoveredDeviceAcl_BlankNames_GrantNothing(string gateway, string target)
    {
        using var db = CreateDbContext();

        var granted = await CreateService(db).EnsureDiscoveredDeviceAclAsync(gateway, target);
        await db.SaveChangesAsync();

        Assert.False(granted);
        Assert.Empty(db.EMQXMqttAcls);
    }
}
