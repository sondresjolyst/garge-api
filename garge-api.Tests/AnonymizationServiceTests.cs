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

    private static Sensor VoltSensor() => new() { Id = 1, Name = "garge_volt", Type = "voltage", Role = "sensor", RegistrationCode = "rc", DefaultName = "Battery", ParentName = "gw" };

    private static BatteryHealth RowAt(DateTime at) =>
        new() { SensorId = 1, CurrentVoltage = 12.5f, RestingMedian = 12.4f, VoltageMin24h = 12.3f, OnChargerNow = false, Timestamp = at };

    private static BatteryChargeEvent ChargeAt(DateTime from, DateTime to) =>
        new() { SensorId = 1, StartedAt = from, EndedAt = to, PeakVoltage = 14.4f, RestingAtTime = 12.4f, DurationMinutes = 60, IsFull = true, SettledVoltage = 12.8f };

    [Fact]
    public async Task AnonymizeSensorPeriod_RowsInThreeDaysAfterThePeriod_LoseValuesFromErasedReadings()
    {
        using var db = CreateDbContext();
        db.Sensors.Add(VoltSensor());
        var period = new SensorOwnershipPeriod { UserId = "user-A", SensorId = 1, StartedAt = Jan, EndedAt = Jun };
        db.SensorOwnershipPeriods.Add(period);
        var justAfter = RowAt(Jun);
        var nearEnd = RowAt(Jun.AddDays(3).AddMinutes(-1));
        var past = RowAt(Jun.AddDays(3));
        db.BatteryHealthData.AddRange(RowAt(Mar), justAfter, nearEnd, past);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Service(db).AnonymizeSensorPeriodAsync(period.Id, TestContext.Current.CancellationToken);

        Assert.Equal(3, db.BatteryHealthData.Count());
        Assert.Null(justAfter.RestingMedian);
        Assert.Null(justAfter.VoltageMin24h);
        Assert.Equal(12.5f, justAfter.CurrentVoltage);
        Assert.Null(nearEnd.RestingMedian);
        Assert.Null(nearEnd.VoltageMin24h);
        Assert.Equal(12.4f, past.RestingMedian);
        Assert.Equal(12.3f, past.VoltageMin24h);
    }

    [Fact]
    public async Task AnonymizeSensorPeriod_RowCoveredByAnotherPeriod_KeepsItsValues()
    {
        using var db = CreateDbContext();
        db.Sensors.Add(VoltSensor());
        var period = new SensorOwnershipPeriod { UserId = "user-A", SensorId = 1, StartedAt = Jan, EndedAt = Jun };
        db.SensorOwnershipPeriods.AddRange(period, new SensorOwnershipPeriod { UserId = "user-B", SensorId = 1, StartedAt = Jun });
        var row = RowAt(Jun.AddDays(1));
        db.BatteryHealthData.Add(row);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Service(db).AnonymizeSensorPeriodAsync(period.Id, TestContext.Current.CancellationToken);

        Assert.Equal(12.4f, row.RestingMedian);
    }

    [Fact]
    public async Task AnonymizeSensorPeriod_RowOfAnotherSensorOrBeforeThePeriod_KeepsItsValues()
    {
        using var db = CreateDbContext();
        db.Sensors.Add(VoltSensor());
        var period = new SensorOwnershipPeriod { UserId = "user-A", SensorId = 1, StartedAt = Mar, EndedAt = Jun };
        db.SensorOwnershipPeriods.Add(period);
        var other = RowAt(Jun.AddDays(1));
        other.SensorId = 2;
        var before = RowAt(Jan);
        db.BatteryHealthData.AddRange(other, before);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Service(db).AnonymizeSensorPeriodAsync(period.Id, TestContext.Current.CancellationToken);

        Assert.Equal(12.4f, other.RestingMedian);
        Assert.Equal(12.4f, before.RestingMedian);
    }

    [Fact]
    public async Task AnonymizeSensorPeriod_NextClaimantWithTheFirstOwnerStart_GetsNoErasedValue()
    {
        using var db = CreateDbContext();
        db.Sensors.Add(VoltSensor());
        var sold = Jun;
        var seller = new SensorOwnershipPeriod { UserId = "seller", SensorId = 1, StartedAt = SensorOwnershipPeriod.FirstOwnerStart, EndedAt = sold };
        db.SensorOwnershipPeriods.Add(seller);
        db.SensorData.AddRange(
            new SensorData { SensorId = 1, Value = "10.5", Timestamp = sold.AddDays(-1) },
            new SensorData { SensorId = 1, Value = "12.7", Timestamp = sold.AddDays(1) });
        db.BatteryHealthData.AddRange(
            new BatteryHealth { SensorId = 1, CurrentVoltage = 10.5f, RestingMedian = 10.5f, VoltageMin24h = 10.5f, Timestamp = sold.AddDays(-1) },
            new BatteryHealth { SensorId = 1, CurrentVoltage = 12.7f, RestingMedian = 10.5f, VoltageMin24h = 10.5f, Timestamp = sold.AddDays(1) });
        db.BatteryChargeEvents.AddRange(
            ChargeAt(sold.AddDays(-10), sold.AddDays(-9)),
            ChargeAt(sold.AddHours(-2), sold.AddHours(5)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Service(db).AnonymizeSensorPeriodAsync(seller.Id, TestContext.Current.CancellationToken);

        Assert.False(db.SensorOwnershipPeriods.Any(p => p.SensorId == 1));
        db.SensorOwnershipPeriods.Add(new SensorOwnershipPeriod { UserId = "later", SensorId = 1, StartedAt = SensorOwnershipPeriod.FirstOwnerStart });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var clock = new FixedClock(sold.AddDays(2));
        var reader = new BatteryHealthReadService(db, RealMapper, clock);

        var latest = await reader.GetLatestAsync(1, "later", false, TestContext.Current.CancellationToken);
        Assert.NotNull(latest);
        Assert.Equal(12.7f, latest.CurrentVoltage);
        Assert.Null(latest.RestingMedian);
        Assert.Null(latest.VoltageMin24h);
        Assert.Null(latest.LastFullChargeAt);
        Assert.Equal("learning", latest.Status);
        Assert.Empty(await reader.GetEventsAsync(1, "later", false, null, TestContext.Current.CancellationToken));
    }

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    [Fact]
    public async Task AnonymizeSensorPeriod_ChargeOverlappingThePeriod_IsDeleted()
    {
        using var db = CreateDbContext();
        db.Sensors.Add(VoltSensor());
        var period = new SensorOwnershipPeriod { UserId = "user-A", SensorId = 1, StartedAt = Mar, EndedAt = Jun };
        db.SensorOwnershipPeriods.Add(period);
        var before = ChargeAt(Jan, Mar.AddHours(-1));
        var after = ChargeAt(Jun, Jun.AddDays(1));
        db.BatteryChargeEvents.AddRange(
            ChargeAt(Mar.AddDays(-1), Mar),
            ChargeAt(Mar.AddDays(1), Mar.AddDays(2)),
            ChargeAt(Jun.AddHours(-2), Jun.AddHours(5)),
            before,
            after);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Service(db).AnonymizeSensorPeriodAsync(period.Id, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { before.StartedAt, after.StartedAt }, db.BatteryChargeEvents.Select(e => e.StartedAt).OrderBy(t => t));
    }

    [Fact]
    public async Task AnonymizeSensorPeriod_ChargeKeptOnlyWhenAnotherPeriodCoversAllOfIt()
    {
        using var db = CreateDbContext();
        db.Sensors.Add(VoltSensor());
        var period = new SensorOwnershipPeriod { UserId = "user-A", SensorId = 1, StartedAt = Jan, EndedAt = Jul };
        db.SensorOwnershipPeriods.AddRange(period, new SensorOwnershipPeriod { UserId = "user-B", SensorId = 1, StartedAt = Mar, EndedAt = Jun });
        var whole = ChargeAt(Apr, Apr.AddDays(1));
        var endsPast = ChargeAt(Jun.AddHours(-2), Jun.AddHours(2));
        var endsAtEnd = ChargeAt(Jun.AddHours(-2), Jun);
        var startsBefore = ChargeAt(Mar.AddHours(-2), Mar.AddHours(2));
        var otherSensor = ChargeAt(Feb, Feb.AddDays(1));
        otherSensor.SensorId = 2;
        db.BatteryChargeEvents.AddRange(whole, endsPast, endsAtEnd, startsBefore, otherSensor);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Service(db).AnonymizeSensorPeriodAsync(period.Id, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { Feb, Apr }, db.BatteryChargeEvents.Select(e => e.StartedAt).OrderBy(t => t));
    }

    private static readonly DateTime Feb = new(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc);

    // A full charge ending at the given time, with every value measured later or earlier filled in.
    private static BatteryChargeEvent Measured(DateTime end) =>
        new() { SensorId = 1, StartedAt = end.AddHours(-20), EndedAt = end, PeakVoltage = 14.4f, RestingAtTime = 12.4f, DurationMinutes = 1200, IsFull = true, SettledVoltage = 12.8f, SelfDischargePctPerWeek = 0.7f };

    private async Task<BatteryChargeEvent> EraseAround(DateTime chargeEnd, DateTime from, DateTime to, params SensorOwnershipPeriod[] others)
    {
        var db = CreateDbContext();
        db.Sensors.Add(VoltSensor());
        var period = new SensorOwnershipPeriod { UserId = "user-A", SensorId = 1, StartedAt = from, EndedAt = to };
        db.SensorOwnershipPeriods.Add(period);
        db.SensorOwnershipPeriods.AddRange(others);
        var charge = Measured(chargeEnd);
        db.BatteryChargeEvents.Add(charge);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Service(db).AnonymizeSensorPeriodAsync(period.Id, TestContext.Current.CancellationToken);

        return Assert.Single(db.BatteryChargeEvents);
    }

    [Theory]
    [InlineData(12, 13, false)] // Starts 12 hours after the end, on the settled window's first hour.
    [InlineData(35, 36, false)]
    [InlineData(36, 37, false)] // Touches the last hour of the window, which is inclusive.
    [InlineData(37, 38, true)]
    [InlineData(5, 12, true)] // Ends just as the window starts, and the end of a period is not in it.
    public async Task AnonymizeSensorPeriod_SettledWindowOverlappingThePeriod_ClearsSettledVoltage(int fromHours, int toHours, bool kept)
    {
        var end = Apr;
        var charge = await EraseAround(end, end.AddHours(fromHours), end.AddHours(toHours));
        Assert.Equal(kept, charge.SettledVoltage.HasValue);
    }

    [Fact]
    public async Task AnonymizeSensorPeriod_SettledWindowCoveredByAnotherPeriod_KeepsSettledVoltage()
    {
        var end = Apr;
        var other = new SensorOwnershipPeriod { UserId = "user-B", SensorId = 1, StartedAt = end, EndedAt = end.AddDays(10) };
        var charge = await EraseAround(end, end.AddHours(20), end.AddHours(22), other);
        Assert.Equal(12.8f, charge.SettledVoltage);
    }

    [Fact]
    public async Task AnonymizeSensorPeriod_SettledWindowPartlyCoveredByAnotherPeriod_ClearsSettledVoltage()
    {
        var end = Apr;
        var other = new SensorOwnershipPeriod { UserId = "user-B", SensorId = 1, StartedAt = end, EndedAt = end.AddHours(25) };
        var charge = await EraseAround(end, end.AddHours(20), end.AddHours(22), other);
        Assert.Null(charge.SettledVoltage);
    }

    [Theory]
    [InlineData(2, 2.5, false)]
    [InlineData(14.5, 15, false)]
    [InlineData(1, 2, true)] // Ends just as the rest starts, and the end of a period is not in it.
    [InlineData(1, 2.1, false)]
    [InlineData(0.5, 1.5, true)]
    [InlineData(15.5, 16, true)]
    public async Task AnonymizeSensorPeriod_MeasuredRestOverlappingThePeriod_ClearsSelfDischarge(double fromDay, double toDay, bool kept)
    {
        var end = Apr;
        var day0 = end.AddHours(36);
        var charge = await EraseAround(end, day0.AddDays(fromDay), day0.AddDays(toDay));
        Assert.Equal(kept, charge.SelfDischargePctPerWeek.HasValue);
    }

    [Fact]
    public async Task AnonymizeSensorPeriod_MeasuredRestCoveredByAnotherPeriod_KeepsSelfDischarge()
    {
        var end = Apr;
        var day0 = end.AddHours(36);
        var other = new SensorOwnershipPeriod { UserId = "user-B", SensorId = 1, StartedAt = end, EndedAt = day0.AddDays(16) };
        var charge = await EraseAround(end, day0.AddDays(5), day0.AddDays(6), other);
        Assert.Equal(0.7f, charge.SelfDischargePctPerWeek);
    }

    [Theory]
    [InlineData(-8, -7, true)] // Ends exactly as the 7 days start.
    [InlineData(-8, -6.5, false)]
    [InlineData(-1, -0.5, false)]
    public async Task AnonymizeSensorPeriod_BaselineWindowOverlappingThePeriod_ClearsTheBaseline(double fromDays, double toDays, bool kept)
    {
        var end = Apr;
        var start = end.AddHours(-20);
        var charge = await EraseAround(end, start.AddDays(fromDays), start.AddDays(toDays));
        Assert.Equal(kept, charge.RestingAtTime.HasValue);
    }

    [Fact]
    public async Task AnonymizeSensorPeriod_PeriodAfterTheChargeStart_KeepsTheBaseline()
    {
        var end = Apr;
        var charge = await EraseAround(end, end.AddHours(20), end.AddHours(22));
        Assert.Equal(12.4f, charge.RestingAtTime);
    }

    [Fact]
    public async Task AnonymizeSensorPeriod_WindowsInsideTheErasedPeriod_AreCleared()
    {
        var end = Apr;
        var charge = await EraseAround(end, end.AddHours(10), end.AddDays(20));
        Assert.Null(charge.SettledVoltage);
        Assert.Null(charge.SelfDischargePctPerWeek);
    }

    [Fact]
    public async Task AnonymizeSensorPeriod_ChargeOfAnotherSensor_KeepsItsValues()
    {
        var db = CreateDbContext();
        db.Sensors.Add(VoltSensor());
        var period = new SensorOwnershipPeriod { UserId = "user-A", SensorId = 1, StartedAt = Apr.AddHours(20), EndedAt = Apr.AddHours(22) };
        db.SensorOwnershipPeriods.Add(period);
        var other = Measured(Apr);
        other.SensorId = 2;
        db.BatteryChargeEvents.Add(other);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Service(db).AnonymizeSensorPeriodAsync(period.Id, TestContext.Current.CancellationToken);

        Assert.Equal(12.8f, other.SettledVoltage);
    }

    [Fact]
    public async Task AnonymizeSensorPeriod_OtherPeriodsOfAnotherSensor_DoNotCover()
    {
        var end = Apr;
        var other = new SensorOwnershipPeriod { UserId = "user-B", SensorId = 2, StartedAt = end, EndedAt = end.AddDays(10) };
        var charge = await EraseAround(end, end.AddHours(20), end.AddHours(22), other);
        Assert.Null(charge.SettledVoltage);
    }

    [Fact]
    public async Task AnonymizeSensorPeriod_MovesExclusiveReadings_DeletesOriginalsAndDerived()
    {
        using var db = CreateDbContext();
        db.Sensors.Add(new Sensor { Id = 1, Name = "garge_volt", Type = "voltage", Role = "sensor", RegistrationCode = "rc", DefaultName = "Battery", ParentName = "gw" });
        var period = new SensorOwnershipPeriod { UserId = "user-A", SensorId = 1, StartedAt = Jan, EndedAt = Jun };
        db.SensorOwnershipPeriods.Add(period);
        db.SensorData.AddRange(
            new SensorData { SensorId = 1, Value = "12.5", Timestamp = new DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc) },
            new SensorData { SensorId = 1, Value = "12.6", Timestamp = Mar },
            new SensorData { SensorId = 1, Value = "12.7", Timestamp = Apr },
            new SensorData { SensorId = 1, Value = "13.0", Timestamp = Jul }); // outside the period window
        db.BatteryHealthData.Add(new BatteryHealth { SensorId = 1, Timestamp = Mar });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var moved = await Service(db).AnonymizeSensorPeriodAsync(period.Id, TestContext.Current.CancellationToken);

        Assert.Equal(3, moved);
        var series = Assert.Single(db.AnonymizedSeries);
        Assert.Equal("voltage", series.SourceType);
        Assert.Null(series.CalibrationOffsetV);
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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var moved = await Service(db).AnonymizeSensorPeriodAsync(pA.Id, TestContext.Current.CancellationToken);

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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var moved = await Service(db).AnonymizeSwitchPeriodAsync(period.Id, TestContext.Current.CancellationToken);

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
