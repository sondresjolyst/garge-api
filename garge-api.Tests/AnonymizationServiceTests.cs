using garge_api.Models.Anonymized;
using garge_api.Models.Sensor;
using garge_api.Models.Switch;
using garge_api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// Verifies the anonymize-to-ML-store routine: telemetry exclusive to an ownership period is copied
/// into AnonymizedSeries/AnonymizedReading (no link back), originals + regenerable battery data are
/// deleted, and co-owned ranges are preserved. Switch on/off maps to 1/0.
/// </summary>
public class AnonymizationServiceTests : ControllerTestBase
{
    private AnonymizationService Service(Models.ApplicationDbContext db) =>
        new(db, NullLogger<AnonymizationService>.Instance);

    private static readonly DateTime Jan = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Mar = new(2020, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Apr = new(2020, 4, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Jun = new(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Jul = new(2020, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Sep = new(2020, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task AnonymizeSensorPeriod_MovesExclusiveReadings_DeletesOriginalsAndDerived()
    {
        using var db = CreateDbContext();
        db.Sensors.Add(new Sensor { Id = 1, Name = "garge_volt", Type = "voltage", Role = "sensor", RegistrationCode = "rc", DefaultName = "Battery", ParentName = "gw", CalibrationOffsetV = 0.5f });
        var period = new SensorOwnershipPeriod { UserId = "user-A", SensorId = 1, StartedAt = Jan, EndedAt = Jun };
        db.SensorOwnershipPeriods.Add(period);
        db.SensorData.AddRange(
            new SensorData { SensorId = 1, Value = "12.5", Timestamp = new DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc) },
            new SensorData { SensorId = 1, Value = "12.6", Timestamp = Mar },
            new SensorData { SensorId = 1, Value = "12.7", Timestamp = Apr },
            new SensorData { SensorId = 1, Value = "13.0", Timestamp = Jul }); // outside the period window
        db.BatteryHealthData.Add(new BatteryHealth { SensorId = 1, Status = "ok", Timestamp = Mar });
        await db.SaveChangesAsync();

        var moved = await Service(db).AnonymizeSensorPeriodAsync(period.Id);

        Assert.Equal(3, moved);
        var series = Assert.Single(db.AnonymizedSeries);
        Assert.Equal("voltage", series.SourceType);
        Assert.Equal(0.5f, series.CalibrationOffsetV);
        Assert.Equal(3, db.AnonymizedReadings.Count());
        Assert.Contains(db.AnonymizedReadings, r => r.Value == 12.5);
        Assert.Single(db.SensorData); // only the out-of-window Jul row remains
        Assert.Empty(db.BatteryHealthData); // derived data dropped (regenerable)
        Assert.Empty(db.SensorOwnershipPeriods); // period consumed
    }

    [Fact]
    public async Task AnonymizeSensorPeriod_KeepsCoOwnedRange()
    {
        using var db = CreateDbContext();
        db.Sensors.Add(new Sensor { Id = 1, Name = "garge_volt", Type = "voltage", Role = "sensor", RegistrationCode = "rc", DefaultName = "Battery", ParentName = "gw" });
        var pA = new SensorOwnershipPeriod { UserId = "user-A", SensorId = 1, StartedAt = Jan, EndedAt = Jun };
        var pB = new SensorOwnershipPeriod { UserId = "user-B", SensorId = 1, StartedAt = Mar, EndedAt = Sep }; // overlaps Mar..Jun
        db.SensorOwnershipPeriods.AddRange(pA, pB);
        db.SensorData.AddRange(
            new SensorData { SensorId = 1, Value = "1", Timestamp = new DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc) }, // only A
            new SensorData { SensorId = 1, Value = "2", Timestamp = Apr }); // A and B overlap
        await db.SaveChangesAsync();

        var moved = await Service(db).AnonymizeSensorPeriodAsync(pA.Id);

        Assert.Equal(1, moved); // only the A-exclusive Feb row
        Assert.Single(db.SensorData); // Feb moved out; the co-owned Apr row stays — B is still entitled
        Assert.Equal(Apr, db.SensorData.Single().Timestamp);
        Assert.Single(db.SensorOwnershipPeriods); // pB remains
    }

    [Fact]
    public async Task AnonymizeSwitchPeriod_MapsOnOffToOneZero()
    {
        using var db = CreateDbContext();
        db.Switches.Add(new Switch { Id = 1, Name = "garge_socket", Type = "socket", Role = "switch", RegistrationCode = "rc" });
        var period = new SwitchOwnershipPeriod { UserId = "user-A", SwitchId = 1, StartedAt = Jan, EndedAt = Jun };
        db.SwitchOwnershipPeriods.Add(period);
        db.SwitchData.AddRange(
            new SwitchData { SwitchId = 1, Value = "on", Timestamp = new DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc) },
            new SwitchData { SwitchId = 1, Value = "off", Timestamp = Mar },
            new SwitchData { SwitchId = 1, Value = "garbage", Timestamp = Apr });
        await db.SaveChangesAsync();

        var moved = await Service(db).AnonymizeSwitchPeriodAsync(period.Id);

        Assert.Equal(2, moved); // garbage dropped
        var series = Assert.Single(db.AnonymizedSeries);
        Assert.Equal("socket", series.SourceType);
        Assert.Equal(new[] { 1.0, 0.0 }, db.AnonymizedReadings.OrderBy(r => r.Timestamp).Select(r => r.Value));
        Assert.Empty(db.SwitchData);
        Assert.Empty(db.SwitchOwnershipPeriods);
    }

    [Theory]
    [InlineData("12.5", true, 12.5)]
    [InlineData("on", true, 1)]
    [InlineData("OFF", true, 0)]
    [InlineData("true", true, 1)]
    [InlineData("nonsense", false, 0)]
    public void TryParseValue_HandlesNumbersAndOnOff(string raw, bool ok, double expected)
    {
        Assert.Equal(ok, AnonymizationService.TryParseValue(raw, out var value));
        if (ok) Assert.Equal(expected, value);
    }
}
