using System.Net;
using garge_api.Models;
using garge_api.Models.Subscription;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    /// <summary>Stops the Vipps agreements of subscriptions marked with <see cref="Subscription.AgreementStopPending"/>.</summary>
    public static class SubscriptionAgreementStops
    {
        /// <summary>
        /// Stops each agreement and clears the mark. When Vipps refuses the stop, the mark is cleared only if
        /// the agreement is unknown, stopped or expired there. A pending agreement can still be accepted, so it
        /// keeps the mark until it expires. Any other failure keeps the mark, and the charge scheduler tries
        /// again with its next run.
        /// </summary>
        public static async Task StopAsync(ApplicationDbContext db, IVippsService vipps, VippsSettings settings,
            IEnumerable<Subscription> subscriptions, ILogger logger, CancellationToken ct)
        {
            foreach (var sub in subscriptions)
            {
                if (!sub.AgreementStopPending || !settings.IsConfigured(sub.IsTest)) continue;
                // An agreement that was never created has nothing to stop. Activating one marks it again.
                if (!string.IsNullOrEmpty(sub.VippsAgreementId) && !await StopOneAsync(vipps, sub, logger)) continue;
                sub.AgreementStopPending = false;
                await db.SaveChangesAsync(ct);
            }
        }

        // True when the agreement is stopped, or ended in Vipps and can never be charged.
        private static async Task<bool> StopOneAsync(IVippsService vipps, Subscription sub, ILogger logger)
        {
            try
            {
                await vipps.CancelAgreementAsync(sub.VippsAgreementId, StopKey(sub), sub.IsTest);
                logger.LogInformation("Stopped the Vipps agreement of subscription {SubscriptionId}", sub.Id);
                return true;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogWarning(ex, "Vipps does not know the agreement of subscription {SubscriptionId}", sub.Id);
                return true;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
            {
                try
                {
                    var status = (await vipps.GetAgreementAsync(sub.VippsAgreementId, sub.IsTest)).Status;
                    if (status is "STOPPED" or "EXPIRED")
                    {
                        logger.LogInformation("The Vipps agreement of subscription {SubscriptionId} is already {Status}", sub.Id, status);
                        return true;
                    }
                    logger.LogWarning(ex, "Vipps refused to stop the {Status} agreement of subscription {SubscriptionId}, the charge scheduler tries again", status, sub.Id);
                }
                catch (Exception readEx)
                {
                    logger.LogError(readEx, "Reading the Vipps agreement of subscription {SubscriptionId} failed, the charge scheduler tries again", sub.Id);
                }
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Stopping the Vipps agreement of subscription {SubscriptionId} failed, the charge scheduler tries again", sub.Id);
                return false;
            }
        }

        // Stopping is idempotent in Vipps, so each try gets its own key. Vipps answers a reused key with its first
        // answer, which would repeat a refusal an earlier try got.
        internal static string StopKey(Subscription sub) => $"stop-{sub.Id}-{DateTime.UtcNow.Ticks}";

        /// <summary>The subscriptions still waiting for their agreement to be stopped.</summary>
        public static Task<List<Subscription>> PendingAsync(ApplicationDbContext db, CancellationToken ct, string? userId = null) =>
            db.Subscriptions
                .Where(s => s.AgreementStopPending && (userId == null || s.UserId == userId))
                .ToListAsync(ct);
    }
}
