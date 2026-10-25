using garge_api.Helpers;
using SjolystInnovation.AppKit.Hosting;
using garge_api.Hubs;
using garge_api.Models;
using garge_api.Models.Subscription;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SjolystInnovation.AppKit.Auth;

namespace garge_api.Services
{
    /// <summary>
    /// Removes garge's own data for a deleted account inside the deletion transaction: telemetry moves
    /// to the anonymized ML store, user-owned rows go, subscriptions end, and the IP captured at terms
    /// acceptance is cleared.
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

            // Every subscription that has not ended, add-ons included, is stopped the way a cancel stops it, so the
            // charge scheduler never charges it again. Vipps is told once the deletion commits. The subscriptions
            // and their invoices stay, since bokføringsloven keeps them.
            var now = DateTime.UtcNow;
            var openSubscriptions = await db.Subscriptions
                .Where(s => s.UserId == userId && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Pending))
                .ToListAsync(cancellationToken);
            foreach (var sub in openSubscriptions)
            {
                sub.Status = SubscriptionStatus.Stopped;
                sub.UpdatedAt = now;
                sub.AgreementStopPending = true;
            }

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

    /// <summary>
    /// Stops the Vipps agreements of the subscriptions the deletion ended, once it commits. A stop that fails stays
    /// marked, and the charge scheduler tries it again. A stopped subscription is never charged either way.
    /// </summary>
    public sealed class AccountSubscriptionStop(ApplicationDbContext db, IVippsService vipps, IOptions<VippsSettings> settings, ILogger<AccountSubscriptionStop> logger)
        : IAccountDeletedHandler<User>
    {
        public async Task OnDeletedAsync(User user, CancellationToken cancellationToken)
        {
            try
            {
                var pending = await SubscriptionAgreementStops.PendingAsync(db, cancellationToken, user.Id);
                await SubscriptionAgreementStops.StopAsync(db, vipps, settings.Value, pending, logger, cancellationToken);
            }
            catch (Exception ex)
            {
                // The deletion has committed, so a failure here must not reach the caller. The scheduler retries.
                logger.LogError(ex, "Stopping the Vipps agreements after an account deletion failed, the charge scheduler tries again");
            }
        }
    }
}
