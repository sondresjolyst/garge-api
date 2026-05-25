using System.Reflection;
using garge_api.Constants;
using garge_api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// Locks the declarative admin-role gates on the sensor, switch and MQTT admin-CRUD actions. The prior
/// refactor replaced in-body <c>if (!IsAdmin()) Forbid()</c> checks with <c>[Authorize(Roles = ...)]</c>
/// attributes. Unit-level controller calls bypass the MVC authorization pipeline, so a dropped attribute
/// would otherwise go unnoticed. There is no integration/WebApplicationFactory harness in this project,
/// so this asserts via reflection that each gated action still carries an <see cref="AuthorizeAttribute"/>
/// whose Roles names exactly the expected role pair.
/// </summary>
public class AdminRoleGateAttributeTests
{
    private static readonly string SensorRoles = $"{RoleNames.Admin},{RoleNames.SensorAdmin}";
    private static readonly string SwitchRoles = $"{RoleNames.Admin},{RoleNames.SwitchAdmin}";
    private static readonly string MqttRoles = $"{RoleNames.Admin},{RoleNames.MqttAdmin}";

    public static TheoryData<Type, string, string> GatedActions() => new()
    {
        // SensorController admin-CRUD actions.
        { typeof(SensorController), nameof(SensorController.CreateSensor), SensorRoles },
        { typeof(SensorController), nameof(SensorController.CreateSensorDataById), SensorRoles },
        { typeof(SensorController), nameof(SensorController.CreateSensorDataByName), SensorRoles },
        { typeof(SensorController), nameof(SensorController.UpdateSensor), SensorRoles },
        { typeof(SensorController), nameof(SensorController.DeleteSensor), SensorRoles },
        { typeof(SensorController), nameof(SensorController.DeleteSensorData), SensorRoles },
        { typeof(SensorController), nameof(SensorController.DeleteAllSensorData), SensorRoles },

        // SwitchesController admin-CRUD actions.
        { typeof(SwitchesController), nameof(SwitchesController.CreateSwitch), SwitchRoles },
        { typeof(SwitchesController), nameof(SwitchesController.UpdateSwitch), SwitchRoles },
        { typeof(SwitchesController), nameof(SwitchesController.DeleteSwitch), SwitchRoles },
        { typeof(SwitchesController), nameof(SwitchesController.CreateSwitchData), SwitchRoles },
        { typeof(SwitchesController), nameof(SwitchesController.CreateSwitchDataByName), SwitchRoles },

        // MqttController admin-only writes.
        { typeof(MqttController), nameof(MqttController.CreateUser), MqttRoles },
        { typeof(MqttController), nameof(MqttController.CreateAcl), MqttRoles },
        { typeof(MqttController), nameof(MqttController.PostDiscoveredDevice), MqttRoles },
    };

    [Theory]
    [MemberData(nameof(GatedActions))]
    public void GatedAction_CarriesAuthorizeAttribute_WithExpectedRoles(Type controller, string action, string expectedRoles)
    {
        var method = controller.GetMethod(action, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        var authorize = method!.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();
        Assert.True(
            authorize.Count > 0,
            $"{controller.Name}.{action} must carry [Authorize] gating it to '{expectedRoles}'.");

        Assert.Contains(
            authorize,
            a => a.Roles == expectedRoles);
    }

    [Fact]
    public void RoleNameConstants_AreTheCanonicalSeededStrings()
    {
        // The Roles strings above are only a meaningful gate if the constants hold the exact names that
        // Identity seeds and JWTs carry. Pin them so a rename here can't silently widen access.
        Assert.Equal("Admin", RoleNames.Admin);
        Assert.Equal("SensorAdmin", RoleNames.SensorAdmin);
        Assert.Equal("SwitchAdmin", RoleNames.SwitchAdmin);
        Assert.Equal("MqttAdmin", RoleNames.MqttAdmin);
    }
}
