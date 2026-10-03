using garge_api.Constants;
using garge_api.Dtos.Mqtt;
using garge_api.Helpers;
using garge_api.Models;
using garge_api.Models.Mqtt;
using garge_api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Security.Claims;
using Swashbuckle.AspNetCore.Annotations;

namespace garge_api.Controllers
{
    [ApiController]
    [Route("api/mqtt")]
    [EnableCors("AllowAllOrigins")]
    [Authorize]
    public class MqttController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<MqttController> _logger;
        private readonly IMqttAclService _mqttAcls;
        private readonly IDeviceLeaseService _leases;
        private readonly IDeviceCommandService _commands;

        public MqttController(ApplicationDbContext context, ILogger<MqttController> logger, IMqttAclService mqttAcls, IDeviceLeaseService leases, IDeviceCommandService commands)
        {
            _context = context;
            _logger = logger;
            _mqttAcls = mqttAcls;
            _leases = leases;
            _commands = commands;
        }

        /// <summary>
        /// Creates a new EMQX MQTT user.
        /// </summary>
        /// <param name="dto">The user creation data.</param>
        /// <returns>The created user ID and username.</returns>
        [HttpPost("user")]
        [Authorize(Roles = $"{RoleNames.Admin},{RoleNames.MqttAdmin}")]
        [SwaggerOperation(Summary = "Creates a new EMQX MQTT user.")]
        [SwaggerResponse(200, "User created successfully.")]
        [SwaggerResponse(403, "Forbidden.")]
        [SwaggerResponse(409, "Username already exists.")]
        public async Task<IActionResult> CreateUser([FromBody] CreateEMQXMqttUserDto dto)
        {
            _logger.LogInformation("CreateUser called by {@LogData}", new { CallerUserId = User.UserId(), dto.Username });

            if (await _context.EMQXMqttUsers.AnyAsync(u => u.Username == dto.Username))
            {
                _logger.LogWarning("CreateUser conflict: Username {@LogData} already exists", new { dto.Username });
                return Conflict(new { message = "Username already exists." });
            }

            var salt = MqttPasswordHasher.GenerateSalt(16);
            var hash = MqttPasswordHasher.HashPasswordPBKDF2(dto.Password, salt);

            // IsSuperuser intentionally hardcoded to false. Broker superusers
            // bypass all ACLs; granting that flag must be done out-of-band
            // (DB migration / seed) by a platform admin, never via API.
            var user = new EMQXMqttUser
            {
                IsSuperuser = false,
                Username = dto.Username,
                PasswordHash = hash,
                Salt = salt
            };

            _context.EMQXMqttUsers.Add(user);
            await _context.SaveChangesAsync();

            _logger.LogInformation("EMQX MQTT user created: {@LogData}", new { user.Id, user.Username });
            return Ok(new { user.Id, user.Username });
        }

        /// <summary>
        /// Creates a new EMQX MQTT ACL entry.
        /// </summary>
        /// <param name="dto">The ACL creation data.</param>
        /// <returns>The created ACL ID.</returns>
        [HttpPost("acl")]
        [Authorize(Roles = $"{RoleNames.Admin},{RoleNames.MqttAdmin}")]
        [SwaggerOperation(Summary = "Creates a new EMQX MQTT ACL entry.")]
        [SwaggerResponse(200, "ACL created successfully.")]
        [SwaggerResponse(403, "Forbidden.")]
        [SwaggerResponse(404, "User not found.")]
        [SwaggerResponse(409, "ACL already exists for this combination.")]
        public async Task<IActionResult> CreateAcl([FromBody] CreateEMQXMqttAclDto dto)
        {
            _logger.LogInformation("CreateAcl called by {@LogData}", new
            {
                User = User.Identity?.Name,
                dto.Username,
                dto.Permission,
                dto.Action,
                dto.Topic,
                dto.Qos,
                dto.Retain
            });

            if (!await _context.EMQXMqttUsers.AnyAsync(u => u.Username == dto.Username))
            {
                _logger.LogWarning("CreateAcl user not found: {@LogData}", new { dto.Username });
                return NotFound(new { message = "User not found." });
            }

            var acl = new EMQXMqttAcl
            {
                Username = dto.Username,
                Permission = dto.Permission,
                Action = dto.Action,
                Topic = dto.Topic,
                Qos = dto.Qos,
                Retain = dto.Retain
            };

            _context.EMQXMqttAcls.Add(acl);

            try
            {
                await _context.SaveChangesAsync();
                _logger.LogInformation("ACL created: {@LogData}", new { acl.Id, acl.Username, acl.Topic });
                return Ok(new { acl.Id });
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException pgEx && pgEx.SqlState == "23505")
            {
                _logger.LogWarning("CreateAcl conflict: Duplicate ACL for {@LogData}", new { dto.Username, dto.Topic });
                return Conflict(new
                {
                    message = "ACL already exists for this user/topic/action/permission/qos/retain."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while creating ACL for {@LogData}", new { dto.Username });
                return StatusCode(500, new { message = "An unexpected error occurred while creating the ACL." });
            }
        }

        /// <summary>
        /// Registers a discovered device.
        /// </summary>
        /// <param name="dto">The discovered device data.</param>
        /// <returns>The created device ID.</returns>
        [HttpPost("discovered-device")]
        [Authorize(Roles = $"{RoleNames.Admin},{RoleNames.MqttAdmin}")]
        [SwaggerOperation(Summary = "Registers a discovered device.")]
        [SwaggerResponse(200, "Device registered successfully.")]
        [SwaggerResponse(403, "Forbidden.")]
        [SwaggerResponse(409, "Device already exists for this combination.")]
        public async Task<IActionResult> PostDiscoveredDevice([FromBody] CreateDiscoveredDeviceDto dto)
        {
            _logger.LogInformation("PostDiscoveredDevice called by {@LogData}", new
            {
                User = User.Identity?.Name,
                dto.DiscoveredBy,
                dto.Target,
                dto.Type,
                dto.Timestamp
            });

            var device = new DiscoveredDevice
            {
                DiscoveredBy = dto.DiscoveredBy,
                Target = dto.Target,
                Type = dto.Type,
                Timestamp = dto.Timestamp
            };

            var alreadyKnown = false;
            try
            {
                _context.DiscoveredDevices.Add(device);
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException pgEx && pgEx.SqlState == "23505")
            {
                _logger.LogWarning("PostDiscoveredDevice conflict: Device already exists for {@LogData}", new
                {
                    dto.DiscoveredBy,
                    dto.Target,
                    dto.Type
                });
                _context.Entry(device).State = EntityState.Detached;
                alreadyKnown = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while creating the discovered device for {@LogData}", new
                {
                    dto.DiscoveredBy,
                    dto.Target,
                    dto.Type
                });
                return StatusCode(500, new { message = "An unexpected error occurred while creating the discovered device." });
            }

            // Saved separately from the discovery row, and on the already-exists path too: a
            // device only rediscovers on its own schedule, and rediscovery is what renews its
            // lease, so a conflict here must not cost it the renewal.
            try
            {
                await GrantAclIfLeaseHolderAsync(dto.DiscoveredBy, dto.Target);
            }
            catch (Exception ex)
            {
                // The discovery row is already saved and the next report retries the lease, so
                // the caller is told the discovery landed rather than being failed outright.
                _logger.LogError(ex, "Error recording the device lease for {@LogData}", new
                {
                    dto.DiscoveredBy,
                    dto.Target
                });
            }

            if (alreadyKnown)
            {
                return Conflict(new { message = "Discovered device already exists for this combination." });
            }

            _logger.LogInformation("Discovered device created: {@LogData}", new
            {
                device.Id,
                device.DiscoveredBy,
                device.Target,
                device.Type
            });
            return Ok(new { device.Id });
        }

        /// <summary>
        /// Records the state a target device should be in. The operator delivers it and keeps
        /// reissuing until the device is observed to agree, because a command on a device's
        /// <c>/set</c> topic is not retained and is lost outright if no gateway is listening.
        /// </summary>
        [HttpPut("devices/{target}/desired-state")]
        [Authorize(Roles = $"{RoleNames.Admin},{RoleNames.MqttAdmin},{RoleNames.SwitchAdmin}")]
        [SwaggerOperation(Summary = "Records the state a target device should be in.")]
        [SwaggerResponse(200, "The recorded intent.", typeof(PendingDeviceCommandDto))]
        [SwaggerResponse(204, "The target is not a discovered device, so no intent is tracked.")]
        [SwaggerResponse(400, "State is missing.")]
        public async Task<IActionResult> PutDesiredState(string target, [FromBody] SetDeviceStateDto dto)
        {
            if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(dto.State))
            {
                return BadRequest(new { message = "Target and state are required." });
            }

            var intent = await _commands.SetDesiredStateAsync(target.Trim(), dto.State.Trim().ToUpperInvariant());
            if (intent == null)
            {
                // Not a discovered device, so it is published to directly and has no lease to
                // wait on; tracking it would leave a command pending for good.
                return NoContent();
            }

            return Ok(ToPendingDto(intent, await _leases.ControllerOfAsync(intent.Target)));
        }

        /// <summary>
        /// Records the state a target device was observed in. The controlling gateway reports
        /// this from the device's own push, so it reflects the device rather than what any gateway
        /// believes, and it is what settles an outstanding command.
        /// </summary>
        [HttpPut("devices/{target}/observed-state")]
        [Authorize(Roles = $"{RoleNames.Admin},{RoleNames.MqttAdmin}")]
        [SwaggerOperation(Summary = "Records the state a target device was observed in.")]
        [SwaggerResponse(200, "The updated intent.", typeof(PendingDeviceCommandDto))]
        [SwaggerResponse(204, "No command is outstanding for this device.")]
        [SwaggerResponse(400, "State is missing.")]
        public async Task<IActionResult> PutObservedState(string target, [FromBody] SetDeviceStateDto dto)
        {
            if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(dto.State))
            {
                return BadRequest(new { message = "Target and state are required." });
            }

            var intent = await _commands.RecordObservedStateAsync(target.Trim(), dto.State.Trim().ToUpperInvariant());
            if (intent == null)
            {
                return NoContent();
            }

            return Ok(ToPendingDto(intent, await _leases.ControllerOfAsync(intent.Target)));
        }

        /// <summary>
        /// The commands still waiting to be delivered, with the gateway allowed to carry each one.
        /// </summary>
        [HttpGet("devices/pending-commands")]
        [Authorize(Roles = $"{RoleNames.Admin},{RoleNames.MqttAdmin}")]
        [SwaggerOperation(Summary = "Lists the device commands still waiting to be delivered.")]
        [SwaggerResponse(200, "The outstanding commands.", typeof(IEnumerable<PendingDeviceCommandDto>))]
        public async Task<IActionResult> GetPendingCommands()
        {
            var pending = await _commands.PendingAsync();
            var result = new List<PendingDeviceCommandDto>(pending.Count);

            foreach (var intent in pending)
            {
                result.Add(ToPendingDto(intent, await _leases.ControllerOfAsync(intent.Target)));
            }

            return Ok(result);
        }

        /// <summary>
        /// Which targets each gateway may currently act on. A gateway that discovered a device
        /// but does not hold its lease is absent from its own list, which is how it knows to stay
        /// a standby: report the device, but publish nothing for it and answer no commands.
        /// </summary>
        [HttpGet("devices/controls")]
        [Authorize(Roles = $"{RoleNames.Admin},{RoleNames.MqttAdmin}")]
        [SwaggerOperation(Summary = "Lists the targets each gateway currently controls.")]
        [SwaggerResponse(200, "The controlled targets per gateway.", typeof(IEnumerable<DeviceControlListDto>))]
        public async Task<IActionResult> GetDeviceControls()
        {
            var leases = await _leases.LiveLeasesByControllerAsync();

            return Ok(leases
                .Select(pair => new DeviceControlListDto
                {
                    GatewayDeviceName = pair.Key,
                    Targets = pair.Value
                })
                .OrderBy(dto => dto.GatewayDeviceName, StringComparer.Ordinal)
                .ToList());
        }

        /// <summary>Counts one delivery attempt, so a command cannot be retried forever.</summary>
        [HttpPost("devices/{target}/command-attempt")]
        [Authorize(Roles = $"{RoleNames.Admin},{RoleNames.MqttAdmin}")]
        [SwaggerOperation(Summary = "Counts one delivery attempt for a device command.")]
        [SwaggerResponse(204, "Counted, or no command is outstanding.")]
        public async Task<IActionResult> PostCommandAttempt(string target)
        {
            await _commands.RecordAttemptAsync(target.Trim());
            return NoContent();
        }

        private static PendingDeviceCommandDto ToPendingDto(DeviceDesiredState intent, string? controller) => new()
        {
            Target = intent.Target,
            DesiredState = intent.DesiredState,
            ObservedState = intent.ObservedState,
            ControllerDeviceName = controller,
            Attempts = intent.Attempts,
            DesiredStateAt = intent.DesiredStateAt
        };

        /// <summary>
        /// Reports the gateway as seeing the target, which renews or takes the lease, and grants
        /// the broker rows only to whichever gateway ends up holding it. A standby is recorded as
        /// a candidate for a later handover but is given no access, so one device answers the
        /// target's command topic rather than several.
        /// </summary>
        private async Task GrantAclIfLeaseHolderAsync(string gatewayDeviceName, string target)
        {
            var controller = await _leases.ReportSeenAsync(gatewayDeviceName, target);
            if (controller != gatewayDeviceName)
            {
                _logger.LogInformation("Discovered device left to its lease holder {@LogData}",
                    new { DiscoveredBy = gatewayDeviceName, Target = target, Controller = controller });
                await _context.SaveChangesAsync();
                return;
            }

            await _mqttAcls.EnsureDiscoveredDeviceAclAsync(gatewayDeviceName, target);

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException pgEx && pgEx.SqlState == "23505")
            {
                // Two gateways reporting the same target for the first time both see no lease
                // and both insert one; the unique index on Target is what keeps a single holder,
                // so the loser simply becomes a standby. Its own rediscovery renews nothing and
                // the holder keeps the device.
                _logger.LogInformation("Device lease taken by another gateway first {@LogData}",
                    new { DiscoveredBy = gatewayDeviceName, Target = target });
                DetachStagedLeaseAndAcls();
            }
        }

        /// <summary>
        /// Drops the rows staged for a lease another gateway won. Left attached, they would be
        /// retried by the next save on this context and fail again.
        /// </summary>
        private void DetachStagedLeaseAndAcls()
        {
            foreach (var entry in _context.ChangeTracker.Entries()
                         .Where(e => e.State == EntityState.Added &&
                                     (e.Entity is DeviceController || e.Entity is EMQXMqttAcl))
                         .ToList())
            {
                entry.State = EntityState.Detached;
            }
        }
    }
}
