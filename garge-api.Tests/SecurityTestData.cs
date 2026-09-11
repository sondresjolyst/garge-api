using garge_api.Constants;
using garge_api.Hubs;
using garge_api.Models;
using garge_api.Models.Automation;
using garge_api.Models.Sensor;
using garge_api.Models.Switch;
using garge_api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace garge_api.Tests;

internal static class SecurityTestData
{
    public const string Owner = "owner-1";
    public const int SensorId = 1;
    public const int SocketId = 10;
    public const string Device = "garge_a1b2c3d4e5f6";

    public static Sensor AddSensor(ApplicationDbContext db, int id = SensorId, string device = Device) =>
        db.Sensors.Add(new Sensor
        {
            Id = id,
            Name = $"{device}_voltage{(id == SensorId ? "" : id.ToString())}",
            Type = "voltage",
            Role = "sensor",
            RegistrationCode = $"code-{id}",
            DefaultName = $"Bike {id}",
            ParentName = device,
        }).Entity;

    public static void AddSocket(ApplicationDbContext db, int id = SocketId, string type = SwitchTypes.Socket) =>
        db.Switches.Add(new Switch { Id = id, Name = $"wiz_SOCKET_{id}", Type = type, Role = "switch" });

    public static AutomationRule AddChargingRule(
        ApplicationDbContext db, int sensorId = SensorId, int targetId = SocketId,
        string condition = "<", double threshold = 12.65, string action = "on", bool enabled = true) =>
        db.AutomationRules.Add(new AutomationRule
        {
            TargetType = SwitchTypes.Socket,
            TargetId = targetId,
            SensorType = "voltage",
            SensorId = sensorId,
            Condition = condition,
            Threshold = threshold,
            Action = action,
            IsEnabled = enabled,
        }).Entity;

    public static void AddOwner(ApplicationDbContext db, string userId = Owner, int sensorId = SensorId, bool push = true, bool email = true)
    {
        if (!db.UserProfiles.Any(p => p.Id == userId) && db.UserProfiles.Local.All(p => p.Id != userId))
        {
            db.UserProfiles.Add(new UserProfile
            {
                Id = userId,
                PushNotificationsEnabled = push,
                EmailNotificationsEnabled = email,
                User = new User { Id = userId, UserName = userId, Email = $"{userId}@example.com", FirstName = "Test", LastName = "User" },
            });
        }
        db.UserSensors.Add(new UserSensor { UserId = userId, SensorId = sensorId, IsOwner = true });
    }

    /// <summary>Sensor + socket + charging rule + entitled owner with push and email on.</summary>
    public static void SeedReady(ApplicationDbContext db)
    {
        AddSensor(db);
        AddSocket(db);
        AddChargingRule(db);
        AddOwner(db);
        db.SaveChanges();
    }

    public static (SecurityModeService Service, Mock<IDeviceSettingsPublisher> Publisher, Mock<ISecurityNotifier> Notifier) BuildService(ApplicationDbContext db)
    {
        var publisher = new Mock<IDeviceSettingsPublisher>();
        var notifier = new Mock<ISecurityNotifier>();
        notifier.Setup(n => n.NotifyUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = new SecurityModeService(db, new PermissionService(db), publisher.Object, notifier.Object,
            NullLogger<SecurityModeService>.Instance);
        return (service, publisher, notifier);
    }

    public static void VerifyPublished(Mock<IDeviceSettingsPublisher> publisher, int sleepSeconds, bool security, int? floorMillivolts, Times times) =>
        publisher.Verify(p => p.EnqueueDeviceSettingsForBridges(It.Is<DeviceSettingsEventDto>(e =>
            e.DeviceName == Device && e.SleepSeconds == sleepSeconds && e.SecurityEnabled == security && e.FloorMillivolts == floorMillivolts)), times);
}
