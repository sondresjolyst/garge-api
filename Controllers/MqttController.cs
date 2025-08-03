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

        [HttpPost("user")]
        public async Task<IActionResult> CreateUser([FromBody] CreateEMQXMqttUserDto dto)
        {
            _logger.LogInformation("CreateUser called by {User} for Username={Username}, IsSuperuser={IsSuperuser}",
                User.Identity?.Name,
                dto.Username,
                dto.IsSuperuser);

            if (!UserHasRequiredRole())
            {
                _logger.LogWarning("CreateUser forbidden for {User}", User.Identity?.Name);
                return Forbid();
            }

            if (await _context.EMQXMqttUsers.AnyAsync(u => u.Username == dto.Username))
            {
                _logger.LogWarning("CreateUser conflict: Username {Username} already exists", dto.Username);
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

            _logger.LogInformation("EMQX MQTT user created: Id={Id}, Username={Username}", user.Id, user.Username);
            return Ok(new { user.Id, user.Username });
        }

        [HttpPost("acl")]
        public async Task<IActionResult> CreateAcl([FromBody] CreateEMQXMqttAclDto dto)
        {
            _logger.LogInformation("CreateAcl called by {User} for Username={Username}, Permission={Permission}, Action={Action}, Topic={Topic}, Qos={Qos}, Retain={Retain}",
                User.Identity?.Name,
                dto.Username,
                dto.Permission,
                dto.Action,
                dto.Topic,
                dto.Qos,
                dto.Retain);

            if (!UserHasRequiredRole())
            {
                _logger.LogWarning("CreateAcl forbidden for {User}", User.Identity?.Name);
                return Forbid();
            }

            if (!await _context.EMQXMqttUsers.AnyAsync(u => u.Username == dto.Username))
            {
                _logger.LogWarning("CreateAcl user not found: {Username}", dto.Username);
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
                    acl.Id,
                    acl.Username,
                    acl.Topic);
                return Ok(new { acl.Id });
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException pgEx && pgEx.SqlState == "23505")
            {
                _logger.LogWarning("CreateAcl conflict: Duplicate ACL for Username={Username}, Topic={Topic}",
                    dto.Username,
                    dto.Topic);
                return Conflict(new
                {
                    message = "ACL already exists for this user/topic/action/permission/qos/retain."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while creating ACL for Username={Username}",
                    dto.Username);
                return StatusCode(500, new { message = "An unexpected error occurred while creating the ACL." });
            }
        }

        [HttpPost("discovered-device")]
        public async Task<IActionResult> PostDiscoveredDevice([FromBody] CreateDiscoveredDeviceDto dto)
        {
            _logger.LogInformation("PostDiscoveredDevice called by {User} for DiscoveredBy={DiscoveredBy}, Target={Target}, Type={Type}, Timestamp={Timestamp}",
                User.Identity?.Name,
                dto.DiscoveredBy,
                dto.Target,
                dto.Type,
                dto.Timestamp);

            if (!UserHasRequiredRole())
            {
                _logger.LogWarning("PostDiscoveredDevice forbidden for {User}", User.Identity?.Name);
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
                    dto.DiscoveredBy,
                    dto.Target,
                    dto.Type);
                return Conflict(new { message = "Discovered device already exists for this combination." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while creating the discovered device for DiscoveredBy={DiscoveredBy}, Target={Target}, Type={Type}",
                    dto.DiscoveredBy,
                    dto.Target,
                    dto.Type);
                return StatusCode(500, new { message = "An unexpected error occurred while creating the discovered device." });
            }
        }
    }
}
