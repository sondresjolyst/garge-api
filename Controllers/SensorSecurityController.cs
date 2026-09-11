using garge_api.Constants;
using garge_api.Dtos.Sensor;
using garge_api.Helpers;
using garge_api.Hubs;
using garge_api.Models;
using garge_api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;

namespace garge_api.Controllers
{
    /// <summary>
    /// Garge Security endpoints. Callers without the GargeSecurity permission get 404 on every
    /// user-facing endpoint, so the feature is invisible to them.
    /// </summary>
    [ApiController]
    [Route("api/sensors")]
    [EnableCors("AllowAllOrigins")]
    [Authorize]
    public class SensorSecurityController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IDeviceOwnershipService _ownership;
        private readonly IPermissionService _permissions;
        private readonly ISecurityModeService _security;
        private readonly ILogger<SensorSecurityController> _logger;

        public SensorSecurityController(
            ApplicationDbContext context,
            IDeviceOwnershipService ownership,
            IPermissionService permissions,
            ISecurityModeService security,
            ILogger<SensorSecurityController> logger)
        {
            _context = context;
            _ownership = ownership;
            _permissions = permissions;
            _security = security;
            _logger = logger;
        }

        private async Task<(bool Visible, bool IsOwner)> ResolveCallerAsync(string? userId, int sensorId, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(userId)) return (false, false);
            if (!await _context.Sensors.AnyAsync(s => s.Id == sensorId, ct)) return (false, false);
            if (!await _permissions.HasPermissionAsync(userId, PermissionNames.GargeSecurity, ct)) return (false, false);
            if (!await _ownership.CanUserAccessSensorAsync(userId, sensorId, ct)) return (false, false);

            var isOwner = await _context.UserSensors.AnyAsync(us => us.UserId == userId && us.SensorId == sensorId && us.IsOwner, ct);
            return (true, isOwner);
        }

        /// <summary>
        /// Gets the Garge Security state for a sensor.
        /// </summary>
        [HttpGet("{sensorId:int}/security")]
        [SwaggerOperation(Summary = "Gets the Garge Security state for a sensor.")]
        [SwaggerResponse(200, "Security state.", typeof(SensorSecurityDto))]
        [SwaggerResponse(404, "Not found.")]
        public async Task<IActionResult> GetSecurity(int sensorId, CancellationToken ct = default)
        {
            var userId = User.UserId();
            var (visible, isOwner) = await ResolveCallerAsync(userId, sensorId, ct);
            if (!visible) return NotFound();

            return Ok(await _security.GetAsync(sensorId, userId!, isOwner, ct));
        }

        /// <summary>
        /// Turns Garge Security on or off for a sensor, and sets the alert threshold. Owner only.
        /// Turning it on requires an enabled charging automation on the sensor and at least one alert channel.
        /// </summary>
        [HttpPatch("{sensorId:int}/security")]
        [SwaggerOperation(Summary = "Turns Garge Security on or off for a sensor.")]
        [SwaggerResponse(200, "Security state.", typeof(SensorSecurityDto))]
        [SwaggerResponse(400, "Invalid threshold or no charging automation.")]
        [SwaggerResponse(403, "Not the sensor owner.")]
        [SwaggerResponse(404, "Not found.")]
        [SwaggerResponse(409, "No alert channel enabled.")]
        public async Task<IActionResult> UpdateSecurity(int sensorId, [FromBody] UpdateSensorSecurityDto dto, CancellationToken ct = default)
        {
            var userId = User.UserId();
            _logger.LogInformation("UpdateSecurity called by {@LogData}", new { CallerUserId = userId, sensorId, dto.Enabled, dto.ThresholdMinutes });

            var (visible, isOwner) = await ResolveCallerAsync(userId, sensorId, ct);
            if (!visible) return NotFound();
            if (!isOwner) return Forbid();

            var result = await _security.SetAsync(userId!, sensorId, dto.Enabled, dto.ThresholdMinutes, ct);
            switch (result)
            {
                case SecuritySetResult.UnsupportedSensor:
                    return BadRequest(new
                    {
                        code = SecurityMode.ErrorCodes.UnsupportedSensor,
                        message = "Garge Security is only available on battery voltage sensors."
                    });
                case SecuritySetResult.InvalidThreshold:
                    return BadRequest(new
                    {
                        code = SecurityMode.ErrorCodes.InvalidThreshold,
                        message = $"The alert threshold must be between {SecurityMode.MinThresholdMinutes} and {SecurityMode.MaxThresholdMinutes} minutes."
                    });
                case SecuritySetResult.ChargingAutomationRequired:
                    return BadRequest(new
                    {
                        code = SecurityMode.ErrorCodes.ChargingAutomationRequired,
                        message = "Garge Security needs an enabled automation that turns on a charger socket when this battery gets low."
                    });
                case SecuritySetResult.NoAlertChannel:
                    return Conflict(new
                    {
                        code = SecurityMode.ErrorCodes.NoAlertChannel,
                        message = "Turn on push or email notifications in your profile before enabling Garge Security."
                    });
            }

            return Ok(await _security.GetAsync(sensorId, userId!, isOwner, ct));
        }

        /// <summary>
        /// Turns Garge Security off for a sensor and removes its settings. Owner only.
        /// </summary>
        [HttpDelete("{sensorId:int}/security")]
        [SwaggerOperation(Summary = "Turns Garge Security off for a sensor.")]
        [SwaggerResponse(204, "Removed.")]
        [SwaggerResponse(403, "Not the sensor owner.")]
        [SwaggerResponse(404, "Not found.")]
        public async Task<IActionResult> RemoveSecurity(int sensorId, CancellationToken ct = default)
        {
            var userId = User.UserId();
            _logger.LogInformation("RemoveSecurity called by {@LogData}", new { CallerUserId = userId, sensorId });

            var (visible, isOwner) = await ResolveCallerAsync(userId, sensorId, ct);
            if (!visible) return NotFound();
            if (!isOwner) return Forbid();

            await _security.RemoveAsync(userId!, sensorId, ct);
            return NoContent();
        }

        /// <summary>
        /// Records the settings a device reports it is running (from its MQTT config). Called by garge-operator.
        /// </summary>
        [HttpPost("name/{sensorName}/reported-settings")]
        [Authorize(Roles = $"{RoleNames.Admin},{RoleNames.SensorAdmin}")]
        [SwaggerOperation(Summary = "Records the settings a device reports it is running.")]
        [SwaggerResponse(204, "Recorded.")]
        [SwaggerResponse(404, "Sensor not found.")]
        public async Task<IActionResult> ReportSettings(string sensorName, [FromBody] ReportedSettingsDto dto, CancellationToken ct = default)
        {
            var found = await _security.ApplyAckAsync(sensorName, dto.SleepSeconds, dto.SecurityEnabled, dto.Version, ct);
            if (!found)
            {
                _logger.LogWarning("ReportSettings not found: {@LogData}", new { sensorName = LogSanitizer.Sanitize(sensorName) });
                return NotFound(new { message = "Sensor not found!" });
            }
            return NoContent();
        }

        /// <summary>
        /// Lists the current settings for every device that has Garge Security state, one entry per device.
        /// Called by garge-operator on startup to republish retained settings.
        /// </summary>
        [HttpGet("device-settings")]
        [Authorize(Roles = $"{RoleNames.Admin},{DeviceHub.BridgeRole}")]
        [SwaggerOperation(Summary = "Lists device settings for garge-operator.")]
        [SwaggerResponse(200, "Device settings.", typeof(IEnumerable<DeviceSettingsDto>))]
        public async Task<IActionResult> GetDeviceSettings(CancellationToken ct = default)
            => Ok(await _security.GetDeviceSettingsAsync(ct));
    }
}
