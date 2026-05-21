using garge_api.Helpers;
using garge_api.Models.Mqtt;
using garge_api.Models.Sensor;
using garge_api.Models.Switch;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// Tests the shared resale-safe ownership-window boundary used by every read endpoint and the export.
/// A caller sees telemetry only inside their own ownership period(s); admins bypass; switches resolve
/// via a direct period or the discovered-device chain.
/// </summary>
public class OwnershipWindowQueryExtensionsTests : ControllerTestBase
{
    private const string U = "user-1";
    private static readonly DateTime Jan = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Sensor MakeSensor(int id, string parent = "gw") => new()
    {
        Id = id, Name = $"garge_volt_{id}", Type = "voltage", Role = "sensor",
        RegistrationCode = $"rc{id}", DefaultName = "Battery", ParentName = parent
    };

    [Fact]
    public async Task WithinSensorOwnership_ReturnsOnlyReadingsInsideTheWindow()
    {
        using var db = CreateDbContext();
        db.Sensors.Add(MakeSensor(1));
        db.SensorOwnershipPeriods.Add(new SensorOwnershipPeriod { UserId = U, SensorId = 1, StartedAt = Jan, EndedAt = Jan.AddDays(10) });
        db.SensorData.AddRange(
            new SensorData { SensorId = 1, Value = "before", Timestamp = Jan.AddDays(-1) },
            new SensorData { SensorId = 1, Value = "inside", Timestamp = Jan.AddDays(5) },
            new SensorData { SensorId = 1, Value = "after", Timestamp = Jan.AddDays(20) });
        await db.SaveChangesAsync();

        var result = await db.SensorData.WithinSensorOwnership(db, U).ToListAsync();

        Assert.Single(result);
        Assert.Equal("inside", result[0].Value);
    }

    [Fact]
    public async Task WithinSensorOwnership_OpenPeriod_IncludesEverythingFromStart()
    {
        using var db = CreateDbContext();
        db.Sensors.Add(MakeSensor(1));
        db.SensorOwnershipPeriods.Add(new SensorOwnershipPeriod { UserId = U, SensorId = 1, StartedAt = Jan, EndedAt = null });
        db.SensorData.AddRange(
            new SensorData { SensorId = 1, Value = "before", Timestamp = Jan.AddDays(-1) },
            new SensorData { SensorId = 1, Value = "now", Timestamp = Jan.AddDays(365) });
        await db.SaveChangesAsync();

        var result = await db.SensorData.WithinSensorOwnership(db, U).ToListAsync();

        Assert.Single(result);
        Assert.Equal("now", result[0].Value);
    }

    [Fact]
    public async Task WithinSensorOwnership_DifferentUser_SeesNothing()
    {
        using var db = CreateDbContext();
        db.Sensors.Add(MakeSensor(1));
        db.SensorOwnershipPeriods.Add(new SensorOwnershipPeriod { UserId = U, SensorId = 1, StartedAt = Jan, EndedAt = null });
        db.SensorData.Add(new SensorData { SensorId = 1, Value = "x", Timestamp = Jan.AddDays(1) });
        await db.SaveChangesAsync();

        var result = await db.SensorData.WithinSensorOwnership(db, "someone-else").ToListAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task WithinSensorOwnership_Admin_BypassesWindow()
    {
        using var db = CreateDbContext();
        db.Sensors.Add(MakeSensor(1));
        // No ownership period at all.
        db.SensorData.Add(new SensorData { SensorId = 1, Value = "x", Timestamp = Jan });
        await db.SaveChangesAsync();

        var result = await db.SensorData.WithinSensorOwnership(db, U, isAdmin: true).ToListAsync();

        Assert.Single(result);
    }

    [Fact]
    public async Task WithinSensorOwnership_BatteryChargeEvent_BoundsByStartedAt()
    {
        using var db = CreateDbContext();
        db.Sensors.Add(MakeSensor(1));
        db.SensorOwnershipPeriods.Add(new SensorOwnershipPeriod { UserId = U, SensorId = 1, StartedAt = Jan, EndedAt = Jan.AddDays(10) });
        db.BatteryChargeEvents.AddRange(
            new BatteryChargeEvent { SensorId = 1, StartedAt = Jan.AddDays(-2), EndedAt = Jan.AddDays(-1), PeakVoltage = 13, RestingAtTime = 12, PeakRatio = 1.08f, DurationMinutes = 60 },
            new BatteryChargeEvent { SensorId = 1, StartedAt = Jan.AddDays(3), EndedAt = Jan.AddDays(3), PeakVoltage = 13, RestingAtTime = 12, PeakRatio = 1.08f, DurationMinutes = 60 });
        await db.SaveChangesAsync();

        var result = await db.BatteryChargeEvents.WithinSensorOwnership(db, U).ToListAsync();

        Assert.Single(result);
        Assert.Equal(Jan.AddDays(3), result[0].StartedAt);
    }

    [Fact]
    public async Task WithinSwitchOwnership_DirectPeriod_ReturnsInWindow()
    {
        using var db = CreateDbContext();
        db.Switches.Add(new Switch { Id = 1, Name = "sw1", Type = "socket", Role = "switch" });
        db.SwitchOwnershipPeriods.Add(new SwitchOwnershipPeriod { UserId = U, SwitchId = 1, StartedAt = Jan, EndedAt = null });
        db.SwitchData.AddRange(
            new SwitchData { SwitchId = 1, Value = "before", Timestamp = Jan.AddDays(-1) },
            new SwitchData { SwitchId = 1, Value = "on", Timestamp = Jan.AddDays(2) });
        await db.SaveChangesAsync();

        var result = await db.SwitchData.WithinSwitchOwnership(db, U).ToListAsync();

        Assert.Single(result);
        Assert.Equal("on", result[0].Value);
    }

    [Fact]
    public async Task WithinSwitchOwnership_IndirectViaSensorChain_ReturnsInWindow()
    {
        using var db = CreateDbContext();
        // Switch name -> DiscoveredDevice.Target; DiscoveredBy -> Sensor.ParentName; that sensor's period grants access.
        db.Switches.Add(new Switch { Id = 1, Name = "sw1", Type = "socket", Role = "switch" });
        db.DiscoveredDevices.Add(new DiscoveredDevice { Id = 1, DiscoveredBy = "gw", Target = "sw1", Type = "socket", Timestamp = Jan });
        db.Sensors.Add(MakeSensor(1, parent: "gw"));
        db.SensorOwnershipPeriods.Add(new SensorOwnershipPeriod { UserId = U, SensorId = 1, StartedAt = Jan, EndedAt = null });
        db.SwitchData.Add(new SwitchData { SwitchId = 1, Value = "on", Timestamp = Jan.AddDays(2) });
        await db.SaveChangesAsync();

        var result = await db.SwitchData.WithinSwitchOwnership(db, U).ToListAsync();

        Assert.Single(result);
    }

    [Fact]
    public async Task WithinSwitchOwnership_NoAccess_SeesNothing()
    {
        using var db = CreateDbContext();
        db.Switches.Add(new Switch { Id = 1, Name = "sw1", Type = "socket", Role = "switch" });
        db.SwitchData.Add(new SwitchData { SwitchId = 1, Value = "on", Timestamp = Jan });
        await db.SaveChangesAsync();

        var result = await db.SwitchData.WithinSwitchOwnership(db, U).ToListAsync();

        Assert.Empty(result);
    }
}
