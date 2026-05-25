using garge_api.Helpers;
using Xunit;

namespace garge_api.Tests;

public class TimeRangeParserTests
{
    [Theory]
    [InlineData("5m", 5 * 60)]
    [InlineData("1h", 60 * 60)]
    [InlineData("2d", 2 * 24 * 60 * 60)]
    [InlineData("1w", 7 * 24 * 60 * 60)]
    [InlineData("1y", 365 * 24 * 60 * 60)]
    public void Parse_ValidRange_ReturnsExpectedSeconds(string input, double expectedSeconds)
    {
        var result = TimeRangeParser.Parse(input);

        Assert.NotNull(result);
        Assert.Equal(expectedSeconds, result!.Value.TotalSeconds);
    }

    [Theory]
    [InlineData("5M", 5 * 60)]
    [InlineData("1H", 60 * 60)]
    public void Parse_UppercaseUnit_IsCaseInsensitive(string input, double expectedSeconds)
    {
        var result = TimeRangeParser.Parse(input);

        Assert.NotNull(result);
        Assert.Equal(expectedSeconds, result!.Value.TotalSeconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("m")]
    [InlineData("5x")]
    [InlineData("xh")]
    [InlineData("5")]
    public void Parse_InvalidRange_ReturnsNull(string input)
    {
        Assert.Null(TimeRangeParser.Parse(input));
    }
}
