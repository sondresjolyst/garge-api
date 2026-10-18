using garge_api.Models;
using garge_api.Models.Shop;
using garge_api.Models.Subscription;
using garge_api.Dtos.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SjolystInnovation.AppKit.Auth;
using Swashbuckle.AspNetCore.Annotations;
using MapsterMapper;
using garge_api.Models.Admin;
using garge_api.Services;
using garge_api.Helpers;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace garge_api.Controllers
{
    [ApiController]
    [Authorize(Policy = "Admin")]
    public class AdminController : ControllerBase
    {
        private readonly UserManager<User> _userManager;
        private readonly ApplicationDbContext _context;
        private readonly ILogger<AdminController> _logger;
        private readonly IMapper _mapper;
        private readonly Services.IAppSettingsCache? _settingsCache;
        private readonly Services.ISecurityModeService? _security;

        public AdminController(
            UserManager<User> userManager,
            ApplicationDbContext context,
            ILogger<AdminController> logger,
            IMapper mapper,
            Services.IAppSettingsCache? settingsCache = null,
            Services.ISecurityModeService? security = null)
        {
            _userManager = userManager;
            _context = context;
            _logger = logger;
            _mapper = mapper;
            _settingsCache = settingsCache;
            _security = security;
        }

        /// <summary>
        /// Gets aggregate platform stats.
        /// </summary>
        [HttpGet("/api/admin/stats")]
        [SwaggerOperation(Summary = "Gets aggregate platform stats.")]
        [SwaggerResponse(200, "Stats retrieved successfully.", typeof(AdminStatsDto))]
        public async Task<IActionResult> GetStats([FromQuery] bool test = false)
        {
            _logger.LogInformation("GetStats called by {@LogData}", new { CallerUserId = User.GetUserId(), Test = test });

            var now = DateTime.UtcNow;
            var today = now.Date;
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            // Monday-start ISO week.
            var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));

            var totalUsers = await _userManager.Users.CountAsync(u => !u.IsDeleted);
            var totalSensors = await _context.Sensors.CountAsync();
            var totalSwitches = await _context.Switches.CountAsync();
            var activeAutomations = await _context.AutomationRules.CountAsync(r => r.IsEnabled);

            var orderQuery = _context.Orders.Where(o => o.IsTest == test);
            var subQuery = _context.Subscriptions.Where(s => s.IsTest == test);

            var orders = new AdminOrderStatsDto
            {
                Today = await orderQuery.CountAsync(o => o.CreatedAt >= today),
                ThisWeek = await orderQuery.CountAsync(o => o.CreatedAt >= weekStart),
                ThisMonth = await orderQuery.CountAsync(o => o.CreatedAt >= monthStart),
                PendingCapture = await orderQuery.CountAsync(o => o.Status == OrderStatus.Reserved),
                FailedOrCancelled = await orderQuery.CountAsync(o =>
                    o.Status == OrderStatus.Failed || o.Status == OrderStatus.Cancelled),
                TotalRevenueInOre = await orderQuery
                    .Where(o => o.Status == OrderStatus.Paid)
                    .SumAsync(o => (long)o.TotalInOre),
                MonthRevenueInOre = await orderQuery
                    .Where(o => o.Status == OrderStatus.Paid && o.CreatedAt >= monthStart)
                    .SumAsync(o => (long)o.TotalInOre),
            };

            var subscriptions = new AdminSubscriptionStatsDto
            {
                Active = await subQuery.CountAsync(s => s.Status == SubscriptionStatus.Active),
                PendingConfirm = await subQuery.CountAsync(s => s.Status == SubscriptionStatus.Pending),
                StoppedThisMonth = await subQuery.CountAsync(s =>
                    s.Status == SubscriptionStatus.Stopped && s.UpdatedAt >= monthStart),
            };

            var activeWithProduct = await subQuery
                .Where(s => s.Status == SubscriptionStatus.Active)
                .Include(s => s.Product)
                .Where(s => s.Product != null)
                .Select(s => new { s.UnitPriceInOre, s.Quantity, s.Product!.Interval })
                .ToListAsync();
            subscriptions.MonthlyRecurringInOre = activeWithProduct.Sum(p =>
            {
                var total = (long)p.UnitPriceInOre * p.Quantity;
                return p.Interval == BillingInterval.Yearly ? total / 12 : total;
            });

            var stats = new AdminStatsDto
            {
                TotalUsers = totalUsers,
                TotalSensors = totalSensors,
                TotalSwitches = totalSwitches,
                ActiveAutomations = activeAutomations,
                Orders = orders,
                Subscriptions = subscriptions,
            };
            return Ok(stats);
        }

        /// <summary>
        /// Gets all discovered MQTT devices.
        /// </summary>
        [HttpGet("/api/admin/devices")]
        [SwaggerOperation(Summary = "Gets all discovered MQTT devices.")]
        public async Task<IActionResult> GetDevices()
        {
            _logger.LogInformation("GetDevices called by {@LogData}", new { CallerUserId = User.GetUserId() });

            var devices = await _context.DiscoveredDevices
                .OrderByDescending(d => d.Timestamp)
                .ToListAsync();
            return Ok(devices);
        }

        /// <summary>
        /// Deletes a user's account. The row stays, scrubbed, so their orders and invoices keep it for the
        /// bookkeeping retention period.
        /// </summary>
        /// <param name="id">The ID of the user to delete.</param>
        /// <param name="deleter">The shared account deleter.</param>
        /// <returns>No content.</returns>
        [HttpDelete("/api/users/{id}")]
        [SwaggerOperation(Summary = "Deletes a user's account.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(404, "User not found.")]
        [SwaggerResponse(409, "The last admin cannot be deleted.")]
        public async Task<IActionResult> DeleteUser(string id, [FromServices] IAccountDeleter<User> deleter)
        {
            _logger.LogInformation("DeleteUser called for {@LogData}", new { id, CallerUserId = User.GetUserId() });

            var user = await _userManager.FindByIdAsync(id);
            if (user == null || user.IsDeleted)
            {
                _logger.LogWarning("DeleteUser failed: User not found {@LogData}", new { id });
                return NotFound(new { message = "User not found!" });
            }

            try
            {
                await deleter.DeleteAsync(user);
            }
            catch (Exception e) when (e is LastAdminException or AccountChangeConflictException)
            {
                return Conflict(AccountDeletionConflict.Body(e));
            }

            _logger.LogInformation("User deleted: {@LogData}", new { id, CallerUserId = User.GetUserId() });
            return NoContent();
        }
        /// <summary>
        /// Gets the Garge Security alert threshold. Admin only, deliberately off the public settings payload.
        /// </summary>
        [HttpGet("/api/admin/security-settings")]
        [SwaggerOperation(Summary = "Gets Garge Security settings.")]
        [SwaggerResponse(200, "Settings retrieved.", typeof(SecuritySettingsDto))]
        public async Task<IActionResult> GetSecuritySettings()
        {
            var settings = await _context.AppSettings.FindAsync(1) ?? new AppSettings();
            return Ok(new SecuritySettingsDto { AlertThresholdMinutes = settings.SecurityAlertThresholdMinutes });
        }

        /// <summary>
        /// Updates the Garge Security alert threshold. Applies to every armed sensor on the next detector pass.
        /// </summary>
        [HttpPut("/api/admin/security-settings")]
        [SwaggerOperation(Summary = "Updates Garge Security settings.")]
        [SwaggerResponse(200, "Settings updated.", typeof(SecuritySettingsDto))]
        [SwaggerResponse(400, "Threshold out of range.")]
        public async Task<IActionResult> UpdateSecuritySettings([FromBody] UpdateSecuritySettingsDto dto)
        {
            _logger.LogInformation("UpdateSecuritySettings called by {@LogData}", new { CallerUserId = User.GetUserId(), dto.AlertThresholdMinutes });

            if (dto.AlertThresholdMinutes < Constants.SecurityMode.MinThresholdMinutes
                || dto.AlertThresholdMinutes > Constants.SecurityMode.MaxThresholdMinutes)
            {
                return BadRequest(new
                {
                    code = Constants.SecurityMode.ErrorCodes.InvalidThreshold,
                    message = $"The alert threshold must be between {Constants.SecurityMode.MinThresholdMinutes} and {Constants.SecurityMode.MaxThresholdMinutes} minutes."
                });
            }

            var settings = await _context.AppSettings.FindAsync(1);
            if (settings == null)
            {
                settings = new AppSettings { Id = 1 };
                _context.AppSettings.Add(settings);
            }

            settings.SecurityAlertThresholdMinutes = dto.AlertThresholdMinutes;
            await _context.SaveChangesAsync();
            _settingsCache?.Invalidate();

            return Ok(new SecuritySettingsDto { AlertThresholdMinutes = settings.SecurityAlertThresholdMinutes });
        }

        /// <summary>
        /// Gets garge's own app-wide settings. Public, because the frontend reads it without signing in.
        /// The company details are served by the site settings package at <c>/api/company</c>.
        /// </summary>
        [HttpGet("/api/admin/settings")]
        [AllowAnonymous]
        [SwaggerOperation(Summary = "Gets app-wide settings.")]
        [SwaggerResponse(200, "Settings retrieved.", typeof(AppSettingsDto))]
        public async Task<IActionResult> GetAppSettings()
        {
            var settings = await _context.AppSettings.FindAsync(1) ?? new AppSettings();
            return Ok(_mapper.Map<AppSettingsDto>(settings));
        }

        /// <summary>
        /// Updates garge's own app-wide settings. The company details are saved through <c>PUT /api/settings</c>.
        /// </summary>
        [HttpPut("/api/admin/settings")]
        [SwaggerOperation(Summary = "Updates app-wide settings.")]
        [SwaggerResponse(200, "Settings updated.", typeof(AppSettingsDto))]
        public async Task<IActionResult> UpdateAppSettings([FromBody] UpdateAppSettingsDto dto)
        {
            _logger.LogInformation("UpdateAppSettings called by {@LogData}", new { CallerUserId = User.GetUserId() });

            var settings = await _context.AppSettings.FindAsync(1);
            if (settings == null)
            {
                settings = new AppSettings { Id = 1 };
                _context.AppSettings.Add(settings);
            }

            _mapper.Map(dto, settings);
            // The registration date and the switch move together. A date turns VAT on, turning VAT on
            // without one starts it now, and turning it off clears the date.
            if (dto.VatRegisteredFrom is { } from)
            {
                settings.VatEnabled = true;
                // A plain date, as the admin page sends it, means midnight in Norway.
                settings.VatRegisteredFrom = from.Kind == DateTimeKind.Utc ? from : LocalTime.ToUtc(from);
            }
            else
                settings.SyncVatRegisteredFrom(DateTime.UtcNow);
            await _context.SaveChangesAsync();
            _settingsCache?.Invalidate();

            _logger.LogInformation("AppSettings updated: {@LogData}", new { dto.CookieBannerEnabled, CallerUserId = User.GetUserId() });
            return Ok(_mapper.Map<AppSettingsDto>(settings));
        }
    }
}
