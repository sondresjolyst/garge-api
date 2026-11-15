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
using System.Text.Json;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// Verifies the pairing-token flow: a user mints a short-lived single-use token (superseding any
/// earlier unconsumed ones); the operator redeems it for a parent device name, which claims every
/// sensor/switch under that parent for the token's user without stealing devices owned by others.
/// </summary>
public class PairingControllerTests : ControllerTestBase
{
    private PairingController CreateController(ApplicationDbContext db, string userId) =>
        CreateController(db, userId, out _);

    private PairingController CreateController(
        ApplicationDbContext db, string userId, out Mock<IClientProxy> proxy)
    {
        var ownership = new Mock<IDeviceOwnershipService>();

        proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var capturedProxy = proxy;
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(capturedProxy.Object);
        var hub = new Mock<IHubContext<DeviceHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);

        // The real ACL service, not a mock: it is a thin wrapper over the context and the ACL
        // rows it writes are what these tests assert on.
        var mqttAcls = new MqttAclService(db, NullLogger<MqttAclService>.Instance);
        var leases = new DeviceLeaseService(db, NullLogger<DeviceLeaseService>.Instance);

        var controller = new PairingController(db, NullLogger<PairingController>.Instance, ownership.Object, hub.Object, mqttAcls, leases);
        controller.ControllerContext = MakeControllerContext(userId);
        return controller;
    }

    /// <summary>The hub payload is an anonymous object, so match it by reflection.</summary>
    private static bool IsDeviceCreated(object?[] args, string kind, int id)
    {
        if (args.Length != 1 || args[0] is null) return false;
        var payload = args[0]!;
        var type = payload.GetType();
        return type.GetProperty("kind")?.GetValue(payload) as string == kind
            && type.GetProperty("id")?.GetValue(payload) is int actual
            && actual == id;
    }

    private static void AssertDeviceCreated(
        Mock<IClientProxy> proxy, string kind, int id, Func<Times> times)
    {
        proxy.Verify(pr => pr.SendCoreAsync(
            "device-created",
            It.Is<object?[]>(a => IsDeviceCreated(a, kind, id)),
            It.IsAny<CancellationToken>()), times);
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

    // Re-pairing a device you already own adds no ownership, but the setup wizard
    // advances on device-created and would otherwise wait for an event that never
    // comes, leaving the user stuck on "Waiting for your device".
    [Fact]
    public async Task ClaimPairing_UserAlreadyHasDevice_ReportsItAndRaisesDeviceCreated()
    {
        using var db = CreateDbContext();
        db.Users.Add(MakeUser("user-1"));
        db.Sensors.Add(MakeSensor(1));
        db.UserSensors.Add(new UserSensor { UserId = "user-1", SensorId = 1, IsOwner = true });
        db.PairingTokens.Add(MakeToken("user-1"));
        await db.SaveChangesAsync();

        var controller = CreateController(db, "op", out var proxy);
        var result = await controller.ClaimPairing(Claim());

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<PairingClaimResultDto>(ok.Value);
        Assert.Empty(dto.ClaimedSensorIds);
        Assert.Equal(new[] { 1 }, dto.AlreadyOwnedSensorIds);
        Assert.Equal(0, dto.Skipped);
        Assert.Single(db.UserSensors.Where(us => us.UserId == "user-1" && us.SensorId == 1));
        AssertDeviceCreated(proxy, "sensor", 1, Times.Once);
    }

    [Fact]
    public async Task ClaimPairing_UserAlreadyHasSwitch_ReportsItAndRaisesDeviceCreated()
    {
        using var db = CreateDbContext();
        db.Users.Add(MakeUser("user-1"));
        db.Switches.Add(MakeSwitch(2));
        db.DiscoveredDevices.Add(new DiscoveredDevice
        {
            DiscoveredBy = "gw", Target = "garge_socket_2", Type = "switch", Timestamp = DateTime.UtcNow
        });
        db.UserSwitches.Add(new UserSwitch { UserId = "user-1", SwitchId = 2, IsOwner = true });
        db.PairingTokens.Add(MakeToken("user-1"));
        await db.SaveChangesAsync();

        var controller = CreateController(db, "op", out var proxy);
        var result = await controller.ClaimPairing(Claim());

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<PairingClaimResultDto>(ok.Value);
        Assert.Equal(new[] { 2 }, dto.AlreadyOwnedSwitchIds);
        Assert.Equal(0, dto.Skipped);
        Assert.Single(db.UserSwitches.Where(us => us.UserId == "user-1" && us.SwitchId == 2));
        AssertDeviceCreated(proxy, "switch", 2, Times.Once);
    }

    // A shared, non-owner row on someone else's device must stay a skip: the
    // already-owned path must not fire for a device the user does not own.
    [Fact]
    public async Task ClaimPairing_SharedSensorOwnedByAnotherUser_SkipsAndRaisesNothing()
    {
        using var db = CreateDbContext();
        db.Users.Add(MakeUser("user-1"));
        db.Users.Add(MakeUser("owner"));
        db.Sensors.Add(MakeSensor(1));
        db.UserSensors.Add(new UserSensor { UserId = "owner", SensorId = 1, IsOwner = true });
        db.UserSensors.Add(new UserSensor { UserId = "user-1", SensorId = 1, IsOwner = false });
        db.PairingTokens.Add(MakeToken("user-1"));
        await db.SaveChangesAsync();

        var controller = CreateController(db, "op", out var proxy);
        var result = await controller.ClaimPairing(Claim());

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<PairingClaimResultDto>(ok.Value);
        Assert.Empty(dto.ClaimedSensorIds);
        Assert.Empty(dto.AlreadyOwnedSensorIds);
        Assert.Equal(1, dto.Skipped);
        AssertDeviceCreated(proxy, "sensor", 1, Times.Never);
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
    public async Task ProvisionDevice_WithLeasedTargets_AlsoGrantsTheirAcls()
    {
        using var db = CreateDbContext();
        db.Users.Add(MakeUser("user-1"));
        db.PairingTokens.Add(MakeToken("user-1"));
        // Holds the lease but is missing the rows, because the grant was added after it was
        // discovered and it only rediscovers on its own schedule. Pairing catches that up.
        db.DeviceControllers.Add(new DeviceController
        {
            Target = "wiz_SOCKET_6c2990a96cde",
            ControllerDeviceName = DeviceName,
            LeaseExpiresAt = DateTime.UtcNow.AddMinutes(2),
            LastSeenFromTarget = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var result = await CreateController(db, "device").ProvisionDevice(Provision());

        Assert.IsType<OkObjectResult>(result);
        var topics = db.EMQXMqttAcls.Select(a => a.Topic).Distinct().OrderBy(t => t).ToList();
        Assert.Equal(
            new[] { $"garge/devices/{DeviceName}/#", "garge/devices/wiz_SOCKET_6c2990a96cde/#" },
            topics);
        Assert.Equal(4, db.EMQXMqttAcls.Count()); // retained and non-retained row per topic
    }

    [Fact]
    public async Task ProvisionDevice_TargetLeasedByAnotherGateway_GrantsOnlyItsOwnPrefix()
    {
        using var db = CreateDbContext();
        db.Users.Add(MakeUser("user-1"));
        db.PairingTokens.Add(MakeToken("user-1"));
        // This device discovered the socket, but another gateway holds the lease, so pairing must
        // not hand it access: one gateway answers a shared device, not both.
        db.DiscoveredDevices.Add(new DiscoveredDevice
        {
            DiscoveredBy = DeviceName, Target = "wiz_SOCKET_6c2990a96cde", Type = "switch", Timestamp = DateTime.UtcNow
        });
        db.DeviceControllers.Add(new DeviceController
        {
            Target = "wiz_SOCKET_6c2990a96cde",
            ControllerDeviceName = "garge_ffffffffffff",
            LeaseExpiresAt = DateTime.UtcNow.AddMinutes(2),
            LastSeenFromTarget = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var result = await CreateController(db, "device").ProvisionDevice(Provision());

        Assert.IsType<OkObjectResult>(result);
        Assert.All(db.EMQXMqttAcls, a => Assert.Equal($"garge/devices/{DeviceName}/#", a.Topic));
        Assert.Equal(2, db.EMQXMqttAcls.Count());
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
        Assert.NotNull(db.PairingTokens.Single().ProvisionRejectedAt);
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

    private static PairingTokenStatusDto AssertStatus(IActionResult result, string expected)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<PairingTokenStatusDto>(ok.Value);
        Assert.Equal(expected, dto.Status);
        return dto;
    }

    [Fact]
    public async Task GetPairingTokenStatus_FreshToken_Waiting()
    {
        using var db = CreateDbContext();
        db.Users.Add(MakeUser("user-1"));
        db.PairingTokens.Add(MakeToken("user-1"));
        await db.SaveChangesAsync();

        var dto = AssertStatus(await CreateController(db, "user-1").GetPairingTokenStatus("ABC234"), PairingTokenStatusDto.Waiting);
        Assert.Empty(dto.SensorIds);
        Assert.Empty(dto.SwitchIds);
    }

    [Fact]
    public async Task GetPairingTokenStatus_AfterSuccessfulProvision_Provisioned()
    {
        using var db = CreateDbContext();
        db.Users.Add(MakeUser("user-1"));
        db.PairingTokens.Add(MakeToken("user-1"));
        await db.SaveChangesAsync();

        Assert.IsType<OkObjectResult>(await CreateController(db, "device").ProvisionDevice(Provision()));
        Assert.NotNull(db.PairingTokens.Single().ProvisionedAt);

        var dto = AssertStatus(await CreateController(db, "user-1").GetPairingTokenStatus("ABC234"), PairingTokenStatusDto.Provisioned);
        Assert.Empty(dto.SensorIds);
        Assert.Empty(dto.SwitchIds);
    }

    [Fact]
    public async Task GetPairingTokenStatus_ProvisionRefusedOwnedByOther_OwnedByOther()
    {
        using var db = CreateDbContext();
        db.Users.AddRange(MakeUser("user-1"), MakeUser("other", "other@example.com"));
        db.Sensors.Add(MakeSensor(1, parentName: DeviceName));
        db.UserSensors.Add(new UserSensor { UserId = "other", SensorId = 1, IsOwner = true });
        db.PairingTokens.Add(MakeToken("user-1"));
        await db.SaveChangesAsync();

        Assert.IsType<ConflictObjectResult>(await CreateController(db, "device").ProvisionDevice(Provision()));
        var token = db.PairingTokens.Single();
        Assert.Null(token.ProvisionedAt);
        Assert.NotNull(token.ProvisionRejectedAt);
        Assert.Null(token.ConsumedAt);

        var dto = AssertStatus(await CreateController(db, "user-1").GetPairingTokenStatus("ABC234"), PairingTokenStatusDto.OwnedByOther);
        Assert.Empty(dto.SensorIds);
        Assert.Empty(dto.SwitchIds);
    }

    [Fact]
    public async Task ProvisionDevice_OtherFailures_DoNotSetProvisionRejectedAt()
    {
        using var db = CreateDbContext();
        db.Users.Add(MakeUser("user-1"));
        db.PairingTokens.Add(MakeToken("user-1"));
        db.PairingTokens.Add(MakeToken("user-1", "EXPXYZ", expiresAt: DateTime.UtcNow.AddMinutes(-1)));
        db.PairingTokens.Add(MakeToken("user-1", "USEDXY", consumedAt: DateTime.UtcNow.AddMinutes(-1)));
        await db.SaveChangesAsync();

        Assert.IsType<BadRequestObjectResult>(await CreateController(db, "device").ProvisionDevice(Provision(chipId: "nothex")));
        Assert.IsType<NotFoundObjectResult>(await CreateController(db, "device").ProvisionDevice(Provision(token: "ZZZ999")));
        Assert.Equal(410, Assert.IsType<ObjectResult>(await CreateController(db, "device").ProvisionDevice(Provision(token: "EXPXYZ"))).StatusCode);
        Assert.Equal(410, Assert.IsType<ObjectResult>(await CreateController(db, "device").ProvisionDevice(Provision(token: "USEDXY"))).StatusCode);

        Assert.All(db.PairingTokens, t =>
        {
            Assert.Null(t.ProvisionRejectedAt);
            Assert.Null(t.ProvisionedAt);
        });
        AssertStatus(await CreateController(db, "user-1").GetPairingTokenStatus("ABC234"), PairingTokenStatusDto.Waiting);
        AssertStatus(await CreateController(db, "user-1").GetPairingTokenStatus("EXPXYZ"), PairingTokenStatusDto.Expired);
    }

    [Fact]
    public async Task GetPairingTokenStatus_OwnedByOtherPastExpiry_StaysOwnedByOther()
    {
        using var db = CreateDbContext();
        db.Users.Add(MakeUser("user-1"));
        var token = MakeToken("user-1", expiresAt: DateTime.UtcNow.AddMinutes(-1));
        token.ProvisionRejectedAt = DateTime.UtcNow.AddMinutes(-3);
        db.PairingTokens.Add(token);
        await db.SaveChangesAsync();

        AssertStatus(await CreateController(db, "user-1").GetPairingTokenStatus("ABC234"), PairingTokenStatusDto.OwnedByOther);
    }

    [Fact]
    public async Task GetPairingTokenStatus_ProvisionedThenRefused_OwnedByOther()
    {
        using var db = CreateDbContext();
        db.Users.AddRange(MakeUser("user-1"), MakeUser("other", "other@example.com"));
        db.Sensors.Add(MakeSensor(1, parentName: DeviceName));
        db.PairingTokens.Add(MakeToken("user-1"));
        await db.SaveChangesAsync();

        Assert.IsType<OkObjectResult>(await CreateController(db, "device").ProvisionDevice(Provision()));
        AssertStatus(await CreateController(db, "user-1").GetPairingTokenStatus("ABC234"), PairingTokenStatusDto.Provisioned);

        // Another user claims the device before this token is redeemed.
        db.UserSensors.Add(new UserSensor { UserId = "other", SensorId = 1, IsOwner = true });
        await db.SaveChangesAsync();

        Assert.IsType<ConflictObjectResult>(await CreateController(db, "device").ProvisionDevice(Provision()));
        var token = db.PairingTokens.Single();
        Assert.Null(token.ProvisionedAt);
        Assert.NotNull(token.ProvisionRejectedAt);
        AssertStatus(await CreateController(db, "user-1").GetPairingTokenStatus("ABC234"), PairingTokenStatusDto.OwnedByOther);
    }

    [Fact]
    public async Task GetPairingTokenStatus_ConsumedAfterRefusal_Paired()
    {
        using var db = CreateDbContext();
        db.Users.Add(MakeUser("user-1"));
        db.Sensors.Add(MakeSensor(1));
        db.UserSensors.Add(new UserSensor { UserId = "user-1", SensorId = 1, IsOwner = true });
        var token = MakeToken("user-1", consumedAt: DateTime.UtcNow.AddMinutes(-1));
        token.ConsumedByParentName = "gw";
        token.ProvisionRejectedAt = DateTime.UtcNow.AddMinutes(-2);
        db.PairingTokens.Add(token);
        await db.SaveChangesAsync();

        var dto = AssertStatus(await CreateController(db, "user-1").GetPairingTokenStatus("ABC234"), PairingTokenStatusDto.Paired);
        Assert.Equal(new[] { 1 }, dto.SensorIds);
    }

    [Fact]
    public async Task GetPairingTokenStatus_RefusedThenProvisioned_Provisioned()
    {
        using var db = CreateDbContext();
        db.Users.AddRange(MakeUser("user-1"), MakeUser("other", "other@example.com"));
        db.Sensors.Add(MakeSensor(1, parentName: DeviceName));
        var foreignOwner = new UserSensor { UserId = "other", SensorId = 1, IsOwner = true };
        db.UserSensors.Add(foreignOwner);
        db.PairingTokens.Add(MakeToken("user-1"));
        await db.SaveChangesAsync();

        Assert.IsType<ConflictObjectResult>(await CreateController(db, "device").ProvisionDevice(Provision()));
        AssertStatus(await CreateController(db, "user-1").GetPairingTokenStatus("ABC234"), PairingTokenStatusDto.OwnedByOther);

        // The other owner releases the device, so the next attempt succeeds.
        db.UserSensors.Remove(foreignOwner);
        await db.SaveChangesAsync();

        Assert.IsType<OkObjectResult>(await CreateController(db, "device").ProvisionDevice(Provision()));
        var token = db.PairingTokens.Single();
        Assert.Null(token.ProvisionRejectedAt);
        Assert.NotNull(token.ProvisionedAt);
        AssertStatus(await CreateController(db, "user-1").GetPairingTokenStatus("ABC234"), PairingTokenStatusDto.Provisioned);
    }

    [Fact]
    public async Task GetPairingTokenStatus_AfterClaim_PairedWithOnlyCallerOwnedIds()
    {
        using var db = CreateDbContext();
        db.Users.AddRange(MakeUser("user-1"), MakeUser("other", "other@example.com"));
        db.Sensors.AddRange(MakeSensor(3), MakeSensor(1), MakeSensor(5), MakeSensor(9, parentName: "elsewhere"));
        db.Switches.AddRange(MakeSwitch(4), MakeSwitch(2), MakeSwitch(6), MakeSwitch(8));
        db.DiscoveredDevices.AddRange(
            new DiscoveredDevice { DiscoveredBy = "gw", Target = "garge_socket_4", Type = "switch", Timestamp = DateTime.UtcNow },
            new DiscoveredDevice { DiscoveredBy = "gw", Target = "garge_socket_2", Type = "switch", Timestamp = DateTime.UtcNow },
            new DiscoveredDevice { DiscoveredBy = "gw", Target = "garge_socket_6", Type = "switch", Timestamp = DateTime.UtcNow },
            new DiscoveredDevice { DiscoveredBy = "other-gw", Target = "garge_socket_8", Type = "switch", Timestamp = DateTime.UtcNow });
        // Owned by another user, so the claim skips them and the status leaves them out.
        db.UserSensors.Add(new UserSensor { UserId = "other", SensorId = 5, IsOwner = true });
        db.UserSwitches.Add(new UserSwitch { UserId = "other", SwitchId = 6, IsOwner = true });
        // Owned by the caller but not under the paired parent.
        db.UserSensors.Add(new UserSensor { UserId = "user-1", SensorId = 9, IsOwner = true });
        db.UserSwitches.Add(new UserSwitch { UserId = "user-1", SwitchId = 8, IsOwner = true });
        db.PairingTokens.Add(MakeToken("user-1"));
        await db.SaveChangesAsync();

        Assert.IsType<OkObjectResult>(await CreateController(db, "op").ClaimPairing(Claim()));
        // A shared, non-owner row does not count as the caller's device.
        db.UserSensors.Add(new UserSensor { UserId = "user-1", SensorId = 5, IsOwner = false });
        db.UserSwitches.Add(new UserSwitch { UserId = "user-1", SwitchId = 6, IsOwner = false });
        await db.SaveChangesAsync();

        var dto = AssertStatus(await CreateController(db, "user-1").GetPairingTokenStatus("ABC234"), PairingTokenStatusDto.Paired);
        Assert.Equal(new[] { 1, 3 }, dto.SensorIds);
        Assert.Equal(new[] { 2, 4 }, dto.SwitchIds);
    }

    [Fact]
    public async Task GetPairingTokenStatus_DeviceOwnedByAnotherUser_PairedWithEmptyLists()
    {
        using var db = CreateDbContext();
        db.Users.AddRange(MakeUser("user-1"), MakeUser("other", "other@example.com"));
        db.Sensors.Add(MakeSensor(1));
        db.Switches.Add(MakeSwitch(2));
        db.DiscoveredDevices.Add(new DiscoveredDevice { DiscoveredBy = "gw", Target = "garge_socket_2", Type = "switch", Timestamp = DateTime.UtcNow });
        db.UserSensors.Add(new UserSensor { UserId = "other", SensorId = 1, IsOwner = true });
        db.UserSwitches.Add(new UserSwitch { UserId = "other", SwitchId = 2, IsOwner = true });
        db.PairingTokens.Add(MakeToken("user-1"));
        await db.SaveChangesAsync();

        Assert.IsType<OkObjectResult>(await CreateController(db, "op").ClaimPairing(Claim()));

        var dto = AssertStatus(await CreateController(db, "user-1").GetPairingTokenStatus("ABC234"), PairingTokenStatusDto.Paired);
        Assert.Empty(dto.SensorIds);
        Assert.Empty(dto.SwitchIds);
    }

    [Fact]
    public async Task GetPairingTokenStatus_ExpiredToken_Expired()
    {
        using var db = CreateDbContext();
        db.Users.Add(MakeUser("user-1"));
        var token = MakeToken("user-1", expiresAt: DateTime.UtcNow.AddMinutes(-1));
        token.ProvisionedAt = DateTime.UtcNow.AddMinutes(-2);
        db.PairingTokens.Add(token);
        await db.SaveChangesAsync();

        AssertStatus(await CreateController(db, "user-1").GetPairingTokenStatus("ABC234"), PairingTokenStatusDto.Expired);
    }

    [Fact]
    public async Task GetPairingTokenStatus_ConsumedThenPastExpiry_StillPaired()
    {
        using var db = CreateDbContext();
        db.Users.Add(MakeUser("user-1"));
        db.Sensors.Add(MakeSensor(1));
        db.UserSensors.Add(new UserSensor { UserId = "user-1", SensorId = 1, IsOwner = true });
        var token = MakeToken("user-1", expiresAt: DateTime.UtcNow.AddMinutes(-1), consumedAt: DateTime.UtcNow.AddMinutes(-5));
        token.ConsumedByParentName = "gw";
        db.PairingTokens.Add(token);
        await db.SaveChangesAsync();

        var dto = AssertStatus(await CreateController(db, "user-1").GetPairingTokenStatus("ABC234"), PairingTokenStatusDto.Paired);
        Assert.Equal(new[] { 1 }, dto.SensorIds);
    }

    [Fact]
    public async Task GetPairingTokenStatus_OtherUsersToken_NotFoundLikeUnknown()
    {
        using var db = CreateDbContext();
        db.Users.AddRange(MakeUser("user-1"), MakeUser("other", "other@example.com"));
        db.PairingTokens.Add(MakeToken("other"));
        await db.SaveChangesAsync();

        var foreign = Assert.IsType<NotFoundObjectResult>(await CreateController(db, "user-1").GetPairingTokenStatus("ABC234"));
        var unknown = Assert.IsType<NotFoundObjectResult>(await CreateController(db, "user-1").GetPairingTokenStatus("ZZZ999"));
        Assert.Equal(JsonSerializer.Serialize(unknown.Value), JsonSerializer.Serialize(foreign.Value));
    }

    [Fact]
    public async Task GetPairingTokenStatus_UnknownToken_NotFound()
    {
        using var db = CreateDbContext();
        db.Users.Add(MakeUser("user-1"));
        await db.SaveChangesAsync();

        Assert.IsType<NotFoundObjectResult>(await CreateController(db, "user-1").GetPairingTokenStatus("ZZZ999"));
    }

    [Fact]
    public async Task GetPairingTokenStatus_LowercaseToken_Accepted()
    {
        using var db = CreateDbContext();
        db.Users.Add(MakeUser("user-1"));
        db.PairingTokens.Add(MakeToken("user-1"));
        await db.SaveChangesAsync();

        AssertStatus(await CreateController(db, "user-1").GetPairingTokenStatus(" abc234 "), PairingTokenStatusDto.Waiting);
    }
}
