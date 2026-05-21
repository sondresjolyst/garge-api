using garge_api.Dtos.Switch;
using garge_api.Helpers;
using garge_api.Hubs;
using garge_api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;
using System.Security.Claims;
using MapsterMapper;
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
        private readonly IMapper _mapper;
        private readonly ILogger<SwitchesController> _logger;
        private readonly IDeviceOwnershipService _ownership;
        private readonly IHubContext<DeviceHub> _hub;

        public SwitchesController(ApplicationDbContext context, IMapper mapper, ILogger<SwitchesController> logger, IDeviceOwnershipService ownership, IHubContext<DeviceHub> hub)
        {
            _context = context;
            _mapper = mapper;
            _logger = logger;
            _ownership = ownership;
            _hub = hub;
        }

        private bool IsSwitchAdmin()
        {
            var userRoles = User.FindAll(ClaimTypes.Role).Select(r => r.Value).ToList();
            return userRoles.Contains("admin", StringComparer.OrdinalIgnoreCase) ||
                   userRoles.Contains("SwitchAdmin", StringComparer.OrdinalIgnoreCase);
        }

        private async Task<bool> UserHasRequiredRoleAsync(Switch switchEntity)
        {
            // Admins always have access
            if (IsSwitchAdmin()) return true;

            var userId = User.UserId();
            if (string.IsNullOrEmpty(userId)) return false;

            // Delegate to the shared ownership service so the request-time access
            // check and the SignalR dispatch path use identical logic.
            return await _ownership.CanUserAccessSwitchAsync(userId, switchEntity.Id);
        }

        /// <summary>
        /// Bounds switch telemetry to the caller's own ownership window(s): a direct ownership period
        /// OR — for indirect access via the discovered-device chain — the period of an owned sensor
        /// that maps to this switch (switch name -> DiscoveredDevice.Target, DiscoveredBy ->
        /// Sensor.ParentName). A new owner of a re-claimed/resold switch never sees the previous
        /// owner's history. Admins see everything; the access check still gates visibility overall.
        /// </summary>
        private IQueryable<SwitchData> WithinOwnershipWindow(IQueryable<SwitchData> query)
            => query.WithinSwitchOwnership(_context, User.UserId(), IsSwitchAdmin());

        /// <summary>
        /// Retrieves all available switches.
        /// </summary>
        [HttpGet]
        [SwaggerOperation(Summary = "Retrieves all available switches.")]
        [SwaggerResponse(200, "A list of all switches.", typeof(IEnumerable<SwitchDto>))]
        public async Task<IActionResult> GetAllSwitches()
        {
            _logger.LogInformation("GetAllSwitches called by {@LogData}", new { CallerUserId = User.UserId() });

            var allSwitches = await _context.Switches
                .Where(sw => sw.Type.ToUpper() == "SOCKET")
                .ToListAsync();
            var accessibleSwitches = new List<Switch>();

            foreach (var sw in allSwitches)
            {
                if (await UserHasRequiredRoleAsync(sw))
                {
                    accessibleSwitches.Add(sw);
                }
            }

            var userId = User.UserId()!;
            var switchIds = accessibleSwitches.Select(s => s.Id).ToList();
            var customNames = await _context.UserSwitchCustomNames
                .Where(x => x.UserId == userId && switchIds.Contains(x.SwitchId))
                .ToDictionaryAsync(x => x.SwitchId, x => x.CustomName);

            var dtos = accessibleSwitches.Select(sw =>
            {
                var dto = _mapper.Map<SwitchDto>(sw);
                dto.CustomName = customNames.TryGetValue(sw.Id, out var cn) ? cn : null;
                return dto;
            });

            _logger.LogInformation("Returning {@LogData}", new { Count = accessibleSwitches.Count, CallerUserId = User.UserId() });
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
            _logger.LogInformation("GetSwitch called by {@LogData}", new { CallerUserId = User.UserId(), id });

            var switchEntity = await _context.Switches.FindAsync(id);
            if (switchEntity == null)
            {
                _logger.LogWarning("GetSwitch not found: {@LogData}", new { id });
                return NotFound(new { message = "Switch not found!" });
            }

            if (!await UserHasRequiredRoleAsync(switchEntity))
            {
                _logger.LogWarning("GetSwitch forbidden for {@LogData}", new { CallerUserId = User.UserId(), id });
                return Forbid();
            }

            var userId = User.UserId()!;
            var customName = await _context.UserSwitchCustomNames
                .Where(x => x.UserId == userId && x.SwitchId == id)
                .Select(x => x.CustomName)
                .FirstOrDefaultAsync();

            var dto = _mapper.Map<SwitchDto>(switchEntity);
            dto.CustomName = customName;

            _logger.LogInformation("Returning switch {@LogData}", new { id, CallerUserId = User.UserId() });
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
            _logger.LogInformation("CreateSwitch called by {@LogData}", new { CallerUserId = User.UserId(), switchDto.Name, switchDto.Type });

            var userRoles = User.FindAll(ClaimTypes.Role).Select(r => r.Value).ToList();
            if (!userRoles.Contains("SwitchAdmin", StringComparer.OrdinalIgnoreCase) &&
                !userRoles.Contains("admin", StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogWarning("CreateSwitch forbidden for {@LogData}", new { CallerUserId = User.UserId() });
                return Forbid();
            }

            var switchEntity = _mapper.Map<Switch>(switchDto);
            switchEntity.Role = switchDto.Name;
            switchEntity.RegistrationCode = await GenerateSwitchRegistrationCodeAsync();

            _context.Switches.Add(switchEntity);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("duplicate key") == true)
            {
                _logger.LogWarning("CreateSwitch conflict: Switch name {@LogData} already exists", new { switchDto.Name });
                return Conflict(new { message = "Switch name already exists!" });
            }

            var dto = _mapper.Map<SwitchDto>(switchEntity);

            _logger.LogInformation("Switch created: {@LogData}", new { switchEntity.Id, switchEntity.Name });
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
            _logger.LogInformation("UpdateSwitch called by {@LogData}", new { CallerUserId = User.UserId(), id });

            if (!IsSwitchAdmin())
            {
                _logger.LogWarning("UpdateSwitch forbidden for {@LogData}", new { CallerUserId = User.UserId(), id });
                return Forbid();
            }

            if (id != switchDto.Id)
            {
                _logger.LogWarning("UpdateSwitch bad request: Id mismatch {@LogData}", new { id, DtoId = switchDto.Id });
                return BadRequest();
            }

            var existingSwitch = await _context.Switches.FindAsync(id);
            if (existingSwitch == null)
            {
                _logger.LogWarning("UpdateSwitch not found: {@LogData}", new { id });
                return NotFound(new { message = "Switch not found!" });
            }

            _mapper.Map(switchDto, existingSwitch);

            _context.Entry(existingSwitch).State = EntityState.Modified;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Switch updated: {@LogData}", new { id });
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
            _logger.LogInformation("DeleteSwitch called by {@LogData}", new { CallerUserId = User.UserId(), id });

            if (!IsSwitchAdmin())
            {
                _logger.LogWarning("DeleteSwitch forbidden for {@LogData}", new { CallerUserId = User.UserId(), id });
                return Forbid();
            }

            var switchEntity = await _context.Switches.FindAsync(id);
            if (switchEntity == null)
            {
                _logger.LogWarning("DeleteSwitch not found: {@LogData}", new { id });
                return NotFound(new { message = "Switch not found!" });
            }

            _context.Switches.Remove(switchEntity);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Switch deleted: {@LogData}", new { id });
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
            _logger.LogInformation("GetSwitchData called by {@LogData}", new { CallerUserId = User.UserId(), switchId });

            var switchEntity = await _context.Switches.FindAsync(switchId);
            if (switchEntity == null)
            {
                _logger.LogWarning("GetSwitchData not found: {@LogData}", new { switchId });
                return NotFound(new { message = "Switch not found!" });
            }

            if (!await UserHasRequiredRoleAsync(switchEntity))
            {
                _logger.LogWarning("GetSwitchData forbidden for {@LogData}", new { CallerUserId = User.UserId(), switchId });
                return Forbid();
            }

            var query = WithinOwnershipWindow(
                _context.SwitchData.Where(sd => sd.SwitchId == switchId));

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

            _logger.LogInformation("Returning {@LogData}", new { Count = switchDataList.Count, switchId });
            return Ok(dtos);
        }

        /// <summary>
        /// Retrieves state for a specific switch.
        /// </summary>
        [HttpGet("{switchId}/state")]
        [SwaggerOperation(Summary = "Retrieves state for a specific switch.")]
        [SwaggerResponse(200, "The state for the specified switch, or null if no data exists yet.", typeof(SwitchDataDto))]
        [SwaggerResponse(404, "Switch not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> GetSwitchState(int switchId)
        {
            _logger.LogInformation("GetSwitchState called by {@LogData}", new { CallerUserId = User.UserId(), switchId });

            var switchEntity = await _context.Switches.FindAsync(switchId);
            if (switchEntity == null)
            {
                _logger.LogWarning("GetSwitchState not found: {@LogData}", new { switchId });
                return NotFound(new { message = "Switch not found!" });
            }

            if (!await UserHasRequiredRoleAsync(switchEntity))
            {
                _logger.LogWarning("GetSwitchState forbidden for {@LogData}", new { CallerUserId = User.UserId(), switchId });
                return Forbid();
            }

            var query = await WithinOwnershipWindow(
                    _context.SwitchData.Where(sd => sd.SwitchId == switchId))
                .OrderByDescending(sd => sd.Timestamp)
                .FirstOrDefaultAsync();

            if (query == null)
            {
                _logger.LogInformation("GetSwitchState no data found for {@LogData}", new { switchId });
                return Ok((SwitchDataDto?)null);
            }

            var dto = _mapper.Map<SwitchDataDto>(query);

            _logger.LogInformation("Returning switch state for {@LogData}", new { switchId });
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
            _logger.LogInformation("CreateSwitchData called by {@LogData}", new { CallerUserId = User.UserId(), switchId });

            if (!IsSwitchAdmin())
            {
                _logger.LogWarning("CreateSwitchData forbidden for {@LogData}", new { CallerUserId = User.UserId(), switchId });
                return Forbid();
            }

            var switchEntity = await _context.Switches.FindAsync(switchId);
            if (switchEntity == null)
            {
                _logger.LogWarning("CreateSwitchData not found: {@LogData}", new { switchId });
                return NotFound(new { message = "Switch not found!" });
            }

            var validStates = new[] { "ON", "OFF" };
            if (!validStates.Contains(switchDataDto.Value, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogWarning("CreateSwitchData bad request: Invalid value {@LogData}", new { switchDataDto.Value, switchId });
                return BadRequest(new { message = "The value must be either 'ON' or 'OFF'." });
            }

            var switchData = _mapper.Map<SwitchData>(switchDataDto);
            switchData.SwitchId = switchId;
            switchData.Value = switchDataDto.Value.ToUpperInvariant();
            switchData.Timestamp = DateTime.UtcNow;

            _context.SwitchData.Add(switchData);
            await _context.SaveChangesAsync();

            var dto = _mapper.Map<SwitchDataDto>(switchData);

            _logger.LogInformation("Switch data created: {@LogData}", new { switchData.Id, switchId });
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
            _logger.LogInformation("GetMultipleSwitchesData called by {@LogData}", new { CallerUserId = User.UserId(), switchIds });

            var switches = await _context.Switches.Where(s => switchIds.Contains(s.Id)).ToListAsync();
            if (switches.Count != switchIds.Count)
            {
                _logger.LogWarning("GetMultipleSwitchesData not found: {@LogData}", new { switchIds });
                return NotFound(new { message = "One or more switches not found!" });
            }

            foreach (var switchEntity in switches)
            {
                if (!await UserHasRequiredRoleAsync(switchEntity))
                {
                    _logger.LogWarning("GetMultipleSwitchesData forbidden for {@LogData}", new { CallerUserId = User.UserId(), switchId = switchEntity.Id });
                    return Forbid();
                }
            }

            var query = WithinOwnershipWindow(
                _context.SwitchData.Where(sd => switchIds.Contains(sd.SwitchId)));

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

            _logger.LogInformation("Returning {@LogData}", new { Count = switchDataList.Count, switchIds });
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
            _logger.LogInformation("DeleteSwitchData called by {@LogData}", new { CallerUserId = User.UserId(), switchId, dataId });

            var switchEntity = await _context.Switches.FindAsync(switchId);
            if (switchEntity == null)
            {
                _logger.LogWarning("DeleteSwitchData not found: {@LogData}", new { switchId });
                return NotFound(new { message = "Switch not found!" });
            }

            if (!await UserHasRequiredRoleAsync(switchEntity))
            {
                _logger.LogWarning("DeleteSwitchData forbidden for {@LogData}", new { CallerUserId = User.UserId(), switchId });
                return Forbid();
            }

            var switchData = await _context.SwitchData.FindAsync(dataId);
            if (switchData == null || switchData.SwitchId != switchId)
            {
                _logger.LogWarning("DeleteSwitchData not found: {@LogData}", new { dataId, switchId });
                return NotFound(new { message = "Switch data not found!" });
            }

            _context.SwitchData.Remove(switchData);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Switch data deleted: {@LogData}", new { dataId, switchId });
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
            _logger.LogInformation("CreateSwitchDataByName called by {@LogData}", new { CallerUserId = User.UserId(), switchName });

            if (!IsSwitchAdmin())
            {
                _logger.LogWarning("CreateSwitchDataByName forbidden for {@LogData}", new { CallerUserId = User.UserId(), switchName });
                return Forbid();
            }

            var switchEntity = await _context.Switches.FirstOrDefaultAsync(s => s.Name == switchName);
            if (switchEntity == null)
            {
                _logger.LogWarning("CreateSwitchDataByName not found: {@LogData}", new { switchName });
                return NotFound(new { message = "Switch not found!" });
            }

            var validStates = new[] { "ON", "OFF" };
            if (!validStates.Contains(switchDataDto.Value, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogWarning("CreateSwitchDataByName bad request: Invalid value {@LogData}", new { switchDataDto.Value, switchName });
                return BadRequest(new { message = "The value must be either 'ON' or 'OFF'." });
            }

            var switchData = _mapper.Map<SwitchData>(switchDataDto);
            switchData.SwitchId = switchEntity.Id;
            switchData.Value = switchDataDto.Value.ToUpperInvariant();
            switchData.Timestamp = DateTime.UtcNow;

            _context.SwitchData.Add(switchData);
            await _context.SaveChangesAsync();

            var dto = _mapper.Map<SwitchDataDto>(switchData);

            _logger.LogInformation("Switch data created: {@LogData}", new { switchData.Id, switchName });
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
            _logger.LogInformation("DeleteAllSwitchData called by {@LogData}", new { CallerUserId = User.UserId(), switchId });

            var switchEntity = await _context.Switches.FindAsync(switchId);
            if (switchEntity == null)
            {
                _logger.LogWarning("DeleteAllSwitchData not found: {@LogData}", new { switchId });
                return NotFound(new { message = "Switch not found!" });
            }

            if (!await UserHasRequiredRoleAsync(switchEntity))
            {
                _logger.LogWarning("DeleteAllSwitchData forbidden for {@LogData}", new { CallerUserId = User.UserId(), switchId });
                return Forbid();
            }

            var switchData = _context.SwitchData.Where(sd => sd.SwitchId == switchId);
            _context.SwitchData.RemoveRange(switchData);
            await _context.SaveChangesAsync();

            _logger.LogInformation("All switch data deleted for {@LogData}", new { switchId });
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

        /// <summary>
        /// Set or update the custom display name for a switch (per user).
        /// </summary>
        [HttpPatch("{switchId}/custom-name")]
        [SwaggerOperation(Summary = "Set or update the custom display name for a switch.")]
        [SwaggerResponse(200, "Updated switch DTO.", typeof(SwitchDto))]
        [SwaggerResponse(404, "Switch not found.")]
        [SwaggerResponse(403, "User does not have access to this switch.")]
        public async Task<IActionResult> UpdateCustomName(
            int switchId,
            [FromBody] garge_api.Dtos.Sensor.UpdateCustomNameDto dto)
        {
            _logger.LogInformation("UpdateCustomName (switch) called by {@LogData}", new { CallerUserId = User.UserId(), switchId });

            var switchEntity = await _context.Switches.FindAsync(switchId);
            if (switchEntity == null)
            {
                _logger.LogWarning("UpdateCustomName (switch) not found: {@LogData}", new { switchId });
                return NotFound(new { message = "Switch not found!" });
            }

            if (!await UserHasRequiredRoleAsync(switchEntity))
            {
                _logger.LogWarning("UpdateCustomName (switch) forbidden for {@LogData}", new { CallerUserId = User.UserId(), switchId });
                return Forbid();
            }

            var userId = User.UserId()!;

            var existing = await _context.UserSwitchCustomNames
                .FirstOrDefaultAsync(x => x.UserId == userId && x.SwitchId == switchId);

            if (existing != null)
            {
                existing.CustomName = dto.CustomName;
            }
            else
            {
                _context.UserSwitchCustomNames.Add(new Models.Switch.UserSwitchCustomName
                {
                    UserId = userId,
                    SwitchId = switchId,
                    CustomName = dto.CustomName,
                    CreatedAt = DateTime.UtcNow
                });
            }

            await _context.SaveChangesAsync();

            var result = new SwitchDto
            {
                Id = switchEntity.Id,
                Name = switchEntity.Name,
                Type = switchEntity.Type,
                Role = switchEntity.Role,
                CustomName = dto.CustomName
            };

            return Ok(result);
        }

        [HttpPost("claim")]
        [Authorize(Policy = "ActiveSubscription")]
        [SwaggerOperation(Summary = "Claims a switch using a registration code.")]
        [SwaggerResponse(200, "Switch claimed successfully.")]
        [SwaggerResponse(400, "Registration code is required.")]
        [SwaggerResponse(404, "Invalid registration code.")]
        public async Task<IActionResult> ClaimSwitch([FromBody] ClaimSwitchDto dto)
        {
            _logger.LogInformation("ClaimSwitch called by {@LogData}", new { CallerUserId = User.UserId(), dto.RegistrationCode });

            if (string.IsNullOrWhiteSpace(dto.RegistrationCode))
                return BadRequest(new { message = "Registration code is required." });

            var switchEntity = await _context.Switches.FirstOrDefaultAsync(s => s.RegistrationCode == dto.RegistrationCode);
            if (switchEntity == null)
                return NotFound(new { message = "Invalid registration code." });

            var userId = User.UserId();
            var alreadyClaimed = await _context.UserSwitches.AnyAsync(us => us.UserId == userId && us.SwitchId == switchEntity.Id);
            if (!alreadyClaimed)
            {
                _context.UserSwitches.Add(new UserSwitch { UserId = userId!, SwitchId = switchEntity.Id });

                // Open a direct ownership period that bounds which telemetry this user may read. The
                // first-ever owner starts at the epoch sentinel (sees all history); every later
                // (resale) owner starts now, so they never see the previous owner's readings.
                var firstEverOwner = !await _context.SwitchOwnershipPeriods.AnyAsync(p => p.SwitchId == switchEntity.Id);
                _context.SwitchOwnershipPeriods.Add(new SwitchOwnershipPeriod
                {
                    UserId = userId!,
                    SwitchId = switchEntity.Id,
                    StartedAt = firstEverOwner ? SwitchOwnershipPeriod.FirstOwnerStart : DateTime.UtcNow,
                    EndedAt = null
                });

                await _context.SaveChangesAsync();
                _ownership.InvalidateSwitch(switchEntity.Id);
                await _hub.Clients.Group(DeviceHub.UserGroup(userId!)).SendAsync("device-created", new { kind = "switch", id = switchEntity.Id });
                _logger.LogInformation("ClaimSwitch assigned switch to user {@LogData}", new { switchEntity.Id, CallerUserId = User.UserId() });
            }
            return Ok(new { message = "Switch successfully claimed.", switchId = switchEntity.Id, registrationCode = switchEntity.RegistrationCode });
        }

        [HttpDelete("{id}/claim")]
        [Authorize]
        [SwaggerOperation(Summary = "Removes the current user's direct access to a switch.")]
        [SwaggerResponse(200, "Switch unclaimed successfully.")]
        [SwaggerResponse(404, "Switch not found.")]
        public async Task<IActionResult> UnclaimSwitch(int id)
        {
            _logger.LogInformation("UnclaimSwitch called by {@LogData}", new { CallerUserId = User.UserId(), id });

            var userId = User.UserId();
            var userSwitch = await _context.UserSwitches.FirstOrDefaultAsync(us => us.UserId == userId && us.SwitchId == id);
            if (userSwitch != null)
            {
                _context.UserSwitches.Remove(userSwitch);

                // Close this user's open direct ownership period so a future owner of the same switch
                // cannot see this user's history.
                var openPeriods = await _context.SwitchOwnershipPeriods
                    .Where(p => p.UserId == userId && p.SwitchId == id && p.EndedAt == null)
                    .ToListAsync();
                foreach (var period in openPeriods)
                    period.EndedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();
                _ownership.InvalidateSwitch(id);
            }

            _logger.LogInformation("Switch unclaimed by user {@LogData}", new { CallerUserId = User.UserId(), id });
            return Ok(new { message = "Switch removed from your account." });
        }

        private async Task<string> GenerateSwitchRegistrationCodeAsync(int length = 10)
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            string code;
            bool exists;
            do
            {
                code = new string(Enumerable.Range(0, length)
                    .Select(_ => chars[System.Security.Cryptography.RandomNumberGenerator.GetInt32(chars.Length)]).ToArray());
                exists = await _context.Switches.AnyAsync(s => s.RegistrationCode == code);
            } while (exists);
            return code;
        }
    }
}
