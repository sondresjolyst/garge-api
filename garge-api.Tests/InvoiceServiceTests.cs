using garge_api.Helpers;
using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Models.Shop;
using garge_api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace garge_api.Tests;

public class InvoiceServiceTests
{
    private static (InvoiceService svc, ApplicationDbContext db, Mock<IEmailService> email, Mock<IPdfRenderer> pdf) Create(
        Mock<IPdfRenderer>? pdfRenderer = null, Mock<IVippsService>? vipps = null)
    {
        var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(ApplicationDbContext))).Returns(db);
        if (vipps != null)
            serviceProvider.Setup(sp => sp.GetService(typeof(IVippsService))).Returns(vipps.Object);

        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(serviceProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var email = new Mock<IEmailService>();
        var pdf = pdfRenderer ?? new Mock<IPdfRenderer>();
        if (pdfRenderer == null)
        {
            pdf.Setup(p => p.RenderAsync(It.IsAny<string>())).ReturnsAsync(new byte[] { 1, 2, 3 });
        }

        var svc = new InvoiceService(scopeFactory.Object, email.Object, pdf.Object,
            NullLogger<InvoiceService>.Instance);
        return (svc, db, email, pdf);
    }

    private static async Task<Order> SeedPaidOrderAsync(ApplicationDbContext db)
    {
        await db.AppSettings.AddAsync(new AppSettings { Id = 1, CompanyName = "Garge" });
        await db.Users.AddAsync(new User
        {
            Id = "user-1", Email = "buyer@example.com", UserName = "buyer@example.com",
            FirstName = "Sondre", LastName = "Sjølyst"
        });
        var order = new Order
        {
            UserId = "user-1", TotalInOre = 50000, Status = OrderStatus.Paid
        };
        await db.Orders.AddAsync(order);
        await db.SaveChangesAsync();

        var item = new ShopItem { Id = 1, Name = "Garge Sensor", PriceInOre = 50000, IsActive = true };
        await db.ShopItems.AddAsync(item);
        await db.OrderItems.AddAsync(new OrderItem
        {
            OrderId = order.Id, ShopItemId = 1, Quantity = 1,
            PriceAtPurchaseInOre = 50000, UnitPriceExclVatInOre = 50000, VatPercentage = 0
        });
        await db.SaveChangesAsync();
        return order;
    }

    [Fact]
    public async Task GenerateAndStoreAsync_ExistingNonEmptyInvoiceWithoutForce_ShortCircuits()
    {
        var (svc, db, email, pdf) = Create();
        var order = await SeedPaidOrderAsync(db);
        var existing = new Invoice { OrderId = order.Id, IssuedAt = DateTime.UtcNow.AddMinutes(-5), PdfData = [9, 9, 9] };
        db.Invoices.Add(existing);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var returnedId = await svc.GenerateAndStoreAsync(order.Id);

        Assert.Equal(existing.Id, returnedId);
        Assert.Single(db.Invoices);
        Assert.Equal(new byte[] { 9, 9, 9 }, db.Invoices.Single().PdfData);
        email.Verify(e => e.SendEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<EmailAttachment>?>()), Times.Never);
        pdf.Verify(p => p.RenderAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GenerateAndStoreAsync_NoExistingInvoice_RendersAndEmails()
    {
        var (svc, db, email, pdf) = Create();
        var order = await SeedPaidOrderAsync(db);

        var id = await svc.GenerateAndStoreAsync(order.Id);

        var saved = await db.Invoices.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(id, saved.Id);
        Assert.Equal(new byte[] { 1, 2, 3 }, saved.PdfData);
        // The data export lists invoice amounts, so an order invoice stores the order total.
        Assert.Equal(order.TotalInOre, saved.AmountInOre);
        Assert.NotEqual(0, saved.AmountInOre);
        pdf.Verify(p => p.RenderAsync(It.IsAny<string>()), Times.Once);
        email.Verify(e => e.SendEmailAsync(
            "buyer@example.com",
            It.Is<string>(s => s.Contains($"#{id:D4}")),
            It.IsAny<string>(),
            It.Is<IReadOnlyList<EmailAttachment>?>(a => a != null && a.Count == 1)), Times.Once);
    }

    [Fact]
    public async Task GenerateAndStoreAsync_RenderThrows_RemovesNewRow()
    {
        var pdf = new Mock<IPdfRenderer>();
        pdf.Setup(p => p.RenderAsync(It.IsAny<string>())).ThrowsAsync(new Exception("chromium gone"));
        var (svc, db, email, _) = Create(pdf);
        var order = await SeedPaidOrderAsync(db);

        await Assert.ThrowsAsync<Exception>(() => svc.GenerateAndStoreAsync(order.Id));

        Assert.Empty(db.Invoices);
        email.Verify(e => e.SendEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<EmailAttachment>?>()), Times.Never);
    }

    [Fact]
    public async Task GenerateAndStoreAsync_ExistingEmptyRow_WithoutForce_ShortCircuits()
    {
        // Race scenario: a concurrent caller has inserted an in-progress placeholder
        // row but hasn't filled the PDF yet. The second caller must NOT render
        // again (would double-email). Short-circuit on any existing row.
        var (svc, db, email, pdf) = Create();
        var order = await SeedPaidOrderAsync(db);
        var inProgress = new Invoice { OrderId = order.Id, IssuedAt = DateTime.UtcNow, PdfData = [] };
        db.Invoices.Add(inProgress);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var id = await svc.GenerateAndStoreAsync(order.Id);

        Assert.Equal(inProgress.Id, id);
        Assert.Single(db.Invoices);
        pdf.Verify(p => p.RenderAsync(It.IsAny<string>()), Times.Never);
        email.Verify(e => e.SendEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<EmailAttachment>?>()), Times.Never);
    }

    [Fact]
    public async Task GenerateAndStoreAsync_ExistingRow_WithForce_RegeneratesAndEmails()
    {
        // Admin retry path: force=true must re-render and re-email, reusing the
        // same row (and id) so the invoice number stays sequential.
        var (svc, db, email, pdf) = Create();
        var order = await SeedPaidOrderAsync(db);
        var existing = new Invoice { OrderId = order.Id, IssuedAt = DateTime.UtcNow.AddDays(-1), PdfData = [9, 9, 9] };
        db.Invoices.Add(existing);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var id = await svc.GenerateAndStoreAsync(order.Id, force: true);

        Assert.Equal(existing.Id, id);
        var saved = await db.Invoices.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 1, 2, 3 }, saved.PdfData);
        pdf.Verify(p => p.RenderAsync(It.IsAny<string>()), Times.Once);
        email.Verify(e => e.SendEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<EmailAttachment>?>()), Times.Once);
    }

    [Fact]
    public async Task GenerateAndStoreAsync_ForceRegenerateOnExistingInvoice_KeepsRowOnRenderFail()
    {
        var pdf = new Mock<IPdfRenderer>();
        pdf.Setup(p => p.RenderAsync(It.IsAny<string>())).ThrowsAsync(new Exception("chromium gone"));
        var (svc, db, _, _) = Create(pdf);
        var order = await SeedPaidOrderAsync(db);
        var existing = new Invoice { OrderId = order.Id, IssuedAt = DateTime.UtcNow.AddDays(-1), PdfData = [9, 9, 9] };
        db.Invoices.Add(existing);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<Exception>(() => svc.GenerateAndStoreAsync(order.Id, force: true));

        // Force regenerate over a complete invoice must NOT throw away the existing
        // PDF bytes when the new render fails.
        var saved = await db.Invoices.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(existing.Id, saved.Id);
        Assert.Equal(new byte[] { 9, 9, 9 }, saved.PdfData);
    }

    [Fact]
    public async Task GenerateAndStoreAsync_OrderMissing_Throws()
    {
        var (svc, _, _, _) = Create();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.GenerateAndStoreAsync(orderId: 99999));
    }

    private static async Task<garge_api.Models.Subscription.Subscription> SeedSubscriptionAsync(ApplicationDbContext db)
    {
        await db.AppSettings.AddAsync(new AppSettings { Id = 1, CompanyName = "Garge" });
        await db.Users.AddAsync(new User
        {
            Id = "user-sub", Email = "buyer@example.com", UserName = "buyer@example.com",
            FirstName = "Sondre", LastName = "Sjølyst"
        });
        var product = new garge_api.Models.Subscription.Product
        {
            Id = 9, Name = "Garge Basic", PriceInOre = 29900,
            Interval = garge_api.Models.Subscription.BillingInterval.Monthly,
            Type = garge_api.Models.Subscription.ProductType.Primary,
            IsActive = true,
        };
        await db.Products.AddAsync(product);
        var subscription = new garge_api.Models.Subscription.Subscription
        {
            UserId = "user-sub",
            ProductId = product.Id,
            VippsAgreementId = "agr-test",
            Status = garge_api.Models.Subscription.SubscriptionStatus.Active,
        };
        await db.Subscriptions.AddAsync(subscription);
        await db.SaveChangesAsync();
        return subscription;
    }

    [Fact]
    public async Task GenerateForSubscriptionChargeAsync_HappyPath_RendersAndEmails()
    {
        var (svc, db, email, pdf) = Create();
        var subscription = await SeedSubscriptionAsync(db);

        var id = await svc.GenerateForSubscriptionChargeAsync(
            subscription.Id, "charge-1", amountInOre: 29900, occurredAt: DateTime.UtcNow);

        var saved = await db.Invoices.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(id, saved.Id);
        Assert.Equal(subscription.Id, saved.SubscriptionId);
        Assert.Equal("charge-1", saved.VippsChargeId);
        Assert.Equal(29900, saved.AmountInOre);
        Assert.Null(saved.OrderId);
        Assert.Equal(new byte[] { 1, 2, 3 }, saved.PdfData);
        pdf.Verify(p => p.RenderAsync(It.IsAny<string>()), Times.Once);
        email.Verify(e => e.SendEmailAsync(
            "buyer@example.com",
            It.Is<string>(s => s.Contains($"#{id:D4}")),
            It.IsAny<string>(),
            It.Is<IReadOnlyList<EmailAttachment>?>(a => a != null && a.Count == 1)), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GenerateForSubscriptionChargeAsync_MissingAddress_IsFetchedFromTheSubscriptionsEnvironment(bool isTest)
    {
        var vipps = new Mock<IVippsService>();
        vipps.Setup(v => v.GetAgreementAsync("agr-test", isTest))
            .ReturnsAsync(new VippsAgreementResponse { Id = "agr-test", Sub = "sub-env" });
        vipps.Setup(v => v.GetUserInfoAsync("sub-env", isTest))
            .ReturnsAsync(new VippsUserInfo { Address = new VippsAddress { Formatted = "Storgata 1, 0155 Oslo" } });
        var (svc, db, _, _) = Create(vipps: vipps);
        var subscription = await SeedSubscriptionAsync(db);
        subscription.IsTest = isTest;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await svc.GenerateForSubscriptionChargeAsync(subscription.Id, "charge-env", 29900, DateTime.UtcNow);

        var saved = await db.Subscriptions.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Storgata 1, 0155 Oslo", saved.BillingAddress);
    }

    [Fact]
    public async Task GenerateForSubscriptionChargeAsync_DuplicateChargeId_ShortCircuits()
    {
        var (svc, db, email, pdf) = Create();
        var subscription = await SeedSubscriptionAsync(db);

        var first = await svc.GenerateForSubscriptionChargeAsync(
            subscription.Id, "charge-dup", 29900, DateTime.UtcNow);
        var second = await svc.GenerateForSubscriptionChargeAsync(
            subscription.Id, "charge-dup", 29900, DateTime.UtcNow);

        Assert.Equal(first, second);
        Assert.Single(db.Invoices);
        pdf.Verify(p => p.RenderAsync(It.IsAny<string>()), Times.Once);
        email.Verify(e => e.SendEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<EmailAttachment>?>()), Times.Once);
    }

    [Fact]
    public async Task GenerateForSubscriptionChargeAsync_RenderFails_RemovesPlaceholderRow()
    {
        var pdf = new Mock<IPdfRenderer>();
        pdf.Setup(p => p.RenderAsync(It.IsAny<string>())).ThrowsAsync(new Exception("chromium gone"));
        var (svc, db, _, _) = Create(pdf);
        var subscription = await SeedSubscriptionAsync(db);

        await Assert.ThrowsAsync<Exception>(() =>
            svc.GenerateForSubscriptionChargeAsync(subscription.Id, "charge-fail", 29900, DateTime.UtcNow));

        Assert.Empty(db.Invoices);
    }

    private static (InvoiceService svc, ApplicationDbContext db, List<string> html) CreateCapturingHtml()
    {
        var html = new List<string>();
        var pdf = new Mock<IPdfRenderer>();
        pdf.Setup(p => p.RenderAsync(It.IsAny<string>())).Callback<string>(html.Add).ReturnsAsync(new byte[] { 1 });
        var (svc, db, _, _) = Create(pdf);
        return (svc, db, html);
    }

    [Fact]
    public async Task OrderInvoice_SoldBeforeVat_ShowsNoVatEvenAfterVatIsSwitchedOn()
    {
        var (svc, db, html) = CreateCapturingHtml();
        var order = await SeedPaidOrderAsync(db);
        (await db.AppSettings.SingleAsync(TestContext.Current.CancellationToken)).VatEnabled = true;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await svc.GenerateAndStoreAsync(order.Id);

        Assert.DoesNotContain("excl. VAT", html.Single());
        Assert.Equal(0, (await db.Invoices.SingleAsync(TestContext.Current.CancellationToken)).VatPercentage);
    }

    [Fact]
    public async Task OrderInvoice_SoldWithVat_ShowsTheVatInsideThePrice()
    {
        var (svc, db, html) = CreateCapturingHtml();
        var order = await SeedPaidOrderAsync(db);
        var item = await db.OrderItems.SingleAsync(TestContext.Current.CancellationToken);
        item.UnitPriceExclVatInOre = 40000;
        item.VatPercentage = 25;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await svc.GenerateAndStoreAsync(order.Id);

        var page = html.Single();
        Assert.Contains("Subtotal excl. VAT</td><td class=\"r\">NOK " + MoneyFormat.Nok(40000), page);
        Assert.Contains("VAT 25%</td><td class=\"r\">NOK " + MoneyFormat.Nok(10000), page);
        Assert.Equal(25, (await db.Invoices.SingleAsync(TestContext.Current.CancellationToken)).VatPercentage);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 25)]
    public async Task SubscriptionInvoice_TakesTheVatOutOfTheCharge(bool vatEnabled, int vatPercent)
    {
        var (svc, db, html) = CreateCapturingHtml();
        var subscription = await SeedSubscriptionAsync(db);
        (await db.AppSettings.SingleAsync(TestContext.Current.CancellationToken)).VatEnabled = vatEnabled;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await svc.GenerateForSubscriptionChargeAsync(subscription.Id, "charge-vat", 20000, DateTime.UtcNow);

        var invoice = await db.Invoices.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(20000, invoice.AmountInOre);
        Assert.Equal(vatPercent, invoice.VatPercentage);
        if (vatEnabled)
            Assert.Contains("VAT 25%</td><td class=\"r\">NOK " + MoneyFormat.Nok(4000), html.Single());
        else
            Assert.DoesNotContain("excl. VAT", html.Single());
    }

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(25, false, true)]
    [InlineData(25, true, true)]
    [InlineData(0, false, false)]
    public async Task OrderInvoice_ShowsMvaOnlyWhenSoldWithVat(int lineVat, bool vatOnNow, bool expectMva)
    {
        var (svc, db, html) = CreateCapturingHtml();
        var order = await SeedPaidOrderAsync(db);
        var settings = await db.AppSettings.SingleAsync(TestContext.Current.CancellationToken);
        settings.CompanyOrgNumber = "999 888 777";
        settings.VatEnabled = vatOnNow;
        var item = await db.OrderItems.SingleAsync(TestContext.Current.CancellationToken);
        item.VatPercentage = lineVat;
        item.UnitPriceExclVatInOre = lineVat > 0 ? 40000 : 50000;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await svc.GenerateAndStoreAsync(order.Id);

        Assert.Equal(expectMva, html.Single().Contains("999 888 777 MVA"));
        Assert.Contains("999 888 777", html.Single());
    }

    [Fact]
    public async Task OrderInvoice_SplitsTheVatFromEachLineTotal()
    {
        // 3 x 49.02 kr = 147.06 kr. Split from the line total that is 117.65 kr plus 29.41 kr VAT.
        var (svc, db, html) = CreateCapturingHtml();
        var order = await SeedPaidOrderAsync(db);
        var item = await db.OrderItems.SingleAsync(TestContext.Current.CancellationToken);
        item.Quantity = 3;
        item.PriceAtPurchaseInOre = 4902;
        item.UnitPriceExclVatInOre = 3922;
        item.VatPercentage = 25;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await svc.GenerateAndStoreAsync(order.Id);

        Assert.Contains("Subtotal excl. VAT</td><td class=\"r\">NOK " + MoneyFormat.Nok(11765), html.Single());
        Assert.Contains("VAT 25%</td><td class=\"r\">NOK " + MoneyFormat.Nok(2941), html.Single());
    }
}
