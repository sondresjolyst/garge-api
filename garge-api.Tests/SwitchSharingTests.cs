using garge_api.Controllers;
using garge_api.Dtos.Common;
using garge_api.Hubs;
using garge_api.Models;
using garge_api.Models.Switch;
using garge_api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// Verifies switch sharing: an owner can share Read/Edit by email; the recipient gets a viewer row +
/// ownership period from share time; revoke, list, owner-cascade and viewer-leave behave; and a Read
/// viewer cannot perform an Edit-gated mutation (deleting telemetry).
/// </summary>
public class SwitchSharingTests : ControllerTestBase
{
    private SwitchesController CreateController(ApplicationDbContext db, string userId, bool isAdmin = false)
    {
        var ownership = new Mock<IDeviceOwnershipService>();
        ownership.Setup(o => o.CanUserAccessSwitchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        var hub = new Mock<IHubContext<DeviceHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);

        var controller = new SwitchesController(db, MockMapper.Object, NullLogger<SwitchesController>.Instance, ownership.Object, hub.Object);
        controller.ControllerContext = MakeControllerContext(userId, isAdmin);
        return controller;
    }

    private static Switch MakeSwitch(int id = 1) => new()
    {
        Id = id, Name = $"socket-{id}", Type = "socket", Role = "switch",
    };

    private static User MakeUser(string id, string email) => new()
    {
        Id = id, UserName = id, Email = email, NormalizedEmail = email.ToUpperInvariant(),
        FirstName = "F", LastName = "L",
    };

    private static async Task SeedOwnedSwitchAsync(ApplicationDbContext db, string ownerId, int switchId = 1)
    {
        db.Switches.Add(MakeSwitch(switchId));
        db.UserSwitches.Add(new UserSwitch { UserId = ownerId, SwitchId = switchId, IsOwner = true });
        await db.SaveChangesAsync();
    }

    private static ShareRequestDto Share(string email, SharePermission p = SharePermission.Read) =>
        new() { Email = email, Permission = p };

    [Fact]
    public async Task ShareSwitch_CreatesViewerRowAndOwnershipPeriod()
    {
        using var db = CreateDbContext();
        db.Users.AddRange(MakeUser("owner", "owner@x"), MakeUser("viewer", "viewer@x"));
        await SeedOwnedSwitchAsync(db, "owner");

        var result = await CreateController(db, "owner").ShareSwitch(1, Share("viewer@x", SharePermission.Edit));

        Assert.IsType<OkObjectResult>(result);
        var row = db.UserSwitches.Single(us => us.UserId == "viewer" && us.SwitchId == 1);
        Assert.False(row.IsOwner);
        Assert.Equal(SharePermission.Edit, row.Permission);
        var period = db.SwitchOwnershipPeriods.Single(p => p.UserId == "viewer" && p.SwitchId == 1);
        Assert.Null(period.EndedAt);
        Assert.NotEqual(SwitchOwnershipPeriod.FirstOwnerStart, period.StartedAt);
    }

    [Fact]
    public async Task ShareSwitch_NotOwner_Forbid()
    {
        using var db = CreateDbContext();
        db.Users.AddRange(MakeUser("owner", "owner@x"), MakeUser("stranger", "s@x"), MakeUser("viewer", "viewer@x"));
        await SeedOwnedSwitchAsync(db, "owner");

        var result = await CreateController(db, "stranger").ShareSwitch(1, Share("viewer@x"));

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task ShareSwitch_UnknownEmail_NotFound()
    {
        using var db = CreateDbContext();
        db.Users.Add(MakeUser("owner", "owner@x"));
        await SeedOwnedSwitchAsync(db, "owner");

        var result = await CreateController(db, "owner").ShareSwitch(1, Share("ghost@x"));

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task RevokeSwitchShare_RemovesAndClosesPeriod()
    {
        using var db = CreateDbContext();
        db.Users.AddRange(MakeUser("owner", "owner@x"), MakeUser("viewer", "viewer@x"));
        await SeedOwnedSwitchAsync(db, "owner");
        await CreateController(db, "owner").ShareSwitch(1, Share("viewer@x"));

        var result = await CreateController(db, "owner").RevokeSwitchShare(1, "viewer");

        Assert.IsType<OkObjectResult>(result);
        Assert.DoesNotContain(db.UserSwitches, us => us.UserId == "viewer" && us.SwitchId == 1);
        Assert.NotNull(db.SwitchOwnershipPeriods.Single(p => p.UserId == "viewer" && p.SwitchId == 1).EndedAt);
    }

    [Fact]
    public async Task ListSwitchShares_ReturnsRecipients()
    {
        using var db = CreateDbContext();
        db.Users.AddRange(MakeUser("owner", "owner@x"), MakeUser("viewer", "viewer@x"));
        await SeedOwnedSwitchAsync(db, "owner");
        await CreateController(db, "owner").ShareSwitch(1, Share("viewer@x", SharePermission.Edit));

        var result = await CreateController(db, "owner").ListSwitchShares(1);

        var ok = Assert.IsType<OkObjectResult>(result);
        var shares = Assert.IsAssignableFrom<IEnumerable<ShareRecipientDto>>(ok.Value);
        var s = Assert.Single(shares);
        Assert.Equal("viewer", s.UserId);
        Assert.Equal(SharePermission.Edit, s.Permission);
    }

    [Fact]
    public async Task UnclaimSwitch_Owner_CascadesToViewers()
    {
        using var db = CreateDbContext();
        db.Users.AddRange(MakeUser("owner", "owner@x"), MakeUser("viewer", "viewer@x"));
        await SeedOwnedSwitchAsync(db, "owner");
        await CreateController(db, "owner").ShareSwitch(1, Share("viewer@x"));

        var result = await CreateController(db, "owner").UnclaimSwitch(1);

        Assert.IsType<OkObjectResult>(result);
        Assert.DoesNotContain(db.UserSwitches, us => us.SwitchId == 1);
        Assert.All(db.SwitchOwnershipPeriods.Where(p => p.SwitchId == 1), p => Assert.NotNull(p.EndedAt));
    }

    [Fact]
    public async Task UnclaimSwitch_Viewer_LeavesOnly_OwnerIntact()
    {
        using var db = CreateDbContext();
        db.Users.AddRange(MakeUser("owner", "owner@x"), MakeUser("viewer", "viewer@x"));
        await SeedOwnedSwitchAsync(db, "owner");
        await CreateController(db, "owner").ShareSwitch(1, Share("viewer@x"));

        var result = await CreateController(db, "viewer").UnclaimSwitch(1);

        Assert.IsType<OkObjectResult>(result);
        Assert.DoesNotContain(db.UserSwitches, us => us.UserId == "viewer" && us.SwitchId == 1);
        Assert.Contains(db.UserSwitches, us => us.UserId == "owner" && us.SwitchId == 1 && us.IsOwner);
    }

    [Fact]
    public async Task DeleteSwitchData_ReadViewer_Forbidden()
    {
        using var db = CreateDbContext();
        db.Users.AddRange(MakeUser("owner", "owner@x"), MakeUser("viewer", "viewer@x"));
        await SeedOwnedSwitchAsync(db, "owner");
        await CreateController(db, "owner").ShareSwitch(1, Share("viewer@x", SharePermission.Read));

        var result = await CreateController(db, "viewer").DeleteSwitchData(1, 999);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task DeleteSwitchData_EditViewer_PassesAuth()
    {
        // Edit share clears the auth gate; with no such data row it then 404s (proving auth passed).
        using var db = CreateDbContext();
        db.Users.AddRange(MakeUser("owner", "owner@x"), MakeUser("viewer", "viewer@x"));
        await SeedOwnedSwitchAsync(db, "owner");
        await CreateController(db, "owner").ShareSwitch(1, Share("viewer@x", SharePermission.Edit));

        var result = await CreateController(db, "viewer").DeleteSwitchData(1, 999);

        Assert.IsType<NotFoundObjectResult>(result);
    }
}
