using AutoMapper;
using garge_api.Dtos.Sensor;
using garge_api.Models;
using garge_api.Models.Sensor;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;
using System.Security.Claims;

namespace garge_api.Controllers
{
    [ApiController]
    [Route("api/battery-health")]
    [EnableCors("AllowAllOrigins")]
    [Authorize]
    public class BatteryHealthController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IMapper _mapper;
        private readonly ILogger<BatteryHealthController> _logger;
        private static readonly List<string> AdminRoles = new() { "SensorAdmin", "admin" };

        public BatteryHealthController(ApplicationDbContext context, IMapper mapper, ILogger<BatteryHealthController> logger)
        {
            _context = context;
            _mapper = mapper;
            _logger = logger;
        }

        private bool IsAdmin() =>
            User.FindAll(ClaimTypes.Role).Select(r => r.Value)
                .Any(r => AdminRoles.Contains(r, StringComparer.OrdinalIgnoreCase));

        private async Task<bool> UserCanAccessSensorAsync(int sensorId)
        {
            if (IsAdmin()) return true;
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return await _context.UserSensors.AnyAsync(us => us.UserId == userId && us.SensorId == sensorId);
        }

        private static string Sanitize(string input) => input.Replace("\r", "", StringComparison.Ordinal)
                                                              .Replace("\n", "", StringComparison.Ordinal);

        /// <summary>
        /// Stores a battery health reading for the voltage sensor identified by name.
        /// Called by the operator when a battery health MQTT state message arrives.
        /// </summary>
        [HttpPost("name/{sensorName}")]
        [SwaggerOperation(Summary = "Creates a battery health record for a voltage sensor by name.")]
        [SwaggerResponse(201, "The created battery health record.", typeof(BatteryHealthDto))]
        [SwaggerResponse(404, "Voltage sensor not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> CreateBatteryHealth(string sensorName, [FromBody] CreateBatteryHealthDto dto)
        {
            _logger.LogInformation("CreateBatteryHealth called by {@LogData}", new { User = User.Identity?.Name, SensorName = Sanitize(sensorName) });

            var sensor = await _context.Sensors.FirstOrDefaultAsync(s => s.Name == sensorName);
            if (sensor == null)
            {
                _logger.LogWarning("CreateBatteryHealth voltage sensor not found: {SensorName}", Sanitize(sensorName));
                return NotFound(new { message = "Sensor not found!" });
            }

            if (!await UserCanAccessSensorAsync(sensor.Id))
            {
                _logger.LogWarning("CreateBatteryHealth forbidden for {@LogData}", new { User = User.Identity?.Name, SensorName = Sanitize(sensorName) });
                return Forbid();
            }

            var record = _mapper.Map<BatteryHealth>(dto);
            record.SensorId = sensor.Id;
            record.Timestamp = DateTime.UtcNow;

            var previous = await _context.BatteryHealthData
                .Where(bh => bh.SensorId == sensor.Id)
                .OrderByDescending(bh => bh.Timestamp)
                .FirstOrDefaultAsync();

            if (previous != null && dto.ChargesRecorded > previous.ChargesRecorded)
                record.LastChargedAt = DateTime.UtcNow.AddHours(-4);
            else
                record.LastChargedAt = previous?.LastChargedAt;

            _context.BatteryHealthData.Add(record);
            await _context.SaveChangesAsync();

            var result = _mapper.Map<BatteryHealthDto>(record);
            _logger.LogInformation("Battery health record created: {@LogData}", new { record.Id, SensorName = Sanitize(sensorName), record.Status });
            return CreatedAtAction(nameof(GetLatestBatteryHealth), new { sensorName }, result);
        }

        /// <summary>
        /// Returns the latest battery health record for the voltage sensor identified by name.
        /// </summary>
        [HttpGet("name/{sensorName}/latest")]
        [SwaggerOperation(Summary = "Returns the latest battery health record for a voltage sensor by name.")]
        [SwaggerResponse(200, "The latest battery health record, or null if no data exists yet.", typeof(BatteryHealthDto))]
        [SwaggerResponse(404, "Voltage sensor not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> GetLatestBatteryHealth(string sensorName)
        {
            _logger.LogInformation("GetLatestBatteryHealth called by {@LogData}", new { User = User.Identity?.Name, SensorName = Sanitize(sensorName) });

            var sensor = await _context.Sensors.FirstOrDefaultAsync(s => s.Name == sensorName);
            if (sensor == null)
            {
                _logger.LogWarning("GetLatestBatteryHealth voltage sensor not found: {SensorName}", Sanitize(sensorName));
                return NotFound(new { message = "Sensor not found!" });
            }

            if (!await UserCanAccessSensorAsync(sensor.Id))
            {
                _logger.LogWarning("GetLatestBatteryHealth forbidden for {@LogData}", new { User = User.Identity?.Name, SensorName = Sanitize(sensorName) });
                return Forbid();
            }

            var latest = await _context.BatteryHealthData
                .Where(bh => bh.SensorId == sensor.Id)
                .OrderByDescending(bh => bh.Timestamp)
                .FirstOrDefaultAsync();

            if (latest == null)
            {
                _logger.LogInformation("GetLatestBatteryHealth no data for sensor: {SensorName}", Sanitize(sensorName));
                return Ok((BatteryHealthDto?)null);
            }

            return Ok(_mapper.Map<BatteryHealthDto>(latest));
        }
    }
}
