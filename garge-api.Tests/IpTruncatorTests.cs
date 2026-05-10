using garge_api.Helpers;
using Xunit;

namespace garge_api.Tests;

public class IpTruncatorTests
{
    [Theory]
    [InlineData("192.0.2.42", "192.0.2.0")]
    [InlineData("10.0.0.1", "10.0.0.0")]
    [InlineData("203.0.113.255", "203.0.113.0")]
    public void Truncate_Ipv4_ZerosLastOctet(string input, string expected)
    {
        Assert.Equal(expected, IpTruncator.Truncate(input));
    }

    [Fact]
    public void Truncate_Ipv6_ZerosLast80Bits()
    {
        // /48 keeps the first 3 hextets (48 bits), zeros the rest
        var result = IpTruncator.Truncate("2001:db8:abcd:1234:5678:9abc:def0:1111");
        Assert.Equal("2001:db8:abcd::", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("not-an-ip")]
    [InlineData("999.999.999.999")]
    public void Truncate_InvalidInput_ReturnsEmpty(string? input)
    {
        Assert.Equal(string.Empty, IpTruncator.Truncate(input));
    }
}
