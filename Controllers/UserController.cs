using garge_api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
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
    [Route("api/[controller]")]
    [EnableCors("AllowAllOrigins")]
    [Authorize]
    public class UserController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public UserController(ApplicationDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Gets the user profile by ID.
        /// </summary>
        /// <param name="id">The user ID.</param>
        /// <returns>An IActionResult.</returns>
        [HttpGet("profile/{id}")]
        [SwaggerOperation(Summary = "Gets the user profile by ID.")]
        [SwaggerResponse(200, "User profile retrieved successfully.", typeof(UserProfile))]
        [SwaggerResponse(403, "User does not have the required role.")]
        [SwaggerResponse(404, "User profile not found.")]
        public async Task<IActionResult> GetUserProfile(string id)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim == null || userIdClaim.Value != id.ToString())
            {
                return Forbid();
            }

            var userProfile = await _context.UserProfiles
                .SingleOrDefaultAsync(up => up.Id == id);

            if (userProfile == null)
            {
                return NotFound(new { message = "User profile not found!" });
            }

            return Ok(userProfile);
        }
    }
}
