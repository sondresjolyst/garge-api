using garge_api.Models;
using garge_api.Dtos.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;
using AutoMapper;
using garge_api.Models.Admin;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Controllers
{
    [ApiController]
    [Route("api/roles")]
    [Authorize(Roles = "Admin")]
    public class AdminController : ControllerBase
    {
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly UserManager<User> _userManager;
        private readonly ApplicationDbContext _context;
        private readonly ILogger<AdminController> _logger;
        private readonly IMapper _mapper;

        public AdminController(
            RoleManager<IdentityRole> roleManager,
            UserManager<User> userManager,
            ApplicationDbContext context,
            ILogger<AdminController> logger,
            IMapper mapper)
        {
            _roleManager = roleManager;
            _userManager = userManager;
            _context = context;
            _logger = logger;
            _mapper = mapper;
        }

        /// <summary>
        /// Creates a new role.
        /// </summary>
        /// <param name="dto">The role DTO.</param>
        /// <returns>The created role.</returns>
        [HttpPost]
        [SwaggerOperation(Summary = "Creates a new role.")]
        [SwaggerResponse(201, "Role created successfully.", typeof(RoleDto))]
        [SwaggerResponse(409, "Role already exists.")]
        public async Task<IActionResult> CreateRole([FromBody] RoleDto dto)
        {
            _logger.LogInformation("CreateRole called with {@LogData}", new { dto.Name, User = User.Identity?.Name });

            if (await _roleManager.RoleExistsAsync(dto.Name))
            {
                _logger.LogWarning("Role creation failed: {@LogData} already exists", new { dto.Name });
                return Conflict(new { message = "Role already exists!" });
            }

            var result = await _roleManager.CreateAsync(new IdentityRole(dto.Name));
            if (result.Succeeded)
            {
                _logger.LogInformation("Role created: {@LogData}", new { dto.Name, User = User.Identity?.Name });
                return CreatedAtAction(nameof(GetRole), new { roleName = dto.Name }, dto);
            }

            _logger.LogError("Role creation failed for {@LogData}: {@Errors}", new { dto.Name }, result.Errors);
            return BadRequest(result.Errors);
        }

        /// <summary>
        /// Gets all roles.
        /// </summary>
        /// <returns>A list of all roles.</returns>
        [HttpGet]
        [SwaggerOperation(Summary = "Gets all roles.")]
        [SwaggerResponse(200, "Roles retrieved successfully.", typeof(IEnumerable<RoleDto>))]
        public IActionResult GetRoles()
        {
            _logger.LogInformation("GetRoles called by {@LogData}", new { User = User.Identity?.Name });

            var roles = _roleManager.Roles.ToList();
            var dtos = _mapper.Map<IEnumerable<RoleDto>>(roles);
            return Ok(dtos);
        }

        /// <summary>
        /// Gets a role by name.
        /// </summary>
        /// <param name="roleName">The name of the role.</param>
        /// <returns>The role DTO.</returns>
        [HttpGet("{roleName}")]
        [SwaggerOperation(Summary = "Gets a role by name.")]
        [SwaggerResponse(200, "Role retrieved successfully.", typeof(RoleDto))]
        [SwaggerResponse(404, "Role not found.")]
        public async Task<IActionResult> GetRole(string roleName)
        {
            _logger.LogInformation("GetRole called for {@LogData}", new { roleName, User = User.Identity?.Name });

            var role = await _roleManager.FindByNameAsync(roleName);
            if (role == null)
            {
                _logger.LogWarning("Role not found: {@LogData}", new { roleName });
                return NotFound(new { message = "Role not found!" });
            }

            var dto = _mapper.Map<RoleDto>(role);
            return Ok(dto);
        }

        /// <summary>
        /// Deletes a role by its name.
        /// </summary>
        /// <param name="roleName">The name of the role to delete.</param>
        /// <returns>No content.</returns>
        [HttpDelete("{roleName}")]
        [SwaggerOperation(Summary = "Deletes a role by its name.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(404, "Role not found.")]
        public async Task<IActionResult> DeleteRole(string roleName)
        {
            _logger.LogInformation("DeleteRole called for {@LogData}", new { roleName, User = User.Identity?.Name });

            var role = await _roleManager.FindByNameAsync(roleName);
            if (role == null)
            {
                _logger.LogWarning("DeleteRole failed: Role not found {@LogData}", new { roleName });
                return NotFound(new { message = "Role not found!" });
            }

            var result = await _roleManager.DeleteAsync(role);
            if (result.Succeeded)
            {
                _logger.LogInformation("Role deleted: {@LogData}", new { roleName, User = User.Identity?.Name });
                return NoContent();
            }

            _logger.LogError("DeleteRole failed for {@LogData}: {@Errors}", new { roleName }, result.Errors);
            return BadRequest(result.Errors);
        }

        /// <summary>
        /// Assigns a role to a user.
        /// </summary>
        /// <param name="roleName">The role name.</param>
        /// <param name="userEmail">The user's email.</param>
        /// <returns>Status of the assignment.</returns>
        [HttpPost("{roleName}/users")]
        [SwaggerOperation(Summary = "Assigns a role to a user.")]
        [SwaggerResponse(200, "Role assigned successfully.")]
        [SwaggerResponse(404, "User not found.")]
        public async Task<IActionResult> AssignRole([FromRoute] string roleName, [FromQuery] string userEmail)
        {
            _logger.LogInformation("AssignRole called: {@LogData}", new { roleName, userEmail, User = User.Identity?.Name });

            var user = await _userManager.FindByEmailAsync(userEmail);
            if (user == null)
            {
                _logger.LogWarning("AssignRole failed: User not found {@LogData}", new { userEmail });
                return NotFound(new { message = "User not found!" });
            }

            var result = await _userManager.AddToRoleAsync(user, roleName);
            if (result.Succeeded)
            {
                _logger.LogInformation("Role assigned: {@LogData}", new { roleName, userEmail, User = User.Identity?.Name });
                return Ok(new { message = "Role assigned successfully!" });
            }

            _logger.LogError("AssignRole failed for {@LogData}: {@Errors}", new { userEmail }, result.Errors);
            return BadRequest(result.Errors);
        }

        /// <summary>
        /// Removes a role from a user.
        /// </summary>
        /// <param name="roleName">The role name.</param>
        /// <param name="userEmail">The user's email.</param>
        /// <returns>No content.</returns>
        [HttpDelete("{roleName}/users")]
        [SwaggerOperation(Summary = "Removes a role from a user.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(404, "User not found.")]
        public async Task<IActionResult> RemoveRole([FromRoute] string roleName, [FromQuery] string userEmail)
        {
            _logger.LogInformation("RemoveRole called: {@LogData}", new { roleName, userEmail, User = User.Identity?.Name });

            var user = await _userManager.FindByEmailAsync(userEmail);
            if (user == null)
            {
                _logger.LogWarning("RemoveRole failed: User not found {@LogData}", new { userEmail });
                return NotFound(new { message = "User not found!" });
            }

            var result = await _userManager.RemoveFromRoleAsync(user, roleName);
            if (result.Succeeded)
            {
                _logger.LogInformation("Role removed: {@LogData}", new { roleName, userEmail, User = User.Identity?.Name });
                return NoContent();
            }

            _logger.LogError("RemoveRole failed for {@LogData}: {@Errors}", new { userEmail }, result.Errors);
            return BadRequest(result.Errors);
        }

        /// <summary>
        /// Gets all users with their roles.
        /// </summary>
        /// <returns>A list of all users.</returns>
        [HttpGet("/api/users")]
        [SwaggerOperation(Summary = "Gets all users.")]
        [SwaggerResponse(200, "Users retrieved successfully.", typeof(IEnumerable<UserDto>))]
        public async Task<IActionResult> GetUsers()
        {
            _logger.LogInformation("GetUsers called by {@LogData}", new { User = User.Identity?.Name });

            var users = _userManager.Users.ToList();
            var dtos = new List<UserDto>();
            foreach (var user in users)
            {
                var dto = _mapper.Map<UserDto>(user);
                dto.Roles = await _userManager.GetRolesAsync(user);
                dtos.Add(dto);
            }
            return Ok(dtos);
        }

        /// <summary>
        /// Gets aggregate platform stats.
        /// </summary>
        [HttpGet("/api/admin/stats")]
        [SwaggerOperation(Summary = "Gets aggregate platform stats.")]
        [SwaggerResponse(200, "Stats retrieved successfully.", typeof(AdminStatsDto))]
        public async Task<IActionResult> GetStats()
        {
            _logger.LogInformation("GetStats called by {@LogData}", new { User = User.Identity?.Name });

            var stats = new AdminStatsDto
            {
                TotalUsers = await _userManager.Users.CountAsync(),
                TotalSensors = await _context.Sensors.CountAsync(),
                TotalSwitches = await _context.Switches.CountAsync(),
                ActiveAutomations = await _context.AutomationRules.CountAsync(r => r.IsEnabled),
            };
            return Ok(stats);
        }

        /// <summary>
        /// Gets all discovered MQTT devices.
        /// </summary>
        [HttpGet("/api/admin/devices")]
        [SwaggerOperation(Summary = "Gets all discovered MQTT devices.")]
        public async Task<IActionResult> GetDevices()
        {
            _logger.LogInformation("GetDevices called by {@LogData}", new { User = User.Identity?.Name });

            var devices = await _context.DiscoveredDevices
                .OrderByDescending(d => d.Timestamp)
                .ToListAsync();
            return Ok(devices);
        }

        /// <summary>
        /// Gets cumulative daily stats over time for charting.
        /// </summary>
        [HttpGet("/api/admin/stats/history")]
        [SwaggerOperation(Summary = "Gets cumulative daily stats over time.")]
        public async Task<IActionResult> GetStatsHistory()
        {
            _logger.LogInformation("GetStatsHistory called by {@LogData}", new { User = User.Identity?.Name });

            var userDates = await _userManager.Users
                .Select(u => u.CreatedAt.Date)
                .ToListAsync();

            var sensorDates = await _context.Sensors
                .Select(s => s.CreatedAt.Date)
                .ToListAsync();

            var switchDates = await _context.Switches
                .Select(s => s.CreatedAt.Date)
                .ToListAsync();

            var automationDates = await _context.AutomationRules
                .Select(a => a.CreatedAt.Date)
                .ToListAsync();

            var sanityFloor = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var allDates = userDates.Concat(sensorDates).Concat(switchDates).Concat(automationDates)
                .Where(d => d >= sanityFloor)
                .ToList();
            if (!allDates.Any()) return Ok(new List<object>());

            var start = allDates.Min();
            var today = DateTime.UtcNow.Date;

            var result = new List<object>();
            int users = 0, sensors = 0, switches = 0, automations = 0;

            for (var date = start; date <= today; date = date.AddDays(1))
            {
                users += userDates.Count(d => d == date);
                sensors += sensorDates.Count(d => d == date);
                switches += switchDates.Count(d => d == date);
                automations += automationDates.Count(d => d == date);

                result.Add(new
                {
                    date = date.ToString("yyyy-MM-dd"),
                    totalUsers = users,
                    totalSensors = sensors,
                    totalSwitches = switches,
                    totalAutomations = automations,
                });
            }

            return Ok(result);
        }

        /// <summary>
        /// Deletes a user by their ID.
        /// </summary>
        /// <param name="id">The ID of the user to delete.</param>
        /// <returns>No content.</returns>
        [HttpDelete("/api/users/{id}")]
        [SwaggerOperation(Summary = "Deletes a user by their ID.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(404, "User not found.")]
        public async Task<IActionResult> DeleteUser(string id)
        {
            _logger.LogInformation("DeleteUser called for {@LogData}", new { id, User = User.Identity?.Name });

            var user = await _userManager.FindByIdAsync(id);
            if (user == null)
            {
                _logger.LogWarning("DeleteUser failed: User not found {@LogData}", new { id });
                return NotFound(new { message = "User not found!" });
            }

            var result = await _userManager.DeleteAsync(user);
            if (result.Succeeded)
            {
                _logger.LogInformation("User deleted: {@LogData}", new { id, User = User.Identity?.Name });
                return NoContent();
            }

            _logger.LogError("DeleteUser failed for {@LogData}: {@Errors}", new { id }, result.Errors);
            return BadRequest(result.Errors);
        }

        /// <summary>
        /// Assigns a permission to a role.
        /// </summary>
        /// <param name="roleName">The role name.</param>
        /// <param name="permission">The permission to assign.</param>
        /// <returns>Status of the assignment.</returns>
        [HttpPost("{roleName}/permissions")]
        [SwaggerOperation(Summary = "Assigns a permission to a role.")]
        [SwaggerResponse(200, "Permission assigned successfully.")]
        [SwaggerResponse(404, "Role not found.")]
        public async Task<IActionResult> AssignPermission([FromRoute] string roleName, [FromQuery] string permission)
        {
            _logger.LogInformation("AssignPermission called: {@LogData}", new { roleName, permission, User = User.Identity?.Name });

            if (!await _roleManager.RoleExistsAsync(roleName))
            {
                _logger.LogWarning("AssignPermission failed: Role not found {@LogData}", new { roleName });
                return NotFound(new { message = "Role not found!" });
            }

            var rolePermission = new RolePermission
            {
                RoleName = roleName,
                Permission = permission
            };

            _context.RolePermissions.Add(rolePermission);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Permission assigned: {@LogData}", new { permission, roleName, User = User.Identity?.Name });
            return Ok(new { message = "Permission assigned successfully!" });
        }
    }
}
