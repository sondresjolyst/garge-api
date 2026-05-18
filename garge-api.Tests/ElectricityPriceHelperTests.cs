using garge_api.Helpers;
using Xunit;

namespace garge_api.Tests;

public class ElectricityPriceHelperTests
{
    [Theory]
    [InlineData("NO1", 0.25)]
    [InlineData("NO2", 0.25)]
    [InlineData("NO3", 0.25)]
    [InlineData("NO5", 0.25)]
    [InlineData("no2", 0.25)]
    [InlineData("NO4", 0.0)]
    [InlineData("no4", 0.0)]
    [InlineData("SE1", 0.0)]
    [InlineData("", 0.0)]
    [InlineData(null, 0.0)]
    public void GetVatRate_ReturnsExpectedRatePerArea(string? area, double expected)
    {
        var rate = ElectricityPriceHelper.GetVatRate(area);
        Assert.Equal((decimal)expected, rate);
    }

    [Theory]
    [InlineData("NO2", 2.0, 2.5)]
    [InlineData("NO4", 2.0, 2.0)]
    [InlineData("SE1", 2.0, 2.0)]
    public void ToGross_MultipliesSpotByOnePlusVat(string area, double spot, double expectedGross)
    {
        var gross = ElectricityPriceHelper.ToGross((decimal)spot, area);
        Assert.Equal((decimal)expectedGross, gross);
    }
}
