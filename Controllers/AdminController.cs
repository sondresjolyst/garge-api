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
        public async Task<IActionResult> GetRoles()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null)
            {
                return BadRequest(new { message = "User ID not found in claims." });
            }

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                return NotFound(new { message = "User not found!" });
            }

            var userRoles = await _userManager.GetRolesAsync(user);
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

        /// <summary>
        /// Deletes a user by their ID.
        /// </summary>
        /// <param name="id">The ID of the user to delete.</param>
        /// <returns>No content.</returns>
        [HttpDelete("user/{id}")]
        [SwaggerOperation(Summary = "Deletes a user by their ID.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(404, "User not found.")]
        public async Task<IActionResult> DeleteUser(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null)
            {
                return NotFound(new { message = "User not found!" });
            }

            _context.Users.Remove(user);
            await _context.SaveChangesAsync();

            return NoContent();
        }

        /// <summary>
        /// Deletes a user by their email.
        /// </summary>
        /// <param name="email">The email of the user to delete.</param>
        /// <returns>No content.</returns>
        [HttpDelete("user/{email}")]
        [SwaggerOperation(Summary = "Deletes a user by their email.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(404, "User not found.")]
        public async Task<IActionResult> DeleteUserByEmail(string email)
        {
            var user = await _userManager.FindByEmailAsync(email);
            if (user == null)
            {
                return NotFound(new { message = "User not found!" });
            }

            _context.Users.Remove(user);
            await _context.SaveChangesAsync();

            return NoContent();
        }

        /// <summary>
        /// Deletes a role by its name.
        /// </summary>
        /// <param name="roleName">The name of the role to delete.</param>
        /// <returns>No content.</returns>
        [HttpDelete("role/{roleName}")]
        [SwaggerOperation(Summary = "Deletes a role by its name.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(404, "Role not found.")]
        public async Task<IActionResult> DeleteRole(string roleName)
        {
            var role = await _roleManager.FindByNameAsync(roleName);
            if (role == null)
            {
                return NotFound(new { message = "Role not found!" });
            }

            var result = await _roleManager.DeleteAsync(role);
            if (result.Succeeded)
            {
                return NoContent();
            }

            return BadRequest(result.Errors);
        }

        /// <summary>
        /// Deletes a role assignment for a user.
        /// </summary>
        /// <param name="userEmail">The user's email.</param>
        /// <param name="roleName">The name of the role to remove.</param>
        /// <returns>No content.</returns>
        [HttpDelete("role-assignment")]
        [SwaggerOperation(Summary = "Deletes a role assignment for a user.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(404, "User or role not found.")]
        public async Task<IActionResult> DeleteRoleAssignment(string userEmail, string roleName)
        {
            var user = await _userManager.FindByEmailAsync(userEmail);
            if (user == null)
            {
                return NotFound(new { message = "User not found!" });
            }

            var result = await _userManager.RemoveFromRoleAsync(user, roleName);
            if (result.Succeeded)
            {
                return NoContent();
            }

            return BadRequest(result.Errors);
        }
    }
}
