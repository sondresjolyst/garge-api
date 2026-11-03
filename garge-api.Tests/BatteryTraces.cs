using System.Globalization;
using garge_api.Helpers;

namespace garge_api.Tests;

/// <summary>Builds voltage traces for the battery tests, hourly by default.</summary>
internal sealed class BatteryTrace
{
    public static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public List<BatteryHealthMath.VoltageSample> Samples { get; } = new();

    private double _hours;

    public double Hours => _hours;

    /// <summary>One reading per hour at the given voltage.</summary>
    public BatteryTrace Hold(int hours, float volts)
    {
        for (var i = 0; i < hours; i++) Add(volts);
        return this;
    }

    /// <summary>One reading per value, one hour apart.</summary>
    public BatteryTrace Values(params float[] volts)
    {
        foreach (var v in volts) Add(v);
        return this;
    }

    /// <summary>Hourly readings moving evenly from one voltage to another.</summary>
    public BatteryTrace Ramp(int hours, float from, float to)
    {
        for (var i = 0; i < hours; i++) Add(from + (to - from) * i / Math.Max(1, hours - 1));
        return this;
    }

    /// <summary>Time passes with no readings. The next reading comes after the gap plus one hour.</summary>
    public BatteryTrace Gap(double hours)
    {
        _hours += hours;
        return this;
    }

    private void Add(float volts)
    {
        Samples.Add(new BatteryHealthMath.VoltageSample(T0.AddHours(_hours), volts));
        _hours += 1;
    }
}

/// <summary>
/// Readings from real batteries, as hours since the first reading and volts,
/// for the shapes that synthetic traces do not cover well.
/// </summary>
internal static class BatteryFixtures
{
    public const string LongRestSlowRamp = "long-rest-slow-ramp";
    public const string SuddenDrop = "sudden-drop";
    public const string TwoCloseCharges = "two-close-charges";
    public const string RegularCycles = "regular-cycles";
    public const string LongFloat = "long-float";

    public static IReadOnlyList<(double Hours, float Volts)> Read(string name) =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Battery", name + ".csv"))
            .Select(line => line.Split(','))
            .Select(p => (double.Parse(p[0], CultureInfo.InvariantCulture), float.Parse(p[1], CultureInfo.InvariantCulture)))
            .ToList();

    public static List<BatteryHealthMath.VoltageSample> Samples(string name) =>
        Read(name).Select(r => new BatteryHealthMath.VoltageSample(BatteryTrace.T0.AddHours(r.Hours), (float)r.Volts)).ToList();
}
