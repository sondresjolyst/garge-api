using garge_api.Models;
using garge_api.Models.Shop;
using garge_api.Models.Subscription;
using garge_api.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace garge_api.Tests;

public class DeletedUserPurgeServiceTests
{
    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static User AddDeletedUser(ApplicationDbContext db, string id, bool deleted = true)
    {
        var user = new User
        {
            Id = id, UserName = id, Email = $"{id}@x", FirstName = "Deleted", LastName = "User",
            IsDeleted = deleted, DeletedAt = deleted ? DateTime.UtcNow.AddYears(-6) : null,
        };
        db.Users.Add(user);
        db.UserProfiles.Add(new UserProfile { Id = id, User = user });
        return user;
    }

    private static void AddOrderWithInvoice(ApplicationDbContext db, string userId, DateTime invoiceIssuedAt)
    {
        var order = new Order { UserId = userId, TotalInOre = 1000, Status = OrderStatus.Paid };
        db.Orders.Add(order);
        db.SaveChanges();
        db.OrderItems.Add(new OrderItem
        {
            OrderId = order.Id, ShopItemId = 1, Quantity = 1,
            PriceAtPurchaseInOre = 1000, UnitPriceExclVatInOre = 800, VatPercentage = 25,
        });
        db.Invoices.Add(new Invoice { OrderId = order.Id, AmountInOre = 1000, IssuedAt = invoiceIssuedAt, PdfData = [1] });
        db.SaveChanges();
    }

    [Fact]
    public async Task Purge_LastInvoicePast5Years_RemovesUserAndWholeTrail()
    {
        using var db = NewDb();
        AddDeletedUser(db, "u1");
        AddOrderWithInvoice(db, "u1", DateTime.UtcNow.AddYears(-6));
        db.Subscriptions.Add(new Subscription
        {
            UserId = "u1", ProductId = 1, VippsAgreementId = "agr-u1", Quantity = 1,
            Status = SubscriptionStatus.Stopped, ConsentIp = "203.0.113.7", BillingAddress = "Somewhere 1",
        });
        await db.SaveChangesAsync(Ct);

        var purged = await DeletedUserPurgeService.PurgeAsync(db, Ct);

        Assert.Equal(1, purged);
        Assert.False(await db.Users.AnyAsync(u => u.Id == "u1", Ct));
        Assert.Empty(db.Orders);
        Assert.Empty(db.OrderItems);
        Assert.Empty(db.Invoices);
        Assert.Empty(db.Subscriptions);
        Assert.Empty(db.UserProfiles);
    }

    [Fact]
    public async Task Purge_InvoiceWithinRetention_KeepsUser()
    {
        using var db = NewDb();
        AddDeletedUser(db, "u1");
        AddOrderWithInvoice(db, "u1", DateTime.UtcNow); // invoiced this year
        await db.SaveChangesAsync(Ct);

        var purged = await DeletedUserPurgeService.PurgeAsync(db, Ct);

        Assert.Equal(0, purged);
        Assert.True(await db.Users.AnyAsync(u => u.Id == "u1", Ct));
        Assert.Single(db.Invoices);
    }

    [Fact]
    public async Task Purge_NoInvoices_RemovesImmediately()
    {
        using var db = NewDb();
        AddDeletedUser(db, "u1");
        await db.SaveChangesAsync(Ct);

        var purged = await DeletedUserPurgeService.PurgeAsync(db, Ct);

        Assert.Equal(1, purged);
        Assert.False(await db.Users.AnyAsync(u => u.Id == "u1", Ct));
        Assert.Empty(db.UserProfiles);
    }

    [Fact]
    public async Task Purge_ActiveUser_Untouched()
    {
        using var db = NewDb();
        AddDeletedUser(db, "active", deleted: false);
        AddOrderWithInvoice(db, "active", DateTime.UtcNow.AddYears(-6)); // old invoice, but not deleted
        await db.SaveChangesAsync(Ct);

        var purged = await DeletedUserPurgeService.PurgeAsync(db, Ct);

        Assert.Equal(0, purged);
        Assert.True(await db.Users.AnyAsync(u => u.Id == "active", Ct));
    }

    [Fact]
    public async Task Purge_OtherUsersData_NotTouched()
    {
        using var db = NewDb();
        AddDeletedUser(db, "u1");                                   // eligible (no invoices)
        AddDeletedUser(db, "u2", deleted: false);                  // active, has a recent order
        AddOrderWithInvoice(db, "u2", DateTime.UtcNow);
        await db.SaveChangesAsync(Ct);

        var purged = await DeletedUserPurgeService.PurgeAsync(db, Ct);

        Assert.Equal(1, purged);
        Assert.True(await db.Users.AnyAsync(u => u.Id == "u2", Ct));
        Assert.Single(db.Orders);
        Assert.Single(db.Invoices);
    }

    [Theory]
    [InlineData(0, false, true)]    // no invoices -> eligible
    [InlineData(-6, true, true)]    // last invoice 6 years ago -> eligible
    [InlineData(0, true, false)]    // invoice this year -> not yet
    [InlineData(-5, true, false)]   // 5 years ago -> still within window (kept through year+5)
    public void IsRetentionExpired_AnchorsOnLastInvoiceYearPlusFive(int yearsAgo, bool hasInvoice, bool expected)
    {
        var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var dates = hasInvoice ? new[] { now.AddYears(yearsAgo) } : System.Array.Empty<DateTime>();

        Assert.Equal(expected, DeletedUserPurgeService.IsRetentionExpired(dates, now));
    }
}
