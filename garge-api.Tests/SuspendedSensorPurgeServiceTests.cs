using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Models.Sensor;
using garge_api.Models.Subscription;
using garge_api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// Verifies the 6-month suspension cap. By default a claimed sensor's history is kept for the lifetime
/// of the claim (legitimate interest), so the purge leaves it alone. Only when the owner has opted out
/// of retention (Art. 21) AND has no subscription coverage is a sensor suspended past the window
/// anonymized (telemetry moved to the ML store) and force-unclaimed.
/// </summary>
public class SuspendedSensorPurgeServiceTests : ControllerTestBase
{
    private static IAnonymizationService Anonymizer(ApplicationDbContext db) =>
        new AnonymizationService(db, NullLogger<AnonymizationService>.Instance);

    private static IDeviceOwnershipService Ownership() => new Mock<IDeviceOwnershipService>().Object;

    private static ISubscriptionCapacityService Capacity(ApplicationDbContext db) =>
        new SubscriptionCapacityService(db, new MemoryCache(new MemoryCacheOptions()));

    private static readonly TimeSpan SixMonths = TimeSpan.FromDays(180);

    private static Sensor MakeSensor(int id) => new()
    {
        Id = id, Name = $"garge_volt_{id}", Type = "voltage", Role = "sensor",
        RegistrationCode = $"rc{id}", DefaultName = "Battery", ParentName = "gw"
    };

    private static void AddUser(ApplicationDbContext db, string id, bool optedOut) =>
        db.Users.Add(new User
        {
            Id = id, UserName = id, Email = $"{id}@test.com", FirstName = "A", LastName = "B",
            DataRetentionOptOutAt = optedOut ? DateTime.UtcNow : null
        });

    private static void AddActivePrimary(ApplicationDbContext db, string userId)
    {
        db.AppSettings.Add(new AppSettings { Id = 1 });
        db.Products.Add(new Product { Id = 1, Name = "Primary", PriceInOre = 0, Interval = BillingInterval.Monthly, Type = ProductType.Primary });
        db.Subscriptions.Add(new Subscription { UserId = userId, ProductId = 1, VippsAgreementId = "a", Status = SubscriptionStatus.Active, Quantity = 1 });
    }

    [Fact]
    public async Task Purge_OptedOutNoCoverage_PastCap_AnonymizesAndUnclaims()
    {
        using var db = CreateDbContext();
        AddUser(db, "u", optedOut: true);
        db.Sensors.Add(MakeSensor(1));
        db.UserSensors.Add(new UserSensor { UserId = "u", SensorId = 1, IsOwner = true, SuspendedAt = DateTime.UtcNow.AddDays(-200) });
        db.SensorOwnershipPeriods.Add(new SensorOwnershipPeriod { UserId = "u", SensorId = 1, StartedAt = SensorOwnershipPeriod.FirstOwnerStart, EndedAt = null });
        db.SensorData.AddRange(
            new SensorData { SensorId = 1, Value = "12.5", Timestamp = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new SensorData { SensorId = 1, Value = "12.6", Timestamp = new DateTime(2021, 1, 2, 0, 0, 0, DateTimeKind.Utc) });
        await db.SaveChangesAsync();

        var purged = await SuspendedSensorPurgeService.PurgeExpiredAsync(db, Anonymizer(db), Ownership(), Capacity(db), SixMonths);

        Assert.Equal(1, purged);
        Assert.Empty(db.UserSensors);                 // force-unclaimed
        Assert.Empty(db.SensorOwnershipPeriods);       // period consumed by anonymization
        Assert.Empty(db.SensorData);                   // moved out of the personal store
        Assert.Equal(2, db.AnonymizedReadings.Count()); // ...into the ML store
    }

    [Fact]
    public async Task Purge_NotOptedOut_PastCap_LeftAlone()
    {
        // Default user keeps history for the lifetime of the claim — never purged, even past the cap.
        using var db = CreateDbContext();
        AddUser(db, "u", optedOut: false);
        db.Sensors.Add(MakeSensor(1));
        db.UserSensors.Add(new UserSensor { UserId = "u", SensorId = 1, IsOwner = true, SuspendedAt = DateTime.UtcNow.AddDays(-200) });
        await db.SaveChangesAsync();

        var purged = await SuspendedSensorPurgeService.PurgeExpiredAsync(db, Anonymizer(db), Ownership(), Capacity(db), SixMonths);

        Assert.Equal(0, purged);
        Assert.Single(db.UserSensors);
    }

    [Fact]
    public async Task Purge_OptedOutButStillHasSubscription_PastCap_LeftAlone()
    {
        // A paying user is never purged here, even if opted out — the opt-out only bites after lapse.
        using var db = CreateDbContext();
        AddUser(db, "u", optedOut: true);
        AddActivePrimary(db, "u");
        db.Sensors.Add(MakeSensor(1));
        db.UserSensors.Add(new UserSensor { UserId = "u", SensorId = 1, IsOwner = true, SuspendedAt = DateTime.UtcNow.AddDays(-200) });
        await db.SaveChangesAsync();

        var purged = await SuspendedSensorPurgeService.PurgeExpiredAsync(db, Anonymizer(db), Ownership(), Capacity(db), SixMonths);

        Assert.Equal(0, purged);
        Assert.Single(db.UserSensors);
    }

    [Fact]
    public async Task Purge_OptedOut_SuspendedWithinCap_LeftAlone()
    {
        using var db = CreateDbContext();
        AddUser(db, "u", optedOut: true);
        db.Sensors.Add(MakeSensor(1));
        db.UserSensors.Add(new UserSensor { UserId = "u", SensorId = 1, IsOwner = true, SuspendedAt = DateTime.UtcNow.AddDays(-10) });
        await db.SaveChangesAsync();

        var purged = await SuspendedSensorPurgeService.PurgeExpiredAsync(db, Anonymizer(db), Ownership(), Capacity(db), SixMonths);

        Assert.Equal(0, purged);
        Assert.Single(db.UserSensors);
    }

    [Fact]
    public async Task Purge_OptedOut_ActiveSensor_LeftAlone()
    {
        using var db = CreateDbContext();
        AddUser(db, "u", optedOut: true);
        db.Sensors.Add(MakeSensor(1));
        db.UserSensors.Add(new UserSensor { UserId = "u", SensorId = 1, IsOwner = true, SuspendedAt = null });
        await db.SaveChangesAsync();

        var purged = await SuspendedSensorPurgeService.PurgeExpiredAsync(db, Anonymizer(db), Ownership(), Capacity(db), SixMonths);

        Assert.Equal(0, purged);
        Assert.Single(db.UserSensors);
    }

    [Fact]
    public async Task Purge_OptedOutBypassRole_PastCap_LeftAlone()
    {
        // A ComplimentaryUser (or service account / admin) has coverage by role — never purged,
        // even opted out, with no subscription, past the cap.
        using var db = CreateDbContext();
        AddUser(db, "u", optedOut: true);
        GrantRole(db, "u", "ComplimentaryUser");
        db.Sensors.Add(MakeSensor(1));
        db.UserSensors.Add(new UserSensor { UserId = "u", SensorId = 1, IsOwner = true, SuspendedAt = DateTime.UtcNow.AddDays(-200) });
        await db.SaveChangesAsync();

        var purged = await SuspendedSensorPurgeService.PurgeExpiredAsync(db, Anonymizer(db), Ownership(), Capacity(db), SixMonths);

        Assert.Equal(0, purged);
        Assert.Single(db.UserSensors);
    }

    private static void GrantRole(ApplicationDbContext db, string userId, string roleName)
    {
        var roleId = $"role-{roleName}";
        db.Roles.Add(new IdentityRole { Id = roleId, Name = roleName, NormalizedName = roleName.ToUpperInvariant() });
        db.UserRoles.Add(new IdentityUserRole<string> { UserId = userId, RoleId = roleId });
    }
}
