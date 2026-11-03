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

public class BatteryHealthAnalyzerServiceTests
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

        public async Task<BatteryHealth> LatestHealth() => (await Health())[^1];

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
        new { e.SensorId, e.StartedAt, e.EndedAt, e.PeakVoltage, e.RestingAtTime, e.DurationMinutes, e.IsFull, e.SettledVoltage };

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

        var row = Assert.Single(await h.Health());
        Assert.Equal(h.Clock.Now, row.Timestamp);
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
    public async Task Analyze_InvalidReading_IsDropped(string bad)
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

        h.Clock.Now = h.Clock.Now.AddMinutes(90);
        await h.Analyzer.SweepStaleSensorsAsync(Ct);
        Assert.Single(await h.Health());

        h.Clock.Now = h.Clock.Now.AddMinutes(1);
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
    public async Task Reanalyze_OfflineSensor_RebuildsEventsButAddsNoRow()
    {
        var trace = ChargesWithRest((24, 12.7f));
        var h = new Harness(trace.Samples[^1].Timestamp.AddDays(30));
        await h.Seed(trace.Samples);

        await h.Reanalyze();

        Assert.Single(await h.Events());
        Assert.Empty(await h.Health());
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
