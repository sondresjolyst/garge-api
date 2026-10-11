using garge_api.Models.Subscription;
using garge_api.Services;
using Xunit;

namespace garge_api.Tests;

public class SubscriptionPricingTests
{
    private static readonly DateTime Effective = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);

    private static Subscription Sub(int id = 1) => new() { Id = id, UserId = "u", VippsAgreementId = "a", UnitPriceInOre = 4900 };

    private static PriceChangeNotice Notice(int subId = 1, int newPrice = 5900, DateTime? effective = null,
        bool notified = true, bool applied = false, bool cancelled = false) => new()
    {
        SubscriptionId = subId,
        NotifiedAt = notified ? Effective.AddDays(-30) : null,
        EffectiveFrom = notified ? effective ?? Effective : null,
        AppliedAt = applied ? Effective : null,
        PriceChange = new PriceChange { NewPriceInOre = newPrice, EffectiveFrom = Effective, CancelledAt = cancelled ? Effective.AddDays(-1) : null },
    };

    [Fact]
    public void EarliestEffectiveFrom_IsThirtyDaysFromTheStartOfToday()
    {
        Assert.Equal(new DateTime(2026, 11, 10), SubscriptionPricing.EarliestEffectiveFrom(new DateTime(2026, 10, 11, 23, 59, 0)));
    }

    [Fact]
    public void EffectiveFor_IsTheChangesDate_WhenNoticeCameInTime()
    {
        var change = new PriceChange { EffectiveFrom = Effective };
        Assert.Equal(Effective, SubscriptionPricing.EffectiveFor(change, Effective.AddDays(-30).AddHours(20)));
    }

    [Fact]
    public void EffectiveFor_IsThirtyDaysAfterALateNotice()
    {
        var change = new PriceChange { EffectiveFrom = Effective };
        Assert.Equal(Effective.AddDays(1), SubscriptionPricing.EffectiveFor(change, Effective.AddDays(-29).AddHours(1)));
    }

    [Fact]
    public void UnitPriceFor_UsesTheAgreedPrice_BeforeTheEffectiveDate()
    {
        Assert.Equal(4900, SubscriptionPricing.UnitPriceFor(Sub(), [Notice()], Effective.AddDays(-1).AddHours(23)));
    }

    [Fact]
    public void UnitPriceFor_UsesTheNewPrice_FromTheEffectiveDate()
    {
        Assert.Equal(5900, SubscriptionPricing.UnitPriceFor(Sub(), [Notice()], Effective));
        Assert.Equal(5900, SubscriptionPricing.UnitPriceFor(Sub(), [Notice()], Effective.AddDays(3)));
    }

    [Theory]
    [InlineData(false, false, false, 1)]
    [InlineData(true, false, false, 2)]
    [InlineData(false, true, false, 1)]
    [InlineData(false, false, true, 1)]
    [InlineData(false, false, false, 2)]
    public void UnitPriceFor_IgnoresUnsentAppliedCancelledAndOtherSubscribersNotices(bool unsent, bool applied, bool cancelled, int subId)
    {
        var notice = Notice(subId: subId, notified: !unsent, applied: applied, cancelled: cancelled);
        var expected = !unsent && !applied && !cancelled && subId == 1 ? 5900 : 4900;
        Assert.Equal(expected, SubscriptionPricing.UnitPriceFor(Sub(), [notice], Effective));
    }

    [Fact]
    public void UnitPriceFor_TakesTheLatestNoticeInEffect()
    {
        var notices = new[] { Notice(newPrice: 5900), Notice(newPrice: 6900, effective: Effective.AddDays(30)) };
        Assert.Equal(5900, SubscriptionPricing.UnitPriceFor(Sub(), notices, Effective.AddDays(29)));
        Assert.Equal(6900, SubscriptionPricing.UnitPriceFor(Sub(), notices, Effective.AddDays(30)));
    }

    [Fact]
    public void CeilingUnitPrice_CoversASentIncreaseNotInEffectYet()
    {
        Assert.Equal(5900, SubscriptionPricing.CeilingUnitPrice(Sub(), [Notice()]));
    }

    [Fact]
    public void CeilingUnitPrice_IgnoresUnsentAndCancelledNotices()
    {
        Assert.Equal(4900, SubscriptionPricing.CeilingUnitPrice(Sub(), [Notice(notified: false), Notice(cancelled: true), Notice(subId: 2)]));
    }

    [Fact]
    public void CeilingUnitPrice_NeverGoesBelowTheAgreedPrice()
    {
        Assert.Equal(4900, SubscriptionPricing.CeilingUnitPrice(Sub(), [Notice(newPrice: 3900)]));
    }
}
