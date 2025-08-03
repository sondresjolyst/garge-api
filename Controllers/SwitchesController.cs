using garge_api.Dtos.Switch;
using garge_api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;
using System.Security.Claims;
using AutoMapper;
using garge_api.Models.Switch;
using garge_api.Services;

namespace garge_api.Controllers
{
    [ApiController]
    [Route("api/switches")]
    [EnableCors("AllowAllOrigins")]
    [Authorize]
    public class SwitchesController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly IMapper _mapper;
        private readonly ILogger<SwitchesController> _logger;

        public SwitchesController(ApplicationDbContext context, RoleManager<IdentityRole> roleManager, IMapper mapper, ILogger<SwitchesController> logger)
        {
            _context = context;
            _roleManager = roleManager;
            _mapper = mapper;
            _logger = logger;
        }

        private async Task<bool> UserHasRequiredRoleAsync(Switch switchEntity)
        {
            var userRoles = User.FindAll(ClaimTypes.Role).Select(r => r.Value).ToList();

            // Admins always have access
            if (userRoles.Contains("admin", StringComparer.OrdinalIgnoreCase) ||
                userRoles.Contains("switch_admin", StringComparer.OrdinalIgnoreCase) ||
                userRoles.Contains(switchEntity.Role, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            // Use ParentName for device access
            var accessibleParentNames = await _context.Sensors
                .Where(sensor => userRoles.Contains(sensor.Role))
                .Select(sensor => sensor.ParentName)
                .ToListAsync();

            // Check if any device the user can access has discovered this switch
            var discovered = await _context.DiscoveredDevices
                .AnyAsync(dd =>
                    accessibleParentNames.Contains(dd.DiscoveredBy) &&
                    dd.Target == switchEntity.Name);

            return discovered;
        }

        /// <summary>
        /// Retrieves all available switches.
        /// </summary>
        [HttpGet]
        [SwaggerOperation(Summary = "Retrieves all available switches.")]
        [SwaggerResponse(200, "A list of all switches.", typeof(IEnumerable<SwitchDto>))]
        public async Task<IActionResult> GetAllSwitches()
        {
            _logger.LogInformation("GetAllSwitches called by {User}", User.Identity?.Name);

            var allSwitches = await _context.Switches.ToListAsync();
            var accessibleSwitches = new List<Switch>();

            foreach (var sw in allSwitches)
            {
                if (await UserHasRequiredRoleAsync(sw))
                {
                    accessibleSwitches.Add(sw);
                }
            }

            var dtos = _mapper.Map<IEnumerable<SwitchDto>>(accessibleSwitches);

            _logger.LogInformation("Returning {Count} accessible switches for {User}", accessibleSwitches.Count, User.Identity?.Name);
            return Ok(dtos);
        }

        /// <summary>
        /// Retrieves a switch by its ID.
        /// </summary>
        [HttpGet("{id}")]
        [SwaggerOperation(Summary = "Retrieves a switch by its ID.")]
        [SwaggerResponse(200, "The switch with the specified ID.", typeof(SwitchDto))]
        [SwaggerResponse(404, "Switch not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> GetSwitch(int id)
        {
            _logger.LogInformation("GetSwitch called by {User} for Id={Id}", User.Identity?.Name, LogSanitizer.Sanitize(id.ToString()));

            var switchEntity = await _context.Switches.FindAsync(id);
            if (switchEntity == null)
            {
                _logger.LogWarning("GetSwitch not found: Id={Id}", LogSanitizer.Sanitize(id.ToString()));
                return NotFound(new { message = "Switch not found!" });
            }

            if (!await UserHasRequiredRoleAsync(switchEntity))
            {
                _logger.LogWarning("GetSwitch forbidden for {User} on Id={Id}", User.Identity?.Name, LogSanitizer.Sanitize(id.ToString()));
                return Forbid();
            }

            var dto = _mapper.Map<SwitchDto>(switchEntity);

            _logger.LogInformation("Returning switch Id={Id} to {User}", LogSanitizer.Sanitize(id.ToString()), User.Identity?.Name);
            return Ok(dto);
        }

        /// <summary>
        /// Creates a new switch.
        /// </summary>
        [HttpPost]
        [SwaggerOperation(Summary = "Creates a new switch.")]
        [SwaggerResponse(201, "The created switch.", typeof(SwitchDto))]
        [SwaggerResponse(409, "Switch name already exists.")]
        public async Task<IActionResult> CreateSwitch([FromBody] CreateSwitchDto switchDto)
        {
            _logger.LogInformation("CreateSwitch called by {User} with Name={Name}, Type={Type}", User.Identity?.Name, LogSanitizer.Sanitize(switchDto.Name), LogSanitizer.Sanitize(switchDto.Type));

            var userRoles = User.FindAll(ClaimTypes.Role).Select(r => r.Value).ToList();
            if (!userRoles.Contains("switch_admin", StringComparer.OrdinalIgnoreCase) &&
                !userRoles.Contains("admin", StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogWarning("CreateSwitch forbidden for {User}", User.Identity?.Name);
                return Forbid();
            }

            var switchEntity = _mapper.Map<Switch>(switchDto);
            switchEntity.Role = switchDto.Name;

            if (!await _roleManager.RoleExistsAsync(switchEntity.Role))
            {
                var roleResult = await _roleManager.CreateAsync(new IdentityRole(switchEntity.Role));
                if (!roleResult.Succeeded)
                {
                    _logger.LogError("CreateSwitch failed to create role for {Role}", LogSanitizer.Sanitize(switchEntity.Role));
                    return StatusCode(500, new { message = "Failed to create role!" });
                }
            }

            _context.Switches.Add(switchEntity);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("duplicate key") == true)
            {
                _logger.LogWarning("CreateSwitch conflict: Switch name {Name} already exists", LogSanitizer.Sanitize(switchDto.Name));
                return Conflict(new { message = "Switch name already exists!" });
            }

            var dto = _mapper.Map<SwitchDto>(switchEntity);

            _logger.LogInformation("Switch created: Id={Id}, Name={Name}", LogSanitizer.Sanitize(switchEntity.Id.ToString()), LogSanitizer.Sanitize(switchEntity.Name));
            return CreatedAtAction(nameof(GetSwitch), new { id = switchEntity.Id }, dto);
        }

        /// <summary>
        /// Updates an existing switch.
        /// </summary>
        [HttpPut("{id}")]
        [SwaggerOperation(Summary = "Updates an existing switch.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(400, "Bad request.")]
        [SwaggerResponse(404, "Switch not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> UpdateSwitch(int id, [FromBody] UpdateSwitchDto switchDto)
        {
            _logger.LogInformation("UpdateSwitch called by {User} for Id={Id}", User.Identity?.Name, LogSanitizer.Sanitize(id.ToString()));

            if (id != switchDto.Id)
            {
                _logger.LogWarning("UpdateSwitch bad request: Id mismatch {Id} != {DtoId}", LogSanitizer.Sanitize(id.ToString()), LogSanitizer.Sanitize(switchDto.Id.ToString()));
                return BadRequest();
            }

            var existingSwitch = await _context.Switches.FindAsync(id);
            if (existingSwitch == null)
            {
                _logger.LogWarning("UpdateSwitch not found: Id={Id}", LogSanitizer.Sanitize(id.ToString()));
                return NotFound(new { message = "Switch not found!" });
            }

            if (!await UserHasRequiredRoleAsync(existingSwitch))
            {
                _logger.LogWarning("UpdateSwitch forbidden for {User} on Id={Id}", User.Identity?.Name, LogSanitizer.Sanitize(id.ToString()));
                return Forbid();
            }

            _mapper.Map(switchDto, existingSwitch);

            _context.Entry(existingSwitch).State = EntityState.Modified;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Switch updated: Id={Id}", LogSanitizer.Sanitize(id.ToString()));
            return NoContent();
        }

        /// <summary>
        /// Deletes a switch by its ID.
        /// </summary>
        [HttpDelete("{id}")]
        [SwaggerOperation(Summary = "Deletes a switch by its ID.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(404, "Switch not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> DeleteSwitch(int id)
        {
            _logger.LogInformation("DeleteSwitch called by {User} for Id={Id}", User.Identity?.Name, LogSanitizer.Sanitize(id.ToString()));

            var switchEntity = await _context.Switches.FindAsync(id);
            if (switchEntity == null)
            {
                _logger.LogWarning("DeleteSwitch not found: Id={Id}", LogSanitizer.Sanitize(id.ToString()));
                return NotFound(new { message = "Switch not found!" });
            }

            if (!await UserHasRequiredRoleAsync(switchEntity))
            {
                _logger.LogWarning("DeleteSwitch forbidden for {User} on Id={Id}", User.Identity?.Name, LogSanitizer.Sanitize(id.ToString()));
                return Forbid();
            }

            _context.Switches.Remove(switchEntity);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Switch deleted: Id={Id}", LogSanitizer.Sanitize(id.ToString()));
            return NoContent();
        }

        /// <summary>
        /// Retrieves data for a specific switch.
        /// </summary>
        [HttpGet("{switchId}/data")]
        [SwaggerOperation(Summary = "Retrieves data for a specific switch.")]
        [SwaggerResponse(200, "The data for the specified switch.", typeof(IEnumerable<SwitchDataDto>))]
        [SwaggerResponse(404, "Switch not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> GetSwitchData(int switchId, string? timeRange, DateTime? startDate, DateTime? endDate)
        {
            _logger.LogInformation("GetSwitchData called by {User} for SwitchId={SwitchId}", User.Identity?.Name, LogSanitizer.Sanitize(switchId.ToString()));

            var switchEntity = await _context.Switches.FindAsync(switchId);
            if (switchEntity == null)
            {
                _logger.LogWarning("GetSwitchData not found: SwitchId={SwitchId}", LogSanitizer.Sanitize(switchId.ToString()));
                return NotFound(new { message = "Switch not found!" });
            }

            if (!await UserHasRequiredRoleAsync(switchEntity))
            {
                _logger.LogWarning("GetSwitchData forbidden for {User} on SwitchId={SwitchId}", User.Identity?.Name, LogSanitizer.Sanitize(switchId.ToString()));
                return Forbid();
            }

            var query = _context.SwitchData
                .Where(sd => sd.SwitchId == switchId)
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

            var switchDataList = await query.OrderBy(sd => sd.Timestamp).ToListAsync();
            var dtos = _mapper.Map<IEnumerable<SwitchDataDto>>(switchDataList);

            _logger.LogInformation("Returning {Count} switch data entries for SwitchId={SwitchId}", switchDataList.Count, LogSanitizer.Sanitize(switchId.ToString()));
            return Ok(dtos);
        }

        /// <summary>
        /// Retrieves state for a specific switch.
        /// </summary>
        [HttpGet("{switchId}/state")]
        [SwaggerOperation(Summary = "Retrieves state for a specific switch.")]
        [SwaggerResponse(200, "The state for the specified switch.", typeof(IEnumerable<SwitchDataDto>))]
        [SwaggerResponse(404, "Switch not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> GetSwitchState(int switchId)
        {
            _logger.LogInformation("GetSwitchState called by {User} for SwitchId={SwitchId}", User.Identity?.Name, LogSanitizer.Sanitize(switchId.ToString()));

            var switchEntity = await _context.Switches.FindAsync(switchId);
            if (switchEntity == null)
            {
                _logger.LogWarning("GetSwitchState not found: SwitchId={SwitchId}", LogSanitizer.Sanitize(switchId.ToString()));
                return NotFound(new { message = "Switch not found!" });
            }

            if (!await UserHasRequiredRoleAsync(switchEntity))
            {
                _logger.LogWarning("GetSwitchState forbidden for {User} on SwitchId={SwitchId}", User.Identity?.Name, LogSanitizer.Sanitize(switchId.ToString()));
                return Forbid();
            }

            var query = await _context.SwitchData
                .Where(sd => sd.SwitchId == switchId)
                .OrderByDescending(sd => sd.Timestamp)
                .FirstOrDefaultAsync();

            if (query == null)
            {
                _logger.LogWarning("GetSwitchState no data found for SwitchId={SwitchId}", LogSanitizer.Sanitize(switchId.ToString()));
                return NotFound(new { message = "No data found for this switch!" });
            }

            var dto = _mapper.Map<SwitchDataDto>(query);

            _logger.LogInformation("Returning switch state for SwitchId={SwitchId}", LogSanitizer.Sanitize(switchId.ToString()));
            return Ok(dto);
        }

        /// <summary>
        /// Creates new data for a specific switch.
        /// </summary>
        [HttpPost("{switchId}/data")]
        [SwaggerOperation(Summary = "Creates new data for a specific switch.")]
        [SwaggerResponse(201, "The created switch data.", typeof(SwitchDataDto))]
        [SwaggerResponse(400, "Invalid value format.")]
        [SwaggerResponse(404, "Switch not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> CreateSwitchData(int switchId, [FromBody] CreateSwitchDataDto switchDataDto)
        {
            _logger.LogInformation("CreateSwitchData called by {User} for SwitchId={SwitchId} with Value={Value}", User.Identity?.Name, LogSanitizer.Sanitize(switchId.ToString()), LogSanitizer.Sanitize(switchDataDto.Value));

            var switchEntity = await _context.Switches.FindAsync(switchId);
            if (switchEntity == null)
            {
                _logger.LogWarning("CreateSwitchData not found: SwitchId={SwitchId}", LogSanitizer.Sanitize(switchId.ToString()));
                return NotFound(new { message = "Switch not found!" });
            }

            if (!await UserHasRequiredRoleAsync(switchEntity))
            {
                _logger.LogWarning("CreateSwitchData forbidden for {User} on SwitchId={SwitchId}", User.Identity?.Name, LogSanitizer.Sanitize(switchId.ToString()));
                return Forbid();
            }

            var validStates = new[] { "ON", "OFF" };
            if (!validStates.Contains(switchDataDto.Value, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogWarning("CreateSwitchData bad request: Invalid value {Value} for SwitchId={SwitchId}", LogSanitizer.Sanitize(switchDataDto.Value), LogSanitizer.Sanitize(switchId.ToString()));
                return BadRequest(new { message = "The value must be either 'ON' or 'OFF'." });
            }

            var switchData = _mapper.Map<SwitchData>(switchDataDto);
            switchData.SwitchId = switchId;
            switchData.Value = switchDataDto.Value.ToUpperInvariant();
            switchData.Timestamp = DateTime.UtcNow;

            _context.SwitchData.Add(switchData);
            await _context.SaveChangesAsync();

            var dto = _mapper.Map<SwitchDataDto>(switchData);

            _logger.LogInformation("Switch data created: Id={Id}, SwitchId={SwitchId}, Value={Value}", LogSanitizer.Sanitize(switchData.Id.ToString()), LogSanitizer.Sanitize(switchId.ToString()), LogSanitizer.Sanitize(switchData.Value));
            return CreatedAtAction(nameof(GetSwitchData), new { switchId = switchId }, dto);
        }

        /// <summary>
        /// Retrieves data for multiple switches.
        /// </summary>
        [HttpGet("data")]
        [SwaggerOperation(Summary = "Retrieves data for multiple switches.")]
        [SwaggerResponse(200, "The data for the specified switches.", typeof(IEnumerable<SwitchDataDto>))]
        [SwaggerResponse(404, "One or more switches not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> GetMultipleSwitchesData([FromQuery] List<int> switchIds, string? timeRange, DateTime? startDate, DateTime? endDate)
        {
            _logger.LogInformation("GetMultipleSwitchesData called by {User} for SwitchIds={SwitchIds}", User.Identity?.Name, LogSanitizer.Sanitize(string.Join(",", switchIds)));

            var switches = await _context.Switches.Where(s => switchIds.Contains(s.Id)).ToListAsync();
            if (switches.Count != switchIds.Count)
            {
                _logger.LogWarning("GetMultipleSwitchesData not found: One or more switches not found for SwitchIds={SwitchIds}", LogSanitizer.Sanitize(string.Join(",", switchIds)));
                return NotFound(new { message = "One or more switches not found!" });
            }

            foreach (var switchEntity in switches)
            {
                if (!await UserHasRequiredRoleAsync(switchEntity))
                {
                    _logger.LogWarning("GetMultipleSwitchesData forbidden for {User} on SwitchId={SwitchId}", User.Identity?.Name, LogSanitizer.Sanitize(switchEntity.Id.ToString()));
                    return Forbid();
                }
            }

            var query = _context.SwitchData
                .Where(sd => switchIds.Contains(sd.SwitchId))
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

            var switchDataList = await query.OrderBy(sd => sd.Timestamp).ToListAsync();
            var dtos = _mapper.Map<IEnumerable<SwitchDataDto>>(switchDataList);

            _logger.LogInformation("Returning {Count} switch data entries for SwitchIds={SwitchIds}", switchDataList.Count, LogSanitizer.Sanitize(string.Join(",", switchIds)));
            return Ok(dtos);
        }

        /// <summary>
        /// Deletes specific switch data by its ID.
        /// </summary>
        [HttpDelete("{switchId}/data/{dataId}")]
        [SwaggerOperation(Summary = "Deletes specific switch data by its ID.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(404, "Switch or switch data not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> DeleteSwitchData(int switchId, int dataId)
        {
            _logger.LogInformation("DeleteSwitchData called by {User} for SwitchId={SwitchId}, DataId={DataId}", User.Identity?.Name, LogSanitizer.Sanitize(switchId.ToString()), LogSanitizer.Sanitize(dataId.ToString()));

            var switchEntity = await _context.Switches.FindAsync(switchId);
            if (switchEntity == null)
            {
                _logger.LogWarning("DeleteSwitchData not found: SwitchId={SwitchId}", LogSanitizer.Sanitize(switchId.ToString()));
                return NotFound(new { message = "Switch not found!" });
            }

            if (!await UserHasRequiredRoleAsync(switchEntity))
            {
                _logger.LogWarning("DeleteSwitchData forbidden for {User} on SwitchId={SwitchId}", User.Identity?.Name, LogSanitizer.Sanitize(switchId.ToString()));
                return Forbid();
            }

            var switchData = await _context.SwitchData.FindAsync(dataId);
            if (switchData == null || switchData.SwitchId != switchId)
            {
                _logger.LogWarning("DeleteSwitchData not found: DataId={DataId} for SwitchId={SwitchId}", LogSanitizer.Sanitize(dataId.ToString()), LogSanitizer.Sanitize(switchId.ToString()));
                return NotFound(new { message = "Switch data not found!" });
            }

            _context.SwitchData.Remove(switchData);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Switch data deleted: DataId={DataId}, SwitchId={SwitchId}", LogSanitizer.Sanitize(dataId.ToString()), LogSanitizer.Sanitize(switchId.ToString()));
            return NoContent();
        }

        /// <summary>
        /// Creates new data for a specific switch using switchName.
        /// </summary>
        [HttpPost("name/{switchName}/data")]
        [SwaggerOperation(Summary = "Creates new data for a specific switch using switchName.")]
        [SwaggerResponse(201, "The created switch data.", typeof(SwitchDataDto))]
        [SwaggerResponse(400, "Invalid value format.")]
        [SwaggerResponse(404, "Switch not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> CreateSwitchDataByName(string switchName, [FromBody] CreateSwitchDataDto switchDataDto)
        {
            _logger.LogInformation("CreateSwitchDataByName called by {User} for SwitchName={SwitchName} with Value={Value}", User.Identity?.Name, LogSanitizer.Sanitize(switchName), LogSanitizer.Sanitize(switchDataDto.Value));

            var switchEntity = await _context.Switches.FirstOrDefaultAsync(s => s.Name == switchName);
            if (switchEntity == null)
            {
                _logger.LogWarning("CreateSwitchDataByName not found: SwitchName={SwitchName}", LogSanitizer.Sanitize(switchName));
                return NotFound(new { message = "Switch not found!" });
            }

            if (!await UserHasRequiredRoleAsync(switchEntity))
            {
                _logger.LogWarning("CreateSwitchDataByName forbidden for {User} on SwitchName={SwitchName}", User.Identity?.Name, LogSanitizer.Sanitize(switchName));
                return Forbid();
            }

            var validStates = new[] { "ON", "OFF" };
            if (!validStates.Contains(switchDataDto.Value, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogWarning("CreateSwitchDataByName bad request: Invalid value {Value} for SwitchName={SwitchName}", LogSanitizer.Sanitize(switchDataDto.Value), LogSanitizer.Sanitize(switchName));
                return BadRequest(new { message = "The value must be either 'ON' or 'OFF'." });
            }

            var switchData = _mapper.Map<SwitchData>(switchDataDto);
            switchData.SwitchId = switchEntity.Id;
            switchData.Value = switchDataDto.Value.ToUpperInvariant();
            switchData.Timestamp = DateTime.UtcNow;

            _context.SwitchData.Add(switchData);
            await _context.SaveChangesAsync();

            var dto = _mapper.Map<SwitchDataDto>(switchData);

            _logger.LogInformation("Switch data created: Id={Id}, SwitchName={SwitchName}, Value={Value}", LogSanitizer.Sanitize(switchData.Id.ToString()), LogSanitizer.Sanitize(switchName), LogSanitizer.Sanitize(switchData.Value));
            return CreatedAtAction(nameof(GetSwitchData), new { switchId = switchEntity.Id }, dto);
        }

        /// <summary>
        /// Deletes all data for a specific switch.
        /// </summary>
        [HttpDelete("{switchId}/data")]
        [SwaggerOperation(Summary = "Deletes all data for a specific switch.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(404, "Switch not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> DeleteAllSwitchData(int switchId)
        {
            _logger.LogInformation("DeleteAllSwitchData called by {User} for SwitchId={SwitchId}", User.Identity?.Name, LogSanitizer.Sanitize(switchId.ToString()));

            var switchEntity = await _context.Switches.FindAsync(switchId);
            if (switchEntity == null)
            {
                _logger.LogWarning("DeleteAllSwitchData not found: SwitchId={SwitchId}", LogSanitizer.Sanitize(switchId.ToString()));
                return NotFound(new { message = "Switch not found!" });
            }

            if (!await UserHasRequiredRoleAsync(switchEntity))
            {
                _logger.LogWarning("DeleteAllSwitchData forbidden for {User} on SwitchId={SwitchId}", User.Identity?.Name, LogSanitizer.Sanitize(switchId.ToString()));
                return Forbid();
            }

            var switchData = _context.SwitchData.Where(sd => sd.SwitchId == switchId);
            _context.SwitchData.RemoveRange(switchData);
            await _context.SaveChangesAsync();

            _logger.LogInformation("All switch data deleted for SwitchId={SwitchId}", LogSanitizer.Sanitize(switchId.ToString()));
            return NoContent();
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
    }
}
