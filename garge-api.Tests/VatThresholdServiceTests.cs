using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Models.Shop;
using garge_api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace garge_api.Tests;

public class VatThresholdServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Harness
    {
        public required ApplicationDbContext Db { get; init; }
        public required VatThresholdService Service { get; init; }
        public required Mock<ISecurityNotifier> Notifier { get; init; }
        public required string DbName { get; init; }

        public async Task<Invoice> SaleAsync(int amountInOre, DateTime issuedAt, int vatPercent = 0, bool isTest = false, OrderStatus status = OrderStatus.Paid)
        {
            var order = new Order { UserId = "buyer", TotalInOre = amountInOre, Status = status, IsTest = isTest };
            Db.Orders.Add(order);
            await Db.SaveChangesAsync();
            var invoice = new Invoice { OrderId = order.Id, AmountInOre = amountInOre, IssuedAt = issuedAt, VatPercentage = vatPercent, PdfData = [1] };
            Db.Invoices.Add(invoice);
            await Db.SaveChangesAsync();
            return invoice;
        }

        public async Task<Invoice> ChargeAsync(int amountInOre, DateTime issuedAt, bool isTest = false)
        {
            var sub = new garge_api.Models.Subscription.Subscription { UserId = "buyer", ProductId = 1, VippsAgreementId = $"agr-{Guid.NewGuid()}", IsTest = isTest };
            Db.Subscriptions.Add(sub);
            await Db.SaveChangesAsync();
            var invoice = new Invoice { SubscriptionId = sub.Id, AmountInOre = amountInOre, IssuedAt = issuedAt, PdfData = [1] };
            Db.Invoices.Add(invoice);
            await Db.SaveChangesAsync();
            return invoice;
        }

        /// <summary>Pays back part of a sale and makes its refund credit note, as InvoiceService does.</summary>
        public async Task<Invoice> RefundAsync(Invoice sale, int amountInOre, DateTime at, int? creditsInvoiceId = null)
        {
            if (sale.OrderId is { } orderId)
                (await Db.Orders.SingleAsync(o => o.Id == orderId)).RefundedInOre += amountInOre;
            else
                sale.RefundedInOre += amountInOre;
            var note = new Invoice { Kind = InvoiceKind.CreditNote, ForRefund = true, CreditsInvoiceId = creditsInvoiceId ?? sale.Id, AmountInOre = -amountInOre, IssuedAt = at, PdfData = [1] };
            Db.Invoices.Add(note);
            await Db.SaveChangesAsync();
            return note;
        }

        public async Task OutsideAsync(int year, int month, long amountInOre)
        {
            Db.OutsideSalesMonths.Add(new OutsideSalesMonth { Month = new DateOnly(year, month, 1), AmountInOre = amountInOre });
            await Db.SaveChangesAsync();
        }

        public async Task<AppSettings> SettingsAsync() => (await Db.AppSettings.FindAsync(1))!;
    }

    private static async Task<Harness> BuildAsync(long outsideThisMonth = 0, bool vatEnabled = false)
    {
        var dbName = Guid.NewGuid().ToString();
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(dbName).Options);
        db.AppSettings.Add(new AppSettings { Id = 1, VatEnabled = vatEnabled });
        if (outsideThisMonth != 0)
            db.OutsideSalesMonths.Add(new OutsideSalesMonth { Month = new DateOnly(Now.Year, Now.Month, 1), AmountInOre = outsideThisMonth });
        db.Users.Add(new User { Id = "buyer", UserName = "buyer@example.com", Email = "buyer@example.com", FirstName = "B", LastName = "C" });
        db.Roles.Add(new IdentityRole { Id = "role-admin", Name = "Admin", NormalizedName = "ADMIN" });
        db.UserRoles.AddRange(new IdentityUserRole<string> { UserId = "admin-1", RoleId = "role-admin" },
                              new IdentityUserRole<string> { UserId = "admin-2", RoleId = "role-admin" });
        await db.SaveChangesAsync();

        var cache = new Mock<IAppSettingsCache>();
        cache.Setup(c => c.GetAsync()).ReturnsAsync(() => db.AppSettings.AsNoTracking().Single(s => s.Id == 1));
        var notifier = new Mock<ISecurityNotifier>();
        notifier.Setup(n => n.NotifyUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return new Harness
        {
            Db = db, Notifier = notifier, DbName = dbName,
            Service = new VatThresholdService(db, notifier.Object, cache.Object, NullLogger<VatThresholdService>.Instance)
        };
    }

    [Fact]
    public async Task Turnover_CountsTheLast12MonthsOfRealSalesAndTheOutsideSales()
    {
        var h = await BuildAsync(outsideThisMonth: 100_000);
        await h.SaleAsync(1_000_000, Now.AddMonths(-11));
        await h.ChargeAsync(37_375, Now.AddDays(-3));
        await h.SaleAsync(500_000, Now.AddMonths(-13));                        // older than 12 months
        await h.SaleAsync(700_000, Now.AddDays(-1), isTest: true);             // test sale
        await h.ChargeAsync(700_000, Now.AddDays(-1), isTest: true);           // test charge
        var refunded = await h.SaleAsync(800_000, Now.AddDays(-2), status: OrderStatus.Refunded);
        await h.RefundAsync(refunded, 800_000, Now.AddDays(-1));

        var status = await h.Service.GetStatusAsync(Now);

        Assert.Equal(100_000 + 1_000_000 + 37_375, status.TurnoverInOre);
        Assert.Null(status.CrossingInvoiceId);
        Assert.Empty(status.Owed);
    }

    [Fact]
    public async Task Turnover_LeavesOutWhatWasPaidBackOnSubscriptionCharges()
    {
        var h = await BuildAsync();
        var charge = await h.ChargeAsync(37_375, Now.AddDays(-3));
        charge.RefundedInOre = 7_375;
        await h.SaleAsync(10_000, Now.AddDays(-2));
        await h.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var status = await h.Service.GetStatusAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(40_000, status.TurnoverInOre);
    }

    [Fact]
    public async Task OwedSales_ShowTheCorrectionDocuments_NotARefundCreditNote()
    {
        var h = await BuildAsync();
        var crossing = await h.SaleAsync(5_100_000, Now.AddDays(-5));
        h.Db.Invoices.Add(new Invoice { Kind = InvoiceKind.CreditNote, ForRefund = true, CreditsInvoiceId = crossing.Id, AmountInOre = -1000, IssuedAt = Now.AddDays(-4), PdfData = [1] });
        await h.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var owed = Assert.Single((await h.Service.GetStatusAsync(Now, TestContext.Current.CancellationToken)).Owed);

        Assert.Null(owed.CreditNoteId);
    }

    [Fact]
    public async Task TheSaleThatPassesTheThreshold_AndEverySaleAfter_OweVatTakenOutOfThePrice()
    {
        var h = await BuildAsync();
        await h.SaleAsync(4_000_000, Now.AddMonths(-3));
        await h.SaleAsync(900_000, Now.AddMonths(-2));
        var crossing = await h.SaleAsync(200_000, Now.AddMonths(-1));        // 51 000 kr
        var after = await h.ChargeAsync(20_000, Now.AddDays(-5));
        var withVat = await h.SaleAsync(20_000, Now.AddDays(-1), vatPercent: 25);

        var status = await h.Service.GetStatusAsync(Now);

        Assert.Equal(crossing.Id, status.CrossingInvoiceId);
        Assert.Equal([crossing.Id, after.Id], status.Owed.Select(o => o.InvoiceId));
        Assert.Equal(40_000, status.Owed[0].VatInOre);   // 2 000 kr x 25/125
        Assert.Equal(4_000, status.Owed[1].VatInOre);    // 200 kr x 25/125
        Assert.DoesNotContain(status.Owed, o => o.InvoiceId == withVat.Id);
    }

    [Fact]
    public async Task ExactlyTheThreshold_IsNotPassingIt()
    {
        var h = await BuildAsync();
        await h.SaleAsync(4_000_000, Now.AddMonths(-3));
        await h.SaleAsync(1_000_000, Now.AddMonths(-1));

        Assert.Null((await h.Service.GetStatusAsync(Now)).CrossingInvoiceId);
        await h.SaleAsync(1, Now.AddDays(-1));
        Assert.NotNull((await h.Service.GetStatusAsync(Now)).CrossingInvoiceId);
    }

    [Fact]
    public async Task TheThresholdIsCountedOverTheTwelveMonthsBeforeEachSale()
    {
        // 30 000 kr fourteen months ago drops out of the window before the later 25 000 kr.
        var h = await BuildAsync();
        await h.SaleAsync(3_000_000, Now.AddMonths(-14));
        await h.SaleAsync(2_500_000, Now.AddMonths(-1));

        Assert.Null((await h.Service.GetStatusAsync(Now)).CrossingInvoiceId);
    }

    [Fact]
    public async Task OutsideSales_CountTowardPassingTheThreshold()
    {
        var h = await BuildAsync(outsideThisMonth: 4_900_000);
        var sale = await h.SaleAsync(200_000, Now.AddDays(-1));

        Assert.Equal(sale.Id, (await h.Service.GetStatusAsync(Now)).CrossingInvoiceId);
    }

    [Fact]
    public async Task Check_WarnsEveryAdminOncePerLevel()
    {
        var h = await BuildAsync();
        await h.SaleAsync(3_900_000, Now.AddDays(-10));
        await h.Service.CheckAsync(Now);
        h.Notifier.Verify(n => n.NotifyUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        await h.SaleAsync(100_000, Now.AddDays(-9));      // 40 000 kr, 80 %
        await h.Service.CheckAsync(Now);
        await h.Service.CheckAsync(Now);
        h.Notifier.Verify(n => n.NotifyUserAsync(It.IsAny<string>(), "VAT threshold", It.Is<string>(m => m.Contains("80 %")), "garge-vat-threshold-80", It.IsAny<CancellationToken>()), Times.Exactly(2));

        await h.SaleAsync(500_000, Now.AddDays(-8));      // 45 000 kr, 90 %
        await h.Service.CheckAsync(Now);
        h.Notifier.Verify(n => n.NotifyUserAsync("admin-1", "VAT threshold", It.Is<string>(m => m.Contains("90 %")), "garge-vat-threshold-90", It.IsAny<CancellationToken>()), Times.Once);

        var crossing = await h.SaleAsync(600_000, Now.AddDays(-7));
        await h.Service.CheckAsync(Now);
        await h.Service.CheckAsync(Now);
        h.Notifier.Verify(n => n.NotifyUserAsync("admin-2", "Register for VAT", It.Is<string>(m => m.Contains($"#{crossing.Id:D4}")), "garge-vat-threshold-100", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(100, (await h.SettingsAsync()).VatThresholdWarnedPercent);
    }

    [Fact]
    public async Task Check_PassingStraightToTheThreshold_SendsOnlyTheRegistrationNotice()
    {
        var h = await BuildAsync();
        await h.SaleAsync(5_100_000, Now.AddDays(-1));

        await h.Service.CheckAsync(Now);

        h.Notifier.Verify(n => n.NotifyUserAsync(It.IsAny<string>(), "Register for VAT", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        h.Notifier.Verify(n => n.NotifyUserAsync(It.IsAny<string>(), "VAT threshold", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Check_WithVatOn_SendsNothing()
    {
        var h = await BuildAsync(vatEnabled: true);
        await h.SaleAsync(5_100_000, Now.AddDays(-1));

        await h.Service.CheckAsync(Now);

        h.Notifier.Verify(n => n.NotifyUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PartialRefunds_AreTakenOffTurnoverAndTheVatOwed_ButNotOffAnEarlierCrossing()
    {
        // The sale passed the threshold on its own day. The refund the day after lowers turnover from then on.
        var h = await BuildAsync();
        var sale = await h.SaleAsync(5_200_000, Now.AddDays(-3));
        await h.RefundAsync(sale, 300_000, Now.AddDays(-2));

        var status = await h.Service.GetStatusAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(4_900_000, status.TurnoverInOre);
        Assert.Equal(sale.Id, status.CrossingInvoiceId);
        var owed = Assert.Single(status.Owed);
        Assert.Equal(4_900_000, owed.AmountInOre);
        Assert.Equal(980_000, owed.VatInOre);
    }

    [Fact]
    public async Task ARefundBeforeTheNextSale_KeepsTurnoverUnderTheThreshold()
    {
        var h = await BuildAsync();
        var first = await h.SaleAsync(3_000_000, Now.AddDays(-20));
        await h.RefundAsync(first, 3_000_000, Now.AddDays(-10));
        await h.SaleAsync(2_500_000, Now.AddDays(-5));

        var status = await h.Service.GetStatusAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(2_500_000, status.TurnoverInOre);
        Assert.Null(status.CrossingInvoiceId);
        Assert.Null(status.CrossedAt);
    }

    [Fact]
    public async Task AFullyRefundedOrder_CountsUntilTheDayOfItsRefund()
    {
        var h = await BuildAsync();
        var first = await h.SaleAsync(3_000_000, Now.AddDays(-20), status: OrderStatus.Refunded);
        var second = await h.SaleAsync(2_500_000, Now.AddDays(-5));
        await h.RefundAsync(first, 3_000_000, Now.AddDays(-2));

        var status = await h.Service.GetStatusAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(2_500_000, status.TurnoverInOre);
        Assert.Equal(second.Id, status.CrossingInvoiceId);
        Assert.Equal([second.Id], status.Owed.Select(o => o.InvoiceId));
    }

    [Fact]
    public async Task ARefundOfTheInvoiceThatReplacedASale_CountsAgainstTheSale()
    {
        var h = await BuildAsync();
        var sale = await h.SaleAsync(3_000_000, Now.AddDays(-20));
        var replacement = new Invoice { ReplacesInvoiceId = sale.Id, AmountInOre = 3_000_000, VatPercentage = 25, IssuedAt = Now.AddDays(-15), PdfData = [1] };
        h.Db.Invoices.Add(replacement);
        await h.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await h.RefundAsync(sale, 1_000_000, Now.AddDays(-10), creditsInvoiceId: replacement.Id);

        var status = await h.Service.GetStatusAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(2_000_000, status.TurnoverInOre);
    }

    [Fact]
    public async Task ARefundWithoutItsCreditNoteYet_CountsFromWhenTheOrderChanged()
    {
        var h = await BuildAsync();
        var sale = await h.SaleAsync(4_000_000, Now.AddDays(-20));
        var order = await h.Db.Orders.SingleAsync(o => o.Id == sale.OrderId, TestContext.Current.CancellationToken);
        order.RefundedInOre = 1_000_000;
        order.UpdatedAt = Now.AddDays(-10);
        await h.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await h.SaleAsync(1_500_000, Now.AddDays(-5));

        var status = await h.Service.GetStatusAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(4_500_000, status.TurnoverInOre);
        Assert.Null(status.CrossingInvoiceId);
    }

    [Fact]
    public async Task TheWindowIsWholeNorwegianDays()
    {
        // The 12 months up to 10 October 2026 start on 11 October 2025 in Norway. 22:30 UTC on 10 October
        // is past midnight in Norway, 13:00 UTC is not.
        var h = await BuildAsync();
        await h.SaleAsync(100_000, new DateTime(2025, 10, 10, 13, 0, 0, DateTimeKind.Utc));
        await h.SaleAsync(20_000, new DateTime(2025, 10, 10, 22, 30, 0, DateTimeKind.Utc));

        var status = await h.Service.GetStatusAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(20_000, status.TurnoverInOre);
    }

    [Fact]
    public async Task Status_ListsEachMonthTheWindowOverlaps_WithItsSalesAndOutsideSales()
    {
        var h = await BuildAsync(outsideThisMonth: 50_000);
        await h.OutsideAsync(2025, 12, 70_000);
        await h.SaleAsync(30_000, new DateTime(2025, 12, 5, 12, 0, 0, DateTimeKind.Utc));
        var refunded = await h.SaleAsync(10_000, new DateTime(2025, 12, 6, 12, 0, 0, DateTimeKind.Utc));
        await h.RefundAsync(refunded, 4_000, Now.AddDays(-1));

        var status = await h.Service.GetStatusAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(13, status.Months.Count);
        Assert.Equal(new DateOnly(2025, 10, 1), status.Months[0].Month);
        Assert.Equal(new DateOnly(2026, 10, 1), status.Months[^1].Month);
        var december = Assert.Single(status.Months, m => m.Month == new DateOnly(2025, 12, 1));
        Assert.Equal((36_000L, 70_000L), (december.SalesInOre, december.OutsideInOre));
        Assert.Equal(50_000, status.Months[^1].OutsideInOre);
        Assert.Equal(156_000, status.TurnoverInOre);
    }

    [Fact]
    public async Task OutsideSalesAlone_PassTheThreshold_FromTheFirstDayOfTheirMonth()
    {
        var h = await BuildAsync(outsideThisMonth: 5_100_000);
        await h.SaleAsync(10_000, new DateTime(2026, 9, 30, 21, 0, 0, DateTimeKind.Utc));   // 30 September in Norway
        var sale = await h.SaleAsync(10_000, Now.AddDays(-1));

        await h.Service.CheckAsync(Now, TestContext.Current.CancellationToken);

        var settings = await h.SettingsAsync();
        Assert.Null(settings.VatCrossingInvoiceId);
        Assert.Equal(new DateTime(2026, 9, 30, 22, 0, 0, DateTimeKind.Utc), settings.VatCrossedAt);   // 1 October in Norway
        var status = await h.Service.GetStatusAsync(Now, TestContext.Current.CancellationToken);
        Assert.Null(status.CrossingInvoiceId);
        Assert.Equal([sale.Id], status.Owed.Select(o => o.InvoiceId));
        h.Notifier.Verify(n => n.NotifyUserAsync("admin-1", "Register for VAT", It.Is<string>(m => m.Contains("sales outside garge on 2026-10-01")), "garge-vat-threshold-100", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ASaleRefundedInFullAfterTheCrossing_OwesNoVat()
    {
        var h = await BuildAsync();
        var crossing = await h.SaleAsync(5_100_000, Now.AddDays(-10));
        var refunded = await h.SaleAsync(20_000, Now.AddDays(-5));
        await h.RefundAsync(refunded, 20_000, Now.AddDays(-4));

        var status = await h.Service.GetStatusAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal([crossing.Id], status.Owed.Select(o => o.InvoiceId));
    }

    [Fact]
    public async Task TheSavedCrossing_WinsOverAnEarlierOneFoundLater()
    {
        // Outside sales entered afterwards for August would make the first sale the crossing.
        var h = await BuildAsync();
        var first = await h.SaleAsync(3_000_000, new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc));
        var second = await h.SaleAsync(2_100_000, new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc));
        await h.Service.CheckAsync(Now, TestContext.Current.CancellationToken);
        Assert.Equal(second.Id, (await h.SettingsAsync()).VatCrossingInvoiceId);

        await h.OutsideAsync(2026, 8, 2_500_000);
        var status = await h.Service.GetStatusAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(second.Id, status.CrossingInvoiceId);
        Assert.Equal(second.IssuedAt, status.CrossedAt);
        Assert.DoesNotContain(status.Owed, o => o.InvoiceId == first.Id);
    }

    [Fact]
    public async Task TheCrossingFoundWhenOutsideSalesPassedIt_IsFoundAgainBeforeVatIsOn()
    {
        var h = await BuildAsync(outsideThisMonth: 5_100_000);
        await h.Service.CheckAsync(Now, TestContext.Current.CancellationToken);
        h.Db.OutsideSalesMonths.RemoveRange(h.Db.OutsideSalesMonths);
        await h.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var status = await h.Service.GetStatusAsync(Now, TestContext.Current.CancellationToken);

        Assert.Null(status.CrossedAt);
    }

    [Fact]
    public async Task SetOutsideSales_StoresUpdatesAndRemovesAMonth()
    {
        var h = await BuildAsync();
        var ct = TestContext.Current.CancellationToken;

        Assert.Null(await h.Service.SetOutsideSalesAsync(new DateOnly(2026, 9, 1), 120_000, Now, ct));
        Assert.Null(await h.Service.SetOutsideSalesAsync(new DateOnly(2026, 10, 1), 30_000, Now, ct));
        Assert.Null(await h.Service.SetOutsideSalesAsync(new DateOnly(2026, 9, 1), 150_000, Now, ct));
        Assert.Equal(180_000, (await h.Service.GetStatusAsync(Now, ct)).TurnoverInOre);

        Assert.Null(await h.Service.SetOutsideSalesAsync(new DateOnly(2026, 10, 1), 0, Now, ct));
        var row = Assert.Single(await h.Db.OutsideSalesMonths.AsNoTracking().ToListAsync(ct));
        Assert.Equal((new DateOnly(2026, 9, 1), 150_000L, Now), (row.Month, row.AmountInOre, row.UpdatedAt));
    }

    [Fact]
    public async Task SetOutsideSales_RefusesAMonthThatHasNotStarted()
    {
        // 23:30 UTC on 31 October is 1 November in Norway, so November has started.
        var h = await BuildAsync();
        var ct = TestContext.Current.CancellationToken;
        var lateOctober = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal("The month has not started yet.", await h.Service.SetOutsideSalesAsync(new DateOnly(2026, 11, 1), 10_000, lateOctober, ct));
        Assert.Empty(await h.Db.OutsideSalesMonths.ToListAsync(ct));
        Assert.Null(await h.Service.SetOutsideSalesAsync(new DateOnly(2026, 11, 1), 10_000, new DateTime(2026, 10, 31, 23, 30, 0, DateTimeKind.Utc), ct));
    }

    [Fact]
    public async Task SetOutsideSales_RefusesAMonthMoreThan36MonthsBack()
    {
        var h = await BuildAsync();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("The month is more than 36 months back.", await h.Service.SetOutsideSalesAsync(new DateOnly(2023, 9, 1), 10_000, Now, ct));
        Assert.Empty(await h.Db.OutsideSalesMonths.ToListAsync(ct));
        Assert.Null(await h.Service.SetOutsideSalesAsync(new DateOnly(2023, 10, 1), 10_000, Now, ct));
    }

    [Fact]
    public async Task SavesOfTheSameMonthAtTheSameTime_AddItOnce()
    {
        var h = await BuildAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(h.DbName).Options;
        var cache = new Mock<IAppSettingsCache>();
        cache.Setup(c => c.GetAsync()).ReturnsAsync(() => new ApplicationDbContext(options).AppSettings.AsNoTracking().Single(s => s.Id == 1));

        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            using var db = new ApplicationDbContext(options);
            return await new VatThresholdService(db, h.Notifier.Object, cache.Object, NullLogger<VatThresholdService>.Instance)
                .SetOutsideSalesAsync(new DateOnly(2026, 9, 1), 10_000 + i, Now);
        })));

        Assert.All(answers, Assert.Null);
        Assert.Single(await h.Db.OutsideSalesMonths.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ACrossingFromOutsideSalesAlone_IsTakenBackWhenTheMonthIsCorrected()
    {
        var h = await BuildAsync();
        var ct = TestContext.Current.CancellationToken;
        await h.SaleAsync(4_100_000, Now.AddDays(-3));

        await h.Service.SetOutsideSalesAsync(new DateOnly(2026, 9, 1), 5_100_000, Now, ct);
        var crossed = await h.SettingsAsync();
        Assert.Equal((null, new DateTime(2026, 8, 31, 22, 0, 0, DateTimeKind.Utc), 100), (crossed.VatCrossingInvoiceId, crossed.VatCrossedAt, crossed.VatThresholdWarnedPercent));

        await h.Service.SetOutsideSalesAsync(new DateOnly(2026, 9, 1), 0, Now, ct);

        var cleared = await h.SettingsAsync();
        Assert.Equal((null, null), (cleared.VatCrossingInvoiceId, cleared.VatCrossedAt));
        var status = await h.Service.GetStatusAsync(Now, ct);
        Assert.Null(status.CrossedAt);
        Assert.Empty(status.Owed);
        // The warnings start over, so the 80 % warning for the 41 000 kr left goes out.
        Assert.Equal(80, cleared.VatThresholdWarnedPercent);
    }

    [Fact]
    public async Task ACrossingFromOutsideSalesAlone_StaysOnceVatIsOn()
    {
        var h = await BuildAsync();
        var ct = TestContext.Current.CancellationToken;
        await h.Service.SetOutsideSalesAsync(new DateOnly(2026, 9, 1), 5_100_000, Now, ct);
        var settings = await h.SettingsAsync();
        settings.VatEnabled = true;
        h.Db.OutsideSalesMonths.RemoveRange(h.Db.OutsideSalesMonths);
        await h.Db.SaveChangesAsync(ct);

        Assert.Equal(new DateTime(2026, 8, 31, 22, 0, 0, DateTimeKind.Utc), (await h.Service.GetStatusAsync(Now, ct)).CrossedAt);
    }

    [Fact]
    public async Task ACrossingAtASale_StaysWhenTheOutsideSalesThatHelpedAreCorrected()
    {
        var h = await BuildAsync();
        var ct = TestContext.Current.CancellationToken;
        await h.Service.SetOutsideSalesAsync(new DateOnly(2026, 9, 1), 4_000_000, Now, ct);
        var sale = await h.SaleAsync(1_100_000, Now.AddDays(-2));
        await h.Service.CheckAsync(Now, ct);
        Assert.Equal(sale.Id, (await h.SettingsAsync()).VatCrossingInvoiceId);

        await h.Service.SetOutsideSalesAsync(new DateOnly(2026, 9, 1), 0, Now, ct);

        Assert.Equal((sale.Id, sale.IssuedAt), ((await h.SettingsAsync()).VatCrossingInvoiceId, (await h.SettingsAsync()).VatCrossedAt));
        Assert.Equal(sale.Id, (await h.Service.GetStatusAsync(Now, ct)).CrossingInvoiceId);
    }

    [Fact]
    public async Task ARefundCreditedWeeksLate_CountsFromWhenTheOrderWasRefunded()
    {
        // Refunded on day -40, the note made by the retry job on day -5. The second sale on day -10 comes
        // after the refund, so the threshold is not passed.
        var h = await BuildAsync();
        var first = await h.SaleAsync(3_000_000, Now.AddDays(-60));
        await h.RefundAsync(first, 3_000_000, Now.AddDays(-5));
        var order = await h.Db.Orders.SingleAsync(o => o.Id == first.OrderId, TestContext.Current.CancellationToken);
        order.UpdatedAt = Now.AddDays(-40);
        await h.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await h.SaleAsync(2_500_000, Now.AddDays(-10));

        var status = await h.Service.GetStatusAsync(Now, TestContext.Current.CancellationToken);

        Assert.Null(status.CrossedAt);
        Assert.Equal(2_500_000, status.TurnoverInOre);
    }

    [Fact]
    public async Task ARefundOnTime_KeepsTheDateOfItsNote_WhenTheOrderChangedLater()
    {
        var h = await BuildAsync();
        var first = await h.SaleAsync(3_000_000, Now.AddDays(-60));
        await h.RefundAsync(first, 3_000_000, Now.AddDays(-40));
        var order = await h.Db.Orders.SingleAsync(o => o.Id == first.OrderId, TestContext.Current.CancellationToken);
        order.UpdatedAt = Now.AddDays(-1);
        await h.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await h.SaleAsync(2_500_000, Now.AddDays(-10));

        Assert.Null((await h.Service.GetStatusAsync(Now, TestContext.Current.CancellationToken)).CrossedAt);
    }

    [Fact]
    public async Task ARefundCreditedInParts_IsNeverCountedTwice()
    {
        var h = await BuildAsync();
        var ct = TestContext.Current.CancellationToken;
        var sale = await h.SaleAsync(3_000_000, Now.AddDays(-30));
        var order = await h.Db.Orders.SingleAsync(o => o.Id == sale.OrderId, ct);
        order.RefundedInOre = 1_000_000;
        order.UpdatedAt = Now.AddDays(-20);
        h.Db.Invoices.Add(new Invoice { Kind = InvoiceKind.CreditNote, ForRefund = true, CreditsInvoiceId = sale.Id, AmountInOre = -400_000, IssuedAt = Now.AddDays(-20), PdfData = [1] });
        await h.Db.SaveChangesAsync(ct);

        // 4 000 kr credited, 6 000 kr refunded without a note yet.
        var status = await h.Service.GetStatusAsync(Now, ct);
        Assert.Equal(2_000_000, status.TurnoverInOre);
        Assert.Equal(2_000_000, Assert.Single(status.Months, m => m.SalesInOre != 0).SalesInOre);

        // The retry job credits the rest.
        h.Db.Invoices.Add(new Invoice { Kind = InvoiceKind.CreditNote, ForRefund = true, CreditsInvoiceId = sale.Id, AmountInOre = -600_000, IssuedAt = Now.AddDays(-2), PdfData = [1] });
        await h.Db.SaveChangesAsync(ct);

        Assert.Equal(2_000_000, (await h.Service.GetStatusAsync(Now, ct)).TurnoverInOre);
    }

    [Fact]
    public async Task SetOutsideSales_ChecksTheThreshold()
    {
        var h = await BuildAsync();

        await h.Service.SetOutsideSalesAsync(new DateOnly(2026, 10, 1), 4_000_000, Now, TestContext.Current.CancellationToken);

        Assert.Equal(80, (await h.SettingsAsync()).VatThresholdWarnedPercent);
    }

    [Fact]
    public async Task OutsideSales_DoNotCountTowardOlderWindows()
    {
        // 45 000 kr of outside sales this month counts in no window that ends 18 months ago.
        var h = await BuildAsync(outsideThisMonth: 4_500_000);
        await h.SaleAsync(600_000, Now.AddMonths(-18));

        var status = await h.Service.GetStatusAsync(Now);

        Assert.Null(status.CrossingInvoiceId);
        Assert.Equal(4_500_000, status.TurnoverInOre);
    }

    [Fact]
    public async Task TheCrossingIsStored_SoItIsKeptAfterItLeavesTheWindow()
    {
        var h = await BuildAsync();
        var crossing = await h.SaleAsync(5_100_000, Now.AddMonths(-20));
        await h.Service.CheckAsync(Now.AddMonths(-20).AddDays(1));
        Assert.Equal(crossing.Id, (await h.SettingsAsync()).VatCrossingInvoiceId);
        var later = await h.SaleAsync(10_000, Now.AddDays(-1));

        // Thirty months on, the crossing is outside every window that is loaded.
        var status = await h.Service.GetStatusAsync(Now.AddMonths(10));

        Assert.Equal(crossing.Id, status.CrossingInvoiceId);
        Assert.Equal([crossing.Id, later.Id], status.Owed.Select(o => o.InvoiceId));
    }

    [Fact]
    public async Task Warnings_ReArmWhenTurnoverDropsBelow70Percent()
    {
        var h = await BuildAsync();
        await h.SaleAsync(4_100_000, Now.AddMonths(-11).AddDays(-20));
        await h.Service.CheckAsync(Now.AddMonths(-11).AddDays(-19));
        Assert.Equal(80, (await h.SettingsAsync()).VatThresholdWarnedPercent);

        // The sale leaves the window, so turnover drops to zero and the warnings re-arm.
        await h.Service.CheckAsync(Now.AddMonths(1));
        Assert.Equal(0, (await h.SettingsAsync()).VatThresholdWarnedPercent);

        await h.SaleAsync(4_100_000, Now.AddMonths(1));
        await h.Service.CheckAsync(Now.AddMonths(1).AddDays(1));
        h.Notifier.Verify(n => n.NotifyUserAsync("admin-1", "VAT threshold", It.IsAny<string>(), "garge-vat-threshold-80", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task AWarningThatReachedNoAdmin_IsTriedAgain()
    {
        var h = await BuildAsync();
        h.Notifier.Reset();
        h.Notifier.SetupSequence(n => n.NotifyUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false).ReturnsAsync(false).ReturnsAsync(true).ReturnsAsync(true);
        await h.SaleAsync(4_100_000, Now.AddDays(-1));

        await h.Service.CheckAsync(Now);
        Assert.Equal(0, (await h.SettingsAsync()).VatThresholdWarnedPercent);
        await h.Service.CheckAsync(Now);
        Assert.Equal(80, (await h.SettingsAsync()).VatThresholdWarnedPercent);
    }

    [Fact]
    public async Task ChecksAtTheSameTime_SendEachWarningOnce()
    {
        var h = await BuildAsync();
        await h.SaleAsync(4_100_000, Now.AddDays(-1));
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(h.DbName).Options;
        var cache = new Mock<IAppSettingsCache>();
        cache.Setup(c => c.GetAsync()).ReturnsAsync(() => new ApplicationDbContext(options).AppSettings.AsNoTracking().Single(s => s.Id == 1));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            using var db = new ApplicationDbContext(options);
            await new VatThresholdService(db, h.Notifier.Object, cache.Object, NullLogger<VatThresholdService>.Instance).CheckAsync(Now);
        })));

        h.Notifier.Verify(n => n.NotifyUserAsync("admin-1", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
