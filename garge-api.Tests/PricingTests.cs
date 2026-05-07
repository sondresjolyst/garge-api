using garge_api.Services;
using Xunit;

namespace garge_api.Tests;

public class PricingTests
{
    [Theory]
    [InlineData(0,    false, 0)]
    [InlineData(100,  false, 100)]
    [InlineData(8000, true,  10000)]
    [InlineData(4900, true,  6125)]
    [InlineData(4902, true,  6128)]
    [InlineData(29900, true, 37375)]
    public void EffectiveInOre_AppliesVatWithMidpointAwayFromZero(int input, bool vat, int expected)
    {
        Assert.Equal(expected, Pricing.EffectiveInOre(input, vat));
    }

    [Fact]
    public void Constants_MatchVippsBasisPoints()
    {
        Assert.Equal(25, Pricing.VatPercent);
        Assert.Equal(2500, Pricing.VatBasisPoints);
    }
}
