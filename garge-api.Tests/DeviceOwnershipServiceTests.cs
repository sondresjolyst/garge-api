using garge_api.Models;
using garge_api.Models.Mqtt;
using garge_api.Models.Sensor;
using garge_api.Models.Switch;
using garge_api.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace garge_api.Tests;

public class DeviceOwnershipServiceTests : ControllerTestBase
{
    private static IMemoryCache BuildCache() =>
        new MemoryCache(Options.Create(new MemoryCacheOptions()));

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

        var svc = new DeviceOwnershipService(BuildScopeFactory(db), BuildCache());
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

        var svc = new DeviceOwnershipService(BuildScopeFactory(db), BuildCache());
        var owners = await svc.ListSwitchOwnersAsync(1, TestContext.Current.CancellationToken);

        Assert.Single(owners);
        Assert.Contains("uX", owners);
    }

    [Fact]
    public async Task ListSwitchOwners_For_Unknown_Switch_Returns_Empty()
    {
        var db = CreateDbContext();
        var svc = new DeviceOwnershipService(BuildScopeFactory(db), BuildCache());

        var owners = await svc.ListSwitchOwnersAsync(999, TestContext.Current.CancellationToken);

        Assert.Empty(owners);
    }

    [Fact]
    public async Task ListSwitchOwners_Isolates_Across_Trees_With_Same_ParentName()
    {
        var db = CreateDbContext();

        // Switch is discovered by gateway-A only.
        db.Switches.Add(new garge_api.Models.Switch.Switch
        {
            Id = 1,
            Name = "wiz_SOCKET_aabb",
            Type = "SOCKET",
            Role = "Default",
            CreatedAt = DateTime.UtcNow
        });

        // User A owns a sensor under gateway-A → should access switch.
        db.Sensors.Add(new Sensor
        {
            Id = 10,
            Name = "voltage-A",
            Type = "voltage",
            Role = "Default",
            RegistrationCode = "reg-A",
            DefaultName = "Battery",
            ParentName = "garge_A",
            CreatedAt = DateTime.UtcNow
        });
        // User B owns a sensor under gateway-B (different parent) → must NOT access switch.
        db.Sensors.Add(new Sensor
        {
            Id = 11,
            Name = "voltage-B",
            Type = "voltage",
            Role = "Default",
            RegistrationCode = "reg-B",
            DefaultName = "Battery",
            ParentName = "garge_B",
            CreatedAt = DateTime.UtcNow
        });
        db.UserSensors.Add(new UserSensor { UserId = "userA", SensorId = 10 });
        db.UserSensors.Add(new UserSensor { UserId = "userB", SensorId = 11 });

        // Switch was discovered only by gateway-A.
        db.DiscoveredDevices.Add(new DiscoveredDevice
        {
            Id = 100,
            DiscoveredBy = "garge_A",
            Target = "wiz_SOCKET_aabb",
            Type = "SOCKET",
            Timestamp = DateTime.UtcNow
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var svc = new DeviceOwnershipService(BuildScopeFactory(db), BuildCache());
        var owners = await svc.ListSwitchOwnersAsync(1, TestContext.Current.CancellationToken);

        Assert.Single(owners);
        Assert.Contains("userA", owners);
        Assert.DoesNotContain("userB", owners);
    }

    [Fact]
    public async Task ListSwitchOwners_ParentName_Collision_Includes_Both_Trees()
    {
        // If two unrelated user trees genuinely share a ParentName (collision
        // scenario — e.g., two gateways issued the same name), both users land
        // in the owner list. This test pins current behavior; if the model
        // gains a stricter source-of-truth (e.g., explicit UserSwitch only),
        // this test should be flipped to assert isolation.
        var db = CreateDbContext();
        db.Switches.Add(new garge_api.Models.Switch.Switch
        {
            Id = 2,
            Name = "wiz_SOCKET_ccdd",
            Type = "SOCKET",
            Role = "Default",
            CreatedAt = DateTime.UtcNow
        });
        db.Sensors.Add(new Sensor
        {
            Id = 20,
            Name = "vA",
            Type = "voltage",
            Role = "Default",
            RegistrationCode = "rA",
            DefaultName = "B",
            ParentName = "garge_shared",
            CreatedAt = DateTime.UtcNow
        });
        db.Sensors.Add(new Sensor
        {
            Id = 21,
            Name = "vB",
            Type = "voltage",
            Role = "Default",
            RegistrationCode = "rB",
            DefaultName = "B",
            ParentName = "garge_shared",
            CreatedAt = DateTime.UtcNow
        });
        db.UserSensors.Add(new UserSensor { UserId = "u1", SensorId = 20 });
        db.UserSensors.Add(new UserSensor { UserId = "u2", SensorId = 21 });
        db.DiscoveredDevices.Add(new DiscoveredDevice
        {
            Id = 200,
            DiscoveredBy = "garge_shared",
            Target = "wiz_SOCKET_ccdd",
            Type = "SOCKET",
            Timestamp = DateTime.UtcNow
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var svc = new DeviceOwnershipService(BuildScopeFactory(db), BuildCache());
        var owners = await svc.ListSwitchOwnersAsync(2, TestContext.Current.CancellationToken);

        Assert.Equal(2, owners.Count);
        Assert.Contains("u1", owners);
        Assert.Contains("u2", owners);
    }

    [Fact]
    public async Task ListSensorOwners_Returns_All_UserSensors_Bindings()
    {
        var db = CreateDbContext();
        db.UserSensors.Add(new UserSensor { UserId = "a", SensorId = 7 });
        db.UserSensors.Add(new UserSensor { UserId = "b", SensorId = 7 });
        db.UserSensors.Add(new UserSensor { UserId = "c", SensorId = 8 });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var svc = new DeviceOwnershipService(BuildScopeFactory(db), BuildCache());

        var ownersOf7 = await svc.ListSensorOwnersAsync(7, TestContext.Current.CancellationToken);
        Assert.Equal(2, ownersOf7.Count);
        Assert.Contains("a", ownersOf7);
        Assert.Contains("b", ownersOf7);

        var ownersOf99 = await svc.ListSensorOwnersAsync(99, TestContext.Current.CancellationToken);
        Assert.Empty(ownersOf99);
    }
}
