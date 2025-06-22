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
            if (await _roleManager.RoleExistsAsync(dto.Name))
                return Conflict(new { message = "Role already exists!" });

            var result = await _roleManager.CreateAsync(new IdentityRole(dto.Name));
            if (result.Succeeded)
                return CreatedAtAction(nameof(GetRole), new { roleName = dto.Name }, dto);

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
            var role = await _roleManager.FindByNameAsync(roleName);
            if (role == null)
                return NotFound(new { message = "Role not found!" });

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
            var role = await _roleManager.FindByNameAsync(roleName);
            if (role == null)
                return NotFound(new { message = "Role not found!" });

            var result = await _roleManager.DeleteAsync(role);
            if (result.Succeeded)
                return NoContent();

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
            var user = await _userManager.FindByEmailAsync(userEmail);
            if (user == null)
                return NotFound(new { message = "User not found!" });

            var result = await _userManager.AddToRoleAsync(user, roleName);
            if (result.Succeeded)
                return Ok(new { message = "Role assigned successfully!" });

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
            var user = await _userManager.FindByEmailAsync(userEmail);
            if (user == null)
                return NotFound(new { message = "User not found!" });

            var result = await _userManager.RemoveFromRoleAsync(user, roleName);
            if (result.Succeeded)
                return NoContent();

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
            var user = await _userManager.FindByIdAsync(id);
            if (user == null)
                return NotFound(new { message = "User not found!" });

            var result = await _userManager.DeleteAsync(user);
            if (result.Succeeded)
                return NoContent();

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
            if (!await _roleManager.RoleExistsAsync(roleName))
                return NotFound(new { message = "Role not found!" });

            var rolePermission = new RolePermission
            {
                RoleName = roleName,
                Permission = permission
            };

            _context.RolePermissions.Add(rolePermission);
            await _context.SaveChangesAsync();

            return Ok(new { message = "Permission assigned successfully!" });
        }
    }
}
