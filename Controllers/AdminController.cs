using garge_api.Models;
using garge_api.Models.Shop;
using garge_api.Models.Subscription;
using garge_api.Dtos.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;
using MapsterMapper;
using garge_api.Models.Admin;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace garge_api.Controllers
{
    [ApiController]
    [Route("api/roles")]
    [Authorize(Policy = "Admin")]
    public class AdminController : ControllerBase
    {
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly UserManager<User> _userManager;
        private readonly ApplicationDbContext _context;
        private readonly ILogger<AdminController> _logger;
        private readonly IMapper _mapper;
        private readonly IEmailService _emailService;
        private readonly Services.IAppSettingsCache? _settingsCache;

        public AdminController(
            RoleManager<IdentityRole> roleManager,
            UserManager<User> userManager,
            ApplicationDbContext context,
            ILogger<AdminController> logger,
            IMapper mapper,
            IEmailService emailService,
            Services.IAppSettingsCache? settingsCache = null)
        {
            _roleManager = roleManager;
            _userManager = userManager;
            _context = context;
            _logger = logger;
            _mapper = mapper;
            _emailService = emailService;
            _settingsCache = settingsCache;
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
            _logger.LogInformation("CreateRole called with {@LogData}", new { dto.Name, CallerUserId = User.UserId() });

            if (await _roleManager.RoleExistsAsync(dto.Name))
            {
                _logger.LogWarning("Role creation failed: {@LogData} already exists", new { dto.Name });
                return Conflict(new { message = "Role already exists!" });
            }

            var result = await _roleManager.CreateAsync(new IdentityRole(dto.Name));
            if (result.Succeeded)
            {
                _logger.LogInformation("Role created: {@LogData}", new { dto.Name, CallerUserId = User.UserId() });
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
            _logger.LogInformation("GetRoles called by {@LogData}", new { CallerUserId = User.UserId() });

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
            _logger.LogInformation("GetRole called for {@LogData}", new { roleName, CallerUserId = User.UserId() });

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
            _logger.LogInformation("DeleteRole called for {@LogData}", new { roleName, CallerUserId = User.UserId() });

            var role = await _roleManager.FindByNameAsync(roleName);
            if (role == null)
            {
                _logger.LogWarning("DeleteRole failed: Role not found {@LogData}", new { roleName });
                return NotFound(new { message = "Role not found!" });
            }

            var result = await _roleManager.DeleteAsync(role);
            if (result.Succeeded)
            {
                _logger.LogInformation("Role deleted: {@LogData}", new { roleName, CallerUserId = User.UserId() });
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
            _logger.LogInformation("AssignRole called: {@LogData}", new { roleName, CallerUserId = User.UserId() });

            var user = await _userManager.FindByEmailAsync(userEmail);
            if (user == null)
            {
                _logger.LogWarning("AssignRole failed: User not found");
                return NotFound(new { message = "User not found!" });
            }

            var result = await _userManager.AddToRoleAsync(user, roleName);
            if (result.Succeeded)
            {
                _logger.LogInformation("Role assigned: {@LogData}", new { roleName, TargetUserId = user.Id, CallerUserId = User.UserId() });
                return Ok(new { message = "Role assigned successfully!" });
            }

            _logger.LogError("AssignRole failed for {TargetUserId}: {@Errors}", user.Id, result.Errors);
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
            _logger.LogInformation("RemoveRole called: {@LogData}", new { roleName, CallerUserId = User.UserId() });

            var user = await _userManager.FindByEmailAsync(userEmail);
            if (user == null)
            {
                _logger.LogWarning("RemoveRole failed: User not found");
                return NotFound(new { message = "User not found!" });
            }

            var result = await _userManager.RemoveFromRoleAsync(user, roleName);
            if (result.Succeeded)
            {
                _logger.LogInformation("Role removed: {@LogData}", new { roleName, TargetUserId = user.Id, CallerUserId = User.UserId() });
                return NoContent();
            }

            _logger.LogError("RemoveRole failed for {TargetUserId}: {@Errors}", user.Id, result.Errors);
            return BadRequest(result.Errors);
        }

        /// <summary>
        /// Gets all users with their roles. Soft-deleted (scrubbed) accounts are hidden by default;
        /// pass includeDeleted=true to include them.
        /// </summary>
        /// <param name="includeDeleted">When true, also returns soft-deleted accounts.</param>
        /// <returns>A list of all users.</returns>
        [HttpGet("/api/users")]
        [SwaggerOperation(Summary = "Gets all users.")]
        [SwaggerResponse(200, "Users retrieved successfully.", typeof(IEnumerable<UserDto>))]
        public async Task<IActionResult> GetUsers([FromQuery] bool includeDeleted = false)
        {
            _logger.LogInformation("GetUsers called by {@LogData}", new { CallerUserId = User.UserId(), IncludeDeleted = includeDeleted });

            var query = _userManager.Users;
            if (!includeDeleted) query = query.Where(u => !u.IsDeleted);
            var users = query.ToList();
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
        public async Task<IActionResult> GetStats([FromQuery] bool test = false)
        {
            _logger.LogInformation("GetStats called by {@LogData}", new { CallerUserId = User.UserId(), Test = test });

            var now = DateTime.UtcNow;
            var today = now.Date;
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            // Monday-start ISO week.
            var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));

            var totalUsers = await _userManager.Users.CountAsync(u => !u.IsDeleted);
            var totalSensors = await _context.Sensors.CountAsync();
            var totalSwitches = await _context.Switches.CountAsync();
            var activeAutomations = await _context.AutomationRules.CountAsync(r => r.IsEnabled);

            var orderQuery = _context.Orders.Where(o => o.IsTest == test);
            var subQuery = _context.Subscriptions.Where(s => s.IsTest == test);

            var orders = new AdminOrderStatsDto
            {
                Today = await orderQuery.CountAsync(o => o.CreatedAt >= today),
                ThisWeek = await orderQuery.CountAsync(o => o.CreatedAt >= weekStart),
                ThisMonth = await orderQuery.CountAsync(o => o.CreatedAt >= monthStart),
                PendingCapture = await orderQuery.CountAsync(o => o.Status == OrderStatus.Reserved),
                FailedOrCancelled = await orderQuery.CountAsync(o =>
                    o.Status == OrderStatus.Failed || o.Status == OrderStatus.Cancelled),
                TotalRevenueInOre = await orderQuery
                    .Where(o => o.Status == OrderStatus.Paid)
                    .SumAsync(o => (long)o.TotalInOre),
                MonthRevenueInOre = await orderQuery
                    .Where(o => o.Status == OrderStatus.Paid && o.CreatedAt >= monthStart)
                    .SumAsync(o => (long)o.TotalInOre),
            };

            var subscriptions = new AdminSubscriptionStatsDto
            {
                Active = await subQuery.CountAsync(s => s.Status == SubscriptionStatus.Active),
                PendingConfirm = await subQuery.CountAsync(s => s.Status == SubscriptionStatus.Pending),
                StoppedThisMonth = await subQuery.CountAsync(s =>
                    s.Status == SubscriptionStatus.Stopped && s.UpdatedAt >= monthStart),
            };

            var activeWithProduct = await subQuery
                .Where(s => s.Status == SubscriptionStatus.Active)
                .Include(s => s.Product)
                .Where(s => s.Product != null)
                .Select(s => new { s.Product!.PriceInOre, s.Product.Interval })
                .ToListAsync();
            subscriptions.MonthlyRecurringInOre = activeWithProduct.Sum(p =>
                p.Interval == BillingInterval.Yearly ? (long)(p.PriceInOre / 12) : (long)p.PriceInOre);

            var stats = new AdminStatsDto
            {
                TotalUsers = totalUsers,
                TotalSensors = totalSensors,
                TotalSwitches = totalSwitches,
                ActiveAutomations = activeAutomations,
                Orders = orders,
                Subscriptions = subscriptions,
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
            _logger.LogInformation("GetDevices called by {@LogData}", new { CallerUserId = User.UserId() });

            var devices = await _context.DiscoveredDevices
                .OrderByDescending(d => d.Timestamp)
                .ToListAsync();
            return Ok(devices);
        }

        /// <summary>
        /// Gets Brevo transactional email stats for the last N days.
        /// </summary>
        [HttpGet("/api/admin/email-stats")]
        [SwaggerOperation(Summary = "Gets Brevo email stats.")]
        [SwaggerResponse(200, "Email stats retrieved successfully.", typeof(EmailStatsDto))]
        public async Task<IActionResult> GetEmailStats([FromQuery] int days = 30)
        {
            _logger.LogInformation("GetEmailStats called by {@LogData}", new { CallerUserId = User.UserId() });
            try
            {
                var stats = await _emailService.GetEmailStatsAsync(days);
                return Ok(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError("GetEmailStats failed: {Error}", ex.Message);
                return StatusCode(502, new { message = "Failed to fetch email stats from Brevo." });
            }
        }

        /// <summary>
        /// Gets cumulative daily stats over time for charting.
        /// </summary>
        [HttpGet("/api/admin/stats/history")]
        [SwaggerOperation(Summary = "Gets cumulative daily stats over time.")]
        public async Task<IActionResult> GetStatsHistory()
        {
            _logger.LogInformation("GetStatsHistory called by {@LogData}", new { CallerUserId = User.UserId() });

            // Frozen completed-day snapshots are immutable, so they outlive the per-user data they
            // were derived from and survive the 5-year purge. The daily job freezes completed days;
            // today is always recomputed here so same-day churn is not lost.
            var snapshots = await Services.StatsSnapshotService.GetHistoryAsync(_context);

            var result = snapshots.Select(s => (object)new
            {
                date = s.Date.ToString("yyyy-MM-dd"),
                totalUsers = s.TotalUsers,
                totalSensors = s.TotalSensors,
                totalSwitches = s.TotalSwitches,
                totalAutomations = s.TotalAutomations,
            }).ToList();

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
            _logger.LogInformation("DeleteUser called for {@LogData}", new { id, CallerUserId = User.UserId() });

            var user = await _userManager.FindByIdAsync(id);
            if (user == null)
            {
                _logger.LogWarning("DeleteUser failed: User not found {@LogData}", new { id });
                return NotFound(new { message = "User not found!" });
            }

            var result = await _userManager.DeleteAsync(user);
            if (result.Succeeded)
            {
                _logger.LogInformation("User deleted: {@LogData}", new { id, CallerUserId = User.UserId() });
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
            _logger.LogInformation("AssignPermission called: {@LogData}", new { roleName, permission, CallerUserId = User.UserId() });

            if (!await _roleManager.RoleExistsAsync(roleName))
            {
                _logger.LogWarning("AssignPermission failed: Role not found {@LogData}", new { roleName });
                return NotFound(new { message = "Role not found!" });
            }

            if (!Constants.RoleNames.KnownPermissions.Contains(permission))
            {
                _logger.LogWarning("AssignPermission failed: Unknown permission {@LogData}", new { permission });
                return BadRequest(new { message = $"Unknown permission. Allowed: {string.Join(", ", Constants.RoleNames.KnownPermissions)}." });
            }

            var rolePermission = new RolePermission
            {
                RoleName = roleName,
                Permission = permission
            };

            _context.RolePermissions.Add(rolePermission);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Permission assigned: {@LogData}", new { permission, roleName, CallerUserId = User.UserId() });
            return Ok(new { message = "Permission assigned successfully!" });
        }

        /// <summary>
        /// Gets app-wide settings. Public — called by the frontend without authentication.
        /// </summary>
        [HttpGet("/api/admin/settings")]
        [AllowAnonymous]
        [SwaggerOperation(Summary = "Gets app-wide settings.")]
        [SwaggerResponse(200, "Settings retrieved.", typeof(AppSettingsDto))]
        public async Task<IActionResult> GetAppSettings()
        {
            var settings = await _context.AppSettings.FindAsync(1) ?? new AppSettings();
            return Ok(_mapper.Map<AppSettingsDto>(settings));
        }

        /// <summary>
        /// Updates app-wide settings.
        /// </summary>
        [HttpPut("/api/admin/settings")]
        [SwaggerOperation(Summary = "Updates app-wide settings.")]
        [SwaggerResponse(200, "Settings updated.", typeof(AppSettingsDto))]
        public async Task<IActionResult> UpdateAppSettings([FromBody] UpdateAppSettingsDto dto)
        {
            _logger.LogInformation("UpdateAppSettings called by {@LogData}", new { CallerUserId = User.UserId() });

            var settings = await _context.AppSettings.FindAsync(1);
            if (settings == null)
            {
                settings = new AppSettings { Id = 1 };
                _context.AppSettings.Add(settings);
            }

            _mapper.Map(dto, settings);
            await _context.SaveChangesAsync();
            _settingsCache?.Invalidate();

            _logger.LogInformation("AppSettings updated: {@LogData}", new { dto.CookieBannerEnabled, CallerUserId = User.UserId() });
            return Ok(_mapper.Map<AppSettingsDto>(settings));
        }
    }
}
