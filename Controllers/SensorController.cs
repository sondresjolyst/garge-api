using garge_api.Constants;
using garge_api.Controllers.Common;
using garge_api.Dtos.Common;
using garge_api.Helpers;
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
    public class SensorController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IMapper _mapper;
        private readonly ILogger<SensorController> _logger;
        private readonly IDeviceOwnershipService _ownership;
        private readonly IHubContext<DeviceHub> _hub;
        private readonly ISubscriptionCapacityService _capacity;
        private static readonly List<string> AdminRoles = new() { "SensorAdmin", "admin" };

        public SensorController(ApplicationDbContext context, IMapper mapper, ILogger<SensorController> logger, IDeviceOwnershipService ownership, IHubContext<DeviceHub> hub, ISubscriptionCapacityService capacity)
        {
            _context = context;
            _mapper = mapper;
            _logger = logger;
            _ownership = ownership;
            _hub = hub;
            _capacity = capacity;
        }

        private bool IsAdmin()
        {
            var userRoles = User.FindAll(ClaimTypes.Role).Select(r => r.Value).ToList();
            return userRoles.Any(role => AdminRoles.Contains(role, StringComparer.OrdinalIgnoreCase));
        }

        private async Task<bool> UserCanAccessSensorAsync(int sensorId, CancellationToken ct = default)
        {
            if (IsAdmin()) return true;
            var userId = User.UserId();
            if (string.IsNullOrEmpty(userId)) return false;
            // Delegate to the shared ownership service.
            return await _ownership.CanUserAccessSensorAsync(userId, sensorId, ct);
        }

        /// <summary>
        /// Restricts a SensorData query to readings that fall inside the caller's own ownership
        /// period(s), so a new owner of a re-claimed/resold sensor never sees the previous owner's
        /// history. Admins see everything. The access check (UserCanAccessSensorAsync) still gates
        /// whether the sensor is visible at all; this bounds the time window of the data returned.
        /// </summary>
        private IQueryable<SensorData> WithinOwnershipWindow(IQueryable<SensorData> query)
            => query.WithinSensorOwnership(_context, User.UserId(), IsAdmin());

        /// <summary>
        /// Returns the caller's sensor capacity, how much is in use, and whether they may claim another.
        /// </summary>
        [HttpGet("capacity")]
        [Authorize]
        [SwaggerOperation(Summary = "Gets the caller's sensor capacity and claim eligibility.")]
        [SwaggerResponse(200, "Capacity returned.", typeof(SensorCapacityDto))]
        public async Task<IActionResult> GetMyCapacity()
        {
            var userId = User.UserId();
            if (string.IsNullOrEmpty(userId)) return Unauthorized();

            var bypass = await _capacity.HasSubscriptionBypassAsync(userId);
            var capacity = await _capacity.GetCapacityAsync(userId);
            var used = await _capacity.GetActiveOwnedSensorCountAsync(userId);

            return Ok(new SensorCapacityDto
            {
                Capacity = capacity,
                Used = used,
                Bypass = bypass,
                CanClaim = bypass || used < capacity,
            });
        }

        /// <summary>
        /// Returns true when the caller has this owned sensor suspended (turned off or over quota).
        /// Suspended sensors remain visible in the list, but their dashboard and history reads are
        /// blocked. Admins are never suspended. Export and unclaim/delete must not use this gate, as
        /// those are GDPR rights.
        /// </summary>
        private async Task<bool> IsSensorSuspendedForCallerAsync(int sensorId, CancellationToken ct = default)
        {
            if (IsAdmin()) return false;
            var userId = User.UserId();
            return await _context.UserSensors.AnyAsync(us => us.UserId == userId && us.SensorId == sensorId && us.SuspendedAt != null, ct);
        }

        /// <summary>Returns the subset of the given sensor ids that the caller has suspended (empty for admins).</summary>
        private async Task<HashSet<int>> CallerSuspendedSensorIdsAsync(IEnumerable<int> sensorIds, CancellationToken ct = default)
        {
            if (IsAdmin()) return new HashSet<int>();
            var userId = User.UserId();
            var ids = await _context.UserSensors
                .Where(us => us.UserId == userId && us.SuspendedAt != null && sensorIds.Contains(us.SensorId))
                .Select(us => us.SensorId)
                .ToListAsync(ct);
            return ids.ToHashSet();
        }

        /// <summary>
        /// Retrieves all sensors the user has access to.
        /// </summary>
        /// <returns>A list of sensors the user has access to.</returns>
        [HttpGet]
        [SwaggerOperation(Summary = "Retrieves all available sensors.")]
        [SwaggerResponse(200, "A list of all sensors.", typeof(IEnumerable<SensorDto>))]
        public async Task<IActionResult> GetAllSensors(CancellationToken ct = default)
        {
            _logger.LogInformation("GetAllSensors called by {@LogData}", new { CallerUserId = User.UserId() });

            var currentUserId = User.UserId();

            List<Sensor> sensors;
            if (IsAdmin())
            {
                sensors = await _context.Sensors.AsNoTracking().ToListAsync(ct);
            }
            else
            {
                sensors = await _context.Sensors
                    .AsNoTracking()
                    .Where(s => _context.UserSensors.Any(us => us.UserId == currentUserId && us.SensorId == s.Id))
                    .ToListAsync(ct);
            }

            // Fetch all custom names for the current user
            var customNames = await _context.UserSensorCustomNames
                .Where(x => x.UserId == currentUserId)
                .ToDictionaryAsync(x => x.SensorId, x => x.CustomName, ct);

            // Fetch all voltage color thresholds for the current user
            var voltageThresholds = await _context.UserSensorVoltageThresholds
                .Where(x => x.UserId == currentUserId)
                .ToDictionaryAsync(x => x.SensorId, x => new { x.WarningVoltage, x.CriticalVoltage }, ct);

            var sensorIds = sensors.Select(s => s.Id).ToList();
            var suspendedIds = await CallerSuspendedSensorIdsAsync(sensorIds, ct);

            // The caller's owner, edit, or read relationship per sensor (admins are treated as owner).
            var accessRows = IsAdmin()
                ? new List<UserSensor>()
                : await _context.UserSensors
                    .Where(us => us.UserId == currentUserId && sensorIds.Contains(us.SensorId))
                    .ToListAsync(ct);
            var accessById = accessRows.ToDictionary(us => us.SensorId, us => DeviceAccess.From(us.IsOwner, us.Permission));

            // Map sensors and apply the user-specific custom name, suspended flag, and access state.
            var dtos = sensors.Select(sensor =>
            {
                var dto = _mapper.Map<SensorDto>(sensor);
                if (customNames.TryGetValue(sensor.Id, out var customName))
                    dto.CustomName = customName;
                if (voltageThresholds.TryGetValue(sensor.Id, out var threshold))
                {
                    dto.WarningVoltage = threshold.WarningVoltage;
                    dto.CriticalVoltage = threshold.CriticalVoltage;
                }
                dto.Suspended = suspendedIds.Contains(sensor.Id);
                dto.Access = IsAdmin() ? DeviceAccess.Owner : (accessById.TryGetValue(sensor.Id, out var a) ? a : DeviceAccess.Owner);
                return dto;
            }).ToList();

            _logger.LogInformation("Returning {@LogData}", new { Count = dtos.Count, CallerUserId = User.UserId() });
            return Ok(dtos);
        }

        /// <summary>
        /// Retrieves a sensor by its ID.
        /// </summary>
        /// <param name="id">The ID of the sensor to retrieve.</param>
        /// <param name="ct">Cancellation token bound by the framework.</param>
        /// <returns>The sensor with the specified ID.</returns>
        [HttpGet("{id}")]
        [SwaggerOperation(Summary = "Retrieves a sensor by its ID.")]
        [SwaggerResponse(200, "The sensor with the specified ID.", typeof(SensorDto))]
        [SwaggerResponse(404, "Sensor not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> GetSensor(int id, CancellationToken ct = default)
        {
            _logger.LogInformation("GetSensor called by {@LogData}", new { CallerUserId = User.UserId(), id });

            var sensor = await _context.Sensors.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (sensor == null)
            {
                _logger.LogWarning("GetSensor not found: {@LogData}", new { id });
                return NotFound(new { message = "Sensor not found!" });
            }

            if (!await UserCanAccessSensorAsync(sensor.Id, ct))
            {
                _logger.LogWarning("GetSensor forbidden for {@LogData}", new { CallerUserId = User.UserId(), id });
                return Forbid();
            }

            var dto = _mapper.Map<SensorDto>(sensor);

            var currentUserId = User.UserId();
            var customName = await _context.UserSensorCustomNames
                .Where(x => x.UserId == currentUserId && x.SensorId == id)
                .Select(x => x.CustomName)
                .FirstOrDefaultAsync(ct);

            if (!string.IsNullOrEmpty(customName))
                dto.CustomName = customName;

            var threshold = await _context.UserSensorVoltageThresholds
                .Where(x => x.UserId == currentUserId && x.SensorId == id)
                .Select(x => new { x.WarningVoltage, x.CriticalVoltage })
                .FirstOrDefaultAsync(ct);
            if (threshold != null)
            {
                dto.WarningVoltage = threshold.WarningVoltage;
                dto.CriticalVoltage = threshold.CriticalVoltage;
            }

            dto.Suspended = await IsSensorSuspendedForCallerAsync(id, ct);
            dto.Access = await CallerAccessAsync(id, ct);

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
        /// <param name="ct">Cancellation token bound by the framework.</param>
        /// <returns>The data for the specified sensor.</returns>
        [HttpGet("{sensorId}/data")]
        [SwaggerOperation(Summary = "Retrieves data for a specific sensor.")]
        [SwaggerResponse(200, "The data for the specified sensor.", typeof(IEnumerable<SensorDataDto>))]
        [SwaggerResponse(404, "Sensor not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> GetSensorData(
            int sensorId, string? timeRange, DateTime? startDate,
            DateTime? endDate, string? groupBy = "5m",
            int pageNumber = 1, int pageSize = 100, CancellationToken ct = default)
        {
            _logger.LogInformation("GetSensorData called by {@LogData}", new { CallerUserId = User.UserId(), sensorId });

            var sensor = await _context.Sensors.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sensorId, ct);
            if (sensor == null)
            {
                _logger.LogWarning("GetSensorData not found: {@LogData}", new { sensorId });
                return NotFound(new { message = "Sensor not found!" });
            }

            if (!await UserCanAccessSensorAsync(sensor.Id, ct))
            {
                _logger.LogWarning("GetSensorData forbidden for {@LogData}", new { CallerUserId = User.UserId(), sensorId });
                return Forbid();
            }

            if (await IsSensorSuspendedForCallerAsync(sensor.Id, ct))
                return StatusCode(403, new { message = "Sensor is suspended. Re-subscribe or turn it back on to view its data.", suspended = true });

            var (effectiveStart, effectiveEnd) = TimeRangeQueryExtensions.ResolveRange(timeRange, startDate, endDate);

            var query = WithinOwnershipWindow(
                _context.SensorData.Where(sd => sd.SensorId == sensorId))
                .ApplyTimeRange(effectiveStart, effectiveEnd);

            IEnumerable<SensorDataDto> result;
            int totalCount;

            var groupBySpan = !string.IsNullOrEmpty(groupBy) ? TimeRangeParser.Parse(groupBy) : null;
            if (groupBySpan.HasValue)
            {
                var grouped = await GetAveragedDataAsync(new[] { sensorId }, (long)groupBySpan.Value.TotalSeconds, effectiveStart, effectiveEnd, User.UserId(), IsAdmin(), ct);
                totalCount = grouped.Count;
                result = grouped;
            }
            else
            {
                totalCount = await query.CountAsync(ct);
                var sensorDataList = await query
                    .OrderBy(sd => sd.Timestamp)
                    .Skip((pageNumber - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync(ct);

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
        /// <param name="ct">Cancellation token bound by the framework.</param>
        /// <returns>The data for the specified sensors.</returns>
        [HttpGet("data")]
        [SwaggerOperation(Summary = "Retrieves data for multiple sensors.")]
        [SwaggerResponse(200, "The data for the specified sensors.", typeof(IEnumerable<SensorDataDto>))]
        [SwaggerResponse(404, "One or more sensors not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> GetMultipleSensorsData(
            [FromQuery] List<int> sensorIds, string? timeRange, DateTime? startDate, DateTime? endDate,
            string? groupBy = "5m",
            int pageNumber = 1, int pageSize = 100, CancellationToken ct = default)
        {
            _logger.LogInformation("GetMultipleSensorsData called by {@LogData}", new { CallerUserId = User.UserId(), sensorIds });

            var sensors = await _context.Sensors.Where(s => sensorIds.Contains(s.Id)).ToListAsync(ct);
            if (sensors.Count() != sensorIds.Count())
            {
                _logger.LogWarning("GetMultipleSensorsData not found: {@LogData}", new { sensorIds });
                return NotFound(new { message = "One or more sensors not found!" });
            }

            foreach (var sensor in sensors)
            {
                if (!await UserCanAccessSensorAsync(sensor.Id, ct))
                {
                    _logger.LogWarning("GetMultipleSensorsData forbidden for {@LogData}", new { CallerUserId = User.UserId(), sensorId = sensor.Id });
                    return Forbid();
                }
            }

            // Suspended sensors stay out of the batch (the caller can't read their data while off).
            var suspendedIds = await CallerSuspendedSensorIdsAsync(sensorIds, ct);
            var visibleSensorIds = sensorIds.Where(sid => !suspendedIds.Contains(sid)).ToList();

            var (effectiveStart, effectiveEnd) = TimeRangeQueryExtensions.ResolveRange(timeRange, startDate, endDate);

            var query = WithinOwnershipWindow(
                _context.SensorData.Where(sd => visibleSensorIds.Contains(sd.SensorId)))
                .ApplyTimeRange(effectiveStart, effectiveEnd);

            IEnumerable<SensorDataDto> result;
            int totalCount;

            var groupBySpan = !string.IsNullOrEmpty(groupBy) ? TimeRangeParser.Parse(groupBy) : null;
            if (groupBySpan.HasValue)
            {
                var grouped = await GetAveragedDataAsync(visibleSensorIds, (long)groupBySpan.Value.TotalSeconds, effectiveStart, effectiveEnd, User.UserId(), IsAdmin(), ct);
                totalCount = grouped.Count;
                result = grouped;
            }
            else
            {
                totalCount = await query.CountAsync(ct);
                var sensorDataList = await query
                    .OrderBy(sd => sd.Timestamp)
                    .Skip((pageNumber - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync(ct);

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
            IEnumerable<int> sensorIds, long bucketSeconds, DateTime? effectiveStart, DateTime? effectiveEnd,
            string? userId, bool isAdmin, CancellationToken ct = default)
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

            // Bound to the caller's own ownership window(s); see WithinOwnershipWindow. Admins see all data.
            if (!isAdmin)
            {
                whereClauses.Add(@"EXISTS (SELECT 1 FROM ""SensorOwnershipPeriods"" p
                    WHERE p.""UserId"" = @userId AND p.""SensorId"" = sd.""SensorId""
                    AND sd.""Timestamp"" >= p.""StartedAt""
                    AND (p.""EndedAt"" IS NULL OR sd.""Timestamp"" < p.""EndedAt""))");
                parameters.Add(new("userId", (object?)userId ?? DBNull.Value));
            }

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
                .ToListAsync(ct);
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

        private Task<string> GenerateSensorRegistrationCodeAsync(int length = 10) =>
            RegistrationCode.GenerateUniqueAsync(
                code => _context.Sensors.AnyAsync(s => s.RegistrationCode == code), length);

        /// <summary>
        /// Validates a raw sensor-data value as a number. Returns true and the parsed value on
        /// success; otherwise false. Both data-write endpoints (by id and by name) use this so they
        /// reject invalid input identically with a 400 rather than throwing a 500.
        /// </summary>
        private static bool TryParseSensorValue(string? raw, out double value)
        {
            value = 0;
            return !string.IsNullOrWhiteSpace(raw)
                && double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
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
        [Authorize(Roles = $"{RoleNames.Admin},{RoleNames.SensorAdmin}")]
        public async Task<IActionResult> GenerateMissingRegistrationCodes()
        {
            _logger.LogInformation("GenerateMissingRegistrationCodes called by {@LogData}", new { CallerUserId = User.UserId() });

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
        [Authorize(Roles = $"{RoleNames.Admin},{RoleNames.SensorAdmin}")]
        public async Task<IActionResult> GenerateMissingParentNames()
        {
            _logger.LogInformation("GenerateMissingParentNames called by {@LogData}", new { CallerUserId = User.UserId() });

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
        [Authorize(Policy = "ActiveSubscription")]
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

                // Open an ownership period that bounds which telemetry this user may read. The
                // first-ever owner starts at the epoch sentinel (sees all history); every later
                // (resale) owner starts now, so they never see the previous owner's readings.
                var firstEverOwner = !await _context.SensorOwnershipPeriods.AnyAsync(p => p.SensorId == sensor.Id);
                _context.SensorOwnershipPeriods.Add(new SensorOwnershipPeriod
                {
                    UserId = userId!,
                    SensorId = sensor.Id,
                    StartedAt = firstEverOwner ? SensorOwnershipPeriod.FirstOwnerStart : DateTime.UtcNow,
                    EndedAt = null
                });

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

            var userId = User.UserId()!;

            var callerRow = await _context.UserSensors
                .FirstOrDefaultAsync(us => us.UserId == userId && us.SensorId == id);
            var callerWasOwner = callerRow?.IsOwner == true;

            await CleanUserSensorDataAsync(id, userId);

            // An owner unclaim cascades to every shared recipient, because viewer access is tied to the
            // owner's ownership rather than their own. A recipient unclaim removes only that recipient.
            if (callerWasOwner)
            {
                var viewerIds = await _context.UserSensors
                    .Where(us => us.SensorId == id && !us.IsOwner)
                    .Select(us => us.UserId)
                    .ToListAsync();
                foreach (var viewerId in viewerIds)
                    await CleanUserSensorDataAsync(id, viewerId);
            }

            _ownership.InvalidateSensor(id);
            await _context.SaveChangesAsync();

            // Losing an owned sensor can leave a socket it discovered with no owner. Revoke any shares
            // on now-ownerless sockets so that socket access never outlives ownership.
            if (callerWasOwner)
            {
                await RevokeOrphanedSwitchSharesAsync(sensor.ParentName);
                await _context.SaveChangesAsync();
            }

            _logger.LogInformation("Sensor unclaimed by user {@LogData}", new { CallerUserId = User.UserId(), id, CascadedViewers = callerWasOwner });
            return Ok(new { message = "Sensor removed from your account." });
        }

        /// <summary>
        /// After an owned sensor is removed, revokes shares on any socket its gateway discovered that
        /// now has no remaining owner (no direct owner row and no other owned sensor under the gateway).
        /// This keeps socket-share lifetime tied to ownership without tracking who created each share.
        /// </summary>
        private async Task RevokeOrphanedSwitchSharesAsync(string? parentName)
        {
            if (string.IsNullOrEmpty(parentName)) return;

            var socketNames = await _context.DiscoveredDevices
                .Where(dd => dd.DiscoveredBy == parentName)
                .Select(dd => dd.Target)
                .Distinct()
                .ToListAsync();
            if (socketNames.Count == 0) return;

            var switches = await _context.Switches.Where(s => socketNames.Contains(s.Name)).ToListAsync();
            foreach (var sw in switches)
            {
                var hasDirectOwner = await _context.UserSwitches.AnyAsync(us => us.SwitchId == sw.Id && us.IsOwner);
                var hasIndirectOwner = await _context.DiscoveredDevices
                    .Where(dd => dd.Target == sw.Name)
                    .Join(_context.Sensors, dd => dd.DiscoveredBy, s => s.ParentName, (dd, s) => s.Id)
                    .Join(_context.UserSensors.Where(us => us.IsOwner), sid => sid, us => us.SensorId, (sid, us) => us.UserId)
                    .AnyAsync();
                if (hasDirectOwner || hasIndirectOwner) continue; // Still owned; keep its shares.

                var viewers = await _context.UserSwitches
                    .Where(us => us.SwitchId == sw.Id && !us.IsOwner)
                    .ToListAsync();
                if (viewers.Count == 0) continue;

                foreach (var viewer in viewers)
                {
                    _context.UserSwitches.Remove(viewer);
                    var periods = await _context.SwitchOwnershipPeriods
                        .Where(p => p.UserId == viewer.UserId && p.SwitchId == sw.Id && p.EndedAt == null)
                        .ToListAsync();
                    foreach (var p in periods) p.EndedAt = DateTime.UtcNow;
                    _context.UserSwitchCustomNames.RemoveRange(
                        _context.UserSwitchCustomNames.Where(x => x.UserId == viewer.UserId && x.SwitchId == sw.Id));
                }
                _ownership.InvalidateSwitch(sw.Id);
            }
        }

        /// <summary>
        /// Removes one user's membership and personal rows for a sensor: the UserSensor row, any open
        /// ownership period (closed now), and their custom name, activities, photos, and offline
        /// notifications. Does not save changes or invalidate the cache; the caller is responsible for that.
        /// </summary>
        private async Task CleanUserSensorDataAsync(int sensorId, string userId)
        {
            var membership = await _context.UserSensors
                .FirstOrDefaultAsync(us => us.UserId == userId && us.SensorId == sensorId);
            if (membership != null)
                _context.UserSensors.Remove(membership);

            var openPeriods = await _context.SensorOwnershipPeriods
                .Where(p => p.UserId == userId && p.SensorId == sensorId && p.EndedAt == null)
                .ToListAsync();
            foreach (var period in openPeriods)
                period.EndedAt = DateTime.UtcNow;

            var customName = await _context.UserSensorCustomNames
                .FirstOrDefaultAsync(x => x.UserId == userId && x.SensorId == sensorId);
            if (customName != null)
                _context.UserSensorCustomNames.Remove(customName);

            _context.UserSensorVoltageThresholds.RemoveRange(
                _context.UserSensorVoltageThresholds.Where(x => x.UserId == userId && x.SensorId == sensorId));

            _context.SensorActivities.RemoveRange(_context.SensorActivities.Where(a => a.UserId == userId && a.SensorId == sensorId));
            _context.SensorPhotos.RemoveRange(_context.SensorPhotos.Where(p => p.UserId == userId && p.SensorId == sensorId));
            _context.SensorOfflineNotifications.RemoveRange(_context.SensorOfflineNotifications.Where(n => n.UserId == userId && n.SensorId == sensorId));
        }

        /// <summary>Returns true when the caller owns this sensor (admins included). Only owners may share or revoke.</summary>
        private async Task<bool> UserIsSensorOwnerAsync(int sensorId)
        {
            if (IsAdmin()) return true;
            var userId = User.UserId();
            return userId != null && await _context.UserSensors
                .AnyAsync(us => us.UserId == userId && us.SensorId == sensorId && us.IsOwner);
        }

        /// <summary>Returns true when the caller may edit shared state (owner, Edit-tier share, or admin).</summary>
        private async Task<bool> UserCanEditSensorAsync(int sensorId)
        {
            if (IsAdmin()) return true;
            var userId = User.UserId();
            return userId != null && await _context.UserSensors.AnyAsync(us =>
                us.UserId == userId && us.SensorId == sensorId &&
                (us.IsOwner || us.Permission == SharePermission.Edit));
        }

        /// <summary>Returns the caller's relationship to a sensor for SensorDto.Access (admins count as owner).</summary>
        private async Task<string> CallerAccessAsync(int sensorId, CancellationToken ct = default)
        {
            if (IsAdmin()) return DeviceAccess.Owner;
            var userId = User.UserId();
            var row = await _context.UserSensors
                .Where(us => us.UserId == userId && us.SensorId == sensorId)
                .Select(us => new { us.IsOwner, us.Permission })
                .FirstOrDefaultAsync(ct);
            return row == null ? DeviceAccess.Owner : DeviceAccess.From(row.IsOwner, row.Permission);
        }

        /// <summary>Shares a sensor with another Garge user (Read or Edit). Owner only.</summary>
        [HttpPost("{id}/share")]
        [Authorize]
        [SwaggerOperation(Summary = "Shares a sensor with another user by email.")]
        [SwaggerResponse(200, "Sensor shared.")]
        [SwaggerResponse(403, "Only the owner can share.")]
        [SwaggerResponse(404, "Sensor or recipient not found.")]
        public async Task<IActionResult> ShareSensor(int id, [FromBody] ShareRequestDto dto)
        {
            var sensor = await _context.Sensors.FindAsync(id);
            if (sensor == null) return NotFound(new { message = "Sensor not found." });
            if (!await UserIsSensorOwnerAsync(id)) return Forbid();
            if (string.IsNullOrWhiteSpace(dto.Email))
                return BadRequest(new { message = "Recipient email is required." });

            var normalized = dto.Email.Trim().ToUpperInvariant();
            var target = await _context.Users
                .FirstOrDefaultAsync(u => u.NormalizedEmail == normalized && !u.IsDeleted);
            if (target == null)
                return NotFound(new { message = "No Garge account with that email." });
            if (target.Id == User.UserId())
                return BadRequest(new { message = "You already own this sensor." });

            var upsert = await DeviceShareHelper.UpsertShareAsync(
                _context.UserSensors,
                _context.SensorOwnershipPeriods,
                dto.Permission,
                matchesTarget: us => us.UserId == target.Id && us.SensorId == id,
                isOwner: us => us.IsOwner,
                setPermission: (us, p) => us.Permission = p,
                newMembership: () => new UserSensor { UserId = target.Id, SensorId = id, IsOwner = false, Permission = dto.Permission },
                // The recipient sees data from now on, not the owner's earlier private history.
                newPeriod: () => new SensorOwnershipPeriod { UserId = target.Id, SensorId = id, StartedAt = DateTime.UtcNow, EndedAt = null });

            if (upsert == ShareUpsertResult.AlreadyOwner)
                return BadRequest(new { message = "That user already owns this sensor." });

            await _context.SaveChangesAsync();
            _ownership.InvalidateSensor(id);
            await _hub.Clients.Group(DeviceHub.UserGroup(target.Id))
                .SendAsync("device-created", new { kind = "sensor", id });

            _logger.LogInformation("Sensor shared {@LogData}", new { id, OwnerUserId = User.UserId(), dto.Permission });
            return Ok(new { message = "Sensor shared." });
        }

        /// <summary>Revokes a user's share of a sensor. Owner only.</summary>
        [HttpDelete("{id}/share/{shareUserId}")]
        [Authorize]
        [SwaggerOperation(Summary = "Revokes a user's share of a sensor.")]
        [SwaggerResponse(200, "Share revoked.")]
        [SwaggerResponse(403, "Only the owner can revoke.")]
        [SwaggerResponse(404, "Share not found.")]
        public async Task<IActionResult> RevokeShare(int id, string shareUserId)
        {
            if (!await UserIsSensorOwnerAsync(id)) return Forbid();

            var share = await _context.UserSensors
                .FirstOrDefaultAsync(us => us.UserId == shareUserId && us.SensorId == id && !us.IsOwner);
            if (share == null) return NotFound(new { message = "Share not found." });

            await CleanUserSensorDataAsync(id, shareUserId);
            await _context.SaveChangesAsync();
            _ownership.InvalidateSensor(id);

            _logger.LogInformation("Sensor share revoked {@LogData}", new { id, OwnerUserId = User.UserId(), shareUserId });
            return Ok(new { message = "Share revoked." });
        }

        /// <summary>Lists the users a sensor is shared with. Owner only.</summary>
        [HttpGet("{id}/shares")]
        [Authorize]
        [SwaggerOperation(Summary = "Lists who a sensor is shared with.")]
        [SwaggerResponse(200, "Shares.", typeof(IEnumerable<ShareRecipientDto>))]
        [SwaggerResponse(403, "Only the owner can view shares.")]
        public async Task<IActionResult> ListShares(int id)
        {
            if (!await UserIsSensorOwnerAsync(id)) return Forbid();

            var shares = await DeviceShareHelper.ListRecipientsAsync(
                _context.UserSensors.Where(us => us.SensorId == id && !us.IsOwner),
                _context.Users,
                userIdOf: us => us.UserId,
                permissionOf: us => us.Permission,
                sharedAtOf: us => us.CreatedAt);

            return Ok(shares);
        }

        /// <summary>Turns off an owned sensor, hiding its data and freeing its capacity slot.</summary>
        [HttpPost("{id}/suspend")]
        [Authorize]
        [SwaggerOperation(Summary = "Suspend (turn off) an owned sensor.")]
        [SwaggerResponse(200, "Sensor suspended.")]
        [SwaggerResponse(404, "Sensor not owned by the caller.")]
        public async Task<IActionResult> SuspendSensor(int id)
        {
            var userId = User.UserId();
            var userSensor = await _context.UserSensors.FirstOrDefaultAsync(us => us.UserId == userId && us.SensorId == id && us.IsOwner);
            if (userSensor == null)
                return NotFound(new { message = "You do not own this sensor." });

            if (userSensor.SuspendedAt == null)
            {
                userSensor.SuspendedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                _ownership.InvalidateSensor(id);
                _logger.LogInformation("Sensor suspended by user {@LogData}", new { CallerUserId = User.UserId(), id });
            }
            return Ok(new { message = "Sensor turned off.", suspended = true });
        }

        /// <summary>Turns an owned sensor back on. Returns 400 if it would exceed the caller's capacity.</summary>
        [HttpPost("{id}/activate")]
        [Authorize]
        [SwaggerOperation(Summary = "Activate (turn on) an owned sensor.")]
        [SwaggerResponse(200, "Sensor activated.")]
        [SwaggerResponse(400, "Activating would exceed the plan limit.")]
        [SwaggerResponse(404, "Sensor not owned by the caller.")]
        public async Task<IActionResult> ActivateSensor(int id)
        {
            var userId = User.UserId();
            var userSensor = await _context.UserSensors.FirstOrDefaultAsync(us => us.UserId == userId && us.SensorId == id && us.IsOwner);
            if (userSensor == null)
                return NotFound(new { message = "You do not own this sensor." });

            if (userSensor.SuspendedAt != null)
            {
                var capacity = await _capacity.GetCapacityAsync(userId!);
                var activeOwned = await _capacity.GetActiveOwnedSensorCountAsync(userId!);
                if (activeOwned >= capacity)
                    return BadRequest(new { message = "Turning this on would exceed your plan. Turn off another sensor or upgrade your subscription." });

                userSensor.SuspendedAt = null;
                await _context.SaveChangesAsync();
                _ownership.InvalidateSensor(id);
                _logger.LogInformation("Sensor activated by user {@LogData}", new { CallerUserId = User.UserId(), id });
            }
            return Ok(new { message = "Sensor turned on.", suspended = false });
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
        [Authorize(Roles = $"{RoleNames.Admin},{RoleNames.SensorAdmin}")]
        public async Task<IActionResult> GenerateMissingDefaultNames()
        {
            _logger.LogInformation("GenerateMissingDefaultNames called by {@LogData}", new { CallerUserId = User.UserId() });

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
        [Authorize(Roles = $"{RoleNames.Admin},{RoleNames.SensorAdmin}")]
        [SwaggerOperation(Summary = "Creates a new sensor.")]
        [SwaggerResponse(201, "The created sensor.", typeof(SensorDto))]
        [SwaggerResponse(409, "Sensor name already exists.")]
        public async Task<IActionResult> CreateSensor([FromBody] CreateSensorDto sensorDto)
        {
            _logger.LogInformation("CreateSensor called by {@LogData}", new { CallerUserId = User.UserId(), sensorDto.Name, sensorDto.Type });

            if (!SensorTypes.IsAllowed(sensorDto.Type))
            {
                _logger.LogWarning("CreateSensor rejected unsupported type {@LogData}", new { sensorDto.Name, sensorDto.Type });
                return BadRequest(new { message = $"Unsupported sensor type '{sensorDto.Type}'. Allowed: {string.Join(", ", SensorTypes.Allowed)}." });
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
        [Authorize(Roles = $"{RoleNames.Admin},{RoleNames.SensorAdmin}")]
        [SwaggerOperation(Summary = "Creates new data for a specific sensor using sensorId.")]
        [SwaggerResponse(201, "The created sensor data.", typeof(SensorDataDto))]
        [SwaggerResponse(400, "Invalid value format.")]
        [SwaggerResponse(404, "Sensor not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> CreateSensorDataById(int sensorId, [FromBody] CreateSensorDataDto sensorDataDto)
        {
            _logger.LogInformation("CreateSensorDataById called by {@LogData}", new { CallerUserId = User.UserId(), sensorId });

            var sensor = await _context.Sensors.FindAsync(sensorId);
            if (sensor == null)
            {
                _logger.LogWarning("CreateSensorDataById not found: {@LogData}", new { sensorId });
                return NotFound(new { message = "Sensor not found!" });
            }

            if (!TryParseSensorValue(sensorDataDto.Value, out var parsedValue))
            {
                _logger.LogWarning("CreateSensorDataById bad request: Invalid value {@LogData}", new { sensorDataDto.Value, sensorId });
                return BadRequest(new { message = "Value must be a valid number." });
            }

            var sensorData = new SensorData
            {
                SensorId = sensorId,
                Value = Math.Round(parsedValue, 3).ToString(CultureInfo.InvariantCulture),
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
        [Authorize(Roles = $"{RoleNames.Admin},{RoleNames.SensorAdmin}")]
        [SwaggerOperation(Summary = "Creates new data for a specific sensor using sensorName.")]
        [SwaggerResponse(201, "The created sensor data.", typeof(SensorDataDto))]
        [SwaggerResponse(400, "Invalid value format.")]
        [SwaggerResponse(404, "Sensor not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> CreateSensorDataByName(string sensorName, [FromBody] CreateSensorDataDto sensorDataDto)
        {
            _logger.LogInformation("CreateSensorDataByName called by {@LogData}", new { CallerUserId = User.UserId(), sensorName });

            var sensor = await _context.Sensors.FirstOrDefaultAsync(s => s.Name == sensorName);
            if (sensor == null)
            {
                _logger.LogWarning("CreateSensorDataByName not found: {@LogData}", new { sensorName });
                return NotFound(new { message = "Sensor not found!" });
            }

            if (!TryParseSensorValue(sensorDataDto.Value, out var parsedValue))
            {
                _logger.LogWarning("CreateSensorDataByName bad request: Invalid value {@LogData}", new { sensorDataDto.Value, sensorName });
                return BadRequest(new { message = "Value must be a valid number." });
            }

            var sensorData = new SensorData
            {
                SensorId = sensor.Id,
                Value = Math.Round(parsedValue, 3).ToString(CultureInfo.InvariantCulture),
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
        [Authorize(Roles = $"{RoleNames.Admin},{RoleNames.SensorAdmin}")]
        [SwaggerOperation(Summary = "Updates an existing sensor.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(400, "Bad request.")]
        [SwaggerResponse(404, "Sensor not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> UpdateSensor(int id, [FromBody] UpdateSensorDto sensorDto)
        {
            _logger.LogInformation("UpdateSensor called by {@LogData}", new { CallerUserId = User.UserId(), id });

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
        [Authorize(Roles = $"{RoleNames.Admin},{RoleNames.SensorAdmin}")]
        [SwaggerOperation(Summary = "Deletes a sensor by its ID.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(404, "Sensor not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> DeleteSensor(int id)
        {
            _logger.LogInformation("DeleteSensor called by {@LogData}", new { CallerUserId = User.UserId(), id });

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
        [Authorize(Roles = $"{RoleNames.Admin},{RoleNames.SensorAdmin}")]
        [SwaggerOperation(Summary = "Deletes specific sensor data by its ID.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(404, "Sensor or sensor data not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> DeleteSensorData(int sensorId, int dataId)
        {
            _logger.LogInformation("DeleteSensorData called by {@LogData}", new { CallerUserId = User.UserId(), sensorId, dataId });

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
        [Authorize(Roles = $"{RoleNames.Admin},{RoleNames.SensorAdmin}")]
        [SwaggerOperation(Summary = "Deletes all sensor data for a specific sensor.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(404, "Sensor not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> DeleteAllSensorData(int sensorId)
        {
            _logger.LogInformation("DeleteAllSensorData called by {@LogData}", new { CallerUserId = User.UserId(), sensorId });

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
        /// Sets the caller's voltage color thresholds for a sensor. The reading is shown as a warning
        /// below <paramref name="dto"/>.WarningVoltage and as critical below CriticalVoltage. Upserts the row.
        /// </summary>
        /// <param name="sensorId">The ID of the sensor.</param>
        /// <param name="dto">The warning and critical voltages. Warning must be greater than critical.</param>
        /// <returns>The stored thresholds.</returns>
        [HttpPatch("{sensorId}/voltage-thresholds")]
        [SwaggerOperation(Summary = "Sets the caller's voltage color thresholds for a sensor.")]
        [SwaggerResponse(200, "Thresholds updated.", typeof(UserSensorVoltageThresholdDto))]
        [SwaggerResponse(400, "Invalid thresholds.")]
        [SwaggerResponse(404, "Sensor not found.")]
        [SwaggerResponse(403, "User does not have access to the sensor.")]
        public async Task<IActionResult> UpdateVoltageThresholds(
            int sensorId,
            [FromBody] UpdateVoltageThresholdDto dto)
        {
            _logger.LogInformation("UpdateVoltageThresholds called by {@LogData}", new { CallerUserId = User.UserId(), sensorId });

            var sensor = await _context.Sensors.FindAsync(sensorId);
            if (sensor == null)
            {
                _logger.LogWarning("UpdateVoltageThresholds not found: {@LogData}", new { sensorId });
                return NotFound(new { message = "Sensor not found!" });
            }

            if (!await UserCanAccessSensorAsync(sensor.Id))
            {
                _logger.LogWarning("UpdateVoltageThresholds forbidden for {@LogData}", new { CallerUserId = User.UserId(), sensorId });
                return Forbid();
            }

            var currentUserId = User.UserId();
            if (string.IsNullOrEmpty(currentUserId))
                return Unauthorized();

            if (dto.WarningVoltage <= dto.CriticalVoltage)
            {
                _logger.LogWarning("UpdateVoltageThresholds bad request: {@LogData}", new { sensorId, CallerUserId = currentUserId });
                return BadRequest(new { message = "WarningVoltage must be greater than CriticalVoltage." });
            }

            var entry = await _context.UserSensorVoltageThresholds
                .FirstOrDefaultAsync(x => x.UserId == currentUserId && x.SensorId == sensorId);

            if (entry == null)
            {
                entry = new UserSensorVoltageThreshold
                {
                    UserId = currentUserId,
                    SensorId = sensorId,
                    WarningVoltage = dto.WarningVoltage,
                    CriticalVoltage = dto.CriticalVoltage,
                    CreatedAt = DateTime.UtcNow
                };
                _context.UserSensorVoltageThresholds.Add(entry);
            }
            else
            {
                entry.WarningVoltage = dto.WarningVoltage;
                entry.CriticalVoltage = dto.CriticalVoltage;
            }

            await _context.SaveChangesAsync();

            var resultDto = new UserSensorVoltageThresholdDto
            {
                UserId = entry.UserId,
                SensorId = entry.SensorId,
                WarningVoltage = entry.WarningVoltage,
                CriticalVoltage = entry.CriticalVoltage,
                CreatedAt = entry.CreatedAt
            };

            _logger.LogInformation("Voltage thresholds updated for {@LogData}", new { sensorId, CallerUserId = currentUserId });
            return Ok(resultDto);
        }

        /// <summary>
        /// Clears the caller's voltage color thresholds for a sensor, so the reading is no longer colored.
        /// </summary>
        /// <param name="sensorId">The ID of the sensor.</param>
        [HttpDelete("{sensorId}/voltage-thresholds")]
        [SwaggerOperation(Summary = "Clears the caller's voltage color thresholds for a sensor.")]
        [SwaggerResponse(204, "Thresholds cleared.")]
        [SwaggerResponse(404, "Sensor not found.")]
        [SwaggerResponse(403, "User does not have access to the sensor.")]
        public async Task<IActionResult> ClearVoltageThresholds(int sensorId)
        {
            _logger.LogInformation("ClearVoltageThresholds called by {@LogData}", new { CallerUserId = User.UserId(), sensorId });

            var sensor = await _context.Sensors.FindAsync(sensorId);
            if (sensor == null)
                return NotFound(new { message = "Sensor not found!" });

            if (!await UserCanAccessSensorAsync(sensor.Id))
                return Forbid();

            var currentUserId = User.UserId();
            if (string.IsNullOrEmpty(currentUserId))
                return Unauthorized();

            var entry = await _context.UserSensorVoltageThresholds
                .FirstOrDefaultAsync(x => x.UserId == currentUserId && x.SensorId == sensorId);
            if (entry != null)
            {
                _context.UserSensorVoltageThresholds.Remove(entry);
                await _context.SaveChangesAsync();
            }

            _logger.LogInformation("Voltage thresholds cleared for {@LogData}", new { sensorId, CallerUserId = currentUserId });
            return NoContent();
        }

        /// <summary>
        /// Retrieves the latest data point for a specific sensor.
        /// </summary>
        /// <param name="sensorId">The ID of the sensor.</param>
        /// <param name="ct">Cancellation token bound by the framework.</param>
        /// <returns>The latest sensor data.</returns>
        [HttpGet("{sensorId}/latest-data")]
        [SwaggerOperation(Summary = "Retrieves the latest data point for a specific sensor.")]
        [SwaggerResponse(200, "The latest sensor data.", typeof(SensorDataDto))]
        [SwaggerResponse(404, "Sensor or sensor data not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> GetLatestSensorData(int sensorId, CancellationToken ct = default)
        {
            _logger.LogInformation("GetLatestSensorData called by {@LogData}", new { CallerUserId = User.UserId(), sensorId });

            var sensor = await _context.Sensors.FindAsync(new object[] { sensorId }, ct);
            if (sensor == null)
            {
                _logger.LogWarning("GetLatestSensorData not found: {@LogData}", new { sensorId });
                return NotFound(new { message = "Sensor not found!" });
            }

            if (!await UserCanAccessSensorAsync(sensor.Id, ct))
            {
                _logger.LogWarning("GetLatestSensorData forbidden for {@LogData}", new { CallerUserId = User.UserId(), sensorId });
                return Forbid();
            }

            if (await IsSensorSuspendedForCallerAsync(sensor.Id, ct))
                return StatusCode(403, new { message = "Sensor is suspended. Re-subscribe or turn it back on to view its data.", suspended = true });

            var latestData = await WithinOwnershipWindow(
                    _context.SensorData.Where(sd => sd.SensorId == sensorId))
                .OrderByDescending(sd => sd.Timestamp)
                .FirstOrDefaultAsync(ct);

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
