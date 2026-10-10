using garge_api.Models.Subscription;
using garge_api.Services;
using Xunit;

namespace garge_api.Tests;

public class SubscriptionChargesTests
{
    private static DateTime D(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, "charge-12-638000000000000000")]
    [InlineData(1, "charge-12-638000000000000000-r1")]
    [InlineData(2, "charge-12-638000000000000000-r2")]
    public void Key_RoundTripsThroughTryParse(int attempt, string expected)
    {
        var due = new DateTime(638000000000000000, DateTimeKind.Utc);
        var key = SubscriptionCharges.Key(12, due, attempt);

        Assert.Equal(expected, key);
        Assert.True(SubscriptionCharges.TryParse(key, out var sub, out var parsedDue, out var parsedAttempt));
        Assert.Equal(12, sub);
        Assert.Equal(due, parsedDue);
        Assert.Equal(attempt, parsedAttempt);
    }

    [Fact]
    public void Key_FitsTheVippsOrderIdRules()
    {
        // orderId: 1 to 64 characters of letters, digits and hyphens.
        var key = SubscriptionCharges.Key(int.MaxValue, DateTime.MaxValue, SubscriptionCharges.MaxAttempts);
        Assert.InRange(key.Length, 1, 50);
        Assert.Matches("^[a-zA-Z0-9-]+$", key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("chg_abc")]
    [InlineData("82ce990f-d08a-448c-bd26-ee6be8418d06")]
    [InlineData("charge-12")]
    [InlineData("charge-x-638000000000000000")]
    [InlineData("charge-12-638000000000000000-r0")]
    [InlineData("charge-12-638000000000000000-r")]
    [InlineData("charge-12-638000000000000000-x1")]
    [InlineData("charge-12-99999999999999999999")]
    [InlineData("charge-99999999999-638000000000000000")]
    [InlineData(" charge-12-638000000000000000")]
    [InlineData("charge-12-638000000000000000\n")]
    [InlineData("charge--12-638000000000000000")]
    [InlineData("charge-١٢-638000000000000000")]
    public void TryParse_RejectsAnythingThatIsNotAChargeKey(string? chargeId)
    {
        Assert.False(SubscriptionCharges.TryParse(chargeId, out _, out _, out _));
    }

    [Fact]
    public void NextDue_Monthly_KeepsTheDayOfTheMonthAfterAShortMonth()
    {
        var anchor = D(2026, 1, 31);
        var feb = SubscriptionCharges.NextDue(anchor, D(2026, 1, 31), BillingInterval.Monthly);
        var mar = SubscriptionCharges.NextDue(anchor, feb, BillingInterval.Monthly);
        var apr = SubscriptionCharges.NextDue(anchor, mar, BillingInterval.Monthly);

        Assert.Equal(D(2026, 2, 28), feb);
        Assert.Equal(D(2026, 3, 31), mar);
        Assert.Equal(D(2026, 4, 30), apr);
    }

    [Fact]
    public void NextDue_Monthly_DoesNotDriftWhenTheChargeIsCapturedLate()
    {
        // Captured five days late, after Vipps' retries. The next charge is still on the 5th.
        var next = SubscriptionCharges.NextDue(D(2026, 1, 5), D(2026, 3, 5), BillingInterval.Monthly);
        Assert.Equal(D(2026, 4, 5), next);
    }

    [Fact]
    public void NextDue_Monthly_IsAlwaysAfterTheGivenDate()
    {
        var anchor = D(2025, 8, 29);
        for (var day = D(2025, 8, 29); day < D(2028, 1, 1); day = day.AddDays(1))
        {
            var next = SubscriptionCharges.NextDue(anchor, day, BillingInterval.Monthly);
            Assert.True(next > day, $"{next:yyyy-MM-dd} after {day:yyyy-MM-dd}");
            Assert.True(next <= day.AddMonths(1).AddDays(3), $"{next:yyyy-MM-dd} too far after {day:yyyy-MM-dd}");
            Assert.True(next.Day == 29 || next.Day == DateTime.DaysInMonth(next.Year, next.Month), $"{next:yyyy-MM-dd}");
        }
    }

    [Fact]
    public void NextDue_Yearly_FromALeapDay()
    {
        var anchor = D(2028, 2, 29);
        Assert.Equal(D(2029, 2, 28), SubscriptionCharges.NextDue(anchor, D(2028, 2, 29), BillingInterval.Yearly));
        Assert.Equal(D(2032, 2, 29), SubscriptionCharges.NextDue(anchor, D(2031, 2, 28), BillingInterval.Yearly));
    }

    [Fact]
    public void NextDue_IgnoresTheTimeOfDay()
    {
        var next = SubscriptionCharges.NextDue(new DateTime(2026, 1, 5, 23, 59, 0, DateTimeKind.Utc),
            new DateTime(2026, 2, 5, 13, 0, 0, DateTimeKind.Utc), BillingInterval.Monthly);
        Assert.Equal(D(2026, 3, 5), next);
        Assert.Equal(DateTimeKind.Utc, next.Kind);
    }
}
