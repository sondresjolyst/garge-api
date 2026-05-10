using garge_api.Models;
using garge_api.Models.Mqtt;
using garge_api.Models.Sensor;
using garge_api.Models.Switch;
using garge_api.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace garge_api.Tests;

public class DeviceOwnershipServiceTests : ControllerTestBase
{
    private static IServiceScopeFactory BuildScopeFactory(ApplicationDbContext db)
    {
        var sp = new Mock<IServiceProvider>();
        sp.Setup(x => x.GetService(typeof(ApplicationDbContext))).Returns(db);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(sp.Object);

        var factory = new Mock<IServiceScopeFactory>();
        factory.Setup(f => f.CreateScope()).Returns(scope.Object);
        return factory.Object;
    }

    [Fact]
    public async Task ListSwitchOwners_Returns_Direct_UserSwitches_Bindings()
    {
        var db = CreateDbContext();
        db.Switches.Add(new garge_api.Models.Switch.Switch
        {
            Id = 1,
            Name = "wiz_SOCKET_aabb",
            Type = "SOCKET",
            Role = "Default",
            CreatedAt = DateTime.UtcNow
        });
        db.UserSwitches.Add(new UserSwitch { UserId = "u1", SwitchId = 1 });
        db.UserSwitches.Add(new UserSwitch { UserId = "u2", SwitchId = 1 });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var svc = new DeviceOwnershipService(BuildScopeFactory(db));
        var owners = await svc.ListSwitchOwnersAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(2, owners.Count);
        Assert.Contains("u1", owners);
        Assert.Contains("u2", owners);
    }

    [Fact]
    public async Task ListSwitchOwners_Includes_Indirect_Owners_Via_Sensor_Parent()
    {
        var db = CreateDbContext();
        db.Switches.Add(new garge_api.Models.Switch.Switch
        {
            Id = 1,
            Name = "wiz_SOCKET_aabb",
            Type = "SOCKET",
            Role = "Default",
            CreatedAt = DateTime.UtcNow
        });
        db.Sensors.Add(new Sensor
        {
            Id = 10,
            Name = "voltage-1",
            Type = "voltage",
            Role = "Default",
            RegistrationCode = "test-reg",
            DefaultName = "Battery",
            ParentName = "garge_xyz",
            CreatedAt = DateTime.UtcNow
        });
        db.UserSensors.Add(new UserSensor { UserId = "uX", SensorId = 10 });
        db.DiscoveredDevices.Add(new DiscoveredDevice
        {
            Id = 100,
            DiscoveredBy = "garge_xyz",
            Target = "wiz_SOCKET_aabb",
            Type = "SOCKET",
            Timestamp = DateTime.UtcNow
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var svc = new DeviceOwnershipService(BuildScopeFactory(db));
        var owners = await svc.ListSwitchOwnersAsync(1, TestContext.Current.CancellationToken);

        Assert.Single(owners);
        Assert.Contains("uX", owners);
    }

    [Fact]
    public async Task ListSwitchOwners_For_Unknown_Switch_Returns_Empty()
    {
        var db = CreateDbContext();
        var svc = new DeviceOwnershipService(BuildScopeFactory(db));

        var owners = await svc.ListSwitchOwnersAsync(999, TestContext.Current.CancellationToken);

        Assert.Empty(owners);
    }

    [Fact]
    public async Task ListSensorOwners_Returns_All_UserSensors_Bindings()
    {
        var db = CreateDbContext();
        db.UserSensors.Add(new UserSensor { UserId = "a", SensorId = 7 });
        db.UserSensors.Add(new UserSensor { UserId = "b", SensorId = 7 });
        db.UserSensors.Add(new UserSensor { UserId = "c", SensorId = 8 });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var svc = new DeviceOwnershipService(BuildScopeFactory(db));

        var ownersOf7 = await svc.ListSensorOwnersAsync(7, TestContext.Current.CancellationToken);
        Assert.Equal(2, ownersOf7.Count);
        Assert.Contains("a", ownersOf7);
        Assert.Contains("b", ownersOf7);

        var ownersOf99 = await svc.ListSensorOwnersAsync(99, TestContext.Current.CancellationToken);
        Assert.Empty(ownersOf99);
    }
}
