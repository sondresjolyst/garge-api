using garge_api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace garge_api.Controllers
{
    /// <summary>
    /// Handles authentication-related actions.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [EnableCors("AllowAllOrigins")]
    [Authorize]
    public class AuthController : ControllerBase
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly IConfiguration _configuration;
        private readonly ApplicationDbContext _context;

        public AuthController(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager, IConfiguration configuration, ApplicationDbContext context)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _configuration = configuration;
            _context = context;
        }

        /// <summary>
        /// Registers a new user.
        /// </summary>
        /// <param name="model">The registration model.</param>
        /// <returns>An IActionResult.</returns>
        [HttpPost("register")]
        [AllowAnonymous]
        public async Task<IActionResult> Register([FromBody] ApplicationUser user, string password)
        {
            if (string.IsNullOrEmpty(user.Email))
            {
                return BadRequest(new { message = "Email is required!" });
            }

            var existingUser = await _userManager.FindByEmailAsync(user.Email);
            if (existingUser != null)
            {
                return Conflict(new { message = "Email is already registered!" });
            }

            var result = await _userManager.CreateAsync(user, password);
            if (result.Succeeded)
            {
                var userProfile = new UserProfile
                {
                    Id = user.Id,
                    FirstName = user.FirstName,
                    LastName = user.LastName,
                    Email = user.Email,
                    User = user
                };
                _context.UserProfiles.Add(userProfile);
                await _context.SaveChangesAsync();

                return Ok(new { message = "User registered successfully!" });
            }

            return BadRequest(result.Errors);
        }

        /// <summary>
        /// Logs in a user.
        /// </summary>
        /// <param name="login">The login model.</param>
        /// <returns>An IActionResult.</returns>
        [HttpPost("login")]
        [AllowAnonymous]
        public async Task<IActionResult> Login([FromBody] LoginModel login)
        {
            if (string.IsNullOrEmpty(login.Email) || string.IsNullOrEmpty(login.Password))
            {
                return BadRequest(new { message = "Email and Password are required!" });
            }

            var email = login.Email ?? string.Empty;
            var user = await _userManager.FindByEmailAsync(email);
            if (user == null)
            {
                return Unauthorized(new { message = "Invalid credentials!" });
            }

            var result = await _signInManager.PasswordSignInAsync(user.UserName ?? string.Empty, login.Password, false, false);
            if (!result.Succeeded)
            {
                return Unauthorized(new { message = "Invalid credentials!" });
            }

            var userRoles = await _userManager.GetRolesAsync(user);

            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.UTF8.GetBytes(_configuration["Jwt:Key"] ?? string.Empty);
            var claims = new List<Claim>
    {
        new Claim(JwtRegisteredClaimNames.Sub, user.Id?.ToString() ?? string.Empty),
        new Claim(JwtRegisteredClaimNames.UniqueName, user.UserName ?? string.Empty),
        new Claim(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty)
    };

            claims.AddRange(userRoles.Select(role => new Claim(ClaimTypes.Role, role)));

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.AddDays(7),
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature),
                Issuer = _configuration["Jwt:Issuer"],
                Audience = _configuration["Jwt:Issuer"]
            };

            var token = tokenHandler.CreateToken(tokenDescriptor);
            var tokenString = tokenHandler.WriteToken(token);

            return Ok(new { token = tokenString });
        }
    }
}
