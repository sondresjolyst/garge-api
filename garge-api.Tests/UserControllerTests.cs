using MapsterMapper;
using garge_api.Controllers;
using garge_api.Dtos.User;
using garge_api.Hubs;
using garge_api.Models;
using SjolystInnovation.AppKit.Auth;
using garge_api.Models.Push;
using garge_api.Models.Sensor;
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
        var controller = new UserController(db, MockUserManager.Object, MockMapper.Object,
            NullLogger<UserController>.Instance, new PermissionService(db));
        controller.ControllerContext = MakeControllerContext(callerId);
        return controller;
    }

    private static User MakeDbUser(string id = "user-1") => new()
    {
        Id = id, UserName = "userone", Email = "user@test.com",
        FirstName = "First", LastName = "Last", PhoneNumber = "47900000000",
        EmailConfirmed = true, TermsAcceptedIp = "203.0.113.7", PasswordHash = "hashed-secret"
    };

    [Fact]
    public async Task DeleteOwnAccount_DeletesThroughTheSharedDeleter()
    {
        using var db = CreateDbContext();
        var user = MakeDbUser();
        MockUserManager.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        var deleter = new Mock<IAccountDeleter<User>>();

        var result = await CreateUserController(db, callerId: user.Id).DeleteOwnAccount(user.Id, deleter.Object);

        Assert.IsType<NoContentResult>(result);
        deleter.Verify(d => d.DeleteAsync(user, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteOwnAccount_LastAdmin_ReturnsConflict()
    {
        using var db = CreateDbContext();
        var user = MakeDbUser();
        MockUserManager.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        var deleter = new Mock<IAccountDeleter<User>>();
        deleter.Setup(d => d.DeleteAsync(user, It.IsAny<CancellationToken>())).ThrowsAsync(new LastAdminException());

        var result = await CreateUserController(db, callerId: user.Id).DeleteOwnAccount(user.Id, deleter.Object);

        Assert.Equal(409, Assert.IsType<ConflictObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task DeleteOwnAccount_AlreadyDeleted_Returns404()
    {
        using var db = CreateDbContext();
        var user = MakeDbUser();
        user.IsDeleted = true;
        MockUserManager.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        var deleter = new Mock<IAccountDeleter<User>>();

        var result = await CreateUserController(db, callerId: user.Id).DeleteOwnAccount(user.Id, deleter.Object);

        Assert.IsType<NotFoundObjectResult>(result);
        deleter.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DeleteOwnAccount_OtherUser_ReturnsForbidden()
    {
        using var db = CreateDbContext();
        var deleter = new Mock<IAccountDeleter<User>>();

        var result = await CreateUserController(db, callerId: "user-1").DeleteOwnAccount("user-2", deleter.Object);

        Assert.IsType<ForbidResult>(result);
        deleter.VerifyNoOtherCalls();
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

    [Fact]
    public async Task GetDataRetention_ReflectsOptOutState()
    {
        using var db = CreateDbContext();
        var user = MakeDbUser();
        user.DataRetentionOptOutAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        MockUserManager.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);

        var result = await CreateUserController(db, callerId: user.Id).GetDataRetention(user.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<DataRetentionDto>(ok.Value);
        Assert.True(dto.OptOut);
        Assert.Equal(user.DataRetentionOptOutAt, dto.OptedOutAt);
    }

    [Fact]
    public async Task UpdateDataRetention_OptOut_StampsTimestamp()
    {
        using var db = CreateDbContext();
        var user = MakeDbUser();
        MockUserManager.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        MockUserManager.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);

        var result = await CreateUserController(db, callerId: user.Id)
            .UpdateDataRetention(user.Id, new UpdateDataRetentionDto { OptOut = true });

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<DataRetentionDto>(ok.Value);
        Assert.True(dto.OptOut);
        Assert.NotNull(user.DataRetentionOptOutAt);
    }

    [Fact]
    public async Task UpdateDataRetention_OptIn_ClearsTimestamp()
    {
        using var db = CreateDbContext();
        var user = MakeDbUser();
        user.DataRetentionOptOutAt = DateTime.UtcNow;
        MockUserManager.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        MockUserManager.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);

        var result = await CreateUserController(db, callerId: user.Id)
            .UpdateDataRetention(user.Id, new UpdateDataRetentionDto { OptOut = false });

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<DataRetentionDto>(ok.Value);
        Assert.False(dto.OptOut);
        Assert.Null(user.DataRetentionOptOutAt);
    }

    [Fact]
    public async Task UpdateDataRetention_OtherUser_ReturnsForbidden()
    {
        using var db = CreateDbContext();
        var result = await CreateUserController(db, callerId: "user-1")
            .UpdateDataRetention("user-2", new UpdateDataRetentionDto { OptOut = true });
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task UpdatePreferences_TurningOffLastAlertChannelWhileSecurityIsOn_Returns409()
    {
        var db = CreateDbContext();
        var user = MakeDbUser();
        db.Users.Add(user);
        db.UserProfiles.Add(new UserProfile { Id = user.Id, User = user, PushNotificationsEnabled = false, EmailNotificationsEnabled = true });
        db.UserSensorSecurities.Add(new UserSensorSecurity { UserId = user.Id, SensorId = 1, Enabled = true });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateUserController(db).UpdatePreferences(user.Id,
            new UpdateUserPreferencesDto { PriceZone = "NO2", EmailNotificationsEnabled = false });

        Assert.IsType<ConflictObjectResult>(result);
        Assert.True(db.UserProfiles.Single().EmailNotificationsEnabled);
    }

    [Fact]
    public async Task UpdatePreferences_TurningOffBothChannelsWithoutSecurity_IsAllowed()
    {
        var db = CreateDbContext();
        var user = MakeDbUser();
        db.Users.Add(user);
        db.UserProfiles.Add(new UserProfile { Id = user.Id, User = user, PushNotificationsEnabled = false, EmailNotificationsEnabled = true });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        MockMapper.Setup(m => m.Map<UserProfileDto>(It.IsAny<object>())).Returns(new UserProfileDto { Id = user.Id });

        var result = await CreateUserController(db).UpdatePreferences(user.Id,
            new UpdateUserPreferencesDto { PriceZone = "NO2", EmailNotificationsEnabled = false });

        Assert.IsType<OkObjectResult>(result);
        Assert.False(db.UserProfiles.Single().EmailNotificationsEnabled);
    }
}
