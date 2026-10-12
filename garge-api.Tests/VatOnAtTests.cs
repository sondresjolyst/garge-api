using garge_api.Models.Admin;
using Xunit;

namespace garge_api.Tests;

public class VatOnAtTests
{
    private static readonly DateTime Registered = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void VatOff_CoversNoSale() =>
        Assert.False(new AppSettings { VatEnabled = false, VatRegisteredFrom = Registered }.VatOnAt(Registered.AddDays(1)));

    [Fact]
    public void VatOnWithoutADate_CoversEverySale() =>
        Assert.True(new AppSettings { VatEnabled = true }.VatOnAt(DateTime.MinValue));

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void VatOnFromADate_CoversSalesFromThatMoment(int seconds, bool expected) =>
        Assert.Equal(expected, new AppSettings { VatEnabled = true, VatRegisteredFrom = Registered }.VatOnAt(Registered.AddSeconds(seconds)));
}
