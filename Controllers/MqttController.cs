using garge_api.Dtos.Mqtt;
using garge_api.Models;
using garge_api.Models.Mqtt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using garge_api.Services;

namespace garge_api.Controllers
{
    [ApiController]
    [Route("api/mqtt")]
    [EnableCors("AllowAllOrigins")]
    [Authorize]
    public class MqttController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private static readonly List<string> AdminRoles = new() { "mqtt_admin", "admin" };
        private readonly ILogger<MqttController> _logger;

        public MqttController(ApplicationDbContext context, ILogger<MqttController> logger)
        {
            _context = context;
            _logger = logger;
        }

        private bool UserHasRequiredRole()
        {
            var userRoles = User.FindAll(ClaimTypes.Role).Select(r => r.Value).ToList();
            return userRoles.Any(role => AdminRoles.Contains(role, StringComparer.OrdinalIgnoreCase));
        }

        private static string GenerateSalt(int length = 16)
        {
            var saltBytes = new byte[length];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(saltBytes);
            return Convert.ToHexString(saltBytes).ToLowerInvariant();
        }

        private static string HashPasswordPBKDF2(string password, string salt, int iterations = 300_000, int hashByteSize = 32)
        {
            var saltBytes = Encoding.UTF8.GetBytes(salt);
            using var pbkdf2 = new Rfc2898DeriveBytes(password, saltBytes, iterations, HashAlgorithmName.SHA512);
            var hash = pbkdf2.GetBytes(hashByteSize);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Creates a new EMQX MQTT user with a securely hashed password.
        /// </summary>
        /// <param name="dto">The user creation data, including username, password, and superuser flag.</param>
        /// <returns>Returns the created user's ID and username, or a conflict if the username already exists.</returns>
        [HttpPost("user")]
        public async Task<IActionResult> CreateUser([FromBody] CreateEMQXMqttUserDto dto)
        {
            _logger.LogInformation("CreateUser called by {User} for Username={Username}, IsSuperuser={IsSuperuser}",
                LogSanitizer.Sanitize(User.Identity?.Name),
                LogSanitizer.Sanitize(dto.Username),
                dto.IsSuperuser);

            if (!UserHasRequiredRole())
            {
                _logger.LogWarning("CreateUser forbidden for {User}", LogSanitizer.Sanitize(User.Identity?.Name));
                return Forbid();
            }

            if (await _context.EMQXMqttUsers.AnyAsync(u => u.Username == dto.Username))
            {
                _logger.LogWarning("CreateUser conflict: Username {Username} already exists", LogSanitizer.Sanitize(dto.Username));
                return Conflict(new { message = "Username already exists." });
            }

            var salt = GenerateSalt(16);
            var hash = HashPasswordPBKDF2(dto.Password, salt);

            var user = new EMQXMqttUser
            {
                IsSuperuser = dto.IsSuperuser,
                Username = dto.Username,
                PasswordHash = hash,
                Salt = salt
            };

            _context.EMQXMqttUsers.Add(user);
            await _context.SaveChangesAsync();

            _logger.LogInformation("EMQX MQTT user created: Id={Id}, Username={Username}", LogSanitizer.Sanitize(user.Id.ToString()), LogSanitizer.Sanitize(user.Username));
            return Ok(new { user.Id, user.Username });
        }

        /// <summary>
        /// Grants an ACL (Access Control List) entry for a specific MQTT user.
        /// </summary>
        /// <param name="dto">The ACL creation data, including username, permission, action, topic, QoS, and retain flag.</param>
        /// <returns>
        /// Returns the created ACL's ID, a not found error if the user does not exist,
        /// or a conflict if an identical ACL already exists.
        /// </returns>
        [HttpPost("acl")]
        public async Task<IActionResult> CreateAcl([FromBody] CreateEMQXMqttAclDto dto)
        {
            _logger.LogInformation("CreateAcl called by {User} for Username={Username}, Permission={Permission}, Action={Action}, Topic={Topic}, Qos={Qos}, Retain={Retain}",
                LogSanitizer.Sanitize(User.Identity?.Name),
                LogSanitizer.Sanitize(dto.Username),
                LogSanitizer.Sanitize(dto.Permission),
                LogSanitizer.Sanitize(dto.Action),
                LogSanitizer.Sanitize(dto.Topic),
                dto.Qos,
                dto.Retain);

            if (!UserHasRequiredRole())
            {
                _logger.LogWarning("CreateAcl forbidden for {User}", LogSanitizer.Sanitize(User.Identity?.Name));
                return Forbid();
            }

            if (!await _context.EMQXMqttUsers.AnyAsync(u => u.Username == dto.Username))
            {
                _logger.LogWarning("CreateAcl user not found: {Username}", LogSanitizer.Sanitize(dto.Username));
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
                _logger.LogInformation("ACL created: Id={Id}, Username={Username}, Topic={Topic}",
                    LogSanitizer.Sanitize(acl.Id.ToString()),
                    LogSanitizer.Sanitize(acl.Username),
                    LogSanitizer.Sanitize(acl.Topic));
                return Ok(new { acl.Id });
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException pgEx && pgEx.SqlState == "23505")
            {
                _logger.LogWarning("CreateAcl conflict: Duplicate ACL for Username={Username}, Topic={Topic}",
                    LogSanitizer.Sanitize(dto.Username),
                    LogSanitizer.Sanitize(dto.Topic));
                return Conflict(new
                {
                    message = "ACL already exists for this user/topic/action/permission/qos/retain."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while creating ACL for Username={Username}",
                    LogSanitizer.Sanitize(dto.Username));
                return StatusCode(500, new { message = "An unexpected error occurred while creating the ACL." });
            }
        }

        /// <summary>
        /// Posts a discovered device, including discoverer, target device, type, and timestamp.
        /// </summary>
        /// <param name="dto">The discovered device data.</param>
        /// <returns>Returns the created discovered device's ID.</returns>
        [HttpPost("discovered-device")]
        public async Task<IActionResult> PostDiscoveredDevice([FromBody] CreateDiscoveredDeviceDto dto)
        {
            _logger.LogInformation("PostDiscoveredDevice called by {User} for DiscoveredBy={DiscoveredBy}, Target={Target}, Type={Type}, Timestamp={Timestamp}",
                LogSanitizer.Sanitize(User.Identity?.Name),
                LogSanitizer.Sanitize(dto.DiscoveredBy),
                LogSanitizer.Sanitize(dto.Target),
                LogSanitizer.Sanitize(dto.Type),
                dto.Timestamp);

            if (!UserHasRequiredRole())
            {
                _logger.LogWarning("PostDiscoveredDevice forbidden for {User}", LogSanitizer.Sanitize(User.Identity?.Name));
                return Forbid();
            }

            var device = new DiscoveredDevice
            {
                DiscoveredBy = dto.DiscoveredBy,
                Target = dto.Target,
                Type = dto.Type,
                Timestamp = dto.Timestamp
            };

            try
            {
                _context.DiscoveredDevices.Add(device);
                await _context.SaveChangesAsync();
                _logger.LogInformation(
                    "Discovered device created: Id={Id}, DiscoveredBy={DiscoveredBy}, Target={Target}, Type={Type}",
                    device.Id,
                    device.DiscoveredBy,
                    device.Target,
                    device.Type);
                return Ok(new { device.Id });
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException pgEx && pgEx.SqlState == "23505")
            {
                _logger.LogWarning("PostDiscoveredDevice conflict: Device already exists for DiscoveredBy={DiscoveredBy}, Target={Target}, Type={Type}",
                    LogSanitizer.Sanitize(dto.DiscoveredBy),
                    LogSanitizer.Sanitize(dto.Target),
                    LogSanitizer.Sanitize(dto.Type));
                return Conflict(new { message = "Discovered device already exists for this combination." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while creating the discovered device for DiscoveredBy={DiscoveredBy}, Target={Target}, Type={Type}",
                    LogSanitizer.Sanitize(dto.DiscoveredBy),
                    LogSanitizer.Sanitize(dto.Target),
                    LogSanitizer.Sanitize(dto.Type));
                return StatusCode(500, new { message = "An unexpected error occurred while creating the discovered device." });
            }
        }
    }
}
