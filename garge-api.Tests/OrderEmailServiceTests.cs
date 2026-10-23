using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Models.Shop;
using garge_api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SjolystInnovation.AppKit.Pdf;
using Xunit;

namespace garge_api.Tests;

public class OrderEmailServiceTests
{
    private static readonly byte[] PdfBytes = [0x25, 0x50, 0x44, 0x46];

    private static (OrderEmailService svc, ApplicationDbContext db, Mock<IEmailService> email) Create(Mock<IPdfRenderer>? pdfRenderer = null)
    {
        var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(ApplicationDbContext))).Returns(db);

        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(serviceProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var email = new Mock<IEmailService>();
        var pdf = pdfRenderer ?? new Mock<IPdfRenderer>();
        if (pdfRenderer == null)
            pdf.Setup(p => p.RenderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(PdfBytes);
        var svc = new OrderEmailService(scopeFactory.Object, email.Object, pdf.Object,
            NullLogger<OrderEmailService>.Instance);
        return (svc, db, email);
    }

    private static async Task<Order> SeedOrderAsync(ApplicationDbContext db, string? email = "buyer@example.com",
        string? shippingAddress = null, string firstName = "Sondre")
    {
        await db.AppSettings.AddAsync(new AppSettings { Id = 1, CompanyName = "Garge" });
        var user = new User
        {
            Id = "user-1", Email = email, UserName = email,
            FirstName = firstName, LastName = "Sjølyst"
        };
        await db.Users.AddAsync(user);

        var item = new ShopItem { Id = 1, Name = "Garge Sensor", PriceInOre = 50000, IsActive = true };
        await db.ShopItems.AddAsync(item);

        var order = new Order
        {
            UserId = user.Id, TotalInOre = 50000, Status = OrderStatus.Reserved,
            ShippingAddress = shippingAddress
        };
        await db.Orders.AddAsync(order);
        await db.SaveChangesAsync();

        await db.OrderItems.AddAsync(new OrderItem
        {
            OrderId = order.Id, ShopItemId = item.Id, Quantity = 1,
            PriceAtPurchaseInOre = 50000, UnitPriceExclVatInOre = 50000, VatPercentage = 0
        });
        await db.SaveChangesAsync();
        return order;
    }

    [Fact]
    public async Task SendOrderConfirmedAsync_SendsEmailWithSubjectAndItemTotal()
    {
        var (svc, db, email) = Create();
        var order = await SeedOrderAsync(db);

        await svc.SendOrderConfirmedAsync(order.Id);

        email.Verify(e => e.SendEmailAsync(
            "buyer@example.com",
            It.Is<string>(s => s.Contains($"#{order.Id}") && s.Contains("Garge")),
            It.Is<string>(html => html.Contains("Garge Sensor") && html.Contains("500.00")),
            It.IsAny<IReadOnlyList<EmailAttachment>?>()), Times.Once);
    }

    [Fact]
    public async Task SendOrderConfirmedAsync_IncludesShippingAddress_WhenPresent()
    {
        var (svc, db, email) = Create();
        var order = await SeedOrderAsync(db, shippingAddress: "Mårvegen 21a, 4347 Lye");

        await svc.SendOrderConfirmedAsync(order.Id);

        email.Verify(e => e.SendEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.Is<string>(html => html.Contains("21a, 4347 Lye") && html.Contains("Ship to:")),
            It.IsAny<IReadOnlyList<EmailAttachment>?>()), Times.Once);
    }

    [Fact]
    public async Task SendOrderConfirmedAsync_OmitsShippingBlock_WhenNull()
    {
        var (svc, db, email) = Create();
        var order = await SeedOrderAsync(db, shippingAddress: null);

        await svc.SendOrderConfirmedAsync(order.Id);

        email.Verify(e => e.SendEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.Is<string>(html => !html.Contains("Ship to:")),
            It.IsAny<IReadOnlyList<EmailAttachment>?>()), Times.Once);
    }

    [Fact]
    public async Task SendOrderConfirmedAsync_DoesNotSend_WhenUserMissingEmail()
    {
        var (svc, db, email) = Create();
        var order = await SeedOrderAsync(db, email: null);

        await svc.SendOrderConfirmedAsync(order.Id);

        email.Verify(e => e.SendEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>?>()), Times.Never);
    }

    [Fact]
    public async Task SendOrderConfirmedAsync_DoesNotSend_WhenOrderMissing()
    {
        var (svc, db, email) = Create();
        await db.AppSettings.AddAsync(new AppSettings { Id = 1 }, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await svc.SendOrderConfirmedAsync(orderId: 99999);

        email.Verify(e => e.SendEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>?>()), Times.Never);
    }

    [Fact]
    public async Task SendOrderConfirmedAsync_SwallowsEmailServiceException()
    {
        var (svc, db, email) = Create();
        var order = await SeedOrderAsync(db);
        email.Setup(e => e.SendEmailAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<EmailAttachment>?>()))
            .ThrowsAsync(new Exception("brevo down"));

        await svc.SendOrderConfirmedAsync(order.Id);
    }

    [Fact]
    public async Task SendOrderConfirmedAsync_AttachesWithdrawalFormPdf()
    {
        var (svc, db, email) = Create();
        var order = await SeedOrderAsync(db);
        IReadOnlyList<EmailAttachment>? sent = null;
        string? html = null;
        email.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<EmailAttachment>?>()))
            .Callback<string, string, string, IReadOnlyList<EmailAttachment>?>((_, _, h, a) => { html = h; sent = a; })
            .ReturnsAsync(true);

        await svc.SendOrderConfirmedAsync(order.Id);

        var attachment = Assert.Single(sent!);
        Assert.Equal("withdrawal-form.pdf", attachment.FileName);
        Assert.Equal("application/pdf", attachment.ContentType);
        Assert.Equal(PdfBytes, attachment.Content);
        Assert.Contains("The withdrawal form is attached", html);
    }

    [Fact]
    public async Task SendOrderConfirmedAsync_RendersFilledInEnglishForm()
    {
        var pdf = new Mock<IPdfRenderer>();
        string? rendered = null;
        pdf.Setup(p => p.RenderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((h, _) => rendered = h)
            .ReturnsAsync(PdfBytes);
        var (svc, db, _) = Create(pdf);
        var order = await SeedOrderAsync(db);

        await svc.SendOrderConfirmedAsync(order.Id);

        Assert.NotNull(rendered);
        Assert.Contains("Model withdrawal form", rendered);
        Assert.Contains("Garge Sensor", rendered);
        Assert.Contains($"#{order.Id}", rendered);
    }

    [Fact]
    public async Task SendOrderConfirmedAsync_SendsWithoutAttachment_WhenRenderFails()
    {
        var pdf = new Mock<IPdfRenderer>();
        pdf.Setup(p => p.RenderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("chromium down"));
        var (svc, db, email) = Create(pdf);
        var order = await SeedOrderAsync(db);

        await svc.SendOrderConfirmedAsync(order.Id);

        email.Verify(e => e.SendEmailAsync(
            "buyer@example.com",
            It.Is<string>(s => s.Contains($"#{order.Id}")),
            It.Is<string>(html => html.Contains("Garge Sensor") && !html.Contains("withdrawal form")),
            It.Is<IReadOnlyList<EmailAttachment>?>(a => a != null && a.Count == 0)), Times.Once);
    }

    [Fact]
    public void BuildWithdrawalForm_MapsSellerFromSettings()
    {
        var settings = new AppSettings
        {
            CompanyName = "Garge", CompanyLegalName = "Sjølyst Innovation AS", OrgNumber = "938 517 789",
            StreetAddress = "Mårvegen 21a", PostalCode = "4347", AddressLocality = "Lye",
            PublicEmail = "post@example.com"
        };
        var order = new Order { Id = 42, UserId = "u", CreatedAt = DateTime.UtcNow };

        var seller = OrderEmailService.BuildWithdrawalForm(order, settings).Seller;

        Assert.Equal("Garge", seller.Name);
        Assert.Equal("Sjølyst Innovation AS", seller.LegalName);
        Assert.Equal("938 517 789", seller.OrganizationNumber);
        Assert.Equal(["Mårvegen 21a, 4347 Lye"], seller.AddressLines);
        Assert.Equal("post@example.com", seller.Email);
    }

    [Fact]
    public void BuildWithdrawalForm_MapsOrderItemsOnePerLineAndEnglish()
    {
        var order = new Order
        {
            Id = 42, UserId = "u", CreatedAt = DateTime.UtcNow,
            OrderItems =
            {
                new OrderItem { Quantity = 2, ShopItem = new ShopItem { Name = "Garge Sensor", PriceInOre = 1 } },
                new OrderItem { Quantity = 1, ShopItem = new ShopItem { Name = "Garge Switch", PriceInOre = 1 } }
            }
        };

        var request = OrderEmailService.BuildWithdrawalForm(order, new AppSettings());

        Assert.Equal(WithdrawalFormLanguage.English, request.Language);
        Assert.Equal("#42", request.Order.Reference);
        Assert.Equal(["Garge Sensor", "Garge Switch"], request.Order.Items);
        Assert.Null(request.Order.ReceivedOn);
    }

    [Theory]
    // 23:30 UTC on 31 December is already 1 January in Oslo (UTC+1 in winter).
    [InlineData("2026-12-31T23:30:00Z", "2027-01-01")]
    // 21:30 UTC on 30 June is 23:30 in Oslo (UTC+2 in summer), still the same day.
    [InlineData("2026-06-30T21:30:00Z", "2026-06-30")]
    // 22:30 UTC on 30 June is past midnight in Oslo.
    [InlineData("2026-06-30T22:30:00Z", "2026-07-01")]
    public void BuildWithdrawalForm_UsesOsloOrderDate(string createdAtUtc, string expected)
    {
        var createdAt = DateTime.Parse(createdAtUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        var order = new Order { Id = 1, UserId = "u", CreatedAt = createdAt };

        var request = OrderEmailService.BuildWithdrawalForm(order, new AppSettings());

        Assert.Equal(DateOnly.Parse(expected), request.Order.OrderedOn);
    }
}
