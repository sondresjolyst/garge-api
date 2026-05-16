using garge_api.Controllers.Common;
using garge_api.Dtos.Common;
using garge_api.Dtos.Sensor;
using garge_api.Hubs;
using garge_api.Models;
using garge_api.Models.Sensor;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Swashbuckle.AspNetCore.Annotations;
using System.Globalization;
using System.Security.Claims;
using MapsterMapper;
using garge_api.Services;

namespace garge_api.Controllers
{
    [ApiController]
    [Route("api/sensors")]
    [EnableCors("AllowAllOrigins")]
    [Authorize]
    [Authorize(Policy = "ActiveSubscription")]
    public class SensorController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IMapper _mapper;
        private readonly ILogger<SensorController> _logger;
        private readonly IDeviceOwnershipService _ownership;
        private readonly IHubContext<DeviceHub> _hub;
        private static readonly List<string> AdminRoles = new() { "SensorAdmin", "admin" };

        public SensorController(ApplicationDbContext context, IMapper mapper, ILogger<SensorController> logger, IDeviceOwnershipService ownership, IHubContext<DeviceHub> hub)
        {
            _context = context;
            _mapper = mapper;
            _logger = logger;
            _ownership = ownership;
            _hub = hub;
        }

        private bool IsAdmin()
        {
            var userRoles = User.FindAll(ClaimTypes.Role).Select(r => r.Value).ToList();
            return userRoles.Any(role => AdminRoles.Contains(role, StringComparer.OrdinalIgnoreCase));
        }

        private async Task<bool> UserCanAccessSensorAsync(int sensorId)
        {
            if (IsAdmin()) return true;
            var userId = User.UserId();
            if (string.IsNullOrEmpty(userId)) return false;
            // Delegate to the shared ownership service.
            return await _ownership.CanUserAccessSensorAsync(userId, sensorId);
        }

        /// <summary>
        /// Retrieves all sensors the user has access to.
        /// </summary>
        /// <returns>A list of sensors the user has access to.</returns>
        [HttpGet]
        [SwaggerOperation(Summary = "Retrieves all available sensors.")]
        [SwaggerResponse(200, "A list of all sensors.", typeof(IEnumerable<SensorDto>))]
        public async Task<IActionResult> GetAllSensors()
        {
            _logger.LogInformation("GetAllSensors called by {@LogData}", new { CallerUserId = User.UserId() });

            var currentUserId = User.UserId();

            List<Sensor> sensors;
            if (IsAdmin())
            {
                sensors = await _context.Sensors.ToListAsync();
            }
            else
            {
                sensors = await _context.Sensors
                    .Where(s => _context.UserSensors.Any(us => us.UserId == currentUserId && us.SensorId == s.Id))
                    .ToListAsync();
            }

            // Fetch all custom names for the current user
            var customNames = await _context.UserSensorCustomNames
                .Where(x => x.UserId == currentUserId)
                .ToDictionaryAsync(x => x.SensorId, x => x.CustomName);

            // Map sensors and inject the user-specific custom name
            var dtos = sensors.Select(sensor =>
            {
                var dto = _mapper.Map<SensorDto>(sensor);
                if (customNames.TryGetValue(sensor.Id, out var customName))
                    dto.CustomName = customName;
                return dto;
            }).ToList();

            _logger.LogInformation("Returning {@LogData}", new { Count = dtos.Count, CallerUserId = User.UserId() });
            return Ok(dtos);
        }

        /// <summary>
        /// Retrieves a sensor by its ID.
        /// </summary>
        /// <param name="id">The ID of the sensor to retrieve.</param>
        /// <returns>The sensor with the specified ID.</returns>
        [HttpGet("{id}")]
        [SwaggerOperation(Summary = "Retrieves a sensor by its ID.")]
        [SwaggerResponse(200, "The sensor with the specified ID.", typeof(SensorDto))]
        [SwaggerResponse(404, "Sensor not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> GetSensor(int id)
        {
            _logger.LogInformation("GetSensor called by {@LogData}", new { CallerUserId = User.UserId(), id });

            var sensor = await _context.Sensors.FindAsync(id);
            if (sensor == null)
            {
                _logger.LogWarning("GetSensor not found: {@LogData}", new { id });
                return NotFound(new { message = "Sensor not found!" });
            }

            if (!await UserCanAccessSensorAsync(sensor.Id))
            {
                _logger.LogWarning("GetSensor forbidden for {@LogData}", new { CallerUserId = User.UserId(), id });
                return Forbid();
            }

            var dto = _mapper.Map<SensorDto>(sensor);

            var currentUserId = User.UserId();
            var customName = await _context.UserSensorCustomNames
                .Where(x => x.UserId == currentUserId && x.SensorId == id)
                .Select(x => x.CustomName)
                .FirstOrDefaultAsync();

            if (!string.IsNullOrEmpty(customName))
                dto.CustomName = customName;

            _logger.LogInformation("Returning sensor {@LogData}", new { id, CallerUserId = User.UserId() });
            return Ok(dto);
        }

        /// <summary>
        /// Retrieves data for a specific sensor.
        /// </summary>
        /// <param name="sensorId">The ID of the sensor to retrieve data for.</param>
        /// <param name="timeRange">The time range for the data (e.g., 5m, 10m, 30m, 1h). Takes precedence over startDate and endDate.</param>
        /// <param name="startDate">The start date for the data range.</param>
        /// <param name="endDate">The end date for the data range.</param>
        /// <param name="groupBy">The level to group the data by (e.g., "minute", "hour", "day", "5m", "10h", "2d").</param>
        /// <param name="pageNumber">The page number for pagination.</param>
        /// <param name="pageSize">The number of items per page.</param>
        /// <returns>The data for the specified sensor.</returns>
        [HttpGet("{sensorId}/data")]
        [SwaggerOperation(Summary = "Retrieves data for a specific sensor.")]
        [SwaggerResponse(200, "The data for the specified sensor.", typeof(IEnumerable<SensorDataDto>))]
        [SwaggerResponse(404, "Sensor not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> GetSensorData(
            int sensorId, string? timeRange, DateTime? startDate,
            DateTime? endDate, string? groupBy = "5m",
            int pageNumber = 1, int pageSize = 100)
        {
            _logger.LogInformation("GetSensorData called by {@LogData}", new { CallerUserId = User.UserId(), sensorId });

            var sensor = await _context.Sensors.FindAsync(sensorId);
            if (sensor == null)
            {
                _logger.LogWarning("GetSensorData not found: {@LogData}", new { sensorId });
                return NotFound(new { message = "Sensor not found!" });
            }

            if (!await UserCanAccessSensorAsync(sensor.Id))
            {
                _logger.LogWarning("GetSensorData forbidden for {@LogData}", new { CallerUserId = User.UserId(), sensorId });
                return Forbid();
            }

            var query = _context.SensorData
                .Where(sd => sd.SensorId == sensorId)
                .AsQueryable();

            DateTime? effectiveStart = null;
            DateTime? effectiveEnd = null;

            if (!string.IsNullOrEmpty(timeRange))
            {
                var now = DateTime.UtcNow;
                var timeSpan = ParseTimeRange(timeRange);
                if (timeSpan.HasValue)
                {
                    effectiveStart = now.Subtract(timeSpan.Value);
                    query = query.Where(sd => sd.Timestamp >= effectiveStart.Value);
                }
            }
            else
            {
                if (startDate.HasValue) { effectiveStart = startDate; query = query.Where(sd => sd.Timestamp >= startDate.Value); }
                if (endDate.HasValue) { effectiveEnd = endDate; query = query.Where(sd => sd.Timestamp <= endDate.Value); }
            }

            IEnumerable<SensorDataDto> result;
            int totalCount;

            var groupBySpan = !string.IsNullOrEmpty(groupBy) ? ParseTimeRange(groupBy) : null;
            if (groupBySpan.HasValue)
            {
                var grouped = await GetAveragedDataAsync(new[] { sensorId }, (long)groupBySpan.Value.TotalSeconds, effectiveStart, effectiveEnd);
                totalCount = grouped.Count;
                result = grouped;
            }
            else
            {
                totalCount = await query.CountAsync();
                var sensorDataList = await query
                    .OrderBy(sd => sd.Timestamp)
                    .Skip((pageNumber - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                result = _mapper.Map<IEnumerable<SensorDataDto>>(sensorDataList);
            }

            _logger.LogInformation("Returning {@LogData}", new { Count = totalCount, sensorId });
            return Ok(new
            {
                TotalCount = totalCount,
                PageNumber = pageNumber,
                PageSize = pageSize,
                Data = result
            });
        }

        /// <summary>
        /// Retrieves data for multiple sensors.
        /// </summary>
        /// <param name="sensorIds">The IDs of the sensors to retrieve data for.</param>
        /// <param name="timeRange">The time range for the data (e.g., 5m, 10m, 30m, 1h). Takes precedence over startDate and endDate.</param>
        /// <param name="startDate">The start date for the data range.</param>
        /// <param name="endDate">The end date for the data range.</param>
        /// <param name="groupBy">The level to group the data by (e.g., "minute", "hour", "day", "5m", "10h", "2d").</param>
        /// <param name="pageNumber">The page number for pagination.</param>
        /// <param name="pageSize">The number of items per page.</param>
        /// <returns>The data for the specified sensors.</returns>
        [HttpGet("data")]
        [SwaggerOperation(Summary = "Retrieves data for multiple sensors.")]
        [SwaggerResponse(200, "The data for the specified sensors.", typeof(IEnumerable<SensorDataDto>))]
        [SwaggerResponse(404, "One or more sensors not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> GetMultipleSensorsData(
            [FromQuery] List<int> sensorIds, string? timeRange, DateTime? startDate, DateTime? endDate,
            string? groupBy = "5m",
            int pageNumber = 1, int pageSize = 100)
        {
            _logger.LogInformation("GetMultipleSensorsData called by {@LogData}", new { CallerUserId = User.UserId(), sensorIds });

            var sensors = await _context.Sensors.Where(s => sensorIds.Contains(s.Id)).ToListAsync();
            if (sensors.Count() != sensorIds.Count())
            {
                _logger.LogWarning("GetMultipleSensorsData not found: {@LogData}", new { sensorIds });
                return NotFound(new { message = "One or more sensors not found!" });
            }

            foreach (var sensor in sensors)
            {
                if (!await UserCanAccessSensorAsync(sensor.Id))
                {
                    _logger.LogWarning("GetMultipleSensorsData forbidden for {@LogData}", new { CallerUserId = User.UserId(), sensorId = sensor.Id });
                    return Forbid();
                }
            }

            var query = _context.SensorData
                .Where(sd => sensorIds.Contains(sd.SensorId))
                .AsQueryable();

            DateTime? effectiveStart = null;
            DateTime? effectiveEnd = null;

            if (!string.IsNullOrEmpty(timeRange))
            {
                var now = DateTime.UtcNow;
                var timeSpan = ParseTimeRange(timeRange);
                if (timeSpan.HasValue)
                {
                    effectiveStart = now.Subtract(timeSpan.Value);
                    query = query.Where(sd => sd.Timestamp >= effectiveStart.Value);
                }
            }
            else
            {
                if (startDate.HasValue) { effectiveStart = startDate; query = query.Where(sd => sd.Timestamp >= startDate.Value); }
                if (endDate.HasValue) { effectiveEnd = endDate; query = query.Where(sd => sd.Timestamp <= endDate.Value); }
            }

            IEnumerable<SensorDataDto> result;
            int totalCount;

            var groupBySpan = !string.IsNullOrEmpty(groupBy) ? ParseTimeRange(groupBy) : null;
            if (groupBySpan.HasValue)
            {
                var grouped = await GetAveragedDataAsync(sensorIds, (long)groupBySpan.Value.TotalSeconds, effectiveStart, effectiveEnd);
                totalCount = grouped.Count;
                result = grouped;
            }
            else
            {
                totalCount = await query.CountAsync();
                var sensorDataList = await query
                    .OrderBy(sd => sd.Timestamp)
                    .Skip((pageNumber - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                result = _mapper.Map<IEnumerable<SensorDataDto>>(sensorDataList);
            }

            _logger.LogInformation("Returning {@LogData}", new { Count = totalCount, sensorIds });
            return Ok(new
            {
                TotalCount = totalCount,
                PageNumber = pageNumber,
                PageSize = pageSize,
                Data = result
            });
        }

        private async Task<List<SensorDataDto>> GetAveragedDataAsync(
            IEnumerable<int> sensorIds, long bucketSeconds, DateTime? effectiveStart, DateTime? effectiveEnd)
        {
            var effectiveBucket = EnforcedBucketSeconds(bucketSeconds, effectiveStart, effectiveEnd);

            var whereClauses = new List<string> { "sd.\"SensorId\" = ANY(@sensorIds)" };
            var parameters = new List<NpgsqlParameter>
            {
                new("bucketSeconds", effectiveBucket),
                new("sensorIds", sensorIds.ToArray()),
            };

            if (effectiveStart.HasValue) { whereClauses.Add("sd.\"Timestamp\" >= @startDate"); parameters.Add(new("startDate", effectiveStart.Value)); }
            if (effectiveEnd.HasValue) { whereClauses.Add("sd.\"Timestamp\" <= @endDate"); parameters.Add(new("endDate", effectiveEnd.Value)); }

            var sql = $"""
                SELECT
                    MIN(sd."Id") AS "Id",
                    sd."SensorId",
                    to_timestamp(FLOOR(EXTRACT(EPOCH FROM sd."Timestamp") / @bucketSeconds) * @bucketSeconds) AS "Timestamp",
                    ROUND(AVG(sd."Value"::double precision)::numeric, 3)::text AS "Value"
                FROM "SensorData" sd
                WHERE {string.Join(" AND ", whereClauses)}
                GROUP BY sd."SensorId", FLOOR(EXTRACT(EPOCH FROM sd."Timestamp") / @bucketSeconds)
                ORDER BY "Timestamp"
                """;

            _context.Database.SetCommandTimeout(120);
            return await _context.Database
                .SqlQueryRaw<SensorDataDto>(sql, parameters.Cast<object>().ToArray())
                .ToListAsync();
        }

        /// <summary>
        /// Enforces a minimum bucket size per sensor so the result never exceeds ~500 points per sensor,
        /// preventing timeouts on large date ranges with fine-grained grouping.
        /// Each sensor always gets the same granularity regardless of how many sensors are queried.
        /// The result is always snapped up to a human-friendly interval so timestamps land on clean boundaries.
        /// </summary>
        private static long EnforcedBucketSeconds(long requestedBucket, DateTime? start, DateTime? end)
        {
            if (!start.HasValue || !end.HasValue) return requestedBucket;
            var rangeSeconds = (long)(end.Value - start.Value).TotalSeconds;
            if (rangeSeconds <= 0) return requestedBucket;
            const int maxPointsPerSensor = 500;
            var minBucket = (rangeSeconds + maxPointsPerSensor - 1) / maxPointsPerSensor;
            if (requestedBucket >= minBucket) return requestedBucket;
            return SnapToCleanInterval(minBucket);
        }

        // Human-readable bucket boundaries: 5m, 10m, 15m, 30m, 1h, 2h, 3h, 4h, 6h, 12h, 1d, 2d, 1w
        private static readonly long[] CleanIntervals =
            [300, 600, 900, 1800, 3600, 7200, 10800, 14400, 21600, 43200, 86400, 172800, 604800];

        private static long SnapToCleanInterval(long seconds)
        {
            foreach (var interval in CleanIntervals)
                if (interval >= seconds) return interval;
            return CleanIntervals[^1];
        }

        private static DateTime GetGroupingKey(DateTime timestamp, string? groupBy)
        {
            if (string.IsNullOrEmpty(groupBy))
                return timestamp;

            var timeSpan = ParseTimeRange(groupBy);
            if (timeSpan.HasValue)
            {
                var ticks = (timestamp.Ticks / timeSpan.Value.Ticks) * timeSpan.Value.Ticks;
                return new DateTime(ticks, DateTimeKind.Utc);
            }

            return timestamp;
        }

        private static TimeSpan? ParseTimeRange(string timeRange)
        {
            if (string.IsNullOrEmpty(timeRange) || timeRange.Length < 2)
                return null;

            var value = timeRange.Substring(0, timeRange.Length - 1);
            var unit = timeRange.Substring(timeRange.Length - 1).ToLower();

            if (!int.TryParse(value, out var intValue))
                return null;

            return unit switch
            {
                "m" => TimeSpan.FromMinutes(intValue),
                "h" => TimeSpan.FromHours(intValue),
                "d" => TimeSpan.FromDays(intValue),
                "w" => TimeSpan.FromDays(intValue * 7),
                "y" => TimeSpan.FromDays(intValue * 365),
                _ => null,
            };
        }

        private async Task<string> GenerateSensorRegistrationCodeAsync(int length = 10)
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            string code;
            bool exists;

            do
            {
                code = new string(Enumerable.Range(0, length)
                    .Select(_ => chars[System.Security.Cryptography.RandomNumberGenerator.GetInt32(chars.Length)]).ToArray());
                exists = await _context.Sensors.AnyAsync(s => s.RegistrationCode == code);
            } while (exists);

            return code;
        }

        private static string GenerateParentName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            var lastUnderscore = name.LastIndexOf('_');
            if (lastUnderscore > -1)
                return name.Substring(0, lastUnderscore);
            return name;
        }

        [HttpPost("generate-missing-codes")]
        public async Task<IActionResult> GenerateMissingRegistrationCodes()
        {
            _logger.LogInformation("GenerateMissingRegistrationCodes called by {@LogData}", new { CallerUserId = User.UserId() });

            if (!IsAdmin())
            {
                _logger.LogWarning("GenerateMissingRegistrationCodes forbidden for {@LogData}", new { CallerUserId = User.UserId() });
                return Forbid();
            }

            var sensorsWithoutCode = await _context.Sensors
                .Where(s => string.IsNullOrEmpty(s.RegistrationCode))
                .ToListAsync();

            _logger.LogInformation("Found {@LogData}", new { Count = sensorsWithoutCode.Count });

            foreach (var sensor in sensorsWithoutCode)
            {
                string code;
                do
                {
                    code = await GenerateSensorRegistrationCodeAsync();
                }
                while (await _context.Sensors.AnyAsync(s => s.RegistrationCode == code));

                sensor.RegistrationCode = code;
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation("Updated RegistrationCode for {@LogData}", new { Count = sensorsWithoutCode.Count });

            return Ok(new { updated = sensorsWithoutCode.Count });
        }

        /// <summary>
        /// Generates and sets ParentName for all sensors where it is missing.
        /// </summary>
        /// <returns>The number of sensors updated.</returns>
        [HttpPost("generate-missing-parent-names")]
        public async Task<IActionResult> GenerateMissingParentNames()
        {
            _logger.LogInformation("GenerateMissingParentNames called by {@LogData}", new { CallerUserId = User.UserId() });

            if (!IsAdmin())
            {
                _logger.LogWarning("GenerateMissingParentNames forbidden for {@LogData}", new { CallerUserId = User.UserId() });
                return Forbid();
            }

            var sensorsWithoutParentName = await _context.Sensors
                .Where(s => string.IsNullOrEmpty(s.ParentName))
                .ToListAsync();

            _logger.LogInformation("Found {@LogData}", new { Count = sensorsWithoutParentName.Count });

            foreach (var sensor in sensorsWithoutParentName)
            {
                sensor.ParentName = GenerateParentName(sensor.Name);
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation("Updated ParentName for {@LogData}", new { Count = sensorsWithoutParentName.Count });

            return Ok(new { updated = sensorsWithoutParentName.Count });
        }

        /// <summary>
        /// Claims a sensor for the current user using a registration code.
        /// </summary>
        /// <param name="dto">The registration code DTO.</param>
        /// <returns>Success or error message.</returns>
        [HttpPost("claim")]
        [Authorize]
        public async Task<IActionResult> ClaimSensor([FromBody] ClaimSensorDto dto)
        {
            _logger.LogInformation("ClaimSensor called by {@LogData}", new { CallerUserId = User.UserId(), RegistrationCode = dto.RegistrationCode });

            if (string.IsNullOrWhiteSpace(dto.RegistrationCode))
            {
                _logger.LogWarning("ClaimSensor bad request: Registration code is required {@LogData}", new { CallerUserId = User.UserId() });
                return BadRequest(new { message = "Registration code is required." });
            }

            var sensor = await _context.Sensors.FirstOrDefaultAsync(s => s.RegistrationCode == dto.RegistrationCode);
            if (sensor == null)
            {
                _logger.LogWarning("ClaimSensor not found: Invalid registration code {@LogData}", new { RegistrationCode = dto.RegistrationCode, CallerUserId = User.UserId() });
                return NotFound(new { message = "Invalid registration code." });
            }

            var userId = User.UserId();
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                _logger.LogWarning("ClaimSensor unauthorized: User not found {@LogData}", new { UserId = userId });
                return Unauthorized();
            }

            // Check if already claimed
            var alreadyClaimed = await _context.UserSensors.AnyAsync(us => us.UserId == userId && us.SensorId == sensor.Id);
            if (!alreadyClaimed)
            {
                _context.UserSensors.Add(new UserSensor { UserId = userId!, SensorId = sensor.Id, IsOwner = true });
                await _context.SaveChangesAsync();
                _ownership.InvalidateSensor(sensor.Id);
                await _hub.Clients.Group(DeviceHub.UserGroup(userId!)).SendAsync("device-created", new { kind = "sensor", id = sensor.Id });
                _logger.LogInformation("ClaimSensor assigned sensor to user {@LogData}", new { sensor.Id, CallerUserId = User.UserId() });
            }
            else
            {
                _logger.LogInformation("ClaimSensor user already has sensor {@LogData}", new { CallerUserId = User.UserId(), sensor.Id });
            }

            _logger.LogInformation("Sensor successfully claimed for user {@LogData}", new { CallerUserId = User.UserId() });
            return Ok(new { message = "Sensor successfully claimed and assigned to your account." });
        }

        [HttpDelete("{id}/claim")]
        [Authorize]
        [SwaggerOperation(Summary = "Removes the current user's access to a sensor by revoking the sensor role.")]
        [SwaggerResponse(200, "Sensor unclaimed successfully.")]
        [SwaggerResponse(404, "Sensor not found.")]
        [SwaggerResponse(403, "User does not have access to this sensor.")]
        public async Task<IActionResult> UnclaimSensor(int id)
        {
            _logger.LogInformation("UnclaimSensor called by {@LogData}", new { CallerUserId = User.UserId(), id });

            var sensor = await _context.Sensors.FindAsync(id);
            if (sensor == null)
            {
                _logger.LogWarning("UnclaimSensor not found: {@LogData}", new { id });
                return NotFound(new { message = "Sensor not found." });
            }

            if (!await UserCanAccessSensorAsync(sensor.Id))
            {
                _logger.LogWarning("UnclaimSensor forbidden for {@LogData}", new { CallerUserId = User.UserId(), id });
                return Forbid();
            }

            var userId = User.UserId();

            var userSensor = await _context.UserSensors
                .FirstOrDefaultAsync(us => us.UserId == userId && us.SensorId == id);
            if (userSensor != null)
            {
                _context.UserSensors.Remove(userSensor);
                _ownership.InvalidateSensor(id);
            }

            // Remove any custom name the user has set for this sensor
            var customName = await _context.UserSensorCustomNames
                .FirstOrDefaultAsync(x => x.UserId == userId && x.SensorId == id);
            if (customName != null)
            {
                _context.UserSensorCustomNames.Remove(customName);
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation("Sensor unclaimed by user {@LogData}", new { CallerUserId = User.UserId(), id });
            return Ok(new { message = "Sensor removed from your account." });
        }

        private static string GenerateDefaultName(string originalName)
        {
            var parts = originalName.Split('_');
            if (parts.Length < 2)
                return originalName;

            var code = parts.Length >= 2 ? parts[^2] : null;
            var type = parts.Length >= 1 ? parts[^1] : null;

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(type))
                return originalName;

            return $"Garge {code} {type}";
        }

        [HttpPost("generate-missing-default-names")]
        public async Task<IActionResult> GenerateMissingDefaultNames()
        {
            _logger.LogInformation("GenerateMissingDefaultNames called by {@LogData}", new { CallerUserId = User.UserId() });

            if (!IsAdmin())
            {
                _logger.LogWarning("GenerateMissingDefaultNames forbidden for {@LogData}", new { CallerUserId = User.UserId() });
                return Forbid();
            }

            var sensorsWithoutDefaultName = await _context.Sensors
                .Where(s => string.IsNullOrEmpty(s.DefaultName))
                .ToListAsync();

            _logger.LogInformation("Found {@LogData}", new { Count = sensorsWithoutDefaultName.Count });

            foreach (var sensor in sensorsWithoutDefaultName)
            {
                sensor.DefaultName = GenerateDefaultName(sensor.Name);
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation("Updated DefaultName for {@LogData}", new { Count = sensorsWithoutDefaultName.Count });

            return Ok(new { updated = sensorsWithoutDefaultName.Count });
        }

        /// <summary>
        /// Creates a new sensor.
        /// </summary>
        /// <param name="sensorDto">The sensor to create.</param>
        /// <returns>The created sensor.</returns>
        [HttpPost]
        [SwaggerOperation(Summary = "Creates a new sensor.")]
        [SwaggerResponse(201, "The created sensor.", typeof(SensorDto))]
        [SwaggerResponse(409, "Sensor name already exists.")]
        public async Task<IActionResult> CreateSensor([FromBody] CreateSensorDto sensorDto)
        {
            _logger.LogInformation("CreateSensor called by {@LogData}", new { CallerUserId = User.UserId(), sensorDto.Name, sensorDto.Type });

            if (!IsAdmin())
            {
                _logger.LogWarning("CreateSensor forbidden for {@LogData}", new { CallerUserId = User.UserId() });
                return Forbid();
            }

            var sensor = new Sensor
            {
                Name = sensorDto.Name,
                Type = sensorDto.Type,
                Role = sensorDto.Name,
                RegistrationCode = await GenerateSensorRegistrationCodeAsync(),
                DefaultName = GenerateDefaultName(sensorDto.Name),
                ParentName = sensorDto.ParentName
            };

            _context.Sensors.Add(sensor);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("duplicate key") == true)
            {
                _logger.LogWarning("CreateSensor conflict: Sensor name {@LogData} already exists", new { sensorDto.Name });
                return Conflict(new { message = "Sensor name already exists!" });
            }

            var dto = _mapper.Map<SensorDto>(sensor);
            _logger.LogInformation("Sensor created: {@LogData}", new { sensor.Id, sensor.Name });
            return CreatedAtAction(nameof(GetSensor), new { id = sensor.Id }, dto);
        }

        /// <summary>
        /// Creates new data for a specific sensor using sensorId.
        /// </summary>
        /// <param name="sensorId">The ID of the sensor to create data for.</param>
        /// <param name="sensorDataDto">The data to create.</param>
        /// <returns>The created sensor data.</returns>
        [HttpPost("{sensorId}/data")]
        [SwaggerOperation(Summary = "Creates new data for a specific sensor using sensorId.")]
        [SwaggerResponse(201, "The created sensor data.", typeof(SensorDataDto))]
        [SwaggerResponse(404, "Sensor not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> CreateSensorDataById(int sensorId, [FromBody] CreateSensorDataDto sensorDataDto)
        {
            _logger.LogInformation("CreateSensorDataById called by {@LogData}", new { CallerUserId = User.UserId(), sensorId });

            if (!IsAdmin())
            {
                _logger.LogWarning("CreateSensorDataById forbidden for {@LogData}", new { CallerUserId = User.UserId(), sensorId });
                return Forbid();
            }

            var sensor = await _context.Sensors.FindAsync(sensorId);
            if (sensor == null)
            {
                _logger.LogWarning("CreateSensorDataById not found: {@LogData}", new { sensorId });
                return NotFound(new { message = "Sensor not found!" });
            }

            var sensorData = new SensorData
            {
                SensorId = sensorId,
                Value = Math.Round(double.Parse(sensorDataDto.Value), 3).ToString(CultureInfo.InvariantCulture),
                Timestamp = DateTime.UtcNow
            };

            _context.SensorData.Add(sensorData);
            await _context.SaveChangesAsync();

            var dto = _mapper.Map<SensorDataDto>(sensorData);
            _logger.LogInformation("Sensor data created: {@LogData}", new { sensorData.Id, sensorId });
            return CreatedAtAction(nameof(GetSensorData), new { sensorId }, dto);
        }

        /// <summary>
        /// Creates new data for a specific sensor using sensorName.
        /// </summary>
        /// <param name="sensorName">The name of the sensor to create data for.</param>
        /// <param name="sensorDataDto">The data to create.</param>
        /// <returns>The created sensor data.</returns>
        [HttpPost("name/{sensorName}/data")]
        [SwaggerOperation(Summary = "Creates new data for a specific sensor using sensorName.")]
        [SwaggerResponse(201, "The created sensor data.", typeof(SensorDataDto))]
        [SwaggerResponse(404, "Sensor not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> CreateSensorDataByName(string sensorName, [FromBody] CreateSensorDataDto sensorDataDto)
        {
            _logger.LogInformation("CreateSensorDataByName called by {@LogData}", new { CallerUserId = User.UserId(), sensorName });

            if (!IsAdmin())
            {
                _logger.LogWarning("CreateSensorDataByName forbidden for {@LogData}", new { CallerUserId = User.UserId(), sensorName });
                return Forbid();
            }

            var sensor = await _context.Sensors.FirstOrDefaultAsync(s => s.Name == sensorName);
            if (sensor == null)
            {
                _logger.LogWarning("CreateSensorDataByName not found: {@LogData}", new { sensorName });
                return NotFound(new { message = "Sensor not found!" });
            }

            if (string.IsNullOrWhiteSpace(sensorDataDto.Value) || !double.TryParse(sensorDataDto.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedValue))
            {
                _logger.LogWarning("CreateSensorDataByName bad request: Invalid value {@LogData}", new { sensorDataDto.Value, sensorName });
                return BadRequest(new { message = "Value must be a valid number." });
            }

            var sensorData = new SensorData
            {
                SensorId = sensor.Id,
                Value = Math.Round(double.Parse(sensorDataDto.Value), 3).ToString(CultureInfo.InvariantCulture),
                Timestamp = DateTime.UtcNow
            };

            _context.SensorData.Add(sensorData);
            await _context.SaveChangesAsync();

            var dto = _mapper.Map<SensorDataDto>(sensorData);
            _logger.LogInformation("Sensor data created: {@LogData}", new { sensorData.Id, sensorName });
            return CreatedAtAction(nameof(GetSensorData), new { sensorId = sensor.Id }, dto);
        }

        /// <summary>
        /// Updates an existing sensor.
        /// </summary>
        /// <param name="id">The ID of the sensor to update.</param>
        /// <param name="sensorDto">The updated sensor data.</param>
        /// <returns>No content.</returns>
        [HttpPut("{id}")]
        [SwaggerOperation(Summary = "Updates an existing sensor.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(400, "Bad request.")]
        [SwaggerResponse(404, "Sensor not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> UpdateSensor(int id, [FromBody] UpdateSensorDto sensorDto)
        {
            _logger.LogInformation("UpdateSensor called by {@LogData}", new { CallerUserId = User.UserId(), id });

            if (!IsAdmin())
            {
                _logger.LogWarning("UpdateSensor forbidden for {@LogData}", new { CallerUserId = User.UserId(), id });
                return Forbid();
            }

            var existingSensor = await _context.Sensors.FindAsync(id);
            if (existingSensor == null)
            {
                _logger.LogWarning("UpdateSensor not found: {@LogData}", new { id });
                return NotFound(new { message = "Sensor not found!" });
            }

            existingSensor.Name = sensorDto.Name;
            existingSensor.Type = sensorDto.Type;
            existingSensor.Role = sensorDto.Role;

            _context.Entry(existingSensor).State = EntityState.Modified;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Sensor updated: {@LogData}", new { id });
            return NoContent();
        }

        /// <summary>
        /// Deletes a sensor by its ID.
        /// </summary>
        /// <param name="id">The ID of the sensor to delete.</param>
        /// <returns>No content.</returns>
        [HttpDelete("{id}")]
        [SwaggerOperation(Summary = "Deletes a sensor by its ID.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(404, "Sensor not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> DeleteSensor(int id)
        {
            _logger.LogInformation("DeleteSensor called by {@LogData}", new { CallerUserId = User.UserId(), id });

            if (!IsAdmin())
            {
                _logger.LogWarning("DeleteSensor forbidden for {@LogData}", new { CallerUserId = User.UserId(), id });
                return Forbid();
            }

            var sensor = await _context.Sensors.FindAsync(id);
            if (sensor == null)
            {
                _logger.LogWarning("DeleteSensor not found: {@LogData}", new { id });
                return NotFound(new { message = "Sensor not found!" });
            }

            _context.Sensors.Remove(sensor);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Sensor deleted: {@LogData}", new { id });
            return NoContent();
        }

        /// <summary>
        /// Deletes specific sensor data by its ID.
        /// </summary>
        /// <param name="sensorId">The ID of the sensor.</param>
        /// <param name="dataId">The ID of the sensor data to delete.</param>
        /// <returns>No content.</returns>
        [HttpDelete("{sensorId}/data/{dataId}")]
        [SwaggerOperation(Summary = "Deletes specific sensor data by its ID.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(404, "Sensor or sensor data not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> DeleteSensorData(int sensorId, int dataId)
        {
            _logger.LogInformation("DeleteSensorData called by {@LogData}", new { CallerUserId = User.UserId(), sensorId, dataId });

            if (!IsAdmin())
            {
                _logger.LogWarning("DeleteSensorData forbidden for {@LogData}", new { CallerUserId = User.UserId(), sensorId });
                return Forbid();
            }

            var sensor = await _context.Sensors.FindAsync(sensorId);
            if (sensor == null)
            {
                _logger.LogWarning("DeleteSensorData not found: {@LogData}", new { sensorId });
                return NotFound(new { message = "Sensor not found!" });
            }

            var sensorData = await _context.SensorData.FindAsync(dataId);
            if (sensorData == null || sensorData.SensorId != sensorId)
            {
                _logger.LogWarning("DeleteSensorData not found: {@LogData}", new { dataId, sensorId });
                return NotFound(new { message = "Sensor data not found!" });
            }

            _context.SensorData.Remove(sensorData);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Sensor data deleted: {@LogData}", new { dataId, sensorId });
            return NoContent();
        }

        /// <summary>
        /// Deletes all sensor data for a specific sensor.
        /// </summary>
        /// <param name="sensorId">The ID of the sensor.</param>
        /// <returns>No content.</returns>
        [HttpDelete("{sensorId}/data")]
        [SwaggerOperation(Summary = "Deletes all sensor data for a specific sensor.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(404, "Sensor not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> DeleteAllSensorData(int sensorId)
        {
            _logger.LogInformation("DeleteAllSensorData called by {@LogData}", new { CallerUserId = User.UserId(), sensorId });

            if (!IsAdmin())
            {
                _logger.LogWarning("DeleteAllSensorData forbidden for {@LogData}", new { CallerUserId = User.UserId(), sensorId });
                return Forbid();
            }

            var sensor = await _context.Sensors.FindAsync(sensorId);
            if (sensor == null)
            {
                _logger.LogWarning("DeleteAllSensorData not found: {@LogData}", new { sensorId });
                return NotFound(new { message = "Sensor not found!" });
            }

            var sensorData = _context.SensorData.Where(sd => sd.SensorId == sensorId);
            _context.SensorData.RemoveRange(sensorData);
            await _context.SaveChangesAsync();

            _logger.LogInformation("All sensor data deleted for {@LogData}", new { sensorId });
            return NoContent();
        }

        /// <summary>
        /// Updates the custom name of a sensor.
        /// </summary>
        /// <param name="sensorId">The ID of the sensor to update.</param>
        /// <param name="dto">The new custom name.</param>
        /// <param name="userId">Optional user ID (admin only). Defaults to the authenticated user.</param>
        /// <returns>The custom name data.</returns>
        [HttpPatch("{sensorId}/custom-name")]
        [SwaggerOperation(Summary = "Updates the custom name of a sensor for a user.")]
        [SwaggerResponse(200, "Custom name updated.", typeof(UserSensorCustomNameDto))]
        [SwaggerResponse(404, "Sensor not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> UpdateCustomName(
            int sensorId,
            [FromBody] UpdateCustomNameDto dto,
            [FromQuery] string? userId = null)
        {
            _logger.LogInformation("UpdateCustomName called by {@LogData}", new { CallerUserId = User.UserId(), sensorId });

            var sensor = await _context.Sensors.FindAsync(sensorId);
            if (sensor == null)
            {
                _logger.LogWarning("UpdateCustomName not found: {@LogData}", new { sensorId });
                return NotFound(new { message = "Sensor not found!" });
            }

            if (!await UserCanAccessSensorAsync(sensor.Id))
            {
                _logger.LogWarning("UpdateCustomName forbidden for {@LogData}", new { CallerUserId = User.UserId(), sensorId });
                return Forbid();
            }

            var currentUserId = User.UserId();
            var isSensorAdmin = User.Claims
                .Where(c => c.Type == ClaimTypes.Role)
                .Select(c => c.Value)
                .Any(role => AdminRoles.Contains(role, StringComparer.OrdinalIgnoreCase));
            var targetUserId = userId ?? currentUserId;

            if (!isSensorAdmin && targetUserId != currentUserId)
            {
                _logger.LogWarning("UpdateCustomName forbidden: {@LogData}", new { CallerUserId = User.UserId() });
                return Forbid();
            }

            if (string.IsNullOrEmpty(targetUserId))
            {
                _logger.LogWarning("UpdateCustomName unauthorized: {@LogData}", new { CallerUserId = User.UserId() });
                return Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(dto.CustomName) || dto.CustomName.Length > 50)
            {
                _logger.LogWarning("UpdateCustomName bad request: {@LogData}", new { sensorId, CallerUserId = User.UserId() });
                return BadRequest(new { message = "CustomName is required and must be at most 50 characters." });
            }

            var entry = await _context.UserSensorCustomNames
                .FirstOrDefaultAsync(x => x.UserId == targetUserId && x.SensorId == sensorId);

            if (entry == null)
            {
                entry = new UserSensorCustomName
                {
                    UserId = targetUserId,
                    SensorId = sensorId,
                    CustomName = dto.CustomName,
                    CreatedAt = DateTime.UtcNow
                };
                _context.UserSensorCustomNames.Add(entry);
            }
            else
            {
                entry.CustomName = dto.CustomName;
                _context.Entry(entry).Property(x => x.CustomName).IsModified = true;
            }

            await _context.SaveChangesAsync();

            var resultDto = new UserSensorCustomNameDto
            {
                UserId = entry.UserId,
                SensorId = entry.SensorId,
                CustomName = entry.CustomName,
                CreatedAt = entry.CreatedAt
            };

            _logger.LogInformation("Custom name updated for {@LogData}", new { sensorId, CallerUserId = User.UserId() });
            return Ok(resultDto);
        }

        /// <summary>
        /// Retrieves the latest data point for a specific sensor.
        /// </summary>
        /// <param name="sensorId">The ID of the sensor.</param>
        /// <returns>The latest sensor data.</returns>
        [HttpGet("{sensorId}/latest-data")]
        [SwaggerOperation(Summary = "Retrieves the latest data point for a specific sensor.")]
        [SwaggerResponse(200, "The latest sensor data.", typeof(SensorDataDto))]
        [SwaggerResponse(404, "Sensor or sensor data not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> GetLatestSensorData(int sensorId)
        {
            _logger.LogInformation("GetLatestSensorData called by {@LogData}", new { CallerUserId = User.UserId(), sensorId });

            var sensor = await _context.Sensors.FindAsync(sensorId);
            if (sensor == null)
            {
                _logger.LogWarning("GetLatestSensorData not found: {@LogData}", new { sensorId });
                return NotFound(new { message = "Sensor not found!" });
            }

            if (!await UserCanAccessSensorAsync(sensor.Id))
            {
                _logger.LogWarning("GetLatestSensorData forbidden for {@LogData}", new { CallerUserId = User.UserId(), sensorId });
                return Forbid();
            }

            var latestData = await _context.SensorData
                .Where(sd => sd.SensorId == sensorId)
                .OrderByDescending(sd => sd.Timestamp)
                .FirstOrDefaultAsync();

            if (latestData == null)
            {
                _logger.LogWarning("GetLatestSensorData not found: No data for sensor {@LogData}", new { sensorId });
                return NotFound(new { message = "No data found for this sensor." });
            }

            var dto = _mapper.Map<SensorDataDto>(latestData);
            return Ok(dto);
        }

        [HttpPost("{sensorId}/photo")]
        [SwaggerOperation(Summary = "Upload or replace a photo for a sensor.")]
        [SwaggerResponse(200, "Photo saved.")]
        [SwaggerResponse(400, "Invalid request.")]
        [SwaggerResponse(403, "No access to sensor.")]
        public async Task<IActionResult> UploadSensorPhoto(int sensorId, [FromBody] UploadPhotoDto dto)
        {
            if (!await UserCanAccessSensorAsync(sensorId))
                return Forbid();

            var userId = User.UserId()!;
            return await PhotoEndpointHelpers.UpsertAsync(
                _context,
                _context.SensorPhotos,
                sp => sp.SensorId == sensorId,
                () => new SensorPhoto
                {
                    SensorId = sensorId,
                    UserId = userId,
                    Data = dto.Data,
                    ContentType = dto.ContentType
                },
                dto,
                userId);
        }

        [HttpGet("{sensorId}/photo")]
        [SwaggerOperation(Summary = "Get the photo for a sensor.")]
        [SwaggerResponse(200, "Photo data.")]
        [SwaggerResponse(403, "No access to sensor.")]
        [SwaggerResponse(404, "No photo found.")]
        public async Task<IActionResult> GetSensorPhoto(int sensorId)
        {
            if (!await UserCanAccessSensorAsync(sensorId))
                return Forbid();

            return await PhotoEndpointHelpers.GetAsync(
                _context.SensorPhotos,
                sp => sp.SensorId == sensorId);
        }

        [HttpDelete("{sensorId}/photo")]
        [SwaggerOperation(Summary = "Delete the photo for a sensor.")]
        [SwaggerResponse(200, "Photo deleted.")]
        [SwaggerResponse(403, "No access to sensor.")]
        [SwaggerResponse(404, "No photo found.")]
        public async Task<IActionResult> DeleteSensorPhoto(int sensorId)
        {
            if (!await UserCanAccessSensorAsync(sensorId))
                return Forbid();

            return await PhotoEndpointHelpers.DeleteAsync(
                _context,
                _context.SensorPhotos,
                sp => sp.SensorId == sensorId);
        }
    }
}
