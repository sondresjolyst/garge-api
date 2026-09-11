using garge_api.Constants;
using garge_api.Controllers;
using garge_api.Dtos.Sensor;
using garge_api.Hubs;
using garge_api.Models;
using garge_api.Models.Sensor;
using garge_api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static garge_api.Tests.SecurityTestData;

namespace garge_api.Tests;

public class SensorSecurityControllerTests : ControllerTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (SensorSecurityController Controller, Mock<IDeviceSettingsPublisher> Publisher) Build(ApplicationDbContext db, string userId = Owner)
    {
        var ownership = new Mock<IDeviceOwnershipService>();
        ownership.Setup(o => o.CanUserAccessSensorAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string uid, int sid, CancellationToken _) => db.UserSensors.Any(us => us.UserId == uid && us.SensorId == sid));
        var (service, publisher, _) = BuildService(db);
        var controller = new SensorSecurityController(db, ownership.Object, new PermissionService(db), service,
            NullLogger<SensorSecurityController>.Instance)
        {
            ControllerContext = MakeControllerContext(userId)
        };
        return (controller, publisher);
    }

    private static void SeedEntitledOwner(ApplicationDbContext db)
    {
        SeedReady(db);
        GrantRoles(db, Owner, RoleNames.GargeSecurity);
    }

    private static string? ErrorCode(IActionResult result) =>
        ((result as ObjectResult)?.Value?.GetType().GetProperty("code")?.GetValue(((ObjectResult)result).Value)) as string;

    [Fact]
    public async Task NotEntitled_Gets404OnEveryVerb()
    {
        var db = CreateDbContext();
        SeedReady(db);
        var (controller, _) = Build(db);

        Assert.IsType<NotFoundResult>(await controller.GetSecurity(SensorId, Ct));
        Assert.IsType<NotFoundResult>(await controller.UpdateSecurity(SensorId, new UpdateSensorSecurityDto { Enabled = true }, Ct));
        Assert.IsType<NotFoundResult>(await controller.RemoveSecurity(SensorId, Ct));
    }

    [Fact]
    public async Task EntitledButNoAccessToSensor_Gets404()
    {
        var db = CreateDbContext();
        SeedEntitledOwner(db);
        GrantRoles(db, "stranger", RoleNames.GargeSecurity);
        var (controller, _) = Build(db, "stranger");

        Assert.IsType<NotFoundResult>(await controller.GetSecurity(SensorId, Ct));
    }

    [Fact]
    public async Task UnknownSensor_Gets404()
    {
        var db = CreateDbContext();
        SeedEntitledOwner(db);
        var (controller, _) = Build(db);

        Assert.IsType<NotFoundResult>(await controller.GetSecurity(999, Ct));
    }

    [Fact]
    public async Task EditShare_CanReadButNotChange()
    {
        var db = CreateDbContext();
        SeedEntitledOwner(db);
        db.UserSensors.Add(new UserSensor { UserId = "sharer", SensorId = SensorId, IsOwner = false, Permission = SharePermission.Edit });
        await db.SaveChangesAsync(Ct);
        GrantRoles(db, "sharer", RoleNames.GargeSecurity);
        var (controller, _) = Build(db, "sharer");

        var get = Assert.IsType<OkObjectResult>(await controller.GetSecurity(SensorId, Ct));
        Assert.False(((SensorSecurityDto)get.Value!).IsOwner);
        Assert.IsType<ForbidResult>(await controller.UpdateSecurity(SensorId, new UpdateSensorSecurityDto { Enabled = true }, Ct));
        Assert.IsType<ForbidResult>(await controller.RemoveSecurity(SensorId, Ct));
    }

    [Fact]
    public async Task Enable_WithoutChargingRule_Returns400WithCode()
    {
        var db = CreateDbContext();
        SeedEntitledOwner(db);
        db.AutomationRules.RemoveRange(db.AutomationRules);
        await db.SaveChangesAsync(Ct);
        var (controller, _) = Build(db);

        var result = await controller.UpdateSecurity(SensorId, new UpdateSensorSecurityDto { Enabled = true }, Ct);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(SecurityMode.ErrorCodes.ChargingAutomationRequired, ErrorCode(result));
    }

    [Fact]
    public async Task Enable_WithGreaterThanRuleOnly_Returns400()
    {
        var db = CreateDbContext();
        SeedEntitledOwner(db);
        (await db.AutomationRules.SingleAsync(Ct)).Condition = ">";
        await db.SaveChangesAsync(Ct);
        var (controller, _) = Build(db);

        Assert.Equal(SecurityMode.ErrorCodes.ChargingAutomationRequired,
            ErrorCode(await controller.UpdateSecurity(SensorId, new UpdateSensorSecurityDto { Enabled = true }, Ct)));
    }

    [Fact]
    public async Task Enable_WithNonSocketTarget_Returns400()
    {
        var db = CreateDbContext();
        SeedEntitledOwner(db);
        (await db.Switches.SingleAsync(Ct)).Type = "light";
        await db.SaveChangesAsync(Ct);
        var (controller, _) = Build(db);

        Assert.Equal(SecurityMode.ErrorCodes.ChargingAutomationRequired,
            ErrorCode(await controller.UpdateSecurity(SensorId, new UpdateSensorSecurityDto { Enabled = true }, Ct)));
    }

    [Fact]
    public async Task Enable_WithNoAlertChannel_Returns409()
    {
        var db = CreateDbContext();
        SeedEntitledOwner(db);
        var profile = await db.UserProfiles.SingleAsync(Ct);
        profile.PushNotificationsEnabled = false;
        profile.EmailNotificationsEnabled = false;
        await db.SaveChangesAsync(Ct);
        var (controller, _) = Build(db);

        var result = await controller.UpdateSecurity(SensorId, new UpdateSensorSecurityDto { Enabled = true }, Ct);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(SecurityMode.ErrorCodes.NoAlertChannel, ErrorCode(result));
    }

    [Fact]
    public async Task Enable_WithInvalidThreshold_Returns400()
    {
        var db = CreateDbContext();
        SeedEntitledOwner(db);
        var (controller, _) = Build(db);

        Assert.Equal(SecurityMode.ErrorCodes.InvalidThreshold,
            ErrorCode(await controller.UpdateSecurity(SensorId, new UpdateSensorSecurityDto { Enabled = true, ThresholdMinutes = 10 }, Ct)));
    }

    [Fact]
    public async Task Enable_Succeeds_ReturnsPendingAndRequests600()
    {
        var db = CreateDbContext();
        SeedEntitledOwner(db);
        var (controller, publisher) = Build(db);

        var result = Assert.IsType<OkObjectResult>(await controller.UpdateSecurity(SensorId, new UpdateSensorSecurityDto { Enabled = true, ThresholdMinutes = 40 }, Ct));
        var dto = (SensorSecurityDto)result.Value!;

        Assert.True(dto.Enabled);
        Assert.Equal(40, dto.ThresholdMinutes);
        Assert.Equal(600, dto.RequestedSleepSeconds);
        Assert.Equal(SecurityMode.States.Pending, dto.State);
        Assert.Equal(SecurityMode.Reasons.AwaitingWake, dto.Reason);
        Assert.True(dto.IsOwner);
        Assert.NotNull(dto.EnforcingRule);
        VerifyPublished(publisher, 600, true, 12550, Times.Once());
    }

    [Fact]
    public async Task Remove_TurnsOffAndRepublishes3600()
    {
        var db = CreateDbContext();
        SeedEntitledOwner(db);
        var (controller, publisher) = Build(db);
        await controller.UpdateSecurity(SensorId, new UpdateSensorSecurityDto { Enabled = true }, Ct);

        Assert.IsType<NoContentResult>(await controller.RemoveSecurity(SensorId, Ct));

        Assert.Empty(db.UserSensorSecurities);
        Assert.Equal(3600, (await db.SensorSecurityStates.SingleAsync(Ct)).RequestedSleepSeconds);
        VerifyPublished(publisher, 3600, false, null, Times.Once());
    }

    [Fact]
    public async Task ReportSettings_UnknownSensor_Returns404()
    {
        var db = CreateDbContext();
        var (controller, _) = Build(db);

        Assert.IsType<NotFoundObjectResult>(await controller.ReportSettings("nope", new ReportedSettingsDto { SleepSeconds = 600, SecurityEnabled = true }, Ct));
    }

    [Fact]
    public async Task ReportSettings_ArmsAndGetShowsArmed()
    {
        var db = CreateDbContext();
        SeedEntitledOwner(db);
        var (controller, _) = Build(db);
        await controller.UpdateSecurity(SensorId, new UpdateSensorSecurityDto { Enabled = true }, Ct);

        Assert.IsType<NoContentResult>(await controller.ReportSettings((await db.Sensors.SingleAsync(Ct)).Name,
            new ReportedSettingsDto { SleepSeconds = 600, SecurityEnabled = true, Version = "v1.15.0" }, Ct));

        var dto = (SensorSecurityDto)Assert.IsType<OkObjectResult>(await controller.GetSecurity(SensorId, Ct)).Value!;
        Assert.Equal(SecurityMode.States.Armed, dto.State);
        Assert.NotNull(dto.ArmedAt);
    }
}

public class SensorListSecurityFieldTests : ControllerTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<SensorDto>> ListAsync(ApplicationDbContext db, string userId)
    {
        var ownership = new Mock<IDeviceOwnershipService>();
        var hub = new Mock<IHubContext<DeviceHub>>();
        var capacity = new Mock<ISubscriptionCapacityService>().Object;
        var (security, _, _) = BuildService(db);
        var controller = new SensorController(db, RealMapper, NullLogger<SensorController>.Instance, ownership.Object, hub.Object, capacity,
            new PermissionService(db), security)
        {
            ControllerContext = MakeControllerContext(userId)
        };

        var result = Assert.IsType<OkObjectResult>(await controller.GetAllSensors(Ct));
        return (List<SensorDto>)result.Value!;
    }

    [Fact]
    public async Task EntitledOwner_SeesSecuritySummary()
    {
        var db = CreateDbContext();
        SeedReady(db);
        GrantRoles(db, Owner, RoleNames.GargeSecurity);
        var (service, _, _) = BuildService(db);
        await service.SetAsync(Owner, SensorId, true, null, Ct);

        var sensor = Assert.Single(await ListAsync(db, Owner));

        Assert.NotNull(sensor.Security);
        Assert.True(sensor.Security!.Enabled);
        Assert.Equal(SecurityMode.States.Pending, sensor.Security.State);
    }

    [Fact]
    public async Task NotEntitled_HasNoSecurityField_EvenWithAStoredRow()
    {
        var db = CreateDbContext();
        SeedReady(db);
        db.UserSensorSecurities.Add(new UserSensorSecurity { UserId = Owner, SensorId = SensorId, Enabled = true });
        await db.SaveChangesAsync(Ct);

        var sensor = Assert.Single(await ListAsync(db, Owner));

        Assert.Null(sensor.Security);
    }
}
