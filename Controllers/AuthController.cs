using AutoMapper;
using garge_api.Dtos.Auth;
using garge_api.Models;
using garge_api.Models.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Swashbuckle.AspNetCore.Annotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Security.Cryptography;

namespace garge_api.Controllers
{
    /// <summary>
    /// Handles authentication-related actions.
    /// </summary>
    [ApiController]
    [Route("api/auth")]
    [EnableCors("AllowAllOrigins")]
    [Authorize]
    public class AuthController : ControllerBase
    {
        private readonly UserManager<User> _userManager;
        private readonly SignInManager<User> _signInManager;
        private readonly IConfiguration _configuration;
        private readonly ApplicationDbContext _context;
        private readonly EmailService _emailService;
        private readonly IMapper _mapper;

        public AuthController(
            UserManager<User> userManager,
            SignInManager<User> signInManager,
            IConfiguration configuration,
            ApplicationDbContext context,
            EmailService emailService,
            IMapper mapper)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _configuration = configuration;
            _context = context;
            _emailService = emailService;
            _mapper = mapper;
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

            // Use AutoMapper to map DTO to User
            var user = _mapper.Map<User>(registerUserDto);

            var result = await _userManager.CreateAsync(user, registerUserDto.Password);
            if (result.Succeeded)
            {
                await _userManager.AddToRoleAsync(user, "Default");

                var userProfile = new UserProfile
                {
                    Id = user.Id,
                    FirstName = user.FirstName,
                    LastName = user.LastName,
                    Email = user.Email!,
                    User = user
                };
                _context.UserProfiles.Add(userProfile);
                await _context.SaveChangesAsync();

                var verificationCode = GenerateVerificationCode();
                user.EmailVerificationCode = verificationCode;
                user.EmailVerificationCodeExpiration = DateTime.UtcNow.AddHours(1);
                await _userManager.UpdateAsync(user);

                await _emailService.SendEmailAsync(user.Email!, "Confirm your email", $"Your verification code is: {verificationCode}");

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

            var rawToken = GenerateRefreshToken();
            var hashedToken = HashText(rawToken);

            // Limit to 5 active tokens per user
            var userTokens = _context.RefreshTokens
                .Where(t => t.UserId == user.Id && t.Revoked == null && t.Expires > DateTime.UtcNow)
                .OrderBy(t => t.Created)
                .ToList();
            if (userTokens.Count >= 5)
            {
                var oldest = userTokens.First();
                oldest.Revoked = DateTime.UtcNow;
            }

            var refreshToken = new RefreshToken
            {
                Token = hashedToken,
                UserId = user.Id,
                Expires = DateTime.UtcNow.AddMonths(6),
                Created = DateTime.UtcNow
            };
            _context.RefreshTokens.Add(refreshToken);
            await _context.SaveChangesAsync();

            return Ok(new { token = tokenString, refreshToken = rawToken });
        }

        /// <summary>
        /// Resends the email verification email.
        /// </summary>
        /// <param name="model">The user's email.</param>
        /// <returns>An IActionResult.</returns>
        [HttpPost("resend-email-verification")]
        [AllowAnonymous]
        [SwaggerOperation(Summary = "Resends the email verification code.")]
        [SwaggerResponse(200, "Email verification sent successfully.")]
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
                return BadRequest(new { message = "Email is already verified!" });
            }

            var verificationCode = GenerateVerificationCode();
            user.EmailVerificationCode = verificationCode;
            user.EmailVerificationCodeExpiration = DateTime.UtcNow.AddHours(1);
            await _userManager.UpdateAsync(user);

            await _emailService.SendEmailAsync(user.Email!, "Verify your email", $"Your verification code is: {verificationCode}");

            return Ok(new { message = "Email verification sent successfully. Please check your email to verify your account." });
        }

        /// <summary>
        /// Verify the email using the verification code.
        /// </summary>
        /// <param name="model">The verification data.</param>
        /// <returns>An IActionResult.</returns>
        [HttpPost("verify-email")]
        [AllowAnonymous]
        [SwaggerOperation(Summary = "Verify the email using the verification code.")]
        [SwaggerResponse(200, "Email verified successfully.")]
        [SwaggerResponse(400, "Invalid request.")]
        [SwaggerResponse(404, "User not found.")]
        public async Task<IActionResult> VerifyEmail([FromBody] VerifyEmailDto model)
        {
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null)
            {
                return NotFound(new { message = "User not found!" });
            }

            if (user.EmailConfirmed)
            {
                return BadRequest(new { message = "Email is already verified!" });
            }

            if (user.EmailVerificationCode != model.Code || user.EmailVerificationCodeExpiration < DateTime.UtcNow)
            {
                return BadRequest(new { message = "Invalid or expired verification code!" });
            }

            user.EmailConfirmed = true;
            user.EmailVerificationCode = null;
            user.EmailVerificationCodeExpiration = null;
            await _userManager.UpdateAsync(user);

            return Ok(new { message = "Email verified successfully!" });
        }

        [HttpPost("refresh-token")]
        [AllowAnonymous]
        [SwaggerOperation(Summary = "Refresh JWT using a valid refresh token.")]
        [SwaggerResponse(200, "Token refreshed successfully.")]
        [SwaggerResponse(400, "Invalid request.")]
        [SwaggerResponse(401, "Invalid or expired refresh token.")]
        public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequestDto request)
        {
            var principal = GetPrincipalFromExpiredToken(request.Token);
            if (principal == null)
            {
                Console.WriteLine("invalid token1");
                return BadRequest(new { message = "Invalid token" });
            }

            var userId = principal.Claims.FirstOrDefault(x =>
                x.Type == JwtRegisteredClaimNames.Sub ||
                x.Type == ClaimTypes.NameIdentifier
            )?.Value;
            if (userId == null)
            {
                Console.WriteLine(userId);
                Console.WriteLine("invalid token2");
                return BadRequest(new { message = "Invalid token" });
            }

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
                return Unauthorized(new { message = "User not found" });

            var hashedInput = HashText(request.RefreshToken);
            var storedToken = _context.RefreshTokens
                .FirstOrDefault(t => t.Token == hashedInput && t.UserId == user.Id && t.Revoked == null && t.Expires > DateTime.UtcNow);

            if (storedToken == null)
                return Unauthorized(new { message = "Invalid or expired refresh token" });

            // Generate new JWT and refresh token
            var userRoles = await _userManager.GetRolesAsync(user);
            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.UTF8.GetBytes(_configuration["Jwt:Key"] ?? string.Empty);
            var claims = new List<Claim>
            {

                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString() ?? string.Empty),
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

            var newToken = tokenHandler.CreateToken(tokenDescriptor);
            var newTokenString = tokenHandler.WriteToken(newToken);

            // Revoke old refresh token and issue a new one
            storedToken.Revoked = DateTime.UtcNow;
            var newRawToken = GenerateRefreshToken();
            var newHashedToken = HashText(newRawToken);

            // Limit to 5 active tokens per user
            var userTokens = _context.RefreshTokens
                .Where(t => t.UserId == user.Id && t.Revoked == null && t.Expires > DateTime.UtcNow)
                .OrderBy(t => t.Created)
                .ToList();
            if (userTokens.Count >= 5)
            {
                var oldest = userTokens.First();
                oldest.Revoked = DateTime.UtcNow;
            }

            var newRefreshToken = new RefreshToken
            {
                Token = newHashedToken,
                UserId = user.Id,
                Expires = DateTime.UtcNow.AddMonths(6),
                Created = DateTime.UtcNow
            };
            _context.RefreshTokens.Add(newRefreshToken);
            await _context.SaveChangesAsync();

            return Ok(new { token = newTokenString, refreshToken = newRawToken });
        }

        /// <summary>
        /// Request a password reset code via email.
        /// </summary>
        [HttpPost("request-password-reset")]
        [AllowAnonymous]
        [SwaggerOperation(Summary = "Request a password reset code via email.")]
        [SwaggerResponse(200, "Reset code sent.")]
        [SwaggerResponse(404, "User not found.")]
        public async Task<IActionResult> RequestPasswordReset([FromBody] RequestPasswordResetDto model)
        {
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null)
                return NotFound(new { message = "User not found!" });

            var code = GenerateVerificationCode();
            user.PasswordResetCodeHash = HashText(code);
            user.PasswordResetCodeExpiration = DateTime.UtcNow.AddMinutes(30);
            await _userManager.UpdateAsync(user);

            await _emailService.SendEmailAsync(user.Email!, "Password Reset Code", $"Your password reset code is: {code}");

            return Ok(new { message = "Password reset code sent. Please check your email." });
        }

        /// <summary>
        /// Reset password using the code sent via email.
        /// </summary>
        [HttpPost("reset-password")]
        [AllowAnonymous]
        [SwaggerOperation(Summary = "Reset password using the code sent via email.")]
        [SwaggerResponse(200, "Password reset successfully.")]
        [SwaggerResponse(400, "Invalid or expired code.")]
        [SwaggerResponse(404, "User not found.")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordDto model)
        {
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null)
                return NotFound(new { message = "User not found!" });

            if (user.PasswordResetCodeHash != HashText(model.Code) || user.PasswordResetCodeExpiration < DateTime.UtcNow)
                return BadRequest(new { message = "Invalid or expired reset code!" });

            var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
            var result = await _userManager.ResetPasswordAsync(user, resetToken, model.NewPassword);

            if (!result.Succeeded)
                return BadRequest(result.Errors);

            user.PasswordResetCodeHash = null;
            user.PasswordResetCodeExpiration = null;
            await _userManager.UpdateAsync(user);

            return Ok(new { message = "Password reset successfully." });
        }

        private string GenerateVerificationCode()
        {
            var random = new Random();
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            return new string(Enumerable.Repeat(chars, 6)
                .Select(s => s[random.Next(s.Length)]).ToArray());
        }

        private string HashText(string text)
        {
            using var sha256 = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(text);
            var hash = sha256.ComputeHash(bytes);
            return Convert.ToBase64String(hash);
        }

        private string GenerateRefreshToken()
        {
            var randomNumber = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(randomNumber);
            return Convert.ToBase64String(randomNumber);
        }

        private ClaimsPrincipal? GetPrincipalFromExpiredToken(string token)
        {
            var tokenValidationParameters = new TokenValidationParameters
            {
                ValidateAudience = false,
                ValidateIssuer = false,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["Jwt:Key"] ?? string.Empty)),
                ValidateLifetime = false
            };

            var tokenHandler = new JwtSecurityTokenHandler();
            try
            {
                var principal = tokenHandler.ValidateToken(token, tokenValidationParameters, out var securityToken);
                if (securityToken is JwtSecurityToken jwtSecurityToken &&
                    jwtSecurityToken.Header.Alg.Equals(SecurityAlgorithms.HmacSha256, StringComparison.InvariantCultureIgnoreCase))
                {
                    return principal;
                }
            }
            catch
            {
                return null;
            }
            return null;
        }
    }
}
