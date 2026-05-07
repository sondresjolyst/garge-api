using garge_api.Authorization;
using garge_api.Models;
using garge_api.Models.Admin;
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
    private static (ActiveSubscriptionHandler handler, ApplicationDbContext db) Create()
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

        var handler = new ActiveSubscriptionHandler(scopeFactory.Object, new MemoryCache(new MemoryCacheOptions()));
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
    public async Task PureViewer_NoOwnedSensors_Succeeds()
    {
        var (handler, db) = Create();
        await db.AppSettings.AddAsync(new AppSettings { Id = 1 });
        await db.SaveChangesAsync();

        var ctx = MakeContext("viewer-1");
        await Handle(handler, ctx);

        Assert.True(ctx.HasSucceeded);
    }

    [Fact]
    public async Task OneOwnedSensor_OneActiveSubscription_Succeeds()
    {
        var (handler, db) = Create();
        await db.AppSettings.AddAsync(new AppSettings { Id = 1 });
        await db.UserSensors.AddAsync(new UserSensor { UserId = "user-1", SensorId = 1, IsOwner = true });
        await db.Subscriptions.AddAsync(new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr1", Status = SubscriptionStatus.Active
        });
        await db.SaveChangesAsync();

        var ctx = MakeContext("user-1");
        await Handle(handler, ctx);

        Assert.True(ctx.HasSucceeded);
    }

    [Fact]
    public async Task TwoOwnedSensors_OneSubscription_Fails()
    {
        var (handler, db) = Create();
        await db.AppSettings.AddAsync(new AppSettings { Id = 1 });
        await db.UserSensors.AddRangeAsync(
            new UserSensor { UserId = "user-1", SensorId = 1, IsOwner = true },
            new UserSensor { UserId = "user-1", SensorId = 2, IsOwner = true });
        await db.Subscriptions.AddAsync(new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr1", Status = SubscriptionStatus.Active
        });
        await db.SaveChangesAsync();

        var ctx = MakeContext("user-1");
        await Handle(handler, ctx);

        Assert.False(ctx.HasSucceeded);
    }

    [Fact]
    public async Task TwoOwnedSensors_TwoSubscriptions_Succeeds()
    {
        var (handler, db) = Create();
        await db.AppSettings.AddAsync(new AppSettings { Id = 1 });
        await db.UserSensors.AddRangeAsync(
            new UserSensor { UserId = "user-1", SensorId = 1, IsOwner = true },
            new UserSensor { UserId = "user-1", SensorId = 2, IsOwner = true });
        await db.Subscriptions.AddRangeAsync(
            new Subscription { UserId = "user-1", ProductId = 1, VippsAgreementId = "agr1", Status = SubscriptionStatus.Active },
            new Subscription { UserId = "user-1", ProductId = 2, VippsAgreementId = "agr2", Status = SubscriptionStatus.Active });
        await db.SaveChangesAsync();

        var ctx = MakeContext("user-1");
        await Handle(handler, ctx);

        Assert.True(ctx.HasSucceeded);
    }

    [Fact]
    public async Task OwnedSensor_PendingSubscriptionOnly_Fails()
    {
        var (handler, db) = Create();
        await db.AppSettings.AddAsync(new AppSettings { Id = 1 });
        await db.UserSensors.AddAsync(new UserSensor { UserId = "user-1", SensorId = 1, IsOwner = true });
        await db.Subscriptions.AddAsync(new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr1", Status = SubscriptionStatus.Pending
        });
        await db.SaveChangesAsync();

        var ctx = MakeContext("user-1");
        await Handle(handler, ctx);

        Assert.False(ctx.HasSucceeded);
    }

    [Fact]
    public async Task SharedSensor_IsOwnerFalse_NotCountedForBilling()
    {
        var (handler, db) = Create();
        await db.AppSettings.AddAsync(new AppSettings { Id = 1 });
        // User owns 1 sensor, has 1 subscription — should succeed
        // Plus 1 shared sensor (IsOwner=false) that must NOT count towards billing
        await db.UserSensors.AddRangeAsync(
            new UserSensor { UserId = "user-1", SensorId = 1, IsOwner = true },
            new UserSensor { UserId = "user-1", SensorId = 2, IsOwner = false });
        await db.Subscriptions.AddAsync(new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr1", Status = SubscriptionStatus.Active
        });
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
        await db.UserSensors.AddAsync(new UserSensor { UserId = "user-1", SensorId = 1, IsOwner = true });
        await db.Subscriptions.AddAsync(new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr1", Status = SubscriptionStatus.Active,
            IsTest = true
        });
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
        await db.UserSensors.AddAsync(new UserSensor { UserId = "user-1", SensorId = 1, IsOwner = true });
        await db.Subscriptions.AddAsync(new Subscription
        {
            UserId = "user-1", ProductId = 1,
            VippsAgreementId = "agr1", Status = SubscriptionStatus.Active,
            IsTest = true
        });
        await db.SaveChangesAsync();

        var ctx = MakeContext("user-1");
        await Handle(handler, ctx);

        Assert.True(ctx.HasSucceeded);
    }
}
