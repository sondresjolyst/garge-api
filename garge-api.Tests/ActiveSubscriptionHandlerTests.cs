using garge_api.Authorization;
using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Services;
using garge_api.Models.Sensor;
using garge_api.Models.Subscription;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System.Security.Claims;
using Xunit;

namespace garge_api.Tests;

public class ActiveSubscriptionHandlerTests
{
    private const int PrimaryProductId = 1;
    private const int AddOnProductId = 2;

    private static (ActiveSubscriptionHandler handler, ApplicationDbContext db) Create()
    {
        var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        db.Products.AddRange(
            new Product { Id = PrimaryProductId, Name = "Primary", PriceInOre = 0, Interval = BillingInterval.Monthly, Type = ProductType.Primary },
            new Product { Id = AddOnProductId,  Name = "AddOn",   PriceInOre = 0, Interval = BillingInterval.Monthly, Type = ProductType.AddOn });
        db.SaveChanges();

        var capacityService = new SubscriptionCapacityService(db, new MemoryCache(new MemoryCacheOptions()));

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(ApplicationDbContext))).Returns(db);
        serviceProvider.Setup(sp => sp.GetService(typeof(ISubscriptionCapacityService))).Returns(capacityService);

        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(serviceProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var handler = new ActiveSubscriptionHandler(scopeFactory.Object);
        return (handler, db);
    }

    private static AuthorizationHandlerContext MakeContext(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        foreach (var role in roles)
            claims.Add(new(ClaimTypes.Role, role));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        return new AuthorizationHandlerContext([new ActiveSubscriptionRequirement()], principal, null);
    }

    private static Subscription Primary(string userId, SubscriptionStatus status = SubscriptionStatus.Active, int quantity = 1, bool isTest = false)
        => new()
        {
            UserId = userId, ProductId = PrimaryProductId,
            VippsAgreementId = $"primary-{userId}", Status = status, Quantity = quantity, IsTest = isTest,
        };

    private static Subscription AddOn(string userId, int quantity, SubscriptionStatus status = SubscriptionStatus.Active, bool isTest = false)
        => new()
        {
            UserId = userId, ProductId = AddOnProductId,
            VippsAgreementId = $"addon-{userId}-{Guid.NewGuid():N}", Status = status, Quantity = quantity, IsTest = isTest,
        };

    private static async Task Handle(ActiveSubscriptionHandler handler, AuthorizationHandlerContext ctx)
        => await ((IAuthorizationHandler)handler).HandleAsync(ctx);

    [Theory]
    [InlineData("Admin")]
    [InlineData("SensorAdmin")]
    [InlineData("MqttAdmin")]
    public async Task BypassRole_Succeeds_WithoutCheckingDb(string role)
    {
        var (handler, _) = Create();
        var ctx = MakeContext("user-1", role);

        await Handle(handler, ctx);

        Assert.True(ctx.HasSucceeded);
    }

    [Fact]
    public async Task NoSubscription_NoOwnedSensors_Fails()
    {
        // A brand-new user with no Primary cannot claim their first sensor.
        var (handler, db) = Create();
        await db.AppSettings.AddAsync(new AppSettings { Id = 1 });
        await db.SaveChangesAsync();

        var ctx = MakeContext("user-1");
        await Handle(handler, ctx);

        Assert.False(ctx.HasSucceeded);
    }

    [Fact]
    public async Task PrimaryActive_NoOwnedSensors_Succeeds()
    {
        // Primary covers the first sensor — claim is allowed.
        var (handler, db) = Create();
        await db.AppSettings.AddAsync(new AppSettings { Id = 1 });
        await db.Subscriptions.AddAsync(Primary("user-1"));
        await db.SaveChangesAsync();

        var ctx = MakeContext("user-1");
        await Handle(handler, ctx);

        Assert.True(ctx.HasSucceeded);
    }

    [Fact]
    public async Task PrimaryActive_OneOwnedSensor_Fails()
    {
        // Primary covers exactly one sensor; without AddOn, the next claim is denied.
        var (handler, db) = Create();
        await db.AppSettings.AddAsync(new AppSettings { Id = 1 });
        await db.UserSensors.AddAsync(new UserSensor { UserId = "user-1", SensorId = 1, IsOwner = true });
        await db.Subscriptions.AddAsync(Primary("user-1"));
        await db.SaveChangesAsync();

        var ctx = MakeContext("user-1");
        await Handle(handler, ctx);

        Assert.False(ctx.HasSucceeded);
    }

    [Fact]
    public async Task PrimaryPlusAddOnQuantityTwo_TwoOwnedSensors_Succeeds()
    {
        // Capacity = 1 (Primary) + 2 (AddOn × 2) = 3; with 2 owned the user can claim a third.
        var (handler, db) = Create();
        await db.AppSettings.AddAsync(new AppSettings { Id = 1 });
        await db.UserSensors.AddRangeAsync(
            new UserSensor { UserId = "user-1", SensorId = 1, IsOwner = true },
            new UserSensor { UserId = "user-1", SensorId = 2, IsOwner = true });
        await db.Subscriptions.AddRangeAsync(Primary("user-1"), AddOn("user-1", quantity: 2));
        await db.SaveChangesAsync();

        var ctx = MakeContext("user-1");
        await Handle(handler, ctx);

        Assert.True(ctx.HasSucceeded);
    }

    [Fact]
    public async Task AddOnWithoutPrimary_Fails()
    {
        // AddOn alone does not grant capacity — Primary is required as the base.
        var (handler, db) = Create();
        await db.AppSettings.AddAsync(new AppSettings { Id = 1 });
        await db.Subscriptions.AddAsync(AddOn("user-1", quantity: 5));
        await db.SaveChangesAsync();

        var ctx = MakeContext("user-1");
        await Handle(handler, ctx);

        Assert.False(ctx.HasSucceeded);
    }

    [Fact]
    public async Task PendingPrimary_NotCountedAsActive()
    {
        var (handler, db) = Create();
        await db.AppSettings.AddAsync(new AppSettings { Id = 1 });
        await db.Subscriptions.AddAsync(Primary("user-1", status: SubscriptionStatus.Pending));
        await db.SaveChangesAsync();

        var ctx = MakeContext("user-1");
        await Handle(handler, ctx);

        Assert.False(ctx.HasSucceeded);
    }

    [Fact]
    public async Task SharedSensor_IsOwnerFalse_NotCountedForBilling()
    {
        // User owns 1 sensor (covered by Primary). A shared sensor (IsOwner=false) must not
        // push them over capacity, so the user can still claim another sensor.
        var (handler, db) = Create();
        await db.AppSettings.AddAsync(new AppSettings { Id = 1 });
        await db.UserSensors.AddRangeAsync(
            new UserSensor { UserId = "user-1", SensorId = 1, IsOwner = false });
        await db.Subscriptions.AddAsync(Primary("user-1"));
        await db.SaveChangesAsync();

        var ctx = MakeContext("user-1");
        await Handle(handler, ctx);

        Assert.True(ctx.HasSucceeded);
    }

    [Fact]
    public async Task TestModeOff_TestSubscription_NotCounted()
    {
        var (handler, db) = Create();
        await db.AppSettings.AddAsync(new AppSettings { Id = 1, VippsTestMode = false });
        await db.Subscriptions.AddAsync(Primary("user-1", isTest: true));
        await db.SaveChangesAsync();

        var ctx = MakeContext("user-1");
        await Handle(handler, ctx);

        Assert.False(ctx.HasSucceeded);
    }

    [Fact]
    public async Task TestModeOn_TestSubscription_Counted()
    {
        var (handler, db) = Create();
        await db.AppSettings.AddAsync(new AppSettings { Id = 1, VippsTestMode = true });
        await db.Subscriptions.AddAsync(Primary("user-1", isTest: true));
        await db.SaveChangesAsync();

        var ctx = MakeContext("user-1");
        await Handle(handler, ctx);

        Assert.True(ctx.HasSucceeded);
    }

    [Fact]
    public async Task SuspendedOwnedSensor_NotCounted_FreesCapacity()
    {
        // The user owns one sensor but has turned it off (SuspendedAt set). It must not consume the
        // Primary's single slot, so they can claim a different sensor.
        var (handler, db) = Create();
        await db.AppSettings.AddAsync(new AppSettings { Id = 1 });
        await db.UserSensors.AddAsync(new UserSensor { UserId = "user-1", SensorId = 1, IsOwner = true, SuspendedAt = DateTime.UtcNow });
        await db.Subscriptions.AddAsync(Primary("user-1"));
        await db.SaveChangesAsync();

        var ctx = MakeContext("user-1");
        await Handle(handler, ctx);

        Assert.True(ctx.HasSucceeded);
    }

    [Fact]
    public async Task CancelledPrimary_WithinPaidPeriod_StillGrantsCapacity()
    {
        // Cancelled (Stopped) but paid through a future NextChargeDate — capacity continues until then.
        var (handler, db) = Create();
        await db.AppSettings.AddAsync(new AppSettings { Id = 1 });
        var sub = Primary("user-1", status: SubscriptionStatus.Stopped);
        sub.NextChargeDate = DateTime.UtcNow.AddDays(10);
        await db.Subscriptions.AddAsync(sub);
        await db.SaveChangesAsync();

        var ctx = MakeContext("user-1");
        await Handle(handler, ctx);

        Assert.True(ctx.HasSucceeded);
    }

    [Fact]
    public async Task CancelledPrimary_AfterPaidPeriod_NoCapacity()
    {
        // Stopped and the paid period has ended — no capacity.
        var (handler, db) = Create();
        await db.AppSettings.AddAsync(new AppSettings { Id = 1 });
        var sub = Primary("user-1", status: SubscriptionStatus.Stopped);
        sub.NextChargeDate = DateTime.UtcNow.AddDays(-1);
        await db.Subscriptions.AddAsync(sub);
        await db.SaveChangesAsync();

        var ctx = MakeContext("user-1");
        await Handle(handler, ctx);

        Assert.False(ctx.HasSucceeded);
    }
}
