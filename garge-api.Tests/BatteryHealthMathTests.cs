using garge_api.Helpers;
using Xunit;
using Sample = garge_api.Helpers.BatteryHealthMath.VoltageSample;

namespace garge_api.Tests;

public class BatteryHealthMathTests
{
    private static readonly DateTime T0 = BatteryTrace.T0;

    private static IReadOnlyList<BatteryHealthMath.ChargeWindow> Detect(BatteryTrace trace) =>
        BatteryHealthMath.DetectCharges(trace.Samples).Charges;

    private static double HoursOf(DateTime t) => (t - T0).TotalHours;

    // 3 days at rest, then a day on the charger at 14.4 V.
    private static BatteryTrace RestThenCharge(float rest = 12.4f, int chargeHours = 24, float charge = 14.4f) =>
        new BatteryTrace().Hold(72, rest).Hold(chargeHours, charge);

    private record Expected(double Start, double? End, bool Full, float? Settled, float Peak, float Baseline);

    private static void AssertCharges(string fixture, params Expected[] expected)
    {
        var charges = BatteryHealthMath.DetectCharges(BatteryFixtures.Samples(fixture)).Charges;
        Assert.Equal(expected.Length, charges.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            var e = expected[i];
            var c = charges[i];
            Assert.InRange(HoursOf(c.StartedAt), e.Start - 0.01, e.Start + 0.01);
            if (e.End == null) Assert.Null(c.EndedAt);
            else Assert.InRange(HoursOf(c.EndedAt!.Value), e.End.Value - 0.01, e.End.Value + 0.01);
            Assert.Equal(e.Full, c.IsFull);
            if (e.Settled == null) Assert.Null(c.SettledVoltage);
            else Assert.InRange(c.SettledVoltage!.Value, e.Settled.Value - 0.002f, e.Settled.Value + 0.002f);
            Assert.InRange(c.PeakVoltage, e.Peak - 0.002f, e.Peak + 0.002f);
            Assert.InRange(c.RestingAtTime, e.Baseline - 0.002f, e.Baseline + 0.002f);
        }
    }

    private static string StatusOf(string fixture)
    {
        var anchors = BatteryHealthMath.DetectCharges(BatteryFixtures.Samples(fixture)).Charges
            .Where(c => c.IsFull && c.SettledVoltage != null)
            .Select(c => new Sample(c.EndedAt!.Value, c.SettledVoltage!.Value))
            .ToList();
        return BatteryHealthMath.ClassifyStatus(anchors).Status;
    }

    // Real-shaped traces. Expected values are the replay results of the same readings.

    [Fact]
    public void Fixture_LongRestSlowRamp_ThreeFullChargesWithOneSlowRampAndOneLowPeak()
    {
        AssertCharges(BatteryFixtures.LongRestSlowRamp,
            new(981.565, 1109.267, true, 12.823f, 13.523f, 11.957f),
            // A slow ramp after an early spike stays one charge.
            new(2215.880, 2625.696, true, 12.816f, 14.125f, 12.271f),
            // Peaks at only 13.725 V and still counts as full.
            new(3845.854, 4380.629, true, 12.753f, 13.725f, 12.275f));
        Assert.Equal("good", StatusOf(BatteryFixtures.LongRestSlowRamp));
    }

    [Fact]
    public void Fixture_SuddenDrop_ChargeEndsAtTheDrop()
    {
        AssertCharges(BatteryFixtures.SuddenDrop,
            new(1391.707, 1488.544, true, 12.754f, 14.550f, 12.126f),
            // The end moves to the sudden drop, and the settled voltage follows it.
            new(2319.998, 2380.941, true, 12.717f, 14.522f, 12.345f),
            new(3673.619, 3773.498, true, 12.684f, 14.563f, 12.179f),
            new(4592.984, 4684.868, true, 12.651f, 14.557f, 12.249f));
        Assert.Equal("good", StatusOf(BatteryFixtures.SuddenDrop));
    }

    [Fact]
    public void Fixture_TwoCloseCharges_StayTwoChargesWithSevenFull()
    {
        AssertCharges(BatteryFixtures.TwoCloseCharges,
            new(506.631, 537.566, false, 12.872f, 14.126f, 11.502f),
            new(1151.416, 1244.199, true, 13.014f, 13.564f, 12.478f),
            new(1795.699, 1844.577, true, 13.092f, 14.114f, 12.700f),
            new(2384.231, 2433.101, true, 13.105f, 14.125f, 12.692f),
            new(3015.372, 3057.224, true, 13.116f, 14.120f, 12.669f),
            new(3547.949, 3596.830, true, 13.047f, 14.126f, 12.671f),
            new(4007.011, 4055.897, true, 13.071f, 14.126f, 12.713f),
            new(4092.798, 4689.942, true, 13.160f, 14.126f, 13.057f));
        Assert.Equal("good", StatusOf(BatteryFixtures.TwoCloseCharges));
    }

    [Fact]
    public void Fixture_RegularCycles_AllChargesFull()
    {
        AssertCharges(BatteryFixtures.RegularCycles,
            new(74.431, 183.006, true, 13.166f, 14.013f, 13.049f),
            new(461.029, 507.871, true, 13.313f, 14.149f, 13.023f),
            new(1093.034, 1142.861, true, 13.359f, 14.199f, 12.965f),
            new(1770.613, 1819.427, true, 13.312f, 14.175f, 13.019f),
            new(1948.979, 2020.733, true, 13.325f, 14.199f, 13.191f),
            new(2532.125, 2582.956, true, 13.297f, 14.199f, 13.020f),
            new(3248.519, 3297.336, true, 13.266f, 14.164f, 12.923f),
            new(3705.892, 3756.706, true, 13.240f, 14.188f, 12.919f),
            new(4402.466, 4452.290, true, 13.203f, 14.154f, 12.823f));
        Assert.Equal("good", StatusOf(BatteryFixtures.RegularCycles));
    }

    [Fact]
    public void Fixture_LongFloat_FourDayChargeIsOneCharge()
    {
        AssertCharges(BatteryFixtures.LongFloat,
            new(1671.791, 1767.527, true, 14.021f, 15.713f, 13.446f),
            new(3773.726, 3869.486, true, 13.969f, 15.713f, 13.406f));
        Assert.Equal("good", StatusOf(BatteryFixtures.LongFloat));
    }

    [Theory]
    [InlineData(BatteryFixtures.LongRestSlowRamp)]
    [InlineData(BatteryFixtures.SuddenDrop)]
    [InlineData(BatteryFixtures.TwoCloseCharges)]
    [InlineData(BatteryFixtures.RegularCycles)]
    [InlineData(BatteryFixtures.LongFloat)]
    public void Fixture_ResumedAtEveryChargeEnd_EqualsFullReplay(string fixture)
    {
        var samples = BatteryFixtures.Samples(fixture);
        var full = BatteryHealthMath.DetectCharges(samples).Charges;

        for (var i = 0; i < full.Count; i++)
        {
            var end = full[i].EndedAt!.Value;
            var resumed = BatteryHealthMath.DetectCharges(samples.Where(s => s.Timestamp >= end).ToList(), end);
            Assert.False(resumed.ResumePointChanged);
            Assert.Equal(full[i].SettledVoltage, resumed.ResumeSettledVoltage);
            Assert.Equal(full.Skip(i + 1), resumed.Charges);
        }
    }

    // Charge start.

    [Fact]
    public void DetectCharges_FlatVoltage_FindsNothing()
    {
        Assert.Empty(Detect(new BatteryTrace().Hold(500, 12.4f)));
    }

    [Fact]
    public void DetectCharges_NoReadings_FindsNothing()
    {
        Assert.Empty(Detect(new BatteryTrace()));
    }

    [Fact]
    public void DetectCharges_TwoReadingsAtStartRatio_StartsAtTheFirst()
    {
        var charges = Detect(new BatteryTrace().Hold(72, 12.0f).Values(12.37f, 12.37f));
        Assert.Equal(72, HoursOf(charges.Single().StartedAt));
        Assert.True(charges.Single().IsOpen);
        Assert.Equal(12.0f, charges.Single().RestingAtTime);
    }

    [Fact]
    public void DetectCharges_JustBelowStartRatio_StaysAtRest()
    {
        Assert.Empty(Detect(new BatteryTrace().Hold(72, 12.0f).Hold(10, 12.35f)));
    }

    [Fact]
    public void DetectCharges_OneHighReading_IsIgnored()
    {
        Assert.Empty(Detect(new BatteryTrace().Hold(72, 12.4f).Values(14.8f).Hold(72, 12.4f)));
    }

    [Fact]
    public void DetectCharges_BaselineIsLowQuartile()
    {
        // Over a quarter of the rest sits at 12.0 and the rest of it at 12.5.
        var charge = Detect(new BatteryTrace().Hold(42, 12.5f).Hold(30, 12.0f).Values(12.4f, 12.4f)).Single();
        Assert.Equal(72, HoursOf(charge.StartedAt));
        Assert.Equal(12.0f, charge.RestingAtTime);
    }

    [Fact]
    public void DetectCharges_RestingAboveLowQuartile_KeepsCounting()
    {
        // Under a quarter sits at 12.0, so the low quartile is 12.5 and 12.6 is rest.
        Assert.Empty(Detect(new BatteryTrace().Hold(62, 12.5f).Hold(10, 12.0f).Hold(5, 12.6f)));
    }

    [Fact]
    public void DetectCharges_BaselineForgetsReadingsOlderThanSevenDays()
    {
        // Counting the old 12.0 readings would make 12.5 a charge.
        Assert.Empty(Detect(new BatteryTrace().Hold(72, 12.0f).Ramp(72, 12.0f, 12.3f).Hold(170, 12.3f).Hold(5, 12.5f)));
    }

    [Fact]
    public void DetectCharges_BaselineCountsReadingsUpToSevenDaysOld()
    {
        // The window is 169 readings wide and 50 of them sit at 12.0.
        var charge = Detect(new BatteryTrace().Hold(50, 12.0f).Hold(118, 12.35f).Values(12.8f, 12.8f)).Single();
        Assert.Equal(12.0f, charge.RestingAtTime);
    }

    [Fact]
    public void DetectCharges_FewerThanThreeRestReadings_BaselineIsLastRestReading()
    {
        // The third reading is rest against 12.2. With 3 rest readings the baseline falls to 12.0.
        var charge = Detect(new BatteryTrace().Values(12.0f, 12.2f, 12.4f, 12.4f, 12.4f)).Single();
        Assert.Equal(3, HoursOf(charge.StartedAt));
        Assert.Equal(12.0f, charge.RestingAtTime);
    }

    [Fact]
    public void DetectCharges_BaselineCountsAReadingExactlySevenDaysOld()
    {
        // Rest readings at 12.0, 12.0, 12.3, 12.3 and 12.3 give a low quartile of 12.0 only
        // while the oldest still counts. Both high readings have the same timestamp.
        var trace = new BatteryTrace().Values(12.0f, 12.0f, 12.3f, 12.3f, 12.3f);
        var at = T0.AddDays(BatteryHealthMath.BaselineWindowDays);
        trace.Samples.Add(new Sample(at, 12.4f));
        trace.Samples.Add(new Sample(at, 12.4f));
        Assert.Equal(at, Detect(trace).Single().StartedAt);
    }

    [Fact]
    public void DetectCharges_SecondReadingHighAfterTheFirst_StartsFromThatOne()
    {
        // A single rest reading is the baseline, so no 3 readings are needed before a charge can start.
        Assert.Equal(1, HoursOf(Detect(new BatteryTrace().Values(12f, 13f, 13f)).Single().StartedAt));
    }

    [Fact]
    public void DetectCharges_NoRestReadingsAfterAGap_BaselineIsTheSettleLow()
    {
        // The sensor went quiet right after settling, so the first readings count from the low.
        var charges = Detect(RestThenCharge().Hold(12, 12.7f).Gap(100).Values(13.1f, 13.1f, 13.1f));
        Assert.Equal(2, charges.Count);
        Assert.Equal(12.7f, charges[1].RestingAtTime);
    }

    [Fact]
    public void DetectCharges_SettlingReadingsAreNotRestReadings()
    {
        // The sensor went quiet until a week after the end. Only the 3 new readings are
        // rest readings, so 12.4 V counts from 12.2 V and not from the low of 12.0 V.
        var trace = RestThenCharge(rest: 11f).Hold(12, 12.0f).Gap(159).Values(12.2f, 12.2f, 12.2f, 12.4f, 12.4f);
        Assert.False(Detect(trace).Single().IsOpen);
    }

    [Fact]
    public void DetectCharges_CapEndsSevenDaysAfterTheFirstLowReading()
    {
        // The reading at 167 hours is under the cap and the one at 168 hours is not, so no charge starts.
        var trace = RestThenCharge().Values(12.8f, 12.7f).Hold(10, 12.6f).Hold(155, 12.9f).Values(13.0f, 13.0f).Hold(10, 12.9f);
        Assert.False(Detect(trace).Single().IsOpen);
    }

    [Fact]
    public void DetectCharges_BaselineFrozenForTheCharge()
    {
        var charge = Detect(RestThenCharge().Hold(10, 12.7f).Hold(48, 14.4f)).Single();
        Assert.Equal(12.4f, charge.RestingAtTime);
    }

    // Plateau.

    [Fact]
    public void Plateau_FewerThanSixReadings_IsNotSet()
    {
        Assert.Equal(0f, Detect(new BatteryTrace().Hold(72, 12.0f).Hold(5, 14.0f)).Single().Plateau);
    }

    [Fact]
    public void Plateau_SixReadings_IsSet()
    {
        Assert.Equal(14.0f, Detect(new BatteryTrace().Hold(72, 12.0f).Hold(6, 14.0f)).Single().Plateau);
    }

    [Fact]
    public void Plateau_UsesLowerMedian()
    {
        var charge = Detect(new BatteryTrace().Hold(72, 12.0f).Values(14f, 14f, 14f, 15f, 15f, 15f)).Single();
        Assert.Equal(14.0f, charge.Plateau);
    }

    [Fact]
    public void Plateau_ShortRiseDoesNotChangeIt()
    {
        // 7 of the last 12 readings are still at 14 V.
        Assert.Equal(14.0f, Detect(new BatteryTrace().Hold(72, 12.0f).Hold(20, 14f).Hold(5, 15f)).Single().Plateau);
    }

    [Fact]
    public void Plateau_FollowsTheLastTwelveReadings()
    {
        // 7 of the last 12 readings are at 15 V.
        Assert.Equal(15.0f, Detect(new BatteryTrace().Hold(72, 12.0f).Hold(10, 14f).Hold(7, 15f)).Single().Plateau);
    }

    [Fact]
    public void Plateau_EarlySpikeDoesNotSetIt()
    {
        var charge = Detect(new BatteryTrace().Hold(72, 12.0f).Values(15.5f, 15.5f).Hold(20, 14f).Hold(10, 13.6f)).Single();
        Assert.Equal(14f, charge.Plateau);
        Assert.Equal(15.5f, charge.PeakVoltage);
        Assert.True(charge.IsOpen);
    }

    [Fact]
    public void Plateau_KeepsTheHighestValue()
    {
        // 13.85 V is below 0.965 of 14.4 V, so keeping the plateau is what ends this charge.
        var charge = Detect(new BatteryTrace().Hold(72, 12.0f).Hold(24, 14.4f).Hold(24, 13.95f).Hold(4, 13.85f)).Single();
        Assert.Equal(14.4f, charge.Plateau);
        Assert.Equal(72 + 48, HoursOf(charge.EndedAt!.Value));
    }

    // Charge end.

    [Fact]
    public void DetectCharges_ChargeInProgress_StaysOpen()
    {
        var charge = Detect(RestThenCharge()).Single();
        Assert.True(charge.IsOpen);
        Assert.Null(charge.EndedAt);
        Assert.False(charge.IsFull);
        Assert.Null(charge.SettledVoltage);
        Assert.Equal(23, charge.HeldHours);
    }

    [Fact]
    public void DetectCharges_TwoLowReadings_EndAtTheFirst()
    {
        var charge = Detect(RestThenCharge().Hold(48, 12.7f)).Single();
        Assert.Equal(72 + 24, HoursOf(charge.EndedAt!.Value));
    }

    [Fact]
    public void DetectCharges_OneLowReading_DoesNotEnd()
    {
        Assert.True(Detect(RestThenCharge().Values(12.7f).Hold(10, 14.4f)).Single().IsOpen);
    }

    [Fact]
    public void DetectCharges_TwoLowReadingsApart_DoNotEnd()
    {
        Assert.True(Detect(RestThenCharge().Values(12.7f).Hold(10, 14.4f).Values(12.7f)).Single().IsOpen);
    }

    [Fact]
    public void DetectCharges_JustAboveEndRatio_DoesNotEnd()
    {
        // 0.965 x 14.4 V is about 13.896 V.
        Assert.True(Detect(RestThenCharge().Hold(48, 13.90f)).Single().IsOpen);
    }

    [Fact]
    public void DetectCharges_JustBelowEndRatio_Ends()
    {
        Assert.False(Detect(RestThenCharge().Hold(48, 13.89f)).Single().IsOpen);
    }

    [Fact]
    public void DetectCharges_ClimbingBelowEndRatio_RestartsTheCount()
    {
        // Each reading rises more than 5 mV, so 2 low readings in a row never settle.
        Assert.True(Detect(RestThenCharge().Values(13.0f, 13.01f, 13.02f, 13.03f, 13.04f)).Single().IsOpen);
    }

    [Fact]
    public void DetectCharges_SmallRiseBelowEndRatio_CountsAsLow()
    {
        var charge = Detect(RestThenCharge().Values(13.0f, 13.004f).Hold(10, 12.9f)).Single();
        Assert.Equal(72 + 24, HoursOf(charge.EndedAt!.Value));
    }

    [Fact]
    public void DetectCharges_RestartAfterClimb_EndsAtTheNewFirstReading()
    {
        var charge = Detect(RestThenCharge().Values(13.0f, 13.02f, 13.02f).Hold(10, 12.9f)).Single();
        Assert.Equal(72 + 25, HoursOf(charge.EndedAt!.Value));
    }

    [Fact]
    public void DetectCharges_BeforeAnyPlateau_ChargeDoesNotEnd()
    {
        // No plateau yet after 2 charge readings, so the drop right after them does not end the charge.
        Assert.True(Detect(new BatteryTrace().Hold(72, 12.0f).Values(14f, 14f).Hold(3, 12.1f)).Single().IsOpen);
    }

    // Same session.

    [Fact]
    public void DetectCharges_NewChargeWithin24Hours_ContinuesTheSession()
    {
        var charge = Detect(RestThenCharge().Hold(20, 12.7f).Hold(24, 14.4f)).Single();
        Assert.Equal(72, HoursOf(charge.StartedAt));
    }

    [Fact]
    public void DetectCharges_NewChargeAtExactly24Hours_ContinuesTheSession()
    {
        Assert.Single(Detect(RestThenCharge().Hold(24, 12.7f).Hold(24, 14.4f)));
    }

    [Fact]
    public void DetectCharges_SharpRiseAfter24Hours_IsANewCharge()
    {
        var charges = Detect(RestThenCharge().Hold(30, 12.7f).Hold(24, 14.4f));
        Assert.Equal(2, charges.Count);
        Assert.Equal(72 + 24, HoursOf(charges[0].EndedAt!.Value));
        Assert.Equal(72 + 24 + 30, HoursOf(charges[1].StartedAt));
    }

    [Fact]
    public void DetectCharges_SlowClimbWithin72Hours_ContinuesTheSession()
    {
        // The first high reading rises only 90 mV, and 12.7 x 1.03 is about 13.08.
        var trace = RestThenCharge().Hold(40, 12.7f).Ramp(5, 12.7f, 13.0f).Values(13.09f, 13.15f).Hold(24, 14.4f);
        Assert.Single(Detect(trace));
    }

    [Fact]
    public void DetectCharges_RiseOf100MillivoltsAfter24Hours_IsANewCharge()
    {
        // The first high reading rises 110 mV from the reading before it.
        var trace = RestThenCharge().Hold(40, 12.7f).Ramp(4, 12.7f, 12.99f).Values(13.1f).Hold(24, 14.4f);
        Assert.Equal(2, Detect(trace).Count);
    }

    [Fact]
    public void DetectCharges_RiseJustOver100Millivolts_IsANewCharge()
    {
        const float before = 13.0f;
        var after = before + BatteryHealthMath.SlowClimbMaxRiseVolts;
        var trace = RestThenCharge(rest: 12.0f, charge: 14f).Hold(40, 12.65f).Values(12.65f, before, after).Hold(24, 14f);
        Assert.Equal(2, Detect(trace).Count);
    }

    [Fact]
    public void DetectCharges_SlowClimbAtExactly72Hours_ContinuesTheSession()
    {
        var trace = RestThenCharge().Hold(60, 12.7f).Ramp(12, 12.7f, 13.0f).Values(13.09f, 13.15f).Hold(24, 14.4f);
        Assert.Single(Detect(trace));
    }

    [Fact]
    public void DetectCharges_SlowClimbAfter72Hours_IsANewCharge()
    {
        var trace = RestThenCharge().Hold(80, 12.7f).Ramp(10, 12.7f, 13.09f).Hold(24, 14.4f);
        Assert.Equal(2, Detect(trace).Count);
    }

    [Fact]
    public void DetectCharges_MergedSession_KeepsStartBaselineAndPeak()
    {
        var charge = Detect(RestThenCharge().Hold(10, 12.7f).Hold(30, 14.6f)).Single();
        Assert.Equal(72, HoursOf(charge.StartedAt));
        Assert.Equal(12.4f, charge.RestingAtTime);
        Assert.Equal(14.6f, charge.PeakVoltage);
    }

    // Settling baseline.

    [Fact]
    public void DetectCharges_DuringSettle_BaselineIsLowestSettlingReading()
    {
        // 13.1 V is more than 1.03 x 12.7 V, so the charge opens again during the settle.
        var charge = Detect(RestThenCharge().Values(12.9f, 12.7f, 12.9f, 13.1f, 13.1f)).Single();
        Assert.True(charge.IsOpen);
    }

    [Fact]
    public void DetectCharges_SettleIsTwelveHours_ReadingsAfterItAreRest()
    {
        // During the settle only the low counts. After it, rest readings count as well,
        // so 3 readings at 12.9 V add up and the low quartile takes over after a week.
        var trace = RestThenCharge().Hold(12, 12.7f).Hold(160, 12.9f).Hold(5, 13.1f);
        Assert.Single(Detect(trace));
        Assert.True(Detect(trace).Single().EndedAt != null);
    }

    [Fact]
    public void DetectCharges_WithinSevenDaysAfterCharge_BaselineCappedAtSettleLow()
    {
        // Settles to 12.6, climbs to 12.9, then 13.0 is over 1.03 x 12.6 but not over 1.03 x 12.9.
        var trace = RestThenCharge().Values(12.8f, 12.7f).Hold(10, 12.6f).Hold(90, 12.9f).Hold(24, 13.0f);
        var charges = Detect(trace);
        Assert.Equal(2, charges.Count);
        Assert.Equal(12.6f, charges[1].RestingAtTime);
    }

    [Fact]
    public void DetectCharges_AfterSevenDays_BaselineNoLongerCapped()
    {
        Assert.Single(Detect(RestThenCharge().Values(12.8f, 12.7f).Hold(10, 12.6f).Hold(170, 12.9f).Hold(24, 13.0f)));
    }

    [Fact]
    public void DetectCharges_SettleLowIncludesTheFirstLowReading()
    {
        // The first low reading, 11.9 V, is the settle low. The second is 4 mV higher.
        var trace = RestThenCharge(rest: 11.0f).Values(11.9f, 11.904f).Hold(100, 11.96f).Hold(6, 12.26f);
        var charges = Detect(trace);
        Assert.Equal(2, charges.Count);
        Assert.Equal(11.9f, charges[1].RestingAtTime);
    }

    [Fact]
    public void DetectCharges_RestReadingsBeforeTheCharge_DoNotSurviveIt()
    {
        // The rest at 11.5 V is cleared by the charge, so 12.8 V is rest against 12.5 V.
        var trace = new BatteryTrace().Hold(72, 11.5f).Hold(24, 14.4f).Hold(200, 12.5f).Hold(10, 12.8f);
        Assert.Single(Detect(trace));
    }

    // Slow climb then drop.

    [Fact]
    public void DetectCharges_SlowClimbThenDrop_MovesTheEnd()
    {
        var trace = RestThenCharge().Hold(12, 12.7f).Ramp(40, 12.7f, 12.9f).Values(12.75f).Hold(40, 12.7f);
        Assert.Equal(72 + 24 + 12 + 40, HoursOf(Detect(trace).Single().EndedAt!.Value));
    }

    [Fact]
    public void DetectCharges_DropMovesSettledVoltageToo()
    {
        var trace = RestThenCharge().Hold(12, 12.7f).Ramp(40, 12.7f, 12.9f).Values(12.75f).Hold(40, 12.6f);
        Assert.Equal(12.6f, Detect(trace).Single().SettledVoltage);
    }

    [Fact]
    public void DetectCharges_DropOfExactly100Millivolts_MovesTheEnd()
    {
        const float high = 13.0f;
        var low = high - BatteryHealthMath.SlowClimbDropVolts;
        var trace = RestThenCharge().Hold(12, 12.7f).Values(high, low).Hold(30, 12.7f);
        Assert.Equal(72 + 24 + 13, HoursOf(Detect(trace).Single().EndedAt!.Value));
    }

    [Fact]
    public void DetectCharges_SmallDrop_DoesNotMoveTheEnd()
    {
        var trace = RestThenCharge().Hold(12, 12.7f).Ramp(40, 12.7f, 12.9f).Values(12.82f).Hold(40, 12.82f);
        Assert.Equal(72 + 24, HoursOf(Detect(trace).Single().EndedAt!.Value));
    }

    [Fact]
    public void DetectCharges_DropFromNearTheSettleLow_DoesNotMoveTheEnd()
    {
        // 40 mV above the settling low is noise, even with a large drop after it.
        var trace = RestThenCharge().Hold(12, 12.7f).Values(12.74f, 12.6f).Hold(30, 12.7f);
        Assert.Equal(72 + 24, HoursOf(Detect(trace).Single().EndedAt!.Value));
    }

    [Fact]
    public void DetectCharges_DropOf110Millivolts_MovesTheEnd()
    {
        var trace = RestThenCharge().Hold(12, 12.7f).Values(13.0f, 12.89f).Hold(30, 12.7f);
        Assert.Equal(72 + 24 + 13, HoursOf(Detect(trace).Single().EndedAt!.Value));
    }

    [Fact]
    public void DetectCharges_DropFrom55MillivoltsAboveTheLow_MovesTheEnd()
    {
        var trace = RestThenCharge().Hold(12, 12.7f).Values(12.755f, 12.6f).Hold(30, 12.65f);
        Assert.Equal(72 + 24 + 13, HoursOf(Detect(trace).Single().EndedAt!.Value));
    }

    [Fact]
    public void DetectCharges_DropAfter72Hours_DoesNotMoveTheEnd()
    {
        var trace = RestThenCharge().Hold(12, 12.7f).Ramp(61, 12.7f, 12.9f).Values(12.75f).Hold(40, 12.7f);
        Assert.Equal(72 + 24, HoursOf(Detect(trace).Single().EndedAt!.Value));
    }

    [Fact]
    public void DetectCharges_DropAtExactly72Hours_MovesTheEnd()
    {
        var trace = RestThenCharge().Hold(12, 12.7f).Ramp(60, 12.7f, 12.9f).Values(12.75f).Hold(40, 12.7f);
        Assert.Equal(72 + 24 + 72, HoursOf(Detect(trace).Single().EndedAt!.Value));
    }

    [Fact]
    public void DetectCharges_DropResetsSettleLow()
    {
        // After the drop the settle low is the drop reading, so a climb counts from it.
        var trace = RestThenCharge().Hold(12, 12.7f).Ramp(40, 12.7f, 12.9f).Values(12.2f).Hold(30, 12.3f).Hold(5, 12.6f);
        var charges = Detect(trace);
        Assert.Equal(2, charges.Count);
        Assert.Equal(12.2f, charges[1].RestingAtTime);
    }

    [Fact]
    public void DetectCharges_DropClearsRestReadings()
    {
        // The low rest readings before the drop would make 13.2 V a charge.
        var trace = RestThenCharge().Hold(30, 12.7f).Values(13.0f, 12.89f).Hold(14, 12.89f).Hold(3, 13.2f);
        Assert.False(Detect(trace).Single().IsOpen);
    }

    [Fact]
    public void DetectCharges_DropClearsAHighReadingBeforeIt()
    {
        // 13.1 V is one high reading before the drop. The 13.4 V after it is again only one.
        var trace = RestThenCharge().Hold(14, 12.7f).Values(13.1f, 12.95f, 13.4f);
        Assert.False(Detect(trace).Single().IsOpen);
    }

    [Fact]
    public void DetectCharges_DropRestartsTheSevenDayCap()
    {
        // The drop comes 58 hours after the first end, and 193 hours after that end the
        // cap at the new settling low of 12.6 V still holds.
        var trace = RestThenCharge().Hold(12, 12.7f).Ramp(46, 12.7f, 12.9f).Values(12.6f).Hold(134, 12.9f).Hold(24, 13.0f);
        var charges = Detect(trace);
        Assert.Equal(2, charges.Count);
        Assert.Equal(12.6f, charges[1].RestingAtTime);
    }

    // Full charge.

    [Fact]
    public void FullCharge_SixHoursAtPlateau_IsFull()
    {
        // 7 readings at plateau span 6 hours.
        var charge = Detect(RestThenCharge(chargeHours: 7).Hold(48, 12.7f)).Single();
        Assert.Equal(6, charge.HeldHours);
        Assert.True(charge.IsFull);
    }

    [Fact]
    public void FullCharge_FiveHoursAtPlateau_IsNotFull()
    {
        var charge = Detect(RestThenCharge(chargeHours: 6).Hold(48, 12.7f)).Single();
        Assert.Equal(5, charge.HeldHours);
        Assert.False(charge.IsFull);
    }

    [Fact]
    public void FullCharge_BelowHoldRatio_DoesNotCount()
    {
        // 0.98 x 14.4 V is about 14.11 V.
        var charge = Detect(new BatteryTrace().Hold(72, 12.4f).Hold(12, 14.4f).Hold(20, 14.0f).Hold(48, 12.7f)).Single();
        Assert.Equal(11, charge.HeldHours);
    }

    [Fact]
    public void FullCharge_OneLowReadingBetween_CountsNeitherSideOfIt()
    {
        var charge = Detect(new BatteryTrace().Hold(72, 12.4f).Hold(10, 14.4f).Values(14.0f).Hold(10, 14.4f).Hold(48, 12.7f)).Single();
        Assert.Equal(18, charge.HeldHours);
    }

    [Fact]
    public void FullCharge_AtHoldRatio_Counts()
    {
        var charge = Detect(new BatteryTrace().Hold(72, 12.4f).Hold(12, 14.4f).Hold(10, 14.12f).Hold(48, 12.7f)).Single();
        Assert.Equal(21, charge.HeldHours);
    }

    [Fact]
    public void FullCharge_GapOverThreeHours_IsIgnored()
    {
        var charge = Detect(new BatteryTrace().Hold(72, 12.4f).Hold(4, 14.4f).Gap(5).Hold(4, 14.4f).Hold(48, 12.7f)).Single();
        Assert.Equal(6, charge.HeldHours);
    }

    [Fact]
    public void FullCharge_GapOfExactlyThreeHours_Counts()
    {
        var charge = Detect(new BatteryTrace().Hold(72, 12.4f).Hold(4, 14.4f).Gap(2).Hold(4, 14.4f).Hold(48, 12.7f)).Single();
        Assert.Equal(9, charge.HeldHours);
    }

    [Fact]
    public void FullCharge_LowReadingsAtTheEnd_DoNotCount()
    {
        // The charge readings before the first low one are all that count.
        var charge = Detect(RestThenCharge(chargeHours: 7).Hold(48, 12.7f)).Single();
        Assert.Equal(6, charge.HeldHours);
    }

    // Settled voltage.

    [Fact]
    public void Settled_IsMedianTwelveToThirtySixHoursAfterTheEnd()
    {
        var trace = RestThenCharge().Hold(12, 12.9f).Hold(25, 12.7f).Hold(48, 12.3f);
        Assert.Equal(12.7f, Detect(trace).Single().SettledVoltage);
    }

    [Fact]
    public void Settled_FewerThanThreeReadings_IsNull()
    {
        Assert.Null(Detect(RestThenCharge().Hold(14, 12.7f)).Single().SettledVoltage);
    }

    [Fact]
    public void Settled_ThreeReadings_IsSet()
    {
        Assert.Equal(12.7f, Detect(RestThenCharge().Hold(15, 12.7f)).Single().SettledVoltage);
    }

    [Fact]
    public void PostChargeResting_ReadsTheInclusiveWindow()
    {
        var samples = new List<Sample>
        {
            new(T0.AddHours(11.9), 11f),
            new(T0.AddHours(12), 12f),
            new(T0.AddHours(20), 12.5f),
            new(T0.AddHours(36), 13f),
            new(T0.AddHours(36.1), 14f),
        };
        Assert.Equal(12.5f, BatteryHealthMath.PostChargeResting(samples, T0, DateTime.MaxValue));
    }

    [Fact]
    public void PostChargeResting_StopsAtTheNextCharge()
    {
        var samples = new List<Sample>
        {
            new(T0.AddHours(12), 12f),
            new(T0.AddHours(13), 12.2f),
            new(T0.AddHours(14), 12.4f),
            new(T0.AddHours(15), 14f),
            new(T0.AddHours(16), 14f),
            new(T0.AddHours(17), 14f),
        };
        Assert.Equal(12.2f, BatteryHealthMath.PostChargeResting(samples, T0, T0.AddHours(15)));
    }

    [Fact]
    public void Settled_StopsAtTheNextClosedCharge()
    {
        // The next charge opens 30 hours after the end and is itself closed.
        var charges = Detect(RestThenCharge().Hold(30, 12.7f).Hold(24, 14.4f).Hold(48, 12.7f));
        Assert.Equal(2, charges.Count);
        Assert.Equal(12.7f, charges[0].SettledVoltage);
    }

    // Only 2 rest readings lie in the settle window before the next charge at 34 hours.
    private static BatteryTrace TwoSettleReadingsThenCharge() =>
        RestThenCharge().Values(12.7f, 12.7f).Gap(30).Values(12.7f, 12.7f).Hold(24, 14.4f);

    [Fact]
    public void Settled_StopsAtTheOpenCharge()
    {
        var charges = Detect(TwoSettleReadingsThenCharge());
        Assert.Null(charges[0].SettledVoltage);
        Assert.True(charges[1].IsOpen);
    }

    [Fact]
    public void Settled_StopsAtALaterClosedCharge()
    {
        var charges = Detect(TwoSettleReadingsThenCharge().Hold(48, 12.7f));
        Assert.Null(charges[0].SettledVoltage);
        Assert.False(charges[1].IsOpen);
    }

    // Resume.

    [Fact]
    public void Resume_FirstReadingNotAtTheEnd_AsksForFullReplay()
    {
        var result = BatteryHealthMath.DetectCharges(RestThenCharge().Hold(48, 12.7f).Samples, T0.AddHours(1000));
        Assert.True(result.ResumePointChanged);
        Assert.Empty(result.Charges);
    }

    [Fact]
    public void Resume_NoReadings_AsksForFullReplay()
    {
        Assert.True(BatteryHealthMath.DetectCharges(new List<Sample>(), T0).ResumePointChanged);
    }

    private static BatteryHealthMath.ChargeDetection ResumeAt(BatteryTrace trace, double endHours)
    {
        var end = T0.AddHours(endHours);
        return BatteryHealthMath.DetectCharges(trace.Samples.Where(s => s.Timestamp >= end).ToList(), end);
    }

    [Fact]
    public void Resume_NewChargeContinuesTheResumedCharge_AsksForFullReplay()
    {
        Assert.True(ResumeAt(RestThenCharge().Hold(10, 12.7f).Hold(24, 14.4f), 72 + 24).ResumePointChanged);
    }

    [Fact]
    public void Resume_DropMovesTheResumedEnd_AsksForFullReplay()
    {
        var trace = RestThenCharge().Hold(12, 12.7f).Ramp(40, 12.7f, 12.9f).Values(12.75f).Hold(40, 12.7f);
        Assert.True(ResumeAt(trace, 72 + 24).ResumePointChanged);
    }

    [Fact]
    public void Resume_LaterChargesInTheSameRun_CanStillMerge()
    {
        // After the resumed charge comes a new one, which a third charge continues.
        var trace = RestThenCharge().Hold(100, 12.7f).Hold(24, 14.4f).Hold(10, 12.8f).Hold(24, 14.4f).Hold(100, 12.8f);
        var full = BatteryHealthMath.DetectCharges(trace.Samples).Charges;
        Assert.Equal(2, full.Count);
        var resumed = ResumeAt(trace, 72 + 24);
        Assert.False(resumed.ResumePointChanged);
        Assert.Equal(full[1], resumed.Charges.Single());
        Assert.Equal(full[0].SettledVoltage, resumed.ResumeSettledVoltage);
    }

    [Fact]
    public void Resume_LaterDropInTheSameRun_CanStillMoveThatEnd()
    {
        var trace = RestThenCharge().Hold(100, 12.7f).Hold(24, 14.4f).Hold(12, 12.7f).Ramp(40, 12.7f, 12.9f).Values(12.75f).Hold(40, 12.7f);
        var full = BatteryHealthMath.DetectCharges(trace.Samples).Charges;
        var resumed = ResumeAt(trace, 72 + 24);
        Assert.False(resumed.ResumePointChanged);
        Assert.Equal(full[1], resumed.Charges.Single());
    }

    [Fact]
    public void Resume_SecondLowReadingSlightlyHigher_KeepsTheFirstAsSettleLow()
    {
        var trace = RestThenCharge(rest: 11.0f).Values(11.9f, 11.904f).Hold(100, 11.96f).Hold(6, 12.26f);
        var full = BatteryHealthMath.DetectCharges(trace.Samples).Charges;
        Assert.Equal(full[1], ResumeAt(trace, 72 + 24).Charges.Single());
    }

    [Fact]
    public void Resume_GapBeforeTheSecondLowReading_GivesTheSameSettleLow()
    {
        // The second low reading comes 21 hours after the first and is lower.
        var trace = RestThenCharge().Values(12.9f).Gap(20).Values(12.5f).Hold(100, 12.6f).Hold(6, 13.1f);
        var full = BatteryHealthMath.DetectCharges(trace.Samples).Charges;
        Assert.Equal(2, full.Count);
        Assert.Equal(12.5f, full[1].RestingAtTime);
        Assert.Equal(full[1], ResumeAt(trace, 72 + 24).Charges.Single());
    }

    [Fact]
    public void Resume_OpenChargeAfterTheEnd_StaysOpen()
    {
        var trace = RestThenCharge().Hold(100, 12.7f).Hold(24, 14.4f);
        var resumed = ResumeAt(trace, 72 + 24);
        Assert.True(resumed.Charges.Single().IsOpen);
        Assert.Equal(12.7f, resumed.ResumeSettledVoltage);
    }

    // Resting voltage.

    private static readonly IReadOnlyList<BatteryHealthMath.ChargeWindow> NoCharges = Array.Empty<BatteryHealthMath.ChargeWindow>();

    [Fact]
    public void Resting_NoRestReadings_IsNull()
    {
        var trace = new BatteryTrace().Hold(10, 12.4f).Hold(100, 14.4f);
        Assert.Null(BatteryHealthMath.ComputeRestingVoltage(trace.Samples, Detect(trace), trace.Samples[^1].Timestamp));
    }

    [Fact]
    public void Resting_IsBottomQuartileMedian()
    {
        var samples = new List<Sample>();
        for (var i = 0; i < 8; i++) samples.Add(new Sample(T0.AddHours(i), 12f + i * 0.1f));
        // The bottom quartile of 8 readings is 12.0 and 12.1.
        Assert.Equal(12.05f, BatteryHealthMath.ComputeRestingVoltage(samples, NoCharges, T0.AddHours(8))!.Value, 0.001f);
    }

    [Fact]
    public void Resting_FewReadings_UsesTheLowest()
    {
        var samples = new List<Sample> { new(T0, 12.5f), new(T0.AddHours(1), 12.1f) };
        Assert.Equal(12.1f, BatteryHealthMath.ComputeRestingVoltage(samples, NoCharges, T0.AddHours(2)));
    }

    [Fact]
    public void Resting_UsesOnlyTheLastThreeDays()
    {
        var at = T0.AddDays(10);
        var samples = new List<Sample>
        {
            new(at.AddDays(-3).AddMinutes(-1), 11f),
            new(at.AddDays(-3), 12f),
            new(at, 12.5f),
            new(at.AddMinutes(1), 10f),
        };
        Assert.Equal(12f, BatteryHealthMath.ComputeRestingVoltage(samples, NoCharges, at));
    }

    [Fact]
    public void Resting_LeavesOutChargingAndTwelveHoursAfter()
    {
        var at = T0.AddDays(10);
        var charge = new BatteryHealthMath.ChargeWindow(at.AddHours(-30), at.AddHours(-20), 14f, 12f, 14f, 10, true, null);
        var samples = new List<Sample>
        {
            new(at.AddHours(-31), 12.6f),
            new(at.AddHours(-30), 11f),
            new(at.AddHours(-20), 11f),
            new(at.AddHours(-8).AddMinutes(-1), 11f),
            new(at.AddHours(-8), 12.8f),
        };
        // 12.6 and 12.8 remain, and the bottom quartile of 2 is the lower one.
        Assert.Equal(12.6f, BatteryHealthMath.ComputeRestingVoltage(samples, new[] { charge }, at));
    }

    [Fact]
    public void Resting_ReadingExactlyTwelveHoursAfterTheEnd_Counts()
    {
        var at = T0.AddDays(10);
        var charge = new BatteryHealthMath.ChargeWindow(at.AddHours(-30), at.AddHours(-20), 14f, 12f, 14f, 10, true, null);
        var samples = new List<Sample> { new(at.AddHours(-8), 12.8f) };
        Assert.Equal(12.8f, BatteryHealthMath.ComputeRestingVoltage(samples, new[] { charge }, at));
    }

    [Fact]
    public void Resting_LeavesOutAnOpenCharge()
    {
        var at = T0.AddDays(10);
        var open = new BatteryHealthMath.ChargeWindow(at.AddHours(-5), null, 14f, 12f, 14f, 5, false, null);
        var samples = new List<Sample> { new(at.AddHours(-6), 12.6f), new(at.AddHours(-1), 11f) };
        Assert.Equal(12.6f, BatteryHealthMath.ComputeRestingVoltage(samples, new[] { open }, at));
    }

    // Status.

    [Fact]
    public void Status_NoAnchors_IsLearning()
    {
        var result = BatteryHealthMath.ClassifyStatus(new List<Sample>());
        Assert.Equal("learning", result.Status);
        Assert.Null(result.DropPct);
        Assert.Null(result.SlopePctPerWeek);
    }

    [Fact]
    public void Status_OneAnchor_IsLearning()
    {
        var result = BatteryHealthMath.ClassifyStatus(new List<Sample> { new(T0, 12.8f) });
        Assert.Equal("learning", result.Status);
        Assert.Null(result.DropPct);
    }

    [Theory]
    [InlineData(12.8f, 12.8f, "good")]
    [InlineData(100f, 98.1f, "good")]
    [InlineData(100f, 98f, "good")]
    [InlineData(100f, 97.9f, "attention")]
    [InlineData(100f, 95.1f, "attention")]
    [InlineData(100f, 95f, "attention")]
    [InlineData(100f, 94.9f, "replace")]
    public void Status_S1Boundaries(float earlier, float latest, string expected)
    {
        // With 2 anchors there is no slope, so S1 alone decides.
        var result = BatteryHealthMath.ClassifyStatus(new List<Sample> { new(T0, earlier), new(T0.AddDays(30), latest) });
        Assert.Equal(expected, result.Status);
        Assert.Null(result.SlopePctPerWeek);
    }

    [Fact]
    public void Status_LatestRising_DropIsZero()
    {
        var result = BatteryHealthMath.ClassifyStatus(new List<Sample> { new(T0, 12f), new(T0.AddDays(30), 13f) });
        Assert.Equal(0f, result.DropPct);
        Assert.Equal("good", result.Status);
    }

    [Fact]
    public void Status_DropIsMeasuredAgainstMedianOfEarlier()
    {
        // Earlier values 12, 14 and 13 have median 13.
        var result = BatteryHealthMath.ClassifyStatus(new List<Sample>
        {
            new(T0, 12f), new(T0.AddDays(1000), 14f), new(T0.AddDays(2000), 13f), new(T0.AddDays(3000), 12.74f),
        });
        Assert.Equal(2f, result.DropPct!.Value, 0.01f);
    }

    [Fact]
    public void Status_DropMedianOfEvenCount_AveragesTheMiddle()
    {
        // Earlier values 12 and 14 have median 13, and the latest is 1 % below it.
        var result = BatteryHealthMath.ClassifyStatus(new List<Sample>
        {
            new(T0, 12f), new(T0.AddDays(1000), 14f), new(T0.AddDays(2000), 12.87f),
        });
        Assert.Equal(1f, result.DropPct!.Value, 0.01f);
    }

    [Theory]
    [InlineData(0.09f, "good")]
    [InlineData(0.15f, "attention")]
    [InlineData(0.29f, "attention")]
    [InlineData(0.40f, "replace")]
    public void Status_S2Boundaries(float declinePctWeek, string expected)
    {
        // A straight decline with weekly anchors. The S1 drop stays under its limit.
        var anchors = new List<Sample>();
        for (var i = 0; i < 3; i++) anchors.Add(new Sample(T0.AddDays(7 * i), 100f - declinePctWeek * i));
        var result = BatteryHealthMath.ClassifyStatus(anchors);
        Assert.Equal(expected, result.Status);
        Assert.True(result.DropPct < BatteryHealthMath.S1AttentionDropPct);
    }

    [Fact]
    public void Status_S2NeedsThreeAnchors()
    {
        var anchors = new List<Sample> { new(T0, 100f), new(T0.AddDays(7), 99f) };
        Assert.Null(BatteryHealthMath.ClassifyStatus(anchors).SlopePctPerWeek);
        anchors.Add(new Sample(T0.AddDays(14), 98.9f));
        Assert.NotNull(BatteryHealthMath.ClassifyStatus(anchors).SlopePctPerWeek);
    }

    [Fact]
    public void Status_WorstSignalWins_S2OverS1()
    {
        var result = BatteryHealthMath.ClassifyStatus(new List<Sample> { new(T0, 100f), new(T0.AddDays(7), 99.5f), new(T0.AddDays(14), 99f) });
        Assert.True(result.DropPct < BatteryHealthMath.S1AttentionDropPct);
        Assert.Equal("replace", result.Status);
    }

    [Fact]
    public void Status_WorstSignalWins_S1OverS2()
    {
        // The latest is far below, spread over so many years that the slope is tiny.
        var result = BatteryHealthMath.ClassifyStatus(new List<Sample> { new(T0, 100f), new(T0.AddDays(5000), 100f), new(T0.AddDays(10000), 90f) });
        Assert.True(Math.Abs(result.SlopePctPerWeek!.Value) <= BatteryHealthMath.S2AttentionSlopePctWeek);
        Assert.Equal("replace", result.Status);
    }

    [Fact]
    public void Status_RisingSlope_IsNoDecline()
    {
        var result = BatteryHealthMath.ClassifyStatus(new List<Sample> { new(T0, 90f), new(T0.AddDays(7), 95f), new(T0.AddDays(14), 100f) });
        Assert.True(result.SlopePctPerWeek > 0);
        Assert.Equal("good", result.Status);
    }

    [Fact]
    public void Slope_FlatOrSinglePoint_IsZero()
    {
        Assert.Equal(0f, BatteryHealthMath.ComputeSlopePercentPerWeek(new List<Sample> { new(T0, 12f) }));
        Assert.Equal(0f, BatteryHealthMath.ComputeSlopePercentPerWeek(new List<Sample> { new(T0, 12f), new(T0, 13f) }));
        Assert.Equal(0f, BatteryHealthMath.ComputeSlopePercentPerWeek(new List<Sample> { new(T0, 12f), new(T0.AddDays(7), 12f) }));
    }

    [Fact]
    public void Slope_KnownDecline_MatchesPercentPerWeekOfMean()
    {
        // 1 V down over 7 days around a mean of 100 V is 1 % per week.
        var slope = BatteryHealthMath.ComputeSlopePercentPerWeek(new List<Sample> { new(T0, 100.5f), new(T0.AddDays(7), 99.5f) });
        Assert.Equal(-1f, slope, 0.001f);
    }

    // Self-discharge.

    private static List<Sample> Rest(double fromHours, double toHours, Func<double, float> volts)
    {
        var samples = new List<Sample>();
        for (var h = fromHours; h < toHours; h++) samples.Add(new Sample(T0.AddHours(h), volts(h)));
        return samples;
    }

    [Fact]
    public void SelfDischarge_ComputesPercentPerWeekFromTheTwoMedians()
    {
        // The charge ends at T0, so day 0 is 36 hours later. 13 V at day 2 to 4, 12.87 V at day 13 to 15.
        var samples = new List<Sample>();
        samples.AddRange(Rest(36 + 48, 36 + 96, _ => 13f));
        samples.AddRange(Rest(36 + 312, 36 + 360, _ => 12.87f));
        var expected = (13f - 12.87f) / 13f / 11f * 7f * 100f;
        Assert.Equal(expected, BatteryHealthMath.ComputeSelfDischarge(samples, T0)!.Value, 0.0001f);
    }

    [Fact]
    public void SelfDischarge_ReadsOnlyTheTwoWindows()
    {
        var samples = Rest(0, 36 + 400, h =>
            h >= 36 + 48 && h < 36 + 96 ? 13f
            : h >= 36 + 312 && h < 36 + 360 ? 12.5f
            : 5f);
        var expected = (13f - 12.5f) / 13f / 11f * 7f * 100f;
        Assert.Equal(expected, BatteryHealthMath.ComputeSelfDischarge(samples, T0)!.Value, 0.0001f);
    }

    [Fact]
    public void SelfDischarge_MediansCoverTheWholeWindows()
    {
        // A straight decline over the whole rest, so each median sits in the middle of its window.
        Func<double, float> volts = h => 13.5f - (float)h * 0.001f;
        var value = BatteryHealthMath.ComputeSelfDischarge(Rest(0, 36 + 400, volts), T0)!.Value;
        var start = volts(36 + 71.5);
        var end = volts(36 + 335.5);
        Assert.Equal((start - end) / start / 11f * 7f * 100f, value, 0.005f);
    }

    [Fact]
    public void SelfDischarge_ReadingAtTheEndOfAWindow_IsLeftOut()
    {
        // Values 12 and 14 have median 13. A reading exactly at the end of the window would move it.
        var samples = new List<Sample>();
        samples.AddRange(Rest(36 + 48, 36 + 54, h => h < 36 + 51 ? 12f : 14f));
        samples.Add(new Sample(T0.AddHours(36 + 96), 14f));
        samples.AddRange(Rest(36 + 312, 36 + 318, _ => 13f));
        Assert.Equal(0f, BatteryHealthMath.ComputeSelfDischarge(samples, T0));
    }

    [Fact]
    public void SelfDischarge_MedianEvenCount_AveragesTheMiddle()
    {
        var samples = new List<Sample>();
        samples.AddRange(Rest(36 + 48, 36 + 54, h => h < 36 + 51 ? 12f : 14f));
        samples.AddRange(Rest(36 + 312, 36 + 318, _ => 13f));
        Assert.Equal(0f, BatteryHealthMath.ComputeSelfDischarge(samples, T0));
    }

    [Theory]
    [InlineData(5, 48)]
    [InlineData(48, 5)]
    public void SelfDischarge_TooFewReadings_IsNull(int startReadings, int endReadings)
    {
        var samples = new List<Sample>();
        samples.AddRange(Rest(36 + 48, 36 + 48 + startReadings, _ => 13f));
        samples.AddRange(Rest(36 + 312, 36 + 312 + endReadings, _ => 12.9f));
        Assert.Null(BatteryHealthMath.ComputeSelfDischarge(samples, T0));
    }

    [Fact]
    public void SelfDischarge_SixReadingsEach_IsSet()
    {
        var samples = new List<Sample>();
        samples.AddRange(Rest(36 + 48, 36 + 54, _ => 13f));
        samples.AddRange(Rest(36 + 312, 36 + 318, _ => 12.9f));
        Assert.NotNull(BatteryHealthMath.ComputeSelfDischarge(samples, T0));
    }

    // Stored self-discharge per charge. The charge ends at hour 96, so day 0 is hour 132.

    [Fact]
    public void RestSelfDischarge_NeedsFifteenDaysFromDayZero()
    {
        var samples = Rest(36 + 48, 36 + 360, _ => 13f);
        Assert.Null(BatteryHealthMath.ComputeRestSelfDischarge(samples, T0, T0.AddHours(36 + 360 - 1)));
        Assert.NotNull(BatteryHealthMath.ComputeRestSelfDischarge(samples, T0, T0.AddHours(36 + 360)));
    }

    private static BatteryTrace FullChargeThenRest(int restHours) =>
        RestThenCharge().Ramp(restHours, 13f, 12.8f);

    [Fact]
    public void DetectCharges_RestShorterThanFifteenDays_HasNoSelfDischarge()
    {
        Assert.Null(Detect(FullChargeThenRest(36 + 359)).Single().SelfDischargePctPerWeek);
    }

    [Fact]
    public void DetectCharges_LongRest_HasSelfDischargeFromTheWindows()
    {
        var trace = FullChargeThenRest(36 + 365);
        var charge = Detect(trace).Single();
        Assert.NotNull(charge.SelfDischargePctPerWeek);
        Assert.Equal(BatteryHealthMath.ComputeSelfDischarge(trace.Samples, charge.EndedAt!.Value), charge.SelfDischargePctPerWeek);
    }

    [Fact]
    public void DetectCharges_NextChargeEndsTheRest_OnlyAfterThatRestQualifies()
    {
        // A short rest before the next charge and a long one after it.
        var charges = Detect(FullChargeThenRest(200).Hold(24, 14.4f).Ramp(500, 13f, 12.8f));
        Assert.Equal(2, charges.Count);
        Assert.Null(charges[0].SelfDischargePctPerWeek);
        Assert.NotNull(charges[1].SelfDischargePctPerWeek);
    }

    [Fact]
    public void DetectCharges_LongRestBeforeANextCharge_KeepsItsValue()
    {
        var charges = Detect(FullChargeThenRest(500).Hold(24, 14.4f).Hold(50, 12.8f));
        Assert.NotNull(charges[0].SelfDischargePctPerWeek);
        Assert.Null(charges[1].SelfDischargePctPerWeek);
    }

    [Fact]
    public void DetectCharges_AnOpenChargeEndsTheRest()
    {
        var charges = Detect(FullChargeThenRest(200).Hold(400, 14.4f));
        Assert.True(charges[1].IsOpen);
        Assert.Null(charges[0].SelfDischargePctPerWeek);
    }

    [Fact]
    public void DetectCharges_PartialCharge_HasNoSelfDischarge()
    {
        var charge = Detect(new BatteryTrace().Hold(72, 12.4f).Hold(6, 14.4f).Ramp(500, 13f, 12.8f)).Single();
        Assert.False(charge.IsFull);
        Assert.Null(charge.SelfDischargePctPerWeek);
    }

    [Fact]
    public void Resume_ReturnsTheSelfDischargeOfTheResumedCharge()
    {
        var trace = FullChargeThenRest(500);
        var full = Detect(trace).Single();
        var resumed = ResumeAt(trace, 72 + 24);
        Assert.NotNull(resumed.ResumeSelfDischarge);
        Assert.Equal(full.SelfDischargePctPerWeek, resumed.ResumeSelfDischarge);
    }

    // Viewer limits. A charge from hour 100 to 200, with a value measured up to hour 200 + 36 + 360.

    private static readonly BatteryHealthMath.StoredCharge Charge = new(T0.AddHours(100), T0.AddHours(200), 14.4f, true, 12.8f, 0.7f);

    private static BatteryHealthMath.OwnershipPeriod Period(double from, double? to) =>
        new(T0.AddHours(from), to == null ? null : T0.AddHours(to.Value));

    private static BatteryHealthMath.StoredCharge? Limit(params BatteryHealthMath.OwnershipPeriod[] periods) =>
        BatteryHealthMath.LimitChargeToPeriods(Charge, periods);

    [Fact]
    public void Period_IncludesItsStartAndNotItsEnd()
    {
        var period = Period(10, 20);
        Assert.True(period.Contains(T0.AddHours(10)));
        Assert.False(period.Contains(T0.AddHours(9.9)));
        Assert.True(period.Contains(T0.AddHours(19.9)));
        Assert.False(period.Contains(T0.AddHours(20)));
        Assert.True(Period(10, null).Contains(T0.AddYears(50)));
    }

    [Fact]
    public void InsideOnePeriod_BothEndsMustShareAPeriod()
    {
        var periods = new[] { Period(0, 10), Period(20, 30) };
        Assert.True(BatteryHealthMath.InsideOnePeriod(periods, T0.AddHours(1), T0.AddHours(9)));
        Assert.False(BatteryHealthMath.InsideOnePeriod(periods, T0.AddHours(1), T0.AddHours(25)));
        Assert.False(BatteryHealthMath.InsideOnePeriod(periods, T0.AddHours(11), T0.AddHours(12)));
        Assert.False(BatteryHealthMath.InsideOnePeriod(Array.Empty<BatteryHealthMath.OwnershipPeriod>(), T0, T0));
        Assert.True(BatteryHealthMath.InsideOnePeriod(null, T0, T0));
    }

    [Fact]
    public void LimitCharge_NoPeriodsMeansAnAdmin_NothingIsLimited()
    {
        Assert.Equal(Charge, BatteryHealthMath.LimitChargeToPeriods(Charge, null));
    }

    [Fact]
    public void LimitCharge_InsideAnOpenPeriod_KeepsEverything()
    {
        Assert.Equal(Charge, Limit(Period(100, null)));
    }

    [Theory]
    [InlineData(101, null)]
    [InlineData(0d, 200d)]
    [InlineData(150d, 300d)]
    public void LimitCharge_CrossingABoundary_IsLeftOut(double from, double? to)
    {
        Assert.Null(Limit(Period(from, to)));
    }

    [Fact]
    public void LimitCharge_StartInOnePeriodEndInAnother_IsLeftOut()
    {
        Assert.Null(Limit(Period(0, 150), Period(160, null)));
    }

    [Fact]
    public void LimitCharge_EndsJustInsideThePeriod_CountsWithoutSettledOrRest()
    {
        var limited = Limit(Period(100, 200.5));
        Assert.NotNull(limited);
        Assert.Null(limited.SettledVoltage);
        Assert.Null(limited.SelfDischargePctPerWeek);
        Assert.Equal(14.4f, limited.PeakVoltage);
    }

    [Theory]
    [InlineData(236, false)]
    [InlineData(236.5, true)]
    public void LimitCharge_SettledNeedsThirtySixHoursInside(double periodEnd, bool counts)
    {
        var limited = Limit(Period(100, periodEnd));
        Assert.Equal(counts, limited!.SettledVoltage.HasValue);
        Assert.Null(limited.SelfDischargePctPerWeek);
    }

    [Theory]
    [InlineData(596, false)]
    [InlineData(596.5, true)]
    public void LimitCharge_SelfDischargeNeedsTheMeasuredRestInside(double periodEnd, bool counts)
    {
        var limited = Limit(Period(100, periodEnd));
        Assert.True(limited!.SettledVoltage.HasValue);
        Assert.Equal(counts, limited.SelfDischargePctPerWeek.HasValue);
    }

    [Fact]
    public void LimitCharge_RestInsideALaterPeriod_DoesNotCount()
    {
        // The charge is in the first period and the end of its rest in the second.
        var limited = Limit(Period(100, 210), Period(220, null));
        Assert.NotNull(limited);
        Assert.Null(limited.SettledVoltage);
        Assert.Null(limited.SelfDischargePctPerWeek);
    }

    [Fact]
    public void RestingAndMinimum_WindowReachingBeforeThePeriod_AreNull()
    {
        var row = T0.AddDays(10);
        var justInsideResting = new[] { new BatteryHealthMath.OwnershipPeriod(row.AddDays(-3), null) };
        var tooLate = new[] { new BatteryHealthMath.OwnershipPeriod(row.AddDays(-3).AddMinutes(1), null) };
        Assert.Equal(12.5f, BatteryHealthMath.RestingInsidePeriods(12.5f, row, justInsideResting));
        Assert.Null(BatteryHealthMath.RestingInsidePeriods(12.5f, row, tooLate));

        var justInsideMin = new[] { new BatteryHealthMath.OwnershipPeriod(row.AddHours(-24), null) };
        var tooLateMin = new[] { new BatteryHealthMath.OwnershipPeriod(row.AddHours(-24).AddMinutes(1), null) };
        Assert.Equal(12.1f, BatteryHealthMath.Min24hInsidePeriods(12.1f, row, justInsideMin));
        Assert.Null(BatteryHealthMath.Min24hInsidePeriods(12.1f, row, tooLateMin));
    }

    [Fact]
    public void Baseline_WindowReachingBeforeThePeriod_IsNull()
    {
        var start = T0.AddDays(10);
        Assert.Equal(12.4f, BatteryHealthMath.BaselineInsidePeriods(12.4f, start, new[] { new BatteryHealthMath.OwnershipPeriod(start.AddDays(-7), null) }));
        Assert.Null(BatteryHealthMath.BaselineInsidePeriods(12.4f, start, new[] { new BatteryHealthMath.OwnershipPeriod(start.AddDays(-7).AddMinutes(1), null) }));
        Assert.Equal(12.4f, BatteryHealthMath.BaselineInsidePeriods(12.4f, start, null));
    }

    [Fact]
    public void RestingAndMinimum_Admin_AreKept()
    {
        Assert.Equal(12.5f, BatteryHealthMath.RestingInsidePeriods(12.5f, T0, null));
        Assert.Equal(12.1f, BatteryHealthMath.Min24hInsidePeriods(12.1f, T0, null));
    }

    private static BatteryHealthMath.StoredCharge Stored(int day, float settled = 12.8f, bool full = true, float? selfDischarge = null, float peak = 14.4f) =>
        new(T0.AddDays(day), T0.AddDays(day).AddHours(24), peak, full, settled, selfDischarge);

    [Fact]
    public void Summary_NoCharges_IsLearningWithNothingElse()
    {
        var s = BatteryHealthMath.SummarizeCharges(Array.Empty<BatteryHealthMath.StoredCharge>(), T0);
        Assert.Equal("learning", s.Status);
        Assert.Null(s.SlopePctPerWeek);
        Assert.Null(s.LastFullChargeAt);
        Assert.Null(s.LastFullChargePeak);
        Assert.Equal(0, s.FullChargesLast30d);
        Assert.Null(s.SelfDischargePctPerWeek);
    }

    [Fact]
    public void Summary_LastFullAndCountSkipPartialCharges()
    {
        var charges = new[] { Stored(0, peak: 14.1f), Stored(100, peak: 14.2f), Stored(120, full: false, peak: 15f) };
        var s = BatteryHealthMath.SummarizeCharges(charges, T0.AddDays(125));
        Assert.Equal(T0.AddDays(100).AddHours(24), s.LastFullChargeAt);
        Assert.Equal(14.2f, s.LastFullChargePeak);
        Assert.Equal(1, s.FullChargesLast30d);
    }

    [Fact]
    public void Summary_CountIncludesExactlyThirtyDays()
    {
        var end = T0.AddDays(100).AddHours(24);
        var charges = new[] { Stored(100) };
        Assert.Equal(1, BatteryHealthMath.SummarizeCharges(charges, end.AddDays(30)).FullChargesLast30d);
        Assert.Equal(0, BatteryHealthMath.SummarizeCharges(charges, end.AddDays(30).AddMinutes(1)).FullChargesLast30d);
    }

    [Fact]
    public void Summary_StatusUsesFullChargesWithSettledVoltage()
    {
        // The partial charge settled low. The third has no settled voltage after limiting.
        var charges = new[]
        {
            Stored(0), Stored(100), Stored(120, settled: 10f, full: false),
            Stored(140) with { SettledVoltage = null },
        };
        var s = BatteryHealthMath.SummarizeCharges(charges, T0.AddDays(150));
        Assert.Equal("good", s.Status);
        Assert.Null(s.SlopePctPerWeek);
        Assert.Equal(T0.AddDays(140).AddHours(24), s.LastFullChargeAt);
    }

    [Fact]
    public void Summary_SelfDischargeIsTheLatestChargeWithAValue()
    {
        var charges = new[] { Stored(0, selfDischarge: 0.5f), Stored(100, selfDischarge: 0.9f), Stored(120) };
        Assert.Equal(0.9f, BatteryHealthMath.SummarizeCharges(charges, T0.AddDays(150)).SelfDischargePctPerWeek);
    }
}
