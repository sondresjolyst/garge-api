using System.Security.Claims;
using garge_api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace garge_api.Tests;

public class DeviceHubTests
{
    private static (DeviceHub hub, Mock<IGroupManager> groups) BuildHub(ClaimsPrincipal user, string connectionId = "conn-1")
    {
        var groups = new Mock<IGroupManager>();
        var clients = new Mock<IHubCallerClients>();

        var ctx = new Mock<HubCallerContext>();
        ctx.SetupGet(c => c.ConnectionId).Returns(connectionId);
        ctx.SetupGet(c => c.User).Returns(user);

        var hub = new DeviceHub(NullLogger<DeviceHub>.Instance, new HubConnectionTracker())
        {
            Context = ctx.Object,
            Groups = groups.Object,
            Clients = clients.Object,
        };
        return (hub, groups);
    }

    private static ClaimsPrincipal MakeUser(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        foreach (var r in roles) claims.Add(new Claim(ClaimTypes.Role, r));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    [Fact]
    public async Task Regular_User_Joins_User_Group()
    {
        var (hub, groups) = BuildHub(MakeUser("u-42"));

        await hub.OnConnectedAsync();

        groups.Verify(g => g.AddToGroupAsync("conn-1", DeviceHub.UserGroup("u-42"), default), Times.Once);
        groups.Verify(g => g.AddToGroupAsync("conn-1", DeviceHub.BridgeGroup, default), Times.Never);
    }

    [Fact]
    public async Task DeviceBridge_Role_Joins_Bridge_Group()
    {
        var (hub, groups) = BuildHub(MakeUser("op-1", DeviceHub.BridgeRole));

        await hub.OnConnectedAsync();

        groups.Verify(g => g.AddToGroupAsync("conn-1", DeviceHub.BridgeGroup, default), Times.Once);
        groups.Verify(g => g.AddToGroupAsync("conn-1", DeviceHub.UserGroup("op-1"), default), Times.Never);
    }

    [Fact]
    public void UserGroup_Format_Is_Stable()
    {
        Assert.Equal("user-abc", DeviceHub.UserGroup("abc"));
    }
}
