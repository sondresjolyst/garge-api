using garge_api.Helpers;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// Verifies the throttle that keeps per-reading work, such as battery analysis, from running on
/// every reading a sensor sends.
/// </summary>
public class PerKeyThrottleTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>A clock the test moves by hand.</summary>
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    [Fact]
    public void FirstCallForAKeyIsAllowed()
    {
        var throttle = new PerKeyThrottle(Interval, new TestClock(Start));

        Assert.True(throttle.TryEnter(1));
    }

    [Fact]
    public void RepeatWithinIntervalIsRejected()
    {
        var clock = new TestClock(Start);
        var throttle = new PerKeyThrottle(Interval, clock);
        throttle.TryEnter(1);

        clock.Advance(Interval - TimeSpan.FromSeconds(1));

        Assert.False(throttle.TryEnter(1));
    }

    [Fact]
    public void RepeatAfterIntervalIsAllowed()
    {
        var clock = new TestClock(Start);
        var throttle = new PerKeyThrottle(Interval, clock);
        throttle.TryEnter(1);

        clock.Advance(Interval);

        Assert.True(throttle.TryEnter(1));
    }

    [Fact]
    public void RejectedCallDoesNotExtendTheWindow()
    {
        var clock = new TestClock(Start);
        var throttle = new PerKeyThrottle(Interval, clock);
        throttle.TryEnter(1);
        clock.Advance(TimeSpan.FromMinutes(10));
        throttle.TryEnter(1);

        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.True(throttle.TryEnter(1));
    }

    [Fact]
    public void KeysAreThrottledIndependently()
    {
        var throttle = new PerKeyThrottle(Interval, new TestClock(Start));
        throttle.TryEnter(1);

        Assert.True(throttle.TryEnter(2));
    }
}
