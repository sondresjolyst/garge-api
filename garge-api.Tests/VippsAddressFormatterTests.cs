using garge_api.Services;
using Xunit;

namespace garge_api.Tests;

public class VippsAddressFormatterTests
{
    [Fact]
    public void Format_NullAddress_ReturnsNull()
    {
        Assert.Null(VippsAddressFormatter.Format(null));
    }

    [Fact]
    public void Format_PrefersFormattedField()
    {
        var address = new VippsAddress
        {
            Formatted = "Mårvegen 21a, 4347 Lye, Norway",
            StreetAddress = "ignored",
            PostalCode = "ignored",
            Region = "ignored",
            Country = "ignored"
        };

        Assert.Equal("Mårvegen 21a, 4347 Lye, Norway", VippsAddressFormatter.Format(address));
    }

    [Fact]
    public void Format_FallsBackToJoinedParts_WhenFormattedMissing()
    {
        var address = new VippsAddress
        {
            StreetAddress = "Mårvegen 21a",
            PostalCode = "4347",
            Region = "Lye",
            Country = "NO"
        };

        Assert.Equal("Mårvegen 21a, 4347, Lye, NO", VippsAddressFormatter.Format(address));
    }

    [Fact]
    public void Format_SkipsNullAndEmptyParts()
    {
        var address = new VippsAddress
        {
            StreetAddress = "Mårvegen 21a",
            PostalCode = null,
            Region = "",
            Country = "NO"
        };

        Assert.Equal("Mårvegen 21a, NO", VippsAddressFormatter.Format(address));
    }

    [Fact]
    public void Format_AllPartsEmpty_ReturnsNull()
    {
        var address = new VippsAddress
        {
            StreetAddress = null,
            PostalCode = "",
            Region = null,
            Country = ""
        };

        Assert.Null(VippsAddressFormatter.Format(address));
    }

    [Fact]
    public void Format_TruncatesToMaxLength()
    {
        var address = new VippsAddress { Formatted = new string('x', 600) };

        var result = VippsAddressFormatter.Format(address, maxLength: 500);

        Assert.NotNull(result);
        Assert.Equal(500, result!.Length);
    }

    [Fact]
    public void Format_WithinMaxLength_NotTruncated()
    {
        var address = new VippsAddress { Formatted = new string('x', 100) };

        Assert.Equal(new string('x', 100), VippsAddressFormatter.Format(address, maxLength: 500));
    }
}
