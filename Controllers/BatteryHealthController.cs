using MapsterMapper;
using garge_api.Dtos.Sensor;
using garge_api.Helpers;
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
    [Authorize(Policy = "ActiveSubscription")]
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
            var userId = User.UserId();
            return await _context.UserSensors.AnyAsync(us => us.UserId == userId && us.SensorId == sensorId);
        }

        /// <summary>
        /// Deprecated: battery health is now computed server-side by
        /// <c>BatteryHealthAnalyzerService</c> from the voltage stream.
        /// Endpoint is kept temporarily so firmware/operator that still
        /// posts can do so without errors; the payload is ignored.
        /// </summary>
        [HttpPost("name/{sensorName}")]
        [SwaggerOperation(Summary = "Deprecated. Battery health is now computed server-side; payload ignored.", Tags = new[] { "Deprecated" })]
        [SwaggerResponse(200, "Acknowledged. No record written; analyzer runs from voltage stream.")]
        public IActionResult CreateBatteryHealth(string sensorName, [FromBody] CreateBatteryHealthDto _)
        {
            _logger.LogInformation("CreateBatteryHealth (deprecated) called for {SensorName}; ignoring payload — analyzer drives health from voltage.",
                LogSanitizer.Sanitize(sensorName));
            return Ok(new { message = "Battery health is computed server-side. This endpoint is a no-op." });
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
            _logger.LogInformation("GetLatestBatteryHealth called by {@LogData}", new { CallerUserId = User.UserId(), SensorName = LogSanitizer.Sanitize(sensorName) });

            var sensor = await _context.Sensors.FirstOrDefaultAsync(s => s.Name == sensorName);
            if (sensor == null)
            {
                _logger.LogWarning("GetLatestBatteryHealth voltage sensor not found: {SensorName}", LogSanitizer.Sanitize(sensorName));
                return NotFound(new { message = "Sensor not found!" });
            }

            if (!await UserCanAccessSensorAsync(sensor.Id))
            {
                _logger.LogWarning("GetLatestBatteryHealth forbidden for {@LogData}", new { CallerUserId = User.UserId(), SensorName = LogSanitizer.Sanitize(sensorName) });
                return Forbid();
            }

            var latest = await _context.BatteryHealthData
                .Where(bh => bh.SensorId == sensor.Id)
                .OrderByDescending(bh => bh.Timestamp)
                .FirstOrDefaultAsync();

            if (latest == null)
            {
                _logger.LogInformation("GetLatestBatteryHealth no data for sensor: {SensorName}", LogSanitizer.Sanitize(sensorName));
                return Ok((BatteryHealthDto?)null);
            }

            var dto = _mapper.Map<BatteryHealthDto>(latest);
            dto.CalibrationOffsetV = sensor.CalibrationOffsetV;
            return Ok(dto);
        }

        /// <summary>Returns detected charge events for a voltage sensor, optionally since a given timestamp.</summary>
        [HttpGet("name/{sensorName}/events")]
        [SwaggerOperation(Summary = "Returns detected battery charge events for a voltage sensor.")]
        [SwaggerResponse(200, "Charge events.", typeof(IEnumerable<BatteryChargeEventDto>))]
        public async Task<IActionResult> GetChargeEvents(string sensorName, [FromQuery] DateTime? since)
        {
            var sensor = await _context.Sensors.FirstOrDefaultAsync(s => s.Name == sensorName);
            if (sensor == null) return NotFound(new { message = "Sensor not found!" });
            if (!await UserCanAccessSensorAsync(sensor.Id)) return Forbid();

            var query = _context.BatteryChargeEvents.Where(e => e.SensorId == sensor.Id);
            if (since.HasValue) query = query.Where(e => e.StartedAt >= since.Value);
            var events = await query.OrderByDescending(e => e.StartedAt).ToListAsync();
            return Ok(_mapper.Map<List<BatteryChargeEventDto>>(events));
        }

        /// <summary>Stores a per-sensor calibration offset based on a multimeter reading at the moment of calibration.</summary>
        [HttpPost("name/{sensorName}/calibration")]
        [SwaggerOperation(Summary = "Calibrate a voltage sensor against a multimeter reading.")]
        [SwaggerResponse(200, "Calibration offset stored.")]
        public async Task<IActionResult> Calibrate(string sensorName, [FromBody] CalibrationDto dto)
        {
            var sensor = await _context.Sensors.FirstOrDefaultAsync(s => s.Name == sensorName);
            if (sensor == null) return NotFound(new { message = "Sensor not found!" });
            if (!await UserCanAccessSensorAsync(sensor.Id)) return Forbid();

            var latest = await _context.SensorData
                .Where(sd => sd.SensorId == sensor.Id)
                .OrderByDescending(sd => sd.Timestamp)
                .Select(sd => sd.Value)
                .FirstOrDefaultAsync();
            if (string.IsNullOrEmpty(latest) ||
                !float.TryParse(latest, System.Globalization.CultureInfo.InvariantCulture, out var sensorReading))
                return BadRequest(new { message = "No recent sensor reading to calibrate against." });

            sensor.CalibrationOffsetV = dto.MultimeterVoltage - sensorReading;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Calibration set for {SensorName}: offset={Offset:F3}V",
                LogSanitizer.Sanitize(sensorName), sensor.CalibrationOffsetV);
            return Ok(new { calibrationOffsetV = sensor.CalibrationOffsetV });
        }

        /// <summary>Clears a sensor's stored calibration offset.</summary>
        [HttpDelete("name/{sensorName}/calibration")]
        [SwaggerOperation(Summary = "Clear a voltage sensor's calibration offset.")]
        [SwaggerResponse(204, "Calibration cleared.")]
        public async Task<IActionResult> ClearCalibration(string sensorName)
        {
            var sensor = await _context.Sensors.FirstOrDefaultAsync(s => s.Name == sensorName);
            if (sensor == null) return NotFound(new { message = "Sensor not found!" });
            if (!await UserCanAccessSensorAsync(sensor.Id)) return Forbid();

            sensor.CalibrationOffsetV = null;
            await _context.SaveChangesAsync();
            return NoContent();
        }
    }
}
