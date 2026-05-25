using garge_api.Controllers;
using garge_api.Dtos.Sensor;
using garge_api.Hubs;
using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Models.Sensor;
using garge_api.Models.Subscription;
using garge_api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace garge_api.Tests;

public class SensorCapacityEndpointTests : ControllerTestBase
{
    private SensorController CreateController(ApplicationDbContext db, string userId)
    {
        var hub = new Mock<IHubContext<DeviceHub>>();
        var capacity = new SubscriptionCapacityService(db, new MemoryCache(new MemoryCacheOptions()));
        var controller = new SensorController(
            db, MockMapper.Object, NullLogger<SensorController>.Instance,
            new Mock<IDeviceOwnershipService>().Object, hub.Object, capacity);
        controller.ControllerContext = MakeControllerContext(userId);
        return controller;
    }

    private static void GrantRole(ApplicationDbContext db, string userId, string roleName)
    {
        var roleId = $"role-{roleName}";
        db.Roles.Add(new IdentityRole { Id = roleId, Name = roleName, NormalizedName = roleName.ToUpperInvariant() });
        db.UserRoles.Add(new IdentityUserRole<string> { UserId = userId, RoleId = roleId });
    }

    private static void AddActivePrimary(ApplicationDbContext db, string userId)
    {
        db.Products.Add(new Product { Id = 1, Name = "Primary", PriceInOre = 0, Interval = BillingInterval.Monthly, Type = ProductType.Primary });
        db.Subscriptions.Add(new Subscription { UserId = userId, ProductId = 1, VippsAgreementId = "a", Status = SubscriptionStatus.Active, Quantity = 1 });
    }

    [Fact]
    public async Task GetMyCapacity_NoSubscription_ZeroAndCannotClaim()
    {
        using var db = CreateDbContext();
        db.AppSettings.Add(new AppSettings { Id = 1 });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateController(db, "u").GetMyCapacity();

        var dto = Assert.IsType<SensorCapacityDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(0, dto.Capacity);
        Assert.False(dto.Bypass);
        Assert.False(dto.CanClaim);
    }

    [Fact]
    public async Task GetMyCapacity_ActivePrimaryWithRoom_CanClaim()
    {
        using var db = CreateDbContext();
        db.AppSettings.Add(new AppSettings { Id = 1 });
        AddActivePrimary(db, "u"); // capacity 1
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateController(db, "u").GetMyCapacity();

        var dto = Assert.IsType<SensorCapacityDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(1, dto.Capacity);
        Assert.Equal(0, dto.Used);
        Assert.True(dto.CanClaim);
    }

    [Fact]
    public async Task GetMyCapacity_ComplimentaryUser_BypassesWithNoSubscription()
    {
        using var db = CreateDbContext();
        db.AppSettings.Add(new AppSettings { Id = 1 });
        GrantRole(db, "u", "ComplimentaryUser");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateController(db, "u").GetMyCapacity();

        var dto = Assert.IsType<SensorCapacityDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.True(dto.Bypass);
        Assert.True(dto.CanClaim); // claims allowed despite capacity 0 + no subscription
    }
}
