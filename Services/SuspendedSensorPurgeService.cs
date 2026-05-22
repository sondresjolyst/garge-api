using garge_api.Models;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    /// <summary>
    /// Enforces the retention cap on suspended sensors of users who have opted out of long-term
    /// retention. By default Garge keeps a claimed sensor's history for the lifetime of the claim
    /// (legitimate interest, disclosed in the privacy policy) so a returning seasonal subscriber keeps
    /// their year-over-year data. A user can object (GDPR Art. 21) via the data-retention opt-out; once
    /// such a user has no subscription coverage, a sensor they have kept suspended for more than
    /// 6 months is force-unclaimed and its telemetry is moved into the anonymized ML store (or deleted
    /// if nothing is exclusive). Paying users are never purged here.
    /// </summary>
    public class SuspendedSensorPurgeService : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
        public static readonly TimeSpan Retention = TimeSpan.FromDays(180); // 6 months

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<SuspendedSensorPurgeService> _logger;

        public SuspendedSensorPurgeService(IServiceScopeFactory scopeFactory, ILogger<SuspendedSensorPurgeService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var anonymizer = scope.ServiceProvider.GetRequiredService<IAnonymizationService>();
                    var ownership = scope.ServiceProvider.GetRequiredService<IDeviceOwnershipService>();
                    var capacity = scope.ServiceProvider.GetRequiredService<ISubscriptionCapacityService>();
                    var purged = await PurgeExpiredAsync(db, anonymizer, ownership, capacity, Retention, stoppingToken);
                    if (purged > 0)
                        _logger.LogInformation("Purged {Count} sensors suspended beyond the {Days}-day cap", purged, Retention.TotalDays);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Suspended-sensor purge failed");
                }

                try { await Task.Delay(Interval, stoppingToken); }
                catch (TaskCanceledException) { break; }
            }
        }

        /// <summary>
        /// For every owned sensor suspended longer than <paramref name="retention"/> whose owner has
        /// opted out of retention AND no longer has subscription coverage: anonymize the owner's
        /// exclusive telemetry and force-unclaim the sensor (removing their ownership and personal rows
        /// for it). Sensors of users who have not opted out, or who still have an active/grace
        /// subscription, are left untouched. Returns the number of sensors purged.
        /// </summary>
        internal static async Task<int> PurgeExpiredAsync(
            ApplicationDbContext db,
            IAnonymizationService anonymizer,
            IDeviceOwnershipService ownership,
            ISubscriptionCapacityService capacity,
            TimeSpan retention,
            CancellationToken ct = default)
        {
            var cutoff = DateTime.UtcNow - retention;
            // Only users who have actively objected to retention (Art. 21 opt-out) are in scope; the
            // default is to keep history for the lifetime of the claim under legitimate interest.
            var expired = await db.UserSensors
                .Where(us => us.IsOwner && us.SuspendedAt != null && us.SuspendedAt < cutoff
                          && db.Users.Any(u => u.Id == us.UserId && u.DataRetentionOptOutAt != null))
                .Select(us => new { us.UserId, us.SensorId })
                .ToListAsync(ct);

            var coverageCache = new Dictionary<string, bool>();
            var purged = 0;
            foreach (var item in expired)
            {
                // A paying user (active or paid-period grace), or one with a subscription-bypass role
                // (complimentary, service account, admin), keeps their data even if opted out — the
                // opt-out only takes effect once there is no coverage left to honour.
                if (!coverageCache.TryGetValue(item.UserId, out var hasCoverage))
                {
                    hasCoverage = await capacity.HasSubscriptionBypassAsync(item.UserId, ct)
                        || await capacity.GetCapacityAsync(item.UserId, ct) > 0;
                    coverageCache[item.UserId] = hasCoverage;
                }
                if (hasCoverage)
                    continue;

                // Move this owner's exclusive telemetry to the ML store and delete its period(s).
                var periodIds = await db.SensorOwnershipPeriods
                    .Where(p => p.UserId == item.UserId && p.SensorId == item.SensorId)
                    .Select(p => p.Id)
                    .ToListAsync(ct);
                foreach (var periodId in periodIds)
                    await anonymizer.AnonymizeSensorPeriodAsync(periodId, ct);

                // Force-unclaim: remove ownership and the owner's personal rows for this sensor.
                db.UserSensors.RemoveRange(db.UserSensors.Where(us => us.UserId == item.UserId && us.SensorId == item.SensorId));
                db.UserSensorCustomNames.RemoveRange(db.UserSensorCustomNames.Where(x => x.UserId == item.UserId && x.SensorId == item.SensorId));
                db.SensorActivities.RemoveRange(db.SensorActivities.Where(a => a.UserId == item.UserId && a.SensorId == item.SensorId));
                db.SensorPhotos.RemoveRange(db.SensorPhotos.Where(p => p.UserId == item.UserId && p.SensorId == item.SensorId));
                db.SensorOfflineNotifications.RemoveRange(db.SensorOfflineNotifications.Where(n => n.UserId == item.UserId && n.SensorId == item.SensorId));
                await db.SaveChangesAsync(ct);

                ownership.InvalidateSensor(item.SensorId);
                purged++;
            }

            return purged;
        }
    }
}
