using garge_api.Controllers;
using garge_api.Dtos.Sensor;
using garge_api.Hubs;
using garge_api.Models;
using garge_api.Services;
using MapsterMapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace garge_api.Tests;

public class SensorControllerCreateTests : ControllerTestBase
{
    private SensorController CreateController(ApplicationDbContext db)
    {
        var ownership = new Mock<IDeviceOwnershipService>().Object;
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(Mock.Of<IClientProxy>());
        var hub = new Mock<IHubContext<DeviceHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);

        var capacity = new SubscriptionCapacityService(db, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));

        var controller = new SensorController(
            db, MockMapper.Object,
            NullLogger<SensorController>.Instance,
            ownership, hub.Object, capacity);
        controller.ControllerContext = MakeControllerContext("admin-1", isAdmin: true);
        return controller;
    }

    [Theory]
    [InlineData("battery")]
    [InlineData("battery_health")]
    [InlineData("pressure")]
    [InlineData("")]
    public async Task CreateSensor_UnsupportedType_Returns400(string type)
    {
        using var db = CreateDbContext();
        var controller = CreateController(db);

        var result = await controller.CreateSensor(new CreateSensorDto
        {
            Name = $"garge_test_{type}",
            Type = type,
            ParentName = "garge_test",
        });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(db.Sensors);
    }

    [Theory]
    [InlineData("voltage")]
    [InlineData("temperature")]
    [InlineData("humidity")]
    public async Task CreateSensor_AllowedType_CreatesRow(string type)
    {
        using var db = CreateDbContext();
        var controller = CreateController(db);

        var result = await controller.CreateSensor(new CreateSensorDto
        {
            Name = $"garge_test_{type}",
            Type = type,
            ParentName = "garge_test",
        });

        Assert.IsNotType<BadRequestObjectResult>(result);
        Assert.Single(db.Sensors);
        Assert.Equal(type, db.Sensors.Single().Type);
    }
}
