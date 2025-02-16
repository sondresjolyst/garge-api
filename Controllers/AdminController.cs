using garge_api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Swashbuckle.AspNetCore.Annotations;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace garge_api.Controllers
{
    /// <summary>
    /// Handles admin-related actions.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Roles = "Admin")]
    public class AdminController : ControllerBase
    {
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ApplicationDbContext _context;
        private readonly ILogger<AdminController> _logger;

        public AdminController(RoleManager<IdentityRole> roleManager, UserManager<ApplicationUser> userManager, ApplicationDbContext context, ILogger<AdminController> logger)
        {
            _roleManager = roleManager;
            _userManager = userManager;
            _context = context;
            _logger = logger;
        }

        /// <summary>
        /// Creates a new role.
        /// </summary>
        /// <param name="roleName">The role name.</param>
        /// <returns>An IActionResult.</returns>
        [HttpPost("create-role")]
        [SwaggerOperation(Summary = "Creates a new role.")]
        [SwaggerResponse(200, "Role created successfully.")]
        [SwaggerResponse(409, "Role already exists.")]
        [SwaggerResponse(400, "Invalid request.")]
        public async Task<IActionResult> CreateRole(string roleName)
        {
            if (await _roleManager.RoleExistsAsync(roleName))
            {
                return Conflict(new { message = "Role already exists!" });
            }

            var result = await _roleManager.CreateAsync(new IdentityRole(roleName));
            if (result.Succeeded)
            {
                return Ok(new { message = "Role created successfully!" });
            }

            return BadRequest(result.Errors);
        }

        /// <summary>
        /// Assigns a role to a user.
        /// </summary>
        /// <param name="userEmail">The user's email.</param>
        /// <param name="roleName">The role name.</param>
        /// <returns>An IActionResult.</returns>
        [HttpPost("assign-role")]
        [SwaggerOperation(Summary = "Assigns a role to a user.")]
        [SwaggerResponse(200, "Role assigned successfully.")]
        [SwaggerResponse(404, "User not found.")]
        [SwaggerResponse(400, "Invalid request.")]
        public async Task<IActionResult> AssignRole(string userEmail, string roleName)
        {
            var user = await _userManager.FindByEmailAsync(userEmail);
            if (user == null)
            {
                return NotFound(new { message = "User not found!" });
            }

            var result = await _userManager.AddToRoleAsync(user, roleName);
            if (result.Succeeded)
            {
                return Ok(new { message = "Role assigned successfully!" });
            }

            return BadRequest(result.Errors);
        }

        /// <summary>
        /// Assigns a permission to a role.
        /// </summary>
        /// <param name="roleName">The role name.</param>
        /// <param name="permission">The permission.</param>
        /// <returns>An IActionResult.</returns>
        [HttpPost("assign-permission")]
        [SwaggerOperation(Summary = "Assigns a permission to a role.")]
        [SwaggerResponse(200, "Permission assigned successfully.")]
        [SwaggerResponse(404, "Role not found.")]
        [SwaggerResponse(400, "Invalid request.")]
        public async Task<IActionResult> AssignPermission(string roleName, string permission)
        {
            if (!await _roleManager.RoleExistsAsync(roleName))
            {
                return NotFound(new { message = "Role not found!" });
            }

            var rolePermission = new RolePermission
            {
                RoleName = roleName,
                Permission = permission
            };

            _context.RolePermissions.Add(rolePermission);
            await _context.SaveChangesAsync();

            return Ok(new { message = "Permission assigned successfully!" });
        }

        /// <summary>
        /// Gets all roles.
        /// </summary>
        /// <returns>An IActionResult.</returns>
        [HttpGet("roles")]
        [SwaggerOperation(Summary = "Gets all roles.")]
        [SwaggerResponse(200, "Roles retrieved successfully.")]
        public IActionResult GetRoles()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var userRoles = _userManager.GetRolesAsync(_userManager.FindByIdAsync(userId).Result).Result;
            _logger.LogInformation($"User {userId} has roles: {string.Join(", ", userRoles)}");

            var roles = _roleManager.Roles;
            return Ok(roles);
        }

        /// <summary>
        /// Gets all users.
        /// </summary>
        /// <returns>An IActionResult.</returns>
        [HttpGet("users")]
        [SwaggerOperation(Summary = "Gets all users.")]
        [SwaggerResponse(200, "Users retrieved successfully.")]
        public IActionResult GetUsers()
        {
            var users = _userManager.Users.Select(user => new UserDto
            {
                Id = user.Id,
                UserName = user.UserName,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Email = user.Email
            }).ToList();

            return Ok(users);
        }
    }
}
