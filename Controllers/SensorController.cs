using garge_api.Dtos.Sensor;
using garge_api.Models;
using garge_api.Models.Sensor;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;
using System.Globalization;
using System.Security.Claims;
using AutoMapper;
using System.Security.AccessControl;

namespace garge_api.Controllers
{
    [ApiController]
    [Route("api/sensors")]
    [EnableCors("AllowAllOrigins")]
    [Authorize]
    public class SensorController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly IMapper _mapper;
        private static readonly List<string> AdminRoles = new() { "sensor_admin", "admin" };

        public SensorController(ApplicationDbContext context, RoleManager<IdentityRole> roleManager, IMapper mapper)
        {
            _context = context;
            _roleManager = roleManager;
            _mapper = mapper;
        }

        private bool UserHasRequiredRole(string sensorRole)
        {
            var userRoles = User.FindAll(ClaimTypes.Role).Select(r => r.Value).ToList();
            return userRoles.Any(role => role.Equals(sensorRole, StringComparison.OrdinalIgnoreCase)) ||
                   userRoles.Any(role => AdminRoles.Contains(role, StringComparer.OrdinalIgnoreCase));
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
            var userRoles = User.FindAll(ClaimTypes.Role).Select(r => r.Value).ToList();
            var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            List<Sensor> sensors;
            if (userRoles.Any(role => AdminRoles.Contains(role, StringComparer.OrdinalIgnoreCase)))
            {
                sensors = await _context.Sensors.ToListAsync();
            }
            else
            {
                sensors = await _context.Sensors
                    .Where(sensor => userRoles.Contains(sensor.Role))
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
            var sensor = await _context.Sensors.FindAsync(id);
            if (sensor == null)
                return NotFound(new { message = "Sensor not found!" });

            if (!UserHasRequiredRole(sensor.Role))
                return Forbid();

            var dto = _mapper.Map<SensorDto>(sensor);

            var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var customName = await _context.UserSensorCustomNames
                .Where(x => x.UserId == currentUserId && x.SensorId == id)
                .Select(x => x.CustomName)
                .FirstOrDefaultAsync();

            if (!string.IsNullOrEmpty(customName))
                dto.CustomName = customName;

            return Ok(dto);
        }

        /// <summary>
        /// Retrieves data for a specific sensor.
        /// </summary>
        /// <param name="sensorId">The ID of the sensor to retrieve data for.</param>
        /// <param name="timeRange">The time range for the data (e.g., 5m, 10m, 30m, 1h). Takes precedence over startDate and endDate.</param>
        /// <param name="startDate">The start date for the data range.</param>
        /// <param name="endDate">The end date for the data range.</param>
        /// <param name="average">Whether to return averaged data.</param>
        /// <param name="groupBy">The level to group the data by (e.g., "minute", "hour", "day", "5m", "10h", "2d").</param>
        /// <returns>The data for the specified sensor.</returns>
        [HttpGet("{sensorId}/data")]
        [SwaggerOperation(Summary = "Retrieves data for a specific sensor.")]
        [SwaggerResponse(200, "The data for the specified sensor.", typeof(IEnumerable<SensorDataDto>))]
        [SwaggerResponse(404, "Sensor not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> GetSensorData(
            int sensorId, string? timeRange, DateTime? startDate,
            DateTime? endDate, bool average = false, string? groupBy = "minute", 
            int pageNumber = 1, int pageSize = 100)
        {
            var sensor = await _context.Sensors.FindAsync(sensorId);
            if (sensor == null)
                return NotFound(new { message = "Sensor not found!" });

            if (!UserHasRequiredRole(sensor.Role))
                return Forbid();

            var query = _context.SensorData
                .Where(sd => sd.SensorId == sensorId)
                .AsQueryable();

            if (!string.IsNullOrEmpty(timeRange))
            {
                var now = DateTime.UtcNow;
                var timeSpan = ParseTimeRange(timeRange);
                if (timeSpan.HasValue)
                    query = query.Where(sd => sd.Timestamp >= now.Subtract(timeSpan.Value));
            }
            else
            {
                if (startDate.HasValue)
                    query = query.Where(sd => sd.Timestamp >= startDate.Value);
                if (endDate.HasValue)
                    query = query.Where(sd => sd.Timestamp <= endDate.Value);
            }

            IEnumerable<SensorDataDto> result;
            int totalCount;

            if (average)
            {
                var grouped = query
                    .AsEnumerable()
                    .GroupBy(sd => GetGroupingKey(sd.Timestamp, groupBy))
                    .Select(g => new SensorDataDto
                    {
                        Id = g.First().Id,
                        SensorId = sensorId,
                        Timestamp = g.Key,
                        Value = Math.Round(g.Average(sd => double.Parse(sd.Value)), 3).ToString(CultureInfo.InvariantCulture)
                    })
                    .OrderBy(sd => sd.Timestamp)
                    .ToList();

                totalCount = grouped.Count;
                result = grouped
                    .Skip((pageNumber - 1) * pageSize)
                    .Take(pageSize)
                    .ToList();
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
        /// <param name="average">Whether to return averaged data.</param>
        /// <param name="groupBy">The level to group the data by (e.g., "minute", "hour", "day", "5m", "10h", "2d").</param>
        /// <returns>The data for the specified sensors.</returns>
        [HttpGet("data")]
        [SwaggerOperation(Summary = "Retrieves data for multiple sensors.")]
        [SwaggerResponse(200, "The data for the specified sensors.", typeof(IEnumerable<SensorDataDto>))]
        [SwaggerResponse(404, "One or more sensors not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> GetMultipleSensorsData(
            [FromQuery] List<int> sensorIds, string? timeRange, DateTime? startDate, DateTime? endDate,
            bool average = false, string? groupBy = "minute",
            int pageNumber = 1, int pageSize = 100)
        {
            var sensors = await _context.Sensors.Where(s => sensorIds.Contains(s.Id)).ToListAsync();
            if (sensors.Count() != sensorIds.Count())
                return NotFound(new { message = "One or more sensors not found!" });

            foreach (var sensor in sensors)
            {
                if (!UserHasRequiredRole(sensor.Role))
                    return Forbid();
            }

            var query = _context.SensorData
                .Where(sd => sensorIds.Contains(sd.SensorId))
                .AsQueryable();

            if (!string.IsNullOrEmpty(timeRange))
            {
                var now = DateTime.UtcNow;
                var timeSpan = ParseTimeRange(timeRange);
                if (timeSpan.HasValue)
                    query = query.Where(sd => sd.Timestamp >= now.Subtract(timeSpan.Value));
            }
            else
            {
                if (startDate.HasValue)
                    query = query.Where(sd => sd.Timestamp >= startDate.Value);
                if (endDate.HasValue)
                    query = query.Where(sd => sd.Timestamp <= endDate.Value);
            }

            IEnumerable<SensorDataDto> result;
            int totalCount;

            if (average)
            {
                var grouped = query
                    .AsEnumerable()
                    .GroupBy(sd => new { sd.SensorId, Timestamp = GetGroupingKey(sd.Timestamp, groupBy) })
                    .Select(g => new SensorDataDto
                    {
                        Id = g.First().Id,
                        SensorId = g.Key.SensorId,
                        Timestamp = g.Key.Timestamp,
                        Value = Math.Round(g.Average(sd => double.Parse(sd.Value)), 3).ToString(CultureInfo.InvariantCulture)
                    })
                    .OrderBy(sd => sd.Timestamp)
                    .ToList();

                totalCount = grouped.Count();
                result = grouped
                    .Skip((pageNumber - 1) * pageSize)
                    .Take(pageSize)
                    .ToList();
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

            return Ok(new
            {
                TotalCount = totalCount,
                PageNumber = pageNumber,
                PageSize = pageSize,
                Data = result
            });
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
            var random = new Random();
            string code;
            bool exists;

            do
            {
                code = new string(Enumerable.Repeat(chars, length)
                    .Select(s => s[random.Next(s.Length)]).ToArray());
                exists = await _context.Sensors.AnyAsync(s => s.RegistrationCode == code);
            } while (exists);

            return code;
        }

        [HttpPost("generate-missing-codes")]
        public async Task<IActionResult> GenerateMissingRegistrationCodes()
        {
            if (!UserHasRequiredRole("sensor_admin"))
                return Forbid();

            var sensorsWithoutCode = await _context.Sensors
                .Where(s => string.IsNullOrEmpty(s.RegistrationCode))
                .ToListAsync();

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
            return Ok(new { updated = sensorsWithoutCode.Count });
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
            if (string.IsNullOrWhiteSpace(dto.RegistrationCode))
                return BadRequest(new { message = "Registration code is required." });

            var sensor = await _context.Sensors.FirstOrDefaultAsync(s => s.RegistrationCode == dto.RegistrationCode);
            if (sensor == null)
                return NotFound(new { message = "Invalid registration code." });

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
                return Unauthorized();

            var userManager = HttpContext.RequestServices.GetRequiredService<UserManager<User>>();
            if (!await userManager.IsInRoleAsync(user, sensor.Role))
            {
                var result = await userManager.AddToRoleAsync(user, sensor.Role);
                if (!result.Succeeded)
                    return StatusCode(500, new { message = "Failed to assign sensor role to user." });
            }

            return Ok(new { message = "Sensor successfully claimed and assigned to your account." });
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
            if (!UserHasRequiredRole("sensor_admin"))
                return Forbid();

            var sensorsWithoutDefaultName = await _context.Sensors
                .Where(s => string.IsNullOrEmpty(s.DefaultName))
                .ToListAsync();

            foreach (var sensor in sensorsWithoutDefaultName)
            {
                sensor.DefaultName = GenerateDefaultName(sensor.Name);
            }

            await _context.SaveChangesAsync();
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
            if (!UserHasRequiredRole("sensor_admin"))
                return Forbid();

            var sensor = new Sensor
            {
                Name = sensorDto.Name,
                Type = sensorDto.Type,
                Role = sensorDto.Name,
                RegistrationCode = await GenerateSensorRegistrationCodeAsync(),
                DefaultName = GenerateDefaultName(sensorDto.Name)
            };

            if (!await _roleManager.RoleExistsAsync(sensor.Role))
            {
                var roleResult = await _roleManager.CreateAsync(new IdentityRole(sensor.Role));
                if (!roleResult.Succeeded)
                    return StatusCode(500, new { message = "Failed to create role!" });
            }

            _context.Sensors.Add(sensor);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("duplicate key") == true)
            {
                return Conflict(new { message = "Sensor name already exists!" });
            }

            var dto = _mapper.Map<SensorDto>(sensor);
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
            var sensor = await _context.Sensors.FindAsync(sensorId);
            if (sensor == null)
                return NotFound(new { message = "Sensor not found!" });

            if (!UserHasRequiredRole(sensor.Role))
                return Forbid();

            var sensorData = new SensorData
            {
                SensorId = sensorId,
                Value = Math.Round(double.Parse(sensorDataDto.Value), 3).ToString(CultureInfo.InvariantCulture),
                Timestamp = DateTime.UtcNow
            };

            _context.SensorData.Add(sensorData);
            await _context.SaveChangesAsync();

            var dto = _mapper.Map<SensorDataDto>(sensorData);
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
            var sensor = await _context.Sensors.FirstOrDefaultAsync(s => s.Name == sensorName);
            if (sensor == null)
                return NotFound(new { message = "Sensor not found!" });

            if (!UserHasRequiredRole(sensor.Role))
                return Forbid();

            var sensorData = new SensorData
            {
                SensorId = sensor.Id,
                Value = Math.Round(double.Parse(sensorDataDto.Value), 3).ToString(CultureInfo.InvariantCulture),
                Timestamp = DateTime.UtcNow
            };

            _context.SensorData.Add(sensorData);
            await _context.SaveChangesAsync();

            var dto = _mapper.Map<SensorDataDto>(sensorData);
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
            var existingSensor = await _context.Sensors.FindAsync(id);
            if (existingSensor == null)
                return NotFound(new { message = "Sensor not found!" });

            if (!UserHasRequiredRole(existingSensor.Role))
                return Forbid();

            existingSensor.Name = sensorDto.Name;
            existingSensor.Type = sensorDto.Type;
            existingSensor.Role = sensorDto.Role;

            _context.Entry(existingSensor).State = EntityState.Modified;
            await _context.SaveChangesAsync();

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
            var sensor = await _context.Sensors.FindAsync(id);
            if (sensor == null)
                return NotFound(new { message = "Sensor not found!" });

            if (!UserHasRequiredRole(sensor.Role))
                return Forbid();

            _context.Sensors.Remove(sensor);
            await _context.SaveChangesAsync();

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
            var sensor = await _context.Sensors.FindAsync(sensorId);
            if (sensor == null)
                return NotFound(new { message = "Sensor not found!" });

            if (!UserHasRequiredRole(sensor.Role))
                return Forbid();

            var sensorData = await _context.SensorData.FindAsync(dataId);
            if (sensorData == null || sensorData.SensorId != sensorId)
                return NotFound(new { message = "Sensor data not found!" });

            _context.SensorData.Remove(sensorData);
            await _context.SaveChangesAsync();

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
            var sensor = await _context.Sensors.FindAsync(sensorId);
            if (sensor == null)
                return NotFound(new { message = "Sensor not found!" });

            if (!UserHasRequiredRole(sensor.Role))
                return Forbid();

            var sensorData = _context.SensorData.Where(sd => sd.SensorId == sensorId);
            _context.SensorData.RemoveRange(sensorData);
            await _context.SaveChangesAsync();

            return NoContent();
        }

        /// <summary>
        /// Updates the custom name of a sensor.
        /// </summary>
        /// <param name="sensorId">The ID of the sensor to update.</param>
        /// <param name="dto">The new custom name.</param>
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
            var sensor = await _context.Sensors.FindAsync(sensorId);
            if (sensor == null)
                return NotFound(new { message = "Sensor not found!" });

            if (!UserHasRequiredRole(sensor.Role))
                return Forbid();

            var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var isSensorAdmin = User.Claims
                .Where(c => c.Type == ClaimTypes.Role)
                .Select(c => c.Value)
                .Any(role => AdminRoles.Contains(role, StringComparer.OrdinalIgnoreCase));
            var targetUserId = userId ?? currentUserId;

            if (!isSensorAdmin && targetUserId != currentUserId)
                return Forbid();

            if (string.IsNullOrEmpty(targetUserId))
                return Unauthorized();

            if (string.IsNullOrWhiteSpace(dto.CustomName) || dto.CustomName.Length > 50)
                return BadRequest(new { message = "CustomName is required and must be at most 50 characters." });

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

            return Ok(resultDto);
        }
    }
}
