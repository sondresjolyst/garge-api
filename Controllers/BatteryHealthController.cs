using garge_api.Constants;
using garge_api.Dtos.Sensor;
using garge_api.Helpers;
using SjolystInnovation.AppKit.Hosting;
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
        private readonly ILogger<BatteryHealthController> _logger;
        private readonly BatteryHealthAnalyzerService _analyzer;
        private readonly BatteryHealthReadService _reader;
        private static readonly List<string> AdminRoles = new() { "SensorAdmin", "admin" };

        public BatteryHealthController(
            ApplicationDbContext context,
            ILogger<BatteryHealthController> logger,
            BatteryHealthAnalyzerService analyzer,
            BatteryHealthReadService reader)
        {
            _context = context;
            _logger = logger;
            _analyzer = analyzer;
            _reader = reader;
        }

        private bool IsAdmin() =>
            User.FindAll(ClaimTypes.Role).Select(r => r.Value)
                .Any(r => AdminRoles.Contains(r, StringComparer.OrdinalIgnoreCase));

        private async Task<bool> UserCanAccessSensorAsync(int sensorId)
        {
            if (IsAdmin()) return true;
            var userId = User.GetUserId();
            return await _context.UserSensors.AnyAsync(us => us.UserId == userId && us.SensorId == sensorId);
        }

        /// <summary>True when the caller has this owned sensor suspended (turned off / over quota). Admins are never suspended.</summary>
        private async Task<bool> IsSensorSuspendedForCallerAsync(int sensorId)
        {
            if (IsAdmin()) return false;
            var userId = User.GetUserId();
            return await _context.UserSensors.AnyAsync(us => us.UserId == userId && us.SensorId == sensorId && us.SuspendedAt != null);
        }

        /// <summary>
        /// Returns the latest battery health record for the voltage sensor identified by name. Status
        /// and the values next to it are worked out from the charges inside the caller's own ownership
        /// periods, so two viewers of one sensor can get different results.
        /// </summary>
        [HttpGet("name/{sensorName}/latest")]
        [SwaggerOperation(Summary = "Returns the latest battery health record for a voltage sensor by name.")]
        [SwaggerResponse(200, "The latest battery health record inside the caller's periods.", typeof(BatteryHealthDto))]
        [SwaggerResponse(204, "No health row exists inside the caller's periods yet. The response has no body.")]
        [SwaggerResponse(404, "Voltage sensor not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> GetLatestBatteryHealth(string sensorName)
        {
            _logger.LogInformation("GetLatestBatteryHealth called by {@LogData}", new { CallerUserId = User.GetUserId(), SensorName = LogSanitizer.Sanitize(sensorName) });

            var sensor = await _context.Sensors.FirstOrDefaultAsync(s => s.Name == sensorName);
            if (sensor == null)
            {
                _logger.LogWarning("GetLatestBatteryHealth voltage sensor not found: {SensorName}", LogSanitizer.Sanitize(sensorName));
                return NotFound(new { message = "Sensor not found!" });
            }

            if (!await UserCanAccessSensorAsync(sensor.Id))
            {
                _logger.LogWarning("GetLatestBatteryHealth forbidden for {@LogData}", new { CallerUserId = User.GetUserId(), SensorName = LogSanitizer.Sanitize(sensorName) });
                return Forbid();
            }

            if (await IsSensorSuspendedForCallerAsync(sensor.Id))
                return StatusCode(403, new { message = "Sensor is suspended. Re-subscribe or turn it back on to view its data.", suspended = true });

            var dto = await _reader.GetLatestAsync(sensor.Id, User.GetUserId(), IsAdmin(), HttpContext.RequestAborted);
            if (dto == null)
                _logger.LogInformation("GetLatestBatteryHealth no data for sensor: {SensorName}", LogSanitizer.Sanitize(sensorName));
            return Ok(dto);
        }

        /// <summary>Returns detected charge events inside the caller's ownership periods, optionally since a given timestamp.</summary>
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

            return Ok(await _reader.GetEventsAsync(sensor.Id, User.GetUserId(), IsAdmin(), since, HttpContext.RequestAborted));
        }

        /// <summary>
        /// Admin: rebuild every voltage sensor from its whole reading history.
        /// Replaces each sensor's charge events with a full replay and bypasses
        /// the 90-min staleness gate. An online sensor with a reading newer than its
        /// latest health row also gets a new row. Status comes from the rebuilt charges
        /// at read time. The same readings always give the same charge events.
        /// </summary>
        [HttpPost("admin/reanalyze")]
        [Authorize(Policy = "Admin")]
        [SwaggerOperation(Summary = "Admin: rebuild charge events and health for all voltage sensors from their whole history.")]
        [SwaggerResponse(200, "Processed counts.")]
        public async Task<IActionResult> ReanalyzeAll(CancellationToken ct)
        {
            var sensorIds = await _context.Sensors
                .Where(s => s.Type == SensorTypes.Voltage)
                .Select(s => s.Id)
                .ToListAsync(ct);

            var ok = 0;
            var failed = 0;
            foreach (var id in sensorIds)
            {
                try
                {
                    await _analyzer.ReanalyzeSensorAsync(id, ct);
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
        /// Admin: rebuild one voltage sensor from its whole reading history.
        /// Replaces its charge events with a full replay. An online sensor with a
        /// reading newer than its latest health row also gets a new row.
        /// </summary>
        [HttpPost("admin/reanalyze/{sensorId:int}")]
        [Authorize(Policy = "Admin")]
        [SwaggerOperation(Summary = "Admin: rebuild charge events and health for one voltage sensor from its whole history.")]
        [SwaggerResponse(200, "Reanalyzed.")]
        [SwaggerResponse(404, "Sensor not found or not a voltage sensor.")]
        public async Task<IActionResult> ReanalyzeOne(int sensorId, CancellationToken ct)
        {
            var exists = await _context.Sensors
                .AnyAsync(s => s.Id == sensorId && s.Type == SensorTypes.Voltage, ct);
            if (!exists) return NotFound(new { message = "Voltage sensor not found." });

            await _analyzer.ReanalyzeSensorAsync(sensorId, ct);
            return Ok(new { sensorId });
        }
    }
}
