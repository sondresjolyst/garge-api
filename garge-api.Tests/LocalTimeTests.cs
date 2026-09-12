using garge_api.Services;
using Xunit;

namespace garge_api.Tests;

public class LocalTimeTests
{
    [Theory]
    [InlineData("2026-09-12T13:26:00Z", "2026-09-12 15:26")] // summer time, UTC+2
    [InlineData("2026-01-15T13:26:00Z", "2026-01-15 14:26")] // standard time, UTC+1
    public void Format_RendersNorwegianLocalTime(string utc, string expected) =>
        Assert.Equal(expected, LocalTime.Format(DateTime.Parse(utc).ToUniversalTime()));

    [Fact]
    public void Format_HonoursACustomFormat() =>
        Assert.Equal("15:26", LocalTime.Format(DateTime.Parse("2026-09-12T13:26:00Z").ToUniversalTime(), "HH:mm"));
}
