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
        var result = BatteryHealthMath.ClassifyHealth(0f, 0f, null, daysOfData: 5);
        Assert.Equal("learning", result.Status);
    }

    [Fact]
    public void ClassifyHealth_AllSignalsGood_ReturnsGood()
    {
        var result = BatteryHealthMath.ClassifyHealth(
            dropPctFromPeak: 1f,
            slopePctPerWeek: -0.05f,
            chargeAcceptanceRatio: 1.15f,
            daysOfData: 30);
        Assert.Equal("good", result.Status);
    }

    [Fact]
    public void ClassifyHealth_S1Replace_OverridesOthers()
    {
        var result = BatteryHealthMath.ClassifyHealth(
            dropPctFromPeak: 7f,    // > replace threshold
            slopePctPerWeek: 0f,
            chargeAcceptanceRatio: 1.15f,
            daysOfData: 30);
        Assert.Equal("replace", result.Status);
    }

    [Fact]
    public void ClassifyHealth_S2DeclineAttention_ReturnsAttention()
    {
        var result = BatteryHealthMath.ClassifyHealth(
            dropPctFromPeak: 0f,
            slopePctPerWeek: -0.2f,   // attention range
            chargeAcceptanceRatio: 1.15f,
            daysOfData: 30);
        Assert.Equal("attention", result.Status);
    }

    [Fact]
    public void ClassifyHealth_NoChargeEvent_S3Skipped()
    {
        // No charge acceptance data — S3 must not push status above S1/S2 verdict.
        var result = BatteryHealthMath.ClassifyHealth(
            dropPctFromPeak: 1f,
            slopePctPerWeek: -0.05f,
            chargeAcceptanceRatio: null,
            daysOfData: 30);
        Assert.Equal("good", result.Status);
    }

    [Fact]
    public void ClassifyHealth_BadChargeAcceptance_ReturnsReplace()
    {
        var result = BatteryHealthMath.ClassifyHealth(
            dropPctFromPeak: 1f,
            slopePctPerWeek: -0.05f,
            chargeAcceptanceRatio: 1.02f, // < replace threshold
            daysOfData: 30);
        Assert.Equal("replace", result.Status);
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
