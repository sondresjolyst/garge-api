using garge_api.Hubs;
using garge_api.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace garge_api.Tests;

public class CoalescingDispatcherTests
{
    private static (CoalescingDispatcher dispatcher, Mock<IHubContext<DeviceHub>> hub, Mock<IClientProxy> proxy, Mock<IHubClients> clients) Build()
    {
        var proxy = new Mock<IClientProxy>();
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        var hub = new Mock<IHubContext<DeviceHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);

        var dispatcher = new CoalescingDispatcher(hub.Object, NullLogger<CoalescingDispatcher>.Instance);
        return (dispatcher, hub, proxy, clients);
    }

    private static SwitchEventDto Sw(int id, string value) =>
        new(1, id, value, DateTime.UtcNow, new SwitchSummaryDto(id, $"sw-{id}", "SOCKET"));

    private static SensorEventDto Sn(int id, string value) =>
        new(1, id, value, DateTime.UtcNow, new SensorSummaryDto(id, $"sn-{id}", "voltage"));

    private static async Task RunDrainAsync(CoalescingDispatcher dispatcher, TimeSpan duration)
    {
        var cts = new CancellationTokenSource(duration);
        try
        {
            await dispatcher.StartAsync(cts.Token);
            await Task.Delay(duration, cts.Token);
        }
        catch (OperationCanceledException) { }
        await dispatcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Coalesces_Multiple_Switch_Events_For_Same_Id_To_One_Send()
    {
        var (dispatcher, _, proxy, _) = Build();

        for (int i = 0; i < 5; i++)
        {
            dispatcher.EnqueueSwitchForUser("u1", Sw(42, i % 2 == 0 ? "ON" : "OFF"));
        }

        await RunDrainAsync(dispatcher, TimeSpan.FromMilliseconds(300));

        proxy.Verify(p => p.SendCoreAsync(
            "switch",
            It.IsAny<object[]>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Different_Entities_For_Same_User_Each_Send_Once()
    {
        var (dispatcher, _, proxy, _) = Build();

        dispatcher.EnqueueSwitchForUser("u1", Sw(1, "ON"));
        dispatcher.EnqueueSwitchForUser("u1", Sw(2, "ON"));
        dispatcher.EnqueueSensorForUser("u1", Sn(3, "21.5"));

        await RunDrainAsync(dispatcher, TimeSpan.FromMilliseconds(300));

        proxy.Verify(p => p.SendCoreAsync("switch", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        proxy.Verify(p => p.SendCoreAsync("sensor", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Bridge_Group_Is_Targeted_Independently()
    {
        var (dispatcher, _, _, clients) = Build();

        dispatcher.EnqueueSwitchForBridges(Sw(1, "ON"));

        dispatcher.EnqueueSwitchForUser("u1", Sw(1, "ON"));

        await RunDrainAsync(dispatcher, TimeSpan.FromMilliseconds(300));

        clients.Verify(c => c.Group(DeviceHub.BridgeGroup), Times.AtLeastOnce);
        clients.Verify(c => c.Group(DeviceHub.UserGroup("u1")), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Failure_On_One_Group_Does_Not_Block_Others()
    {
        var failingProxy = new Mock<IClientProxy>();
        failingProxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated failure"));

        var goodProxy = new Mock<IClientProxy>();

        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(DeviceHub.UserGroup("u1"))).Returns(failingProxy.Object);
        clients.Setup(c => c.Group(DeviceHub.UserGroup("u2"))).Returns(goodProxy.Object);

        var hub = new Mock<IHubContext<DeviceHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);

        var dispatcher = new CoalescingDispatcher(hub.Object, NullLogger<CoalescingDispatcher>.Instance);

        dispatcher.EnqueueSwitchForUser("u1", Sw(1, "ON"));
        dispatcher.EnqueueSwitchForUser("u2", Sw(1, "ON"));

        await RunDrainAsync(dispatcher, TimeSpan.FromMilliseconds(300));

        goodProxy.Verify(p => p.SendCoreAsync("switch", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
