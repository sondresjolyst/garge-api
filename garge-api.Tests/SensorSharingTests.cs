using garge_api.Controllers;
using garge_api.Dtos.Sensor;
using garge_api.Hubs;
using garge_api.Models;
using garge_api.Models.Mqtt;
using garge_api.Models.Sensor;
using garge_api.Models.Switch;
using garge_api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// Verifies sensor sharing: an owner can share Read/Edit with another user by email; the recipient gets
/// a viewer row + an ownership period from share time; revoke and re-share work; owner-unclaim cascades
/// to recipients while a recipient's unclaim only removes themselves.
/// </summary>
public class SensorSharingTests : ControllerTestBase
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

    private static Sensor MakeSensor(int id = 1) => new()
    {
        Id = id, Name = $"garge_volt_{id}", Type = "voltage", Role = "sensor",
        RegistrationCode = $"rc{id}", DefaultName = "Battery", ParentName = "gw"
    };

    private static User MakeUser(string id, string email) => new()
    {
        Id = id, UserName = id, Email = email, NormalizedEmail = email.ToUpperInvariant(),
        FirstName = "F", LastName = "L"
    };

    private static async Task SeedOwnedSensorAsync(ApplicationDbContext db, string ownerId, int sensorId = 1)
    {
        db.Sensors.Add(MakeSensor(sensorId));
        db.UserSensors.Add(new UserSensor { UserId = ownerId, SensorId = sensorId, IsOwner = true });
        await db.SaveChangesAsync();
    }

    private static ShareSensorDto Share(string email, SharePermission p = SharePermission.Read) =>
        new() { Email = email, Permission = p };

    [Fact]
    public async Task ShareSensor_CreatesViewerRowAndOwnershipPeriodFromShareTime()
    {
        using var db = CreateDbContext();
        db.Users.AddRange(MakeUser("owner", "owner@x"), MakeUser("viewer", "viewer@x"));
        await SeedOwnedSensorAsync(db, "owner");

        var result = await CreateController(db, "owner").ShareSensor(1, Share("viewer@x", SharePermission.Edit));

        Assert.IsType<OkObjectResult>(result);
        var row = db.UserSensors.Single(us => us.UserId == "viewer" && us.SensorId == 1);
        Assert.False(row.IsOwner);
        Assert.Equal(SharePermission.Edit, row.Permission);
        var period = db.SensorOwnershipPeriods.Single(p => p.UserId == "viewer" && p.SensorId == 1);
        Assert.Null(period.EndedAt);
        Assert.NotEqual(SensorOwnershipPeriod.FirstOwnerStart, period.StartedAt); // from now, not the full-history epoch
    }

    [Fact]
    public async Task ShareSensor_NotOwner_Forbid()
    {
        using var db = CreateDbContext();
        db.Users.AddRange(MakeUser("owner", "owner@x"), MakeUser("stranger", "stranger@x"), MakeUser("viewer", "viewer@x"));
        await SeedOwnedSensorAsync(db, "owner");

        var result = await CreateController(db, "stranger").ShareSensor(1, Share("viewer@x"));

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task ShareSensor_UnknownEmail_NotFound()
    {
        using var db = CreateDbContext();
        db.Users.Add(MakeUser("owner", "owner@x"));
        await SeedOwnedSensorAsync(db, "owner");

        var result = await CreateController(db, "owner").ShareSensor(1, Share("ghost@x"));

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task ShareSensor_Reshare_UpdatesPermission_NoDuplicateRowOrPeriod()
    {
        using var db = CreateDbContext();
        db.Users.AddRange(MakeUser("owner", "owner@x"), MakeUser("viewer", "viewer@x"));
        await SeedOwnedSensorAsync(db, "owner");

        await CreateController(db, "owner").ShareSensor(1, Share("viewer@x", SharePermission.Read));
        await CreateController(db, "owner").ShareSensor(1, Share("viewer@x", SharePermission.Edit));

        var rows = db.UserSensors.Where(us => us.UserId == "viewer" && us.SensorId == 1).ToList();
        Assert.Single(rows);
        Assert.Equal(SharePermission.Edit, rows[0].Permission);
        Assert.Single(db.SensorOwnershipPeriods.Where(p => p.UserId == "viewer" && p.SensorId == 1));
    }

    [Fact]
    public async Task RevokeShare_RemovesViewerAndClosesPeriod()
    {
        using var db = CreateDbContext();
        db.Users.AddRange(MakeUser("owner", "owner@x"), MakeUser("viewer", "viewer@x"));
        await SeedOwnedSensorAsync(db, "owner");
        await CreateController(db, "owner").ShareSensor(1, Share("viewer@x"));

        var result = await CreateController(db, "owner").RevokeShare(1, "viewer");

        Assert.IsType<OkObjectResult>(result);
        Assert.DoesNotContain(db.UserSensors, us => us.UserId == "viewer" && us.SensorId == 1);
        Assert.NotNull(db.SensorOwnershipPeriods.Single(p => p.UserId == "viewer" && p.SensorId == 1).EndedAt);
    }

    [Fact]
    public async Task RevokeShare_NotOwner_Forbid()
    {
        using var db = CreateDbContext();
        db.Users.AddRange(MakeUser("owner", "owner@x"), MakeUser("viewer", "viewer@x"), MakeUser("stranger", "s@x"));
        await SeedOwnedSensorAsync(db, "owner");
        await CreateController(db, "owner").ShareSensor(1, Share("viewer@x"));

        var result = await CreateController(db, "stranger").RevokeShare(1, "viewer");

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task ListShares_ReturnsRecipientsWithPermission()
    {
        using var db = CreateDbContext();
        db.Users.AddRange(MakeUser("owner", "owner@x"), MakeUser("viewer", "viewer@x"));
        await SeedOwnedSensorAsync(db, "owner");
        await CreateController(db, "owner").ShareSensor(1, Share("viewer@x", SharePermission.Edit));

        var result = await CreateController(db, "owner").ListShares(1);

        var ok = Assert.IsType<OkObjectResult>(result);
        var shares = Assert.IsAssignableFrom<IEnumerable<SensorShareDto>>(ok.Value);
        var s = Assert.Single(shares);
        Assert.Equal("viewer", s.UserId);
        Assert.Equal(SharePermission.Edit, s.Permission);
    }

    [Fact]
    public async Task UnclaimSensor_Owner_CascadesToViewers()
    {
        using var db = CreateDbContext();
        db.Users.AddRange(MakeUser("owner", "owner@x"), MakeUser("viewer", "viewer@x"));
        await SeedOwnedSensorAsync(db, "owner");
        await CreateController(db, "owner").ShareSensor(1, Share("viewer@x"));

        var result = await CreateController(db, "owner").UnclaimSensor(1);

        Assert.IsType<OkObjectResult>(result);
        Assert.DoesNotContain(db.UserSensors, us => us.SensorId == 1);          // owner + viewer both gone
        Assert.All(db.SensorOwnershipPeriods.Where(p => p.SensorId == 1), p => Assert.NotNull(p.EndedAt));
    }

    [Fact]
    public async Task UnclaimSensor_OrphansDiscoveredSocket_RevokesItsShares()
    {
        // Owner indirectly owns socket-a via sensor 1's gateway, and shared it. Unclaiming the sensor
        // leaves socket-a with no owner, so its share must be revoked (the discovery edge).
        using var db = CreateDbContext();
        db.Users.AddRange(MakeUser("owner", "owner@x"), MakeUser("viewer", "viewer@x"));
        db.Sensors.Add(MakeSensor(1)); // ParentName "gw"
        db.UserSensors.Add(new UserSensor { UserId = "owner", SensorId = 1, IsOwner = true });
        db.Switches.Add(new Switch { Id = 10, Name = "socket-a", Type = "socket", Role = "switch" });
        db.DiscoveredDevices.Add(new DiscoveredDevice { DiscoveredBy = "gw", Target = "socket-a", Type = "socket", Timestamp = DateTime.UtcNow });
        db.UserSwitches.Add(new UserSwitch { UserId = "viewer", SwitchId = 10, IsOwner = false, Permission = SharePermission.Read });
        db.SwitchOwnershipPeriods.Add(new SwitchOwnershipPeriod { UserId = "viewer", SwitchId = 10, StartedAt = DateTime.UtcNow, EndedAt = null });
        await db.SaveChangesAsync();

        var result = await CreateController(db, "owner").UnclaimSensor(1);

        Assert.IsType<OkObjectResult>(result);
        Assert.DoesNotContain(db.UserSwitches, us => us.SwitchId == 10);
        Assert.NotNull(db.SwitchOwnershipPeriods.Single(p => p.SwitchId == 10).EndedAt);
    }

    [Fact]
    public async Task UnclaimSensor_SocketStillOwned_KeepsItsShares()
    {
        // A second owner still owns the socket indirectly, so unclaiming one sensor keeps the share.
        using var db = CreateDbContext();
        db.Users.AddRange(MakeUser("owner", "owner@x"), MakeUser("other", "other@x"), MakeUser("viewer", "viewer@x"));
        db.Sensors.Add(MakeSensor(1)); // gw
        db.Sensors.Add(new Sensor { Id = 2, Name = "garge_volt_2", Type = "voltage", Role = "sensor", RegistrationCode = "rc2", DefaultName = "Battery", ParentName = "gw" });
        db.UserSensors.AddRange(
            new UserSensor { UserId = "owner", SensorId = 1, IsOwner = true },
            new UserSensor { UserId = "other", SensorId = 2, IsOwner = true });
        db.Switches.Add(new Switch { Id = 10, Name = "socket-a", Type = "socket", Role = "switch" });
        db.DiscoveredDevices.Add(new DiscoveredDevice { DiscoveredBy = "gw", Target = "socket-a", Type = "socket", Timestamp = DateTime.UtcNow });
        db.UserSwitches.Add(new UserSwitch { UserId = "viewer", SwitchId = 10, IsOwner = false, Permission = SharePermission.Read });
        await db.SaveChangesAsync();

        await CreateController(db, "owner").UnclaimSensor(1);

        Assert.Contains(db.UserSwitches, us => us.SwitchId == 10 && us.UserId == "viewer"); // still owned by "other"
    }

    [Fact]
    public async Task UnclaimSensor_Viewer_LeavesOnly_OwnerIntact()
    {
        using var db = CreateDbContext();
        db.Users.AddRange(MakeUser("owner", "owner@x"), MakeUser("viewer", "viewer@x"));
        await SeedOwnedSensorAsync(db, "owner");
        await CreateController(db, "owner").ShareSensor(1, Share("viewer@x"));

        var result = await CreateController(db, "viewer").UnclaimSensor(1);

        Assert.IsType<OkObjectResult>(result);
        Assert.DoesNotContain(db.UserSensors, us => us.UserId == "viewer" && us.SensorId == 1);   // viewer left
        Assert.Contains(db.UserSensors, us => us.UserId == "owner" && us.SensorId == 1 && us.IsOwner); // owner stays
    }
}
