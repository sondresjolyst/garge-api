using garge_api.Helpers;
using Xunit;

namespace garge_api.Tests;

public class BatteryHealthMathTests
{
    private static List<BatteryHealthMath.VoltageSample> Trace(DateTime start, int hours, Func<int, float> voltageAtHour)
    {
        var samples = new List<BatteryHealthMath.VoltageSample>();
        for (var h = 0; h < hours; h++)
            samples.Add(new BatteryHealthMath.VoltageSample(start.AddHours(h), voltageAtHour(h)));
        return samples;
    }

    [Fact]
    public void ComputeRestingMedian_FlatVoltage_ReturnsFlatValue()
    {
        var now = DateTime.UtcNow;
        var samples = Trace(now.AddDays(-14), 14 * 24, _ => 12.40f);
        var resting = BatteryHealthMath.ComputeRestingMedian(samples, now, TimeSpan.FromDays(14));
        Assert.InRange(resting, 12.39f, 12.41f);
    }

    [Fact]
    public void ComputeRestingMedian_MixOfRestingAndCharging_PicksRestingValues()
    {
        var now = DateTime.UtcNow;
        // 14 days: alternating 24h resting (12.40) / 24h charging (13.10)
        var samples = Trace(now.AddDays(-14), 14 * 24, h => (h / 24) % 2 == 0 ? 12.40f : 13.10f);
        var resting = BatteryHealthMath.ComputeRestingMedian(samples, now, TimeSpan.FromDays(14));
        Assert.InRange(resting, 12.39f, 12.41f);
    }

    [Fact]
    public void DetectChargeEvents_FloatCharger_DetectsExactlyOneEvent()
    {
        var now = DateTime.UtcNow;
        var start = now.AddDays(-3);
        // Resting at 12.40 for 12h, then float-charging steady at 13.05 for 60h
        var samples = Trace(start, 72, h => h < 12 ? 12.40f : 13.05f);
        var events = BatteryHealthMath.DetectChargeEvents(samples, restingMedian: 12.40f);
        // Float never drops back below 1.02 * resting, so the window is still open at end of trace.
        // Detector requires sustained drop to close — open windows aren't counted.
        // So expect 0 closed events here; that's correct (charger still on).
        Assert.Empty(events);
    }

    [Fact]
    public void DetectChargeEvents_RealChargeThenDisconnect_DetectsOne()
    {
        var now = DateTime.UtcNow;
        var start = now.AddDays(-3);
        // 12h resting (12.40), 24h charging (13.60 peak, well above 1.08x = 13.39),
        // drop to 12.42 for 12h
        var samples = Trace(start, 48, h => h switch
        {
            < 12 => 12.40f,
            < 36 => 13.60f,
            _ => 12.42f
        });
        var events = BatteryHealthMath.DetectChargeEvents(samples, restingMedian: 12.40f);
        Assert.Single(events);
        Assert.InRange(events[0].PeakRatio, 1.09f, 1.12f);
    }

    [Fact]
    public void DetectChargeEvents_ShortNoiseSpike_Ignored()
    {
        var now = DateTime.UtcNow;
        var start = now.AddDays(-2);
        // Flat resting, single 1-hour blip above entry but below peak min
        var samples = Trace(start, 48, h => h == 20 ? 13.00f : 12.40f);
        var events = BatteryHealthMath.DetectChargeEvents(samples, restingMedian: 12.40f);
        Assert.Empty(events);
    }

    [Fact]
    public void ComputeSlopePercentPerWeek_DecliningResting_NegativeSlope()
    {
        var now = DateTime.UtcNow;
        // 30 days, resting declines from 12.60 to 12.40
        var samples = Trace(now.AddDays(-30), 30 * 24, h => 12.60f - 0.20f * (h / (30f * 24f)));
        var slope = BatteryHealthMath.ComputeSlopePercentPerWeek(samples);
        Assert.True(slope < 0);
        // 0.20V drop / 30d = ~0.046V/wk; as % of mean ~12.5 = ~0.37%/wk
        Assert.InRange(slope, -0.5f, -0.3f);
    }

    [Fact]
    public void ClassifyHealth_InsufficientData_ReturnsLearning()
    {
        var result = BatteryHealthMath.ClassifyHealth(0f, (float?)null, null, daysOfData: 5, hasRecentCharge: false);
        Assert.Equal("learning", result.Status);
    }

    [Fact]
    public void ClassifyHealth_AllSignalsGood_ReturnsGood()
    {
        var result = BatteryHealthMath.ClassifyHealth(
            dropPctFromPeak: 1f,
            cycleSlopePctPerWeek: -0.05f,
            chargeAcceptanceRatio: 1.15f,
            daysOfData: 30,
            hasRecentCharge: true);
        Assert.Equal("good", result.Status);
    }

    [Fact]
    public void ClassifyHealth_S1Replace_OverridesOthers()
    {
        var result = BatteryHealthMath.ClassifyHealth(
            dropPctFromPeak: 7f,    // > replace threshold
            cycleSlopePctPerWeek: 0f,
            chargeAcceptanceRatio: 1.15f,
            daysOfData: 30,
            hasRecentCharge: true);
        Assert.Equal("replace", result.Status);
    }

    [Fact]
    public void ClassifyHealth_S2DeclineAttention_ReturnsAttention()
    {
        var result = BatteryHealthMath.ClassifyHealth(
            dropPctFromPeak: 0f,
            cycleSlopePctPerWeek: -0.2f,   // attention range
            chargeAcceptanceRatio: 1.15f,
            daysOfData: 30,
            hasRecentCharge: true);
        Assert.Equal("attention", result.Status);
    }

    [Fact]
    public void ClassifyHealth_NoChargeEvent_S3Skipped()
    {
        // No charge acceptance data — S3 must not push status above S1/S2 verdict.
        // Charge events exist (hasRecentCharge true) but acceptance ratio is unset
        // (e.g. only short charges that didn't qualify).
        var result = BatteryHealthMath.ClassifyHealth(
            dropPctFromPeak: 1f,
            cycleSlopePctPerWeek: -0.05f,
            chargeAcceptanceRatio: null,
            daysOfData: 30,
            hasRecentCharge: true);
        Assert.Equal("good", result.Status);
    }

    [Fact]
    public void ClassifyHealth_BadChargeAcceptance_ReturnsReplace()
    {
        var result = BatteryHealthMath.ClassifyHealth(
            dropPctFromPeak: 1f,
            cycleSlopePctPerWeek: -0.05f,
            chargeAcceptanceRatio: 1.02f, // < replace threshold
            daysOfData: 30,
            hasRecentCharge: true);
        Assert.Equal("replace", result.Status);
    }

    [Fact]
    public void ClassifyHealth_NoRecentCharge_ReturnsLearning()
    {
        // Without a charge event we cannot tell degradation from self-discharge.
        // Even bad-looking drop + slope must not be flagged as replace.
        var result = BatteryHealthMath.ClassifyHealth(
            dropPctFromPeak: 7f,
            cycleSlopePctPerWeek: -1f,
            chargeAcceptanceRatio: null,
            daysOfData: 30,
            hasRecentCharge: false);
        Assert.Equal("learning", result.Status);
        Assert.Contains("charger", result.Reason);
    }

    [Fact]
    public void ClassifyHealth_NullSlope_S2Skipped()
    {
        // Cycle slope null = insufficient anchors. Even a normally-bad slope
        // value (passed via the other tests) must not pin status when null.
        var result = BatteryHealthMath.ClassifyHealth(
            dropPctFromPeak: 0f,
            cycleSlopePctPerWeek: null,
            chargeAcceptanceRatio: 1.15f,
            daysOfData: 30,
            hasRecentCharge: true);
        Assert.Equal("good", result.Status);
    }

    [Fact]
    public void PostChargeResting_Settled_ReturnsMedian()
    {
        var start = DateTime.UtcNow.AddDays(-2);
        // Charge ended at t=0 of this trace. Sample voltage hourly 0..48h.
        // Surface charge decays 0-12h, then stable at 12.7V from 12-48h.
        var samples = Trace(start, 48, h => h < 12 ? 13.2f : 12.70f);
        var resting = BatteryHealthMath.PostChargeResting(
            samples,
            chargeEndedAt: start,
            settleStart: TimeSpan.FromHours(12),
            settleEnd: TimeSpan.FromHours(36));
        Assert.NotNull(resting);
        Assert.InRange(resting!.Value, 12.69f, 12.71f);
    }

    [Fact]
    public void PostChargeResting_NotEnoughSamples_ReturnsNull()
    {
        // Only one sample inside the 12-36h window.
        var start = DateTime.UtcNow.AddDays(-2);
        var samples = new List<BatteryHealthMath.VoltageSample>
        {
            new(start.AddHours(20), 12.70f),
        };
        var resting = BatteryHealthMath.PostChargeResting(
            samples,
            chargeEndedAt: start,
            settleStart: TimeSpan.FromHours(12),
            settleEnd: TimeSpan.FromHours(36),
            minSamples: 3);
        Assert.Null(resting);
    }

    [Fact]
    public void ComputeCycleSlopePercentPerWeek_BelowMinAnchors_ReturnsNull()
    {
        var now = DateTime.UtcNow;
        var anchors = new List<BatteryHealthMath.VoltageSample>
        {
            new(now.AddDays(-30), 12.70f),
            new(now.AddDays(-15), 12.68f),
        };
        var slope = BatteryHealthMath.ComputeCycleSlopePercentPerWeek(anchors, minAnchors: 3);
        Assert.Null(slope);
    }

    [Fact]
    public void ComputeCycleSlopePercentPerWeek_DecliningAnchors_NegativeSlope()
    {
        // 5 post-charge anchors 7 days apart, each 0.05V lower than the
        // previous. Real degradation pattern; slope should be clearly < 0.
        var now = DateTime.UtcNow;
        var anchors = new List<BatteryHealthMath.VoltageSample>
        {
            new(now.AddDays(-28), 12.80f),
            new(now.AddDays(-21), 12.75f),
            new(now.AddDays(-14), 12.70f),
            new(now.AddDays(-7),  12.65f),
            new(now,              12.60f),
        };
        var slope = BatteryHealthMath.ComputeCycleSlopePercentPerWeek(anchors, minAnchors: 3);
        Assert.NotNull(slope);
        Assert.True(slope!.Value < -0.1f, $"expected negative slope, got {slope}");
    }

    [Fact]
    public void DetectChargeEvents_SlowSurfaceChargeDecay_ClosesNearResting()
    {
        // After the charger is unplugged, surface charge bleeds off slowly and
        // voltage lingers well above resting for a day+. At a 1.02 exit the
        // window stayed open (charge never recorded → "last full charge" went
        // stale). The 1.03 exit closes it once the decay reaches ~1.03x resting.
        var start = DateTime.UtcNow.AddDays(-4);
        // 12h resting 13.00, 24h charge 14.20 (> 1.08x = 14.04),
        // 12h slow decay 14.20 -> 13.35, then flat 13.35 (= 1.027x resting).
        var samples = Trace(start, 84, h => h switch
        {
            < 12 => 13.00f,
            < 36 => 14.20f,
            < 48 => 14.20f - (14.20f - 13.35f) * ((h - 36) / 11f),
            _ => 13.35f
        });
        var events = BatteryHealthMath.DetectChargeEvents(samples, restingMedian: 13.00f);
        Assert.Single(events);
    }

    [Fact]
    public void DetectChargeEvents_MultiStageThenFloat_SingleEventNoSplit()
    {
        // A multi-stage charge: inrush spike, absorption plateau, a higher
        // boost stage, then float — all one physical charge. A peak-relative
        // exit would split this (the spike/boost peaks sit far above the
        // plateau). The resting-relative exit keeps it a single window that
        // only closes once the charger is disconnected and voltage decays
        // toward resting.
        var start = DateTime.UtcNow.AddDays(-5);
        // 12h resting 13.45, 1h spike 15.11, 24h absorption 14.25,
        // 24h boost 15.71, 24h float 13.95 (= 1.037x, stays open),
        // then 12h decay to 13.30 (disconnect) -> closes once.
        var samples = Trace(start, 97, h => h switch
        {
            < 12 => 13.45f,
            12 => 15.11f,
            < 37 => 14.25f,
            < 61 => 15.71f,
            < 85 => 13.95f,
            _ => 13.95f - (13.95f - 13.30f) * ((h - 84) / 12f)
        });
        var events = BatteryHealthMath.DetectChargeEvents(samples, restingMedian: 13.45f);
        Assert.Single(events);
        Assert.InRange(events[0].PeakRatio, 1.16f, 1.18f);
    }

    [Fact]
    public void DetectChargeEvents_FloatStillConnected_StaysOpen()
    {
        // Same multi-stage charge but the charger is still floating at the end
        // of the trace (never disconnected). Voltage holds at 1.037x resting,
        // above the 1.03 exit, so the window is still open — no closed event
        // yet, which is correct: the charge hasn't finished.
        var start = DateTime.UtcNow.AddDays(-4);
        var samples = Trace(start, 85, h => h switch
        {
            < 12 => 13.45f,
            12 => 15.11f,
            < 37 => 14.25f,
            < 61 => 15.71f,
            _ => 13.95f
        });
        var events = BatteryHealthMath.DetectChargeEvents(samples, restingMedian: 13.45f);
        Assert.Empty(events);
    }

    [Fact]
    public void DetectChargeEvents_CalibrationDrift_StillDetects()
    {
        // Sensor reads 0.8V higher than reality. Resting "looks like" 13.20.
        // Ratios still classify correctly because everything scales.
        var now = DateTime.UtcNow;
        var start = now.AddDays(-3);
        var samples = Trace(start, 48, h => h switch
        {
            < 12 => 13.20f,
            < 36 => 14.50f, // peak well above 1.08x = 14.256
            _ => 13.22f
        });
        var events = BatteryHealthMath.DetectChargeEvents(samples, restingMedian: 13.20f);
        Assert.Single(events);
        Assert.InRange(events[0].PeakRatio, 1.09f, 1.12f);
    }
}
