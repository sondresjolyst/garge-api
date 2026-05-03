using garge_api.Controllers;
using garge_api.Dtos.Push;
using garge_api.Models;
using garge_api.Models.Push;
using garge_api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace garge_api.Tests;

public class PushSubscriptionControllerTests : ControllerTestBase
{
    private static PushSubscriptionController CreateController(
        ApplicationDbContext db,
        string? vapidPublicKey = "test-vapid-public-key",
        string userId = "u1")
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vapid:PublicKey"] = vapidPublicKey
            })
            .Build();

        var push = new Mock<IWebPushService>();
        var logger = new Mock<ILogger<PushSubscriptionController>>();

        var controller = new PushSubscriptionController(db, config, push.Object, logger.Object);
        controller.ControllerContext = MakeControllerContext(userId);
        return controller;
    }

    [Fact]
    public void GetVapidPublicKey_NotConfigured_Returns503()
    {
        var db = CreateDbContext();
        var controller = CreateController(db, vapidPublicKey: null);

        var result = controller.GetVapidPublicKey();

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, status.StatusCode);
    }

    [Fact]
    public void GetVapidPublicKey_Configured_ReturnsKey()
    {
        var db = CreateDbContext();
        var controller = CreateController(db, vapidPublicKey: "my-public-key");

        var result = controller.GetVapidPublicKey();

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = ok.Value!.GetType().GetProperty("publicKey")?.GetValue(ok.Value);
        Assert.Equal("my-public-key", body);
    }

    [Fact]
    public async Task Subscribe_NewSubscription_CreatesRecord()
    {
        var db = CreateDbContext();
        var controller = CreateController(db);
        var dto = new CreatePushSubscriptionDto
        {
            Endpoint = "https://push.example.com/sub/1",
            P256dh = "dh-key",
            Auth = "auth-secret"
        };

        var result = await controller.Subscribe(dto);

        Assert.IsType<OkResult>(result);
        Assert.Single(db.PushSubscriptions);
        var sub = db.PushSubscriptions.First();
        Assert.Equal("u1", sub.UserId);
        Assert.Equal(dto.Endpoint, sub.Endpoint);
        Assert.Equal(dto.P256dh, sub.P256dh);
        Assert.Equal(dto.Auth, sub.Auth);
    }

    [Fact]
    public async Task Subscribe_ExistingSubscription_UpdatesKeysWithoutDuplicate()
    {
        var db = CreateDbContext();
        db.PushSubscriptions.Add(new PushSubscription
        {
            UserId = "u1",
            Endpoint = "https://push.example.com/sub/1",
            P256dh = "old-dh",
            Auth = "old-auth"
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var dto = new CreatePushSubscriptionDto
        {
            Endpoint = "https://push.example.com/sub/1",
            P256dh = "new-dh",
            Auth = "new-auth"
        };

        var result = await controller.Subscribe(dto);

        Assert.IsType<OkResult>(result);
        Assert.Single(db.PushSubscriptions);
        var sub = db.PushSubscriptions.First();
        Assert.Equal("new-dh", sub.P256dh);
        Assert.Equal("new-auth", sub.Auth);
    }

    [Fact]
    public async Task Unsubscribe_ExistingEndpoint_RemovesRecord()
    {
        var db = CreateDbContext();
        db.PushSubscriptions.Add(new PushSubscription
        {
            UserId = "u1",
            Endpoint = "https://push.example.com/sub/1",
            P256dh = "dh",
            Auth = "auth"
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var dto = new DeletePushSubscriptionDto { Endpoint = "https://push.example.com/sub/1" };

        var result = await controller.Unsubscribe(dto);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(db.PushSubscriptions);
    }

    [Fact]
    public async Task Unsubscribe_NonExistentEndpoint_ReturnsNoContent()
    {
        var db = CreateDbContext();
        var controller = CreateController(db);
        var dto = new DeletePushSubscriptionDto { Endpoint = "https://push.example.com/not-found" };

        var result = await controller.Unsubscribe(dto);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task Subscribe_OtherUsersSubscription_CreatesSeperateRecord()
    {
        var db = CreateDbContext();
        db.PushSubscriptions.Add(new PushSubscription
        {
            UserId = "other-user",
            Endpoint = "https://push.example.com/sub/1",
            P256dh = "dh",
            Auth = "auth"
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId: "u1");
        var dto = new CreatePushSubscriptionDto
        {
            Endpoint = "https://push.example.com/sub/1",
            P256dh = "new-dh",
            Auth = "new-auth"
        };

        await controller.Subscribe(dto);

        Assert.Equal(2, db.PushSubscriptions.Count());
    }
}
