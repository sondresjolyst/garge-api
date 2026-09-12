using garge_api.Models;
using garge_api.Models.Admin;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    /// <summary>
    /// Sends a Garge Security message to a user: web push when enabled, and email when enabled or when
    /// push did not deliver. Returns true when at least one channel delivered.
    /// </summary>
    public interface ISecurityNotifier
    {
        Task<bool> NotifyUserAsync(string userId, string title, string message, string? tag = null, CancellationToken ct = default);
    }

    public class SecurityNotifier(
        ApplicationDbContext db,
        IWebPushService push,
        IEmailService email,
        ILogger<SecurityNotifier> logger) : ISecurityNotifier
    {
        /// <summary>
        /// True when the user can be reached: email enabled, or push enabled with at least one subscription.
        /// The push flag alone is not enough — turning push off in the app deletes the subscription, not the flag.
        /// </summary>
        public static async Task<bool> HasAlertChannelAsync(ApplicationDbContext db, string userId, bool pushEnabled, bool emailEnabled, CancellationToken ct = default)
            => emailEnabled || (pushEnabled && await db.PushSubscriptions.AnyAsync(s => s.UserId == userId, ct));

        public async Task<bool> NotifyUserAsync(string userId, string title, string message, string? tag = null, CancellationToken ct = default)
        {
            var profile = await db.UserProfiles
                .Include(p => p.User)
                .FirstOrDefaultAsync(p => p.Id == userId, ct);
            if (profile == null) return false;

            var pushDelivered = false;
            if (profile.PushNotificationsEnabled)
            {
                try
                {
                    pushDelivered = await push.SendAsync(userId, title, message, tag, ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Security push failed for user {UserId}", userId);
                }
            }

            var emailDelivered = false;
            if ((profile.EmailNotificationsEnabled || !pushDelivered) && !string.IsNullOrWhiteSpace(profile.User.Email))
            {
                try
                {
                    var settings = await db.AppSettings.FindAsync([1], ct) ?? new AppSettings();
                    var html = SecurityEmailTemplates.Alert(settings, profile.User.FirstName, title, message);
                    await email.SendEmailAsync(profile.User.Email, title, html);
                    emailDelivered = true;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Security email failed for user {UserId}", userId);
                }
            }

            return pushDelivered || emailDelivered;
        }
    }
}
