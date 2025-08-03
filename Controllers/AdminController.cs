using garge_api.Models;
using garge_api.Dtos.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;
using AutoMapper;
using garge_api.Models.Admin;

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
            _logger.LogInformation("CreateRole called with Name={RoleName} by {User}", dto.Name, User.Identity?.Name);

            if (await _roleManager.RoleExistsAsync(dto.Name))
            {
                _logger.LogWarning("Role creation failed: Role {RoleName} already exists", dto.Name);
                return Conflict(new { message = "Role already exists!" });
            }

            var result = await _roleManager.CreateAsync(new IdentityRole(dto.Name));
            if (result.Succeeded)
            {
                _logger.LogInformation("Role {RoleName} created successfully by {User}", dto.Name, User.Identity?.Name);
                return CreatedAtAction(nameof(GetRole), new { roleName = dto.Name }, dto);
            }

            _logger.LogError("Role creation failed for {RoleName}: {Errors}", dto.Name, result.Errors);
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
            _logger.LogInformation("GetRoles called by {User}", User.Identity?.Name);

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
            _logger.LogInformation("GetRole called for {RoleName} by {User}", roleName, User.Identity?.Name);

            var role = await _roleManager.FindByNameAsync(roleName);
            if (role == null)
            {
                _logger.LogWarning("Role {RoleName} not found", roleName);
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
            _logger.LogInformation("DeleteRole called for {RoleName} by {User}", roleName, User.Identity?.Name);

            var role = await _roleManager.FindByNameAsync(roleName);
            if (role == null)
            {
                _logger.LogWarning("DeleteRole failed: Role {RoleName} not found", roleName);
                return NotFound(new { message = "Role not found!" });
            }

            var result = await _roleManager.DeleteAsync(role);
            if (result.Succeeded)
            {
                _logger.LogInformation("Role {RoleName} deleted by {User}", roleName, User.Identity?.Name);
                return NoContent();
            }

            _logger.LogError("DeleteRole failed for {RoleName}: {Errors}", roleName, result.Errors);
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
            _logger.LogInformation("AssignRole called: Role={RoleName}, UserEmail={UserEmail} by {User}", roleName, userEmail, User.Identity?.Name);

            var user = await _userManager.FindByEmailAsync(userEmail);
            if (user == null)
            {
                _logger.LogWarning("AssignRole failed: User {UserEmail} not found", userEmail);
                return NotFound(new { message = "User not found!" });
            }

            var result = await _userManager.AddToRoleAsync(user, roleName);
            if (result.Succeeded)
            {
                _logger.LogInformation("Role {RoleName} assigned to {UserEmail} by {User}", roleName, userEmail, User.Identity?.Name);
                return Ok(new { message = "Role assigned successfully!" });
            }

            _logger.LogError("AssignRole failed for {UserEmail}: {Errors}", userEmail, result.Errors);
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
            _logger.LogInformation("RemoveRole called: Role={RoleName}, UserEmail={UserEmail} by {User}", roleName, userEmail, User.Identity?.Name);

            var user = await _userManager.FindByEmailAsync(userEmail);
            if (user == null)
            {
                _logger.LogWarning("RemoveRole failed: User {UserEmail} not found", userEmail);
                return NotFound(new { message = "User not found!" });
            }

            var result = await _userManager.RemoveFromRoleAsync(user, roleName);
            if (result.Succeeded)
            {
                _logger.LogInformation("Role {RoleName} removed from {UserEmail} by {User}", roleName, userEmail, User.Identity?.Name);
                return NoContent();
            }

            _logger.LogError("RemoveRole failed for {UserEmail}: {Errors}", userEmail, result.Errors);
            return BadRequest(result.Errors);
        }

        /// <summary>
        /// Gets all users.
        /// </summary>
        /// <returns>A list of all users.</returns>
        [HttpGet("/api/users")]
        [SwaggerOperation(Summary = "Gets all users.")]
        [SwaggerResponse(200, "Users retrieved successfully.", typeof(IEnumerable<UserDto>))]
        public IActionResult GetUsers()
        {
            _logger.LogInformation("GetUsers called by {User}", User.Identity?.Name);

            var users = _userManager.Users.ToList();
            var dtos = _mapper.Map<IEnumerable<UserDto>>(users);
            return Ok(dtos);
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
            _logger.LogInformation("DeleteUser called for Id={UserId} by {User}", id, User.Identity?.Name);

            var user = await _userManager.FindByIdAsync(id);
            if (user == null)
            {
                _logger.LogWarning("DeleteUser failed: User {UserId} not found", id);
                return NotFound(new { message = "User not found!" });
            }

            var result = await _userManager.DeleteAsync(user);
            if (result.Succeeded)
            {
                _logger.LogInformation("User {UserId} deleted by {User}", id, User.Identity?.Name);
                return NoContent();
            }

            _logger.LogError("DeleteUser failed for {UserId}: {Errors}", id, result.Errors);
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
            _logger.LogInformation("AssignPermission called: Role={RoleName}, Permission={Permission} by {User}", roleName, permission, User.Identity?.Name);

            if (!await _roleManager.RoleExistsAsync(roleName))
            {
                _logger.LogWarning("AssignPermission failed: Role {RoleName} not found", roleName);
                return NotFound(new { message = "Role not found!" });
            }

            var rolePermission = new RolePermission
            {
                RoleName = roleName,
                Permission = permission
            };

            _context.RolePermissions.Add(rolePermission);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Permission {Permission} assigned to Role {RoleName} by {User}", permission, roleName, User.Identity?.Name);
            return Ok(new { message = "Permission assigned successfully!" });
        }
    }
}
