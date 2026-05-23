using garge_api.Dtos.Automation;
using garge_api.Models;
using garge_api.Models.Automation;
using garge_api.Models.Mqtt;
using garge_api.Models.Sensor;
using garge_api.Models.Switch;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace garge_api.Tests;

public class AutomationControllerTests : ControllerTestBase
{
    private static AutomationRule MakeRule(int targetId = 10, int sensorId = 5) => new()
    {
        TargetType = "switch",
        TargetId = targetId,
        SensorType = "sensor",
        SensorId = sensorId,
        Condition = ">",
        Threshold = 20,
        Action = "on"
    };

    [Fact]
    public async Task GetRules_AdminUser_ReturnsAllRules()
    {
        var db = CreateDbContext();
        db.AutomationRules.AddRange(MakeRule(), MakeRule(targetId: 20, sensorId: 6));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateAutomationController(db, isAdmin: true).GetRules();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var rules = Assert.IsAssignableFrom<IEnumerable<AutomationRuleDto>>(ok.Value);
        Assert.Equal(2, rules.Count());
    }

    [Fact]
    public async Task GetRules_RegularUser_ReturnsOnlyAccessibleRules()
    {
        var db = CreateDbContext();

        db.Switches.Add(new Switch { Id = 10, Name = "switch-a", Type = "socket", Role = "switch" });
        db.Sensors.Add(new Sensor
        {
            Id = 5, Name = "sensor-1", Type = "temperature", Role = "sensor",
            RegistrationCode = "reg-1", DefaultName = "Sensor 1", ParentName = "hub-1"
        });
        db.UserSensors.Add(new UserSensor { UserId = "user-1", SensorId = 5 });
        db.DiscoveredDevices.Add(new DiscoveredDevice
        {
            DiscoveredBy = "hub-1", Target = "switch-a", Type = "socket", Timestamp = DateTime.UtcNow
        });
        db.AutomationRules.AddRange(
            MakeRule(targetId: 10, sensorId: 5),   // accessible
            MakeRule(targetId: 99, sensorId: 99)    // not accessible
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateAutomationController(db, userId: "user-1", isAdmin: false).GetRules();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var rules = Assert.IsAssignableFrom<IEnumerable<AutomationRuleDto>>(ok.Value);
        Assert.Single(rules);
    }

    [Fact]
    public async Task GetRules_UserWithNoAccess_ReturnsEmpty()
    {
        var db = CreateDbContext();
        db.AutomationRules.Add(MakeRule());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateAutomationController(db, userId: "user-1", isAdmin: false).GetRules();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var rules = Assert.IsAssignableFrom<IEnumerable<AutomationRuleDto>>(ok.Value);
        Assert.Empty(rules);
    }

    [Fact]
    public async Task CreateRule_AdminUser_ReturnsCreatedRule()
    {
        var db = CreateDbContext();
        var dto = new CreateAutomationRuleDto
        {
            TargetType = "switch", TargetId = 10, SensorType = "sensor", SensorId = 5,
            Condition = ">", Threshold = 25, Action = "on", IsEnabled = true
        };

        var result = await CreateAutomationController(db, isAdmin: true).CreateRule(dto);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var rule = Assert.IsType<AutomationRuleDto>(ok.Value);
        Assert.Equal("on", rule.Action);
        Assert.Equal(25, rule.Threshold);
        Assert.Single(db.AutomationRules);
    }

    private static void SeedAccessChain(ApplicationDbContext db, string userId, bool isOwner, SharePermission permission = SharePermission.Read)
    {
        db.Switches.Add(new Switch { Id = 10, Name = "switch-a", Type = "socket", Role = "switch" });
        db.Sensors.Add(new Sensor
        {
            Id = 5, Name = "sensor-1", Type = "temperature", Role = "sensor",
            RegistrationCode = "reg-1", DefaultName = "Sensor 1", ParentName = "hub-1"
        });
        db.UserSensors.Add(new UserSensor { UserId = userId, SensorId = 5, IsOwner = isOwner, Permission = permission });
        db.DiscoveredDevices.Add(new DiscoveredDevice
        {
            DiscoveredBy = "hub-1", Target = "switch-a", Type = "socket", Timestamp = DateTime.UtcNow
        });
    }

    private static CreateAutomationRuleDto MakeCreateDto() => new()
    {
        TargetType = "switch", TargetId = 10, SensorType = "sensor", SensorId = 5,
        Condition = ">", Threshold = 25, Action = "on", IsEnabled = true
    };

    [Fact]
    public async Task CreateRule_ReadShareViewer_Forbidden()
    {
        // A read-only share must not let the recipient create automations on the owner's garage.
        var db = CreateDbContext();
        SeedAccessChain(db, "viewer", isOwner: false, permission: SharePermission.Read);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateAutomationController(db, userId: "viewer", isAdmin: false).CreateRule(MakeCreateDto());

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(db.AutomationRules);
    }

    [Fact]
    public async Task CreateRule_EditShareViewer_Allowed()
    {
        var db = CreateDbContext();
        SeedAccessChain(db, "viewer", isOwner: false, permission: SharePermission.Edit);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateAutomationController(db, userId: "viewer", isAdmin: false).CreateRule(MakeCreateDto());

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Single(db.AutomationRules);
    }

    [Fact]
    public async Task MarkTriggered_RuleNotFound_ReturnsNotFound()
    {
        var db = CreateDbContext();

        var result = await CreateAutomationController(db, isAdmin: true).MarkTriggered(999);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task MarkTriggered_AdminUser_SetsLastTriggeredAtAndReturnsNoContent()
    {
        var db = CreateDbContext();
        var rule = MakeRule();
        db.AutomationRules.Add(rule);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateAutomationController(db, isAdmin: true).MarkTriggered(rule.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.NotNull(db.AutomationRules.Find(rule.Id)!.LastTriggeredAt);
    }
}
