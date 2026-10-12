using MapsterMapper;
using garge_api.Dtos.Auth;
using garge_api.Helpers;
using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Models.Auth;
using garge_api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SjolystInnovation.AppKit.Auth;
using Swashbuckle.AspNetCore.Annotations;

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
        private readonly IJwtTokenService _jwt;
        private readonly IRefreshTokenService _refreshTokens;
        private readonly ApplicationDbContext _context;
        private readonly IEmailService _emailService;
        private readonly IMapper _mapper;
        private readonly ILogger<AuthController> _logger;

        public AuthController(
            UserManager<User> userManager,
            SignInManager<User> signInManager,
            IJwtTokenService jwt,
            IRefreshTokenService refreshTokens,
            ApplicationDbContext context,
            IEmailService emailService,
            IMapper mapper,
            ILogger<AuthController> logger)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _jwt = jwt;
            _refreshTokens = refreshTokens;
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

            // Map the registration DTO to the User entity.
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

                var verificationCode = EmailVerificationCodes.Issue(user);
                await _userManager.UpdateAsync(user);

                var settings = await _context.AppSettings.FindAsync(1) ?? new AppSettings();
                await _emailService.SendEmailAsync(user.Email!, "Confirm your email",
                    AuthEmailTemplates.VerificationCode(settings, user.FirstName, verificationCode));

                _logger.LogInformation("User registered successfully {UserId}", user.Id);
                return Ok(new { message = genericResponse });
            }

            _logger.LogError("Register failed: {@Errors}", result.Errors);

            // Return the field-keyed error shape the client parses so a server-only failure such as
            // a taken username surfaces on its field instead of a generic "failed to register".
            // Identity emits username, email, and password error codes.
            var fieldErrors = result.Errors
                .GroupBy(e => RegisterErrorField(e.Code))
                .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray());
            return BadRequest(new { errors = fieldErrors });
        }

        /// <summary>Maps an ASP.NET Identity error code to the registration form field it belongs to.</summary>
        private static string RegisterErrorField(string code) =>
            code.Contains("UserName", StringComparison.OrdinalIgnoreCase) ? "UserName"
            : code.Contains("Email", StringComparison.OrdinalIgnoreCase) ? "Email"
            : code.Contains("Password", StringComparison.OrdinalIgnoreCase) ? "Password"
            : "UserName";

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
                return Unauthorized(new { message = "Invalid email or password." });
            }

            var result = await _signInManager.PasswordSignInAsync(user.UserName ?? string.Empty, login.Password, false, lockoutOnFailure: true);
            if (!result.Succeeded)
            {
                if (result.IsLockedOut)
                {
                    _logger.LogWarning("Login blocked: account locked {UserId}", user.Id);
                    return Unauthorized(new { message = "Too many failed attempts — this login is temporarily locked. Try again later, or reset your password." });
                }
                _logger.LogWarning("Login failed: Invalid credentials");
                return Unauthorized(new { message = "Invalid email or password." });
            }

            var userRoles = await _userManager.GetRolesAsync(user);
            var tokenString = _jwt.Build(user, userRoles);
            var rawToken = await _refreshTokens.IssueAsync(user.Id);
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
                var verificationCode = EmailVerificationCodes.Issue(user);
                await _userManager.UpdateAsync(user);
                var settings = await _context.AppSettings.FindAsync(1) ?? new AppSettings();
                await _emailService.SendEmailAsync(user.Email!, "Verify your email",
                    AuthEmailTemplates.VerificationCode(settings, user.FirstName, verificationCode));
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
        [SwaggerResponse(429, "Too many wrong codes. A new code is needed.")]
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

            switch (EmailVerificationCodes.Check(user, model.Code))
            {
                case CodeCheck.AlreadyVerified:
                    _logger.LogWarning("VerifyEmail: already verified");
                    return BadRequest(new { message = genericInvalid });
                case CodeCheck.TooManyAttempts:
                    _logger.LogWarning("VerifyEmail failed: Too many attempts {UserId}", user.Id);
                    return StatusCode(429, new { message = "Too many attempts. Request a new verification code." });
                case CodeCheck.Invalid:
                    await _userManager.UpdateAsync(user);
                    _logger.LogWarning("VerifyEmail failed: Invalid or expired code");
                    return BadRequest(new { message = genericInvalid });
            }

            EmailVerificationCodes.Complete(user);
            await _userManager.UpdateAsync(user);

            _logger.LogInformation("Email verified successfully {UserId}", user.Id);
            return Ok(new { message = "Email verified successfully!" });
        }

        /// <summary>
        /// Signs out by deleting the refresh token. It answers the same whether or not the token was live.
        /// </summary>
        /// <param name="request">The refresh token to delete.</param>
        /// <returns>An IActionResult.</returns>
        [HttpPost("logout")]
        [AllowAnonymous]
        [SwaggerOperation(Summary = "Signs out by deleting the refresh token.")]
        [SwaggerResponse(204, "Signed out.")]
        [SwaggerResponse(400, "Invalid request.")]
        public async Task<IActionResult> Logout([FromBody] LogoutRequestDto request)
        {
            var revoked = await _refreshTokens.RevokeAsync(request.RefreshToken);
            _logger.LogInformation("Logout called {@LogData}", new { revoked });
            return NoContent();
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

            var userId = _jwt.ReadUserIdFromExpired(request.Token);
            if (userId == null)
            {
                _logger.LogWarning("RefreshToken failed: Invalid token provided");
                return BadRequest(new { message = "Invalid token" });
            }

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null || user.IsDeleted)
            {
                _logger.LogWarning("RefreshToken failed: User not found {@LogData}", new { userId });
                return Unauthorized(new { message = "User not found" });
            }

            var rotation = await _refreshTokens.RotateAsync(user.Id, request.RefreshToken);
            if (rotation.Status != RotationStatus.Rotated)
            {
                _logger.LogWarning("RefreshToken failed: {Status} {@LogData}", rotation.Status, new { user.Id });
                return Unauthorized(new { message = "Invalid or expired refresh token" });
            }

            var userRoles = await _userManager.GetRolesAsync(user);
            var newTokenString = _jwt.Build(user, userRoles);
            var newRawToken = rotation.RefreshToken!;

            _logger.LogInformation("Token refreshed successfully {@LogData}", new { user.Id });
            return Ok(new { token = newTokenString, refreshToken = newRawToken });
        }
        /// <summary>
        /// Request a password reset code via email.
        /// </summary>
        /// <param name="model">The password reset request data.</param>
        /// <returns>An IActionResult.</returns>
        [HttpPost("request-password-reset")]
        [AllowAnonymous]
        [SwaggerOperation(Summary = "Request a password reset code via email.")]
        [SwaggerResponse(200, "Always returns 200; a reset code is sent only if the email is registered.")]
        public async Task<IActionResult> RequestPasswordReset([FromBody] RequestPasswordResetDto model)
        {
            _logger.LogInformation("RequestPasswordReset called");

            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user != null)
            {
                var code = PasswordResetCodes.Issue(user);
                await _userManager.UpdateAsync(user);
                var settings = await _context.AppSettings.FindAsync(1) ?? new AppSettings();
                await _emailService.SendEmailAsync(user.Email!, "Password Reset Code",
                    AuthEmailTemplates.PasswordReset(settings, user.FirstName, code));
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

            switch (PasswordResetCodes.Check(user, model.Code))
            {
                case CodeCheck.TooManyAttempts:
                    _logger.LogWarning("ResetPassword failed: Too many attempts {UserId}", user.Id);
                    return StatusCode(429, new { message = "Too many attempts. Request a new reset code." });
                case CodeCheck.Invalid:
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

            PasswordResetCodes.Complete(user);
            await _userManager.UpdateAsync(user);
            // A new password ends every session, so one that was taken over cannot carry on.
            var signedOut = await _refreshTokens.RevokeAllForUserAsync(user.Id);
            _logger.LogInformation("Password reset signed out {Sessions} session(s) {UserId}", signedOut, user.Id);

            _logger.LogInformation("Password reset successfully {UserId}", user.Id);
            return Ok(new { message = "Password reset successfully." });
        }
    }
}
