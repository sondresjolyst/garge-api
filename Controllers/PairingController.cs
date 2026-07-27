using garge_api.Constants;
using garge_api.Dtos.Pairing;
using garge_api.Helpers;
using garge_api.Hubs;
using garge_api.Models;
using garge_api.Models.Mqtt;
using garge_api.Models.Pairing;
using garge_api.Models.Sensor;
using garge_api.Models.Switch;
using garge_api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace garge_api.Controllers
{
    [ApiController]
    [Route("api/pairing")]
    [EnableCors("AllowAllOrigins")]
    [Authorize]
    public class PairingController : ControllerBase
    {
        /// <summary>How long a freshly minted pairing token stays redeemable.</summary>
        internal static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);

        /// <summary>Pairing tokens are short so a human can type them into the device setup flow.</summary>
        internal const int TokenLength = 6;

        private readonly ApplicationDbContext _context;
        private readonly ILogger<PairingController> _logger;
        private readonly IDeviceOwnershipService _ownership;
        private readonly IHubContext<DeviceHub> _hub;

        public PairingController(ApplicationDbContext context, ILogger<PairingController> logger, IDeviceOwnershipService ownership, IHubContext<DeviceHub> hub)
        {
            _context = context;
            _logger = logger;
            _ownership = ownership;
            _hub = hub;
        }

        /// <summary>
        /// Mints a short-lived, single-use pairing token for the current user. The physical device
        /// sends the token over MQTT during setup and the operator service redeems it via
        /// <see cref="ClaimPairing"/>. Minting a new token invalidates the user's previous
        /// unconsumed tokens so at most one is redeemable at a time.
        /// </summary>
        [HttpPost("token")]
        [Authorize(Policy = "ActiveSubscription")]
        [SwaggerOperation(Summary = "Mints a short-lived single-use pairing token for the current user.")]
        [SwaggerResponse(200, "The minted token and its expiry.", typeof(PairingTokenDto))]
        public async Task<IActionResult> CreatePairingToken()
        {
            _logger.LogInformation("CreatePairingToken called by {@LogData}", new { CallerUserId = User.UserId() });

            var userId = User.UserId();
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                _logger.LogWarning("CreatePairingToken unauthorized: User not found {@LogData}", new { UserId = userId });
                return Unauthorized();
            }

            // A new token supersedes any earlier unconsumed ones, so a lost or mistyped token can
            // never be redeemed after the user has minted a replacement.
            var staleTokens = await _context.PairingTokens
                .Where(t => t.UserId == userId && t.ConsumedAt == null)
                .ToListAsync();
            _context.PairingTokens.RemoveRange(staleTokens);

            // Checked against every stored token (not just live ones) so the candidate can never
            // trip the unique index on Token.
            var token = await RegistrationCode.GenerateUniqueAsync(
                code => _context.PairingTokens.AnyAsync(t => t.Token == code), TokenLength);

            var pairingToken = new PairingToken
            {
                Token = token,
                UserId = userId!,
                ExpiresAt = DateTime.UtcNow.Add(TokenLifetime)
            };
            _context.PairingTokens.Add(pairingToken);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Pairing token minted {@LogData}", new { CallerUserId = userId, pairingToken.ExpiresAt, Superseded = staleTokens.Count });
            return Ok(new PairingTokenDto { Token = pairingToken.Token, ExpiresAt = pairingToken.ExpiresAt });
        }

        /// <summary>
        /// Redeems a pairing token for a parent device: claims every sensor under the parent name
        /// (and every switch its gateway discovered) for the token's user. Called by the operator
        /// service after the device sends the token over MQTT. Devices the user already has, and
        /// devices owned by another user, are skipped rather than stolen.
        /// </summary>
        [HttpPost("claim")]
        [Authorize(Roles = $"{RoleNames.Admin},{RoleNames.SensorAdmin},{RoleNames.SwitchAdmin}")]
        [SwaggerOperation(Summary = "Redeems a pairing token, claiming the parent device's sensors and switches for the token's user.")]
        [SwaggerResponse(200, "The claimed device ids.", typeof(PairingClaimResultDto))]
        [SwaggerResponse(400, "Token or parent name missing.")]
        [SwaggerResponse(404, "Unknown token, or no devices exist yet for the parent name.")]
        [SwaggerResponse(410, "Token expired or already consumed.")]
        public async Task<IActionResult> ClaimPairing([FromBody] ClaimPairingTokenDto dto)
        {
            _logger.LogInformation("ClaimPairing called by {@LogData}", new { CallerUserId = User.UserId(), dto.ParentName });

            if (string.IsNullOrWhiteSpace(dto.Token) || string.IsNullOrWhiteSpace(dto.ParentName))
            {
                _logger.LogWarning("ClaimPairing bad request: Token and parent name are required {@LogData}", new { CallerUserId = User.UserId() });
                return BadRequest(new { message = "Token and parent name are required." });
            }

            // Tokens are minted from an uppercase alphabet; accept any casing from the device.
            var normalizedToken = dto.Token.Trim().ToUpperInvariant();
            var parentName = dto.ParentName.Trim();

            var token = await _context.PairingTokens.FirstOrDefaultAsync(t => t.Token == normalizedToken);
            if (token == null)
            {
                _logger.LogWarning("ClaimPairing not found: Unknown pairing token {@LogData}", new { CallerUserId = User.UserId() });
                return NotFound(new { message = "Unknown pairing token." });
            }

            if (token.ConsumedAt != null)
            {
                _logger.LogWarning("ClaimPairing gone: Token already consumed {@LogData}", new { token.Id, token.ConsumedAt });
                return StatusCode(410, new { message = "Pairing token has already been used." });
            }

            if (token.ExpiresAt <= DateTime.UtcNow)
            {
                _logger.LogWarning("ClaimPairing gone: Token expired {@LogData}", new { token.Id, token.ExpiresAt });
                return StatusCode(410, new { message = "Pairing token has expired." });
            }

            var sensors = await _context.Sensors.Where(s => s.ParentName == parentName).ToListAsync();

            // Switches carry no parent name of their own; they hang off a gateway through the
            // discovered-device chain (DiscoveredBy = parent, Target = switch name), the same
            // relation the switch controller uses for indirect ownership.
            var switchNames = await _context.DiscoveredDevices
                .Where(dd => dd.DiscoveredBy == parentName)
                .Select(dd => dd.Target)
                .Distinct()
                .ToListAsync();
            var switches = await _context.Switches.Where(s => switchNames.Contains(s.Name)).ToListAsync();

            if (sensors.Count == 0 && switches.Count == 0)
            {
                // Distinct body so the operator can tell "retry later, devices not registered yet"
                // apart from an invalid token.
                _logger.LogWarning("ClaimPairing not found: No devices for parent {@LogData}", new { parentName });
                return NotFound(new { message = "No devices found for that parent name.", code = "devices-not-found" });
            }

            var result = new PairingClaimResultDto();

            foreach (var sensor in sensors)
            {
                var alreadyClaimed = await _context.UserSensors.AnyAsync(us => us.UserId == token.UserId && us.SensorId == sensor.Id);
                var ownedByOther = await _context.UserSensors.AnyAsync(us => us.SensorId == sensor.Id && us.IsOwner && us.UserId != token.UserId);
                if (alreadyClaimed || ownedByOther)
                {
                    result.Skipped++;
                    continue;
                }

                _context.UserSensors.Add(new UserSensor { UserId = token.UserId, SensorId = sensor.Id, IsOwner = true });

                // Open an ownership period that bounds which telemetry this user may read. The
                // first-ever owner starts at the epoch sentinel (sees all history); every later
                // (resale) owner starts now, so they never see the previous owner's readings.
                var firstEverOwner = !await _context.SensorOwnershipPeriods.AnyAsync(p => p.SensorId == sensor.Id);
                _context.SensorOwnershipPeriods.Add(new SensorOwnershipPeriod
                {
                    UserId = token.UserId,
                    SensorId = sensor.Id,
                    StartedAt = firstEverOwner ? SensorOwnershipPeriod.FirstOwnerStart : DateTime.UtcNow,
                    EndedAt = null
                });

                result.ClaimedSensorIds.Add(sensor.Id);
            }

            foreach (var switchEntity in switches)
            {
                var alreadyClaimed = await _context.UserSwitches.AnyAsync(us => us.UserId == token.UserId && us.SwitchId == switchEntity.Id);
                var ownedByOther = await _context.UserSwitches.AnyAsync(us => us.SwitchId == switchEntity.Id && us.IsOwner && us.UserId != token.UserId);
                if (alreadyClaimed || ownedByOther)
                {
                    result.Skipped++;
                    continue;
                }

                _context.UserSwitches.Add(new UserSwitch { UserId = token.UserId, SwitchId = switchEntity.Id, IsOwner = true });

                // Open a direct ownership period that bounds which telemetry this user may read. The
                // first-ever owner starts at the epoch sentinel (sees all history); every later
                // (resale) owner starts now, so they never see the previous owner's readings.
                var firstEverOwner = !await _context.SwitchOwnershipPeriods.AnyAsync(p => p.SwitchId == switchEntity.Id);
                _context.SwitchOwnershipPeriods.Add(new SwitchOwnershipPeriod
                {
                    UserId = token.UserId,
                    SwitchId = switchEntity.Id,
                    StartedAt = firstEverOwner ? SwitchOwnershipPeriod.FirstOwnerStart : DateTime.UtcNow,
                    EndedAt = null
                });

                result.ClaimedSwitchIds.Add(switchEntity.Id);
            }

            token.ConsumedAt = DateTime.UtcNow;
            token.ConsumedByParentName = parentName;

            await _context.SaveChangesAsync();

            foreach (var sensorId in result.ClaimedSensorIds)
            {
                _ownership.InvalidateSensor(sensorId);
                await _hub.Clients.Group(DeviceHub.UserGroup(token.UserId)).SendAsync("device-created", new { kind = "sensor", id = sensorId });
            }
            foreach (var switchId in result.ClaimedSwitchIds)
            {
                _ownership.InvalidateSwitch(switchId);
                await _hub.Clients.Group(DeviceHub.UserGroup(token.UserId)).SendAsync("device-created", new { kind = "switch", id = switchId });
            }

            _logger.LogInformation("ClaimPairing devices assigned to user {@LogData}", new
            {
                token.UserId,
                parentName,
                ClaimedSensors = result.ClaimedSensorIds.Count,
                ClaimedSwitches = result.ClaimedSwitchIds.Count,
                result.Skipped
            });
            return Ok(result);
        }

        /// <summary>Chip ids are the device MAC: exactly 12 lowercase hex characters.</summary>
        private static readonly Regex ChipIdRegex = new("^[0-9a-f]{12}$", RegexOptions.Compiled);

        /// <summary>Equivalent of the provisioning script's <c>secrets.token_urlsafe(16)</c>.</summary>
        private static string GenerateDevicePassword()
        {
            var bytes = RandomNumberGenerator.GetBytes(16);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>
        /// Exchanges a pairing token for per-device MQTT broker credentials. Anonymous:
        /// possession of a live pairing token is the authorization. The token is not consumed
        /// here — it stays redeemable for the subsequent <see cref="ClaimPairing"/> step.
        /// </summary>
        [HttpPost("provision")]
        [AllowAnonymous]
        [SwaggerOperation(Summary = "Exchanges a pairing token for per-device MQTT broker credentials.")]
        [SwaggerResponse(200, "The MQTT username and plaintext password (returned exactly once).", typeof(DeviceCredentialsDto))]
        [SwaggerResponse(400, "Chip id is not 12 hex characters.")]
        [SwaggerResponse(404, "Unknown token.")]
        [SwaggerResponse(409, "Device is owned by another user.")]
        [SwaggerResponse(410, "Token expired or already consumed.")]
        public async Task<IActionResult> ProvisionDevice([FromBody] ProvisionDeviceDto dto)
        {
            var chipId = dto.ChipId?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!ChipIdRegex.IsMatch(chipId))
            {
                _logger.LogWarning("ProvisionDevice bad request: Invalid chip id");
                return BadRequest(new { message = "ChipId must be 12 hexadecimal characters." });
            }
            var deviceName = $"garge_{chipId}";

            var normalizedToken = (dto.Token ?? string.Empty).Trim().ToUpperInvariant();
            var token = await _context.PairingTokens.FirstOrDefaultAsync(t => t.Token == normalizedToken);
            if (token == null)
            {
                _logger.LogWarning("ProvisionDevice not found: Unknown pairing token {@LogData}", new { deviceName });
                return NotFound(new { message = "Unknown pairing token." });
            }

            if (token.ConsumedAt != null)
            {
                _logger.LogWarning("ProvisionDevice gone: Token already consumed {@LogData}", new { token.Id, token.ConsumedAt, deviceName });
                return StatusCode(410, new { message = "Pairing token has already been used." });
            }

            if (token.ExpiresAt <= DateTime.UtcNow)
            {
                _logger.LogWarning("ProvisionDevice gone: Token expired {@LogData}", new { token.Id, token.ExpiresAt, deviceName });
                return StatusCode(410, new { message = "Pairing token has expired." });
            }

            // Rotating credentials for a device owned by someone else would let a stranger's
            // token hijack it; unclaimed devices (including first boot, no rows yet) may proceed.
            var sensorIds = await _context.Sensors
                .Where(s => s.ParentName == deviceName)
                .Select(s => s.Id)
                .ToListAsync();
            var sensorOwnedByOther = await _context.UserSensors
                .AnyAsync(us => sensorIds.Contains(us.SensorId) && us.IsOwner && us.UserId != token.UserId);

            // Switches have no parent name; they relate to the gateway via DiscoveredDevices.
            var switchNames = await _context.DiscoveredDevices
                .Where(dd => dd.DiscoveredBy == deviceName)
                .Select(dd => dd.Target)
                .Distinct()
                .ToListAsync();
            var switchIds = await _context.Switches
                .Where(s => switchNames.Contains(s.Name))
                .Select(s => s.Id)
                .ToListAsync();
            var switchOwnedByOther = await _context.UserSwitches
                .AnyAsync(us => switchIds.Contains(us.SwitchId) && us.IsOwner && us.UserId != token.UserId);

            if (sensorOwnedByOther || switchOwnedByOther)
            {
                _logger.LogWarning("ProvisionDevice conflict: Device owned by another user {@LogData}", new { deviceName, token.UserId });
                return Conflict(new { message = "Device is owned by another user." });
            }

            var password = GenerateDevicePassword();
            var salt = MqttPasswordHasher.GenerateSalt(16);
            var hash = MqttPasswordHasher.HashPasswordPBKDF2(password, salt);

            var brokerUser = await _context.EMQXMqttUsers.FirstOrDefaultAsync(u => u.Username == deviceName);
            var rotated = brokerUser != null;
            if (brokerUser == null)
            {
                // Broker superusers bypass all ACLs.
                brokerUser = new EMQXMqttUser { IsSuperuser = false, Username = deviceName };
                _context.EMQXMqttUsers.Add(brokerUser);
            }
            brokerUser.PasswordHash = hash;
            brokerUser.Salt = salt;

            // Two rows (retain 1 and 0) mirror the provisioning script; upsert is idempotent.
            var topic = $"garge/devices/{deviceName}/#";
            foreach (short retain in new short[] { 1, 0 })
            {
                var exists = await _context.EMQXMqttAcls.AnyAsync(a =>
                    a.Username == deviceName && a.Permission == "allow" && a.Action == "all" &&
                    a.Topic == topic && a.Qos == 0 && a.Retain == retain);
                if (!exists)
                {
                    _context.EMQXMqttAcls.Add(new EMQXMqttAcl
                    {
                        Username = deviceName,
                        Permission = "allow",
                        Action = "all",
                        Topic = topic,
                        Qos = 0,
                        Retain = retain
                    });
                }
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation("ProvisionDevice credentials issued {@LogData}", new
            {
                token.UserId,
                DeviceName = deviceName,
                Rotated = rotated
            });
            return Ok(new DeviceCredentialsDto { Username = deviceName, Password = password });
        }
    }
}
