using MapsterMapper;
using garge_api.Dtos.Sensor;
using garge_api.Helpers;
using garge_api.Models;
using garge_api.Models.Sensor;
using garge_api.Services;
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
        private readonly BatteryHealthAnalyzerService _analyzer;
        private static readonly List<string> AdminRoles = new() { "SensorAdmin", "admin" };

        public BatteryHealthController(
            ApplicationDbContext context,
            IMapper mapper,
            ILogger<BatteryHealthController> logger,
            BatteryHealthAnalyzerService analyzer)
        {
            _context = context;
            _mapper = mapper;
            _logger = logger;
            _analyzer = analyzer;
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

        // Bound derived battery data to the caller's own ownership window(s) so a new owner of a
        // re-claimed/resold sensor never sees the previous owner's history. Admins see everything.
        private IQueryable<BatteryHealth> WithinOwnershipWindow(IQueryable<BatteryHealth> query)
            => query.WithinSensorOwnership(_context, User.UserId(), IsAdmin());

        private IQueryable<BatteryChargeEvent> WithinOwnershipWindow(IQueryable<BatteryChargeEvent> query)
            => query.WithinSensorOwnership(_context, User.UserId(), IsAdmin());

        private IQueryable<SensorData> WithinOwnershipWindow(IQueryable<SensorData> query)
            => query.WithinSensorOwnership(_context, User.UserId(), IsAdmin());

        /// <summary>True when the caller has this owned sensor suspended (turned off / over quota). Admins are never suspended.</summary>
        private async Task<bool> IsSensorSuspendedForCallerAsync(int sensorId)
        {
            if (IsAdmin()) return false;
            var userId = User.UserId();
            return await _context.UserSensors.AnyAsync(us => us.UserId == userId && us.SensorId == sensorId && us.SuspendedAt != null);
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

            if (await IsSensorSuspendedForCallerAsync(sensor.Id))
                return StatusCode(403, new { message = "Sensor is suspended. Re-subscribe or turn it back on to view its data.", suspended = true });

            var latest = await WithinOwnershipWindow(
                    _context.BatteryHealthData.Where(bh => bh.SensorId == sensor.Id))
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
            if (await IsSensorSuspendedForCallerAsync(sensor.Id))
                return StatusCode(403, new { message = "Sensor is suspended. Re-subscribe or turn it back on to view its data.", suspended = true });

            var query = WithinOwnershipWindow(_context.BatteryChargeEvents.Where(e => e.SensorId == sensor.Id));
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

            var latest = await WithinOwnershipWindow(
                    _context.SensorData.Where(sd => sd.SensorId == sensor.Id))
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

        /// <summary>
        /// Admin: force-reanalyze every voltage sensor. Bypasses the 90-min
        /// staleness gate used by the background sweep and the per-sensor
        /// NOTIFY trigger. Useful after deploying a new analyzer version so
        /// the UI doesn't show pre-fix data until each sensor publishes its
        /// next reading. Idempotent.
        /// </summary>
        [HttpPost("admin/reanalyze")]
        [Authorize(Policy = "Admin")]
        [SwaggerOperation(Summary = "Admin: re-run the analyzer for all voltage sensors.")]
        [SwaggerResponse(200, "Processed counts.")]
        public async Task<IActionResult> ReanalyzeAll(CancellationToken ct)
        {
            var sensorIds = await _context.Sensors
                .Where(s => s.Type == "voltage")
                .Select(s => s.Id)
                .ToListAsync(ct);

            var ok = 0;
            var failed = 0;
            foreach (var id in sensorIds)
            {
                try
                {
                    await _analyzer.AnalyzeSensorAsync(id, ct);
                    ok++;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogWarning(ex, "Reanalyze failed for sensor {SensorId}", id);
                }
            }

            _logger.LogInformation("BatteryHealth ReanalyzeAll: ok={Ok} failed={Failed} total={Total}", ok, failed, sensorIds.Count);
            return Ok(new { processed = ok, failed, total = sensorIds.Count });
        }

        /// <summary>
        /// Admin: force-reanalyze a single voltage sensor by id.
        /// </summary>
        [HttpPost("admin/reanalyze/{sensorId:int}")]
        [Authorize(Policy = "Admin")]
        [SwaggerOperation(Summary = "Admin: re-run the analyzer for one voltage sensor.")]
        [SwaggerResponse(200, "Reanalyzed.")]
        [SwaggerResponse(404, "Sensor not found or not a voltage sensor.")]
        public async Task<IActionResult> ReanalyzeOne(int sensorId, CancellationToken ct)
        {
            var exists = await _context.Sensors
                .AnyAsync(s => s.Id == sensorId && s.Type == "voltage", ct);
            if (!exists) return NotFound(new { message = "Voltage sensor not found." });

            await _analyzer.AnalyzeSensorAsync(sensorId, ct);
            return Ok(new { sensorId });
        }
    }
}
