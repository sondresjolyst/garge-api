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
        Mock<IPdfRenderer>? pdfRenderer = null, Mock<IVippsService>? vipps = null, Func<ApplicationDbContext, IVatThresholdService>? vat = null)
    {
        var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(ApplicationDbContext))).Returns(db);
        if (vipps != null)
            serviceProvider.Setup(sp => sp.GetService(typeof(IVippsService))).Returns(vipps.Object);
        if (vat != null)
            serviceProvider.Setup(sp => sp.GetService(typeof(IVatThresholdService))).Returns(vat(db));

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
    public async Task GenerateAndStoreAsync_RenderThrows_KeepsTheSaleForTheRetry()
    {
        var pdf = new Mock<IPdfRenderer>();
        pdf.Setup(p => p.RenderAsync(It.IsAny<string>())).ThrowsAsync(new Exception("chromium gone"));
        var (svc, db, email, _) = Create(pdf);
        var order = await SeedPaidOrderAsync(db);

        await Assert.ThrowsAsync<Exception>(() => svc.GenerateAndStoreAsync(order.Id));

        var kept = await db.Invoices.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(order.TotalInOre, kept.AmountInOre);
        Assert.Empty(kept.PdfData);
        Assert.NotNull(kept.PdfAttemptedAt);
        email.Verify(e => e.SendEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<EmailAttachment>?>()), Times.Never);
    }

    [Fact]
    public async Task GenerateAndStoreAsync_PdfBeingMade_WithoutForce_ShortCircuits()
    {
        // Another caller started the PDF moments ago. Making it again would email twice.
        var (svc, db, email, pdf) = Create();
        var order = await SeedPaidOrderAsync(db);
        var inProgress = new Invoice { OrderId = order.Id, IssuedAt = DateTime.UtcNow, PdfData = [], PdfAttemptedAt = DateTime.UtcNow };
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
    public async Task GenerateForSubscriptionChargeAsync_RenderFails_KeepsTheSaleForTheRetry()
    {
        var pdf = new Mock<IPdfRenderer>();
        pdf.Setup(p => p.RenderAsync(It.IsAny<string>())).ThrowsAsync(new Exception("chromium gone"));
        var (svc, db, _, _) = Create(pdf);
        var subscription = await SeedSubscriptionAsync(db);

        await Assert.ThrowsAsync<Exception>(() =>
            svc.GenerateForSubscriptionChargeAsync(subscription.Id, "charge-fail", 29900, DateTime.UtcNow));

        var kept = await db.Invoices.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(("charge-fail", 29900), (kept.VippsChargeId, kept.AmountInOre));
        Assert.Empty(kept.PdfData);
    }

    [Fact]
    public async Task RetryMissingPdfs_MakesFailedPdfsAndEmailsThem_LeavesOnesBeingMade()
    {
        var (svc, db, email, pdf) = Create();
        var order = await SeedPaidOrderAsync(db);
        var failed = new Invoice { OrderId = order.Id, AmountInOre = order.TotalInOre, IssuedAt = DateTime.UtcNow.AddHours(-1), PdfData = [],
            PdfAttemptedAt = DateTime.UtcNow - InvoiceService.PdfRetryAfter - TimeSpan.FromMinutes(1) };
        db.Invoices.Add(failed);
        var subscription = new garge_api.Models.Subscription.Subscription { UserId = "user-1", ProductId = 1, VippsAgreementId = "agr-r" };
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.Invoices.Add(new Invoice { SubscriptionId = subscription.Id, VippsChargeId = "charge-busy", AmountInOre = 29900, IssuedAt = DateTime.UtcNow,
            PdfData = [], PdfAttemptedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, await svc.RetryMissingPdfsAsync());

        var made = await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == failed.Id, TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 1, 2, 3 }, made.PdfData);
        Assert.Equal(failed.IssuedAt, made.IssuedAt);
        email.Verify(e => e.SendEmailAsync("buyer@example.com", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>?>()), Times.Once);
        Assert.Empty((await db.Invoices.AsNoTracking().SingleAsync(i => i.VippsChargeId == "charge-busy", TestContext.Current.CancellationToken)).PdfData);
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

    private static VatThresholdService RealVat(ApplicationDbContext db)
    {
        var cache = new Mock<IAppSettingsCache>();
        cache.Setup(c => c.GetAsync()).ReturnsAsync(() => db.AppSettings.AsNoTracking().Single(x => x.Id == 1));
        return new VatThresholdService(db, new Mock<ISecurityNotifier>().Object, cache.Object, NullLogger<VatThresholdService>.Instance);
    }

    private static async Task<Invoice> OwedInvoiceAsync(ApplicationDbContext db)
    {
        var order = await SeedPaidOrderAsync(db);
        var invoice = new Invoice { OrderId = order.Id, AmountInOre = 5_100_000, IssuedAt = DateTime.UtcNow.AddDays(-2), PdfData = [1] };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        return invoice;
    }

    [Fact]
    public async Task RegeneratingAnOrderInvoice_KeepsItsDate()
    {
        var (svc, db, _, _) = Create();
        var order = await SeedPaidOrderAsync(db);
        await svc.GenerateAndStoreAsync(order.Id);
        var issued = (await db.Invoices.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).IssuedAt;

        await Task.Delay(20, TestContext.Current.CancellationToken);
        await svc.GenerateAndStoreAsync(order.Id, force: true);

        Assert.Equal(issued, (await db.Invoices.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).IssuedAt);
    }

    [Fact]
    public async Task NewInvoices_CheckTheVatThreshold_RegeneratedOnesDoNot()
    {
        var vat = new Mock<IVatThresholdService>();
        var (svc, db, _, _) = Create(vat: _ => vat.Object);
        var order = await SeedPaidOrderAsync(db);
        await svc.GenerateAndStoreAsync(order.Id);
        await svc.GenerateAndStoreAsync(order.Id, force: true);
        vat.Verify(v => v.CheckAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);

        var subVat = new Mock<IVatThresholdService>();
        var (subSvc, subDb, _, _) = Create(vat: _ => subVat.Object);
        var subscription = await SeedSubscriptionAsync(subDb);
        await subSvc.GenerateForSubscriptionChargeAsync(subscription.Id, "charge-check", 29900, DateTime.UtcNow);
        subVat.Verify(v => v.CheckAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AFailedThresholdCheck_DoesNotFailTheInvoice()
    {
        var vat = new Mock<IVatThresholdService>();
        vat.Setup(v => v.CheckAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("boom"));
        var (svc, db, _, _) = Create(vat: _ => vat.Object);
        var order = await SeedPaidOrderAsync(db);

        var id = await svc.GenerateAndStoreAsync(order.Id);

        Assert.True(id > 0);
    }

    private static (InvoiceService svc, ApplicationDbContext db, Mock<IEmailService> email, Dictionary<string, string> pages) CreateForCorrections(Mock<IPdfRenderer>? pdf = null)
    {
        var pages = new Dictionary<string, string>();
        if (pdf == null)
        {
            pdf = new Mock<IPdfRenderer>();
            pdf.Setup(p => p.RenderAsync(It.IsAny<string>()))
                .Callback<string>(h => pages[h.Contains("CREDIT NOTE") ? "credit" : h.Contains("Replaces invoice") ? "invoice" : "other"] = h)
                .ReturnsAsync((string h) => h.Contains("CREDIT NOTE") ? new byte[] { 2 } : new byte[] { 3 });
        }
        var (svc, db, email, _) = Create(pdf, vat: RealVat);
        return (svc, db, email, pages);
    }

    private static async Task TurnVatOnAsync(ApplicationDbContext db)
    {
        var settings = await db.AppSettings.SingleAsync();
        settings.VatEnabled = true;
        settings.CompanyOrgNumber = "999 888 777";
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task VatCorrections_WithVatOff_AreRefused()
    {
        var (svc, db, email, pages) = CreateForCorrections();
        await OwedInvoiceAsync(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.GenerateVatCorrectionsAsync());
        Assert.Empty(pages);
        Assert.Equal(1, await db.Invoices.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VatCorrections_MakeACreditNoteAndANewInvoiceWithVat_AndEmailBothOnce()
    {
        var (svc, db, email, pages) = CreateForCorrections();
        var original = await OwedInvoiceAsync(db);
        await TurnVatOnAsync(db);

        Assert.Equal(1, await svc.GenerateVatCorrectionsAsync());
        Assert.Equal(0, await svc.GenerateVatCorrectionsAsync());

        var credit = await db.Invoices.AsNoTracking().SingleAsync(i => i.CreditsInvoiceId == original.Id, TestContext.Current.CancellationToken);
        var replacement = await db.Invoices.AsNoTracking().SingleAsync(i => i.ReplacesInvoiceId == original.Id, TestContext.Current.CancellationToken);
        Assert.Equal((InvoiceKind.CreditNote, -5_100_000, 0), (credit.Kind, credit.AmountInOre, credit.VatPercentage));
        Assert.Equal((InvoiceKind.Invoice, 5_100_000, 25), (replacement.Kind, replacement.AmountInOre, replacement.VatPercentage));
        Assert.Equal(new byte[] { 2 }, credit.PdfData);
        Assert.Equal(new byte[] { 3 }, replacement.PdfData);
        Assert.Null(credit.OrderId);
        Assert.Null(replacement.OrderId);

        Assert.Contains($"Credit for invoice #{original.Id:D4}", pages["credit"]);
        Assert.Contains($"Invoice #{replacement.Id:D4} replaces it", pages["credit"]);
        var page = pages["invoice"];
        Assert.Contains($"#{replacement.Id:D4}", page);
        Assert.Contains($"Replaces invoice #{original.Id:D4}", page);
        Assert.Contains($"credited by credit note #{credit.Id:D4}", page);
        Assert.Contains("Garge Sensor", page);
        Assert.Contains("999 888 777 MVA", page);
        Assert.Contains("Subtotal excl. VAT</td><td class=\"r\">NOK " + MoneyFormat.Nok(4_080_000), page);
        Assert.Contains("VAT 25%</td><td class=\"r\">NOK " + MoneyFormat.Nok(1_020_000), page);
        Assert.Contains("nothing more to pay", page);

        email.Verify(e => e.SendEmailAsync("buyer@example.com", It.Is<string>(t => t.Contains($"#{replacement.Id:D4}") && t.Contains($"#{original.Id:D4}")),
            It.IsAny<string>(), It.Is<IReadOnlyList<EmailAttachment>?>(a => a != null && a.Count == 2)), Times.Once);
        var saved = await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == original.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(saved.VatCorrectedAt);
        Assert.NotNull(saved.VatCorrectionEmailedAt);
        Assert.Equal(new byte[] { 1 }, saved.PdfData);
    }

    [Fact]
    public async Task VatCorrections_DoNotCountAsTurnover()
    {
        var (svc, db, _, _) = CreateForCorrections();
        var original = await OwedInvoiceAsync(db);
        await TurnVatOnAsync(db);
        var before = await RealVat(db).GetStatusAsync(DateTime.UtcNow);

        await svc.GenerateVatCorrectionsAsync();

        var after = await RealVat(db).GetStatusAsync(DateTime.UtcNow);
        Assert.Equal(before.TurnoverInOre, after.TurnoverInOre);
        var owed = Assert.Single(after.Owed);
        Assert.Equal(original.Id, owed.InvoiceId);
        Assert.NotNull(owed.CreditNoteId);
        Assert.NotNull(owed.ReplacementInvoiceId);
        Assert.NotNull(owed.CorrectedAt);
    }

    [Fact]
    public async Task VatCorrection_ForAPartlyRefundedSale_InvoicesWhatWasKept()
    {
        var (svc, db, _, pages) = CreateForCorrections();
        var original = await OwedInvoiceAsync(db);
        var order = await db.Orders.SingleAsync(o => o.Id == original.OrderId, TestContext.Current.CancellationToken);
        order.RefundedInOre = 50_000;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await TurnVatOnAsync(db);

        await svc.GenerateVatCorrectionsAsync();

        // 51 000 kr less 500 kr refunded is 50 500 kr, of which 10 100 kr is VAT. The credit note cancels the whole original.
        Assert.Equal(5_050_000, (await db.Invoices.AsNoTracking().SingleAsync(i => i.ReplacesInvoiceId == original.Id, TestContext.Current.CancellationToken)).AmountInOre);
        Assert.Equal(-5_100_000, (await db.Invoices.AsNoTracking().SingleAsync(i => i.CreditsInvoiceId == original.Id, TestContext.Current.CancellationToken)).AmountInOre);
        Assert.Contains("VAT 25%</td><td class=\"r\">NOK " + MoneyFormat.Nok(1_010_000), pages["invoice"]);
    }

    [Fact]
    public async Task VatCorrections_AreMadeInSaleOrder()
    {
        var (svc, db, _, _) = CreateForCorrections();
        var first = await OwedInvoiceAsync(db);
        var order2 = new Order { UserId = "user-1", TotalInOre = 10000, Status = OrderStatus.Paid };
        db.Orders.Add(order2);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var second = new Invoice { OrderId = order2.Id, AmountInOre = 10000, IssuedAt = DateTime.UtcNow.AddDays(-1), PdfData = [1] };
        db.Invoices.Add(second);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await TurnVatOnAsync(db);

        Assert.Equal(2, await svc.GenerateVatCorrectionsAsync());

        var firstCredit = (await db.Invoices.AsNoTracking().SingleAsync(i => i.CreditsInvoiceId == first.Id, TestContext.Current.CancellationToken)).Id;
        var secondCredit = (await db.Invoices.AsNoTracking().SingleAsync(i => i.CreditsInvoiceId == second.Id, TestContext.Current.CancellationToken)).Id;
        Assert.True(firstCredit < secondCredit);
    }

    [Fact]
    public async Task AVatCorrectionWhosePdfFailed_IsFinishedByTheRetryJob_AndEmailedOnce()
    {
        var pdf = new Mock<IPdfRenderer>();
        pdf.SetupSequence(p => p.RenderAsync(It.IsAny<string>()))
            .ThrowsAsync(new Exception("chromium gone"))
            .ReturnsAsync(new byte[] { 2 })
            .ReturnsAsync(new byte[] { 3 });
        var (svc, db, email, _) = CreateForCorrections(pdf);
        var original = await OwedInvoiceAsync(db);
        await TurnVatOnAsync(db);

        Assert.Equal(1, await svc.GenerateVatCorrectionsAsync());
        email.Verify(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>?>()), Times.Never);

        var saved = await db.Invoices.SingleAsync(i => i.Id == original.Id, TestContext.Current.CancellationToken);
        saved.VatCorrectedAt = DateTime.UtcNow - InvoiceService.PdfRetryAfter - TimeSpan.FromMinutes(1);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, await svc.RetryMissingPdfsAsync());
        Assert.Equal(0, await svc.RetryMissingPdfsAsync());

        email.Verify(e => e.SendEmailAsync("buyer@example.com", It.IsAny<string>(), It.IsAny<string>(), It.Is<IReadOnlyList<EmailAttachment>?>(a => a != null && a.Count == 2)), Times.Once);
        Assert.Equal(new byte[] { 2 }, (await db.Invoices.AsNoTracking().SingleAsync(i => i.CreditsInvoiceId == original.Id, TestContext.Current.CancellationToken)).PdfData);
    }

    [Fact]
    public async Task AVatCorrectionWhoseNewInvoicePdfFailed_KeepsTheCreditNotePdf()
    {
        var pdf = new Mock<IPdfRenderer>();
        pdf.SetupSequence(p => p.RenderAsync(It.IsAny<string>()))
            .ReturnsAsync(new byte[] { 2 })
            .ThrowsAsync(new Exception("chromium gone"))
            .ReturnsAsync(new byte[] { 3 });
        var (svc, db, email, _) = CreateForCorrections(pdf);
        var original = await OwedInvoiceAsync(db);
        await TurnVatOnAsync(db);

        await svc.GenerateVatCorrectionsAsync();
        var saved = await db.Invoices.SingleAsync(i => i.Id == original.Id, TestContext.Current.CancellationToken);
        saved.VatCorrectedAt = DateTime.UtcNow - InvoiceService.PdfRetryAfter - TimeSpan.FromMinutes(1);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await svc.RetryMissingPdfsAsync();

        Assert.Equal(new byte[] { 2 }, (await db.Invoices.AsNoTracking().SingleAsync(i => i.CreditsInvoiceId == original.Id, TestContext.Current.CancellationToken)).PdfData);
        Assert.Equal(new byte[] { 3 }, (await db.Invoices.AsNoTracking().SingleAsync(i => i.ReplacesInvoiceId == original.Id, TestContext.Current.CancellationToken)).PdfData);
        email.Verify(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>?>()), Times.Once);
    }
}
