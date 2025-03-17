using garge_api.Models;
using garge_api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Swashbuckle.AspNetCore.Annotations;
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
        private readonly EmailService _emailService;

        public AuthController(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager, IConfiguration configuration, ApplicationDbContext context, EmailService emailService)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _configuration = configuration;
            _context = context;
            _emailService = emailService;
        }

        /// <summary>
        /// Registers a new user.
        /// </summary>
        /// <param name="registerUserDto">The registration data.</param>
        /// <returns>An IActionResult.</returns>
        [HttpPost("register")]
        [AllowAnonymous]
        [SwaggerOperation(Summary = "Registers a new user.")]
        [SwaggerResponse(200, "User registered successfully.")]
        [SwaggerResponse(400, "Invalid request.")]
        [SwaggerResponse(409, "Email is already registered.")]
        public async Task<IActionResult> Register([FromBody] RegisterUserDto registerUserDto)
        {
            if (string.IsNullOrEmpty(registerUserDto.Email))
            {
                return BadRequest(new { message = "Email is required!" });
            }

            var existingUser = await _userManager.FindByEmailAsync(registerUserDto.Email);
            if (existingUser != null)
            {
                return Conflict(new { message = "Email is already registered!" });
            }

            var user = new ApplicationUser
            {
                UserName = registerUserDto.UserName,
                Email = registerUserDto.Email,
                FirstName = registerUserDto.FirstName,
                LastName = registerUserDto.LastName
            };

            var result = await _userManager.CreateAsync(user, registerUserDto.Password);
            if (result.Succeeded)
            {
                await _userManager.AddToRoleAsync(user, "Default");

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

                var verificationCode = GenerateVerificationCode();
                user.EmailVerificationCode = verificationCode;
                user.EmailVerificationCodeExpiration = DateTime.UtcNow.AddHours(1);
                await _userManager.UpdateAsync(user);

                await _emailService.SendEmailAsync(user.Email, "Confirm your email", $"Your verification code is: {verificationCode}");

                return Ok(new { message = "User registered successfully. Please check your email to confirm your account." });
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
        [SwaggerOperation(Summary = "Logs in a user.")]
        [SwaggerResponse(200, "User logged in successfully.")]
        [SwaggerResponse(400, "Invalid request.")]
        [SwaggerResponse(401, "Invalid credentials.")]
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
                NotBefore = DateTime.UtcNow.AddSeconds(-5),
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature),
                Issuer = _configuration["Jwt:Issuer"],
                Audience = _configuration["Jwt:Issuer"]
            };

            var token = tokenHandler.CreateToken(tokenDescriptor);
            var tokenString = tokenHandler.WriteToken(token);

            return Ok(new { token = tokenString });
        }

        /// <summary>
        /// Resends the email confirmation.
        /// </summary>
        /// <param name="email">The user's email.</param>
        /// <returns>An IActionResult.</returns>
        [HttpPost("resendconfirmation")]
        [AllowAnonymous]
        [SwaggerOperation(Summary = "Resends the email confirmation.")]
        [SwaggerResponse(200, "Email confirmation sent successfully.")]
        [SwaggerResponse(400, "Invalid request.")]
        [SwaggerResponse(404, "User not found.")]
        public async Task<IActionResult> ResendEmailConfirmation([FromBody] ResendEmailConfirmationDto model)
        {
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null)
            {
                return NotFound(new { message = "User not found!" });
            }

            if (user.EmailConfirmed)
            {
                return BadRequest(new { message = "Email is already confirmed!" });
            }

            var verificationCode = GenerateVerificationCode();
            user.EmailVerificationCode = verificationCode;
            user.EmailVerificationCodeExpiration = DateTime.UtcNow.AddHours(1);
            await _userManager.UpdateAsync(user);

            await _emailService.SendEmailAsync(user.Email, "Confirm your email", $"Your verification code is: {verificationCode}");

            return Ok(new { message = "Email confirmation sent successfully. Please check your email to confirm your account." });
        }

        /// <summary>
        /// Confirms the email using the verification code.
        /// </summary>
        /// <param name="model">The verification data.</param>
        /// <returns>An IActionResult.</returns>
        [HttpPost("confirmemail")]
        [AllowAnonymous]
        [SwaggerOperation(Summary = "Confirms the email using the verification code.")]
        [SwaggerResponse(200, "Email confirmed successfully.")]
        [SwaggerResponse(400, "Invalid request.")]
        [SwaggerResponse(404, "User not found.")]
        public async Task<IActionResult> ConfirmEmail([FromBody] ConfirmEmailDto model)
        {
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null)
            {
                return NotFound(new { message = "User not found!" });
            }

            if (user.EmailConfirmed)
            {
                return BadRequest(new { message = "Email is already confirmed!" });
            }

            if (user.EmailVerificationCode != model.Code || user.EmailVerificationCodeExpiration < DateTime.UtcNow)
            {
                return BadRequest(new { message = "Invalid or expired verification code!" });
            }

            user.EmailConfirmed = true;
            user.EmailVerificationCode = null;
            user.EmailVerificationCodeExpiration = null;
            await _userManager.UpdateAsync(user);

            return Ok(new { message = "Email confirmed successfully!" });
        }

        private string GenerateVerificationCode()
        {
            var random = new Random();
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            return new string(Enumerable.Repeat(chars, 6)
                .Select(s => s[random.Next(s.Length)]).ToArray());
        }
    }

    public class ResendEmailConfirmationDto
    {
        public string Email { get; set; }
    }

    public class ConfirmEmailDto
    {
        public string Email { get; set; }
        public string Code { get; set; }
    }
}
