using garge_api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
