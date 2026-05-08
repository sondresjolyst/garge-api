using AutoMapper;
using garge_api.Dtos.User;
using garge_api.Helpers;
using garge_api.Models;
using garge_api.Models.Auth;
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
    [Authorize(Policy = "ActiveSubscription")]
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
            _logger.LogInformation("GetUserProfile called by {@LogData}", new { CallerUserId = User.UserId(), id });

            if (!User.IsCallerOf(id))
            {
                _logger.LogWarning("GetUserProfile forbidden for {@LogData}", new { CallerUserId = User.UserId(), id });
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
            profileResponse.EmailConfirmed = user.EmailConfirmed;
            profileResponse.PriceZone = userProfile.PriceZone;

            _logger.LogInformation("User profile returned for {@LogData}", new { id });
            return Ok(profileResponse);
        }

        /// <summary>
        /// Deletes the caller's own account and all associated personal data (GDPR Article 17).
        /// </summary>
        [HttpDelete("{id}/account")]
        [SwaggerOperation(Summary = "Deletes the caller's own account and personal data.")]
        [SwaggerResponse(204, "Account deleted.")]
        [SwaggerResponse(403, "Forbidden.")]
        [SwaggerResponse(404, "User not found.")]
        public async Task<IActionResult> DeleteOwnAccount(string id)
        {
            if (!User.IsCallerOf(id)) return Forbid();

            var user = await _userManager.FindByIdAsync(id);
            if (user == null || user.IsDeleted)
                return NotFound(new { message = "User not found!" });

            // Soft-delete: scrub PII but keep the User row so Orders + Invoices retain
            // a valid FK for the 5-year retention window required by the Norwegian
            // Bookkeeping Act (bokføringsloven §13).
            ClearUserOwnedRows(id);
            await ScrubUserPiiAsync(user);

            var result = await _userManager.UpdateAsync(user);
            if (!result.Succeeded)
            {
                _logger.LogError("DeleteOwnAccount failed for {UserId}: {Errors}", LogSanitizer.Sanitize(id), result.Errors);
                return BadRequest(result.Errors);
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation("Account soft-deleted by user {UserId}", LogSanitizer.Sanitize(id));
            return NoContent();
        }

        /// <summary>
        /// Removes all per-user rows that should not survive soft-delete (custom
        /// names, refresh tokens, push subs, webhook subs, etc.). Add new
        /// user-owned tables here when introduced.
        /// </summary>
        private void ClearUserOwnedRows(string userId)
        {
            _context.RefreshTokens.RemoveRange(_context.RefreshTokens.Where(t => t.UserId == userId));
            _context.UserSensorCustomNames.RemoveRange(_context.UserSensorCustomNames.Where(x => x.UserId == userId));
            _context.UserSwitchCustomNames.RemoveRange(_context.UserSwitchCustomNames.Where(x => x.UserId == userId));
            _context.SensorActivities.RemoveRange(_context.SensorActivities.Where(a => a.UserId == userId));
            _context.PushSubscriptions.RemoveRange(_context.PushSubscriptions.Where(s => s.UserId == userId));
            _context.SensorOfflineNotifications.RemoveRange(_context.SensorOfflineNotifications.Where(n => n.UserId == userId));
            _context.WebhookSubscriptions.RemoveRange(_context.WebhookSubscriptions.Where(w => w.UserId == userId));
            _context.UserSensors.RemoveRange(_context.UserSensors.Where(us => us.UserId == userId));
            _context.UserSwitches.RemoveRange(_context.UserSwitches.Where(us => us.UserId == userId));
        }

        /// <summary>
        /// Sets the User row's PII fields to safe defaults and locks the account.
        /// Caller is responsible for committing the row via UpdateAsync + SaveChanges.
        /// </summary>
        private async Task ScrubUserPiiAsync(User user)
        {
            user.FirstName = "Deleted";
            user.LastName = "User";
            user.Email = null;
            user.NormalizedEmail = null;
            user.PhoneNumber = null;
            user.UserName = $"deleted-{user.Id}";
            user.NormalizedUserName = $"DELETED-{user.Id}".ToUpperInvariant();
            user.EmailConfirmed = false;
            user.EmailVerificationCodeHash = null;
            user.EmailVerificationCodeExpiration = null;
            user.PasswordResetCodeHash = null;
            user.PasswordResetCodeExpiration = null;
            user.PasswordResetAttempts = 0;
            user.LockoutEnabled = true;
            user.LockoutEnd = DateTimeOffset.MaxValue;
            user.IsDeleted = true;
            user.DeletedAt = DateTime.UtcNow;
            await _userManager.UpdateSecurityStampAsync(user);
        }

        /// <summary>
        /// Exports all personal data held for the caller's account (GDPR Article 20).
        /// </summary>
        [HttpGet("{id}/export")]
        [SwaggerOperation(Summary = "Exports the caller's personal data in JSON format.")]
        [SwaggerResponse(200, "Personal data export.")]
        [SwaggerResponse(403, "Forbidden.")]
        [SwaggerResponse(404, "User not found.")]
        public async Task<IActionResult> ExportData(string id)
        {
            if (!User.IsCallerOf(id)) return Forbid();

            var profile = await _context.UserProfiles.FindAsync(id);
            if (profile == null)
                return NotFound(new { message = "User not found!" });

            var user = await _userManager.FindByIdAsync(id);

            var sensorCustomNames = await _context.UserSensorCustomNames
                .Where(x => x.UserId == id)
                .ToDictionaryAsync(x => x.SensorId, x => x.CustomName);

            var sensors = await _context.UserSensors
                .Where(us => us.UserId == id)
                .Join(_context.Sensors, us => us.SensorId, s => s.Id,
                    (us, s) => new { s.Id, s.DefaultName, s.Type })
                .ToListAsync();

            var switchCustomNames = await _context.UserSwitchCustomNames
                .Where(x => x.UserId == id)
                .ToDictionaryAsync(x => x.SwitchId, x => x.CustomName);

            // Direct ownership
            var directSwitchIds = await _context.UserSwitches
                .Where(us => us.UserId == id)
                .Select(us => us.SwitchId)
                .ToListAsync();

            // Sensor-derived access: sensors the user owns → ParentName → DiscoveredDevices → switch name
            var accessibleParentNames = await _context.UserSensors
                .Where(us => us.UserId == id)
                .Join(_context.Sensors, us => us.SensorId, s => s.Id, (us, s) => s.ParentName)
                .ToListAsync();

            var discoveredSwitchNames = await _context.DiscoveredDevices
                .Where(dd => accessibleParentNames.Contains(dd.DiscoveredBy))
                .Select(dd => dd.Target)
                .ToListAsync();

            var switches = await _context.Switches
                .Where(sw => directSwitchIds.Contains(sw.Id) || discoveredSwitchNames.Contains(sw.Name))
                .Select(sw => new { sw.Id, sw.Name, sw.Type })
                .ToListAsync();

            var sensorIds = sensors.Select(s => s.Id).ToList();
            var switchIds = switches.Select(sw => sw.Id).ToList();

            var sensorActivities = await _context.SensorActivities
                .Where(a => a.UserId == id)
                .OrderBy(a => a.ActivityDate)
                .Select(a => new { a.SensorId, a.Title, a.Notes, a.ActivityDate, a.CreatedAt, a.UpdatedAt })
                .ToListAsync();

            var sensorPhotos = await _context.SensorPhotos
                .Where(p => p.UserId == id)
                .Select(p => new { p.SensorId, p.ContentType, p.Data, p.CreatedAt })
                .ToListAsync();

            var sensorReadings = await _context.SensorData
                .Where(sd => sensorIds.Contains(sd.SensorId))
                .OrderBy(sd => sd.Timestamp)
                .Select(sd => new { sd.SensorId, sd.Value, sd.Timestamp })
                .ToListAsync();

            var switchHistory = await _context.SwitchData
                .Where(sd => switchIds.Contains(sd.SwitchId))
                .OrderBy(sd => sd.Timestamp)
                .Select(sd => new { sd.SwitchId, sd.Value, sd.Timestamp })
                .ToListAsync();

            var export = new
            {
                ExportedAt = DateTime.UtcNow,
                Account = new
                {
                    FirstName = user?.FirstName,
                    LastName = user?.LastName,
                    Email = user?.Email,
                    profile.PriceZone,
                    EmailConfirmed = user?.EmailConfirmed
                },
                Sensors = sensors.Select(s => new
                {
                    s.Id,
                    s.Type,
                    DefaultName = s.DefaultName,
                    CustomName = sensorCustomNames.TryGetValue(s.Id, out var cn) ? cn : null,
                    Readings = sensorReadings
                        .Where(r => r.SensorId == s.Id)
                        .Select(r => new { r.Value, r.Timestamp }),
                    Activities = sensorActivities
                        .Where(a => a.SensorId == s.Id)
                        .Select(a => new { a.Title, a.Notes, a.ActivityDate, a.CreatedAt, a.UpdatedAt }),
                    Photo = sensorPhotos
                        .Where(p => p.SensorId == s.Id)
                        .Select(p => new { p.ContentType, p.Data, p.CreatedAt })
                        .FirstOrDefault()
                }),
                Switches = switches.Select(sw => new
                {
                    sw.Id,
                    sw.Type,
                    DefaultName = sw.Name,
                    CustomName = switchCustomNames.TryGetValue(sw.Id, out var csn) ? csn : null,
                    History = switchHistory
                        .Where(h => h.SwitchId == sw.Id)
                        .Select(h => new { h.Value, h.Timestamp })
                })
            };

            _logger.LogInformation("Data exported for user {UserId}", LogSanitizer.Sanitize(id));
            return Ok(export);
        }

        /// <summary>
        /// Updates user preferences (e.g. price zone).
        /// </summary>
        [HttpPut("{id}/preferences")]
        [SwaggerOperation(Summary = "Updates user preferences.")]
        [SwaggerResponse(200, "Preferences updated.", typeof(UserProfileDto))]
        [SwaggerResponse(403, "Forbidden.")]
        [SwaggerResponse(404, "User profile not found.")]
        public async Task<IActionResult> UpdatePreferences(string id, [FromBody] UpdateUserPreferencesDto dto)
        {
            if (!User.IsCallerOf(id)) return Forbid();

            var userProfile = await _context.UserProfiles.SingleOrDefaultAsync(up => up.Id == id);
            if (userProfile == null)
                return NotFound(new { message = "User profile not found!" });

            userProfile.PriceZone = dto.PriceZone;
            if (dto.PushNotificationsEnabled.HasValue)
                userProfile.PushNotificationsEnabled = dto.PushNotificationsEnabled.Value;
            if (dto.OfflineAlertThresholdHours.HasValue)
                userProfile.OfflineAlertThresholdHours = dto.OfflineAlertThresholdHours.Value;
            await _context.SaveChangesAsync();

            var user = await _userManager.FindByIdAsync(id);
            var response = _mapper.Map<UserProfileDto>(userProfile);
            response.EmailConfirmed = user?.EmailConfirmed ?? false;
            response.PriceZone = userProfile.PriceZone;

            return Ok(response);
        }

        /// <summary>
        /// Updates first name, last name, phone (GDPR Art. 16 rectification).
        /// </summary>
        [HttpPut("{id}/profile")]
        [SwaggerOperation(Summary = "Updates the caller's first name, last name, phone.")]
        [SwaggerResponse(204, "Profile updated.")]
        [SwaggerResponse(403, "Forbidden.")]
        [SwaggerResponse(404, "User not found.")]
        public async Task<IActionResult> UpdateProfile(string id, [FromBody] UpdateProfileDto dto)
        {
            if (!User.IsCallerOf(id)) return Forbid();

            var user = await _userManager.FindByIdAsync(id);
            if (user == null || user.IsDeleted)
                return NotFound(new { message = "User not found!" });

            user.FirstName = dto.FirstName;
            user.LastName = dto.LastName;
            user.PhoneNumber = dto.PhoneNumber;

            var result = await _userManager.UpdateAsync(user);
            if (!result.Succeeded)
            {
                _logger.LogWarning("UpdateProfile failed for {UserId}: {Errors}", LogSanitizer.Sanitize(id), result.Errors);
                return BadRequest(result.Errors);
            }

            _logger.LogInformation("Profile updated for user {UserId}", LogSanitizer.Sanitize(id));
            return NoContent();
        }
    }
}
