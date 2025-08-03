using AutoMapper;
using garge_api.Dtos.User;
using garge_api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;
using System.Security.Claims;

namespace garge_api.Controllers
{
    /// <summary>
    /// Handles user-related actions.
    /// </summary>
    [ApiController]
    [Route("api/users")]
    [EnableCors("AllowAllOrigins")]
    [Authorize]
    public class UserController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<User> _userManager;
        private readonly IMapper _mapper;
        private readonly ILogger<UserController> _logger;

        public UserController(ApplicationDbContext context, UserManager<User> userManager, IMapper mapper, ILogger<UserController> logger)
        {
            _context = context;
            _userManager = userManager;
            _mapper = mapper;
            _logger = logger;
        }

        /// <summary>
        /// Gets the user profile by ID.
        /// </summary>
        /// <param name="id">The user ID.</param>
        /// <returns>An IActionResult.</returns>
        [HttpGet("{id}/profile")]
        [SwaggerOperation(Summary = "Gets the user profile by ID.")]
        [SwaggerResponse(200, "User profile retrieved successfully.", typeof(UserProfile))]
        [SwaggerResponse(403, "User does not have the required role.")]
        [SwaggerResponse(404, "User profile not found.")]
        public async Task<IActionResult> GetUserProfile(string id)
        {
            _logger.LogInformation("GetUserProfile called by {@LogData}", new { User = User.Identity?.Name, id });

            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim == null || userIdClaim.Value != id.ToString())
            {
                _logger.LogWarning("GetUserProfile forbidden for {@LogData}", new { User = User.Identity?.Name, id, ClaimId = userIdClaim?.Value });
                return Forbid();
            }

            var userProfile = await _context.UserProfiles
                .Include(up => up.User)
                .SingleOrDefaultAsync(up => up.Id == id);

            if (userProfile == null)
            {
                _logger.LogWarning("GetUserProfile not found: UserProfile for {@LogData}", new { id });
                return NotFound(new { message = "User profile not found!" });
            }

            var user = await _userManager.FindByIdAsync(id);
            if (user == null)
            {
                _logger.LogWarning("GetUserProfile not found: User for {@LogData}", new { id });
                return NotFound(new { message = "User not found!" });
            }

            var profileResponse = _mapper.Map<UserProfileDto>(userProfile);

            _logger.LogInformation("User profile returned for {@LogData}", new { id });
            return Ok(profileResponse);
        }
    }
}
