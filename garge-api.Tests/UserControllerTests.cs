using MapsterMapper;
using garge_api.Controllers;
using garge_api.Dtos.User;
using garge_api.Hubs;
using garge_api.Models;
using garge_api.Models.Auth;
using garge_api.Models.Push;
using garge_api.Models.Shop;
using garge_api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace garge_api.Tests;

public class UserControllerTests : ControllerTestBase
{
    private UserController CreateUserController(ApplicationDbContext db, string callerId = "user-1")
    {
        var ownership = new Mock<IDeviceOwnershipService>().Object;
        var tracker = new Mock<IHubConnectionTracker>();
        tracker.Setup(t => t.GetConnectionIds(It.IsAny<string>())).Returns(Array.Empty<string>());

        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Clients(It.IsAny<IReadOnlyList<string>>())).Returns(Mock.Of<IClientProxy>());
        var hub = new Mock<IHubContext<DeviceHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);

        var controller = new UserController(
            db, MockUserManager.Object, MockMapper.Object,
            NullLogger<UserController>.Instance,
            ownership, tracker.Object, hub.Object);
        controller.ControllerContext = MakeControllerContext(callerId);
        return controller;
    }

    private static User MakeDbUser(string id = "user-1") => new()
    {
        Id = id, UserName = "userone", Email = "user@test.com",
        FirstName = "First", LastName = "Last", PhoneNumber = "47900000000",
        EmailConfirmed = true
    };

    [Fact]
    public async Task DeleteOwnAccount_SoftDeletes_ScrubsPiiAndKeepsOrders()
    {
        using var db = CreateDbContext();
        var user = MakeDbUser();
        MockUserManager.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        MockUserManager.Setup(m => m.UpdateSecurityStampAsync(user)).ReturnsAsync(IdentityResult.Success);
        MockUserManager.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);

        db.Orders.Add(new Order
        {
            Id = 1, UserId = user.Id, VippsOrderId = "garge-order-000001",
            TotalInOre = 9900, Status = OrderStatus.Paid
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateUserController(db, callerId: user.Id).DeleteOwnAccount(user.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.True(user.IsDeleted);
        Assert.NotNull(user.DeletedAt);
        Assert.Equal("Deleted", user.FirstName);
        Assert.Equal("User", user.LastName);
        Assert.Null(user.Email);
        Assert.Null(user.NormalizedEmail);
        Assert.Null(user.PhoneNumber);
        Assert.False(user.EmailConfirmed);
        Assert.Equal(DateTimeOffset.MaxValue, user.LockoutEnd);

        Assert.Single(db.Orders);
        Assert.Equal(OrderStatus.Paid, db.Orders.First().Status);
    }

    [Fact]
    public async Task DeleteOwnAccount_RemovesUserOwnedTables()
    {
        using var db = CreateDbContext();
        var user = MakeDbUser();
        MockUserManager.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        MockUserManager.Setup(m => m.UpdateSecurityStampAsync(user)).ReturnsAsync(IdentityResult.Success);
        MockUserManager.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);

        db.RefreshTokens.Add(new RefreshToken
        {
            Token = "t", UserId = user.Id,
            Expires = DateTime.UtcNow.AddMonths(1), Created = DateTime.UtcNow
        });
        db.PushSubscriptions.Add(new PushSubscription
        {
            Id = 1, UserId = user.Id, Endpoint = "https://push", P256dh = "k", Auth = "a"
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await CreateUserController(db, callerId: user.Id).DeleteOwnAccount(user.Id);

        Assert.Empty(db.RefreshTokens);
        Assert.Empty(db.PushSubscriptions);
    }

    [Fact]
    public async Task DeleteOwnAccount_AlreadyDeleted_Returns404()
    {
        using var db = CreateDbContext();
        var user = MakeDbUser();
        user.IsDeleted = true;
        MockUserManager.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);

        var result = await CreateUserController(db, callerId: user.Id).DeleteOwnAccount(user.Id);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task DeleteOwnAccount_OtherUser_ReturnsForbidden()
    {
        using var db = CreateDbContext();
        var result = await CreateUserController(db, callerId: "user-1").DeleteOwnAccount("user-2");
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task UpdateProfile_OwnerSucceeds_PersistsChanges()
    {
        using var db = CreateDbContext();
        var user = MakeDbUser();
        MockUserManager.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        MockUserManager.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);

        var dto = new UpdateProfileDto
        {
            FirstName = "Sondre", LastName = "Sjølyst", PhoneNumber = "47900112233"
        };

        var result = await CreateUserController(db, callerId: user.Id).UpdateProfile(user.Id, dto);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal("Sondre", user.FirstName);
        Assert.Equal("Sjølyst", user.LastName);
        Assert.Equal("47900112233", user.PhoneNumber);
    }

    [Fact]
    public async Task UpdateProfile_OtherUser_ReturnsForbidden()
    {
        using var db = CreateDbContext();
        var dto = new UpdateProfileDto { FirstName = "X", LastName = "Y" };
        var result = await CreateUserController(db, callerId: "user-1").UpdateProfile("user-2", dto);
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task UpdateProfile_DeletedUser_Returns404()
    {
        using var db = CreateDbContext();
        var user = MakeDbUser();
        user.IsDeleted = true;
        MockUserManager.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);

        var dto = new UpdateProfileDto { FirstName = "X", LastName = "Y" };
        var result = await CreateUserController(db, callerId: user.Id).UpdateProfile(user.Id, dto);

        Assert.IsType<NotFoundObjectResult>(result);
    }
}
