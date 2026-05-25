using garge_api.Helpers;
using garge_api.Models;
using garge_api.Models.Sensor;
using garge_api.Models.Switch;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// Exercises the generic sharing helper that backs both SensorController and SwitchesController so the
/// shared upsert and recipient-listing logic is verified independently of either controller. Covers
/// both entity families (UserSensor/SensorOwnershipPeriod and UserSwitch/SwitchOwnershipPeriod).
/// </summary>
public class DeviceShareHelperTests : ControllerTestBase
{
    private static User MakeUser(string id, string email) => new()
    {
        Id = id, UserName = id, Email = email, NormalizedEmail = email.ToUpperInvariant(),
        FirstName = "F", LastName = "L",
    };

    [Fact]
    public async Task UpsertShareAsync_NewRecipient_AddsMembershipAndPeriod()
    {
        using var db = CreateDbContext();

        var result = await DeviceShareHelper.UpsertShareAsync(
            db.UserSensors, db.SensorOwnershipPeriods,
            SharePermission.Edit,
            matchesTarget: us => us.UserId == "viewer" && us.SensorId == 1,
            isOwner: us => us.IsOwner,
            setPermission: (us, p) => us.Permission = p,
            newMembership: () => new UserSensor { UserId = "viewer", SensorId = 1, IsOwner = false, Permission = SharePermission.Edit },
            newPeriod: () => new SensorOwnershipPeriod { UserId = "viewer", SensorId = 1, StartedAt = DateTime.UtcNow, EndedAt = null });
        await db.SaveChangesAsync();

        Assert.Equal(ShareUpsertResult.Added, result);
        var row = Assert.Single(db.UserSensors);
        Assert.False(row.IsOwner);
        Assert.Equal(SharePermission.Edit, row.Permission);
        Assert.Single(db.SensorOwnershipPeriods);
    }

    [Fact]
    public async Task UpsertShareAsync_ExistingViewer_UpdatesPermission_NoNewPeriod()
    {
        using var db = CreateDbContext();
        db.UserSensors.Add(new UserSensor { UserId = "viewer", SensorId = 1, IsOwner = false, Permission = SharePermission.Read });
        db.SensorOwnershipPeriods.Add(new SensorOwnershipPeriod { UserId = "viewer", SensorId = 1, StartedAt = DateTime.UtcNow, EndedAt = null });
        await db.SaveChangesAsync();

        var result = await DeviceShareHelper.UpsertShareAsync(
            db.UserSensors, db.SensorOwnershipPeriods,
            SharePermission.Edit,
            matchesTarget: us => us.UserId == "viewer" && us.SensorId == 1,
            isOwner: us => us.IsOwner,
            setPermission: (us, p) => us.Permission = p,
            newMembership: () => new UserSensor { UserId = "viewer", SensorId = 1, IsOwner = false, Permission = SharePermission.Edit },
            newPeriod: () => new SensorOwnershipPeriod { UserId = "viewer", SensorId = 1, StartedAt = DateTime.UtcNow, EndedAt = null });
        await db.SaveChangesAsync();

        Assert.Equal(ShareUpsertResult.PermissionUpdated, result);
        Assert.Single(db.UserSensors);
        Assert.Equal(SharePermission.Edit, db.UserSensors.Single().Permission);
        Assert.Single(db.SensorOwnershipPeriods); // no duplicate period
    }

    [Fact]
    public async Task UpsertShareAsync_TargetIsOwner_ReturnsAlreadyOwner_NoChange()
    {
        using var db = CreateDbContext();
        db.UserSwitches.Add(new UserSwitch { UserId = "owner", SwitchId = 1, IsOwner = true });
        await db.SaveChangesAsync();

        var result = await DeviceShareHelper.UpsertShareAsync(
            db.UserSwitches, db.SwitchOwnershipPeriods,
            SharePermission.Edit,
            matchesTarget: us => us.UserId == "owner" && us.SwitchId == 1,
            isOwner: us => us.IsOwner,
            setPermission: (us, p) => us.Permission = p,
            newMembership: () => new UserSwitch { UserId = "owner", SwitchId = 1, IsOwner = false, Permission = SharePermission.Edit },
            newPeriod: () => new SwitchOwnershipPeriod { UserId = "owner", SwitchId = 1, StartedAt = DateTime.UtcNow, EndedAt = null });
        await db.SaveChangesAsync();

        Assert.Equal(ShareUpsertResult.AlreadyOwner, result);
        Assert.True(db.UserSwitches.Single().IsOwner);
        Assert.Empty(db.SwitchOwnershipPeriods);
    }

    [Fact]
    public async Task ListRecipientsAsync_ReturnsOnlyViewers_WithUserFields()
    {
        using var db = CreateDbContext();
        db.Users.AddRange(MakeUser("owner", "owner@x"), MakeUser("viewer", "viewer@x"));
        db.UserSwitches.AddRange(
            new UserSwitch { UserId = "owner", SwitchId = 1, IsOwner = true },
            new UserSwitch { UserId = "viewer", SwitchId = 1, IsOwner = false, Permission = SharePermission.Edit });
        await db.SaveChangesAsync();

        var recipients = await DeviceShareHelper.ListRecipientsAsync(
            db.UserSwitches.Where(us => us.SwitchId == 1 && !us.IsOwner),
            db.Users,
            userIdOf: us => us.UserId,
            permissionOf: us => us.Permission,
            sharedAtOf: us => us.CreatedAt);

        var r = Assert.Single(recipients);
        Assert.Equal("viewer", r.UserId);
        Assert.Equal("viewer@x", r.Email);
        Assert.Equal(SharePermission.Edit, r.Permission);
    }
}
