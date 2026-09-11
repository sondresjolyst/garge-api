using garge_api.Constants;
using garge_api.Dtos.Sensor;
using garge_api.Hubs;
using garge_api.Models;
using garge_api.Models.Automation;
using garge_api.Models.Sensor;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    public enum SecuritySetResult
    {
        Ok,
        InvalidThreshold,
        ChargingAutomationRequired,
        NoAlertChannel,
        UnsupportedSensor,
    }

    /// <summary>
    /// Garge Security: per-user intent (<see cref="UserSensorSecurity"/>), per-sensor device state
    /// (<see cref="SensorSecurityState"/>), and the settings published to each device.
    /// </summary>
    public interface ISecurityModeService
    {
        Task<AutomationRule?> FindChargingRuleAsync(int sensorId, CancellationToken ct = default);
        Task<SecuritySetResult> SetAsync(string userId, int sensorId, bool enabled, int? thresholdMinutes, CancellationToken ct = default);
        Task RemoveAsync(string userId, int sensorId, CancellationToken ct = default);
        Task<SensorSecurityDto> GetAsync(int sensorId, string userId, bool isOwner, CancellationToken ct = default);
        Task<Dictionary<int, SensorSecuritySummaryDto>> GetSummariesAsync(IReadOnlyCollection<int> sensorIds, CancellationToken ct = default);
        Task ReconcileSensorAsync(int sensorId, CancellationToken ct = default);
        Task ReconcileUserAsync(string userId, CancellationToken ct = default);
        Task RecomputeDeviceAsync(string parentName, CancellationToken ct = default);
        Task<bool> ApplyAckAsync(string sensorName, int sleepSeconds, bool securityEnabled, string? version, CancellationToken ct = default);
        Task<List<DeviceSettingsDto>> GetDeviceSettingsAsync(CancellationToken ct = default);
    }

    public class SecurityModeService(
        ApplicationDbContext db,
        IPermissionService permissions,
        IDeviceSettingsPublisher publisher,
        ISecurityNotifier notifier,
        ILogger<SecurityModeService> logger) : ISecurityModeService
    {
        public async Task<AutomationRule?> FindChargingRuleAsync(int sensorId, CancellationToken ct = default)
        {
            if (!await IsVoltageSensorAsync(sensorId, ct)) return null;

            var candidates = await db.AutomationRules
                .Where(r => r.SensorId == sensorId && r.IsEnabled && (r.Condition == "<" || r.Condition == "<="))
                .ToListAsync(ct);
            candidates = candidates
                .Where(r => string.Equals(r.Action, "on", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (candidates.Count == 0) return null;

            var targetIds = candidates.Select(r => r.TargetId).Distinct().ToList();
            var socketIds = (await db.Switches
                    .Where(s => targetIds.Contains(s.Id))
                    .Select(s => new { s.Id, s.Type })
                    .ToListAsync(ct))
                .Where(s => string.Equals(s.Type, SwitchTypes.Socket, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Id)
                .ToHashSet();

            return candidates
                .Where(r => socketIds.Contains(r.TargetId))
                .OrderByDescending(r => r.Threshold)
                .ThenBy(r => r.Id)
                .FirstOrDefault();
        }

        public async Task<SecuritySetResult> SetAsync(string userId, int sensorId, bool enabled, int? thresholdMinutes, CancellationToken ct = default)
        {
            if (!await IsVoltageSensorAsync(sensorId, ct))
                return SecuritySetResult.UnsupportedSensor;
            if (thresholdMinutes is < SecurityMode.MinThresholdMinutes or > SecurityMode.MaxThresholdMinutes)
                return SecuritySetResult.InvalidThreshold;

            var row = await db.UserSensorSecurities.FirstOrDefaultAsync(r => r.UserId == userId && r.SensorId == sensorId, ct);
            var now = DateTime.UtcNow;

            if (enabled)
            {
                var rule = await FindChargingRuleAsync(sensorId, ct);
                if (rule == null) return SecuritySetResult.ChargingAutomationRequired;

                var profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.Id == userId, ct);
                if (profile == null || !await SecurityNotifier.HasAlertChannelAsync(
                        db, userId, profile.PushNotificationsEnabled, profile.EmailNotificationsEnabled, ct))
                    return SecuritySetResult.NoAlertChannel;

                if (row == null)
                {
                    row = new UserSensorSecurity { UserId = userId, SensorId = sensorId, CreatedAt = now };
                    db.UserSensorSecurities.Add(row);
                }
                if (!row.Enabled) row.EnabledAt = now;
                row.Enabled = true;
                row.ThresholdMinutes = thresholdMinutes ?? row.ThresholdMinutes;
                row.EnforcingAutomationRuleId = rule.Id;
            }
            else if (row != null)
            {
                row.Enabled = false;
                if (thresholdMinutes.HasValue) row.ThresholdMinutes = thresholdMinutes.Value;
                await ResolveSecurityLatchesAsync(userId, sensorId, now, ct);
            }

            await db.SaveChangesAsync(ct);
            await RecomputeForSensorAsync(sensorId, ct);

            logger.LogInformation("Garge Security set {@LogData}", new { UserId = userId, SensorId = sensorId, Enabled = enabled });
            return SecuritySetResult.Ok;
        }

        public async Task RemoveAsync(string userId, int sensorId, CancellationToken ct = default)
        {
            var row = await db.UserSensorSecurities.FirstOrDefaultAsync(r => r.UserId == userId && r.SensorId == sensorId, ct);
            if (row == null) return;

            db.UserSensorSecurities.Remove(row);
            await ResolveSecurityLatchesAsync(userId, sensorId, DateTime.UtcNow, ct);
            await db.SaveChangesAsync(ct);
            await RecomputeForSensorAsync(sensorId, ct);
        }

        public async Task<SensorSecurityDto> GetAsync(int sensorId, string userId, bool isOwner, CancellationToken ct = default)
        {
            var rows = await db.UserSensorSecurities.Where(r => r.SensorId == sensorId).ToListAsync(ct);
            var owners = await OwnerPairsAsync([sensorId], ct);
            var row = rows.FirstOrDefault(r => r.UserId == userId && owners.Contains((r.UserId, r.SensorId)))
                ?? rows.FirstOrDefault(r => r.Enabled && owners.Contains((r.UserId, r.SensorId)));
            var state = await db.SensorSecurityStates.FirstOrDefaultAsync(s => s.SensorId == sensorId, ct);
            var latestReading = await db.SensorData
                .Where(d => d.SensorId == sensorId)
                .MaxAsync(d => (DateTime?)d.Timestamp, ct);

            var enabled = row?.Enabled ?? false;
            var (stateName, reason) = ComputeState(enabled, state, latestReading);

            return new SensorSecurityDto
            {
                SensorId = sensorId,
                Enabled = enabled,
                ThresholdMinutes = row?.ThresholdMinutes ?? SecurityMode.DefaultThresholdMinutes,
                RequestedSleepSeconds = state?.RequestedSleepSeconds ?? SecurityMode.LongSleepSeconds,
                AppliedSleepSeconds = state?.AppliedSleepSeconds,
                ArmedAt = state?.ArmedAt,
                LastReportedAt = latestReading,
                State = stateName,
                Reason = reason,
                EnforcingRule = await BuildEnforcingRuleAsync(sensorId, userId, ct),
                IsOwner = isOwner,
            };
        }

        public async Task<Dictionary<int, SensorSecuritySummaryDto>> GetSummariesAsync(IReadOnlyCollection<int> sensorIds, CancellationToken ct = default)
        {
            if (sensorIds.Count == 0) return [];

            var owners = await OwnerPairsAsync(sensorIds, ct);
            var ownedRows = (await db.UserSensorSecurities
                    .Where(r => sensorIds.Contains(r.SensorId))
                    .Select(r => new { r.UserId, r.SensorId, r.Enabled })
                    .ToListAsync(ct))
                .Where(r => owners.Contains((r.UserId, r.SensorId)))
                .ToList();
            var enabledSensorIds = ownedRows.Where(r => r.Enabled).Select(r => r.SensorId).ToHashSet();
            var rowSensorIds = ownedRows.Select(r => r.SensorId).Distinct().ToList();
            var states = await db.SensorSecurityStates
                .Where(s => sensorIds.Contains(s.SensorId))
                .ToDictionaryAsync(s => s.SensorId, ct);

            return rowSensorIds.ToDictionary(id => id, id =>
            {
                var enabled = enabledSensorIds.Contains(id);
                var (stateName, _) = ComputeState(enabled, states.GetValueOrDefault(id), null);
                return new SensorSecuritySummaryDto { Enabled = enabled, State = stateName };
            });
        }

        public async Task ReconcileSensorAsync(int sensorId, CancellationToken ct = default)
        {
            var enabledRows = await db.UserSensorSecurities
                .Where(r => r.SensorId == sensorId && r.Enabled)
                .ToListAsync(ct);

            var disabled = new List<(string UserId, string Message)>();
            if (enabledRows.Count > 0)
            {
                var entitled = await permissions.GetUsersWithPermissionAsync(
                    enabledRows.Select(r => r.UserId), PermissionNames.GargeSecurity, ct);
                var owners = await OwnerPairsAsync([sensorId], ct);
                var rule = await FindChargingRuleAsync(sensorId, ct);
                var now = DateTime.UtcNow;

                foreach (var row in enabledRows)
                {
                    if (!owners.Contains((row.UserId, row.SensorId)))
                    {
                        row.Enabled = false;
                        await ResolveSecurityLatchesAsync(row.UserId, sensorId, now, ct);
                        continue;
                    }

                    string? message = null;
                    if (!entitled.Contains(row.UserId))
                        message = "Garge Security is no longer available on your account, so it has been turned off.";
                    else if (rule == null)
                        message = "The charging automation it needs was removed or changed, so Garge Security has been turned off.";

                    if (message == null)
                    {
                        row.EnforcingAutomationRuleId = rule!.Id;
                        continue;
                    }

                    row.Enabled = false;
                    await ResolveSecurityLatchesAsync(row.UserId, sensorId, now, ct);
                    disabled.Add((row.UserId, message));
                }

                await db.SaveChangesAsync(ct);
            }

            await RecomputeForSensorAsync(sensorId, ct);

            foreach (var (userId, message) in disabled)
            {
                logger.LogInformation("Garge Security auto-disabled {@LogData}", new { UserId = userId, SensorId = sensorId });
                var name = await SensorDisplayNameAsync(userId, sensorId, ct);
                await notifier.NotifyUserAsync(userId, "Garge Security turned off", $"{name}: {message}", ct);
            }
        }

        public async Task ReconcileUserAsync(string userId, CancellationToken ct = default)
        {
            var sensorIds = await db.UserSensorSecurities
                .Where(r => r.UserId == userId && r.Enabled)
                .Select(r => r.SensorId)
                .ToListAsync(ct);
            foreach (var sensorId in sensorIds)
                await ReconcileSensorAsync(sensorId, ct);
        }

        public async Task RecomputeDeviceAsync(string parentName, CancellationToken ct = default)
        {
            var sensorIds = await db.Sensors
                .Where(s => s.ParentName == parentName)
                .Select(s => s.Id)
                .ToListAsync(ct);
            if (sensorIds.Count == 0) return;

            var enabledRows = await db.UserSensorSecurities
                .Where(r => sensorIds.Contains(r.SensorId) && r.Enabled)
                .ToListAsync(ct);
            var entitled = enabledRows.Count == 0
                ? []
                : await permissions.GetUsersWithPermissionAsync(enabledRows.Select(r => r.UserId), PermissionNames.GargeSecurity, ct);
            var owners = await OwnerPairsAsync(sensorIds, ct);
            var wanting = enabledRows
                .Where(r => entitled.Contains(r.UserId) && owners.Contains((r.UserId, r.SensorId)))
                .ToList();

            int? floor = null;
            foreach (var wantingSensorId in wanting.Select(r => r.SensorId).Distinct())
            {
                var rule = await FindChargingRuleAsync(wantingSensorId, ct);
                if (rule == null) continue;
                var candidate = (int)Math.Round(rule.Threshold * 1000) - SecurityMode.FloorMarginMillivolts;
                floor = floor.HasValue ? Math.Max(floor.Value, candidate) : candidate;
            }

            var securityOn = wanting.Count > 0 && floor.HasValue;
            var requested = securityOn ? SecurityMode.ShortSleepSeconds : SecurityMode.LongSleepSeconds;
            var publishedFloor = securityOn ? floor : null;

            var states = await db.SensorSecurityStates
                .Where(s => sensorIds.Contains(s.SensorId))
                .ToListAsync(ct);
            if (!securityOn && states.Count == 0) return;

            var now = DateTime.UtcNow;
            var changed = false;
            foreach (var sensorId in sensorIds)
            {
                var state = states.FirstOrDefault(s => s.SensorId == sensorId);
                if (state == null)
                {
                    state = new SensorSecurityState { SensorId = sensorId, RequestedAt = now };
                    db.SensorSecurityStates.Add(state);
                    states.Add(state);
                    changed = true;
                }
                if (state.RequestedSleepSeconds != requested)
                {
                    state.ArmedAt = null;
                    state.OfflineDisarmedAt = null;
                    changed = true;
                }
                if (state.RequestedSleepSeconds != requested || state.FloorMillivolts != publishedFloor)
                {
                    state.RequestedSleepSeconds = requested;
                    state.FloorMillivolts = publishedFloor;
                    state.RequestedAt = now;
                    changed = true;
                }
            }

            if (!changed)
            {
                await db.SaveChangesAsync(ct);
                return;
            }

            foreach (var state in states) state.LastPublishedAt = now;
            await db.SaveChangesAsync(ct);

            if (string.IsNullOrWhiteSpace(parentName))
            {
                logger.LogWarning("Garge Security settings not published: sensor has no device name {@LogData}", new { SensorIds = sensorIds });
                return;
            }

            publisher.EnqueueDeviceSettingsForBridges(new DeviceSettingsEventDto(
                sensorIds.Min(), parentName, requested, securityOn, publishedFloor, ToUnixMs(now)));
            logger.LogInformation("Garge Security settings published {@LogData}", new { Device = parentName, SleepSeconds = requested, FloorMillivolts = publishedFloor });
        }

        public async Task<bool> ApplyAckAsync(string sensorName, int sleepSeconds, bool securityEnabled, string? version, CancellationToken ct = default)
        {
            var sensor = await db.Sensors.FirstOrDefaultAsync(s => s.Name == sensorName, ct);
            if (sensor == null) return false;

            var deviceSensorIds = await db.Sensors
                .Where(s => s.ParentName == sensor.ParentName)
                .Select(s => s.Id)
                .ToListAsync(ct);
            var states = await db.SensorSecurityStates
                .Where(s => deviceSensorIds.Contains(s.SensorId))
                .ToListAsync(ct);
            if (states.Count == 0) return true;

            var now = DateTime.UtcNow;
            var newlyPaused = new List<int>();
            foreach (var state in states)
            {
                var wasPaused = IsPausedLowBattery(state);

                state.AppliedSleepSeconds = sleepSeconds;
                state.AppliedAt = now;
                state.SecurityModeReported = securityEnabled;
                state.ReportedFirmwareVersion = version;

                if (securityEnabled
                    && sleepSeconds == SecurityMode.ShortSleepSeconds
                    && state.RequestedSleepSeconds == SecurityMode.ShortSleepSeconds)
                {
                    state.ArmedAt ??= now;
                    state.OfflineDisarmedAt = null;
                }
                else
                {
                    state.ArmedAt = null;
                }

                if (!wasPaused && IsPausedLowBattery(state))
                    newlyPaused.Add(state.SensorId);
            }
            await db.SaveChangesAsync(ct);

            foreach (var sensorId in newlyPaused)
            {
                var owners = await db.UserSensorSecurities
                    .Where(r => r.SensorId == sensorId && r.Enabled)
                    .Select(r => r.UserId)
                    .ToListAsync(ct);
                foreach (var userId in owners)
                {
                    var name = await SensorDisplayNameAsync(userId, sensorId, ct);
                    await notifier.NotifyUserAsync(userId, "Garge Security paused",
                        $"{name}: the battery is below its charging level, so the sensor has gone back to checking in every hour. Check that the charger is plugged in and working.", ct);
                }
            }

            return true;
        }

        public async Task<List<DeviceSettingsDto>> GetDeviceSettingsAsync(CancellationToken ct = default)
        {
            var rows = await (
                from state in db.SensorSecurityStates
                join sensor in db.Sensors on state.SensorId equals sensor.Id
                select new { state.SensorId, sensor.ParentName, state.RequestedSleepSeconds, state.FloorMillivolts, state.RequestedAt })
                .ToListAsync(ct);

            return rows
                .Where(r => !string.IsNullOrWhiteSpace(r.ParentName))
                .GroupBy(r => r.ParentName)
                .Select(g =>
                {
                    var sleep = g.Min(r => r.RequestedSleepSeconds);
                    var securityOn = sleep == SecurityMode.ShortSleepSeconds;
                    return new DeviceSettingsDto
                    {
                        SensorId = g.Min(r => r.SensorId),
                        DeviceName = g.Key,
                        SleepSeconds = sleep,
                        SecurityEnabled = securityOn,
                        FloorMillivolts = securityOn ? g.Max(r => r.FloorMillivolts) : null,
                        Version = ToUnixMs(g.Max(r => r.RequestedAt)),
                    };
                })
                .ToList();
        }

        internal static (string State, string? Reason) ComputeState(bool enabled, SensorSecurityState? state, DateTime? latestReading)
        {
            if (!enabled || state == null || state.RequestedSleepSeconds != SecurityMode.ShortSleepSeconds)
                return (SecurityMode.States.Off, null);
            if (state.OfflineDisarmedAt != null)
                return (SecurityMode.States.Offline, null);
            if (state.ArmedAt != null)
                return (SecurityMode.States.Armed, null);
            if (IsPausedLowBattery(state))
                return (SecurityMode.States.PausedLowBattery, SecurityMode.Reasons.LowBattery);
            if (state.AppliedSleepSeconds == null && latestReading > state.RequestedAt)
                return (SecurityMode.States.Pending, SecurityMode.Reasons.FirmwareTooOld);
            return (SecurityMode.States.Pending, SecurityMode.Reasons.AwaitingWake);
        }

        internal async Task<HashSet<(string UserId, int SensorId)>> OwnerPairsAsync(IReadOnlyCollection<int> sensorIds, CancellationToken ct)
            => (await db.UserSensors
                    .Where(us => us.IsOwner && sensorIds.Contains(us.SensorId))
                    .Select(us => new { us.UserId, us.SensorId })
                    .ToListAsync(ct))
                .Select(x => (x.UserId, x.SensorId))
                .ToHashSet();

        private Task<bool> IsVoltageSensorAsync(int sensorId, CancellationToken ct)
            => db.Sensors.AnyAsync(s => s.Id == sensorId && s.Type == SensorTypes.Voltage, ct);

        private static bool IsPausedLowBattery(SensorSecurityState state) =>
            state.RequestedSleepSeconds == SecurityMode.ShortSleepSeconds
            && state.SecurityModeReported
            && state.AppliedSleepSeconds == SecurityMode.LongSleepSeconds;

        private async Task RecomputeForSensorAsync(int sensorId, CancellationToken ct)
        {
            var parentName = await db.Sensors
                .Where(s => s.Id == sensorId)
                .Select(s => s.ParentName)
                .FirstOrDefaultAsync(ct);
            if (parentName != null)
                await RecomputeDeviceAsync(parentName, ct);
        }

        private async Task ResolveSecurityLatchesAsync(string userId, int sensorId, DateTime now, CancellationToken ct)
        {
            var open = await db.SensorOfflineNotifications
                .Where(n => n.UserId == userId && n.SensorId == sensorId && n.Kind == NotificationKinds.Security && n.ResolvedAt == null)
                .ToListAsync(ct);
            foreach (var latch in open) latch.ResolvedAt = now;
        }

        private async Task<EnforcingRuleDto?> BuildEnforcingRuleAsync(int sensorId, string userId, CancellationToken ct)
        {
            var rule = await FindChargingRuleAsync(sensorId, ct);
            if (rule == null) return null;

            var customName = await db.UserSwitchCustomNames
                .Where(x => x.UserId == userId && x.SwitchId == rule.TargetId)
                .Select(x => x.CustomName)
                .FirstOrDefaultAsync(ct);
            var switchName = await db.Switches
                .Where(s => s.Id == rule.TargetId)
                .Select(s => s.Name)
                .FirstOrDefaultAsync(ct);

            return new EnforcingRuleDto
            {
                Id = rule.Id,
                TargetId = rule.TargetId,
                TargetName = customName ?? switchName ?? $"Socket #{rule.TargetId}",
                Condition = rule.Condition,
                Threshold = rule.Threshold,
            };
        }

        private async Task<string> SensorDisplayNameAsync(string userId, int sensorId, CancellationToken ct)
        {
            var customName = await db.UserSensorCustomNames
                .Where(x => x.UserId == userId && x.SensorId == sensorId)
                .Select(x => x.CustomName)
                .FirstOrDefaultAsync(ct);
            if (customName != null) return customName;

            var defaultName = await db.Sensors
                .Where(s => s.Id == sensorId)
                .Select(s => s.DefaultName)
                .FirstOrDefaultAsync(ct);
            return defaultName ?? $"Sensor #{sensorId}";
        }

        private static long ToUnixMs(DateTime utc) =>
            new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
    }
}
