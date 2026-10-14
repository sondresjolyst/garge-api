using garge_api.Helpers;
using garge_api.Hubs;
using garge_api.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SjolystInnovation.AppKit.Auth;

namespace garge_api.Services
{
    /// <summary>
    /// Removes garge's own data for a deleted account inside the deletion transaction: telemetry moves
    /// to the anonymized ML store, user-owned rows go, and the IP captured at terms acceptance is cleared.
    /// </summary>
    public sealed class AccountDataEraser(ApplicationDbContext db, IAnonymizationService anonymizer, IDeviceOwnershipService ownership)
        : IAccountDataEraser<User>
    {
        public async Task EraseAsync(User user, CancellationToken cancellationToken)
        {
            var userId = user.Id;
            // Telemetry first, while the ownership rows still say which readings were the user's.
            var sensorPeriodIds = await db.SensorOwnershipPeriods
                .Where(p => p.UserId == userId).Select(p => p.Id).ToListAsync();
            foreach (var periodId in sensorPeriodIds)
                await anonymizer.AnonymizeSensorPeriodAsync(periodId);

            var switchPeriodIds = await db.SwitchOwnershipPeriods
                .Where(p => p.UserId == userId).Select(p => p.Id).ToListAsync();
            foreach (var periodId in switchPeriodIds)
                await anonymizer.AnonymizeSwitchPeriodAsync(periodId);

            // Capture associated device ids before removing the rows so we
            // can invalidate the ownership cache once the changes commit.
            var sensorIds = await db.UserSensors.Where(us => us.UserId == userId).Select(us => us.SensorId).ToListAsync();
            var switchIds = await db.UserSwitches.Where(us => us.UserId == userId).Select(us => us.SwitchId).ToListAsync();

            db.UserSensorCustomNames.RemoveRange(db.UserSensorCustomNames.Where(x => x.UserId == userId));
            db.UserSensorVoltageThresholds.RemoveRange(db.UserSensorVoltageThresholds.Where(x => x.UserId == userId));
            db.UserSensorSecurities.RemoveRange(db.UserSensorSecurities.Where(x => x.UserId == userId));
            db.UserSwitchCustomNames.RemoveRange(db.UserSwitchCustomNames.Where(x => x.UserId == userId));
            db.SensorActivities.RemoveRange(db.SensorActivities.Where(a => a.UserId == userId));
            db.SensorPhotos.RemoveRange(db.SensorPhotos.Where(p => p.UserId == userId));
            db.PushSubscriptions.RemoveRange(db.PushSubscriptions.Where(s => s.UserId == userId));
            db.SensorOfflineNotifications.RemoveRange(db.SensorOfflineNotifications.Where(n => n.UserId == userId));
            db.UserSensors.RemoveRange(db.UserSensors.Where(us => us.UserId == userId));
            db.UserSwitches.RemoveRange(db.UserSwitches.Where(us => us.UserId == userId));

            foreach (var sid in sensorIds) ownership.InvalidateSensor(sid);
            foreach (var sid in switchIds) ownership.InvalidateSwitch(sid);

            // Not part of the bokføringsloven invoice record, so it has no basis to outlive the account.
            user.TermsAcceptedIp = null;
        }
    }

    /// <summary>Tells the deleted user's open SignalR connections to disconnect once the deletion commits.</summary>
    public sealed class AccountHubDisconnect(IHubConnectionTracker hubConnections, IHubContext<DeviceHub> hub, ILogger<AccountHubDisconnect> logger)
        : IAccountDeletedHandler<User>
    {
        public async Task OnDeletedAsync(User user, CancellationToken cancellationToken)
        {
            var userId = user.Id;
            var connectionIds = hubConnections.GetConnectionIds(userId);
            if (connectionIds.Count == 0) return;

            try
            {
                // Signal the client to stop the connection and sign out.
                await hub.Clients.Clients(connectionIds).SendAsync("forceLogout");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "ForceDisconnectHubAsync: send forceLogout failed for user {UserId}", LogSanitizer.Sanitize(userId));
            }
        }
    }
}
