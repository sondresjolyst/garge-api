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
        Assert.Equal(SecuritySetResult.Ok, await service.SetAsync(Owner, SensorId, true, Ct));
        return (service, publisher, notifier);
    }

    [Theory]
    [InlineData(12.65, 12550)]   // the normal case
    [InlineData(3.1, 3000)]      // exactly the minimum
    [InlineData(20.1, 20000)]    // exactly the maximum, the ADS1115 ceiling
    public void DeriveFloorMillivolts_AcceptsUsableThresholds(double volts, int expected)
        => Assert.Equal(expected, SecurityModeService.DeriveFloorMillivolts(volts));

    [Theory]
    [InlineData(3.09)]                    // one millivolt under the minimum
    [InlineData(20.11)]                   // one millivolt over the maximum
    [InlineData(0)]
    [InlineData(-12.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void DeriveFloorMillivolts_RefusesUnusableThresholds(double volts)
        => Assert.Null(SecurityModeService.DeriveFloorMillivolts(volts));

    // A device reporting a floor other than the one it was sent is not guarding the
    // battery the server thinks it is, so it must not read as armed.
    [Fact]
    public async Task Ack_WithAFloorOtherThanRequested_DoesNotArm()
    {
        var db = CreateDbContext();
        var (service, _, _) = await EnabledAsync(db);
        var name = (await db.Sensors.FindAsync([SensorId], Ct))!.Name;

        await service.ApplyAckAsync(name, SecurityMode.ShortSleepSeconds, true, null, true, 9999, ct: Ct);

        var state = await db.SensorSecurityStates.SingleAsync(Ct);
        Assert.Null(state.ArmedAt);
    }

    // A device that reports no floor while one was requested is not guarding the
    // battery either: it lost the setting.
    [Fact]
    public async Task Ack_ReportingNoFloorWhileOneIsRequested_DoesNotArm()
    {
        var db = CreateDbContext();
        var (service, _, _) = await EnabledAsync(db);
        Assert.NotNull((await db.SensorSecurityStates.SingleAsync(Ct)).FloorMillivolts);
        var name = (await db.Sensors.FindAsync([SensorId], Ct))!.Name;

        await service.ApplyAckAsync(name, SecurityMode.ShortSleepSeconds, true, null, true, null, ct: Ct);

        var state = await db.SensorSecurityStates.SingleAsync(Ct);
        Assert.Null(state.ArmedAt);
    }

    // An armed sensor that starts reporting a different floor has to disarm, or the
    // app keeps claiming a battery is watched that is not.
    [Fact]
    public async Task Ack_WithAFloorOtherThanRequested_DisarmsAnArmedSensor()
    {
        var db = CreateDbContext();
        var (service, _, _) = await EnabledAsync(db);
        var requested = (await db.SensorSecurityStates.SingleAsync(Ct)).FloorMillivolts;
        Assert.NotNull(requested);
        var name = (await db.Sensors.FindAsync([SensorId], Ct))!.Name;
        await service.ApplyAckAsync(name, SecurityMode.ShortSleepSeconds, true, null, true, requested, ct: Ct);
        Assert.NotNull((await db.SensorSecurityStates.SingleAsync(Ct)).ArmedAt);

        await service.ApplyAckAsync(name, SecurityMode.ShortSleepSeconds, true, null, true, requested + 100, ct: Ct);

        var state = await db.SensorSecurityStates.SingleAsync(Ct);
        Assert.Null(state.ArmedAt);
    }

    // The stored floor already matches what is wanted, so nothing else republishes.
    // Without a newer version the device ignores the retained message it applied, and
    // the sensor would stay unarmed and silent for good.
    [Fact]
    public async Task Ack_WithAFloorOtherThanRequested_RepublishesTheSettings()
    {
        var db = CreateDbContext();
        var (service, publisher, _) = await EnabledAsync(db);
        var requested = (await db.SensorSecurityStates.SingleAsync(Ct)).FloorMillivolts;
        Assert.NotNull(requested);
        var name = (await db.Sensors.FindAsync([SensorId], Ct))!.Name;
        var versionBefore = (await db.SensorSecurityStates.SingleAsync(Ct)).RequestedAt;

        await service.ApplyAckAsync(name, SecurityMode.ShortSleepSeconds, true, null, true, requested + 100, ct: Ct);

        // Once on enable and once for this mismatch, both carrying the wanted floor.
        VerifyPublished(publisher, SecurityMode.ShortSleepSeconds, true, requested, Times.Exactly(2));
        Assert.True((await db.SensorSecurityStates.SingleAsync(Ct)).RequestedAt > versionBefore);
    }

    [Fact]
    public async Task Ack_WithTheRequestedFloor_Arms()
    {
        var db = CreateDbContext();
        var (service, _, _) = await EnabledAsync(db);
        var requested = (await db.SensorSecurityStates.SingleAsync(Ct)).FloorMillivolts;
        Assert.NotNull(requested);
        var name = (await db.Sensors.FindAsync([SensorId], Ct))!.Name;

        await service.ApplyAckAsync(name, SecurityMode.ShortSleepSeconds, true, null, true, requested, ct: Ct);

        var state = await db.SensorSecurityStates.SingleAsync(Ct);
        Assert.NotNull(state.ArmedAt);
    }

    // Firmware that takes no settings never acks, so arming it would leave the sensor
    // reading pending for good and the app blaming a firmware update that cannot fix it.
    [Fact]
    public async Task Set_OnHardwareThatCannotTakeSettings_IsRefused()
    {
        var db = CreateDbContext();
        SeedReady(db);
        GrantRoles(db, Owner, RoleNames.GargeSecurity);
        (await db.Sensors.FindAsync([SensorId], Ct))!.SecurityCapable = false;
        await db.SaveChangesAsync(Ct);
        var (service, publisher, _) = BuildService(db);

        var result = await service.SetAsync(Owner, SensorId, true, Ct);

        Assert.Equal(SecuritySetResult.UnsupportedHardware, result);
        Assert.Empty(await db.UserSensorSecurities.ToListAsync(Ct));
        publisher.VerifyNoOtherCalls();
    }

    // Null is a device the bridge has not heard from, not one known to be incapable.
    [Fact]
    public async Task Set_WithCapabilityUnknown_IsAllowed()
    {
        var db = CreateDbContext();
        SeedReady(db);
        GrantRoles(db, Owner, RoleNames.GargeSecurity);
        Assert.Null((await db.Sensors.FindAsync([SensorId], Ct))!.SecurityCapable);
        var (service, _, _) = BuildService(db);

        Assert.Equal(SecuritySetResult.Ok, await service.SetAsync(Owner, SensorId, true, Ct));
    }

    // Turning it off has to keep working on hardware that cannot take settings: a sensor
    // enabled before the bridge knew better still needs a way out.
    [Fact]
    public async Task Set_Off_OnHardwareThatCannotTakeSettings_IsAllowed()
    {
        var db = CreateDbContext();
        var (service, _, _) = await EnabledAsync(db);
        (await db.Sensors.FindAsync([SensorId], Ct))!.SecurityCapable = false;
        await db.SaveChangesAsync(Ct);

        Assert.Equal(SecuritySetResult.Ok, await service.SetAsync(Owner, SensorId, false, Ct));
        Assert.False((await db.UserSensorSecurities.SingleAsync(Ct)).Enabled);
    }

    [Fact]
    public async Task SetCapability_RecordsItAndReportsItToTheOwner()
    {
        var db = CreateDbContext();
        SeedReady(db);
        GrantRoles(db, Owner, RoleNames.GargeSecurity);
        var (service, _, _) = BuildService(db);
        var name = (await db.Sensors.FindAsync([SensorId], Ct))!.Name;

        Assert.True(await service.SetCapabilityAsync(name, false, Ct));

        Assert.False((await db.Sensors.FindAsync([SensorId], Ct))!.SecurityCapable);
        Assert.False((await service.GetAsync(SensorId, Owner, true, Ct)).Capable);
    }

    // A sensor turned on before the bridge knew the hardware would otherwise sit enabled
    // and unarmed for good, which is the failure this whole flag exists to prevent.
    [Fact]
    public async Task SetCapability_False_TurnsOffASensorAlreadyOn()
    {
        var db = CreateDbContext();
        var (service, _, notifier) = await EnabledAsync(db);
        var name = (await db.Sensors.FindAsync([SensorId], Ct))!.Name;
        Assert.True((await db.UserSensorSecurities.SingleAsync(Ct)).Enabled);

        Assert.True(await service.SetCapabilityAsync(name, false, Ct));

        Assert.False((await db.UserSensorSecurities.SingleAsync(Ct)).Enabled);
        notifier.Verify(n => n.NotifyUserAsync(Owner, "Garge Security turned off",
            It.Is<string>(m => m.Contains("hardware does not support")), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetCapability_True_LeavesASensorOn()
    {
        var db = CreateDbContext();
        var (service, _, notifier) = await EnabledAsync(db);
        var name = (await db.Sensors.FindAsync([SensorId], Ct))!.Name;

        Assert.True(await service.SetCapabilityAsync(name, true, Ct));

        Assert.True((await db.UserSensorSecurities.SingleAsync(Ct)).Enabled);
        notifier.VerifyNoOtherCalls();
    }

    // The bridge posts this from every config message, so an unchanged value must stay a
    // success and write nothing.
    [Fact]
    public async Task SetCapability_Repeated_StaysSuccessful()
    {
        var db = CreateDbContext();
        SeedReady(db);
        var (service, _, _) = BuildService(db);
        var name = (await db.Sensors.FindAsync([SensorId], Ct))!.Name;

        Assert.True(await service.SetCapabilityAsync(name, false, Ct));
        Assert.True(await service.SetCapabilityAsync(name, false, Ct));
        Assert.False((await db.Sensors.FindAsync([SensorId], Ct))!.SecurityCapable);
    }

    [Fact]
    public async Task SetCapability_ForAnUnknownSensor_ReportsNotFound()
    {
        var db = CreateDbContext();
        SeedReady(db);
        var (service, _, _) = BuildService(db);

        Assert.False(await service.SetCapabilityAsync("garge_nosuchdevice_voltage", true, Ct));
    }

    // A new charging level means a new floor, and the device is still guarding the old
    // one until it acks. Reading armed in between claims a battery is watched at a level
    // nothing is watching it at.
    [Fact]
    public async Task ANewChargingLevel_DisarmsUntilTheDeviceAcksIt()
    {
        var db = CreateDbContext();
        var (service, publisher, _) = await EnabledAsync(db);
        var name = (await db.Sensors.FindAsync([SensorId], Ct))!.Name;
        var firstFloor = (await db.SensorSecurityStates.SingleAsync(Ct)).FloorMillivolts;
        await service.ApplyAckAsync(name, SecurityMode.ShortSleepSeconds, true, null, true, firstFloor, ct: Ct);
        Assert.NotNull((await db.SensorSecurityStates.SingleAsync(Ct)).ArmedAt);

        var rule = await db.AutomationRules.SingleAsync(Ct);
        rule.Threshold = 13.2;
        await db.SaveChangesAsync(Ct);
        await service.RecomputeDeviceAsync(Device, Ct);

        var state = await db.SensorSecurityStates.SingleAsync(Ct);
        Assert.Null(state.ArmedAt);
        Assert.Equal(13100, state.FloorMillivolts);
        VerifyPublished(publisher, SecurityMode.ShortSleepSeconds, true, 13100, Times.Once());
    }

    // A bridge that reports no floor at all runs against firmware that predates floor
    // reporting. Those devices have to keep arming, or the fleet stops working the
    // moment the server is deployed.
    [Fact]
    public async Task Ack_WithNoFloorReported_StillArms()
    {
        var db = CreateDbContext();
        var (service, _, _) = await EnabledAsync(db);
        var name = (await db.Sensors.FindAsync([SensorId], Ct))!.Name;

        await service.ApplyAckAsync(name, SecurityMode.ShortSleepSeconds, true, null, false, null, ct: Ct);

        var state = await db.SensorSecurityStates.SingleAsync(Ct);
        Assert.NotNull(state.ArmedAt);
    }

    [Theory]
    [InlineData(25.0)]   // 24 900 mV, above what the ADS1115 can read
    [InlineData(1.1)]    // 1 000 mV, a floor no reading can fall below
    public async Task Enable_WithThresholdOutsideBatteryRange_DoesNotArm(double threshold)
    {
        var db = CreateDbContext();
        SeedReady(db, threshold: threshold);
        GrantRoles(db, Owner, RoleNames.GargeSecurity);
        var (service, _, _) = BuildService(db);

        Assert.Equal(SecuritySetResult.InvalidThreshold, await service.SetAsync(Owner, SensorId, true, Ct));
        Assert.Empty(db.SensorSecurityStates);
        Assert.Empty(db.UserSensorSecurities.Where(r => r.Enabled));
    }

    // The boundary itself must still arm, so a legitimate low threshold is not refused.
    [Fact]
    public async Task Enable_WithThresholdAtTheBatteryRangeEdge_Arms()
    {
        var db = CreateDbContext();
        SeedReady(db, threshold: (SecurityMode.MinFloorMillivolts + SecurityMode.FloorMarginMillivolts) / 1000.0);
        GrantRoles(db, Owner, RoleNames.GargeSecurity);
        var (service, _, _) = BuildService(db);

        await service.SetAsync(Owner, SensorId, true, Ct);

        var state = await db.SensorSecurityStates.SingleAsync(Ct);
        Assert.Equal(SecurityMode.ShortSleepSeconds, state.RequestedSleepSeconds);
        Assert.Equal(SecurityMode.MinFloorMillivolts, state.FloorMillivolts);
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

        Assert.Equal(SecuritySetResult.ChargingAutomationRequired, await service.SetAsync(Owner, SensorId, true, Ct));
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

        Assert.Equal(SecuritySetResult.NoAlertChannel, await service.SetAsync(Owner, SensorId, true, Ct));
    }

    [Fact]
    public async Task Enable_WithoutEntitlement_StoresIntentButKeepsDeviceOnLongSleep()
    {
        var db = CreateDbContext();
        SeedReady(db);
        var (service, publisher, _) = BuildService(db);

        await service.SetAsync(Owner, SensorId, true, Ct);

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

        await service.SetAsync(Owner, SensorId, false, Ct);

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
        notifier.Verify(n => n.NotifyUserAsync(Owner, "Garge Security turned off", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
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
        notifier.Verify(n => n.NotifyUserAsync(Owner, "Garge Security turned off", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Ack_MatchingRequest_ArmsOnce()
    {
        var db = CreateDbContext();
        var (service, _, _) = await EnabledAsync(db);
        var sensorName = (await db.Sensors.SingleAsync(Ct)).Name;

        Assert.True(await service.ApplyAckAsync(sensorName, 600, true, "v1.15.0", ct: Ct));
        var state = await db.SensorSecurityStates.SingleAsync(Ct);
        var armedAt = state.ArmedAt;
        await service.ApplyAckAsync(sensorName, 600, true, "v1.15.0", ct: Ct);

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

        await service.ApplyAckAsync(sensorName, 3600, false, null, ct: Ct);

        Assert.Null((await db.SensorSecurityStates.SingleAsync(Ct)).ArmedAt);
    }

    [Fact]
    public async Task Ack_FloorTripped_DisarmsAndNotifiesOnce()
    {
        var db = CreateDbContext();
        var (service, _, notifier) = await EnabledAsync(db);
        var sensorName = (await db.Sensors.SingleAsync(Ct)).Name;
        await service.ApplyAckAsync(sensorName, 600, true, null, ct: Ct);

        await service.ApplyAckAsync(sensorName, 3600, true, null, ct: Ct);
        await service.ApplyAckAsync(sensorName, 3600, true, null, ct: Ct);

        var state = await db.SensorSecurityStates.SingleAsync(Ct);
        Assert.Null(state.ArmedAt);
        Assert.Equal((SecurityMode.States.PausedLowBattery, SecurityMode.Reasons.LowBattery), SecurityModeService.ComputeState(true, state, null));
        notifier.Verify(n => n.NotifyUserAsync(Owner, "Garge Security paused", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
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
        await service.SetAsync(Owner, SensorId, true, Ct);

        await service.ApplyAckAsync((await db.Sensors.FindAsync([SensorId], Ct))!.Name, 600, true, null, ct: Ct);

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

        Assert.True(await service.ApplyAckAsync((await db.Sensors.SingleAsync(Ct)).Name, 3600, false, null, ct: Ct));
        Assert.Empty(db.SensorSecurityStates);
    }

    [Fact]
    public async Task Ack_UnknownSensor_ReturnsFalse()
    {
        var db = CreateDbContext();
        var (service, _, _) = BuildService(db);

        Assert.False(await service.ApplyAckAsync("nope", 600, true, null, ct: Ct));
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
        await service.SetAsync(Owner, SensorId, true, Ct);

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

        Assert.Equal(SecuritySetResult.UnsupportedSensor, await service.SetAsync(Owner, SensorId, true, Ct));
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

        Assert.Equal(SecuritySetResult.NoAlertChannel, await service.SetAsync(Owner, SensorId, true, Ct));
    }
}
