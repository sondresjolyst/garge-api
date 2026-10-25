using garge_api.Hubs;
using garge_api.Models;
using garge_api.Models.Push;
using garge_api.Models.Sensor;
using garge_api.Models.Shop;
using garge_api.Models.Subscription;
using garge_api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SjolystInnovation.AppKit.Auth;
using System.Net;
using Xunit;

namespace garge_api.Tests;

public class AccountCleanupTests : ControllerTestBase
{
    private static User MakeDbUser(string id = "user-1") => new()
    {
        Id = id, UserName = "userone", Email = "user@test.com",
        FirstName = "First", LastName = "Last", TermsAcceptedIp = "203.0.113.7"
    };

    private static AccountDataEraser Eraser(ApplicationDbContext db) =>
        new(db, new AnonymizationService(db, NullLogger<AnonymizationService>.Instance), new Mock<IDeviceOwnershipService>().Object);

    [Fact]
    public async Task Erasing_clears_the_terms_ip_and_keeps_orders()
    {
        using var db = CreateDbContext();
        var user = MakeDbUser();
        db.Orders.Add(new Order { Id = 1, UserId = user.Id, VippsOrderId = "garge-order-000001", TotalInOre = 9900, Status = OrderStatus.Paid });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Eraser(db).EraseAsync(user, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Null(user.TermsAcceptedIp);
        Assert.Equal(OrderStatus.Paid, Assert.Single(db.Orders).Status);
    }

    [Fact]
    public async Task Erasing_removes_the_user_owned_rows()
    {
        using var db = CreateDbContext();
        var user = MakeDbUser();
        db.PushSubscriptions.Add(new PushSubscription { Id = 1, UserId = user.Id, Endpoint = "https://push", P256dh = "k", Auth = "a" });
        db.PushSubscriptions.Add(new PushSubscription { Id = 2, UserId = "someone-else", Endpoint = "https://push2", P256dh = "k", Auth = "a" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Eraser(db).EraseAsync(user, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("someone-else", Assert.Single(db.PushSubscriptions).UserId);
    }

    [Fact]
    public async Task Erasing_moves_telemetry_to_the_ml_store_and_leaves_no_orphans()
    {
        using var db = CreateDbContext();
        var user = MakeDbUser();
        db.Sensors.Add(new Sensor { Id = 1, Name = "garge_volt", Type = "voltage", Role = "sensor", RegistrationCode = "rc", DefaultName = "Battery", ParentName = "gw" });
        db.UserSensors.Add(new UserSensor { UserId = user.Id, SensorId = 1, IsOwner = true });
        db.SensorOwnershipPeriods.Add(new SensorOwnershipPeriod { UserId = user.Id, SensorId = 1, StartedAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), EndedAt = null });
        db.SensorData.AddRange(
            new SensorData { SensorId = 1, Value = "12.5", Timestamp = new DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc) },
            new SensorData { SensorId = 1, Value = "12.6", Timestamp = new DateTime(2020, 3, 1, 0, 0, 0, DateTimeKind.Utc) });
        db.BatteryHealthData.Add(new BatteryHealth { SensorId = 1, Status = "ok", Timestamp = new DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc) });
        db.SensorPhotos.Add(new SensorPhoto { UserId = user.Id, SensorId = 1, ContentType = "image/jpeg", Data = "AQID" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Eraser(db).EraseAsync(user, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, db.AnonymizedReadings.Count());
        Assert.Equal("voltage", db.AnonymizedSeries.Single().SourceType);
        Assert.Empty(db.SensorData);
        Assert.Empty(db.BatteryHealthData);
        Assert.Empty(db.SensorPhotos);
        Assert.Empty(db.SensorOwnershipPeriods);
        Assert.Empty(db.UserSensors);
    }

    [Fact]
    public async Task The_handler_tells_open_connections_to_sign_out()
    {
        var tracker = new Mock<IHubConnectionTracker>();
        tracker.Setup(t => t.GetConnectionIds("user-1")).Returns(new[] { "c1", "c2" });
        var proxy = new Mock<IClientProxy>();
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Clients(It.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "c1", "c2" })))).Returns(proxy.Object);
        var hub = new Mock<IHubContext<DeviceHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);

        await new AccountHubDisconnect(tracker.Object, hub.Object, NullLogger<AccountHubDisconnect>.Instance)
            .OnDeletedAsync(MakeDbUser(), TestContext.Current.CancellationToken);

        proxy.Verify(p => p.SendCoreAsync("forceLogout", It.Is<object?[]>(a => a.Length == 0), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task The_handler_does_nothing_without_connections()
    {
        var tracker = new Mock<IHubConnectionTracker>();
        tracker.Setup(t => t.GetConnectionIds("user-1")).Returns(Array.Empty<string>());
        var hub = new Mock<IHubContext<DeviceHub>>(MockBehavior.Strict);

        await new AccountHubDisconnect(tracker.Object, hub.Object, NullLogger<AccountHubDisconnect>.Instance)
            .OnDeletedAsync(MakeDbUser(), TestContext.Current.CancellationToken);

        hub.VerifyNoOtherCalls();
    }

    private static readonly VippsSettings Configured = new()
    {
        BaseUrl = "https://api.vipps.no",
        ClientId = "id", ClientSecret = "s", SubscriptionKey = "k", MerchantSerialNumber = "msn-prod",
        TestClientId = "tid", TestClientSecret = "ts", TestSubscriptionKey = "tk", TestMerchantSerialNumber = "msn-test"
    };

    private static Subscription Sub(string agreementId, SubscriptionStatus status, int productId = 1, string userId = "user-1", bool isTest = false) => new()
    {
        UserId = userId, ProductId = productId, UnitPriceInOre = 5000, VippsAgreementId = agreementId, Status = status, IsTest = isTest,
        NextChargeDate = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Utc)
    };

    private static void AddProducts(ApplicationDbContext db)
    {
        db.Products.Add(new Product { Id = 1, Name = "Garge Basic", PriceInOre = 5000, Interval = BillingInterval.Monthly, Type = ProductType.Primary, IsActive = true });
        db.Products.Add(new Product { Id = 2, Name = "Extra sensor", PriceInOre = 2000, Interval = BillingInterval.Monthly, Type = ProductType.AddOn, IsActive = true });
    }

    [Fact]
    public async Task Erasing_stops_every_open_subscription_and_add_on_and_keeps_the_invoices()
    {
        using var db = CreateDbContext();
        var user = MakeDbUser();
        AddProducts(db);
        var primary = Sub("agr_primary", SubscriptionStatus.Active);
        var addOn = Sub("agr_addon", SubscriptionStatus.Active, productId: 2);
        var pending = Sub("agr_pending", SubscriptionStatus.Pending);
        var ended = Sub("agr_ended", SubscriptionStatus.Expired);
        var others = Sub("agr_other", SubscriptionStatus.Active, userId: "someone-else");
        db.Subscriptions.AddRange(primary, addOn, pending, ended, others);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.Invoices.Add(new Invoice { SubscriptionId = primary.Id, VippsChargeId = "chr-1", AmountInOre = 5000, IssuedAt = DateTime.UtcNow, PdfData = [1] });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var before = DateTime.UtcNow;

        await Eraser(db).EraseAsync(user, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        foreach (var sub in new[] { primary, addOn, pending })
        {
            Assert.Equal(SubscriptionStatus.Stopped, sub.Status);
            Assert.NotNull(sub.AgreementStopRequestedAt);
            Assert.True(sub.UpdatedAt >= before);
            // A cancel keeps the date the paid period ends.
            Assert.Equal(new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc), sub.NextChargeDate);
        }
        Assert.Equal(SubscriptionStatus.Expired, ended.Status);
        Assert.Null(ended.AgreementStopRequestedAt);
        Assert.Equal(SubscriptionStatus.Active, others.Status);
        Assert.Null(others.AgreementStopRequestedAt);
        Assert.Equal(5, db.Subscriptions.Count());
        Assert.Equal("chr-1", Assert.Single(db.Invoices).VippsChargeId);
    }

    private static AccountSubscriptionStop StopHandler(ApplicationDbContext db, Mock<IVippsService> vipps, VippsSettings? settings = null) =>
        new(db, vipps.Object, Options.Create(settings ?? Configured), NullLogger<AccountSubscriptionStop>.Instance);

    [Fact]
    public async Task The_stop_handler_stops_the_agreements_in_their_own_environment_and_clears_the_mark()
    {
        using var db = CreateDbContext();
        var live = Sub("agr_live", SubscriptionStatus.Stopped);
        var test = Sub("agr_test", SubscriptionStatus.Stopped, isTest: true);
        var others = Sub("agr_other", SubscriptionStatus.Stopped, userId: "someone-else");
        foreach (var s in new[] { live, test, others }) s.AgreementStopRequestedAt = DateTime.UtcNow;
        db.Subscriptions.AddRange(live, test, others);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var vipps = new Mock<IVippsService>();

        await StopHandler(db, vipps).OnDeletedAsync(MakeDbUser(), TestContext.Current.CancellationToken);

        vipps.Verify(v => v.CancelAgreementAsync("agr_live", It.Is<string>(k => k.StartsWith($"stop-{live.Id}-")), false), Times.Once);
        vipps.Verify(v => v.CancelAgreementAsync("agr_test", It.Is<string>(k => k.StartsWith($"stop-{test.Id}-")), true), Times.Once);
        vipps.Verify(v => v.CancelAgreementAsync("agr_other", It.IsAny<string>(), It.IsAny<bool?>()), Times.Never);
        Assert.Null(live.AgreementStopRequestedAt);
        Assert.Null(test.AgreementStopRequestedAt);
        Assert.NotNull(others.AgreementStopRequestedAt);
    }

    [Fact]
    public async Task A_stop_that_fails_keeps_the_mark_for_the_scheduler_and_does_not_throw()
    {
        using var db = CreateDbContext();
        var first = Sub("agr_down", SubscriptionStatus.Stopped);
        var second = Sub("agr_up", SubscriptionStatus.Stopped);
        first.AgreementStopRequestedAt = second.AgreementStopRequestedAt = DateTime.UtcNow;
        db.Subscriptions.AddRange(first, second);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var vipps = new Mock<IVippsService>();
        vipps.Setup(v => v.CancelAgreementAsync("agr_down", It.IsAny<string>(), It.IsAny<bool?>()))
            .ThrowsAsync(new HttpRequestException("down", null, HttpStatusCode.ServiceUnavailable));

        await StopHandler(db, vipps).OnDeletedAsync(MakeDbUser(), TestContext.Current.CancellationToken);

        Assert.NotNull(first.AgreementStopRequestedAt);
        Assert.Null(second.AgreementStopRequestedAt);
    }

    [Fact]
    public async Task A_handler_failure_after_the_commit_does_not_reach_the_caller()
    {
        using var db = CreateDbContext();
        var sub = Sub("agr_live", SubscriptionStatus.Stopped);
        sub.AgreementStopRequestedAt = DateTime.UtcNow;
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var vipps = new Mock<IVippsService>();
        vipps.Setup(v => v.CancelAgreementAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()))
            .Callback(() => db.Dispose())
            .Returns(Task.CompletedTask);

        await StopHandler(db, vipps).OnDeletedAsync(MakeDbUser(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_stop_of_an_agreement_vipps_does_not_know_clears_the_mark()
    {
        using var db = CreateDbContext();
        var sub = Sub("agr_unknown", SubscriptionStatus.Stopped);
        sub.AgreementStopRequestedAt = DateTime.UtcNow;
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var vipps = new Mock<IVippsService>();
        vipps.Setup(v => v.CancelAgreementAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()))
            .ThrowsAsync(new HttpRequestException("gone", null, HttpStatusCode.NotFound));
        vipps.Setup(v => v.GetAgreementAsync(It.IsAny<string>(), It.IsAny<bool?>()))
            .ThrowsAsync(new HttpRequestException("gone", null, HttpStatusCode.NotFound));

        await StopHandler(db, vipps).OnDeletedAsync(MakeDbUser(), TestContext.Current.CancellationToken);

        Assert.Null(sub.AgreementStopRequestedAt);
    }

    [Theory]
    [InlineData("STOPPED", false)]
    [InlineData("EXPIRED", false)]
    [InlineData("PENDING", true)]
    [InlineData("ACTIVE", true)]
    public async Task A_stop_vipps_refuses_clears_the_mark_only_for_an_agreement_that_ended(string status, bool kept)
    {
        using var db = CreateDbContext();
        var sub = Sub("agr_refused", SubscriptionStatus.Stopped, isTest: true);
        sub.AgreementStopRequestedAt = DateTime.UtcNow;
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var vipps = new Mock<IVippsService>();
        vipps.Setup(v => v.CancelAgreementAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()))
            .ThrowsAsync(new HttpRequestException("refused", null, HttpStatusCode.BadRequest));
        vipps.Setup(v => v.GetAgreementAsync("agr_refused", true)).ReturnsAsync(new VippsAgreement { Id = "agr_refused", Status = status });

        await StopHandler(db, vipps).OnDeletedAsync(MakeDbUser(), TestContext.Current.CancellationToken);

        Assert.Equal(kept, sub.AgreementStopRequestedAt != null);
    }

    [Fact]
    public async Task A_refused_stop_whose_agreement_cannot_be_read_keeps_the_mark()
    {
        using var db = CreateDbContext();
        var sub = Sub("agr_refused", SubscriptionStatus.Stopped);
        sub.AgreementStopRequestedAt = DateTime.UtcNow;
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var vipps = new Mock<IVippsService>();
        vipps.Setup(v => v.CancelAgreementAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()))
            .ThrowsAsync(new HttpRequestException("refused", null, HttpStatusCode.BadRequest));
        vipps.Setup(v => v.GetAgreementAsync(It.IsAny<string>(), It.IsAny<bool?>()))
            .ThrowsAsync(new HttpRequestException("refused", null, HttpStatusCode.BadRequest));

        await StopHandler(db, vipps).OnDeletedAsync(MakeDbUser(), TestContext.Current.CancellationToken);

        Assert.NotNull(sub.AgreementStopRequestedAt);
    }

    [Fact]
    public async Task A_subscription_without_an_agreement_has_nothing_to_stop()
    {
        using var db = CreateDbContext();
        var sub = Sub("", SubscriptionStatus.Stopped);
        sub.AgreementStopRequestedAt = DateTime.UtcNow;
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var vipps = new Mock<IVippsService>();

        await StopHandler(db, vipps).OnDeletedAsync(MakeDbUser(), TestContext.Current.CancellationToken);

        vipps.Verify(v => v.CancelAgreementAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()), Times.Never);
        Assert.Null(sub.AgreementStopRequestedAt);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_stop_that_may_succeed_later_keeps_the_mark(HttpStatusCode status)
    {
        using var db = CreateDbContext();
        var sub = Sub("agr_live", SubscriptionStatus.Stopped);
        sub.AgreementStopRequestedAt = DateTime.UtcNow;
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var vipps = new Mock<IVippsService>();
        vipps.Setup(v => v.CancelAgreementAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()))
            .ThrowsAsync(new HttpRequestException("later", null, status));

        await StopHandler(db, vipps).OnDeletedAsync(MakeDbUser(), TestContext.Current.CancellationToken);

        Assert.NotNull(sub.AgreementStopRequestedAt);
    }

    [Fact]
    public async Task A_stop_without_credentials_for_its_environment_waits()
    {
        using var db = CreateDbContext();
        var sub = Sub("agr_test", SubscriptionStatus.Stopped, isTest: true);
        sub.AgreementStopRequestedAt = DateTime.UtcNow;
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var vipps = new Mock<IVippsService>();

        await StopHandler(db, vipps, new VippsSettings { BaseUrl = "https://api.vipps.no", ClientId = "id", ClientSecret = "s", SubscriptionKey = "k", MerchantSerialNumber = "m" })
            .OnDeletedAsync(MakeDbUser(), TestContext.Current.CancellationToken);

        vipps.Verify(v => v.CancelAgreementAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>()), Times.Never);
        Assert.NotNull(sub.AgreementStopRequestedAt);
    }

    [Fact]
    public void The_stop_key_fits_the_vipps_idempotency_key_rules()
    {
        // Idempotency-Key: 1 to 40 characters, without '#', '?', '/' or '\'.
        var key = SubscriptionAgreementStops.StopKey(new Subscription { Id = int.MaxValue, UserId = Guid.NewGuid().ToString(), VippsAgreementId = "agr_" + new string('x', 40) });
        Assert.InRange(key.Length, 1, 40);
        Assert.Matches("^[a-z0-9-]+$", key);
    }

    [Fact]
    public async Task Deleting_the_account_stops_the_agreement_after_the_deletion_is_saved()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));
        services.AddIdentityCore<User>().AddRoles<IdentityRole>().AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddAppKitAccounts<User, ApplicationDbContext>();
        services.AddScoped<IAnonymizationService, AnonymizationService>();
        services.AddSingleton(new Mock<IDeviceOwnershipService>().Object);
        var vipps = new Mock<IVippsService>();
        services.AddSingleton(vipps.Object);
        services.AddSingleton(Options.Create(Configured));
        services.AddScoped<IAccountDataEraser<User>, AccountDataEraser>();
        services.AddScoped<IAccountDeletedHandler<User>, AccountSubscriptionStop>();
        using var provider = services.BuildServiceProvider();

        using (var seed = provider.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Users.Add(MakeDbUser());
            AddProducts(db);
            db.Subscriptions.Add(Sub("agr_primary", SubscriptionStatus.Active));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (bool UserDeleted, SubscriptionStatus Status)? seenAtStop = null;
        vipps.Setup(v => v.CancelAgreementAsync("agr_primary", It.IsAny<string>(), false))
            .Callback(() =>
            {
                using var other = provider.CreateScope();
                var db = other.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                seenAtStop = (db.Users.Single().IsDeleted, db.Subscriptions.Single().Status);
            })
            .Returns(Task.CompletedTask);

        using (var scope = provider.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = await users.FindByIdAsync("user-1");
            await scope.ServiceProvider.GetRequiredService<IAccountDeleter<User>>().DeleteAsync(user!, TestContext.Current.CancellationToken);
        }

        Assert.Equal((true, SubscriptionStatus.Stopped), seenAtStop);
        using var check = provider.CreateScope();
        Assert.Null(check.ServiceProvider.GetRequiredService<ApplicationDbContext>().Subscriptions.Single().AgreementStopRequestedAt);
    }
}
