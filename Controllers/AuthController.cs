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
        private readonly ApplicationDbContext _context;
        private readonly IEmailService _emailService;
        private readonly IMapper _mapper;
        private readonly ILogger<AuthController> _logger;

        public AuthController(
            UserManager<User> userManager,
            ApplicationDbContext context,
            IEmailService emailService,
            IMapper mapper,
            ILogger<AuthController> logger)
        {
            _userManager = userManager;
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
    }
}
