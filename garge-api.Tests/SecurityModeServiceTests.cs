using garge_api.Constants;
using garge_api.Models.Push;
using garge_api.Models.Sensor;
using garge_api.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using static garge_api.Tests.SecurityTestData;

namespace garge_api.Tests;

public class SecurityModeServiceTests : ControllerTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(SecurityModeService Service, Mock<IDeviceSettingsPublisher> Publisher, Mock<ISecurityNotifier> Notifier)> EnabledAsync(Models.ApplicationDbContext db)
    {
        SeedReady(db);
        GrantRoles(db, Owner, RoleNames.GargeSecurity);
        var (service, publisher, notifier) = BuildService(db);
        Assert.Equal(SecuritySetResult.Ok, await service.SetAsync(Owner, SensorId, true, null, Ct));
        return (service, publisher, notifier);
    }

    [Fact]
    public async Task Enable_WithChargingRule_Requests600AndPublishesFloorFromRuleThreshold()
    {
        var db = CreateDbContext();
        var (_, publisher, _) = await EnabledAsync(db);

        var state = await db.SensorSecurityStates.SingleAsync(Ct);
        Assert.Equal(SecurityMode.ShortSleepSeconds, state.RequestedSleepSeconds);
        Assert.Equal(12550, state.FloorMillivolts);
        Assert.Null(state.ArmedAt);
        VerifyPublished(publisher, 600, true, 12550, Times.Once());
    }

    [Theory]
    [InlineData("<")]
    [InlineData("<=")]
    public async Task FindChargingRule_AcceptsLessThanConditions(string condition)
    {
        var db = CreateDbContext();
        AddSensor(db);
        AddSocket(db);
        AddChargingRule(db, condition: condition);
        await db.SaveChangesAsync(Ct);
        var (service, _, _) = BuildService(db);

        Assert.NotNull(await service.FindChargingRuleAsync(SensorId, Ct));
    }

    [Fact]
    public async Task FindChargingRule_RejectsGreaterThanOffActionDisabledAndNonSocket()
    {
        var db = CreateDbContext();
        AddSensor(db);
        AddSocket(db);
        AddSocket(db, id: 11, type: "light");
        AddChargingRule(db, condition: ">");
        AddChargingRule(db, action: "off");
        AddChargingRule(db, enabled: false);
        AddChargingRule(db, targetId: 11);
        await db.SaveChangesAsync(Ct);
        var (service, _, _) = BuildService(db);

        Assert.Null(await service.FindChargingRuleAsync(SensorId, Ct));
    }

    [Fact]
    public async Task FindChargingRule_ActionIsCaseInsensitive_AndHighestThresholdWins()
    {
        var db = CreateDbContext();
        AddSensor(db);
        AddSocket(db);
        AddChargingRule(db, threshold: 12.2, action: "ON");
        AddChargingRule(db, threshold: 12.6, action: "On");
        await db.SaveChangesAsync(Ct);
        var (service, _, _) = BuildService(db);

        var rule = await service.FindChargingRuleAsync(SensorId, Ct);

        Assert.Equal(12.6, rule!.Threshold);
    }

    [Fact]
    public async Task Enable_WithoutChargingRule_IsRejected()
    {
        var db = CreateDbContext();
        AddSensor(db);
        AddOwner(db);
        await db.SaveChangesAsync(Ct);
        GrantRoles(db, Owner, RoleNames.GargeSecurity);
        var (service, publisher, _) = BuildService(db);

        Assert.Equal(SecuritySetResult.ChargingAutomationRequired, await service.SetAsync(Owner, SensorId, true, null, Ct));
        Assert.Empty(db.UserSensorSecurities);
        publisher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Enable_WithNoAlertChannel_IsRejected()
    {
        var db = CreateDbContext();
        AddSensor(db);
        AddSocket(db);
        AddChargingRule(db);
        AddOwner(db, push: false, email: false);
        await db.SaveChangesAsync(Ct);
        GrantRoles(db, Owner, RoleNames.GargeSecurity);
        var (service, _, _) = BuildService(db);

        Assert.Equal(SecuritySetResult.NoAlertChannel, await service.SetAsync(Owner, SensorId, true, null, Ct));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(181)]
    public async Task Enable_WithThresholdOutOfRange_IsRejected(int minutes)
    {
        var db = CreateDbContext();
        SeedReady(db);
        GrantRoles(db, Owner, RoleNames.GargeSecurity);
        var (service, _, _) = BuildService(db);

        Assert.Equal(SecuritySetResult.InvalidThreshold, await service.SetAsync(Owner, SensorId, true, minutes, Ct));
    }

    [Fact]
    public async Task Enable_WithoutEntitlement_StoresIntentButKeepsDeviceOnLongSleep()
    {
        var db = CreateDbContext();
        SeedReady(db);
        var (service, publisher, _) = BuildService(db);

        await service.SetAsync(Owner, SensorId, true, null, Ct);

        Assert.Empty(db.SensorSecurityStates);
        publisher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Disable_Republishes3600_ClearsArming_AndResolvesOpenAlert()
    {
        var db = CreateDbContext();
        var (service, publisher, _) = await EnabledAsync(db);
        var state = await db.SensorSecurityStates.SingleAsync(Ct);
        state.ArmedAt = DateTime.UtcNow.AddHours(-1);
        db.SensorOfflineNotifications.Add(new SensorOfflineNotification { UserId = Owner, SensorId = SensorId, Kind = NotificationKinds.Security });
        await db.SaveChangesAsync(Ct);

        await service.SetAsync(Owner, SensorId, false, null, Ct);

        Assert.Equal(SecurityMode.LongSleepSeconds, state.RequestedSleepSeconds);
        Assert.Null(state.ArmedAt);
        Assert.NotNull((await db.SensorOfflineNotifications.SingleAsync(Ct)).ResolvedAt);
        VerifyPublished(publisher, 3600, false, null, Times.Once());
    }

    [Fact]
    public async Task Reconcile_AfterChargingRuleDisabled_TurnsSecurityOffAndNotifies()
    {
        var db = CreateDbContext();
        var (service, publisher, notifier) = await EnabledAsync(db);
        (await db.AutomationRules.SingleAsync(Ct)).IsEnabled = false;
        await db.SaveChangesAsync(Ct);

        await service.ReconcileSensorAsync(SensorId, Ct);

        Assert.False((await db.UserSensorSecurities.SingleAsync(Ct)).Enabled);
        VerifyPublished(publisher, 3600, false, null, Times.Once());
        notifier.Verify(n => n.NotifyUserAsync(Owner, "Garge Security turned off", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Reconcile_AfterChargingRuleDeleted_TurnsSecurityOff()
    {
        var db = CreateDbContext();
        var (service, _, _) = await EnabledAsync(db);
        db.AutomationRules.Remove(await db.AutomationRules.SingleAsync(Ct));
        await db.SaveChangesAsync(Ct);

        await service.ReconcileSensorAsync(SensorId, Ct);

        Assert.False((await db.UserSensorSecurities.SingleAsync(Ct)).Enabled);
    }

    [Fact]
    public async Task Reconcile_AfterRuleRepointedToAnotherSensor_TurnsSecurityOffOnTheOldSensor()
    {
        var db = CreateDbContext();
        var (service, _, _) = await EnabledAsync(db);
        AddSensor(db, id: 2, device: "garge_other");
        (await db.AutomationRules.SingleAsync(Ct)).SensorId = 2;
        await db.SaveChangesAsync(Ct);

        await service.ReconcileSensorAsync(SensorId, Ct);
        await service.ReconcileSensorAsync(2, Ct);

        Assert.False((await db.UserSensorSecurities.SingleAsync(Ct)).Enabled);
    }

    [Fact]
    public async Task Reconcile_WhenRuleThresholdChanges_RepublishesNewFloor()
    {
        var db = CreateDbContext();
        var (service, publisher, _) = await EnabledAsync(db);
        (await db.AutomationRules.SingleAsync(Ct)).Threshold = 12.4;
        await db.SaveChangesAsync(Ct);

        await service.ReconcileSensorAsync(SensorId, Ct);

        Assert.True((await db.UserSensorSecurities.SingleAsync(Ct)).Enabled);
        VerifyPublished(publisher, 600, true, 12300, Times.Once());
    }

    [Fact]
    public async Task ReconcileUser_AfterRoleRemoved_TurnsSecurityOffAndNotifies()
    {
        var db = CreateDbContext();
        var (service, publisher, notifier) = await EnabledAsync(db);
        db.UserRoles.RemoveRange(db.UserRoles);
        await db.SaveChangesAsync(Ct);

        await service.ReconcileUserAsync(Owner, Ct);

        Assert.False((await db.UserSensorSecurities.SingleAsync(Ct)).Enabled);
        Assert.Equal(SecurityMode.LongSleepSeconds, (await db.SensorSecurityStates.SingleAsync(Ct)).RequestedSleepSeconds);
        VerifyPublished(publisher, 3600, false, null, Times.Once());
        notifier.Verify(n => n.NotifyUserAsync(Owner, "Garge Security turned off", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Ack_MatchingRequest_ArmsOnce()
    {
        var db = CreateDbContext();
        var (service, _, _) = await EnabledAsync(db);
        var sensorName = (await db.Sensors.SingleAsync(Ct)).Name;

        Assert.True(await service.ApplyAckAsync(sensorName, 600, true, "v1.15.0", Ct));
        var state = await db.SensorSecurityStates.SingleAsync(Ct);
        var armedAt = state.ArmedAt;
        await service.ApplyAckAsync(sensorName, 600, true, "v1.15.0", Ct);

        Assert.NotNull(armedAt);
        Assert.Equal(armedAt, state.ArmedAt);
        Assert.Equal("v1.15.0", state.ReportedFirmwareVersion);
    }

    [Fact]
    public async Task Ack_StaleInterval_DoesNotArm()
    {
        var db = CreateDbContext();
        var (service, _, _) = await EnabledAsync(db);
        var sensorName = (await db.Sensors.SingleAsync(Ct)).Name;

        await service.ApplyAckAsync(sensorName, 3600, false, null, Ct);

        Assert.Null((await db.SensorSecurityStates.SingleAsync(Ct)).ArmedAt);
    }

    [Fact]
    public async Task Ack_FloorTripped_DisarmsAndNotifiesOnce()
    {
        var db = CreateDbContext();
        var (service, _, notifier) = await EnabledAsync(db);
        var sensorName = (await db.Sensors.SingleAsync(Ct)).Name;
        await service.ApplyAckAsync(sensorName, 600, true, null, Ct);

        await service.ApplyAckAsync(sensorName, 3600, true, null, Ct);
        await service.ApplyAckAsync(sensorName, 3600, true, null, Ct);

        var state = await db.SensorSecurityStates.SingleAsync(Ct);
        Assert.Null(state.ArmedAt);
        Assert.Equal((SecurityMode.States.PausedLowBattery, SecurityMode.Reasons.LowBattery), SecurityModeService.ComputeState(true, state, null));
        notifier.Verify(n => n.NotifyUserAsync(Owner, "Garge Security paused", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Ack_FromOneEntity_UpdatesEverySensorOnTheDevice()
    {
        var db = CreateDbContext();
        SeedReady(db);
        AddSensor(db, id: 2);
        await db.SaveChangesAsync(Ct);
        GrantRoles(db, Owner, RoleNames.GargeSecurity);
        var (service, _, _) = BuildService(db);
        await service.SetAsync(Owner, SensorId, true, null, Ct);

        await service.ApplyAckAsync((await db.Sensors.FindAsync([SensorId], Ct))!.Name, 600, true, null, Ct);

        var states = await db.SensorSecurityStates.ToListAsync(Ct);
        Assert.Equal(2, states.Count);
        Assert.All(states, s => Assert.NotNull(s.ArmedAt));
    }

    [Fact]
    public async Task Ack_ForDeviceWithoutSecurityState_IsANoOp()
    {
        var db = CreateDbContext();
        AddSensor(db);
        await db.SaveChangesAsync(Ct);
        var (service, _, _) = BuildService(db);

        Assert.True(await service.ApplyAckAsync((await db.Sensors.SingleAsync(Ct)).Name, 3600, false, null, Ct));
        Assert.Empty(db.SensorSecurityStates);
    }

    [Fact]
    public async Task Ack_UnknownSensor_ReturnsFalse()
    {
        var db = CreateDbContext();
        var (service, _, _) = BuildService(db);

        Assert.False(await service.ApplyAckAsync("nope", 600, true, null, Ct));
    }

    [Fact]
    public async Task DeviceSettings_ListsOneEntryPerDevice()
    {
        var db = CreateDbContext();
        SeedReady(db);
        AddSensor(db, id: 2);
        await db.SaveChangesAsync(Ct);
        GrantRoles(db, Owner, RoleNames.GargeSecurity);
        var (service, _, _) = BuildService(db);
        await service.SetAsync(Owner, SensorId, true, null, Ct);

        var settings = await service.GetDeviceSettingsAsync(Ct);

        var entry = Assert.Single(settings);
        Assert.Equal(Device, entry.DeviceName);
        Assert.Equal(600, entry.SleepSeconds);
        Assert.True(entry.SecurityEnabled);
        Assert.Equal(12550, entry.FloorMillivolts);
        Assert.Equal(SensorId, entry.SensorId);
    }

    [Fact]
    public void ComputeState_CoversEveryState()
    {
        var now = DateTime.UtcNow;
        var requested = new SensorSecurityState { RequestedSleepSeconds = 600, RequestedAt = now.AddMinutes(-30) };

        Assert.Equal((SecurityMode.States.Off, (string?)null), SecurityModeService.ComputeState(false, requested, null));
        Assert.Equal((SecurityMode.States.Pending, SecurityMode.Reasons.AwaitingWake), SecurityModeService.ComputeState(true, requested, now.AddMinutes(-40)));
        Assert.Equal((SecurityMode.States.Pending, SecurityMode.Reasons.FirmwareTooOld), SecurityModeService.ComputeState(true, requested, now.AddMinutes(-5)));

        var armed = new SensorSecurityState { RequestedSleepSeconds = 600, AppliedSleepSeconds = 600, ArmedAt = now };
        Assert.Equal((SecurityMode.States.Armed, (string?)null), SecurityModeService.ComputeState(true, armed, now));

        var offline = new SensorSecurityState { RequestedSleepSeconds = 600, OfflineDisarmedAt = now };
        Assert.Equal((SecurityMode.States.Offline, (string?)null), SecurityModeService.ComputeState(true, offline, null));
    }
    [Fact]
    public async Task Reconcile_RowOfUserWhoNoLongerOwnsTheSensor_IsDisabledAndDeviceReturnsToLongSleep()
    {
        var db = CreateDbContext();
        var (service, publisher, notifier) = await EnabledAsync(db);
        db.UserSensors.RemoveRange(db.UserSensors);
        await db.SaveChangesAsync(Ct);

        await service.ReconcileSensorAsync(SensorId, Ct);

        Assert.False((await db.UserSensorSecurities.SingleAsync(Ct)).Enabled);
        VerifyPublished(publisher, 3600, false, null, Times.Once());
        notifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Enable_OnNonVoltageSensor_IsRejectedEvenWithALessThanOnRule()
    {
        var db = CreateDbContext();
        SeedReady(db);
        (await db.Sensors.SingleAsync(Ct)).Type = "temperature";
        await db.SaveChangesAsync(Ct);
        GrantRoles(db, Owner, RoleNames.GargeSecurity);
        var (service, publisher, _) = BuildService(db);

        Assert.Equal(SecuritySetResult.UnsupportedSensor, await service.SetAsync(Owner, SensorId, true, null, Ct));
        Assert.Null(await service.FindChargingRuleAsync(SensorId, Ct));
        publisher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Enable_WithPushFlagButNoSubscriptionAndEmailOff_IsRejected()
    {
        var db = CreateDbContext();
        AddSensor(db);
        AddSocket(db);
        AddChargingRule(db);
        AddOwner(db, push: true, email: false);
        await db.SaveChangesAsync(Ct);
        GrantRoles(db, Owner, RoleNames.GargeSecurity);
        var (service, _, _) = BuildService(db);

        Assert.Equal(SecuritySetResult.NoAlertChannel, await service.SetAsync(Owner, SensorId, true, null, Ct));
    }
}
