using garge_api.Constants;
using garge_api.Models;
using garge_api.Models.Pipeline;
using garge_api.Models.Push;
using garge_api.Models.Sensor;
using garge_api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static garge_api.Tests.SecurityTestData;

namespace garge_api.Tests;

public class SecurityAlertServiceTests : ControllerTestBase
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(2);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Harness(ApplicationDbContext Db, SecurityAlertService Sut, SecurityModeService Security, Mock<ISecurityNotifier> Notifier);

    private static Harness Build(ApplicationDbContext db, int thresholdMinutes = SecurityMode.DefaultThresholdMinutes)
    {
        var (security, _, _) = BuildService(db);
        var notifier = new Mock<ISecurityNotifier>();
        notifier.Setup(n => n.NotifyUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var sut = new SecurityAlertService(db, new PermissionService(db),
            new PipelineHealthService(db, NullLogger<PipelineHealthService>.Instance),
            security, notifier.Object, SettingsCache(thresholdMinutes), NullLogger<SecurityAlertService>.Instance);
        return new Harness(db, sut, security, notifier);
    }

    private async Task<Harness> ArmedAsync(DateTime armedAt, DateTime? lastReading, int thresholdMinutes = SecurityMode.DefaultThresholdMinutes)
    {
        var db = CreateDbContext();
        SeedReady(db);
        GrantRoles(db, Owner, RoleNames.GargeSecurity);
        var h = Build(db, thresholdMinutes);
        await h.Security.SetAsync(Owner, SensorId, true, Ct);
        var state = await db.SensorSecurityStates.SingleAsync(Ct);
        state.AppliedSleepSeconds = 600;
        state.SecurityModeReported = true;
        state.ArmedAt = armedAt;
        if (lastReading.HasValue)
            db.SensorData.Add(new SensorData { SensorId = SensorId, Value = "12.7", Timestamp = lastReading.Value });
        await db.SaveChangesAsync(Ct);
        return h;
    }

    private static void VerifyAlert(Harness h, string title, Times times) =>
        h.Notifier.Verify(n => n.NotifyUserAsync(Owner, title, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task AlertAndAllClear_ShareATagDistinctFromOfflineAlerts()
    {
        var now = DateTime.UtcNow;
        var h = await ArmedAsync(now.AddHours(-2), now.AddMinutes(-30));

        await h.Sut.RunAsync(now, Tick, false, Ct);
        h.Db.SensorData.Add(new SensorData { SensorId = SensorId, Value = "12.7", Timestamp = now.AddMinutes(1) });
        await h.Db.SaveChangesAsync(Ct);
        await h.Sut.RunAsync(now.AddMinutes(2), Tick, false, Ct);

        var tag = $"garge-security-{SensorId}";
        h.Notifier.Verify(n => n.NotifyUserAsync(Owner, "Garge Security alert", It.IsAny<string>(), tag, It.IsAny<CancellationToken>()), Times.Once);
        h.Notifier.Verify(n => n.NotifyUserAsync(Owner, "Garge Security: Sensor back online", It.IsAny<string>(), tag, It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotEqual($"garge-offline-{SensorId}", tag);
    }

    [Fact]
    public async Task QuietForThirtyMinutes_AlertsOnceAndLatches()
    {
        var now = DateTime.UtcNow;
        var h = await ArmedAsync(now.AddHours(-2), now.AddMinutes(-30));

        await h.Sut.RunAsync(now, Tick, false, Ct);
        await h.Sut.RunAsync(now.AddMinutes(2), Tick, false, Ct);

        VerifyAlert(h, "Garge Security alert", Times.Once());
        var latch = await h.Db.SensorOfflineNotifications.SingleAsync(Ct);
        Assert.Equal(NotificationKinds.Security, latch.Kind);
        Assert.Null(latch.ResolvedAt);
    }

    [Fact]
    public async Task StillQuietManyTicksLater_DoesNotAlertAgain()
    {
        var now = DateTime.UtcNow;
        var h = await ArmedAsync(now.AddHours(-2), now.AddMinutes(-30));

        for (var i = 0; i < 12; i++)
            await h.Sut.RunAsync(now.AddMinutes(2 * i), Tick, i % 5 == 0, Ct);

        VerifyAlert(h, "Garge Security alert", Times.Once());
    }

    [Fact]
    public async Task QuietForFifteenMinutes_DoesNothing()
    {
        var now = DateTime.UtcNow;
        var h = await ArmedAsync(now.AddHours(-2), now.AddMinutes(-15));

        await h.Sut.RunAsync(now, Tick, false, Ct);

        h.Notifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ArmedTenMinutesAgoWithoutAReadingYet_DoesNothing()
    {
        var now = DateTime.UtcNow;
        var h = await ArmedAsync(now.AddMinutes(-10), now.AddHours(-3));

        await h.Sut.RunAsync(now, Tick, false, Ct);

        h.Notifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ArmedAndNeverReportedAgain_AlertsAfterThreshold()
    {
        var now = DateTime.UtcNow;
        var h = await ArmedAsync(now.AddMinutes(-30), now.AddHours(-3));

        await h.Sut.RunAsync(now, Tick, false, Ct);

        VerifyAlert(h, "Garge Security alert", Times.Once());
    }

    [Fact]
    public async Task AdminRaisedThreshold_DelaysTheAlert()
    {
        var now = DateTime.UtcNow;
        var h = await ArmedAsync(now.AddHours(-2), now.AddMinutes(-30), thresholdMinutes: 45);

        await h.Sut.RunAsync(now, Tick, false, Ct);

        h.Notifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PendingSensor_IsNotEvaluated()
    {
        var now = DateTime.UtcNow;
        var h = await ArmedAsync(now.AddHours(-2), now.AddHours(-2));
        (await h.Db.SensorSecurityStates.SingleAsync(Ct)).ArmedAt = null;
        await h.Db.SaveChangesAsync(Ct);

        await h.Sut.RunAsync(now, Tick, false, Ct);

        h.Notifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SuspendedSensor_IsNotEvaluated()
    {
        var now = DateTime.UtcNow;
        var h = await ArmedAsync(now.AddHours(-2), now.AddMinutes(-30));
        (await h.Db.UserSensors.SingleAsync(Ct)).SuspendedAt = now.AddDays(-1);
        await h.Db.SaveChangesAsync(Ct);

        await h.Sut.RunAsync(now, Tick, false, Ct);

        h.Notifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task OwnerWithoutEntitlement_IsNotAlerted()
    {
        var now = DateTime.UtcNow;
        var h = await ArmedAsync(now.AddHours(-2), now.AddMinutes(-30));
        h.Db.UserRoles.RemoveRange(h.Db.UserRoles);
        await h.Db.SaveChangesAsync(Ct);

        await h.Sut.RunAsync(now, Tick, false, Ct);

        VerifyAlert(h, "Garge Security alert", Times.Never());
    }

    [Fact]
    public async Task NewReadingAfterAlert_ResolvesAndSendsAllClear()
    {
        var now = DateTime.UtcNow;
        var h = await ArmedAsync(now.AddHours(-2), now.AddMinutes(-1));
        h.Db.SensorOfflineNotifications.Add(new SensorOfflineNotification
        {
            UserId = Owner, SensorId = SensorId, Kind = NotificationKinds.Security, NotifiedAt = now.AddMinutes(-10)
        });
        await h.Db.SaveChangesAsync(Ct);

        await h.Sut.RunAsync(now, Tick, false, Ct);

        Assert.NotNull((await h.Db.SensorOfflineNotifications.SingleAsync(Ct)).ResolvedAt);
        VerifyAlert(h, "Garge Security: Sensor back online", Times.Once());
    }

    [Fact]
    public async Task OutageDuringOpenAlert_DoesNotSendFalseAllClear()
    {
        var now = DateTime.UtcNow;
        var h = await ArmedAsync(now.AddHours(-2), now.AddMinutes(-35));
        h.Db.SensorOfflineNotifications.Add(new SensorOfflineNotification
        {
            UserId = Owner, SensorId = SensorId, Kind = NotificationKinds.Security, NotifiedAt = now.AddMinutes(-8)
        });
        h.Db.PipelineGaps.Add(new PipelineGap { StartedAt = now.AddMinutes(-6), Source = SecurityMode.GapSources.Operator });
        await h.Db.SaveChangesAsync(Ct);

        await h.Sut.RunAsync(now, Tick, false, Ct);

        Assert.Null((await h.Db.SensorOfflineNotifications.SingleAsync(Ct)).ResolvedAt);
        VerifyAlert(h, "Garge Security: Sensor back online", Times.Never());
    }

    [Fact]
    public async Task TwoSensorsOfOneUserQuietInSamePass_SendOneAlert()
    {
        var now = DateTime.UtcNow;
        var h = await ArmedAsync(now.AddHours(-2), now.AddMinutes(-30));
        AddSensor(h.Db, id: 2, device: "garge_second");
        AddChargingRule(h.Db, sensorId: 2);
        h.Db.UserSensors.Add(new UserSensor { UserId = Owner, SensorId = 2, IsOwner = true });
        await h.Db.SaveChangesAsync(Ct);
        await h.Security.SetAsync(Owner, 2, true, Ct);
        var second = await h.Db.SensorSecurityStates.SingleAsync(s => s.SensorId == 2, Ct);
        second.ArmedAt = now.AddHours(-2);
        h.Db.SensorData.Add(new SensorData { SensorId = 2, Value = "12.6", Timestamp = now.AddMinutes(-40) });
        await h.Db.SaveChangesAsync(Ct);

        await h.Sut.RunAsync(now, Tick, false, Ct);

        VerifyAlert(h, "Garge Security alert", Times.Once());
        Assert.Equal(2, await h.Db.SensorOfflineNotifications.CountAsync(Ct));
    }

    [Fact]
    public async Task UndeliverableAlert_IsRetriedNextPass()
    {
        var now = DateTime.UtcNow;
        var h = await ArmedAsync(now.AddHours(-2), now.AddMinutes(-30));
        h.Notifier.Setup(n => n.NotifyUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await h.Sut.RunAsync(now, Tick, false, Ct);
        await h.Sut.RunAsync(now.AddMinutes(2), Tick, false, Ct);

        VerifyAlert(h, "Garge Security alert", Times.Exactly(2));
        Assert.Empty(h.Db.SensorOfflineNotifications);
    }

    [Fact]
    public async Task ShortGapOverlappingSilence_DelaysAlert_WhileNonOverlappingSensorStillAlerts()
    {
        var now = DateTime.UtcNow;
        var h = await ArmedAsync(now.AddHours(-2), now.AddMinutes(-30));
        h.Db.PipelineGaps.Add(new PipelineGap { StartedAt = now.AddMinutes(-20), EndedAt = now.AddMinutes(-15), Source = SecurityMode.GapSources.Operator });
        await h.Db.SaveChangesAsync(Ct);

        await h.Sut.RunAsync(now, Tick, false, Ct);
        VerifyAlert(h, "Garge Security alert", Times.Never());

        for (var minutes = 2; minutes <= 12; minutes += 2)
            await h.Sut.RunAsync(now.AddMinutes(minutes), Tick, false, Ct);
        VerifyAlert(h, "Garge Security alert", Times.Once());
    }

    [Fact]
    public async Task LongOperatorOutage_HoldsUserAlerts_AndNotifiesAdminsOnceWithAllClear()
    {
        var now = DateTime.UtcNow;
        var h = await ArmedAsync(now.AddHours(-2), now.AddMinutes(-3));
        GrantRoles(h.Db, "admin-1", RoleNames.Admin);
        var pipeline = new PipelineHealthService(h.Db, NullLogger<PipelineHealthService>.Instance);
        await pipeline.RecordHeartbeatAsync(true, now.AddMinutes(-3), Ct);

        await h.Sut.RunAsync(now, Tick, false, Ct);
        await h.Sut.RunAsync(now.AddMinutes(6), Tick, false, Ct);
        await h.Sut.RunAsync(now.AddMinutes(60), Tick, false, Ct);
        await h.Sut.RunAsync(now.AddMinutes(120), Tick, false, Ct);

        VerifyAlert(h, "Garge Security alert", Times.Never());
        h.Notifier.Verify(n => n.NotifyUserAsync("admin-1", "Garge pipeline outage", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        await pipeline.RecordHeartbeatAsync(true, now.AddMinutes(121), Ct);
        await h.Sut.RunAsync(now.AddMinutes(122), Tick, false, Ct);

        h.Notifier.Verify(n => n.NotifyUserAsync("admin-1", "Garge pipeline restored", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApiDownFortyMinutes_DoesNotAlertEveryoneOnRestart()
    {
        var now = DateTime.UtcNow;
        var h = await ArmedAsync(now.AddHours(-2), now.AddMinutes(-44));
        h.Db.PipelineHeartbeats.Add(new PipelineHeartbeat { Id = 1, LastDetectorTickAt = now.AddMinutes(-42) });
        await h.Db.SaveChangesAsync(Ct);

        await h.Sut.RunAsync(now, Tick, false, Ct);

        VerifyAlert(h, "Garge Security alert", Times.Never());
        Assert.Single(h.Db.PipelineGaps, g => g.Source == SecurityMode.GapSources.Api);
    }

    [Fact]
    public async Task QuietForAWeek_Disarms_AndAckReArms()
    {
        var now = DateTime.UtcNow;
        var h = await ArmedAsync(now.AddDays(-9), now.AddDays(-8));
        h.Db.SensorOfflineNotifications.Add(new SensorOfflineNotification
        {
            UserId = Owner, SensorId = SensorId, Kind = NotificationKinds.Security, NotifiedAt = now.AddDays(-8)
        });
        await h.Db.SaveChangesAsync(Ct);

        await h.Sut.RunAsync(now, Tick, false, Ct);

        var state = await h.Db.SensorSecurityStates.SingleAsync(Ct);
        Assert.Null(state.ArmedAt);
        Assert.NotNull(state.OfflineDisarmedAt);
        Assert.Equal((SecurityMode.States.Offline, (string?)null), SecurityModeService.ComputeState(true, state, null));
        Assert.NotNull((await h.Db.SensorOfflineNotifications.SingleAsync(Ct)).ResolvedAt);

        await h.Security.ApplyAckAsync((await h.Db.Sensors.SingleAsync(Ct)).Name, 600, true, null, Ct);

        Assert.NotNull(state.ArmedAt);
        Assert.Null(state.OfflineDisarmedAt);
    }

    [Fact]
    public async Task ReconcileSweep_ResetsDeviceWhoseOwnerRowIsGone()
    {
        var now = DateTime.UtcNow;
        var h = await ArmedAsync(now.AddHours(-2), now.AddMinutes(-5));
        h.Db.UserSensorSecurities.RemoveRange(h.Db.UserSensorSecurities);
        await h.Db.SaveChangesAsync(Ct);

        await h.Sut.RunAsync(now, Tick, true, Ct);

        Assert.Equal(SecurityMode.LongSleepSeconds, (await h.Db.SensorSecurityStates.SingleAsync(Ct)).RequestedSleepSeconds);
    }
    [Fact]
    public async Task PausedSensorWithOpenAlert_ResolvesWhenItReportsAgain()
    {
        var now = DateTime.UtcNow;
        var h = await ArmedAsync(now.AddHours(-2), now.AddMinutes(-1));
        var state = await h.Db.SensorSecurityStates.SingleAsync(Ct);
        state.ArmedAt = null;
        state.AppliedSleepSeconds = 3600;
        h.Db.SensorOfflineNotifications.Add(new SensorOfflineNotification
        {
            UserId = Owner, SensorId = SensorId, Kind = NotificationKinds.Security, NotifiedAt = now.AddMinutes(-30)
        });
        await h.Db.SaveChangesAsync(Ct);

        await h.Sut.RunAsync(now, Tick, false, Ct);

        Assert.NotNull((await h.Db.SensorOfflineNotifications.SingleAsync(Ct)).ResolvedAt);
        VerifyAlert(h, "Garge Security: Sensor back online", Times.Once());
    }

    [Fact]
    public async Task FormerOwnerRow_IsNotAlerted()
    {
        var now = DateTime.UtcNow;
        var h = await ArmedAsync(now.AddHours(-2), now.AddMinutes(-30));
        h.Db.UserSensors.RemoveRange(h.Db.UserSensors);
        await h.Db.SaveChangesAsync(Ct);

        await h.Sut.RunAsync(now, Tick, false, Ct);

        VerifyAlert(h, "Garge Security alert", Times.Never());
    }
}
