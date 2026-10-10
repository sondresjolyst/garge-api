using garge_api.Services;
using Xunit;

namespace garge_api.Tests;

public class PricingTests
{
    [Theory]
    [InlineData(20000, 25, 16000, 4000)]   // 200 kr: 160 kr plus 40 kr VAT
    [InlineData(37375, 25, 29900, 7475)]
    [InlineData(29900, 25, 23920, 5980)]
    [InlineData(1, 25, 1, 0)]
    [InlineData(3, 25, 2, 1)]
    [InlineData(0, 25, 0, 0)]
    [InlineData(29900, 0, 29900, 0)]
    public void Split_TakesTheVatOutOfThePrice(int price, int vatPercent, int exclVat, int vat)
    {
        Assert.Equal((exclVat, vat), Pricing.Split(price, vatPercent));
    }

    [Fact]
    public void Split_PartsAlwaysAddUpAndTheVatIsWithinHalfAnOre()
    {
        for (var price = 0; price <= 200_000; price += 7)
        {
            var (exclVat, vat) = Pricing.Split(price, 25);
            Assert.Equal(price, exclVat + vat);
            Assert.InRange(vat - price * 25m / 125, -0.5m, 0.5m);
        }
    }

    [Fact]
    public void VatPercentFor_FollowsTheSetting()
    {
        Assert.Equal(25, Pricing.VatPercentFor(true));
        Assert.Equal(0, Pricing.VatPercentFor(false));
    }

    [Fact]
    public void Constants_MatchVippsBasisPoints()
    {
        Assert.Equal(25, Pricing.VatPercent);
        Assert.Equal(2500, Pricing.VatBasisPoints);
    }
}
