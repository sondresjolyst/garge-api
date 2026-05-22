using MapsterMapper;
using garge_api.Dtos.User;
using garge_api.Helpers;
using garge_api.Hubs;
using garge_api.Models;
using garge_api.Models.Auth;
using garge_api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
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
        private readonly IDeviceOwnershipService _ownership;
        private readonly IHubConnectionTracker _hubConnections;
        private readonly IHubContext<DeviceHub> _hub;
        private readonly IAnonymizationService _anonymizer;

        public UserController(
            ApplicationDbContext context,
            UserManager<User> userManager,
            IMapper mapper,
            ILogger<UserController> logger,
            IDeviceOwnershipService ownership,
            IHubConnectionTracker hubConnections,
            IHubContext<DeviceHub> hub,
            IAnonymizationService anonymizer)
        {
            _context = context;
            _userManager = userManager;
            _mapper = mapper;
            _logger = logger;
            _ownership = ownership;
            _hubConnections = hubConnections;
            _hub = hub;
            _anonymizer = anonymizer;
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
            // Bookkeeping Act (bokføringsloven §13). Anonymizing telemetry is irreversible
            // and AnonymizeUserTelemetryAsync commits as it goes, so the whole sequence runs
            // in one transaction: if the Identity update fails (e.g. validation), the
            // anonymization and row removals roll back rather than leaving a half-deleted user.
            await using var tx = await _context.Database.BeginTransactionAsync();

            await AnonymizeUserTelemetryAsync(id);
            ClearUserOwnedRows(id);
            await ScrubUserPiiAsync(user);

            var result = await _userManager.UpdateAsync(user);
            if (!result.Succeeded)
            {
                _logger.LogError("DeleteOwnAccount failed for {UserId}: {Errors}", LogSanitizer.Sanitize(id), result.Errors);
                return BadRequest(result.Errors);
            }

            await _context.SaveChangesAsync();
            await tx.CommitAsync();

            await ForceDisconnectHubAsync(id);

            _logger.LogInformation("Account soft-deleted by user {UserId}", LogSanitizer.Sanitize(id));
            return NoContent();
        }

        /// <summary>
        /// Removes all per-user rows that should not survive soft-delete (custom
        /// names, refresh tokens, push subs, etc.). Add new user-owned tables
        /// here when introduced.
        /// </summary>
        private void ClearUserOwnedRows(string userId)
        {
            // Capture associated device ids before removing the rows so we
            // can invalidate the ownership cache once the changes commit.
            var sensorIds = _context.UserSensors.Where(us => us.UserId == userId).Select(us => us.SensorId).ToList();
            var switchIds = _context.UserSwitches.Where(us => us.UserId == userId).Select(us => us.SwitchId).ToList();

            _context.RefreshTokens.RemoveRange(_context.RefreshTokens.Where(t => t.UserId == userId));
            _context.UserSensorCustomNames.RemoveRange(_context.UserSensorCustomNames.Where(x => x.UserId == userId));
            _context.UserSwitchCustomNames.RemoveRange(_context.UserSwitchCustomNames.Where(x => x.UserId == userId));
            _context.SensorActivities.RemoveRange(_context.SensorActivities.Where(a => a.UserId == userId));
            _context.SensorPhotos.RemoveRange(_context.SensorPhotos.Where(p => p.UserId == userId));
            _context.PushSubscriptions.RemoveRange(_context.PushSubscriptions.Where(s => s.UserId == userId));
            _context.SensorOfflineNotifications.RemoveRange(_context.SensorOfflineNotifications.Where(n => n.UserId == userId));
            _context.UserSensors.RemoveRange(_context.UserSensors.Where(us => us.UserId == userId));
            _context.UserSwitches.RemoveRange(_context.UserSwitches.Where(us => us.UserId == userId));

            foreach (var sid in sensorIds) _ownership.InvalidateSensor(sid);
            foreach (var sid in switchIds) _ownership.InvalidateSwitch(sid);
        }

        /// <summary>
        /// Moves the user's exclusive telemetry into the anonymized ML store before their ownership
        /// rows are removed. This is the GDPR-erasure path: the readings leave personal scope (no link
        /// back to the user/device) while co-owned ranges are preserved for the other owner. Each call
        /// consumes (deletes) the ownership period it processes. Run before <see cref="ClearUserOwnedRows"/>.
        /// </summary>
        private async Task AnonymizeUserTelemetryAsync(string userId)
        {
            var sensorPeriodIds = await _context.SensorOwnershipPeriods
                .Where(p => p.UserId == userId).Select(p => p.Id).ToListAsync();
            foreach (var periodId in sensorPeriodIds)
                await _anonymizer.AnonymizeSensorPeriodAsync(periodId);

            var switchPeriodIds = await _context.SwitchOwnershipPeriods
                .Where(p => p.UserId == userId).Select(p => p.Id).ToListAsync();
            foreach (var periodId in switchPeriodIds)
                await _anonymizer.AnonymizeSwitchPeriodAsync(periodId);
        }

        /// <summary>
        /// Tells any open SignalR connections for this user to disconnect, then
        /// closes the server-side handles so further events do not fan out.
        /// Called from soft-delete after PII scrub commits.
        /// </summary>
        private async Task ForceDisconnectHubAsync(string userId)
        {
            var connectionIds = _hubConnections.GetConnectionIds(userId);
            if (connectionIds.Count == 0) return;

            try
            {
                // Signal the client to stop the connection and sign out.
                await _hub.Clients.Clients(connectionIds).SendAsync("forceLogout");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ForceDisconnectHubAsync: send forceLogout failed for user {UserId}", LogSanitizer.Sanitize(userId));
            }
        }

        /// <summary>
        /// Sets the User row's PII fields to safe defaults and locks the account.
        /// Caller is responsible for committing the row via UpdateAsync + SaveChanges.
        /// </summary>
        private async Task ScrubUserPiiAsync(User user)
        {
            user.FirstName = "Deleted";
            user.LastName = "User";
            // A valid, unique placeholder — not null. Identity is configured with
            // RequireUniqueEmail, so UpdateAsync rejects a null/empty email (this previously
            // made account deletion fail with InvalidEmail). The .invalid TLD (RFC 2606) is
            // reserved and never routable, so this address carries no recoverable PII.
            user.Email = $"deleted-{user.Id}@deleted.invalid";
            user.NormalizedEmail = user.Email.ToUpperInvariant();
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

            // Bound to the user's own ownership window(s): they export the data from periods they
            // owned, not a previous owner's readings on a re-claimed device. Same boundary as the read
            // endpoints — see OwnershipWindowQueryExtensions.
            var sensorReadings = await _context.SensorData
                .Where(sd => sensorIds.Contains(sd.SensorId))
                .WithinSensorOwnership(_context, id)
                .OrderBy(sd => sd.Timestamp)
                .Select(sd => new { sd.SensorId, sd.Value, sd.Timestamp })
                .ToListAsync();

            var switchHistory = await _context.SwitchData
                .Where(sd => switchIds.Contains(sd.SwitchId))
                .WithinSwitchOwnership(_context, id)
                .OrderBy(sd => sd.Timestamp)
                .Select(sd => new { sd.SwitchId, sd.Value, sd.Timestamp })
                .ToListAsync();

            var batteryHealth = await _context.BatteryHealthData
                .Where(bh => sensorIds.Contains(bh.SensorId))
                .WithinSensorOwnership(_context, id)
                .OrderBy(bh => bh.Timestamp)
                .ToListAsync();

            var automationRules = await _context.AutomationRules
                .Where(r => sensorIds.Contains(r.SensorId)
                            || (r.TargetType == "socket" && switchIds.Contains(r.TargetId)))
                .ToListAsync();

            var groups = await _context.Groups
                .Where(g => g.UserId == id)
                .Include(g => g.GroupSensors)
                .Include(g => g.GroupSwitches)
                .ToListAsync();

            var subscriptions = await _context.Subscriptions
                .Include(s => s.Product)
                .Where(s => s.UserId == id)
                .OrderBy(s => s.CreatedAt)
                .ToListAsync();

            var orders = await _context.Orders
                .Include(o => o.OrderItems).ThenInclude(oi => oi.ShopItem)
                .Where(o => o.UserId == id)
                .OrderBy(o => o.CreatedAt)
                .ToListAsync();

            var orderIds = orders.Select(o => o.Id).ToList();
            var subscriptionIds = subscriptions.Select(s => s.Id).ToList();

            var invoices = await _context.Invoices
                .Where(i => (i.OrderId != null && orderIds.Contains(i.OrderId.Value))
                            || (i.SubscriptionId != null && subscriptionIds.Contains(i.SubscriptionId.Value)))
                .OrderBy(i => i.IssuedAt)
                .Select(i => new { i.Id, i.OrderId, i.SubscriptionId, i.VippsChargeId, i.AmountInOre, i.IssuedAt })
                .ToListAsync();

            var pushSubscriptions = await _context.PushSubscriptions
                .Where(p => p.UserId == id)
                .Select(p => new { p.Endpoint, p.CreatedAt })
                .ToListAsync();

            var offlineNotifications = await _context.SensorOfflineNotifications
                .Where(n => n.UserId == id)
                .OrderBy(n => n.NotifiedAt)
                .Select(n => new { n.SensorId, n.NotifiedAt, n.ResolvedAt })
                .ToListAsync();

            var export = new
            {
                ExportedAt = DateTime.UtcNow,
                Account = new
                {
                    FirstName = user?.FirstName,
                    LastName = user?.LastName,
                    Email = user?.Email,
                    EmailConfirmed = user?.EmailConfirmed,
                    PhoneNumber = user?.PhoneNumber,
                    CreatedAt = user?.CreatedAt,
                    TermsAcceptedAt = user?.TermsAcceptedAt,
                    TermsVersion = user?.TermsVersion,
                    TermsAcceptedIp = user?.TermsAcceptedIp,
                    profile.PriceZone,
                    profile.PushNotificationsEnabled,
                    profile.OfflineAlertThresholdHours
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
                        .FirstOrDefault(),
                    BatteryHealth = batteryHealth
                        .Where(bh => bh.SensorId == s.Id)
                        .Select(bh => new { bh.Status, bh.Timestamp })
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
                }),
                Groups = groups.Select(g => new
                {
                    g.Id,
                    g.Name,
                    g.Icon,
                    SensorIds = g.GroupSensors.Select(gs => gs.SensorId),
                    SwitchIds = g.GroupSwitches.Select(gsw => gsw.SwitchId)
                }),
                AutomationRules = automationRules.Select(r => new
                {
                    r.Id,
                    r.TargetType,
                    r.TargetId,
                    r.SensorType,
                    r.SensorId,
                    r.Condition,
                    r.Threshold,
                    r.Action,
                    r.IsEnabled,
                    r.LastTriggeredAt,
                    r.ElectricityPriceCondition,
                    r.ElectricityPriceThreshold,
                    r.ElectricityPriceArea,
                    r.ElectricityPriceOperator,
                    r.TimerDurationHours,
                    r.TimerActivatedAt,
                    r.CreatedAt
                }),
                Subscriptions = subscriptions.Select(s => new
                {
                    s.Id,
                    ProductName = s.Product?.Name,
                    ProductType = s.Product?.Type.ToString(),
                    s.Quantity,
                    Status = s.Status.ToString(),
                    s.StartDate,
                    s.NextChargeDate,
                    s.ConsentAcceptedAt,
                    s.ConsentIp,
                    s.BillingAddress,
                    s.IsTest,
                    s.CreatedAt,
                    s.UpdatedAt
                }),
                Orders = orders.Select(o => new
                {
                    o.Id,
                    Status = o.Status.ToString(),
                    o.TotalInOre,
                    o.ShippingAddress,
                    o.ShippedAt,
                    o.IsTest,
                    o.CreatedAt,
                    o.UpdatedAt,
                    Items = o.OrderItems.Select(oi => new
                    {
                        ShopItemName = oi.ShopItem != null ? oi.ShopItem.Name : null,
                        oi.Quantity,
                        oi.PriceAtPurchaseInOre
                    })
                }),
                Invoices = invoices,
                PushSubscriptions = pushSubscriptions,
                OfflineNotifications = offlineNotifications
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

        /// <summary>
        /// Gets the caller's sensor-data retention preference.
        /// </summary>
        [HttpGet("{id}/data-retention")]
        [SwaggerOperation(Summary = "Gets the caller's sensor-data retention opt-out state.")]
        [SwaggerResponse(200, "Retention preference.", typeof(DataRetentionDto))]
        [SwaggerResponse(403, "Forbidden.")]
        [SwaggerResponse(404, "User not found.")]
        public async Task<IActionResult> GetDataRetention(string id)
        {
            if (!User.IsCallerOf(id)) return Forbid();

            var user = await _userManager.FindByIdAsync(id);
            if (user == null || user.IsDeleted)
                return NotFound(new { message = "User not found!" });

            return Ok(DataRetentionDto.From(user.DataRetentionOptOutAt));
        }

        /// <summary>
        /// Sets or clears the caller's sensor-data retention opt-out (GDPR Art. 21 objection). When
        /// opted out, suspended sensors are purged after the 6-month cap once the user has no
        /// subscription coverage; the default keeps history for the lifetime of the claim.
        /// </summary>
        [HttpPut("{id}/data-retention")]
        [SwaggerOperation(Summary = "Sets or clears the caller's sensor-data retention opt-out.")]
        [SwaggerResponse(200, "Updated retention preference.", typeof(DataRetentionDto))]
        [SwaggerResponse(403, "Forbidden.")]
        [SwaggerResponse(404, "User not found.")]
        public async Task<IActionResult> UpdateDataRetention(string id, [FromBody] UpdateDataRetentionDto dto)
        {
            if (!User.IsCallerOf(id)) return Forbid();

            var user = await _userManager.FindByIdAsync(id);
            if (user == null || user.IsDeleted)
                return NotFound(new { message = "User not found!" });

            // Idempotent: only stamp when the state actually changes so the original opt-out time is kept.
            if (dto.OptOut && user.DataRetentionOptOutAt == null)
                user.DataRetentionOptOutAt = DateTime.UtcNow;
            else if (!dto.OptOut)
                user.DataRetentionOptOutAt = null;

            var result = await _userManager.UpdateAsync(user);
            if (!result.Succeeded)
            {
                _logger.LogWarning("UpdateDataRetention failed for {UserId}: {Errors}", LogSanitizer.Sanitize(id), result.Errors);
                return BadRequest(result.Errors);
            }

            _logger.LogInformation("Data-retention opt-out set to {OptOut} for user {UserId}", dto.OptOut, LogSanitizer.Sanitize(id));
            return Ok(DataRetentionDto.From(user.DataRetentionOptOutAt));
        }
    }
}
