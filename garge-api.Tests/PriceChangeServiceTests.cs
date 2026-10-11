using garge_api.Models;
using garge_api.Models.Subscription;
using garge_api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// Price increases with notice under digitalytelsesloven § 28. Every subscriber is told, the Vipps
/// ceiling goes up before they are, and nobody pays the new price sooner than 30 days after their notice.
/// </summary>
public class PriceChangeServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Today = Start.UtcDateTime.Date;

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private sealed class Harness
    {
        public required ApplicationDbContext Db { get; init; }
        public required PriceChangeService Service { get; init; }
        public required Mock<IVippsService> Vipps { get; init; }
        public required Mock<ISubscriptionEmailService> Email { get; init; }
        public required TestClock Clock { get; init; }
    }

    private static Harness Create(bool testConfigured = true)
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var vipps = new Mock<IVippsService>();
        var email = new Mock<ISubscriptionEmailService>();
        email.Setup(e => e.SendPriceChangeNoticeAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>()))
            .ReturnsAsync(true);
        var clock = new TestClock(Start);
        var settings = Options.Create(new VippsSettings
        {
            BaseUrl = "https://api.vipps.no",
            ClientId = "id", ClientSecret = "s", SubscriptionKey = "k", MerchantSerialNumber = "msn-prod",
            TestClientId = testConfigured ? "tid" : "", TestClientSecret = "ts", TestSubscriptionKey = "tk", TestMerchantSerialNumber = "msn-test"
        });
        var service = new PriceChangeService(db, vipps.Object, email.Object, settings, clock, NullLogger<PriceChangeService>.Instance);
        return new Harness { Db = db, Service = service, Vipps = vipps, Email = email, Clock = clock };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Product> AddProductAsync(Harness h, int price = 4900, BillingInterval interval = BillingInterval.Monthly)
    {
        var product = new Product { Id = 1, Name = "Garge Extra Sensor", PriceInOre = price, Interval = interval, Type = ProductType.AddOn, IsActive = true };
        h.Db.Products.Add(product);
        await h.Db.SaveChangesAsync(Ct);
        return product;
    }

    private static async Task<Subscription> AddSubAsync(Harness h, int unitPrice = 4900, int quantity = 1,
        SubscriptionStatus status = SubscriptionStatus.Active, bool isTest = false, string agreement = "agr")
    {
        var sub = new Subscription
        {
            UserId = "user-1", ProductId = 1, UnitPriceInOre = unitPrice, Quantity = quantity,
            VippsAgreementId = agreement, Status = status, IsTest = isTest
        };
        h.Db.Subscriptions.Add(sub);
        await h.Db.SaveChangesAsync(Ct);
        return sub;
    }

    private static Task<(PriceChange? Change, string? Error)> ScheduleAsync(Harness h, int newPrice = 5900, int inDays = 30) =>
        h.Service.ScheduleAsync(1, newPrice, Today.AddDays(inDays), "admin-1", Ct);

    [Fact]
    public async Task Schedule_AnIncreaseThirtyDaysOut_IsSavedWithTheOldPrice()
    {
        var h = Create();
        await AddProductAsync(h);

        var (change, error) = await ScheduleAsync(h);

        Assert.Null(error);
        Assert.NotNull(change);
        Assert.Equal(4900, change.OldPriceInOre);
        Assert.Equal(5900, change.NewPriceInOre);
        Assert.Equal(Today.AddDays(30), change.EffectiveFrom);
        Assert.Equal(DateTimeKind.Utc, change.EffectiveFrom.Kind);
        Assert.Equal("admin-1", change.CreatedByUserId);
        // The product keeps its price until the effective date.
        Assert.Equal(4900, (await h.Db.Products.SingleAsync(Ct)).PriceInOre);
    }

    [Fact]
    public async Task Schedule_StoresTheEffectiveDateAsAUtcDate()
    {
        var h = Create();
        await AddProductAsync(h);

        var (change, _) = await h.Service.ScheduleAsync(1, 5900, DateTime.SpecifyKind(Today.AddDays(31).AddHours(15), DateTimeKind.Unspecified), null, Ct);

        Assert.Equal(Today.AddDays(31), change!.EffectiveFrom);
        Assert.Equal(DateTimeKind.Utc, change.EffectiveFrom.Kind);
    }

    [Fact]
    public async Task Schedule_LessThanThirtyDaysOut_IsRefused()
    {
        var h = Create();
        await AddProductAsync(h);

        var (change, error) = await ScheduleAsync(h, inDays: 29);

        Assert.Null(change);
        Assert.Contains(Today.AddDays(30).ToString("yyyy-MM-dd"), error);
        Assert.Empty(h.Db.PriceChanges);
    }

    [Theory]
    [InlineData(4900)]
    [InlineData(3900)]
    public async Task Schedule_ASamePriceOrADecrease_IsRefused(int newPrice)
    {
        var h = Create();
        await AddProductAsync(h);

        var (change, error) = await ScheduleAsync(h, newPrice);

        Assert.Null(change);
        Assert.Contains("increase", error);
    }

    [Fact]
    public async Task Schedule_WhileAnotherChangeIsPending_IsRefused()
    {
        var h = Create();
        await AddProductAsync(h);
        await ScheduleAsync(h);

        var (change, error) = await ScheduleAsync(h, 6900, 40);

        Assert.Null(change);
        Assert.Contains("already has a scheduled price change", error);
    }

    [Fact]
    public async Task Schedule_AfterTheLastChangeWasCancelled_IsAllowed()
    {
        var h = Create();
        await AddProductAsync(h);
        var (first, _) = await ScheduleAsync(h);
        await h.Service.CancelAsync(first!.Id, Ct);

        var (change, error) = await ScheduleAsync(h, 6900, 40);

        Assert.Null(error);
        Assert.NotNull(change);
    }

    [Fact]
    public async Task Schedule_WhenASubscribersTotalWouldPassTheVippsLimit_IsRefused()
    {
        var h = Create();
        await AddProductAsync(h);
        await AddSubAsync(h, quantity: 50);

        // 40 001 øre times 50 is just above 20 000 kr. 40 000 times 50 is the limit itself.
        var (refused, error) = await ScheduleAsync(h, 40_001);
        var (allowed, _) = await ScheduleAsync(h, 40_000);

        Assert.Null(refused);
        Assert.Contains("20 000 kr", error);
        Assert.NotNull(allowed);
    }

    [Fact]
    public async Task Schedule_AnUnknownProduct_IsRefused()
    {
        var h = Create();
        var (change, error) = await h.Service.ScheduleAsync(99, 5900, Today.AddDays(30), null, Ct);
        Assert.Null(change);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Sweep_NotifiesActiveSubscribersBelowTheNewPrice_AfterRaisingTheirCeiling()
    {
        var h = Create();
        await AddProductAsync(h);
        var sub = await AddSubAsync(h, quantity: 3);
        var (change, _) = await ScheduleAsync(h, inDays: 45);

        var order = new List<string>();
        h.Vipps.Setup(v => v.UpdateAgreementMaxAmountAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool?>()))
            .Callback(() => order.Add("ceiling")).Returns(Task.CompletedTask);
        h.Email.Setup(e => e.SendPriceChangeNoticeAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>()))
            .Callback(() => order.Add("email")).ReturnsAsync(true);

        await h.Service.SweepAsync(Ct);

        Assert.Equal(["ceiling", "email"], order);
        h.Vipps.Verify(v => v.UpdateAgreementMaxAmountAsync("agr", 5900 * 3, $"price-{change!.Id}-{sub.Id}-3", false), Times.Once);
        h.Email.Verify(e => e.SendPriceChangeNoticeAsync(sub.Id, 4900, 5900, Today.AddDays(45)), Times.Once);
        var notice = await h.Db.PriceChangeNotices.SingleAsync(Ct);
        Assert.Equal(Start.UtcDateTime, notice.NotifiedAt);
        Assert.Equal(Today.AddDays(45), notice.EffectiveFrom);
        Assert.Null(notice.AppliedAt);
        // The agreed price stays until the effective date.
        Assert.Equal(4900, (await h.Db.Subscriptions.SingleAsync(Ct)).UnitPriceInOre);
    }

    [Fact]
    public async Task Sweep_TwiceInARow_NotifiesOnce()
    {
        var h = Create();
        await AddProductAsync(h);
        await AddSubAsync(h);
        await ScheduleAsync(h);

        await h.Service.SweepAsync(Ct);
        await h.Service.SweepAsync(Ct);

        h.Email.Verify(e => e.SendPriceChangeNoticeAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>()), Times.Once);
        h.Vipps.Verify(v => v.UpdateAgreementMaxAmountAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool?>()), Times.Once);
        Assert.Single(h.Db.PriceChangeNotices);
    }

    [Fact]
    public async Task Sweep_SkipsSubscribersNotActiveOrAlreadyAtTheNewPrice()
    {
        var h = Create();
        await AddProductAsync(h);
        await AddSubAsync(h, status: SubscriptionStatus.Pending, agreement: "pending");
        await AddSubAsync(h, status: SubscriptionStatus.Stopped, agreement: "stopped");
        await AddSubAsync(h, unitPrice: 5900, agreement: "at-price");
        var active = await AddSubAsync(h, agreement: "active");
        await ScheduleAsync(h);

        await h.Service.SweepAsync(Ct);

        var notice = await h.Db.PriceChangeNotices.SingleAsync(Ct);
        Assert.Equal(active.Id, notice.SubscriptionId);
    }

    [Fact]
    public async Task Sweep_ASubscriberWhoActivatesDuringTheNoticePeriod_IsNotifiedThen()
    {
        var h = Create();
        await AddProductAsync(h);
        var sub = await AddSubAsync(h, status: SubscriptionStatus.Pending);
        await ScheduleAsync(h, inDays: 40);
        await h.Service.SweepAsync(Ct);
        Assert.Empty(h.Db.PriceChangeNotices);

        h.Clock.Advance(TimeSpan.FromDays(20));
        sub.Status = SubscriptionStatus.Active;
        await h.Db.SaveChangesAsync(Ct);
        await h.Service.SweepAsync(Ct);

        // Told on day 20, so the new price reaches them on day 50, not on the change's day 40.
        var notice = await h.Db.PriceChangeNotices.SingleAsync(Ct);
        Assert.Equal(Today.AddDays(50), notice.EffectiveFrom);
        h.Email.Verify(e => e.SendPriceChangeNoticeAsync(sub.Id, 4900, 5900, Today.AddDays(50)), Times.Once);
    }

    [Fact]
    public async Task Sweep_WhenRaisingTheCeilingFails_SendsNoNotice_AndRetriesNextTime()
    {
        var h = Create();
        await AddProductAsync(h);
        await AddSubAsync(h);
        await ScheduleAsync(h);
        h.Vipps.SetupSequence(v => v.UpdateAgreementMaxAmountAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool?>()))
            .ThrowsAsync(new HttpRequestException("down"))
            .Returns(Task.CompletedTask);

        await h.Service.SweepAsync(Ct);

        h.Email.Verify(e => e.SendPriceChangeNoticeAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>()), Times.Never);
        Assert.Null((await h.Db.PriceChangeNotices.SingleAsync(Ct)).NotifiedAt);

        await h.Service.SweepAsync(Ct);

        Assert.NotNull((await h.Db.PriceChangeNotices.SingleAsync(Ct)).NotifiedAt);
    }

    [Fact]
    public async Task Sweep_WhenTheEmailFails_LeavesTheNoticeUnsent_AndRetriesNextTime()
    {
        var h = Create();
        await AddProductAsync(h);
        await AddSubAsync(h);
        await ScheduleAsync(h, inDays: 30);
        h.Email.SetupSequence(e => e.SendPriceChangeNoticeAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>()))
            .ReturnsAsync(false)
            .ReturnsAsync(true);

        await h.Service.SweepAsync(Ct);
        var notice = await h.Db.PriceChangeNotices.SingleAsync(Ct);
        Assert.Null(notice.NotifiedAt);
        Assert.Null(notice.EffectiveFrom);

        h.Clock.Advance(TimeSpan.FromDays(1));
        await h.Service.SweepAsync(Ct);

        // A day late, so the 30 days count from the day it was sent.
        notice = await h.Db.PriceChangeNotices.SingleAsync(Ct);
        Assert.Equal(Today.AddDays(31), notice.EffectiveFrom);
    }

    [Fact]
    public async Task Sweep_SkipsSubscribersInAnEnvironmentWithoutVippsCredentials()
    {
        var h = Create(testConfigured: false);
        await AddProductAsync(h);
        await AddSubAsync(h, isTest: true);
        await ScheduleAsync(h);

        await h.Service.SweepAsync(Ct);

        h.Vipps.Verify(v => v.UpdateAgreementMaxAmountAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool?>()), Times.Never);
        Assert.Null((await h.Db.PriceChangeNotices.SingleAsync(Ct)).NotifiedAt);
    }

    [Fact]
    public async Task Sweep_OnTheEffectiveDate_MovesSubscriberAndProductToTheNewPrice()
    {
        var h = Create();
        await AddProductAsync(h);
        await AddSubAsync(h);
        var (change, _) = await ScheduleAsync(h);
        await h.Service.SweepAsync(Ct);

        h.Clock.Advance(TimeSpan.FromDays(29));
        await h.Service.SweepAsync(Ct);
        Assert.Equal(4900, (await h.Db.Subscriptions.SingleAsync(Ct)).UnitPriceInOre);
        Assert.Equal(4900, (await h.Db.Products.SingleAsync(Ct)).PriceInOre);

        h.Clock.Advance(TimeSpan.FromDays(1));
        await h.Service.SweepAsync(Ct);

        Assert.Equal(5900, (await h.Db.Subscriptions.SingleAsync(Ct)).UnitPriceInOre);
        Assert.Equal(5900, (await h.Db.Products.SingleAsync(Ct)).PriceInOre);
        Assert.NotNull((await h.Db.PriceChangeNotices.SingleAsync(Ct)).AppliedAt);
        Assert.NotNull((await h.Db.PriceChanges.SingleAsync(c => c.Id == change!.Id, Ct)).AppliedAt);
    }

    [Fact]
    public async Task Sweep_AfterTheProductHasTheNewPrice_DoesNotNotifyNewSubscribers()
    {
        var h = Create();
        await AddProductAsync(h);
        await ScheduleAsync(h);
        h.Clock.Advance(TimeSpan.FromDays(30));
        await h.Service.SweepAsync(Ct);

        // A subscriber who signed up at the old price before the change took effect keeps it.
        await AddSubAsync(h);
        await h.Service.SweepAsync(Ct);

        Assert.Empty(h.Db.PriceChangeNotices);
    }

    [Fact]
    public async Task Sweep_ACancelledChange_NotifiesNobody_AndChangesNoPrice()
    {
        var h = Create();
        await AddProductAsync(h);
        await AddSubAsync(h);
        var (change, _) = await ScheduleAsync(h);
        await h.Service.CancelAsync(change!.Id, Ct);

        h.Clock.Advance(TimeSpan.FromDays(31));
        await h.Service.SweepAsync(Ct);

        Assert.Empty(h.Db.PriceChangeNotices);
        Assert.Equal(4900, (await h.Db.Subscriptions.SingleAsync(Ct)).UnitPriceInOre);
        Assert.Equal(4900, (await h.Db.Products.SingleAsync(Ct)).PriceInOre);
    }

    [Fact]
    public async Task Cancel_BeforeAFailedNoticeIsRetried_SendsNoNotice()
    {
        var h = Create();
        await AddProductAsync(h);
        await AddSubAsync(h);
        var (change, _) = await ScheduleAsync(h);
        h.Email.Setup(e => e.SendPriceChangeNoticeAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>()))
            .ReturnsAsync(false);
        await h.Service.SweepAsync(Ct);
        await h.Service.CancelAsync(change!.Id, Ct);
        h.Email.Invocations.Clear();
        h.Vipps.Invocations.Clear();

        await h.Service.SweepAsync(Ct);

        Assert.Empty(h.Email.Invocations);
        Assert.Empty(h.Vipps.Invocations);
    }

    [Fact]
    public async Task Cancel_AfterNoticesWentOut_StopsTheIncrease()
    {
        var h = Create();
        await AddProductAsync(h);
        await AddSubAsync(h);
        var (change, _) = await ScheduleAsync(h);
        await h.Service.SweepAsync(Ct);

        Assert.Null(await h.Service.CancelAsync(change!.Id, Ct));

        h.Clock.Advance(TimeSpan.FromDays(31));
        await h.Service.SweepAsync(Ct);
        Assert.Equal(4900, (await h.Db.Subscriptions.SingleAsync(Ct)).UnitPriceInOre);
        Assert.Null((await h.Db.PriceChangeNotices.SingleAsync(Ct)).AppliedAt);
    }

    [Fact]
    public async Task Cancel_AfterASubscriberPaysTheNewPrice_IsRefused()
    {
        var h = Create();
        await AddProductAsync(h);
        await AddSubAsync(h);
        var (change, _) = await ScheduleAsync(h, inDays: 30);
        await h.Service.SweepAsync(Ct);
        h.Clock.Advance(TimeSpan.FromDays(30));
        await h.Service.SweepAsync(Ct);

        var error = await h.Service.CancelAsync(change!.Id, Ct);

        Assert.Contains("taken effect", error);
        Assert.Null((await h.Db.PriceChanges.SingleAsync(Ct)).CancelledAt);
    }

    [Fact]
    public async Task Cancel_Twice_IsRefusedTheSecondTime()
    {
        var h = Create();
        await AddProductAsync(h);
        var (change, _) = await ScheduleAsync(h);

        Assert.Null(await h.Service.CancelAsync(change!.Id, Ct));
        Assert.Contains("already cancelled", await h.Service.CancelAsync(change.Id, Ct));
        Assert.NotNull(await h.Service.CancelAsync(999, Ct));
    }

    [Theory]
    [InlineData(SubscriptionStatus.Active)]
    [InlineData(SubscriptionStatus.Pending)]
    public async Task DirectEdit_AnIncreaseWithSubscribers_IsRefused(SubscriptionStatus status)
    {
        var h = Create();
        var product = await AddProductAsync(h);
        await AddSubAsync(h, status: status);

        var error = await h.Service.PrepareDirectEditAsync(product, 5900, BillingInterval.Monthly, Ct);

        Assert.Contains("Schedule a price change", error);
    }

    [Fact]
    public async Task DirectEdit_AnIncreaseWithOnlyStoppedSubscribers_IsAllowed()
    {
        var h = Create();
        var product = await AddProductAsync(h);
        await AddSubAsync(h, status: SubscriptionStatus.Stopped);

        Assert.Null(await h.Service.PrepareDirectEditAsync(product, 5900, BillingInterval.Monthly, Ct));
    }

    [Fact]
    public async Task DirectEdit_ANewIntervalWithSubscribers_IsRefused()
    {
        var h = Create();
        var product = await AddProductAsync(h);
        await AddSubAsync(h);

        var error = await h.Service.PrepareDirectEditAsync(product, 4900, BillingInterval.Yearly, Ct);

        Assert.Contains("interval", error);
    }

    [Fact]
    public async Task DirectEdit_WithTheSamePriceAndInterval_IsAllowed()
    {
        var h = Create();
        var product = await AddProductAsync(h);
        await AddSubAsync(h);
        await ScheduleAsync(h);

        Assert.Null(await h.Service.PrepareDirectEditAsync(product, 4900, BillingInterval.Monthly, Ct));
    }

    [Fact]
    public async Task DirectEdit_APriceWhileAChangeIsScheduled_IsRefused()
    {
        var h = Create();
        var product = await AddProductAsync(h);
        await ScheduleAsync(h);

        var error = await h.Service.PrepareDirectEditAsync(product, 3900, BillingInterval.Monthly, Ct);

        Assert.Contains("Cancel it", error);
    }

    [Fact]
    public async Task DirectEdit_ADecrease_LowersAgreedPricesAboveIt_AndDropsHigherNoticesNotInEffect()
    {
        var h = Create();
        var product = await AddProductAsync(h);
        var sub = await AddSubAsync(h);
        var cheap = await AddSubAsync(h, unitPrice: 2900, agreement: "cheap");
        // An applied change whose notice to this subscriber is still running.
        var (change, _) = await ScheduleAsync(h);
        await h.Service.SweepAsync(Ct);
        h.Clock.Advance(TimeSpan.FromDays(30));
        product.PriceInOre = 5900;
        change!.AppliedAt = h.Clock.GetUtcNow().UtcDateTime;
        var notice = await h.Db.PriceChangeNotices.SingleAsync(n => n.SubscriptionId == sub.Id, Ct);
        notice.EffectiveFrom = Today.AddDays(40);
        await h.Db.SaveChangesAsync(Ct);

        Assert.Null(await h.Service.PrepareDirectEditAsync(product, 3900, BillingInterval.Monthly, Ct));
        await h.Db.SaveChangesAsync(Ct);

        Assert.Equal(3900, (await h.Db.Subscriptions.SingleAsync(s => s.Id == sub.Id, Ct)).UnitPriceInOre);
        Assert.Equal(2900, (await h.Db.Subscriptions.SingleAsync(s => s.Id == cheap.Id, Ct)).UnitPriceInOre);
        Assert.Empty(h.Db.PriceChangeNotices);
    }
}
