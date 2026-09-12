using garge_api.Constants;
using garge_api.Models;
using garge_api.Models.Push;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    public class SensorOfflineCheckService(
        IServiceScopeFactory scopeFactory,
        ILogger<SensorOfflineCheckService> logger) : BackgroundService
    {
        internal static readonly TimeSpan Tick = TimeSpan.FromMinutes(2);
        internal const int OfflineCheckEveryTicks = 15;
        internal const int ReconcileEveryTicks = 5;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var tick = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await CheckSecurityAsync(tick % ReconcileEveryTicks == 0, stoppingToken); }
                catch (Exception ex) { logger.LogError(ex, "Garge Security check error"); }

                if (tick % OfflineCheckEveryTicks == 0)
                {
                    try { await CheckAsync(stoppingToken); }
                    catch (Exception ex) { logger.LogError(ex, "SensorOfflineCheckService error"); }
                }
                tick++;

                try { await Task.Delay(Tick, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        protected virtual async Task CheckSecurityAsync(bool reconcile, CancellationToken ct)
        {
            using var scope = scopeFactory.CreateScope();
            var alerts = scope.ServiceProvider.GetRequiredService<ISecurityAlertService>();
            await alerts.RunAsync(DateTime.UtcNow, Tick, reconcile, ct);
        }

        protected virtual async Task CheckAsync(CancellationToken ct)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var push = scope.ServiceProvider.GetRequiredService<IWebPushService>();

            var users = await db.UserProfiles
                .Where(up => up.PushNotificationsEnabled)
                .ToListAsync(ct);

            if (users.Count == 0) return;

            var now = DateTime.UtcNow;

            foreach (var user in users)
            {
                var threshold = TimeSpan.FromHours(user.OfflineAlertThresholdHours);

                var sensorIds = await db.UserSensors
                    .Where(us => us.UserId == user.Id)
                    .Select(us => us.SensorId)
                    .ToListAsync(ct);

                foreach (var sensorId in sensorIds)
                {
                    var latest = await db.SensorData
                        .Where(sd => sd.SensorId == sensorId)
                        .OrderByDescending(sd => sd.Timestamp)
                        .Select(sd => (DateTime?)sd.Timestamp)
                        .FirstOrDefaultAsync(ct);

                    var activeNotification = await db.SensorOfflineNotifications
                        .Where(n => n.UserId == user.Id && n.SensorId == sensorId && n.Kind == NotificationKinds.Offline && n.ResolvedAt == null)
                        .FirstOrDefaultAsync(ct);

                    bool isOffline = latest == null || now - latest.Value > threshold;

                    if (isOffline && activeNotification == null)
                    {
                        var securityAlertOpen = await db.SensorOfflineNotifications
                            .AnyAsync(n => n.UserId == user.Id && n.SensorId == sensorId && n.Kind == NotificationKinds.Security && n.ResolvedAt == null, ct);
                        if (securityAlertOpen) continue;

                        var customName = await db.UserSensorCustomNames
                            .Where(x => x.UserId == user.Id && x.SensorId == sensorId)
                            .Select(x => x.CustomName)
                            .FirstOrDefaultAsync(ct);

                        var sensor = await db.Sensors.FindAsync([sensorId], ct);
                        var name = customName ?? sensor?.DefaultName ?? $"Sensor #{sensorId}";

                        bool sent;
                        try
                        {
                            sent = await push.SendAsync(
                                user.Id,
                                "Sensor offline",
                                $"{name} has not reported in over {user.OfflineAlertThresholdHours}h.",
                                $"garge-offline-{sensorId}",
                                ct);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Push failed for sensor {SensorId} user {UserId}", sensorId, user.Id);
                            continue;
                        }

                        if (sent)
                        {
                            db.SensorOfflineNotifications.Add(new SensorOfflineNotification
                            {
                                UserId = user.Id,
                                SensorId = sensorId,
                                NotifiedAt = now,
                            });
                            await db.SaveChangesAsync(ct);

                            logger.LogInformation("Offline notification sent: sensor {SensorId} user {UserId}", sensorId, user.Id);
                        }
                    }
                    else if (!isOffline && activeNotification != null)
                    {
                        activeNotification.ResolvedAt = now;
                        await db.SaveChangesAsync(ct);
                    }
                }
            }
        }
    }
}
