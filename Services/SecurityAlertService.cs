using garge_api.Constants;
using garge_api.Models;
using garge_api.Models.Pipeline;
using garge_api.Models.Push;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    /// <summary>
    /// One Garge Security detection pass: alerts owners whose armed sensors have gone quiet, sends an
    /// all-clear when they report again, disarms sensors silent for a week, and tells admins about
    /// pipeline outages. Silence overlapping a pipeline gap does not count against a sensor.
    /// </summary>
    public interface ISecurityAlertService
    {
        Task RunAsync(DateTime now, TimeSpan detectorInterval, bool reconcile, CancellationToken ct = default);
    }

    public class SecurityAlertService(
        ApplicationDbContext db,
        IPermissionService permissions,
        IPipelineHealthService pipeline,
        ISecurityModeService security,
        ISecurityNotifier notifier,
        ILogger<SecurityAlertService> logger) : ISecurityAlertService
    {
        private static readonly TimeSpan WakePeriod = TimeSpan.FromSeconds(SecurityMode.ShortSleepSeconds);

        public async Task RunAsync(DateTime now, TimeSpan detectorInterval, bool reconcile, CancellationToken ct = default)
        {
            var gaps = await pipeline.EvaluateAsync(now, detectorInterval, SecurityMode.OfflineDisarmAfter + TimeSpan.FromDays(1), ct);
            await NotifyAdminsAboutGapsAsync(gaps, now, ct);

            if (reconcile)
                await ReconcileActiveSensorsAsync(ct);

            await CheckArmedSensorsAsync(now, gaps, ct);
        }

        internal static TimeSpan EffectiveSilence(DateTime reference, DateTime now, IEnumerable<PipelineGap> gaps, TimeSpan perGapCredit)
        {
            var overlaps = gaps
                .Select(g => (
                    Start: g.StartedAt > reference ? g.StartedAt : reference,
                    End: (g.EndedAt ?? now) < now ? g.EndedAt!.Value : now))
                .Where(i => i.End > i.Start)
                .OrderBy(i => i.Start)
                .ToList();

            var silence = now - reference;
            DateTime? mergedStart = null, mergedEnd = null;
            foreach (var (start, end) in overlaps)
            {
                if (mergedEnd != null && start <= mergedEnd)
                {
                    if (end > mergedEnd) mergedEnd = end;
                    continue;
                }
                if (mergedStart != null) silence -= (mergedEnd!.Value - mergedStart.Value) + perGapCredit;
                mergedStart = start;
                mergedEnd = end;
            }
            if (mergedStart != null) silence -= (mergedEnd!.Value - mergedStart.Value) + perGapCredit;
            return silence;
        }

        private async Task CheckArmedSensorsAsync(DateTime now, List<PipelineGap> gaps, CancellationToken ct)
        {
            var states = await db.SensorSecurityStates.Where(s => s.ArmedAt != null).ToListAsync(ct);
            var openLatches = await db.SensorOfflineNotifications
                .Where(n => n.Kind == NotificationKinds.Security && n.ResolvedAt == null)
                .ToListAsync(ct);
            if (states.Count == 0 && openLatches.Count == 0) return;

            var sensorIds = states.Select(s => s.SensorId).ToList();
            var readingSensorIds = sensorIds.Concat(openLatches.Select(l => l.SensorId)).Distinct().ToList();
            var latestReadings = await db.SensorData
                .Where(d => readingSensorIds.Contains(d.SensorId))
                .GroupBy(d => d.SensorId)
                .Select(g => new { SensorId = g.Key, Last = g.Max(d => d.Timestamp) })
                .ToDictionaryAsync(x => x.SensorId, x => x.Last, ct);
            var rows = await db.UserSensorSecurities
                .Where(r => r.Enabled && sensorIds.Contains(r.SensorId))
                .ToListAsync(ct);
            var memberships = await db.UserSensors
                .Where(us => sensorIds.Contains(us.SensorId))
                .Select(us => new { us.UserId, us.SensorId, us.IsOwner, us.SuspendedAt })
                .ToListAsync(ct);
            var activeOwners = memberships
                .Where(m => m.IsOwner && m.SuspendedAt == null)
                .Select(m => (m.UserId, m.SensorId))
                .ToHashSet();
            var entitled = await permissions.GetUsersWithPermissionAsync(rows.Select(r => r.UserId), PermissionNames.GargeSecurity, ct);

            var newlyStale = new Dictionary<string, List<(int SensorId, TimeSpan Quiet)>>();
            var recovered = new Dictionary<string, List<int>>();

            foreach (var latch in openLatches)
            {
                if (latestReadings.TryGetValue(latch.SensorId, out var reading) && reading > latch.NotifiedAt)
                {
                    latch.ResolvedAt = now;
                    Add(recovered, latch.UserId, latch.SensorId);
                }
            }

            foreach (var state in states)
            {
                var armedAt = state.ArmedAt!.Value;
                var reference = latestReadings.TryGetValue(state.SensorId, out var last) && last > armedAt ? last : armedAt;
                var silence = EffectiveSilence(reference, now, gaps, WakePeriod);

                if (silence > SecurityMode.OfflineDisarmAfter)
                {
                    state.ArmedAt = null;
                    state.OfflineDisarmedAt = now;
                    foreach (var latch in openLatches.Where(l => l.SensorId == state.SensorId && l.ResolvedAt == null)) latch.ResolvedAt = now;
                    logger.LogInformation("Garge Security disarmed after a week offline {@LogData}", new { state.SensorId });
                    continue;
                }

                foreach (var row in rows.Where(r => r.SensorId == state.SensorId))
                {
                    if (!entitled.Contains(row.UserId) || !activeOwners.Contains((row.UserId, row.SensorId))) continue;

                    var hasOpenLatch = openLatches.Any(l => l.UserId == row.UserId && l.SensorId == row.SensorId && l.ResolvedAt == null);
                    if (!hasOpenLatch && silence > TimeSpan.FromMinutes(row.ThresholdMinutes))
                        Add(newlyStale, row.UserId, (row.SensorId, now - reference));
                }
            }

            await db.SaveChangesAsync(ct);

            foreach (var (userId, sensors) in newlyStale)
            {
                var names = await SensorNamesAsync(userId, sensors.Select(s => s.SensorId), ct);
                var (title, message) = sensors.Count == 1
                    ? ("Garge Security alert", $"{names[0]} has not checked in for {(int)sensors[0].Quiet.TotalMinutes} minutes.")
                    : ("Garge Security alert", $"{sensors.Count} sensors have stopped checking in: {string.Join(", ", names)}.");

                if (!await notifier.NotifyUserAsync(userId, title, message, ct))
                {
                    logger.LogWarning("Garge Security alert not delivered; will retry {@LogData}", new { UserId = userId, SensorIds = sensors.Select(s => s.SensorId) });
                    continue;
                }

                foreach (var (sensorId, _) in sensors)
                {
                    db.SensorOfflineNotifications.Add(new SensorOfflineNotification
                    {
                        UserId = userId,
                        SensorId = sensorId,
                        Kind = NotificationKinds.Security,
                        NotifiedAt = now,
                    });
                }
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Garge Security alert sent {@LogData}", new { UserId = userId, SensorIds = sensors.Select(s => s.SensorId) });
            }

            foreach (var (userId, sensorIdsBack) in recovered)
            {
                var names = await SensorNamesAsync(userId, sensorIdsBack, ct);
                var message = names.Count == 1
                    ? $"{names[0]} is checking in again."
                    : $"These sensors are checking in again: {string.Join(", ", names)}.";
                await notifier.NotifyUserAsync(userId, "Garge Security all clear", message, ct);
            }
        }

        private async Task ReconcileActiveSensorsAsync(CancellationToken ct)
        {
            var sensorIds = await db.SensorSecurityStates
                .Where(s => s.RequestedSleepSeconds == SecurityMode.ShortSleepSeconds)
                .Select(s => s.SensorId)
                .ToListAsync(ct);
            foreach (var sensorId in sensorIds)
            {
                try
                {
                    await security.ReconcileSensorAsync(sensorId, ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Garge Security reconcile failed for sensor {SensorId}", sensorId);
                }
            }
        }

        private async Task NotifyAdminsAboutGapsAsync(List<PipelineGap> gaps, DateTime now, CancellationToken ct)
        {
            var toNotify = gaps
                .Where(g => g.Source == SecurityMode.GapSources.Operator && g.AdminNotifiedAt == null && g.EndedAt == null
                            && now - g.StartedAt >= SecurityMode.AdminGapNotifyAfter)
                .ToList();
            var toClear = gaps
                .Where(g => g.AdminNotifiedAt != null && g.EndedAt != null && g.AllClearSentAt == null)
                .ToList();
            if (toNotify.Count == 0 && toClear.Count == 0) return;

            var adminIds = await (
                from ur in db.UserRoles
                join r in db.Roles on ur.RoleId equals r.Id
                where r.Name == RoleNames.Admin
                select ur.UserId)
                .Distinct()
                .ToListAsync(ct);

            foreach (var gap in toNotify)
            {
                foreach (var adminId in adminIds)
                {
                    await notifier.NotifyUserAsync(adminId, "Garge pipeline outage",
                        $"No sensor readings have reached the API since {gap.StartedAt:yyyy-MM-dd HH:mm} UTC (operator heartbeat missing or MQTT disconnected). Garge Security alerts are on hold.", ct);
                }
                gap.AdminNotifiedAt = now;
            }
            foreach (var gap in toClear)
            {
                foreach (var adminId in adminIds)
                {
                    await notifier.NotifyUserAsync(adminId, "Garge pipeline restored",
                        $"Sensor readings are reaching the API again. The outage lasted from {gap.StartedAt:HH:mm} to {gap.EndedAt:HH:mm} UTC.", ct);
                }
                gap.AllClearSentAt = now;
            }
            await db.SaveChangesAsync(ct);
        }

        private async Task<List<string>> SensorNamesAsync(string userId, IEnumerable<int> sensorIds, CancellationToken ct)
        {
            var ids = sensorIds.ToList();
            var custom = await db.UserSensorCustomNames
                .Where(x => x.UserId == userId && ids.Contains(x.SensorId))
                .ToDictionaryAsync(x => x.SensorId, x => x.CustomName, ct);
            var defaults = await db.Sensors
                .Where(s => ids.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.DefaultName, ct);
            return ids
                .Select(id => custom.TryGetValue(id, out var c) ? c : defaults.TryGetValue(id, out var d) ? d : $"Sensor #{id}")
                .ToList();
        }

        private static void Add<T>(Dictionary<string, List<T>> map, string key, T value)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(value);
        }
    }
}
