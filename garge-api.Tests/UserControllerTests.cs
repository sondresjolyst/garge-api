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

    [Fact]
    public async Task ExportData_BatteryHealth_HoldsReadingFactsLimitedToThePeriod()
    {
        using var db = CreateDbContext();
        var start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        db.UserProfiles.Add(new UserProfile { Id = "user-1", User = MakeDbUser() });
        db.Sensors.Add(new garge_api.Models.Sensor.Sensor { Id = 1, Name = "v", Type = "voltage", Role = "sensor", RegistrationCode = "rc", DefaultName = "Battery", ParentName = "gw" });
        db.UserSensors.Add(new garge_api.Models.Sensor.UserSensor { UserId = "user-1", SensorId = 1, IsOwner = true });
        db.SensorOwnershipPeriods.Add(new garge_api.Models.Sensor.SensorOwnershipPeriod { UserId = "user-1", SensorId = 1, StartedAt = start });
        // Earlier periods of another sensor and another user must not widen the windows below.
        db.Sensors.Add(new garge_api.Models.Sensor.Sensor { Id = 2, Name = "v2", Type = "voltage", Role = "sensor", RegistrationCode = "rc2", DefaultName = "Battery", ParentName = "gw" });
        db.UserSensors.Add(new garge_api.Models.Sensor.UserSensor { UserId = "user-1", SensorId = 2, IsOwner = true });
        db.SensorOwnershipPeriods.Add(new garge_api.Models.Sensor.SensorOwnershipPeriod { UserId = "user-1", SensorId = 2, StartedAt = start.AddYears(-1) });
        db.SensorOwnershipPeriods.Add(new garge_api.Models.Sensor.SensorOwnershipPeriod { UserId = "user-2", SensorId = 1, StartedAt = start.AddYears(-1) });
        db.BatteryHealthData.AddRange(
            new garge_api.Models.Sensor.BatteryHealth { SensorId = 1, CurrentVoltage = 12.2f, RestingMedian = 12.4f, VoltageMin24h = 12.3f, Timestamp = start.AddHours(12) },
            new garge_api.Models.Sensor.BatteryHealth { SensorId = 1, CurrentVoltage = 12.5f, RestingMedian = 12.4f, VoltageMin24h = 12.3f, Timestamp = start.AddDays(1) },
            new garge_api.Models.Sensor.BatteryHealth { SensorId = 1, CurrentVoltage = 12.6f, RestingMedian = 12.4f, VoltageMin24h = 12.3f, Timestamp = start.AddDays(5) });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        MockUserManager.Setup(m => m.FindByIdAsync("user-1")).ReturnsAsync(MakeDbUser());

        var result = Assert.IsType<OkObjectResult>(await CreateUserController(db).ExportData("user-1"));
        var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(result.Value));
        var rows = json.RootElement.EnumerateObject().First(p => p.NameEquals("Sensors")).Value.EnumerateArray()
            .First(s => s.GetProperty("BatteryHealth").GetArrayLength() > 0).GetProperty("BatteryHealth");

        Assert.Equal(System.Text.Json.JsonValueKind.Null, rows[0].GetProperty("VoltageMin24h").ValueKind);
        var near = rows[1];
        Assert.Equal(12.5f, near.GetProperty("CurrentVoltage").GetSingle());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, near.GetProperty("RestingMedian").ValueKind);
        Assert.Equal(12.3f, near.GetProperty("VoltageMin24h").GetSingle());
        var far = rows[2];
        Assert.Equal(12.4f, far.GetProperty("RestingMedian").GetSingle());
        var names = rows[0].EnumerateObject().Select(p => p.Name).OrderBy(n => n);
        Assert.Equal(new[] { "CurrentVoltage", "OnChargerNow", "RestingMedian", "Timestamp", "VoltageMin24h" }, names);
    }

    [Fact]
    public async Task ExportData_BatteryCharges_AreLimitedToTheViewersPeriods()
    {
        using var db = CreateDbContext();
        var start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddDays(60);
        db.UserProfiles.Add(new UserProfile { Id = "user-1", User = MakeDbUser() });
        db.Sensors.Add(new garge_api.Models.Sensor.Sensor { Id = 1, Name = "v", Type = "voltage", Role = "sensor", RegistrationCode = "rc", DefaultName = "Battery", ParentName = "gw" });
        db.Sensors.Add(new garge_api.Models.Sensor.Sensor { Id = 2, Name = "v2", Type = "voltage", Role = "sensor", RegistrationCode = "rc2", DefaultName = "Battery", ParentName = "gw" });
        db.UserSensors.Add(new garge_api.Models.Sensor.UserSensor { UserId = "user-1", SensorId = 1, IsOwner = true });
        db.UserSensors.Add(new garge_api.Models.Sensor.UserSensor { UserId = "user-1", SensorId = 2, IsOwner = true });
        db.SensorOwnershipPeriods.Add(new garge_api.Models.Sensor.SensorOwnershipPeriod { UserId = "user-1", SensorId = 1, StartedAt = start, EndedAt = end });
        db.SensorOwnershipPeriods.Add(new garge_api.Models.Sensor.SensorOwnershipPeriod { UserId = "user-1", SensorId = 2, StartedAt = start.AddYears(-1) });
        db.SensorOwnershipPeriods.Add(new garge_api.Models.Sensor.SensorOwnershipPeriod { UserId = "user-2", SensorId = 1, StartedAt = start.AddYears(-1) });
        garge_api.Models.Sensor.BatteryChargeEvent Charge(DateTime from) => new()
        {
            SensorId = 1, StartedAt = from, EndedAt = from.AddHours(24), PeakVoltage = 14.4f, RestingAtTime = 12.4f,
            DurationMinutes = 1440, IsFull = true, SettledVoltage = 12.8f, SelfDischargePctPerWeek = 0.7f,
        };
        db.BatteryChargeEvents.AddRange(
            Charge(start.AddDays(-1)),
            Charge(start.AddDays(1)),
            Charge(start.AddDays(20)),
            Charge(end.AddDays(-1).AddHours(-30)),
            Charge(end.AddHours(-12)),
            new garge_api.Models.Sensor.BatteryChargeEvent { SensorId = 2, StartedAt = start.AddDays(5), EndedAt = start.AddDays(6), PeakVoltage = 15f, RestingAtTime = 12f, DurationMinutes = 1440, IsFull = true });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        MockUserManager.Setup(m => m.FindByIdAsync("user-1")).ReturnsAsync(MakeDbUser());

        var result = Assert.IsType<OkObjectResult>(await CreateUserController(db).ExportData("user-1"));
        var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(result.Value));
        var sensors = json.RootElement.GetProperty("Sensors").EnumerateArray().ToDictionary(s => s.GetProperty("Id").GetInt32());
        var charges = sensors[1].GetProperty("BatteryCharges");
        Assert.Equal(15f, sensors[2].GetProperty("BatteryCharges").EnumerateArray().Single().GetProperty("PeakVoltage").GetSingle());

        // The charge before the period and the one crossing its end are left out.
        Assert.Equal(3, charges.GetArrayLength());
        const System.Text.Json.JsonValueKind Null = System.Text.Json.JsonValueKind.Null;

        Assert.Equal(Null, charges[0].GetProperty("RestingAtTime").ValueKind);
        Assert.Equal(12.8f, charges[0].GetProperty("SettledVoltage").GetSingle());
        Assert.Equal(0.7f, charges[0].GetProperty("SelfDischargePctPerWeek").GetSingle());

        Assert.Equal(Null, charges[1].GetProperty("RestingAtTime").ValueKind);

        // The last ends 30 hours before the period does, so its windows reach past the end.
        Assert.Equal(Null, charges[2].GetProperty("SettledVoltage").ValueKind);
        Assert.Equal(Null, charges[2].GetProperty("SelfDischargePctPerWeek").ValueKind);
        Assert.Equal(14.4f, charges[2].GetProperty("PeakVoltage").GetSingle());
        Assert.True(charges[2].GetProperty("IsFull").GetBoolean());
        Assert.Equal(1440, charges[2].GetProperty("DurationMinutes").GetInt32());
        var names = charges[0].EnumerateObject().Select(p => p.Name).OrderBy(n => n);
        Assert.Equal(new[] { "DurationMinutes", "EndedAt", "IsFull", "PeakVoltage", "RestingAtTime", "SelfDischargePctPerWeek", "SettledVoltage", "StartedAt" }, names);
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

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(409, conflict.StatusCode);
        var body = System.Text.Json.JsonSerializer.SerializeToElement(conflict.Value);
        Assert.Equal("lastAdmin", body.GetProperty("code").GetString());
        Assert.Equal(new LastAdminException().Message, body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task DeleteOwnAccount_ConcurrentChange_ReturnsConflictWithItsCode()
    {
        using var db = CreateDbContext();
        var user = MakeDbUser();
        MockUserManager.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        var deleter = new Mock<IAccountDeleter<User>>();
        var refused = new AccountChangeConflictException(new InvalidOperationException("serialization failure"));
        deleter.Setup(d => d.DeleteAsync(user, It.IsAny<CancellationToken>())).ThrowsAsync(refused);

        var result = await CreateUserController(db, callerId: user.Id).DeleteOwnAccount(user.Id, deleter.Object);

        var body = System.Text.Json.JsonSerializer.SerializeToElement(Assert.IsType<ConflictObjectResult>(result).Value);
        Assert.Equal("accountChangeConflict", body.GetProperty("code").GetString());
        Assert.Equal(refused.Message, body.GetProperty("message").GetString());
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
