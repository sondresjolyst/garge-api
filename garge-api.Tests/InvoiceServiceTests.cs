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
    private static (InvoiceService svc, ApplicationDbContext db, Mock<IEmailService> email) Create()
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
        var svc = new InvoiceService(scopeFactory.Object, email.Object,
            NullLogger<InvoiceService>.Instance);
        return (svc, db, email);
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
        return order;
    }

    [Fact]
    public async Task GenerateAndStoreAsync_ExistingInvoiceWithoutForce_ShortCircuits()
    {
        var (svc, db, email) = Create();
        var order = await SeedPaidOrderAsync(db);
        var existing = new Invoice { OrderId = order.Id, IssuedAt = DateTime.UtcNow.AddMinutes(-5), PdfData = [9, 9, 9] };
        db.Invoices.Add(existing);
        await db.SaveChangesAsync();

        var returnedId = await svc.GenerateAndStoreAsync(order.Id);

        Assert.Equal(existing.Id, returnedId);

        // No second invoice row
        Assert.Single(db.Invoices);
        // PDF bytes preserved (no re-render)
        Assert.Equal(new byte[] { 9, 9, 9 }, db.Invoices.Single().PdfData);
        // No email sent on the no-op path
        email.Verify(e => e.SendEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<EmailAttachment>?>()), Times.Never);
    }

    [Fact]
    public async Task GenerateAndStoreAsync_OrderMissing_Throws()
    {
        var (svc, _, _) = Create();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.GenerateAndStoreAsync(orderId: 99999));
    }
}
