using garge_api.Models;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    /// <summary>
    /// Enforces the retention cap on suspended sensors. A sensor an owner has kept suspended for more
    /// than 6 months is force-unclaimed and its telemetry is moved into the anonymized ML store (or
    /// deleted if nothing is exclusive). This is the storage-limitation safeguard required by the GDPR
    /// review: personal data is not kept indefinitely just because the owner left a device off.
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
                    var purged = await PurgeExpiredAsync(db, anonymizer, ownership, Retention, stoppingToken);
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
        /// For every owned sensor suspended longer than <paramref name="retention"/>: anonymize the
        /// owner's exclusive telemetry and force-unclaim the sensor (removing their ownership and
        /// personal rows for it). Returns the number of sensors purged.
        /// </summary>
        internal static async Task<int> PurgeExpiredAsync(
            ApplicationDbContext db,
            IAnonymizationService anonymizer,
            IDeviceOwnershipService ownership,
            TimeSpan retention,
            CancellationToken ct = default)
        {
            var cutoff = DateTime.UtcNow - retention;
            var expired = await db.UserSensors
                .Where(us => us.IsOwner && us.SuspendedAt != null && us.SuspendedAt < cutoff)
                .Select(us => new { us.UserId, us.SensorId })
                .ToListAsync(ct);

            var purged = 0;
            foreach (var item in expired)
            {
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
