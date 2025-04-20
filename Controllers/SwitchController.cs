using garge_api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;
using System.Globalization;
using System.Security.Claims;

namespace garge_api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [EnableCors("AllowAllOrigins")]
    [Authorize]
    public class SwitchController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly RoleManager<IdentityRole> _roleManager;

        public SwitchController(ApplicationDbContext context, RoleManager<IdentityRole> roleManager)
        {
            _context = context;
            _roleManager = roleManager;
        }

        private bool UserHasRequiredRole(string switchRole)
        {
            var userRoles = User.FindAll(ClaimTypes.Role).Select(r => r.Value).ToList();
            return userRoles.Any(role => role.Equals(switchRole, StringComparison.OrdinalIgnoreCase)) ||
                   userRoles.Any(role => role.Equals("switch_admin", StringComparison.OrdinalIgnoreCase)) ||
                   userRoles.Any(role => role.Equals("admin", StringComparison.OrdinalIgnoreCase));
        }

        [HttpGet]
        [SwaggerOperation(Summary = "Retrieves all available switches.")]
        [SwaggerResponse(200, "A list of all switches.", typeof(IEnumerable<Switch>))]
        public async Task<IActionResult> GetAllSwitches()
        {
            var userRoles = User.FindAll(ClaimTypes.Role).Select(r => r.Value).ToList();

            if (userRoles.Contains("admin", StringComparer.OrdinalIgnoreCase) || userRoles.Contains("switch_admin", StringComparer.OrdinalIgnoreCase))
            {
                var allSwitches = await _context.Switches.ToListAsync();
                return Ok(allSwitches);
            }

            var accessibleSwitches = await _context.Switches
                .Where(switchEntity => userRoles.Contains(switchEntity.Role))
                .ToListAsync();

            return Ok(accessibleSwitches);
        }

        [HttpGet("{id}")]
        [SwaggerOperation(Summary = "Retrieves a switch by its ID.")]
        [SwaggerResponse(200, "The switch with the specified ID.", typeof(Switch))]
        [SwaggerResponse(404, "Switch not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> GetSwitch(int id)
        {
            var switchEntity = await _context.Switches.FindAsync(id);
            if (switchEntity == null)
            {
                return NotFound(new { message = "Switch not found!" });
            }

            if (!UserHasRequiredRole(switchEntity.Role))
            {
                return Forbid();
            }

            return Ok(switchEntity);
        }

        [HttpPost]
        [SwaggerOperation(Summary = "Creates a new switch.")]
        [SwaggerResponse(201, "The created switch.", typeof(Switch))]
        [SwaggerResponse(409, "Switch name already exists.")]
        public async Task<IActionResult> CreateSwitch([FromBody] CreateSensorDto switchDto)
        {
            if (!UserHasRequiredRole("switch_admin"))
            {
                return Forbid();
            }

            var switchEntity = new Switch
            {
                Name = switchDto.Name,
                Type = switchDto.Type,
                Role = switchDto.Name
            };

            if (!await _roleManager.RoleExistsAsync(switchEntity.Role))
            {
                var roleResult = await _roleManager.CreateAsync(new IdentityRole(switchEntity.Role));
                if (!roleResult.Succeeded)
                {
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
                return Conflict(new { message = "Switch name already exists!" });
            }

            return CreatedAtAction(nameof(GetSwitch), new { id = switchEntity.Id }, switchEntity);
        }

        [HttpPut("{id}")]
        [SwaggerOperation(Summary = "Updates an existing switch.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(400, "Bad request.")]
        [SwaggerResponse(404, "Switch not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> UpdateSwitch(int id, [FromBody] Switch switchEntity)
        {
            if (id != switchEntity.Id)
            {
                return BadRequest();
            }

            var existingSwitch = await _context.Switches.FindAsync(id);
            if (existingSwitch == null)
            {
                return NotFound(new { message = "Switch not found!" });
            }

            if (!UserHasRequiredRole(existingSwitch.Role))
            {
                return Forbid();
            }

            existingSwitch.Name = switchEntity.Name;
            existingSwitch.Type = switchEntity.Type;
            existingSwitch.Role = switchEntity.Role;

            _context.Entry(existingSwitch).State = EntityState.Modified;
            await _context.SaveChangesAsync();

            return NoContent();
        }

        [HttpDelete("{id}")]
        [SwaggerOperation(Summary = "Deletes a switch by its ID.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(404, "Switch not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> DeleteSwitch(int id)
        {
            var switchEntity = await _context.Switches.FindAsync(id);
            if (switchEntity == null)
            {
                return NotFound(new { message = "Switch not found!" });
            }

            if (!UserHasRequiredRole(switchEntity.Role))
            {
                return Forbid();
            }

            _context.Switches.Remove(switchEntity);
            await _context.SaveChangesAsync();

            return NoContent();
        }

        [HttpGet("{switchId}/data")]
        [SwaggerOperation(Summary = "Retrieves data for a specific switch.")]
        [SwaggerResponse(200, "The data for the specified switch.", typeof(IEnumerable<SwitchData>))]
        [SwaggerResponse(404, "Switch not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> GetSwitchData(int switchId, string? timeRange, DateTime? startDate, DateTime? endDate)
        {
            var switchEntity = await _context.Switches.FindAsync(switchId);
            if (switchEntity == null)
            {
                return NotFound(new { message = "Switch not found!" });
            }

            if (!UserHasRequiredRole(switchEntity.Role))
            {
                return Forbid();
            }

            var query = _context.SwitchData
                .Include(sd => sd.Switch)
                .Where(sd => sd.SwitchId == switchId)
                .AsQueryable();

            if (!string.IsNullOrEmpty(timeRange))
            {
                var now = DateTime.UtcNow;
                var timeSpan = ParseTimeRange(timeRange);
                if (timeSpan.HasValue)
                {
                    query = query.Where(sd => sd.Timestamp >= now.Subtract(timeSpan.Value));
                }
            }
            else
            {
                if (startDate.HasValue)
                {
                    query = query.Where(sd => sd.Timestamp >= startDate.Value);
                }

                if (endDate.HasValue)
                {
                    query = query.Where(sd => sd.Timestamp <= endDate.Value);
                }
            }

            var switchDataList = await query
                .OrderBy(sd => sd.Timestamp)
                .ToListAsync();

            return Ok(switchDataList);
        }

        [HttpPost("{switchId}/data")]
        [SwaggerOperation(Summary = "Creates new data for a specific switch.")]
        [SwaggerResponse(201, "The created switch data.", typeof(SwitchData))]
        [SwaggerResponse(400, "Invalid value format.")]
        [SwaggerResponse(404, "Switch not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> CreateSwitchData(int switchId, [FromBody] CreateSensorDataDto switchDataDto)
        {
            var switchEntity = await _context.Switches.FindAsync(switchId);
            if (switchEntity == null)
            {
                return NotFound(new { message = "Switch not found!" });
            }

            if (!UserHasRequiredRole(switchEntity.Role))
            {
                return Forbid();
            }

            // Validate that the value is either "ON" or "OFF"
            var validStates = new[] { "ON", "OFF" };
            if (!validStates.Contains(switchDataDto.Value, StringComparer.OrdinalIgnoreCase))
            {
                return BadRequest(new { message = "The value must be either 'ON' or 'OFF'." });
            }

            var switchData = new SwitchData
            {
                SwitchId = switchId,
                Value = switchDataDto.Value.ToUpperInvariant(), // Normalize to uppercase
                Timestamp = DateTime.UtcNow
            };

            _context.SwitchData.Add(switchData);
            await _context.SaveChangesAsync();

            var sortedSwitchData = await _context.SwitchData
                .Where(sd => sd.SwitchId == switchId)
                .OrderBy(sd => sd.Timestamp)
                .ToListAsync();

            return CreatedAtAction(nameof(GetSwitchData), new { switchId = switchId }, sortedSwitchData);
        }

        [HttpGet("data")]
        [SwaggerOperation(Summary = "Retrieves data for multiple switches.")]
        [SwaggerResponse(200, "The data for the specified switches.", typeof(IEnumerable<SwitchData>))]
        [SwaggerResponse(404, "One or more switches not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> GetMultipleSwitchesData([FromQuery] List<int> switchIds, string? timeRange, DateTime? startDate, DateTime? endDate)
        {
            var switches = await _context.Switches.Where(s => switchIds.Contains(s.Id)).ToListAsync();
            if (switches.Count != switchIds.Count)
            {
                return NotFound(new { message = "One or more switches not found!" });
            }

            foreach (var switchEntity in switches)
            {
                if (!UserHasRequiredRole(switchEntity.Role))
                {
                    return Forbid();
                }
            }

            var query = _context.SwitchData
                .Include(sd => sd.Switch)
                .Where(sd => switchIds.Contains(sd.SwitchId))
                .AsQueryable();

            if (!string.IsNullOrEmpty(timeRange))
            {
                var now = DateTime.UtcNow;
                var timeSpan = ParseTimeRange(timeRange);
                if (timeSpan.HasValue)
                {
                    query = query.Where(sd => sd.Timestamp >= now.Subtract(timeSpan.Value));
                }
            }
            else
            {
                if (startDate.HasValue)
                {
                    query = query.Where(sd => sd.Timestamp >= startDate.Value);
                }

                if (endDate.HasValue)
                {
                    query = query.Where(sd => sd.Timestamp <= endDate.Value);
                }
            }

            var switchDataList = await query
                .OrderBy(sd => sd.Timestamp)
                .ToListAsync();

            return Ok(switchDataList);
        }

        [HttpDelete("{switchId}/data/{dataId}")]
        [SwaggerOperation(Summary = "Deletes specific switch data by its ID.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(404, "Switch or switch data not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> DeleteSwitchData(int switchId, int dataId)
        {
            var switchEntity = await _context.Switches.FindAsync(switchId);
            if (switchEntity == null)
            {
                return NotFound(new { message = "Switch not found!" });
            }

            if (!UserHasRequiredRole(switchEntity.Role))
            {
                return Forbid();
            }

            var switchData = await _context.SwitchData.FindAsync(dataId);
            if (switchData == null || switchData.SwitchId != switchId)
            {
                return NotFound(new { message = "Switch data not found!" });
            }

            _context.SwitchData.Remove(switchData);
            await _context.SaveChangesAsync();

            return NoContent();
        }

        /// <summary>
        /// Creates new data for a specific switch using switchName.
        /// </summary>
        /// <param name="switchName">The name of the switch to create data for.</param>
        /// <param name="switchDataDto">The data to create.</param>
        /// <returns>The created switch data.</returns>
        [HttpPost("name/{switchName}/data")]
        [SwaggerOperation(Summary = "Creates new data for a specific switch using switchName.")]
        [SwaggerResponse(201, "The created switch data.", typeof(SwitchData))]
        [SwaggerResponse(400, "Invalid value format.")]
        [SwaggerResponse(404, "Switch not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> CreateSwitchDataByName(string switchName, [FromBody] CreateSensorDataDto switchDataDto)
        {
            var switchEntity = await _context.Switches.FirstOrDefaultAsync(s => s.Name == switchName);
            if (switchEntity == null)
            {
                return NotFound(new { message = "Switch not found!" });
            }

            if (!UserHasRequiredRole(switchEntity.Role))
            {
                return Forbid();
            }

            // Validate that the value is either "ON" or "OFF"
            var validStates = new[] { "ON", "OFF" };
            if (!validStates.Contains(switchDataDto.Value, StringComparer.OrdinalIgnoreCase))
            {
                return BadRequest(new { message = "The value must be either 'ON' or 'OFF'." });
            }

            var switchData = new SwitchData
            {
                SwitchId = switchEntity.Id,
                Value = switchDataDto.Value.ToUpperInvariant(), // Normalize to uppercase
                Timestamp = DateTime.UtcNow
            };

            _context.SwitchData.Add(switchData);
            await _context.SaveChangesAsync();

            var sortedSwitchData = await _context.SwitchData
                .Where(sd => sd.SwitchId == switchEntity.Id)
                .OrderBy(sd => sd.Timestamp)
                .ToListAsync();

            return CreatedAtAction(nameof(GetSwitchData), new { switchId = switchEntity.Id }, sortedSwitchData);
        }

        [HttpDelete("{switchId}/data")]
        [SwaggerOperation(Summary = "Deletes all data for a specific switch.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(404, "Switch not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> DeleteAllSwitchData(int switchId)
        {
            var switchEntity = await _context.Switches.FindAsync(switchId);
            if (switchEntity == null)
            {
                return NotFound(new { message = "Switch not found!" });
            }

            if (!UserHasRequiredRole(switchEntity.Role))
            {
                return Forbid();
            }

            var switchData = _context.SwitchData.Where(sd => sd.SwitchId == switchId);
            _context.SwitchData.RemoveRange(switchData);
            await _context.SaveChangesAsync();

            return NoContent();
        }

        private static DateTime GetGroupingKey(DateTime timestamp, string? groupBy)
        {
            if (string.IsNullOrEmpty(groupBy))
            {
                return timestamp;
            }

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
            {
                return null;
            }

            var value = timeRange.Substring(0, timeRange.Length - 1);
            var unit = timeRange.Substring(timeRange.Length - 1).ToLower();

            if (!int.TryParse(value, out var intValue))
            {
                return null;
            }

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
