using System.Net;
using garge_api.Models;
using garge_api.Models.Subscription;
using Microsoft.EntityFrameworkCore;
using SjolystInnovation.AppKit.Vipps;

namespace garge_api.Services
{
    /// <summary>Stops Vipps agreements, including those of subscriptions marked with <see cref="Subscription.AgreementStopRequestedAt"/>.</summary>
    public static class SubscriptionAgreementStops
    {
        /// <summary>How long a mark keeps a deleted account from being purged when its agreement cannot be stopped.</summary>
        public static readonly TimeSpan MarkHoldsPurge = TimeSpan.FromDays(30);

        /// <summary>How long a stop may keep failing before the admins are told.</summary>
        public static readonly TimeSpan StopFailingAlertAfter = TimeSpan.FromDays(3);

        /// <summary>
        /// Stops each marked agreement and clears the mark. When Vipps refuses the stop, the mark is cleared only if
        /// the agreement is unknown, stopped or expired there. A pending agreement can still be accepted, so it
        /// keeps the mark until it expires. Any other failure keeps the mark, and the charge scheduler tries
        /// again with its next run. A subscription in an environment without credentials is left as it is.
        /// </summary>
        public static async Task StopAsync(ApplicationDbContext db, IVippsService vipps, VippsSettings settings,
            IEnumerable<Subscription> subscriptions, ILogger logger, CancellationToken ct)
        {
            foreach (var sub in subscriptions)
            {
                if (sub.AgreementStopRequestedAt == null || !settings.IsConfigured(sub.IsTest)) continue;
                // An agreement that was never created has nothing to stop. Activating one marks it again.
                if (!string.IsNullOrEmpty(sub.VippsAgreementId) && !await StopOneAsync(vipps, sub, logger)) continue;
                sub.AgreementStopRequestedAt = null;
                await db.SaveChangesAsync(ct);
            }
        }

        /// <summary>
        /// Stops the agreement of one subscription. True when it is stopped, or has ended in Vipps and can never be
        /// charged. False when it may still be active, so the caller tries again later.
        /// </summary>
        public static async Task<bool> StopOneAsync(IVippsService vipps, Subscription sub, ILogger logger)
        {
            try
            {
                await vipps.CancelAgreementAsync(sub.VippsAgreementId, StopKey(sub), sub.IsTest);
                logger.LogInformation("Stopped the Vipps agreement of subscription {SubscriptionId}", sub.Id);
                return true;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound && ex is not VippsAccessTokenException)
            {
                // A 400 can also come from the request, so the agreement itself decides.
                var ended = await EndedStatusAsync(vipps, sub, logger);
                if (ended != null)
                {
                    logger.LogInformation("The Vipps agreement of subscription {SubscriptionId} is {Status}", sub.Id, ended);
                    return true;
                }
                logger.LogWarning(ex, "Vipps refused to stop the agreement of subscription {SubscriptionId}, it is tried again", sub.Id);
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Stopping the Vipps agreement of subscription {SubscriptionId} failed, it is tried again", sub.Id);
                return false;
            }
        }

        /// <summary>
        /// STOPPED, EXPIRED or UNKNOWN when Vipps says the agreement has ended or does not exist, so it can never be
        /// charged. Null when it is active or pending, or when it cannot be read, for example because the access
        /// token fails.
        /// </summary>
        public static async Task<string?> EndedStatusAsync(IVippsService vipps, Subscription sub, ILogger logger)
        {
            try
            {
                var status = (await vipps.GetAgreementAsync(sub.VippsAgreementId, sub.IsTest))?.Status;
                return status is "STOPPED" or "EXPIRED" ? status : null;
            }
            // Only a 404 from the agreement call itself. One from the access token says nothing about the agreement.
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound && ex is not VippsAccessTokenException)
            {
                return "UNKNOWN";
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reading the Vipps agreement of subscription {SubscriptionId} failed", sub.Id);
                return null;
            }
        }

        // Stopping is idempotent in Vipps, so each try gets its own key. Vipps answers a reused key with its first
        // answer, which would repeat a refusal an earlier try got.
        internal static string StopKey(Subscription sub) => $"stop-{sub.Id}-{DateTime.UtcNow.Ticks}";

        /// <summary>The subscriptions still waiting for their agreement to be stopped.</summary>
        public static Task<List<Subscription>> PendingAsync(ApplicationDbContext db, CancellationToken ct, string? userId = null) =>
            db.Subscriptions
                .Where(s => s.AgreementStopRequestedAt != null && (userId == null || s.UserId == userId))
                .ToListAsync(ct);
    }
}
