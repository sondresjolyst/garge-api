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
    [Route("api/sensors/{sensorId}/activities")]
    [EnableCors("AllowAllOrigins")]
    [Authorize]
    public class SensorActivitiesController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IMapper _mapper;
        private readonly ILogger<SensorActivitiesController> _logger;
        private static readonly List<string> AdminRoles = new() { "SensorAdmin", "admin" };

        public SensorActivitiesController(
            ApplicationDbContext context,
            IMapper mapper,
            ILogger<SensorActivitiesController> logger)
        {
            _context = context;
            _mapper = mapper;
            _logger = logger;
        }

        private bool IsAdmin()
        {
            var userRoles = User.FindAll(ClaimTypes.Role).Select(r => r.Value).ToList();
            return userRoles.Any(role => AdminRoles.Contains(role, StringComparer.OrdinalIgnoreCase));
        }

        private async Task<bool> UserCanAccessSensorAsync(int sensorId)
        {
            if (IsAdmin()) return true;
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return await _context.UserSensors.AnyAsync(us => us.UserId == userId && us.SensorId == sensorId);
        }

        /// <summary>
        /// Lists activities logged for a sensor (e.g. motorcycle voltmeter), most recent first.
        /// </summary>
        [HttpGet]
        [SwaggerOperation(Summary = "Lists activities for a sensor.")]
        [SwaggerResponse(200, "The list of activities.", typeof(IEnumerable<SensorActivityDto>))]
        [SwaggerResponse(404, "Sensor not found.")]
        [SwaggerResponse(403, "User does not have access to this sensor.")]
        public async Task<IActionResult> GetActivities(int sensorId)
        {
            _logger.LogInformation("GetActivities called by {@LogData}", new { User = User.Identity?.Name, sensorId });

            var sensorExists = await _context.Sensors.AnyAsync(s => s.Id == sensorId);
            if (!sensorExists)
            {
                _logger.LogWarning("GetActivities not found: {@LogData}", new { sensorId });
                return NotFound(new { message = "Sensor not found!" });
            }

            if (!await UserCanAccessSensorAsync(sensorId))
            {
                _logger.LogWarning("GetActivities forbidden for {@LogData}", new { User = User.Identity?.Name, sensorId });
                return Forbid();
            }

            var activities = await _context.SensorActivities
                .Where(a => a.SensorId == sensorId)
                .OrderByDescending(a => a.ActivityDate)
                .ThenByDescending(a => a.CreatedAt)
                .ToListAsync();

            var dtos = _mapper.Map<IEnumerable<SensorActivityDto>>(activities);
            return Ok(dtos);
        }

        /// <summary>
        /// Retrieves a single activity by id.
        /// </summary>
        [HttpGet("{activityId}")]
        [SwaggerOperation(Summary = "Retrieves a single activity by id.")]
        [SwaggerResponse(200, "The activity.", typeof(SensorActivityDto))]
        [SwaggerResponse(404, "Sensor or activity not found.")]
        [SwaggerResponse(403, "User does not have access to this sensor.")]
        public async Task<IActionResult> GetActivity(int sensorId, int activityId)
        {
            var sensorExists = await _context.Sensors.AnyAsync(s => s.Id == sensorId);
            if (!sensorExists)
                return NotFound(new { message = "Sensor not found!" });

            if (!await UserCanAccessSensorAsync(sensorId))
                return Forbid();

            var activity = await _context.SensorActivities
                .FirstOrDefaultAsync(a => a.Id == activityId && a.SensorId == sensorId);

            if (activity == null)
                return NotFound(new { message = "Activity not found!" });

            return Ok(_mapper.Map<SensorActivityDto>(activity));
        }

        /// <summary>
        /// Creates a new activity entry for a sensor.
        /// </summary>
        [HttpPost]
        [SwaggerOperation(Summary = "Creates a new activity for a sensor.")]
        [SwaggerResponse(201, "The created activity.", typeof(SensorActivityDto))]
        [SwaggerResponse(400, "Bad request.")]
        [SwaggerResponse(404, "Sensor not found.")]
        [SwaggerResponse(403, "User does not have access to this sensor.")]
        public async Task<IActionResult> CreateActivity(int sensorId, [FromBody] CreateSensorActivityDto dto)
        {
            _logger.LogInformation("CreateActivity called by {@LogData}", new { User = User.Identity?.Name, sensorId });

            var sensorExists = await _context.Sensors.AnyAsync(s => s.Id == sensorId);
            if (!sensorExists)
                return NotFound(new { message = "Sensor not found!" });

            if (!await UserCanAccessSensorAsync(sensorId))
                return Forbid();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();

            if (string.IsNullOrWhiteSpace(dto.Title))
                return BadRequest(new { message = "Title is required." });

            var now = DateTime.UtcNow;
            var activity = new SensorActivity
            {
                SensorId = sensorId,
                UserId = userId,
                Title = dto.Title,
                Notes = dto.Notes,
                ActivityDate = (dto.ActivityDate ?? now).ToUniversalTime(),
                CreatedAt = now
            };

            _context.SensorActivities.Add(activity);
            await _context.SaveChangesAsync();

            var resultDto = _mapper.Map<SensorActivityDto>(activity);
            _logger.LogInformation("Activity created: {@LogData}", new { activity.Id, sensorId });
            return CreatedAtAction(nameof(GetActivity), new { sensorId, activityId = activity.Id }, resultDto);
        }

        /// <summary>
        /// Updates an existing activity. Only the user who created it (or an admin) may update.
        /// </summary>
        [HttpPut("{activityId}")]
        [SwaggerOperation(Summary = "Updates an activity.")]
        [SwaggerResponse(200, "The updated activity.", typeof(SensorActivityDto))]
        [SwaggerResponse(404, "Sensor or activity not found.")]
        [SwaggerResponse(403, "User does not have access to this sensor or did not create the activity.")]
        public async Task<IActionResult> UpdateActivity(int sensorId, int activityId, [FromBody] UpdateSensorActivityDto dto)
        {
            var sensorExists = await _context.Sensors.AnyAsync(s => s.Id == sensorId);
            if (!sensorExists)
                return NotFound(new { message = "Sensor not found!" });

            if (!await UserCanAccessSensorAsync(sensorId))
                return Forbid();

            var activity = await _context.SensorActivities
                .FirstOrDefaultAsync(a => a.Id == activityId && a.SensorId == sensorId);
            if (activity == null)
                return NotFound(new { message = "Activity not found!" });

            var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!IsAdmin() && activity.UserId != currentUserId)
                return Forbid();

            if (string.IsNullOrWhiteSpace(dto.Title))
                return BadRequest(new { message = "Title is required." });

            activity.Title = dto.Title;
            activity.Notes = dto.Notes;
            if (dto.ActivityDate.HasValue)
                activity.ActivityDate = dto.ActivityDate.Value.ToUniversalTime();
            activity.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return Ok(_mapper.Map<SensorActivityDto>(activity));
        }

        /// <summary>
        /// Deletes an activity. Only the user who created it (or an admin) may delete.
        /// </summary>
        [HttpDelete("{activityId}")]
        [SwaggerOperation(Summary = "Deletes an activity.")]
        [SwaggerResponse(204, "Activity deleted.")]
        [SwaggerResponse(404, "Sensor or activity not found.")]
        [SwaggerResponse(403, "User does not have access to this sensor or did not create the activity.")]
        public async Task<IActionResult> DeleteActivity(int sensorId, int activityId)
        {
            var sensorExists = await _context.Sensors.AnyAsync(s => s.Id == sensorId);
            if (!sensorExists)
                return NotFound(new { message = "Sensor not found!" });

            if (!await UserCanAccessSensorAsync(sensorId))
                return Forbid();

            var activity = await _context.SensorActivities
                .FirstOrDefaultAsync(a => a.Id == activityId && a.SensorId == sensorId);
            if (activity == null)
                return NotFound(new { message = "Activity not found!" });

            var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!IsAdmin() && activity.UserId != currentUserId)
                return Forbid();

            _context.SensorActivities.Remove(activity);
            await _context.SaveChangesAsync();

            return NoContent();
        }
    }
}
