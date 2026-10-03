using garge_api.Models;

namespace garge_api.Services
{
    /// <summary>
    /// Hands a lapsed device lease to a standby gateway. A lease lapses when its holder stops
    /// reporting the target — the gateway lost power, left the network, or moved out of UDP range
    /// of the device — and until someone takes over, the device answers nothing.
    /// </summary>
    public class DeviceLeaseMaintenanceService(
        IServiceScopeFactory scopeFactory,
        ILogger<DeviceLeaseMaintenanceService> logger) : BackgroundService
    {
        /// <summary>
        /// Well under the lease duration, so a lapse is noticed promptly rather than at the end
        /// of the next tick.
        /// </summary>
        internal static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await PromoteAsync(stoppingToken); }
                catch (Exception ex) { logger.LogError(ex, "DeviceLeaseMaintenanceService error"); }

                try { await Task.Delay(Tick, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        protected virtual async Task PromoteAsync(CancellationToken ct)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var leases = scope.ServiceProvider.GetRequiredService<IDeviceLeaseService>();
            var acls = scope.ServiceProvider.GetRequiredService<IMqttAclService>();
            var broker = scope.ServiceProvider.GetRequiredService<IEmqxAdminClient>();

            var handovers = await leases.PromoteExpiredLeasesAsync(ct);
            if (handovers.Count == 0)
            {
                return;
            }

            foreach (var handover in handovers)
            {
                if (handover.PreviousController != null)
                {
                    await acls.RevokeDiscoveredDeviceAclAsync(handover.PreviousController, handover.Target, ct);
                }

                await acls.EnsureDiscoveredDeviceAclAsync(handover.NewController, handover.Target, ct);
            }

            // A renewal landing between the read and this save overwrites the promotion, so the
            // lease can end up with one gateway and the moved ACL rows with another. It settles
            // itself: whichever gateway holds the lease is granted its rows again on its next
            // report, which a live gateway makes every cycle. Guarding it with a row version
            // would mean a column named xmin, which Postgres reserves.
            await db.SaveChangesAsync(ct);

            // Only after the rows are committed, so a client that reconnects immediately is
            // authorised against the new state rather than the old.
            foreach (var handover in handovers)
            {
                if (handover.PreviousController != null)
                {
                    await broker.KickClientAsync(ClientIdOf(handover.PreviousController), ct);
                }
            }
        }

        /// <summary>
        /// A gateway connects with its chip id as the MQTT client id, while its broker username
        /// carries the <c>garge_</c> prefix.
        /// </summary>
        internal static string ClientIdOf(string deviceName) =>
            deviceName.StartsWith("garge_", StringComparison.Ordinal) ? deviceName["garge_".Length..] : deviceName;
    }
}
