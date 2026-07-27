using garge_api.Controllers;
using garge_api.Dtos.Pairing;
using garge_api.Helpers;
using garge_api.Hubs;
using garge_api.Models;
using garge_api.Models.Mqtt;
using garge_api.Models.Pairing;
using garge_api.Models.Sensor;
using garge_api.Models.Switch;
using garge_api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// Verifies the pairing-token flow: a user mints a short-lived single-use token (superseding any
/// earlier unconsumed ones); the operator redeems it for a parent device name, which claims every
/// sensor/switch under that parent for the token's user without stealing devices owned by others.
/// </summary>
public class PairingControllerTests : ControllerTestBase
{
    private PairingController CreateController(ApplicationDbContext db, string userId)
    {
        var ownership = new Mock<IDeviceOwnershipService>();

        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        var hub = new Mock<IHubContext<DeviceHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);

        var controller = new PairingController(db, NullLogger<PairingController>.Instance, ownership.Object, hub.Object);
        controller.ControllerContext = MakeControllerContext(userId);
        return controller;
    }

    private static Sensor MakeSensor(int id, string parentName = "gw") => new()
    {
        Id = id, Name = $"garge_volt_{id}", Type = "voltage", Role = "sensor",
        RegistrationCode = $"rc{id}", DefaultName = "Battery", ParentName = parentName
    };

    private static Switch MakeSwitch(int id) => new()
    {
        Id = id, Name = $"garge_socket_{id}", Type = "switch", Role = "switch", RegistrationCode = $"sw{id}"
    };

    private static PairingToken MakeToken(
        string userId, string token = "ABC234", DateTime? expiresAt = null, DateTime? consumedAt = null) => new()
    {
        Token = token,
        UserId = userId,
        ExpiresAt = expiresAt ?? DateTime.UtcNow.AddMinutes(15),
        ConsumedAt = consumedAt
    };

    private static ClaimPairingTokenDto Claim(string token = "ABC234", string parentName = "gw") =>
        new() { Token = token, ParentName = parentName };

    private const string ChipId = "a1b2c3d4e5f6";
    private const string DeviceName = $"garge_{ChipId}";

    private static ProvisionDeviceDto Provision(string token = "ABC234", string chipId = ChipId) =>
        new() { Token = token, ChipId = chipId };

    [Fact]
    public async Task CreatePairingToken_MintsSixCharTokenWithFifteenMinuteExpiry()
    {
        using var db = CreateDbContext();
        db.Users.Add(MakeUser("user-1"));
        await db.SaveChangesAsync();

        var result = await CreateController(db, "user-1").CreatePairingToken();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<PairingTokenDto>(ok.Value);
        Assert.Equal(PairingController.TokenLength, dto.Token.Length);
        Assert.All(dto.Token, c => Assert.Contains(c, RegistrationCode.Alphabet));
        Assert.InRange(dto.ExpiresAt, DateTime.UtcNow.AddMinutes(14), DateTime.UtcNow.AddMinutes(16));

        var row = db.PairingTokens.Single();
        Assert.Equal(dto.Token, row.Token);
        Assert.Equal("user-1", row.UserId);
        Assert.Null(row.ConsumedAt);
    }

    [Fact]
    public async Task CreatePairingToken_SupersedesPreviousUnconsumedTokens_KeepsConsumedOnes()
    {
        using var db = CreateDbContext();
        db.Users.Add(MakeUser("user-1"));
        db.PairingTokens.Add(MakeToken("user-1", "OLDPQR"));
        db.PairingTokens.Add(MakeToken("user-1", "USEDXY", consumedAt: DateTime.UtcNow.AddMinutes(-5)));
        await db.SaveChangesAsync();

        var result = await CreateController(db, "user-1").CreatePairingToken();

        Assert.IsType<OkObjectResult>(result);
        Assert.DoesNotContain(db.PairingTokens, t => t.Token == "OLDPQR"); // superseded
        Assert.Contains(db.PairingTokens, t => t.Token == "USEDXY"); // consumed rows are audit trail
        Assert.Single(db.PairingTokens.Where(t => t.ConsumedAt == null));
    }

    [Fact]
    public async Task CreatePairingToken_UnknownUser_Unauthorized()
    {
        using var db = CreateDbContext();

        var result = await CreateController(db, "ghost").CreatePairingToken();

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task ClaimPairing_MissingTokenOrParentName_BadRequest()
    {
        using var db = CreateDbContext();

        Assert.IsType<BadRequestObjectResult>(await CreateController(db, "op").ClaimPairing(Claim(token: " ")));
        Assert.IsType<BadRequestObjectResult>(await CreateController(db, "op").ClaimPairing(Claim(parentName: "")));
    }

    [Fact]
    public async Task ClaimPairing_UnknownToken_NotFound()
    {
        using var db = CreateDbContext();

        var result = await CreateController(db, "op").ClaimPairing(Claim("NOPE22"));

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task ClaimPairing_ExpiredToken_Gone()
    {
        using var db = CreateDbContext();
        db.Users.Add(MakeUser("user-1"));
        db.PairingTokens.Add(MakeToken("user-1", expiresAt: DateTime.UtcNow.AddMinutes(-1)));
        await db.SaveChangesAsync();

        var result = await CreateController(db, "op").ClaimPairing(Claim());

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(410, status.StatusCode);
    }

    [Fact]
    public async Task ClaimPairing_ConsumedToken_Gone()
    {
        using var db = CreateDbContext();
        db.Users.Add(MakeUser("user-1"));
        db.PairingTokens.Add(MakeToken("user-1", consumedAt: DateTime.UtcNow.AddMinutes(-1)));
        await db.SaveChangesAsync();

        var result = await CreateController(db, "op").ClaimPairing(Claim());

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(410, status.StatusCode);
    }

    [Fact]
    public async Task ClaimPairing_NoDevicesForParent_NotFoundWithDistinctCode()
    {
        using var db = CreateDbContext();
        db.Users.Add(MakeUser("user-1"));
        db.PairingTokens.Add(MakeToken("user-1"));
        await db.SaveChangesAsync();

        var result = await CreateController(db, "op").ClaimPairing(Claim(parentName: "no-such-gw"));

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var code = notFound.Value!.GetType().GetProperty("code")?.GetValue(notFound.Value);
        Assert.Equal("devices-not-found", code);
        Assert.Null(db.PairingTokens.Single().ConsumedAt); // token stays redeemable for a retry
    }

    [Fact]
    public async Task ClaimPairing_ClaimsSensorsAndDiscoveredSwitches_FirstOwnerGetsEpochStart()
    {
        using var db = CreateDbContext();
        db.Users.Add(MakeUser("user-1"));
        db.Sensors.Add(MakeSensor(1));
        db.Switches.Add(MakeSwitch(2));
        db.DiscoveredDevices.Add(new DiscoveredDevice { DiscoveredBy = "gw", Target = "garge_socket_2", Type = "switch", Timestamp = DateTime.UtcNow });
        db.PairingTokens.Add(MakeToken("user-1"));
        await db.SaveChangesAsync();

        // Lowercase token exercises the case-insensitive match.
        var result = await CreateController(db, "op").ClaimPairing(Claim("abc234"));

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<PairingClaimResultDto>(ok.Value);
        Assert.Equal(new[] { 1 }, dto.ClaimedSensorIds);
        Assert.Equal(new[] { 2 }, dto.ClaimedSwitchIds);
        Assert.Equal(0, dto.Skipped);

        var userSensor = db.UserSensors.Single(us => us.UserId == "user-1" && us.SensorId == 1);
        Assert.True(userSensor.IsOwner);
        var userSwitch = db.UserSwitches.Single(us => us.UserId == "user-1" && us.SwitchId == 2);
        Assert.True(userSwitch.IsOwner);

        // First-ever owners see all history from the device's birth.
        Assert.Equal(SensorOwnershipPeriod.FirstOwnerStart, db.SensorOwnershipPeriods.Single().StartedAt);
        Assert.Equal(SwitchOwnershipPeriod.FirstOwnerStart, db.SwitchOwnershipPeriods.Single().StartedAt);

        var token = db.PairingTokens.Single();
        Assert.NotNull(token.ConsumedAt);
        Assert.Equal("gw", token.ConsumedByParentName);
    }

    [Fact]
    public async Task ClaimPairing_DeviceOwnedByAnotherUser_IsSkippedNotStolen()
    {
        using var db = CreateDbContext();
        db.Users.AddRange(MakeUser("user-1"), MakeUser("other", "other@example.com"));
        db.Sensors.AddRange(MakeSensor(1), MakeSensor(2));
        db.UserSensors.Add(new UserSensor { UserId = "other", SensorId = 1, IsOwner = true });
        db.SensorOwnershipPeriods.Add(new SensorOwnershipPeriod
        {
            UserId = "other", SensorId = 1, StartedAt = SensorOwnershipPeriod.FirstOwnerStart, EndedAt = null
        });
        db.PairingTokens.Add(MakeToken("user-1"));
        await db.SaveChangesAsync();

        var result = await CreateController(db, "op").ClaimPairing(Claim());

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<PairingClaimResultDto>(ok.Value);
        Assert.Equal(new[] { 2 }, dto.ClaimedSensorIds);
        Assert.Equal(1, dto.Skipped);
        Assert.DoesNotContain(db.UserSensors, us => us.UserId == "user-1" && us.SensorId == 1);
        Assert.NotNull(db.PairingTokens.Single().ConsumedAt);
    }

    [Fact]
    public async Task ClaimPairing_ResoldSensor_LaterOwnerPeriodStartsNow()
    {
        using var db = CreateDbContext();
        db.Users.Add(MakeUser("user-1"));
        db.Sensors.Add(MakeSensor(1));
        // A previous, since-closed ownership stint: the new owner must not see that history.
        db.SensorOwnershipPeriods.Add(new SensorOwnershipPeriod
        {
            UserId = "previous-owner", SensorId = 1,
            StartedAt = SensorOwnershipPeriod.FirstOwnerStart, EndedAt = DateTime.UtcNow.AddDays(-1)
        });
        db.PairingTokens.Add(MakeToken("user-1"));
        await db.SaveChangesAsync();

        var result = await CreateController(db, "op").ClaimPairing(Claim());

        Assert.IsType<OkObjectResult>(result);
        var period = db.SensorOwnershipPeriods.Single(p => p.UserId == "user-1" && p.SensorId == 1);
        Assert.NotEqual(SensorOwnershipPeriod.FirstOwnerStart, period.StartedAt); // from now, not the full-history epoch
        Assert.Null(period.EndedAt);
    }

    [Fact]
    public async Task ClaimPairing_UserAlreadyHasDevice_SkipsWithoutDuplicateRows()
    {
        using var db = CreateDbContext();
        db.Users.Add(MakeUser("user-1"));
        db.Sensors.Add(MakeSensor(1));
        db.UserSensors.Add(new UserSensor { UserId = "user-1", SensorId = 1, IsOwner = true });
        db.PairingTokens.Add(MakeToken("user-1"));
        await db.SaveChangesAsync();

        var result = await CreateController(db, "op").ClaimPairing(Claim());

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<PairingClaimResultDto>(ok.Value);
        Assert.Empty(dto.ClaimedSensorIds);
        Assert.Equal(1, dto.Skipped);
        Assert.Single(db.UserSensors.Where(us => us.UserId == "user-1" && us.SensorId == 1));
    }

    [Fact]
    public async Task ProvisionDevice_FirstBoot_CreatesBrokerUserAndAcls()
    {
        using var db = CreateDbContext();
        db.Users.Add(MakeUser("user-1"));
        db.PairingTokens.Add(MakeToken("user-1"));
        await db.SaveChangesAsync();

        var result = await CreateController(db, "device").ProvisionDevice(Provision());

        var ok = Assert.IsType<OkObjectResult>(result);
        var creds = Assert.IsType<DeviceCredentialsDto>(ok.Value);
        Assert.Equal(DeviceName, creds.Username);
        Assert.Equal(22, creds.Password.Length); // secrets.token_urlsafe(16) equivalent
        Assert.All(creds.Password, c => Assert.Contains(c, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"));

        var user = db.EMQXMqttUsers.Single();
        Assert.Equal(DeviceName, user.Username);
        Assert.False(user.IsSuperuser);
        // Only the PBKDF2 hash is stored; the returned plaintext must verify against it.
        Assert.Equal(MqttPasswordHasher.HashPasswordPBKDF2(creds.Password, user.Salt!), user.PasswordHash);

        var acls = db.EMQXMqttAcls.OrderBy(a => a.Retain).ToList();
        Assert.Equal(2, acls.Count);
        Assert.Equal(new short?[] { 0, 1 }, acls.Select(a => a.Retain));
        Assert.All(acls, a =>
        {
            Assert.Equal(DeviceName, a.Username);
            Assert.Equal($"garge/devices/{DeviceName}/#", a.Topic);
            Assert.Equal("all", a.Action);
            Assert.Equal("allow", a.Permission);
            Assert.Equal((short)0, a.Qos);
        });

        // The token is only consumed by the later claim step.
        Assert.Null(db.PairingTokens.Single().ConsumedAt);
    }

    [Fact]
    public async Task ProvisionDevice_ExistingBrokerUser_RotatesPasswordWithoutDuplicates()
    {
        using var db = CreateDbContext();
        db.Users.Add(MakeUser("user-1"));
        db.PairingTokens.Add(MakeToken("user-1"));
        db.EMQXMqttUsers.Add(new EMQXMqttUser { Username = DeviceName, PasswordHash = "old-hash", Salt = "old-salt", IsSuperuser = false });
        db.EMQXMqttAcls.Add(new EMQXMqttAcl { Username = DeviceName, Permission = "allow", Action = "all", Topic = $"garge/devices/{DeviceName}/#", Qos = 0, Retain = 1 });
        db.EMQXMqttAcls.Add(new EMQXMqttAcl { Username = DeviceName, Permission = "allow", Action = "all", Topic = $"garge/devices/{DeviceName}/#", Qos = 0, Retain = 0 });
        await db.SaveChangesAsync();

        // Uppercase chip id exercises the lowercase normalization.
        var result = await CreateController(db, "device").ProvisionDevice(Provision(chipId: ChipId.ToUpperInvariant()));

        var ok = Assert.IsType<OkObjectResult>(result);
        var creds = Assert.IsType<DeviceCredentialsDto>(ok.Value);
        Assert.Equal(DeviceName, creds.Username);

        var user = db.EMQXMqttUsers.Single(); // rotated in place, not duplicated
        Assert.NotEqual("old-hash", user.PasswordHash);
        Assert.NotEqual("old-salt", user.Salt);
        Assert.Equal(MqttPasswordHasher.HashPasswordPBKDF2(creds.Password, user.Salt!), user.PasswordHash);

        Assert.Equal(2, db.EMQXMqttAcls.Count()); // upsert respects the unique composite index
    }

    [Fact]
    public async Task ProvisionDevice_DeviceOwnedByAnotherUser_Conflict()
    {
        using var db = CreateDbContext();
        db.Users.AddRange(MakeUser("user-1"), MakeUser("other", "other@example.com"));
        db.Sensors.Add(MakeSensor(1, parentName: DeviceName));
        db.UserSensors.Add(new UserSensor { UserId = "other", SensorId = 1, IsOwner = true });
        db.PairingTokens.Add(MakeToken("user-1"));
        await db.SaveChangesAsync();

        var result = await CreateController(db, "device").ProvisionDevice(Provision());

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Empty(db.EMQXMqttUsers); // no credentials minted for a device someone else owns
        Assert.Empty(db.EMQXMqttAcls);
    }

    [Fact]
    public async Task ProvisionDevice_ExpiredToken_Gone()
    {
        using var db = CreateDbContext();
        db.Users.Add(MakeUser("user-1"));
        db.PairingTokens.Add(MakeToken("user-1", expiresAt: DateTime.UtcNow.AddMinutes(-1)));
        await db.SaveChangesAsync();

        var result = await CreateController(db, "device").ProvisionDevice(Provision());

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(410, status.StatusCode);
        Assert.Empty(db.EMQXMqttUsers);
    }

    [Fact]
    public async Task ProvisionDevice_InvalidChipId_BadRequest()
    {
        using var db = CreateDbContext();

        Assert.IsType<BadRequestObjectResult>(await CreateController(db, "device").ProvisionDevice(Provision(chipId: "nothex")));
        Assert.IsType<BadRequestObjectResult>(await CreateController(db, "device").ProvisionDevice(Provision(chipId: "a1b2c3d4e5f6aa"))); // too long
        Assert.IsType<BadRequestObjectResult>(await CreateController(db, "device").ProvisionDevice(Provision(chipId: "g1b2c3d4e5f6"))); // non-hex char
    }
}
