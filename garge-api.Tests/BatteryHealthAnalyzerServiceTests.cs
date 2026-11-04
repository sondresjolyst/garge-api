using garge_api.Dtos.Sensor;
using garge_api.Helpers;
using garge_api.Models;
using garge_api.Models.Sensor;
using garge_api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Sample = garge_api.Helpers.BatteryHealthMath.VoltageSample;

namespace garge_api.Tests;

public class BatteryHealthAnalyzerServiceTests : ControllerTestBase
{
    private const int SensorId = 1;
    private static readonly DateTime T0 = BatteryTrace.T0;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal sealed class TestClock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    internal sealed class Harness
    {
        private readonly ServiceProvider _provider;
        public TestClock Clock { get; }
        public BatteryHealthAnalyzerService Analyzer { get; }

        public Harness(DateTime now)
        {
            var name = Guid.NewGuid().ToString();
            _provider = new ServiceCollection()
                .AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(name))
                .BuildServiceProvider();
            Clock = new TestClock(now);
            Analyzer = new BatteryHealthAnalyzerService(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<BatteryHealthAnalyzerService>.Instance,
                Clock);
        }

        public async Task<T> WithDb<T>(Func<ApplicationDbContext, Task<T>> work)
        {
            using var scope = _provider.CreateScope();
            return await work(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
        }

        public async Task Seed(IEnumerable<Sample> samples, string type = "voltage")
        {
            await WithDb(async db =>
            {
                db.Sensors.Add(new Sensor { Id = SensorId, Name = "garge_volt", Type = type, Role = "sensor", RegistrationCode = "rc", DefaultName = "Battery", ParentName = "gw" });
                db.SensorData.AddRange(samples.Select(s => new SensorData
                {
                    SensorId = SensorId,
                    Value = s.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                    Timestamp = s.Timestamp,
                }));
                return await db.SaveChangesAsync(Ct);
            });
        }

        public Task<List<BatteryChargeEvent>> Events() =>
            WithDb(db => db.BatteryChargeEvents.AsNoTracking().OrderBy(e => e.StartedAt).ToListAsync(Ct));

        public Task<List<BatteryHealth>> Health() =>
            WithDb(db => db.BatteryHealthData.AsNoTracking().OrderBy(h => h.Timestamp).ThenBy(h => h.Id).ToListAsync(Ct));

        /// <summary>The latest health as an admin reads it, with no ownership limit.</summary>
        public async Task<BatteryHealthDto> LatestHealth() => (await View(null, isAdmin: true))!;

        public Task<BatteryHealthDto?> View(string? userId, bool isAdmin = false) =>
            WithDb(db => new BatteryHealthReadService(db, RealMapper, Clock).GetLatestAsync(SensorId, userId, isAdmin, Ct));

        public Task<List<BatteryChargeEventDto>> EventsFor(string? userId, bool isAdmin = false) =>
            WithDb(db => new BatteryHealthReadService(db, RealMapper, Clock).GetEventsAsync(SensorId, userId, isAdmin, null, Ct));

        public Task Reanalyze() => Analyzer.ReanalyzeSensorAsync(SensorId, Ct);
        public Task Analyze() => Analyzer.AnalyzeSensorAsync(SensorId, Ct);
    }

    private static DateTime NowAfter(BatteryTrace trace) => trace.Samples[^1].Timestamp.AddMinutes(5);

    private static BatteryTrace ChargesWithRest(params (int charge, float settled)[] cycles)
    {
        var trace = new BatteryTrace().Hold(72, 12.4f);
        foreach (var (charge, settled) in cycles) trace.Hold(charge, 14.4f).Hold(200, settled);
        return trace;
    }

    private static object Comparable(BatteryChargeEvent e) =>
        new { e.SensorId, e.StartedAt, e.EndedAt, e.PeakVoltage, e.RestingAtTime, e.DurationMinutes, e.IsFull, e.SettledVoltage, e.SelfDischargePctPerWeek };

    private static List<object> Comparable(IEnumerable<BatteryChargeEvent> events) => events.Select(Comparable).ToList();

    // Run model.

    [Fact]
    public async Task Analyze_StoresFinishedChargesWithAllFields()
    {
        var trace = ChargesWithRest((24, 12.7f));
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);

        await h.Analyze();

        var e = Assert.Single(await h.Events());
        Assert.Equal(T0.AddHours(72), e.StartedAt);
        Assert.Equal(T0.AddHours(96), e.EndedAt);
        Assert.Equal(24 * 60, e.DurationMinutes);
        Assert.Equal(14.4f, e.PeakVoltage);
        Assert.Equal(12.4f, e.RestingAtTime);
        Assert.True(e.IsFull);
        Assert.Equal(12.7f, e.SettledVoltage);
    }

    [Fact]
    public async Task Analyze_WritesHealthRowFields()
    {
        var trace = ChargesWithRest((24, 12.7f), (24, 12.7f));
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);

        await h.Analyze();

        Assert.Single(await h.Health());
        var row = await h.LatestHealth();
        Assert.Equal(trace.Samples[^1].Timestamp, row.Timestamp);
        Assert.Equal("good", row.Status);
        Assert.Equal(12.7f, row.CurrentVoltage);
        Assert.Equal(12.7f, row.RestingMedian);
        Assert.False(row.OnChargerNow);
        Assert.Equal((await h.Events())[^1].EndedAt, row.LastFullChargeAt);
        Assert.Equal(14.4f, row.LastFullChargePeak);
        Assert.Equal(12.7f, row.VoltageMin24h);
        Assert.Equal(2, row.FullChargesLast30d);
    }

    [Fact]
    public async Task Analyze_OpenCharge_IsNotStoredAndSetsOnCharger()
    {
        var trace = new BatteryTrace().Hold(72, 12.4f).Hold(12, 14.4f);
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);

        await h.Analyze();

        Assert.Empty(await h.Events());
        var row = await h.LatestHealth();
        Assert.True(row.OnChargerNow);
        Assert.Equal("learning", row.Status);
    }

    [Fact]
    public async Task Analyze_NoRestReadings_StoresNullRestingNotZero()
    {
        var trace = new BatteryTrace().Hold(12, 12.4f).Hold(100, 14.4f);
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);

        await h.Analyze();

        Assert.Null((await h.LatestHealth()).RestingMedian);
    }

    [Fact]
    public async Task Analyze_NotAVoltageSensor_DoesNothing()
    {
        var trace = ChargesWithRest((24, 12.7f));
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples, type: "temperature");

        await h.Analyze();
        await h.Reanalyze();

        Assert.Empty(await h.Events());
        Assert.Empty(await h.Health());
    }

    [Fact]
    public async Task Analyze_UnknownSensor_DoesNothing()
    {
        var h = new Harness(T0);
        await h.Analyzer.AnalyzeSensorAsync(99, Ct);
        Assert.Empty(await h.Health());
    }

    [Theory]
    [InlineData("1.102")]
    [InlineData("4.99")]
    [InlineData("20.01")]
    [InlineData("not-a-number")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public async Task Analyze_InvalidLatestReading_IsDropped(string bad)
    {
        var trace = ChargesWithRest((24, 12.7f));
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await h.WithDb(async db =>
        {
            db.SensorData.Add(new SensorData { SensorId = SensorId, Value = bad, Timestamp = h.Clock.Now.AddMinutes(-1) });
            return await db.SaveChangesAsync(Ct);
        });

        await h.Analyze();

        var row = await h.LatestHealth();
        Assert.Equal(12.7f, row.CurrentVoltage);
        Assert.Equal(12.7f, row.VoltageMin24h);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public async Task Analyze_NonFiniteMiddleReading_ChangesNothing(string bad)
    {
        var trace = ChargesWithRest((24, 12.7f), (24, 12.7f));
        var clean = new Harness(NowAfter(trace));
        await clean.Seed(trace.Samples);
        await clean.Analyze();

        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await h.WithDb(async db =>
        {
            // One during the charge, where it could reach the peak and the plateau, and one at rest.
            db.SensorData.Add(new SensorData { SensorId = SensorId, Value = bad, Timestamp = T0.AddHours(80.5) });
            db.SensorData.Add(new SensorData { SensorId = SensorId, Value = bad, Timestamp = T0.AddHours(150.5) });
            return await db.SaveChangesAsync(Ct);
        });
        await h.Analyze();

        Assert.Equal(Comparable(await clean.Events()), Comparable(await h.Events()));
        Assert.Equal((await clean.LatestHealth()).Status, (await h.LatestHealth()).Status);
        Assert.Equal((await clean.LatestHealth()).RestingMedian, (await h.LatestHealth()).RestingMedian);
    }

    [Fact]
    public async Task Reanalyze_OnlyNonFiniteReadings_AddsNoRow()
    {
        var h = new Harness(T0.AddDays(1));
        await h.Seed(Array.Empty<Sample>());
        await h.WithDb(async db =>
        {
            db.SensorData.Add(new SensorData { SensorId = SensorId, Value = "NaN", Timestamp = T0 });
            return await db.SaveChangesAsync(Ct);
        });

        await h.Reanalyze();

        Assert.Empty(await h.Health());
    }

    [Theory]
    [InlineData("5")]
    [InlineData("20")]
    public async Task Analyze_ReadingAtValidLimit_IsKept(string edge)
    {
        var trace = new BatteryTrace().Hold(10, 12.4f);
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await h.WithDb(async db =>
        {
            db.SensorData.Add(new SensorData { SensorId = SensorId, Value = edge, Timestamp = h.Clock.Now.AddMinutes(-1) });
            return await db.SaveChangesAsync(Ct);
        });

        await h.Analyze();

        Assert.Equal(float.Parse(edge), (await h.LatestHealth()).CurrentVoltage);
    }

    [Fact]
    public async Task Analyze_ReadingAfterNow_IsIgnored()
    {
        var trace = new BatteryTrace().Hold(10, 12.4f).Hold(10, 14.4f);
        var h = new Harness(T0.AddHours(9.5));
        await h.Seed(trace.Samples);

        await h.Analyze();

        Assert.Equal(12.4f, (await h.LatestHealth()).CurrentVoltage);
    }

    [Fact]
    public async Task Analyze_VoltageMin24h_LooksBackOneDay()
    {
        var trace = new BatteryTrace().Hold(10, 12.1f).Hold(30, 12.2f);
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);

        await h.Analyze();

        Assert.Equal(12.2f, (await h.LatestHealth()).VoltageMin24h);
    }

    [Fact]
    public async Task Analyze_FullChargesLast30d_CountsOnlyRecentFullCharges()
    {
        // 24 hours of charge are full, 5 hours are not, and the first ends more than 30 days ago.
        var trace = new BatteryTrace().Hold(72, 12.4f).Hold(24, 14.4f).Hold(800, 12.7f).Hold(24, 14.4f).Hold(200, 12.7f).Hold(6, 14.0f).Hold(100, 12.7f);
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);

        await h.Analyze();

        var row = await h.LatestHealth();
        Assert.Equal(1, row.FullChargesLast30d);
        var events = await h.Events();
        Assert.Equal(3, events.Count);
        // The last full charge is the second one, the third was partial.
        Assert.Equal(events[1].EndedAt, row.LastFullChargeAt);
        Assert.Equal(14.4f, row.LastFullChargePeak);
    }

    [Fact]
    public async Task Analyze_FullChargeJustOverThirtyDaysOld_DoesNotCount()
    {
        var trace = new BatteryTrace().Hold(72, 12.4f).Hold(24, 14.4f).Hold(31 * 24, 12.7f);
        var h = new Harness(T0.AddHours(72 + 24).AddDays(30).AddMinutes(1));
        await h.Seed(trace.Samples);

        await h.Analyze();

        var row = await h.LatestHealth();
        Assert.Equal(0, row.FullChargesLast30d);
        Assert.NotNull(row.LastFullChargeAt);
    }

    [Fact]
    public async Task Analyze_FullChargeExactlyThirtyDaysOld_Counts()
    {
        var trace = new BatteryTrace().Hold(72, 12.4f).Hold(24, 14.4f).Hold(30 * 24, 12.7f);
        var h = new Harness(T0.AddHours(72 + 24).AddDays(30));
        await h.Seed(trace.Samples);

        await h.Analyze();

        Assert.Equal(1, (await h.LatestHealth()).FullChargesLast30d);
    }

    [Fact]
    public async Task Analyze_SelfDischarge_UsesRestOlderThanTheReplayStart()
    {
        // The long rest has ended and lies before the final charge's end, so the
        // replay does not read it.
        var trace = new BatteryTrace().Hold(72, 12.4f).Hold(24, 14.4f)
            .Ramp(600, 13.0f, 12.8f)
            .Hold(24, 14.4f).Hold(160, 13.0f);
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await h.Reanalyze();
        await h.Analyze();

        var events = await h.Events();
        Assert.Equal(2, events.Count);
        var row = await h.LatestHealth();
        Assert.NotNull(row.SelfDischargePctPerWeek);
        var expected = BatteryHealthMath.ComputeSelfDischarge(trace.Samples, events[0].EndedAt);
        Assert.Equal(expected, row.SelfDischargePctPerWeek);
    }

    [Fact]
    public async Task Analyze_SelfDischarge_AnOpenChargeEndsTheRest()
    {
        // 10 days of rest, then a charge that is still open after 10 more days.
        var trace = new BatteryTrace().Hold(72, 12.4f).Hold(24, 14.4f).Hold(240, 12.8f).Hold(240, 14.4f);
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);

        await h.Analyze();

        var row = await h.LatestHealth();
        Assert.True(row.OnChargerNow);
        Assert.Null(row.SelfDischargePctPerWeek);
    }

    [Fact]
    public async Task Analyze_SelfDischarge_NullUntilALongRestExists()
    {
        var trace = ChargesWithRest((24, 12.7f));
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);

        await h.Analyze();

        Assert.Null((await h.LatestHealth()).SelfDischargePctPerWeek);
    }

    [Fact]
    public async Task Analyze_SelfDischarge_DoesNotChangeStatus()
    {
        var trace = new BatteryTrace().Hold(72, 12.4f).Hold(24, 14.4f).Ramp(600, 13.0f, 11.0f);
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);

        await h.Analyze();

        var row = await h.LatestHealth();
        Assert.True(row.SelfDischargePctPerWeek > 1f);
        Assert.Equal("learning", row.Status);
    }

    // Offline.

    [Fact]
    public async Task Analyze_ReadingOlderThanThreeDays_AddsNoRowAndLeavesTheLast()
    {
        var trace = ChargesWithRest((24, 12.7f));
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await h.Analyze();
        var before = await h.Health();
        var eventsBefore = await h.Events();

        h.Clock.Now = trace.Samples[^1].Timestamp.AddDays(3).AddMinutes(1);
        await h.Analyze();

        Assert.Equal(before.Count, (await h.Health()).Count);
        Assert.Equal(Comparable(eventsBefore), Comparable(await h.Events()));
    }

    [Fact]
    public async Task Analyze_ReadingExactlyThreeDaysOld_StillAddsARow()
    {
        var trace = ChargesWithRest((24, 12.7f));
        var h = new Harness(trace.Samples[^1].Timestamp.AddDays(3));
        await h.Seed(trace.Samples);

        await h.Analyze();

        Assert.Single(await h.Health());
    }

    [Fact]
    public async Task Analyze_OnlyInvalidRecentReadings_CountsAsOffline()
    {
        var trace = ChargesWithRest((24, 12.7f));
        var h = new Harness(trace.Samples[^1].Timestamp.AddDays(10));
        await h.Seed(trace.Samples);
        await h.WithDb(async db =>
        {
            db.SensorData.Add(new SensorData { SensorId = SensorId, Value = "1.1", Timestamp = h.Clock.Now.AddMinutes(-1) });
            return await db.SaveChangesAsync(Ct);
        });

        await h.Analyze();

        Assert.Empty(await h.Health());
        Assert.Empty(await h.Events());
    }

    [Fact]
    public async Task Sweep_OfflineSensor_AddsNoRow()
    {
        var trace = ChargesWithRest((24, 12.7f));
        var h = new Harness(trace.Samples[^1].Timestamp.AddDays(4));
        await h.Seed(trace.Samples);

        await h.Analyzer.SweepStaleSensorsAsync(Ct);

        Assert.Empty(await h.Health());
        Assert.Empty(await h.Events());
    }

    [Fact]
    public async Task Sweep_OnlineSensorWithOldRow_GetsARow_FreshOneDoesNot()
    {
        var trace = ChargesWithRest((24, 12.7f));
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);

        await h.Analyzer.SweepStaleSensorsAsync(Ct);
        Assert.Single(await h.Health());

        var rowTime = (await h.LatestHealth()).Timestamp;
        await AddReading(h, rowTime.AddMinutes(1), "12.7");
        h.Clock.Now = rowTime.AddMinutes(90);
        await h.Analyzer.SweepStaleSensorsAsync(Ct);
        Assert.Single(await h.Health());

        h.Clock.Now = rowTime.AddMinutes(91);
        await h.Analyzer.SweepStaleSensorsAsync(Ct);
        Assert.Equal(2, (await h.Health()).Count);
    }

    // Reanalyze.

    [Fact]
    public async Task Reanalyze_RunTwice_GivesIdenticalEvents()
    {
        var samples = BatteryFixtures.Samples(BatteryFixtures.TwoCloseCharges);
        var h = new Harness(samples[^1].Timestamp.AddMinutes(5));
        await h.Seed(samples);

        await h.Reanalyze();
        var first = await h.Events();
        await h.Reanalyze();
        var second = await h.Events();

        Assert.Equal(8, first.Count);
        Assert.Equal(Comparable(first), Comparable(second));
        Assert.Equal(first.Select(e => e.Id), second.Select(e => e.Id));
    }

    [Fact]
    public async Task Reanalyze_ReplacesEventsThatTheReadingsDoNotSupport()
    {
        var trace = ChargesWithRest((24, 12.7f));
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await h.WithDb(async db =>
        {
            db.BatteryChargeEvents.Add(new BatteryChargeEvent { SensorId = SensorId, StartedAt = T0.AddDays(-400), EndedAt = T0.AddDays(-399), PeakVoltage = 14, RestingAtTime = 12, DurationMinutes = 60, IsFull = true, SettledVoltage = 13 });
            return await db.SaveChangesAsync(Ct);
        });

        await h.Reanalyze();

        var e = Assert.Single(await h.Events());
        Assert.Equal(T0.AddHours(72), e.StartedAt);
    }

    [Fact]
    public async Task Reanalyze_OfflineSensor_RebuildsEventsAndAddsNoRow()
    {
        var trace = ChargesWithRest((24, 12.7f), (24, 12.7f));
        var h = new Harness(trace.Samples[^1].Timestamp.AddDays(30));
        await h.Seed(trace.Samples);

        await h.Reanalyze();

        Assert.Equal(2, (await h.Events()).Count);
        Assert.Empty(await h.Health());
    }

    [Fact]
    public async Task OfflineSensor_StatusAtReadTimeComesFromRebuiltCharges()
    {
        var trace = ChargesWithRest((24, 12.7f), (24, 12.7f));
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await h.Analyze();
        var rows = (await h.Health()).Count;

        h.Clock.Now = h.Clock.Now.AddDays(20);
        await h.WithDb(async db =>
        {
            db.BatteryChargeEvents.RemoveRange(db.BatteryChargeEvents);
            return await db.SaveChangesAsync(Ct);
        });
        Assert.Equal("learning", (await h.LatestHealth()).Status);

        await h.Reanalyze();

        Assert.Equal(rows, (await h.Health()).Count);
        Assert.Equal("good", (await h.LatestHealth()).Status);
    }

    [Fact]
    public async Task Reanalyze_NoReadings_RemovesEventsAndAddsNoRow()
    {
        var h = new Harness(T0);
        await h.Seed(Array.Empty<Sample>());
        await h.WithDb(async db =>
        {
            db.BatteryChargeEvents.Add(new BatteryChargeEvent { SensorId = SensorId, StartedAt = T0.AddDays(-400), EndedAt = T0.AddDays(-399) });
            return await db.SaveChangesAsync(Ct);
        });

        await h.Reanalyze();

        Assert.Empty(await h.Events());
        Assert.Empty(await h.Health());
    }

    // Incremental equals full.

    [Theory]
    [InlineData(BatteryFixtures.LongRestSlowRamp, 24)]
    [InlineData(BatteryFixtures.SuddenDrop, 24)]
    [InlineData(BatteryFixtures.TwoCloseCharges, 24)]
    [InlineData(BatteryFixtures.RegularCycles, 24)]
    [InlineData(BatteryFixtures.LongFloat, 24)]
    // A run every 7 days acts like a service that was down past the 5-day limit.
    [InlineData(BatteryFixtures.LongRestSlowRamp, 168)]
    [InlineData(BatteryFixtures.SuddenDrop, 168)]
    [InlineData(BatteryFixtures.TwoCloseCharges, 168)]
    [InlineData(BatteryFixtures.RegularCycles, 168)]
    [InlineData(BatteryFixtures.LongFloat, 168)]
    public async Task IncrementalRuns_EqualOneFullReplay(string fixture, int stepHours)
    {
        var samples = BatteryFixtures.Samples(fixture);
        var end = samples[^1].Timestamp;
        var h = new Harness(samples[0].Timestamp);
        await h.Seed(samples);

        for (var t = samples[0].Timestamp.AddHours(stepHours); t < end; t = t.AddHours(stepHours))
        {
            h.Clock.Now = t;
            await h.Analyze();
        }
        h.Clock.Now = end.AddMinutes(5);
        await h.Analyze();
        var incremental = await h.Events();
        var incrementalHealth = await h.LatestHealth();

        var full = new Harness(end.AddMinutes(5));
        await full.Seed(samples);
        await full.Reanalyze();
        var fullHealth = await full.LatestHealth();

        Assert.Equal(Comparable(await full.Events()), Comparable(incremental));
        Assert.Equal(fullHealth.Status, incrementalHealth.Status);
        Assert.Equal(fullHealth.LastFullChargeAt, incrementalHealth.LastFullChargeAt);
        Assert.Equal(fullHealth.FullChargesLast30d, incrementalHealth.FullChargesLast30d);
        Assert.Equal(fullHealth.DailyDropPctPerWeek, incrementalHealth.DailyDropPctPerWeek);
        Assert.Equal(fullHealth.SelfDischargePctPerWeek, incrementalHealth.SelfDischargePctPerWeek);
        Assert.Equal(fullHealth.RestingMedian, incrementalHealth.RestingMedian);
    }

    [Fact]
    public async Task Run_ReadsOnlyFromTheFinalCharge_AndKeepsOlderHistory()
    {
        // Three full charges, the latest final. All readings before its end are then removed.
        var trace = ChargesWithRest((24, 12.7f), (24, 12.7f), (24, 12.6f));
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await h.Analyze();
        var before = await h.Events();
        Assert.Equal(3, before.Count);

        var final = before[^1];
        await h.WithDb(async db =>
        {
            db.SensorData.RemoveRange(db.SensorData.Where(sd => sd.Timestamp < final.EndedAt));
            return await db.SaveChangesAsync(Ct);
        });
        h.Clock.Now = h.Clock.Now.AddMinutes(30);
        await h.Analyze();

        Assert.Equal(Comparable(before), Comparable(await h.Events()));
        var row = await h.LatestHealth();
        Assert.Equal(3, row.FullChargesLast30d);
        Assert.Equal(final.EndedAt, row.LastFullChargeAt);
        Assert.NotNull(row.DailyDropPctPerWeek);
    }

    // With the readings before its end gone, a charge survives only if the run resumes from it.
    private static async Task<BatteryHealthAnalyzerServiceTests.Harness> RunWithEarlierReadingsGone(DateTime now)
    {
        var trace = new BatteryTrace().Hold(72, 12.4f).Hold(24, 14.4f).Hold(400, 12.7f);
        var charge = T0.AddHours(72 + 24);
        var h = new Harness(charge.AddHours(30));
        await h.Seed(trace.Samples);
        await h.Analyze();
        Assert.Single(await h.Events());

        await h.WithDb(async db =>
        {
            db.SensorData.RemoveRange(db.SensorData.Where(sd => sd.Timestamp < charge));
            return await db.SaveChangesAsync(Ct);
        });
        h.Clock.Now = now;
        await h.Analyze();
        return h;
    }

    [Fact]
    public async Task Run_ChargeExactlyFiveDaysOld_IsNotFinalYet()
    {
        var h = await RunWithEarlierReadingsGone(T0.AddHours(72 + 24).AddDays(5));
        Assert.Empty(await h.Events());
    }

    [Fact]
    public async Task Run_ChargeJustOverFiveDaysOld_IsFinal()
    {
        var h = await RunWithEarlierReadingsGone(T0.AddHours(72 + 24).AddDays(5).AddMinutes(1));
        Assert.Single(await h.Events());
    }

    [Fact]
    public async Task Run_FinalCharge_IsNotReplayed()
    {
        var trace = new BatteryTrace().Hold(72, 12.4f).Hold(24, 14.4f).Hold(400, 12.7f);
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await h.Analyze();
        await h.WithDb(async db =>
        {
            (await db.BatteryChargeEvents.SingleAsync(Ct)).PeakVoltage = 1f;
            return await db.SaveChangesAsync(Ct);
        });

        await h.Analyze();

        Assert.Equal(1f, Assert.Single(await h.Events()).PeakVoltage);
    }

    [Fact]
    public async Task Run_EndReadingGone_RebuildsFromWholeHistory()
    {
        var trace = ChargesWithRest((24, 12.7f), (24, 12.7f));
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await h.Analyze();
        var final = (await h.Events())[^1];

        await h.WithDb(async db =>
        {
            db.SensorData.RemoveRange(db.SensorData.Where(sd => sd.Timestamp == final.EndedAt));
            return await db.SaveChangesAsync(Ct);
        });
        await h.Analyze();

        var events = await h.Events();
        Assert.Equal(2, events.Count);
        Assert.Equal(final.EndedAt.AddHours(1), events[^1].EndedAt);
    }

    [Fact]
    public async Task Run_LateRunFillsSettledVoltageOnTheFinalCharge()
    {
        // The first run sees the end of the charge but not yet its settle window.
        var trace = new BatteryTrace().Hold(72, 12.4f).Hold(24, 14.4f).Hold(200, 12.7f);
        var charge = T0.AddHours(72 + 24);
        var h = new Harness(charge.AddHours(3));
        await h.Seed(trace.Samples);
        await h.Analyze();
        Assert.Null(Assert.Single(await h.Events()).SettledVoltage);

        h.Clock.Now = charge.AddDays(6);
        await h.Analyze();

        Assert.Equal(12.7f, Assert.Single(await h.Events()).SettledVoltage);
    }

    [Fact]
    public async Task Run_LateRunFindsChargeContinuingTheFinal_RebuildsAsOneRow()
    {
        var trace = new BatteryTrace().Hold(72, 12.4f).Hold(24, 14.4f).Hold(10, 12.7f).Hold(24, 14.4f).Hold(200, 12.7f);
        var charge = T0.AddHours(72 + 24);
        var h = new Harness(charge.AddHours(3));
        await h.Seed(trace.Samples);
        await h.Analyze();
        Assert.Equal(charge, Assert.Single(await h.Events()).EndedAt);

        h.Clock.Now = charge.AddDays(6);
        await h.Analyze();

        var e = Assert.Single(await h.Events());
        Assert.Equal(T0.AddHours(72), e.StartedAt);
        Assert.Equal(charge.AddHours(10 + 24), e.EndedAt);
    }

    [Fact]
    public async Task Run_StartThatMoved_LeavesNoDuplicateRow()
    {
        // A stored row with another start for the same charge. The replay's row replaces it.
        var trace = ChargesWithRest((24, 12.7f));
        var h = new Harness(T0.AddHours(72 + 24 + 48));
        await h.Seed(trace.Samples);
        await h.WithDb(async db =>
        {
            db.BatteryChargeEvents.Add(new BatteryChargeEvent { SensorId = SensorId, StartedAt = T0.AddHours(74), EndedAt = T0.AddHours(96), PeakVoltage = 14, RestingAtTime = 12, DurationMinutes = 60 });
            return await db.SaveChangesAsync(Ct);
        });

        await h.Analyze();

        var e = Assert.Single(await h.Events());
        Assert.Equal(T0.AddHours(72), e.StartedAt);
    }

    [Fact]
    public async Task Run_StoredRowsAfterFinal_AreUpdatedInPlace()
    {
        var trace = ChargesWithRest((24, 12.7f));
        var h = new Harness(T0.AddHours(72 + 24 + 48));
        await h.Seed(trace.Samples);
        await h.Analyze();
        var row = Assert.Single(await h.Events());

        await h.WithDb(async db =>
        {
            var stored = await db.BatteryChargeEvents.SingleAsync(Ct);
            stored.PeakVoltage = 1f;
            stored.IsFull = false;
            return await db.SaveChangesAsync(Ct);
        });
        await h.Analyze();

        var updated = Assert.Single(await h.Events());
        Assert.Equal(row.Id, updated.Id);
        Assert.Equal(Comparable(row), Comparable(updated));
    }

    // A new row needs a new reading.

    private static async Task AddReading(Harness h, DateTime at, string value)
    {
        await h.WithDb(async db =>
        {
            db.SensorData.Add(new SensorData { SensorId = SensorId, Value = value, Timestamp = at });
            return await db.SaveChangesAsync(Ct);
        });
    }

    [Fact]
    public async Task NoReadingSinceTheLatestRow_AddsNoRowOnEveryPath()
    {
        var trace = ChargesWithRest((24, 12.7f));
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await h.Analyze();
        Assert.Single(await h.Health());

        h.Clock.Now = h.Clock.Now.AddHours(2);
        await h.Analyze();
        await h.Analyzer.SweepStaleSensorsAsync(Ct);
        await h.Reanalyze();

        Assert.Single(await h.Health());
    }

    [Fact]
    public async Task NoReadingSinceTheLatestRow_ChargeReplayStillRuns()
    {
        var trace = ChargesWithRest((24, 12.7f));
        var h = new Harness(T0.AddHours(72 + 24 + 48));
        await h.Seed(trace.Samples.Where(s => s.Timestamp <= T0.AddHours(72 + 24 + 47)));
        await h.Analyze();
        await h.WithDb(async db =>
        {
            (await db.BatteryChargeEvents.SingleAsync(Ct)).PeakVoltage = 1f;
            return await db.SaveChangesAsync(Ct);
        });

        await h.Analyze();

        Assert.Equal(14.4f, Assert.Single(await h.Events()).PeakVoltage);
        Assert.Single(await h.Health());
    }

    [Fact]
    public async Task ReadingAtTheTimeOfTheLatestRow_IsNotNew()
    {
        var trace = ChargesWithRest((24, 12.7f));
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await h.Analyze();
        await AddReading(h, trace.Samples[^1].Timestamp, "12.6");

        h.Clock.Now = h.Clock.Now.AddHours(1);
        await h.Analyze();

        Assert.Single(await h.Health());
    }

    [Fact]
    public async Task InvalidReadingSinceTheLatestRow_IsNotNew()
    {
        var trace = ChargesWithRest((24, 12.7f));
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await h.Analyze();
        await AddReading(h, h.Clock.Now.AddMinutes(1), "NaN");
        await AddReading(h, h.Clock.Now.AddMinutes(2), "1.1");

        h.Clock.Now = h.Clock.Now.AddHours(1);
        await h.Analyze();

        Assert.Single(await h.Health());
    }

    [Fact]
    public async Task ReadingSinceTheLatestRow_AddsARowOnEveryPath()
    {
        var trace = ChargesWithRest((24, 12.7f));
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await h.Analyze();

        await AddReading(h, h.Clock.Now.AddMinutes(1), "12.6");
        h.Clock.Now = h.Clock.Now.AddMinutes(2);
        await h.Analyze();
        Assert.Equal(2, (await h.Health()).Count);

        await AddReading(h, h.Clock.Now.AddMinutes(1), "12.5");
        h.Clock.Now = h.Clock.Now.AddMinutes(2);
        await h.Reanalyze();
        Assert.Equal(3, (await h.Health()).Count);

        await AddReading(h, h.Clock.Now.AddMinutes(1), "12.4");
        h.Clock.Now = h.Clock.Now.AddHours(2);
        await h.Analyzer.SweepStaleSensorsAsync(Ct);
        Assert.Equal(4, (await h.Health()).Count);
        Assert.Equal(12.4f, (await h.LatestHealth()).CurrentVoltage);
    }

    [Fact]
    public async Task ReadingOlderThanTheNewestRow_IsNotNew()
    {
        // The reading is newer than the first row but older than the second.
        var trace = ChargesWithRest((24, 12.7f));
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await h.Analyze();
        var first = (await h.LatestHealth()).Timestamp;
        await AddReading(h, h.Clock.Now.AddMinutes(1), "12.6");
        h.Clock.Now = h.Clock.Now.AddMinutes(2);
        await h.Analyze();
        Assert.Equal(2, (await h.Health()).Count);

        await AddReading(h, first.AddMinutes(1), "12.5");
        h.Clock.Now = h.Clock.Now.AddMinutes(30);
        await h.Analyze();

        Assert.Equal(2, (await h.Health()).Count);
    }

    [Fact]
    public async Task RowOfAnotherSensor_DoesNotHoldBackANewRow()
    {
        var trace = ChargesWithRest((24, 12.7f));
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await h.WithDb(async db =>
        {
            db.BatteryHealthData.Add(new BatteryHealth { SensorId = 2, Timestamp = h.Clock.Now.AddDays(1) });
            return await db.SaveChangesAsync(Ct);
        });

        await h.Analyze();

        Assert.Single(await h.Health(), row => row.SensorId == SensorId);
    }

    [Fact]
    public async Task Row_IsTimedByItsReadingNotByTheRun()
    {
        var trace = ChargesWithRest((24, 12.7f));
        var h = new Harness(trace.Samples[^1].Timestamp.AddHours(10));
        await h.Seed(trace.Samples);

        await h.Analyze();

        Assert.Equal(trace.Samples[^1].Timestamp, Assert.Single(await h.Health()).Timestamp);
    }

    [Fact]
    public async Task Sweep_QuietSensor_DoesNotReplay()
    {
        var trace = new BatteryTrace().Hold(72, 12.4f).Hold(24, 14.4f).Hold(48, 12.7f);
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await h.Analyze();
        await h.WithDb(async db =>
        {
            (await db.BatteryChargeEvents.SingleAsync(Ct)).PeakVoltage = 1f;
            return await db.SaveChangesAsync(Ct);
        });

        h.Clock.Now = h.Clock.Now.AddHours(2);
        await h.Analyzer.SweepStaleSensorsAsync(Ct);

        Assert.Equal(1f, Assert.Single(await h.Events()).PeakVoltage);
        Assert.Single(await h.Health());
    }

    [Fact]
    public async Task Sweep_ReadingOfAnotherSensor_DoesNotCountAsNew()
    {
        var trace = new BatteryTrace().Hold(72, 12.4f).Hold(24, 14.4f).Hold(48, 12.7f);
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await h.Analyze();
        await h.WithDb(async db =>
        {
            (await db.BatteryChargeEvents.SingleAsync(Ct)).PeakVoltage = 1f;
            db.SensorData.Add(new SensorData { SensorId = 2, Value = "12.5", Timestamp = h.Clock.Now });
            return await db.SaveChangesAsync(Ct);
        });

        h.Clock.Now = h.Clock.Now.AddHours(2);
        await h.Analyzer.SweepStaleSensorsAsync(Ct);

        Assert.Equal(1f, Assert.Single(await h.Events()).PeakVoltage);
    }

    [Fact]
    public async Task Sweep_NoRowYet_GetsARow()
    {
        var trace = new BatteryTrace().Hold(10, 12.4f);
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);

        await h.Analyzer.SweepStaleSensorsAsync(Ct);

        Assert.Single(await h.Health());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadingBeforeAClaim_NoRunBetween_NeverReachesTheNewOwner(bool bySweep)
    {
        var trace = new BatteryTrace().Hold(10, 12.5f);
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await h.Analyze();
        var firstRow = (await h.LatestHealth()).Timestamp;

        // A reading arrives, no run happens, then the claim, and the sensor stays quiet.
        var preClaim = firstRow.AddMinutes(10);
        await AddReading(h, preClaim, "11.11");
        var claimed = preClaim.AddMinutes(1);
        await Own(h, "buyer", (claimed - T0).TotalHours);
        h.Clock.Now = claimed.AddHours(2);
        if (bySweep) await h.Analyzer.SweepStaleSensorsAsync(Ct);
        else await h.Reanalyze();

        // The run holds that reading in a row timed before the claim, for an admin only.
        var admin = await h.LatestHealth();
        Assert.Equal(11.11f, admin.CurrentVoltage);
        Assert.Equal(preClaim, admin.Timestamp);
        Assert.Null(await h.View("buyer"));

        await AddReading(h, h.Clock.Now, "12.65");
        h.Clock.Now = h.Clock.Now.AddMinutes(1);
        await h.Analyze();

        Assert.Equal(12.65f, (await h.View("buyer"))!.CurrentVoltage);
    }

    [Fact]
    public async Task RowsWithTheSameTimestamp_LatestIsTheNewestRow()
    {
        // Rows moved to their reading time by the migration can share a time.
        var at = T0.AddDays(1);
        var h = new Harness(at.AddHours(1));
        await h.Seed(Array.Empty<Sample>());
        await h.WithDb(async db =>
        {
            db.BatteryHealthData.Add(new BatteryHealth { SensorId = SensorId, CurrentVoltage = 12.1f, Timestamp = at });
            await db.SaveChangesAsync(Ct);
            db.BatteryHealthData.Add(new BatteryHealth { SensorId = SensorId, CurrentVoltage = 12.2f, Timestamp = at });
            return await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(12.2f, (await h.LatestHealth()).CurrentVoltage);
    }

    [Fact]
    public async Task ClaimWhileTheSensorIsQuiet_HasNoRowUntilANewReadingArrives()
    {
        var trace = ChargesWithRest((24, 12.7f));
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await h.Analyze();

        var claimed = h.Clock.Now.AddHours(1);
        await Own(h, "buyer", (claimed - T0).TotalHours);
        h.Clock.Now = claimed.AddHours(3);
        await h.Analyzer.SweepStaleSensorsAsync(Ct);
        await h.Analyze();
        await h.Reanalyze();
        Assert.Null(await h.View("buyer"));

        await AddReading(h, h.Clock.Now.AddMinutes(1), "12.65");
        h.Clock.Now = h.Clock.Now.AddMinutes(2);
        await h.Analyze();

        var row = (await h.View("buyer"))!;
        Assert.Equal(12.65f, row.CurrentVoltage);
        Assert.Equal("learning", row.Status);
    }

    // Stored self-discharge.

    [Fact]
    public async Task Run_LateRunFillsSelfDischargeOnTheFinalCharge()
    {
        var trace = new BatteryTrace().Hold(72, 12.4f).Hold(24, 14.4f).Ramp(600, 13f, 12.8f);
        var charge = T0.AddHours(72 + 24);
        var h = new Harness(charge.AddDays(6));
        await h.Seed(trace.Samples);
        await h.Analyze();
        Assert.Null(Assert.Single(await h.Events()).SelfDischargePctPerWeek);

        h.Clock.Now = NowAfter(trace);
        await h.Analyze();

        Assert.NotNull(Assert.Single(await h.Events()).SelfDischargePctPerWeek);
    }

    [Fact]
    public async Task Run_PartialFinalCharge_KeepsNoSelfDischarge()
    {
        var trace = new BatteryTrace().Hold(72, 12.4f).Hold(6, 14.4f).Ramp(600, 13f, 12.8f);
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await h.Analyze();
        await h.Analyze();

        var e = Assert.Single(await h.Events());
        Assert.False(e.IsFull);
        Assert.Null(e.SelfDischargePctPerWeek);
    }

    [Fact]
    public async Task ChargeRowsFromBeforeTheColumn_FillOnTheNextRunAndOnARebuild()
    {
        // Two final charges with no stored value, as rows from before the column are.
        var trace = new BatteryTrace().Hold(72, 12.4f).Hold(24, 14.4f).Ramp(600, 13f, 12.8f).Hold(24, 14.4f).Ramp(600, 13f, 12.8f);
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await h.Reanalyze();
        await h.WithDb(async db =>
        {
            foreach (var e in db.BatteryChargeEvents) e.SelfDischargePctPerWeek = null;
            return await db.SaveChangesAsync(Ct);
        });

        await h.Analyze();
        var afterRun = await h.Events();
        Assert.Null(afterRun[0].SelfDischargePctPerWeek);
        Assert.NotNull(afterRun[1].SelfDischargePctPerWeek);

        await h.Reanalyze();
        var afterRebuild = await h.Events();
        Assert.NotNull(afterRebuild[0].SelfDischargePctPerWeek);
        Assert.NotNull(afterRebuild[1].SelfDischargePctPerWeek);
    }

    // Health for each viewer.

    private static async Task Own(Harness h, string userId, double fromHours, double? toHours = null, bool isOwner = true)
    {
        await h.WithDb(async db =>
        {
            db.SensorOwnershipPeriods.Add(new SensorOwnershipPeriod
            {
                UserId = userId,
                SensorId = SensorId,
                StartedAt = T0.AddHours(fromHours),
                EndedAt = toHours == null ? null : T0.AddHours(toHours.Value),
            });
            if (toHours == null) db.UserSensors.Add(new UserSensor { UserId = userId, SensorId = SensorId, IsOwner = isOwner });
            return await db.SaveChangesAsync(Ct);
        });
    }

    // Charges at hours 72, 696, 830, 1470 and 1694. The middle one is low and has its own long rest.
    // A viewer who owned the first era and the last one misses the charge in between.
    private static BatteryTrace ThreeEras() => new BatteryTrace().Hold(72, 12.4f)
        .Hold(24, 14.4f).Ramp(600, 13f, 12.8f)
        .Hold(24, 14.3f).Hold(110, 12.9f)
        .Hold(24, 14.5f).Ramp(600, 11.6f, 11.4f).Hold(16, 11.4f)
        .Hold(24, 14.4f).Hold(200, 12.9f)
        .Hold(24, 14.6f).Hold(200, 12.9f);

    private const double EraOneEnd = 825;
    private const double EraThreeStart = 1460;

    private static async Task<Harness> ThreeErasWithGapViewer()
    {
        var trace = ThreeEras();
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await Own(h, "gap", 0, EraOneEnd);
        await Own(h, "gap", EraThreeStart);
        await h.Analyze();
        return h;
    }

    [Fact]
    public async Task GapViewer_SeesOnlyChargesFromTheirTwoPeriods()
    {
        var h = await ThreeErasWithGapViewer();

        Assert.Equal(5, (await h.EventsFor(null, isAdmin: true)).Count);
        var mine = await h.EventsFor("gap");
        Assert.Equal(new[] { 1694.0, 1470.0, 696.0, 72.0 }, mine.Select(e => (e.StartedAt - T0).TotalHours));
    }

    [Fact]
    public async Task GapViewer_StatusSlopeLastFullAndSelfDischargeLeaveOutTheGap()
    {
        var h = await ThreeErasWithGapViewer();
        var events = await h.Events();
        var admin = await h.LatestHealth();
        var gap = (await h.View("gap"))!;

        // The low settled voltage from the gap pulls the slope down for the admin only.
        Assert.Equal("good", gap.Status);
        Assert.NotEqual(admin.DailyDropPctPerWeek, gap.DailyDropPctPerWeek);
        Assert.Equal(events[^1].EndedAt, gap.LastFullChargeAt);
        Assert.Equal(14.6f, gap.LastFullChargePeak);
        Assert.Equal(2, gap.FullChargesLast30d);

        // The latest rest with a value is in the gap for the admin and in the first era for this viewer.
        Assert.Equal(events[2].SelfDischargePctPerWeek, admin.SelfDischargePctPerWeek);
        Assert.Equal(events[0].SelfDischargePctPerWeek, gap.SelfDischargePctPerWeek);
        Assert.NotEqual(admin.SelfDischargePctPerWeek, gap.SelfDischargePctPerWeek);
    }

    [Fact]
    public async Task GapViewer_LastFullChargeComesFromTheirEarlierPeriodWhenTheLaterOneHasNone()
    {
        var trace = ThreeEras();
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await Own(h, "gap", 0, EraOneEnd);
        await Own(h, "gap", 1900);
        await h.Analyze();

        var events = await h.Events();
        var gap = (await h.View("gap"))!;
        Assert.Equal(events[1].EndedAt, gap.LastFullChargeAt);
        Assert.Equal(14.3f, gap.LastFullChargePeak);
    }

    [Fact]
    public async Task TwoOwners_GetDifferentResultsFromOneRow()
    {
        var trace = ChargesWithRest((24, 12.9f), (24, 12.9f));
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await Own(h, "first", -1000);
        await Own(h, "second", (trace.Samples[^1].Timestamp - T0).TotalHours - 1);

        await h.Analyze();

        Assert.Single(await h.Health());
        var first = (await h.View("first"))!;
        var second = (await h.View("second"))!;
        Assert.Equal("good", first.Status);
        Assert.Equal(2, first.FullChargesLast30d);
        Assert.Equal("learning", second.Status);
        Assert.Null(second.LastFullChargeAt);
        Assert.Null(second.LastFullChargePeak);
        Assert.Equal(0, second.FullChargesLast30d);
        Assert.Equal(first.CurrentVoltage, second.CurrentVoltage);
        Assert.Equal(first.Timestamp, second.Timestamp);
    }

    [Fact]
    public async Task ShareRecipient_FollowsTheirOwnPeriod()
    {
        var trace = ChargesWithRest((24, 12.9f), (24, 12.9f));
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await Own(h, "owner", -1000);
        await Own(h, "viewer", (trace.Samples[^1].Timestamp - T0).TotalHours - 1, isOwner: false);
        await h.Analyze();

        Assert.Equal("good", (await h.View("owner"))!.Status);
        Assert.Equal("learning", (await h.View("viewer"))!.Status);
        Assert.Empty(await h.EventsFor("viewer"));
    }

    [Fact]
    public async Task Seller_KeepsTheirPeriodAtReadLevel_BuyerSeesOnlyTheirs()
    {
        var trace = ChargesWithRest((24, 12.9f), (24, 12.9f));
        var sold = 72 + 24 + 200 + 24 + 100.5;
        var h = new Harness(T0.AddHours(sold - 10));
        await h.Seed(trace.Samples);
        await h.Analyze();
        await Own(h, "seller", -1000, sold);
        await Own(h, "buyer", sold);
        h.Clock.Now = NowAfter(trace);
        await h.Analyze();

        var seller = (await h.View("seller"))!;
        Assert.Equal("good", seller.Status);
        Assert.Equal(2, seller.FullChargesLast30d);
        Assert.True(seller.Timestamp < T0.AddHours(sold));
        Assert.Equal(2, (await h.EventsFor("seller")).Count);

        var buyer = (await h.View("buyer"))!;
        Assert.Equal("learning", buyer.Status);
        Assert.Empty(await h.EventsFor("buyer"));
    }

    [Fact]
    public async Task ChargeCrossingABoundary_CountsForNeitherSide()
    {
        // The charge runs from hour 72 to hour 96, and the sensor changes hands at hour 80.
        var trace = ChargesWithRest((24, 12.9f));
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await Own(h, "seller", -1000, 80);
        await Own(h, "buyer", 80);
        await h.Analyze();

        Assert.Empty(await h.EventsFor("seller"));
        Assert.Empty(await h.EventsFor("buyer"));
        Assert.Null((await h.View("buyer"))!.LastFullChargeAt);
        Assert.Single(await h.EventsFor(null, isAdmin: true));
    }

    [Fact]
    public async Task SettledWindowCrossingABoundary_ChargeCountsButNotAsAnAnchor()
    {
        // The second charge ends at hour 320, and the period ends 10 hours later.
        var trace = ChargesWithRest((24, 12.9f), (24, 12.9f));
        var h = new Harness(T0.AddHours(330));
        await h.Seed(trace.Samples);
        await Own(h, "seller", -1000, 330);
        h.Clock.Now = NowAfter(trace);
        await h.Analyze();
        await h.WithDb(async db =>
        {
            db.BatteryHealthData.Add(new BatteryHealth { SensorId = SensorId, Timestamp = T0.AddHours(329) });
            return await db.SaveChangesAsync(Ct);
        });

        var seller = (await h.View("seller"))!;
        Assert.Equal(T0.AddHours(320), seller.LastFullChargeAt);
        Assert.Equal("learning", seller.Status);
        var events = await h.EventsFor("seller");
        Assert.Null(events[0].SettledVoltage);
        Assert.NotNull(events[1].SettledVoltage);
    }

    [Fact]
    public async Task RestWindowCrossingABoundary_SelfDischargeDoesNotCount()
    {
        // The rest is long enough, but the period ends on day 10 of it.
        var trace = new BatteryTrace().Hold(72, 12.4f).Hold(24, 14.4f).Ramp(600, 13f, 12.8f);
        var end = 96 + 36 + 240;
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await h.Analyze();
        await Own(h, "seller", -1000, end);
        await h.WithDb(async db =>
        {
            db.BatteryHealthData.Add(new BatteryHealth { SensorId = SensorId, Timestamp = T0.AddHours(end - 1) });
            return await db.SaveChangesAsync(Ct);
        });

        Assert.NotNull((await h.LatestHealth()).SelfDischargePctPerWeek);
        var seller = (await h.View("seller"))!;
        Assert.NotNull(seller.LastFullChargeAt);
        Assert.Null(seller.SelfDischargePctPerWeek);
        var e = Assert.Single(await h.EventsFor("seller"));
        Assert.Null(e.SelfDischargePctPerWeek);
        Assert.NotNull(e.SettledVoltage);
    }

    [Fact]
    public async Task RestingAndMinimum_NearAPeriodStart_AreNullForThatViewerOnly()
    {
        var trace = new BatteryTrace().Hold(200, 12.5f);
        var last = (trace.Samples[^1].Timestamp - T0).TotalHours;
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await Own(h, "old", -1000);
        await Own(h, "twoDays", last - 48);
        await Own(h, "fourHours", last - 4);
        await h.Analyze();

        var old = (await h.View("old"))!;
        Assert.Equal(12.5f, old.RestingMedian);
        Assert.Equal(12.5f, old.VoltageMin24h);

        var twoDays = (await h.View("twoDays"))!;
        Assert.Null(twoDays.RestingMedian);
        Assert.Equal(12.5f, twoDays.VoltageMin24h);

        var fourHours = (await h.View("fourHours"))!;
        Assert.Null(fourHours.RestingMedian);
        Assert.Null(fourHours.VoltageMin24h);
        Assert.Equal(12.5f, fourHours.CurrentVoltage);
        Assert.False(fourHours.OnChargerNow);

        var admin = await h.LatestHealth();
        Assert.Equal(12.5f, admin.RestingMedian);
        Assert.Equal(12.5f, admin.VoltageMin24h);
    }

    [Fact]
    public async Task EventsBaseline_NearAPeriodStart_IsNullForThatViewerOnly()
    {
        var trace = ChargesWithRest((24, 12.9f));
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await h.Analyze();
        await Own(h, "old", -1000);
        await Own(h, "near", 70);

        Assert.Equal(12.4f, Assert.Single(await h.EventsFor("old")).RestingAtTime);
        Assert.Null(Assert.Single(await h.EventsFor("near")).RestingAtTime);
        Assert.Equal(12.4f, Assert.Single(await h.EventsFor(null, isAdmin: true)).RestingAtTime);
    }

    [Fact]
    public async Task Viewer_LatestRowComesFromTheirPeriods()
    {
        var trace = new BatteryTrace().Hold(100, 12.5f);
        var h = new Harness(T0.AddHours(50));
        await h.Seed(trace.Samples);
        await h.Analyze();
        h.Clock.Now = NowAfter(trace);
        await h.Analyze();
        await Own(h, "early", -1000, 60);
        await Own(h, "none", 200);

        Assert.Equal(T0.AddHours(50), (await h.View("early"))!.Timestamp);
        Assert.Null(await h.View("none"));
    }

    [Fact]
    public async Task Viewer_PeriodOnAnotherSensorOrOfAnotherUser_DoesNotCount()
    {
        var trace = ChargesWithRest((24, 12.9f), (24, 12.9f));
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await Own(h, "viewer", (trace.Samples[^1].Timestamp - T0).TotalHours - 1);
        await h.WithDb(async db =>
        {
            db.SensorOwnershipPeriods.Add(new SensorOwnershipPeriod { UserId = "viewer", SensorId = 2, StartedAt = T0.AddDays(-100) });
            db.SensorOwnershipPeriods.Add(new SensorOwnershipPeriod { UserId = "other", SensorId = SensorId, StartedAt = T0.AddDays(-100) });
            return await db.SaveChangesAsync(Ct);
        });
        await h.Analyze();

        Assert.Equal("learning", (await h.View("viewer"))!.Status);
        Assert.Empty(await h.EventsFor("viewer"));
        var row = (await h.View("viewer"))!;
        Assert.Null(row.RestingMedian);
    }

    [Fact]
    public async Task Reader_LeavesOutRowsAndChargesOfAnotherSensor()
    {
        var trace = new BatteryTrace().Hold(72, 12.4f).Hold(24, 14.4f).Hold(100, 12.7f);
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await h.Analyze();
        await h.WithDb(async db =>
        {
            db.BatteryHealthData.Add(new BatteryHealth { SensorId = 2, CurrentVoltage = 9f, Timestamp = h.Clock.Now.AddMinutes(1) });
            db.BatteryChargeEvents.Add(new BatteryChargeEvent { SensorId = 2, StartedAt = T0.AddHours(120), EndedAt = T0.AddHours(130), PeakVoltage = 15f, IsFull = true });
            return await db.SaveChangesAsync(Ct);
        });

        var row = await h.LatestHealth();
        Assert.Equal(12.7f, row.CurrentVoltage);
        Assert.Equal(14.4f, row.LastFullChargePeak);
        Assert.Single(await h.EventsFor(null, isAdmin: true));
    }

    [Fact]
    public async Task EventsFor_SinceIncludesAChargeStartingAtThatTime()
    {
        var trace = ChargesWithRest((24, 12.9f), (24, 12.9f));
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await h.Analyze();
        var second = (await h.Events())[1].StartedAt;

        var since = await h.WithDb(db => new BatteryHealthReadService(db, RealMapper, h.Clock).GetEventsAsync(SensorId, null, true, second, Ct));

        Assert.Equal(second, Assert.Single(since).StartedAt);
    }

    [Fact]
    public async Task EventsFor_SinceLeavesOutEarlierCharges()
    {
        var trace = ChargesWithRest((24, 12.9f), (24, 12.9f));
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);
        await h.Analyze();

        var since = await h.WithDb(db => new BatteryHealthReadService(db, RealMapper, h.Clock).GetEventsAsync(SensorId, null, true, T0.AddHours(100), Ct));

        Assert.Single(since);
    }

    // Status from stored history.

    [Fact]
    public async Task Status_ComesFromSettledFullChargesOnly()
    {
        // The latest charge is partial and settled low, which must not affect status.
        var trace = new BatteryTrace().Hold(72, 12.4f).Hold(24, 14.4f).Hold(200, 12.8f).Hold(24, 14.4f).Hold(200, 12.8f).Hold(6, 14.4f).Hold(200, 11.9f);
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);

        await h.Analyze();

        var row = await h.LatestHealth();
        Assert.Equal(3, (await h.Events()).Count);
        Assert.Equal("good", row.Status);
    }

    [Fact]
    public async Task Status_DropAcrossStoredCharges_IsAttention()
    {
        // 3 % below the earlier settled voltage, with the anchors spread far apart in time.
        var trace = new BatteryTrace().Hold(72, 12.4f).Hold(24, 14.4f).Hold(10000, 13f).Hold(24, 14.4f).Hold(100, 12.61f);
        var h = new Harness(NowAfter(trace));
        await h.Seed(trace.Samples);

        await h.Analyze();

        var row = await h.LatestHealth();
        Assert.Equal("attention", row.Status);
        Assert.Null(row.DailyDropPctPerWeek);
    }
}
