using MapsterMapper;
using garge_api.Controllers;
using garge_api.Dtos.User;
using garge_api.Hubs;
using garge_api.Models;
using garge_api.Models.Auth;
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
        var ownership = new Mock<IDeviceOwnershipService>().Object;
        var tracker = new Mock<IHubConnectionTracker>();
        tracker.Setup(t => t.GetConnectionIds(It.IsAny<string>())).Returns(Array.Empty<string>());

        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Clients(It.IsAny<IReadOnlyList<string>>())).Returns(Mock.Of<IClientProxy>());
        var hub = new Mock<IHubContext<DeviceHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);

        var anonymizer = new AnonymizationService(db, NullLogger<AnonymizationService>.Instance);

        var controller = new UserController(
            db, MockUserManager.Object, MockMapper.Object,
            NullLogger<UserController>.Instance,
            ownership, tracker.Object, hub.Object, anonymizer);
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
        // Email must remain a valid, unique, non-PII value — Identity's RequireUniqueEmail
        // rejects null/empty (regression: account deletion used to 400 with InvalidEmail).
        Assert.Equal($"deleted-{user.Id}@deleted.invalid", user.Email);
        Assert.Equal(user.Email!.ToUpperInvariant(), user.NormalizedEmail);
        Assert.DoesNotContain("user@test.com", user.Email!);
        Assert.Null(user.PhoneNumber);
        // Login credential and the terms-acceptance IP are personal data with no basis to outlive
        // the account — both must be cleared so the soft-deleted row holds no recoverable PII.
        Assert.Null(user.PasswordHash);
        Assert.Null(user.TermsAcceptedIp);
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
    public async Task DeleteOwnAccount_MovesTelemetryToMlStore_AndLeavesNoOrphans()
    {
        using var db = CreateDbContext();
        var user = MakeDbUser();
        MockUserManager.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        MockUserManager.Setup(m => m.UpdateSecurityStampAsync(user)).ReturnsAsync(IdentityResult.Success);
        MockUserManager.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);

        db.Sensors.Add(new Sensor { Id = 1, Name = "garge_volt", Type = "voltage", Role = "sensor", RegistrationCode = "rc", DefaultName = "Battery", ParentName = "gw" });
        db.UserSensors.Add(new UserSensor { UserId = user.Id, SensorId = 1, IsOwner = true });
        db.SensorOwnershipPeriods.Add(new SensorOwnershipPeriod { UserId = user.Id, SensorId = 1, StartedAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), EndedAt = null });
        db.SensorData.AddRange(
            new SensorData { SensorId = 1, Value = "12.5", Timestamp = new DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc) },
            new SensorData { SensorId = 1, Value = "12.6", Timestamp = new DateTime(2020, 3, 1, 0, 0, 0, DateTimeKind.Utc) });
        db.BatteryHealthData.Add(new BatteryHealth { SensorId = 1, Status = "ok", Timestamp = new DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc) });
        db.SensorPhotos.Add(new SensorPhoto { UserId = user.Id, SensorId = 1, ContentType = "image/jpeg", Data = "AQID" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await CreateUserController(db, callerId: user.Id).DeleteOwnAccount(user.Id);

        // Telemetry moved to the anonymized ML store...
        Assert.Equal(2, db.AnonymizedReadings.Count());
        Assert.Equal("voltage", db.AnonymizedSeries.Single().SourceType);
        // ...and no personal telemetry / photos / ownership rows left behind (orphan-bug fix).
        Assert.Empty(db.SensorData);
        Assert.Empty(db.BatteryHealthData);
        Assert.Empty(db.SensorPhotos);
        Assert.Empty(db.SensorOwnershipPeriods);
        Assert.Empty(db.UserSensors);
    }

    [Fact]
    public async Task DeleteOwnAccount_ScrubbedUser_PassesRealIdentityValidation()
    {
        // Regression: ScrubUserPiiAsync used to null out the email, which Identity's
        // RequireUniqueEmail validator rejects — DeleteOwnAccount then 400'd, and because
        // anonymization had already committed it left the account half-deleted. Here the mock
        // UpdateAsync delegates to the real UserValidator so the scrubbed user is genuinely
        // validated: this fails (BadRequest) on a null email and passes on the placeholder.
        using var db = CreateDbContext();
        var user = MakeDbUser();
        MockUserManager.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        MockUserManager.Setup(m => m.UpdateSecurityStampAsync(user)).ReturnsAsync(IdentityResult.Success);
        MockUserManager.Object.Options = new IdentityOptions { User = { RequireUniqueEmail = true } };
        MockUserManager.Setup(m => m.GetUserNameAsync(user)).ReturnsAsync(() => user.UserName);
        MockUserManager.Setup(m => m.GetEmailAsync(user)).ReturnsAsync(() => user.Email);
        MockUserManager.Setup(m => m.FindByNameAsync(It.IsAny<string>())).ReturnsAsync((User?)null);
        MockUserManager.Setup(m => m.FindByEmailAsync(It.IsAny<string>())).ReturnsAsync((User?)null);
        MockUserManager.Setup(m => m.UpdateAsync(user))
            .Returns(() => new UserValidator<User>().ValidateAsync(MockUserManager.Object, user));

        var result = await CreateUserController(db, callerId: user.Id).DeleteOwnAccount(user.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.True(user.IsDeleted);
        Assert.EndsWith("@deleted.invalid", user.Email);
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
}
