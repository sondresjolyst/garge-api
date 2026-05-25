using garge_api.Helpers;
using garge_api.Models.Sensor;
using garge_api.Models.Switch;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// Verifies the shared time-range filter extracted from the four telemetry-read endpoints. The
/// resolution (timeRange precedence over explicit dates, unparseable timeRange leaving the query
/// unfiltered) and the inclusive boundary filter must match the original inline blocks for both
/// SensorData and SwitchData.
/// </summary>
public class TimeRangeQueryExtensionsTests
{
    private static DateTime T(int month) => new(2020, month, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ResolveRange_TimeRange_TakesPrecedenceAndYieldsRelativeStart()
    {
        var before = DateTime.UtcNow;
        var (start, end) = TimeRangeQueryExtensions.ResolveRange("1h", T(1), T(2));
        var after = DateTime.UtcNow;

        Assert.NotNull(start);
        Assert.Null(end); // explicit dates ignored when a valid timeRange is supplied
        Assert.InRange(start!.Value, before.AddHours(-1).AddSeconds(-2), after.AddHours(-1).AddSeconds(2));
    }

    [Fact]
    public void ResolveRange_UnparseableTimeRange_YieldsNoBounds_NotFallbackToDates()
    {
        // Mirrors the original blocks: a non-null timeRange that fails to parse left the query unfiltered
        // and did NOT fall through to startDate/endDate.
        var (start, end) = TimeRangeQueryExtensions.ResolveRange("nonsense", T(1), T(2));

        Assert.Null(start);
        Assert.Null(end);
    }

    [Fact]
    public void ResolveRange_NoTimeRange_UsesExplicitDates()
    {
        var (start, end) = TimeRangeQueryExtensions.ResolveRange(null, T(1), T(3));

        Assert.Equal(T(1), start);
        Assert.Equal(T(3), end);
    }

    [Fact]
    public void ResolveRange_Empty_NoDates_YieldsNoBounds()
    {
        var (start, end) = TimeRangeQueryExtensions.ResolveRange("", null, null);

        Assert.Null(start);
        Assert.Null(end);
    }

    [Fact]
    public void ApplyTimeRange_SensorData_FiltersInclusiveBothBounds()
    {
        var data = new[]
        {
            new SensorData { SensorId = 1, Value = "1", Timestamp = T(1) },
            new SensorData { SensorId = 1, Value = "2", Timestamp = T(2) },
            new SensorData { SensorId = 1, Value = "3", Timestamp = T(3) },
            new SensorData { SensorId = 1, Value = "4", Timestamp = T(4) },
        }.AsQueryable();

        var result = data.ApplyTimeRange(T(2), T(3)).ToList();

        Assert.Equal(new[] { "2", "3" }, result.Select(d => d.Value));
    }

    [Fact]
    public void ApplyTimeRange_SwitchData_StartOnlyAndEndOnly()
    {
        var data = new[]
        {
            new SwitchData { SwitchId = 1, Value = "on", Timestamp = T(1) },
            new SwitchData { SwitchId = 1, Value = "off", Timestamp = T(2) },
            new SwitchData { SwitchId = 1, Value = "on", Timestamp = T(3) },
        }.AsQueryable();

        Assert.Equal(2, data.ApplyTimeRange(T(2), null).Count()); // start only
        Assert.Equal(2, data.ApplyTimeRange(null, T(2)).Count()); // end only
        Assert.Equal(3, data.ApplyTimeRange(null, null).Count()); // no bounds
    }

    [Fact]
    public void ApplyTimeRange_SingleStepOverload_AppliesExplicitDates()
    {
        var data = new[]
        {
            new SensorData { SensorId = 1, Value = "1", Timestamp = T(1) },
            new SensorData { SensorId = 1, Value = "2", Timestamp = T(2) },
            new SensorData { SensorId = 1, Value = "3", Timestamp = T(3) },
        }.AsQueryable();

        var result = data.ApplyTimeRange(timeRange: null, startDate: T(2), endDate: null).ToList();

        Assert.Equal(new[] { "2", "3" }, result.Select(d => d.Value));
    }
}
