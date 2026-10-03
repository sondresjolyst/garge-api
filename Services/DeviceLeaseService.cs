using garge_api.Models;
using garge_api.Models.Mqtt;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    /// <summary>
    /// Decides which gateway acts on a shared target device. Any number of gateways can discover
    /// the same Wiz device; without a single holder they would all answer its command topic and
    /// all publish its state. One holds a lease that it renews while it still sees the target, and
    /// the others stand by until that lease lapses.
    /// </summary>
    public interface IDeviceLeaseService
    {
        /// <summary>
        /// Records that <paramref name="gatewayDeviceName"/> currently sees <paramref name="target"/>,
        /// and returns the gateway that holds the lease afterwards. Renews an existing lease held
        /// by this gateway, takes over an expired one, and leaves a live lease held by another
        /// gateway alone. Stages changes; the caller saves.
        /// </summary>
        Task<string> ReportSeenAsync(string gatewayDeviceName, string target, CancellationToken cancellationToken = default);

        /// <summary>
        /// The gateway holding a live lease on this target, or null when nobody does, in which
        /// case a command for it cannot be delivered yet.
        /// </summary>
        Task<string?> ControllerOfAsync(string target, CancellationToken cancellationToken = default);

        /// <summary>The gateways holding leases for the targets this gateway has discovered.</summary>
        Task<IReadOnlyList<string>> ControlledTargetsAsync(string gatewayDeviceName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Every live lease grouped by the gateway holding it, so each gateway can be told which
        /// targets it may act on.
        /// </summary>
        Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> LiveLeasesByControllerAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Promotes a standby for every lease that has lapsed, choosing deterministically among
        /// the gateways that discovered the target. Returns the targets whose holder changed.
        /// Stages changes; the caller saves.
        /// </summary>
        Task<IReadOnlyList<LeaseHandover>> PromoteExpiredLeasesAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>A lease that moved from one gateway to another.</summary>
    public record LeaseHandover(string Target, string? PreviousController, string NewController);

    public class DeviceLeaseService : IDeviceLeaseService
    {
        /// <summary>
        /// How long a lease survives without renewal. Chosen against the sensor publish interval
        /// (60 s): long enough that a holder gets three chances to renew before losing the lease,
        /// short enough that a dead gateway hands over inside a few minutes. Renewal rides on the
        /// publish a sensor already makes, so a shorter window would mean extra traffic on
        /// metered connections rather than extra safety.
        /// </summary>
        public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(180);

        private readonly ApplicationDbContext _context;
        private readonly TimeProvider _time;
        private readonly ILogger<DeviceLeaseService> _logger;

        public DeviceLeaseService(ApplicationDbContext context, ILogger<DeviceLeaseService> logger, TimeProvider? time = null)
        {
            _context = context;
            _logger = logger;
            _time = time ?? TimeProvider.System;
        }

        public async Task<string> ReportSeenAsync(string gatewayDeviceName, string target, CancellationToken cancellationToken = default)
        {
            var now = _time.GetUtcNow().UtcDateTime;

            var existing = await _context.DeviceControllers
                .FirstOrDefaultAsync(c => c.Target == target, cancellationToken)
                ?? _context.DeviceControllers.Local.FirstOrDefault(c => c.Target == target);

            if (existing == null)
            {
                var created = new DeviceController
                {
                    Target = target,
                    ControllerDeviceName = gatewayDeviceName,
                    LeaseExpiresAt = now + LeaseDuration,
                    LastSeenFromTarget = now,
                    CreatedAt = now
                };
                _context.DeviceControllers.Add(created);
                _logger.LogInformation("Device lease taken {@LogData}",
                    new { Target = target, Controller = gatewayDeviceName });
                return gatewayDeviceName;
            }

            if (existing.ControllerDeviceName == gatewayDeviceName)
            {
                existing.LeaseExpiresAt = now + LeaseDuration;
                existing.LastSeenFromTarget = now;
                return gatewayDeviceName;
            }

            // A live lease belongs to its holder: a returning gateway does not preempt, because
            // swapping holders while the current one is working would move the ACL rows and the
            // Wiz push registration for nothing.
            if (existing.LeaseExpiresAt > now)
            {
                return existing.ControllerDeviceName;
            }

            var previous = existing.ControllerDeviceName;
            existing.ControllerDeviceName = gatewayDeviceName;
            existing.LeaseExpiresAt = now + LeaseDuration;
            existing.LastSeenFromTarget = now;
            _logger.LogInformation("Device lease taken over after expiry {@LogData}",
                new { Target = target, Previous = previous, Controller = gatewayDeviceName });
            return gatewayDeviceName;
        }

        public async Task<string?> ControllerOfAsync(string target, CancellationToken cancellationToken = default)
        {
            var now = _time.GetUtcNow().UtcDateTime;

            return await _context.DeviceControllers
                .Where(c => c.Target == target && c.LeaseExpiresAt > now)
                .Select(c => c.ControllerDeviceName)
                .FirstOrDefaultAsync(cancellationToken);
        }

        public async Task<IReadOnlyList<string>> ControlledTargetsAsync(string gatewayDeviceName, CancellationToken cancellationToken = default)
        {
            var now = _time.GetUtcNow().UtcDateTime;

            return await _context.DeviceControllers
                .Where(c => c.ControllerDeviceName == gatewayDeviceName && c.LeaseExpiresAt > now)
                .Select(c => c.Target)
                .OrderBy(t => t)
                .ToListAsync(cancellationToken);
        }

        public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> LiveLeasesByControllerAsync(CancellationToken cancellationToken = default)
        {
            var now = _time.GetUtcNow().UtcDateTime;

            var live = await _context.DeviceControllers
                .Where(c => c.LeaseExpiresAt > now)
                .Select(c => new { c.ControllerDeviceName, c.Target })
                .ToListAsync(cancellationToken);

            return live
                .GroupBy(c => c.ControllerDeviceName)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<string>)g.Select(c => c.Target).OrderBy(t => t, StringComparer.Ordinal).ToList());
        }

        public async Task<IReadOnlyList<LeaseHandover>> PromoteExpiredLeasesAsync(CancellationToken cancellationToken = default)
        {
            var now = _time.GetUtcNow().UtcDateTime;

            var expired = await _context.DeviceControllers
                .Where(c => c.LeaseExpiresAt <= now)
                .ToListAsync(cancellationToken);

            if (expired.Count == 0)
            {
                return [];
            }

            var targets = expired.Select(c => c.Target).ToList();
            var candidates = await _context.DiscoveredDevices
                .Where(d => targets.Contains(d.Target))
                .Select(d => new { d.Target, d.DiscoveredBy })
                .Distinct()
                .ToListAsync(cancellationToken);

            var handovers = new List<LeaseHandover>();
            foreach (var lease in expired)
            {
                // Deterministic order, so two gateways cannot trade the lease back and forth.
                // There is no usable proximity signal: the rssi in a Wiz syncPilot payload is the
                // bulb's own link to its access point, not the distance to either gateway.
                var next = candidates
                    .Where(c => c.Target == lease.Target && c.DiscoveredBy != lease.ControllerDeviceName)
                    .Select(c => c.DiscoveredBy)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .FirstOrDefault();

                if (next == null)
                {
                    continue;
                }

                var previous = lease.ControllerDeviceName;
                lease.ControllerDeviceName = next;
                lease.LeaseExpiresAt = now + LeaseDuration;
                handovers.Add(new LeaseHandover(lease.Target, previous, next));
                _logger.LogInformation("Device lease promoted {@LogData}",
                    new { lease.Target, Previous = previous, Controller = next });
            }

            return handovers;
        }
    }
}
