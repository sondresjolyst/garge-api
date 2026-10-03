using garge_api.Models;
using garge_api.Models.Mqtt;
using garge_api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// Verifies which gateway may act on a shared target. Several gateways can discover the same Wiz
/// device, and the lease is what stops all of them answering its command topic: one holds it while
/// it keeps reporting the target, the rest stand by, and a lapse hands it to a standby so a dead
/// gateway does not leave the device unreachable.
/// </summary>
public class DeviceLeaseServiceTests : ControllerTestBase
{
    private const string GatewayA = "garge_aaaaaaaaaaaa";
    private const string GatewayB = "garge_bbbbbbbbbbbb";
    private const string Target = "wiz_SOCKET_6c2990a96cde";

    private static readonly DateTimeOffset Start = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A clock the test moves by hand, so lease expiry needs no real waiting.</summary>
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private static (DeviceLeaseService Service, TestClock Clock) CreateService(ApplicationDbContext db)
    {
        var clock = new TestClock(Start);
        return (new DeviceLeaseService(db, NullLogger<DeviceLeaseService>.Instance, clock), clock);
    }

    private static DiscoveredDevice Discovery(string by, string target = Target) => new()
    {
        DiscoveredBy = by, Target = target, Type = "switch", Timestamp = Start.UtcDateTime
    };

    [Fact]
    public async Task ReportSeen_FirstGateway_TakesTheLease()
    {
        using var db = CreateDbContext();
        var (service, _) = CreateService(db);

        var controller = await service.ReportSeenAsync(GatewayA, Target);
        await db.SaveChangesAsync();

        Assert.Equal(GatewayA, controller);
        var lease = db.DeviceControllers.Single();
        Assert.Equal(Target, lease.Target);
        Assert.Equal(GatewayA, lease.ControllerDeviceName);
        Assert.Equal(Start.UtcDateTime + DeviceLeaseService.LeaseDuration, lease.LeaseExpiresAt);
    }

    [Fact]
    public async Task ReportSeen_SecondGatewayWhileLeaseIsLive_LeavesTheHolderAlone()
    {
        using var db = CreateDbContext();
        var (service, clock) = CreateService(db);

        await service.ReportSeenAsync(GatewayA, Target);
        await db.SaveChangesAsync();

        clock.Advance(TimeSpan.FromSeconds(30));
        var controller = await service.ReportSeenAsync(GatewayB, Target);
        await db.SaveChangesAsync();

        Assert.Equal(GatewayA, controller);
        Assert.Equal(GatewayA, db.DeviceControllers.Single().ControllerDeviceName);
    }

    [Fact]
    public async Task ReportSeen_HolderReportsAgain_RenewsTheLease()
    {
        using var db = CreateDbContext();
        var (service, clock) = CreateService(db);

        await service.ReportSeenAsync(GatewayA, Target);
        await db.SaveChangesAsync();

        clock.Advance(TimeSpan.FromSeconds(60));
        await service.ReportSeenAsync(GatewayA, Target);
        await db.SaveChangesAsync();

        var lease = db.DeviceControllers.Single();
        Assert.Equal(clock.GetUtcNow().UtcDateTime + DeviceLeaseService.LeaseDuration, lease.LeaseExpiresAt);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, lease.LastSeenFromTarget);
    }

    [Fact]
    public async Task ReportSeen_AfterTheLeaseLapsed_TakesOver()
    {
        using var db = CreateDbContext();
        var (service, clock) = CreateService(db);

        await service.ReportSeenAsync(GatewayA, Target);
        await db.SaveChangesAsync();

        clock.Advance(DeviceLeaseService.LeaseDuration + TimeSpan.FromSeconds(1));
        var controller = await service.ReportSeenAsync(GatewayB, Target);
        await db.SaveChangesAsync();

        Assert.Equal(GatewayB, controller);
        Assert.Equal(GatewayB, db.DeviceControllers.Single().ControllerDeviceName);
    }

    [Fact]
    public async Task ControlledTargets_ReturnsOnlyLiveLeasesOfThatGateway()
    {
        using var db = CreateDbContext();
        var (service, clock) = CreateService(db);

        await service.ReportSeenAsync(GatewayA, Target);
        await service.ReportSeenAsync(GatewayB, "wiz_SHRGBC_d8a01127d90e");
        await db.SaveChangesAsync();

        Assert.Equal([Target], await service.ControlledTargetsAsync(GatewayA));

        // Once the lease lapses the gateway controls nothing, even though the row still names it.
        clock.Advance(DeviceLeaseService.LeaseDuration + TimeSpan.FromSeconds(1));
        Assert.Empty(await service.ControlledTargetsAsync(GatewayA));
    }

    [Fact]
    public async Task PromoteExpiredLeases_HandsALapsedLeaseToAStandby()
    {
        using var db = CreateDbContext();
        var (service, clock) = CreateService(db);
        db.DiscoveredDevices.AddRange(Discovery(GatewayA), Discovery(GatewayB));
        await db.SaveChangesAsync();

        await service.ReportSeenAsync(GatewayA, Target);
        await db.SaveChangesAsync();

        clock.Advance(DeviceLeaseService.LeaseDuration + TimeSpan.FromSeconds(1));
        var handovers = await service.PromoteExpiredLeasesAsync();
        await db.SaveChangesAsync();

        var handover = Assert.Single(handovers);
        Assert.Equal(Target, handover.Target);
        Assert.Equal(GatewayA, handover.PreviousController);
        Assert.Equal(GatewayB, handover.NewController);
        Assert.Equal(GatewayB, db.DeviceControllers.Single().ControllerDeviceName);
    }

    [Fact]
    public async Task PromoteExpiredLeases_NoOtherCandidate_KeepsTheLapsedHolder()
    {
        using var db = CreateDbContext();
        var (service, clock) = CreateService(db);
        db.DiscoveredDevices.Add(Discovery(GatewayA));
        await db.SaveChangesAsync();

        await service.ReportSeenAsync(GatewayA, Target);
        await db.SaveChangesAsync();

        clock.Advance(DeviceLeaseService.LeaseDuration + TimeSpan.FromSeconds(1));
        var handovers = await service.PromoteExpiredLeasesAsync();
        await db.SaveChangesAsync();

        // Moving the rows to nobody would only take access away from the one gateway that might
        // still come back.
        Assert.Empty(handovers);
        Assert.Equal(GatewayA, db.DeviceControllers.Single().ControllerDeviceName);
    }

    [Fact]
    public async Task PromoteExpiredLeases_LiveLease_DoesNothing()
    {
        using var db = CreateDbContext();
        var (service, clock) = CreateService(db);
        db.DiscoveredDevices.AddRange(Discovery(GatewayA), Discovery(GatewayB));
        await db.SaveChangesAsync();

        await service.ReportSeenAsync(GatewayA, Target);
        await db.SaveChangesAsync();

        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Empty(await service.PromoteExpiredLeasesAsync());
        Assert.Equal(GatewayA, db.DeviceControllers.Single().ControllerDeviceName);
    }

    [Fact]
    public async Task PromoteExpiredLeases_ChoosesTheSameStandbyEveryTime()
    {
        using var db = CreateDbContext();
        var (service, clock) = CreateService(db);
        var gatewayC = "garge_cccccccccccc";
        db.DiscoveredDevices.AddRange(Discovery(GatewayB), Discovery(gatewayC), Discovery(GatewayA));
        await db.SaveChangesAsync();

        await service.ReportSeenAsync(GatewayA, Target);
        await db.SaveChangesAsync();

        clock.Advance(DeviceLeaseService.LeaseDuration + TimeSpan.FromSeconds(1));
        var handovers = await service.PromoteExpiredLeasesAsync();

        // Deterministic, so two gateways cannot trade the lease back and forth. There is no
        // usable proximity signal to order them by.
        Assert.Equal(GatewayB, Assert.Single(handovers).NewController);
    }

    [Fact]
    public void ClientIdOf_StripsTheBrokerUsernamePrefix()
    {
        // A gateway connects with its chip id as the client id, while its broker username carries
        // the prefix, and the kick is addressed by client id.
        Assert.Equal("48ca43597fd8", DeviceLeaseMaintenanceService.ClientIdOf("garge_48ca43597fd8"));
        Assert.Equal("48ca43597fd8", DeviceLeaseMaintenanceService.ClientIdOf("48ca43597fd8"));
    }
}
