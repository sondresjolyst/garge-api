using garge_api.Models;
using garge_api.Models.Sensor;
using garge_api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// Verifies the 6-month suspension cap: sensors suspended past the retention window are anonymized
/// (telemetry moved to the ML store) and force-unclaimed; recently suspended or active ones are left alone.
/// </summary>
public class SuspendedSensorPurgeServiceTests : ControllerTestBase
{
    private static IAnonymizationService Anonymizer(ApplicationDbContext db) =>
        new AnonymizationService(db, NullLogger<AnonymizationService>.Instance);

    private static IDeviceOwnershipService Ownership() => new Mock<IDeviceOwnershipService>().Object;

    private static readonly TimeSpan SixMonths = TimeSpan.FromDays(180);

    private static Sensor MakeSensor(int id) => new()
    {
        Id = id, Name = $"garge_volt_{id}", Type = "voltage", Role = "sensor",
        RegistrationCode = $"rc{id}", DefaultName = "Battery", ParentName = "gw"
    };

    [Fact]
    public async Task Purge_SensorSuspendedPastCap_AnonymizesAndUnclaims()
    {
        using var db = CreateDbContext();
        db.Sensors.Add(MakeSensor(1));
        db.UserSensors.Add(new UserSensor { UserId = "u", SensorId = 1, IsOwner = true, SuspendedAt = DateTime.UtcNow.AddDays(-200) });
        db.SensorOwnershipPeriods.Add(new SensorOwnershipPeriod { UserId = "u", SensorId = 1, StartedAt = SensorOwnershipPeriod.FirstOwnerStart, EndedAt = null });
        db.SensorData.AddRange(
            new SensorData { SensorId = 1, Value = "12.5", Timestamp = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new SensorData { SensorId = 1, Value = "12.6", Timestamp = new DateTime(2021, 1, 2, 0, 0, 0, DateTimeKind.Utc) });
        await db.SaveChangesAsync();

        var purged = await SuspendedSensorPurgeService.PurgeExpiredAsync(db, Anonymizer(db), Ownership(), SixMonths);

        Assert.Equal(1, purged);
        Assert.Empty(db.UserSensors);                 // force-unclaimed
        Assert.Empty(db.SensorOwnershipPeriods);       // period consumed by anonymization
        Assert.Empty(db.SensorData);                   // moved out of the personal store
        Assert.Equal(2, db.AnonymizedReadings.Count()); // ...into the ML store
    }

    [Fact]
    public async Task Purge_SensorSuspendedWithinCap_LeftAlone()
    {
        using var db = CreateDbContext();
        db.Sensors.Add(MakeSensor(1));
        db.UserSensors.Add(new UserSensor { UserId = "u", SensorId = 1, IsOwner = true, SuspendedAt = DateTime.UtcNow.AddDays(-10) });
        await db.SaveChangesAsync();

        var purged = await SuspendedSensorPurgeService.PurgeExpiredAsync(db, Anonymizer(db), Ownership(), SixMonths);

        Assert.Equal(0, purged);
        Assert.Single(db.UserSensors);
    }

    [Fact]
    public async Task Purge_ActiveSensor_LeftAlone()
    {
        using var db = CreateDbContext();
        db.Sensors.Add(MakeSensor(1));
        db.UserSensors.Add(new UserSensor { UserId = "u", SensorId = 1, IsOwner = true, SuspendedAt = null });
        await db.SaveChangesAsync();

        var purged = await SuspendedSensorPurgeService.PurgeExpiredAsync(db, Anonymizer(db), Ownership(), SixMonths);

        Assert.Equal(0, purged);
        Assert.Single(db.UserSensors);
    }
}
