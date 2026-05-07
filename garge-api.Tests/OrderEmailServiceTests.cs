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

public class OrderEmailServiceTests
{
    private static (OrderEmailService svc, ApplicationDbContext db, Mock<IEmailService> email) Create()
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
        var svc = new OrderEmailService(scopeFactory.Object, email.Object,
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
        await db.AppSettings.AddAsync(new AppSettings { Id = 1 });
        await db.SaveChangesAsync();

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
}
