using garge_api.Models;
using garge_api.Models.Mqtt;
using garge_api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// Verifies that a device command survives not being deliverable. A command on a device's
/// <c>/set</c> topic is QoS 0 and unretained, so one published while no gateway holds the lease
/// reaches nobody and is gone. The intent is held instead, reissued until the device is observed
/// to agree, and abandoned loudly rather than retried forever.
/// </summary>
public class DeviceCommandServiceTests : ControllerTestBase
{
    private const string Target = "wiz_SOCKET_6c2990a96cde";

    private static DeviceCommandService CreateService(ApplicationDbContext db) =>
        new(db, NullLogger<DeviceCommandService>.Instance);

    /// <summary>
    /// Intent is only tracked for a device some gateway has reported, because only those are
    /// reached through a lease. Seeding that report is what makes a target eligible.
    /// </summary>
    private static void SeedDiscovered(ApplicationDbContext db, params string[] targets)
    {
        foreach (var target in targets)
        {
            db.DiscoveredDevices.Add(new DiscoveredDevice
            {
                DiscoveredBy = "garge_aaaaaaaaaaaa", Target = target, Type = "switch", Timestamp = DateTime.UtcNow
            });
        }
        db.SaveChanges();
    }

    [Fact]
    public async Task SetDesiredState_RecordsAnUnsettledIntent()
    {
        using var db = CreateDbContext();
        SeedDiscovered(db, Target);

        var intent = await CreateService(db).SetDesiredStateAsync(Target, "ON");

        Assert.NotNull(intent);
        Assert.Equal("ON", intent!.DesiredState);
        Assert.Null(intent.ObservedState);
        Assert.False(intent.Settled);
        Assert.Equal(0, intent.Attempts);
        Assert.Single(db.DeviceDesiredStates);
    }

    [Fact]
    public async Task SetDesiredState_Again_ReplacesTheIntentAndResetsTheRetryBudget()
    {
        using var db = CreateDbContext();
        SeedDiscovered(db, Target);
        var service = CreateService(db);

        await service.SetDesiredStateAsync(Target, "ON");
        await service.RecordAttemptAsync(Target);
        await service.RecordAttemptAsync(Target);

        var intent = await service.SetDesiredStateAsync(Target, "OFF");

        Assert.NotNull(intent);
        Assert.Equal("OFF", intent!.DesiredState);
        Assert.Equal(0, intent.Attempts);
        Assert.False(intent.Settled);
        Assert.Single(db.DeviceDesiredStates); // one row per target
    }

    [Fact]
    public async Task RecordObservedState_MatchingTheIntent_SettlesIt()
    {
        using var db = CreateDbContext();
        SeedDiscovered(db, Target);
        var service = CreateService(db);
        await service.SetDesiredStateAsync(Target, "ON");

        var intent = await service.RecordObservedStateAsync(Target, "ON");

        Assert.NotNull(intent);
        Assert.True(intent!.Settled);
        Assert.Equal("ON", intent.ObservedState);
        Assert.NotNull(intent.ObservedStateAt);
        Assert.Empty(await service.PendingAsync());
    }

    [Fact]
    public async Task RecordObservedState_DisagreeingWithTheIntent_StaysPending()
    {
        using var db = CreateDbContext();
        SeedDiscovered(db, Target);
        var service = CreateService(db);
        await service.SetDesiredStateAsync(Target, "ON");

        var intent = await service.RecordObservedStateAsync(Target, "OFF");

        Assert.False(intent!.Settled);
        Assert.Equal(Target, Assert.Single(await service.PendingAsync()).Target);
    }

    [Fact]
    public async Task RecordObservedState_WithNoIntent_ReportsNothingToSettle()
    {
        using var db = CreateDbContext();

        // Nobody asked for this device, so its state is news rather than progress.
        Assert.Null(await CreateService(db).RecordObservedStateAsync(Target, "ON"));
        Assert.Empty(db.DeviceDesiredStates);
    }

    [Fact]
    public async Task Pending_StopsOfferingACommandOnceTheAttemptsRunOut()
    {
        using var db = CreateDbContext();
        SeedDiscovered(db, Target);
        var service = CreateService(db);
        await service.SetDesiredStateAsync(Target, "ON");

        for (var i = 0; i < DeviceCommandService.MaxAttempts; i++)
        {
            Assert.Single(await service.PendingAsync());
            await service.RecordAttemptAsync(Target);
        }

        // Abandoned rather than retried forever, so the caller sees a failure.
        Assert.Empty(await service.PendingAsync());
        var row = db.DeviceDesiredStates.Single();
        Assert.Equal(DeviceCommandService.MaxAttempts, row.Attempts);
        Assert.False(row.Settled);
    }

    [Fact]
    public async Task Pending_ReturnsOldestFirst()
    {
        using var db = CreateDbContext();
        SeedDiscovered(db, "wiz_SOCKET_aaaaaaaaaaaa", "wiz_SOCKET_bbbbbbbbbbbb");
        var service = CreateService(db);

        await service.SetDesiredStateAsync("wiz_SOCKET_aaaaaaaaaaaa", "ON");
        await service.SetDesiredStateAsync("wiz_SOCKET_bbbbbbbbbbbb", "OFF");

        var pending = await service.PendingAsync();

        Assert.Equal(2, pending.Count);
        Assert.True(pending[0].DesiredStateAt <= pending[1].DesiredStateAt);
    }

    [Fact]
    public async Task SetDesiredState_ForASwitchNoGatewayReported_TracksNothing()
    {
        using var db = CreateDbContext();

        // A switch published to directly has no lease to wait on, so an intent for it would sit
        // pending for good and be retried every pass.
        Assert.Null(await CreateService(db).SetDesiredStateAsync("garge_socket_1", "ON"));
        Assert.Empty(db.DeviceDesiredStates);
    }

    [Fact]
    public async Task RecordAttempt_ForAnUnknownTarget_DoesNothing()
    {
        using var db = CreateDbContext();

        Assert.False(await CreateService(db).RecordAttemptAsync(Target));
    }
}
