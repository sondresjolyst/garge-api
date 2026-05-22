using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Models.Sensor;
using garge_api.Models.Subscription;
using garge_api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// Verifies the daily reconciliation sweep: over-quota owners get their newest excess sensors
/// auto-suspended (oldest kept), within-quota owners are untouched.
/// </summary>
public class QuotaReconciliationServiceTests : ControllerTestBase
{
    private static ISubscriptionCapacityService Capacity(ApplicationDbContext db) =>
        new SubscriptionCapacityService(db, new MemoryCache(new MemoryCacheOptions()));

    private static UserSensor Owned(string userId, int sensorId, DateTime createdAt) =>
        new() { UserId = userId, SensorId = sensorId, IsOwner = true, CreatedAt = createdAt, SuspendedAt = null };

    private static void SeedPrimary(ApplicationDbContext db, string userId)
    {
        db.AppSettings.Add(new AppSettings { Id = 1 });
        db.Products.Add(new Product { Id = 1, Name = "Primary", PriceInOre = 0, Interval = BillingInterval.Monthly, Type = ProductType.Primary });
        db.Subscriptions.Add(new Subscription { UserId = userId, ProductId = 1, VippsAgreementId = "a", Status = SubscriptionStatus.Active, Quantity = 1 });
    }

    [Fact]
    public async Task Reconcile_OverCapacity_SuspendsNewestExcess_KeepsOldest()
    {
        using var db = CreateDbContext();
        SeedPrimary(db, "u"); // capacity = 1
        var t0 = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        db.UserSensors.AddRange(
            Owned("u", 1, t0),
            Owned("u", 2, t0.AddDays(1)),
            Owned("u", 3, t0.AddDays(2)));
        await db.SaveChangesAsync();

        var suspended = await QuotaReconciliationService.ReconcileAsync(db, Capacity(db));

        Assert.Equal(2, suspended);
        Assert.Null(db.UserSensors.Single(x => x.SensorId == 1).SuspendedAt);   // oldest kept
        Assert.NotNull(db.UserSensors.Single(x => x.SensorId == 2).SuspendedAt); // newest suspended
        Assert.NotNull(db.UserSensors.Single(x => x.SensorId == 3).SuspendedAt);
    }

    [Fact]
    public async Task Reconcile_WithinCapacity_DoesNothing()
    {
        using var db = CreateDbContext();
        SeedPrimary(db, "u"); // capacity = 1
        db.UserSensors.Add(Owned("u", 1, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        var suspended = await QuotaReconciliationService.ReconcileAsync(db, Capacity(db));

        Assert.Equal(0, suspended);
        Assert.Null(db.UserSensors.Single().SuspendedAt);
    }

    [Fact]
    public async Task Reconcile_NoSubscription_SuspendsAllOwned()
    {
        using var db = CreateDbContext();
        db.AppSettings.Add(new AppSettings { Id = 1 }); // capacity = 0 (no Primary)
        var t0 = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        db.UserSensors.AddRange(Owned("u", 1, t0), Owned("u", 2, t0.AddDays(1)));
        await db.SaveChangesAsync();

        var suspended = await QuotaReconciliationService.ReconcileAsync(db, Capacity(db));

        Assert.Equal(2, suspended);
        Assert.All(db.UserSensors.ToList(), us => Assert.NotNull(us.SuspendedAt));
    }

    [Fact]
    public async Task Reconcile_SubscriptionBypassRole_DoesNotSuspend()
    {
        // A ComplimentaryUser (or service account / admin) has no capacity limit, even with no subscription.
        using var db = CreateDbContext();
        db.AppSettings.Add(new AppSettings { Id = 1 }); // no Primary → capacity 0
        GrantRole(db, "u", "ComplimentaryUser");
        var t0 = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        db.UserSensors.AddRange(Owned("u", 1, t0), Owned("u", 2, t0.AddDays(1)));
        await db.SaveChangesAsync();

        var suspended = await QuotaReconciliationService.ReconcileAsync(db, Capacity(db));

        Assert.Equal(0, suspended);
        Assert.All(db.UserSensors.ToList(), us => Assert.Null(us.SuspendedAt));
    }

    private static void GrantRole(ApplicationDbContext db, string userId, string roleName)
    {
        var roleId = $"role-{roleName}";
        db.Roles.Add(new IdentityRole { Id = roleId, Name = roleName, NormalizedName = roleName.ToUpperInvariant() });
        db.UserRoles.Add(new IdentityUserRole<string> { UserId = userId, RoleId = roleId });
    }
}
