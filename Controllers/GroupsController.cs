using garge_api.Dtos.Group;
using garge_api.Models;
using garge_api.Models.Group;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace garge_api.Controllers
{
    [ApiController]
    [Route("api/groups")]
    [EnableCors("AllowAllOrigins")]
    [Authorize]
    public class GroupsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<GroupsController> _logger;

        public GroupsController(ApplicationDbContext context, ILogger<GroupsController> logger)
        {
            _context = context;
            _logger = logger;
        }

        private string GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        /// <summary>
        /// Get all groups for the current user, including their sensor and switch IDs.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetGroups()
        {
            var userId = GetUserId();
            var groups = await _context.Groups
                .Where(g => g.UserId == userId)
                .Include(g => g.GroupSensors)
                .Include(g => g.GroupSwitches)
                .Select(g => new GroupDto
                {
                    Id = g.Id,
                    Name = g.Name,
                    Icon = g.Icon,
                    SensorIds = g.GroupSensors.Select(gs => gs.SensorId).ToList(),
                    SwitchIds = g.GroupSwitches.Select(gs => gs.SwitchId).ToList()
                })
                .ToListAsync();

            return Ok(groups);
        }

        /// <summary>
        /// Create a new group.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> CreateGroup([FromBody] CreateGroupDto dto)
        {
            var userId = GetUserId();
            var group = new Group
            {
                Name = dto.Name,
                Icon = dto.Icon,
                UserId = userId
            };

            _context.Groups.Add(group);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Group {Name} created by {UserId}", group.Name, userId);

            return CreatedAtAction(nameof(GetGroups), new GroupDto
            {
                Id = group.Id,
                Name = group.Name,
                Icon = group.Icon,
                SensorIds = new List<int>(),
                SwitchIds = new List<int>()
            });
        }

        /// <summary>
        /// Update group name/icon.
        /// </summary>
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateGroup(int id, [FromBody] UpdateGroupDto dto)
        {
            var userId = GetUserId();
            var group = await _context.Groups
                .FirstOrDefaultAsync(g => g.Id == id && g.UserId == userId);

            if (group == null) return NotFound();

            group.Name = dto.Name;
            group.Icon = dto.Icon;
            await _context.SaveChangesAsync();

            return NoContent();
        }

        /// <summary>
        /// Delete a group and all its sensor and switch associations.
        /// </summary>
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteGroup(int id)
        {
            var userId = GetUserId();
            var group = await _context.Groups
                .Include(g => g.GroupSensors)
                .Include(g => g.GroupSwitches)
                .FirstOrDefaultAsync(g => g.Id == id && g.UserId == userId);

            if (group == null) return NotFound();

            _context.GroupSensors.RemoveRange(group.GroupSensors);
            _context.GroupSwitches.RemoveRange(group.GroupSwitches);
            _context.Groups.Remove(group);
            await _context.SaveChangesAsync();

            return NoContent();
        }

        /// <summary>
        /// Add a sensor to a group.
        /// </summary>
        [HttpPost("{id}/sensors/{sensorId}")]
        public async Task<IActionResult> AddSensor(int id, int sensorId)
        {
            var userId = GetUserId();
            var group = await _context.Groups
                .FirstOrDefaultAsync(g => g.Id == id && g.UserId == userId);

            if (group == null) return NotFound();

            var exists = await _context.GroupSensors
                .AnyAsync(gs => gs.GroupId == id && gs.SensorId == sensorId);

            if (!exists)
            {
                _context.GroupSensors.Add(new GroupSensor { GroupId = id, SensorId = sensorId });
                await _context.SaveChangesAsync();
            }

            return NoContent();
        }

        /// <summary>
        /// Remove a sensor from a group.
        /// </summary>
        [HttpDelete("{id}/sensors/{sensorId}")]
        public async Task<IActionResult> RemoveSensor(int id, int sensorId)
        {
            var userId = GetUserId();
            var group = await _context.Groups
                .FirstOrDefaultAsync(g => g.Id == id && g.UserId == userId);

            if (group == null) return NotFound();

            var gs = await _context.GroupSensors
                .FirstOrDefaultAsync(x => x.GroupId == id && x.SensorId == sensorId);

            if (gs != null)
            {
                _context.GroupSensors.Remove(gs);
                await _context.SaveChangesAsync();
            }

            return NoContent();
        }

        /// <summary>
        /// Add a switch to a group.
        /// </summary>
        [HttpPost("{id}/switches/{switchId}")]
        public async Task<IActionResult> AddSwitch(int id, int switchId)
        {
            var userId = GetUserId();
            var group = await _context.Groups
                .FirstOrDefaultAsync(g => g.Id == id && g.UserId == userId);

            if (group == null) return NotFound();

            var exists = await _context.GroupSwitches
                .AnyAsync(gs => gs.GroupId == id && gs.SwitchId == switchId);

            if (!exists)
            {
                _context.GroupSwitches.Add(new GroupSwitch { GroupId = id, SwitchId = switchId });
                await _context.SaveChangesAsync();
            }

            return NoContent();
        }

        /// <summary>
        /// Remove a switch from a group.
        /// </summary>
        [HttpDelete("{id}/switches/{switchId}")]
        public async Task<IActionResult> RemoveSwitch(int id, int switchId)
        {
            var userId = GetUserId();
            var group = await _context.Groups
                .FirstOrDefaultAsync(g => g.Id == id && g.UserId == userId);

            if (group == null) return NotFound();

            var gs = await _context.GroupSwitches
                .FirstOrDefaultAsync(x => x.GroupId == id && x.SwitchId == switchId);

            if (gs != null)
            {
                _context.GroupSwitches.Remove(gs);
                await _context.SaveChangesAsync();
            }

            return NoContent();
        }
    }
}
