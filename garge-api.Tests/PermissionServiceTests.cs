using garge_api.Constants;
using garge_api.Models.Admin;
using garge_api.Services;
using Xunit;

namespace garge_api.Tests;

public class PermissionServiceTests : ControllerTestBase
{
    private static bool OldElectricityCheck(IEnumerable<string> roles) =>
        roles.Contains(RoleNames.Admin, StringComparer.OrdinalIgnoreCase) ||
        roles.Any(role => RoleNames.RolePermissions.TryGetValue(role, out var permissions) &&
                          permissions.Contains("Electricity", StringComparer.OrdinalIgnoreCase));

    [Fact]
    public async Task GargeSecurityRole_GrantsGargeSecurity()
    {
        var db = CreateDbContext();
        GrantRoles(db, "u1", "Default", RoleNames.GargeSecurity);

        var sut = new PermissionService(db);

        Assert.True(await sut.HasPermissionAsync("u1", PermissionNames.GargeSecurity, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DefaultRoleAlone_DoesNotGrantGargeSecurity()
    {
        var db = CreateDbContext();
        GrantRoles(db, "u1", "Default");

        var sut = new PermissionService(db);

        Assert.False(await sut.HasPermissionAsync("u1", PermissionNames.GargeSecurity, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ComplimentaryUserAlone_DoesNotGrantGargeSecurity()
    {
        var db = CreateDbContext();
        GrantRoles(db, "u1", "ComplimentaryUser");

        var sut = new PermissionService(db);

        Assert.False(await sut.HasPermissionAsync("u1", PermissionNames.GargeSecurity, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Admin_HasEveryKnownPermission()
    {
        var db = CreateDbContext();
        GrantRoles(db, "u1", RoleNames.Admin);

        var sut = new PermissionService(db);
        var ct = TestContext.Current.CancellationToken;

        Assert.True(await sut.HasPermissionAsync("u1", PermissionNames.GargeSecurity, ct));
        Assert.True(await sut.HasPermissionAsync("u1", PermissionNames.Electricity, ct));
    }

    [Fact]
    public async Task DatabaseRolePermissionRow_GrantsPermission()
    {
        var db = CreateDbContext();
        GrantRoles(db, "u1", "SensorAdmin");
        db.RolePermissions.Add(new RolePermission { RoleName = "sensoradmin", Permission = PermissionNames.GargeSecurity });
        db.SaveChanges();

        var sut = new PermissionService(db);

        Assert.True(await sut.HasPermissionAsync("u1", PermissionNames.GargeSecurity, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RoleGrantedAfterEarlierCheck_AppliesImmediately()
    {
        var db = CreateDbContext();
        GrantRoles(db, "u1", "Default");
        var sut = new PermissionService(db);
        var ct = TestContext.Current.CancellationToken;
        Assert.False(await sut.HasPermissionAsync("u1", PermissionNames.GargeSecurity, ct));

        GrantRoles(db, "u1", RoleNames.GargeSecurity);

        Assert.True(await sut.HasPermissionAsync("u1", PermissionNames.GargeSecurity, ct));
    }

    [Fact]
    public async Task UserWithoutRoles_HasNoPermissions()
    {
        var db = CreateDbContext();
        var sut = new PermissionService(db);

        Assert.Empty(await sut.GetPermissionsAsync("nobody", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetPermissions_ListsEachGrantOnce()
    {
        var db = CreateDbContext();
        GrantRoles(db, "u1", "Default", RoleNames.GargeSecurity);

        var sut = new PermissionService(db);

        Assert.Equal(
            [PermissionNames.Electricity, PermissionNames.GargeSecurity],
            await sut.GetPermissionsAsync("u1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetUsersWithPermission_ReturnsOnlyEntitledUsers()
    {
        var db = CreateDbContext();
        GrantRoles(db, "u1", RoleNames.GargeSecurity);
        GrantRoles(db, "u2", "Default");
        GrantRoles(db, "u3", RoleNames.Admin);

        var sut = new PermissionService(db);

        var entitled = await sut.GetUsersWithPermissionAsync(["u1", "u2", "u3", "u4"], PermissionNames.GargeSecurity, TestContext.Current.CancellationToken);

        Assert.Equal(new HashSet<string> { "u1", "u3" }, entitled);
    }

    public static TheoryData<string[]> ElectricityRoleCombinations() => new()
    {
        Array.Empty<string>(),
        new[] { "Default" },
        new[] { "Electricity" },
        new[] { "Admin" },
        new[] { "SensorAdmin" },
        new[] { "ComplimentaryUser" },
        new[] { "DeviceBridge" },
        new[] { RoleNames.GargeSecurity },
        new[] { "Default", "ComplimentaryUser" },
        new[] { "Electricity", "SensorAdmin", "MqttAdmin", "AutomationAdmin", "SwitchAdmin" },
    };

    [Theory]
    [MemberData(nameof(ElectricityRoleCombinations))]
    public async Task Electricity_MatchesPreviousInlineCheck(string[] roles)
    {
        var db = CreateDbContext();
        GrantRoles(db, "u1", roles);

        var sut = new PermissionService(db);

        Assert.Equal(OldElectricityCheck(roles), await sut.HasPermissionAsync("u1", PermissionNames.Electricity, TestContext.Current.CancellationToken));
    }
}
