using AutoMapper;
using garge_api.Dtos.Auth;
using garge_api.Helpers;
using garge_api.Models;
using garge_api.Models.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
        private readonly IEmailService _emailService;
        private readonly IMapper _mapper;
        private readonly ILogger<AuthController> _logger;

        public AuthController(
            UserManager<User> userManager,
            SignInManager<User> signInManager,
            IConfiguration configuration,
            ApplicationDbContext context,
            IEmailService emailService,
            IMapper mapper,
            ILogger<AuthController> logger)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _configuration = configuration;
            _context = context;
            _emailService = emailService;
            _mapper = mapper;
            _logger = logger;
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
            _logger.LogInformation("Register called");

            if (string.IsNullOrEmpty(registerUserDto.Email))
            {
                _logger.LogWarning("Register failed: Email is required");
                return BadRequest(new { message = "Email is required!" });
            }

            if (!registerUserDto.ConfirmAge16Plus)
            {
                _logger.LogWarning("Register failed: age confirmation missing");
                return BadRequest(new { message = "You must confirm you are 16 or older to register." });
            }

            if (!registerUserDto.AcceptTerms)
            {
                _logger.LogWarning("Register failed: terms not accepted");
                return BadRequest(new { message = "You must accept the Terms of Service to register." });
            }

            const string genericResponse = "If the email is available, an account has been created. Check your inbox to confirm.";

            var existingUser = await _userManager.FindByEmailAsync(registerUserDto.Email);
            if (existingUser != null)
            {
                _logger.LogWarning("Register: email already registered");
                return Ok(new { message = genericResponse });
            }

            // Use AutoMapper to map DTO to User
            var user = _mapper.Map<User>(registerUserDto);
            user.TermsAcceptedAt = DateTime.UtcNow;
            user.TermsVersion = registerUserDto.TermsVersion;
            user.TermsAcceptedIp = IpTruncator.Truncate(HttpContext?.Connection?.RemoteIpAddress?.ToString());

            var result = await _userManager.CreateAsync(user, registerUserDto.Password);
            if (result.Succeeded)
            {
                await _userManager.AddToRoleAsync(user, "Default");

                var userProfile = new UserProfile
                {
                    Id = user.Id,
                    User = user
                };
                _context.UserProfiles.Add(userProfile);
                await _context.SaveChangesAsync();

                var verificationCode = GenerateVerificationCode();
                user.EmailVerificationCodeHash = HashText(verificationCode);
                user.EmailVerificationCodeExpiration = DateTime.UtcNow.AddHours(1);
                await _userManager.UpdateAsync(user);

                await _emailService.SendEmailAsync(user.Email!, "Confirm your email", $"Your verification code is: {verificationCode}");

                _logger.LogInformation("User registered successfully {UserId}", user.Id);
                return Ok(new { message = genericResponse });
            }

            _logger.LogError("Register failed: {@Errors}", result.Errors);
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
            _logger.LogInformation("Login called");

            if (string.IsNullOrEmpty(login.Email) || string.IsNullOrEmpty(login.Password))
            {
                _logger.LogWarning("Login failed: Email and Password are required");
                return BadRequest(new { message = "Email and Password are required!" });
            }

            var email = login.Email ?? string.Empty;
            var user = await _userManager.FindByEmailAsync(email);
            if (user == null || user.IsDeleted)
            {
                _logger.LogWarning("Login failed: Invalid credentials");
                return Unauthorized(new { message = "Invalid credentials!" });
            }

            var result = await _signInManager.PasswordSignInAsync(user.UserName ?? string.Empty, login.Password, false, lockoutOnFailure: true);
            if (!result.Succeeded)
            {
                _logger.LogWarning("Login failed: Invalid credentials");
                return Unauthorized(new { message = "Invalid credentials!" });
            }

            var userRoles = await _userManager.GetRolesAsync(user);
            var tokenString = BuildJwt(user, userRoles);
            var rawToken = IssueRefreshToken(user.Id ?? throw new InvalidOperationException("Authenticated user has no ID."));
            await _context.SaveChangesAsync();

            _logger.LogInformation("User logged in successfully {UserId}", user.Id);
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
        public async Task<IActionResult> ResendEmailConfirmation([FromBody] ResendEmailConfirmationDto model)
        {
            _logger.LogInformation("ResendEmailConfirmation called");

            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user != null && !user.EmailConfirmed)
            {
                var verificationCode = GenerateVerificationCode();
                user.EmailVerificationCodeHash = HashText(verificationCode);
                user.EmailVerificationCodeExpiration = DateTime.UtcNow.AddHours(1);
                await _userManager.UpdateAsync(user);
                await _emailService.SendEmailAsync(user.Email!, "Verify your email", $"Your verification code is: {verificationCode}");
                _logger.LogInformation("Verification code resent for user {UserId}", user.Id);
            }

            return Ok(new { message = "If this email is registered and unverified, a verification code has been sent." });
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
            _logger.LogInformation("VerifyEmail called");

            const string genericInvalid = "Invalid or expired verification code!";

            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null)
            {
                _logger.LogWarning("VerifyEmail: unknown email");
                return BadRequest(new { message = genericInvalid });
            }

            if (user.EmailConfirmed)
            {
                _logger.LogWarning("VerifyEmail: already verified");
                return BadRequest(new { message = genericInvalid });
            }

            if (user.EmailVerificationCodeHash != HashText(model.Code) || user.EmailVerificationCodeExpiration < DateTime.UtcNow)
            {
                _logger.LogWarning("VerifyEmail failed: Invalid or expired code");
                return BadRequest(new { message = genericInvalid });
            }

            user.EmailConfirmed = true;
            user.EmailVerificationCodeHash = null;
            user.EmailVerificationCodeExpiration = null;
            await _userManager.UpdateAsync(user);

            _logger.LogInformation("Email verified successfully {UserId}", user.Id);
            return Ok(new { message = "Email verified successfully!" });
        }

        /// <summary>
        /// Refresh JWT using a valid refresh token.
        /// </summary>
        /// <param name="request">The refresh token request.</param>
        /// <returns>An IActionResult.</returns>
        [HttpPost("refresh-token")]
        [AllowAnonymous]
        [SwaggerOperation(Summary = "Refresh JWT using a valid refresh token.")]
        [SwaggerResponse(200, "Token refreshed successfully.")]
        [SwaggerResponse(400, "Invalid request.")]
        [SwaggerResponse(401, "Invalid or expired refresh token.")]
        public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequestDto request)
        {
            _logger.LogInformation("RefreshToken called");

            var principal = GetPrincipalFromExpiredToken(request.Token);
            if (principal == null)
            {
                _logger.LogWarning("RefreshToken failed: Invalid token provided");
                return BadRequest(new { message = "Invalid token" });
            }

            var userId = principal.Claims.FirstOrDefault(x =>
                x.Type == JwtRegisteredClaimNames.Sub ||
                x.Type == ClaimTypes.NameIdentifier
            )?.Value;
            if (userId == null)
            {
                _logger.LogWarning("RefreshToken failed: User ID not found in token");
                return BadRequest(new { message = "Invalid token" });
            }

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null || user.IsDeleted)
            {
                _logger.LogWarning("RefreshToken failed: User not found {@LogData}", new { userId });
                return Unauthorized(new { message = "User not found" });
            }

            var hashedInput = HashText(request.RefreshToken);

            using var tx = await _context.Database.BeginTransactionAsync();

            // Reuse detection: if the presented token matches a row we have
            // already revoked, treat it as a stolen-token replay and revoke
            // every refresh token in the family.
            var anyMatch = await _context.RefreshTokens
                .FirstOrDefaultAsync(t => t.Token == hashedInput && t.UserId == user.Id);

            if (anyMatch != null && anyMatch.Revoked != null)
            {
                var now = DateTime.UtcNow;
                var familyLive = await _context.RefreshTokens
                    .Where(t => t.UserId == user.Id && t.Revoked == null)
                    .ToListAsync();
                foreach (var t in familyLive) t.Revoked = now;
                await _context.SaveChangesAsync();
                await tx.CommitAsync();
                _logger.LogWarning("RefreshToken reuse detected; family revoked {@LogData}", new { user.Id });
                return Unauthorized(new { message = "Invalid or expired refresh token" });
            }

            var storedToken = anyMatch != null && anyMatch.Revoked == null && anyMatch.Expires > DateTime.UtcNow
                ? anyMatch
                : null;

            if (storedToken == null)
            {
                await tx.RollbackAsync();
                _logger.LogWarning("RefreshToken failed: Invalid or expired refresh token {@LogData}", new { user.Id });
                return Unauthorized(new { message = "Invalid or expired refresh token" });
            }

            // Revoke old refresh token, issue a fresh JWT + refresh token
            storedToken.Revoked = DateTime.UtcNow;

            var userRoles = await _userManager.GetRolesAsync(user);
            var newTokenString = BuildJwt(user, userRoles);
            var newRawToken = IssueRefreshToken(user.Id ?? throw new InvalidOperationException("Authenticated user has no ID."));

            await _context.SaveChangesAsync();
            await tx.CommitAsync();

            _logger.LogInformation("Token refreshed successfully {@LogData}", new { user.Id });
            return Ok(new { token = newTokenString, refreshToken = newRawToken });
        }

        private string BuildJwt(User user, IList<string> roles)
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.UTF8.GetBytes(_configuration["Jwt:Key"] ?? string.Empty);
            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id ?? string.Empty),
                new Claim(JwtRegisteredClaimNames.UniqueName, user.UserName ?? string.Empty),
                new Claim(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty)
            };
            claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.AddHours(1),
                NotBefore = DateTime.UtcNow.AddSeconds(-5),
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature),
                Issuer = _configuration["Jwt:Issuer"],
                Audience = _configuration["Jwt:Issuer"]
            };
            return tokenHandler.WriteToken(tokenHandler.CreateToken(tokenDescriptor));
        }

        /// <summary>
        /// Adds a new refresh token row for the user (capped at 5 active per user).
        /// Caller must SaveChangesAsync. Returns the raw (un-hashed) token to send to the client.
        /// </summary>
        private string IssueRefreshToken(string userId)
        {
            var rawToken = GenerateRefreshToken();
            var hashedToken = HashText(rawToken);

            var userTokens = _context.RefreshTokens
                .Where(t => t.UserId == userId && t.Revoked == null && t.Expires > DateTime.UtcNow)
                .OrderBy(t => t.Created)
                .ToList();
            if (userTokens.Count >= 5)
            {
                _context.RefreshTokens.Remove(userTokens.First());
                _logger.LogInformation("Oldest refresh token evicted (cap=5) {@LogData}", new { UserId = userId });
            }

            _context.RefreshTokens.Add(new RefreshToken
            {
                Token = hashedToken,
                UserId = userId,
                Expires = DateTime.UtcNow.AddMonths(6),
                Created = DateTime.UtcNow
            });
            return rawToken;
        }

        /// <summary>
        /// Request a password reset code via email.
        /// </summary>
        /// <param name="model">The password reset request data.</param>
        /// <returns>An IActionResult.</returns>
        [HttpPost("request-password-reset")]
        [AllowAnonymous]
        [SwaggerOperation(Summary = "Request a password reset code via email.")]
        [SwaggerResponse(200, "Reset code sent (always, to prevent email enumeration).")]
        public async Task<IActionResult> RequestPasswordReset([FromBody] RequestPasswordResetDto model)
        {
            _logger.LogInformation("RequestPasswordReset called");

            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user != null)
            {
                var code = GenerateVerificationCode();
                user.PasswordResetCodeHash = HashText(code);
                user.PasswordResetCodeExpiration = DateTime.UtcNow.AddMinutes(30);
                user.PasswordResetAttempts = 0;
                await _userManager.UpdateAsync(user);
                await _emailService.SendEmailAsync(user.Email!, "Password Reset Code", $"Your password reset code is: {code}");
                _logger.LogInformation("Password reset code sent for user {UserId}", user.Id);
            }

            return Ok(new { message = "If this email is registered, a password reset code has been sent." });
        }

        /// <summary>
        /// Reset password using the code sent via email.
        /// </summary>
        /// <param name="model">The reset password data.</param>
        /// <returns>An IActionResult.</returns>
        [HttpPost("reset-password")]
        [AllowAnonymous]
        [SwaggerOperation(Summary = "Reset password using the code sent via email.")]
        [SwaggerResponse(200, "Password reset successfully.")]
        [SwaggerResponse(400, "Invalid or expired code.")]
        [SwaggerResponse(404, "User not found.")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordDto model)
        {
            _logger.LogInformation("ResetPassword called");

            const string genericInvalid = "Invalid or expired reset code!";

            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null)
            {
                _logger.LogWarning("ResetPassword: unknown email");
                return BadRequest(new { message = genericInvalid });
            }

            if (user.PasswordResetAttempts >= 5)
            {
                _logger.LogWarning("ResetPassword failed: Too many attempts {UserId}", user.Id);
                return StatusCode(429, new { message = "Too many attempts. Request a new reset code." });
            }

            if (user.PasswordResetCodeHash != HashText(model.Code) || user.PasswordResetCodeExpiration < DateTime.UtcNow)
            {
                user.PasswordResetAttempts++;
                await _userManager.UpdateAsync(user);
                _logger.LogWarning("ResetPassword failed: Invalid or expired code");
                return BadRequest(new { message = genericInvalid });
            }

            var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
            var result = await _userManager.ResetPasswordAsync(user, resetToken, model.NewPassword);

            if (!result.Succeeded)
            {
                _logger.LogError("ResetPassword failed: {@Errors}", result.Errors);
                return BadRequest(result.Errors);
            }

            user.PasswordResetCodeHash = null;
            user.PasswordResetCodeExpiration = null;
            user.PasswordResetAttempts = 0;
            await _userManager.UpdateAsync(user);

            _logger.LogInformation("Password reset successfully {UserId}", user.Id);
            return Ok(new { message = "Password reset successfully." });
        }

        private static string GenerateVerificationCode()
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var result = new char[6];
            for (int i = 0; i < result.Length; i++)
                result[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
            return new string(result);
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
