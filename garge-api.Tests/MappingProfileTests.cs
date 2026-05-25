using garge_api.Dtos.Automation;
using garge_api.Dtos.Group;
using garge_api.Models.Automation;
using garge_api.Models.Group;
using MapsterMapper;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// Verifies the Mapster configurations that replaced hand-written projections map field-for-field.
/// </summary>
public class MappingProfileTests : ControllerTestBase
{
    private static IMapper Mapper => RealMapper;

    [Fact]
    public void AutomationRule_MapsAllDtoFields()
    {
        var rule = new AutomationRule
        {
            Id = 7,
            TargetType = "switch",
            TargetId = 10,
            SensorType = "sensor",
            SensorId = 5,
            Condition = ">",
            Threshold = 12.5,
            Action = "on",
            IsEnabled = false,
            LastTriggeredAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            ElectricityPriceCondition = "below",
            ElectricityPriceThreshold = 0.42,
            ElectricityPriceArea = "NO1",
            ElectricityPriceOperator = "<",
            TimerDurationHours = 3.5,
            TimerActivatedAt = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc),
        };

        var dto = Mapper.Map<AutomationRuleDto>(rule);

        Assert.Equal(rule.Id, dto.Id);
        Assert.Equal(rule.TargetType, dto.TargetType);
        Assert.Equal(rule.TargetId, dto.TargetId);
        Assert.Equal(rule.SensorType, dto.SensorType);
        Assert.Equal(rule.SensorId, dto.SensorId);
        Assert.Equal(rule.Condition, dto.Condition);
        Assert.Equal(rule.Threshold, dto.Threshold);
        Assert.Equal(rule.Action, dto.Action);
        Assert.Equal(rule.IsEnabled, dto.IsEnabled);
        Assert.Equal(rule.LastTriggeredAt, dto.LastTriggeredAt);
        Assert.Equal(rule.ElectricityPriceCondition, dto.ElectricityPriceCondition);
        Assert.Equal(rule.ElectricityPriceThreshold, dto.ElectricityPriceThreshold);
        Assert.Equal(rule.ElectricityPriceArea, dto.ElectricityPriceArea);
        Assert.Equal(rule.ElectricityPriceOperator, dto.ElectricityPriceOperator);
        Assert.Equal(rule.TimerDurationHours, dto.TimerDurationHours);
        Assert.Equal(rule.TimerActivatedAt, dto.TimerActivatedAt);
    }

    [Fact]
    public void AutomationRule_NullableFieldsMapAsNull()
    {
        var rule = new AutomationRule
        {
            Id = 1,
            TargetType = "switch",
            TargetId = 10,
            SensorType = "sensor",
            SensorId = 5,
            Condition = ">",
            Threshold = 20,
            Action = "off",
        };

        var dto = Mapper.Map<AutomationRuleDto>(rule);

        Assert.Null(dto.LastTriggeredAt);
        Assert.Null(dto.ElectricityPriceCondition);
        Assert.Null(dto.ElectricityPriceThreshold);
        Assert.Null(dto.ElectricityPriceArea);
        Assert.Null(dto.ElectricityPriceOperator);
        Assert.Null(dto.TimerDurationHours);
        Assert.Null(dto.TimerActivatedAt);
        Assert.True(dto.IsEnabled); // entity default
    }

    [Fact]
    public void Group_MapsScalarsAndFlattensSensorAndSwitchIds()
    {
        var group = new Group
        {
            Id = 3,
            Name = "Garage",
            Icon = "house",
            UserId = "user-1",
            GroupSensors = new List<GroupSensor>
            {
                new() { GroupId = 3, SensorId = 11 },
                new() { GroupId = 3, SensorId = 12 },
            },
            GroupSwitches = new List<GroupSwitch>
            {
                new() { GroupId = 3, SwitchId = 21 },
            },
        };

        var dto = Mapper.Map<GroupDto>(group);

        Assert.Equal(3, dto.Id);
        Assert.Equal("Garage", dto.Name);
        Assert.Equal("house", dto.Icon);
        Assert.Equal(new[] { 11, 12 }, dto.SensorIds);
        Assert.Equal(new[] { 21 }, dto.SwitchIds);
    }

    [Fact]
    public void Group_NoAssociations_MapsEmptyIdLists()
    {
        var group = new Group { Id = 1, Name = "Empty", Icon = null, UserId = "user-1" };

        var dto = Mapper.Map<GroupDto>(group);

        Assert.Null(dto.Icon);
        Assert.Empty(dto.SensorIds);
        Assert.Empty(dto.SwitchIds);
    }
}
