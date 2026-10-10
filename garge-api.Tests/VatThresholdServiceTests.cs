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

        public async Task<AppSettings> SettingsAsync() => (await Db.AppSettings.FindAsync(1))!;
    }

    private static async Task<Harness> BuildAsync(long otherTurnover = 0, bool vatEnabled = false)
    {
        var dbName = Guid.NewGuid().ToString();
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(dbName).Options);
        db.AppSettings.Add(new AppSettings { Id = 1, OtherTurnoverInOre = otherTurnover, VatEnabled = vatEnabled });
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
    public async Task Turnover_CountsTheLast12MonthsOfRealSalesAndTheOtherTurnover()
    {
        var h = await BuildAsync(otherTurnover: 100_000);
        await h.SaleAsync(1_000_000, Now.AddMonths(-11));
        await h.ChargeAsync(37_375, Now.AddDays(-3));
        await h.SaleAsync(500_000, Now.AddMonths(-13));                        // older than 12 months
        await h.SaleAsync(700_000, Now.AddDays(-1), isTest: true);             // test sale
        await h.ChargeAsync(700_000, Now.AddDays(-1), isTest: true);           // test charge
        await h.SaleAsync(800_000, Now.AddDays(-2), status: OrderStatus.Refunded);

        var status = await h.Service.GetStatusAsync(Now);

        Assert.Equal(100_000 + 1_000_000 + 37_375, status.TurnoverInOre);
        Assert.Null(status.CrossingInvoiceId);
        Assert.Empty(status.Owed);
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
    public async Task OtherTurnover_CountsTowardPassingTheThreshold()
    {
        var h = await BuildAsync(otherTurnover: 4_900_000);
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
    public async Task PartialRefunds_AreTakenOffTurnoverAndTheVatOwed()
    {
        var h = await BuildAsync();
        var sale = await h.SaleAsync(5_200_000, Now.AddDays(-3));
        var order = await h.Db.Orders.SingleAsync(o => o.Id == sale.OrderId, TestContext.Current.CancellationToken);
        order.RefundedInOre = 300_000;
        await h.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var status = await h.Service.GetStatusAsync(Now);

        Assert.Equal(4_900_000, status.TurnoverInOre);
        Assert.Null(status.CrossingInvoiceId);
    }

    [Fact]
    public async Task OtherTurnover_DoesNotCountTowardOlderWindows()
    {
        // 45 000 kr of other sales covers the last 12 months only. Sales 18 months ago stay below.
        var h = await BuildAsync(otherTurnover: 4_500_000);
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
