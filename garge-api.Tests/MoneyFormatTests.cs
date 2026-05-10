using garge_api.Helpers;
using Xunit;

namespace garge_api.Tests;

public class MoneyFormatTests
{
    [Theory]
    [InlineData(0, "0.00")]
    [InlineData(1, "0.01")]
    [InlineData(99, "0.99")]
    [InlineData(100, "1.00")]
    [InlineData(50000, "500.00")]
    [InlineData(123456, "1,234.56")]
    public void Nok_FormatsOreToTwoDecimalString(int ore, string expected)
    {
        Assert.Equal(expected, MoneyFormat.Nok(ore));
    }

    [Fact]
    public void Nok_UsesInvariantCulture()
    {
        var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture =
                new System.Globalization.CultureInfo("nb-NO");
            Assert.Equal("1,234.56", MoneyFormat.Nok(123456));
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = prev;
        }
    }
}
