using garge_api.Models;
using garge_api.Models.Mqtt;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    /// <summary>
    /// Grants broker ACL rows to a gateway device. A gateway publishes its own sensor topics
    /// under its own prefix, but it also publishes the config and state of every Wiz device it
    /// discovered, and subscribes to their command topics, and those sit at the broker root
    /// rather than under the gateway. Each of those needs a row of its own.
    /// </summary>
    public interface IMqttAclService
    {
        /// <summary>Stages the ACL rows for one topic filter, skipping any that already exist.</summary>
        Task EnsureTopicAclAsync(string username, string topic, CancellationToken cancellationToken = default);

        /// <summary>
        /// Stages ACL rows for every device <paramref name="gatewayDeviceName"/> has discovered,
        /// skipping targets a different user owns. Returns the number of targets granted.
        /// </summary>
        Task<int> EnsureDiscoveredDeviceAclsAsync(string gatewayDeviceName, CancellationToken cancellationToken = default);

        /// <summary>Stages the ACL rows for a discovered target, unless a different user owns it.</summary>
        Task<bool> EnsureDiscoveredDeviceAclAsync(string gatewayDeviceName, string target, CancellationToken cancellationToken = default);
    }

    public class MqttAclService : IMqttAclService
    {
        // EMQX matches a retain value per rule, so each topic needs the retained and the
        // non-retained row. Mirrors the rows the pairing provisioning step writes.
        private static readonly short[] RetainValues = [1, 0];

        private readonly ApplicationDbContext _context;
        private readonly ILogger<MqttAclService> _logger;

        public MqttAclService(ApplicationDbContext context, ILogger<MqttAclService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public static string DeviceTopicFilter(string deviceName) => $"garge/devices/{deviceName}/#";

        public async Task EnsureTopicAclAsync(string username, string topic, CancellationToken cancellationToken = default)
        {
            foreach (var retain in RetainValues)
            {
                var retainValue = retain;

                // Rows staged earlier in this unit of work are not in the database yet, so the
                // local view is checked as well; without it one call would stage duplicates.
                // The database predicate is written inline because EF cannot translate a call
                // out to a helper.
                var staged = _context.EMQXMqttAcls.Local.Any(a => Matches(a, username, topic, retainValue));
                var stored = await _context.EMQXMqttAcls.AnyAsync(a =>
                    a.Username == username && a.Permission == "allow" && a.Action == "all" &&
                    a.Topic == topic && a.Qos == 0 && a.Retain == retainValue, cancellationToken);
                if (staged || stored)
                {
                    continue;
                }

                _context.EMQXMqttAcls.Add(new EMQXMqttAcl
                {
                    Username = username,
                    Permission = "allow",
                    Action = "all",
                    Topic = topic,
                    Qos = 0,
                    Retain = retain
                });
            }
        }

        public async Task<bool> EnsureDiscoveredDeviceAclAsync(string gatewayDeviceName, string target, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(gatewayDeviceName) || string.IsNullOrWhiteSpace(target))
            {
                return false;
            }

            // The discovery row is the gateway's own claim, so granting on it alone would let a
            // device name someone else's switch as its target and be given access to it. The
            // owner check is what stops that.
            if (await TargetOwnedByOtherUserAsync(gatewayDeviceName, target, cancellationToken))
            {
                _logger.LogWarning("Skipped discovered-device ACL: target owned by another user {@LogData}",
                    new { GatewayDeviceName = gatewayDeviceName, Target = target });
                return false;
            }

            await EnsureTopicAclAsync(gatewayDeviceName, DeviceTopicFilter(target), cancellationToken);
            return true;
        }

        public async Task<int> EnsureDiscoveredDeviceAclsAsync(string gatewayDeviceName, CancellationToken cancellationToken = default)
        {
            var targets = await _context.DiscoveredDevices
                .Where(d => d.DiscoveredBy == gatewayDeviceName)
                .Select(d => d.Target)
                .Distinct()
                .ToListAsync(cancellationToken);

            var granted = 0;
            foreach (var target in targets)
            {
                if (await EnsureDiscoveredDeviceAclAsync(gatewayDeviceName, target, cancellationToken))
                {
                    granted++;
                }
            }

            return granted;
        }

        private static bool Matches(EMQXMqttAcl acl, string username, string topic, short retain) =>
            acl.Username == username
            && acl.Permission == "allow"
            && acl.Action == "all"
            && acl.Topic == topic
            && acl.Qos == 0
            && acl.Retain == retain;

        /// <summary>
        /// True when the target is a known switch whose owner is not an owner of the gateway.
        /// An unclaimed target, and a target with no owner yet, are both allowed through.
        /// </summary>
        private async Task<bool> TargetOwnedByOtherUserAsync(string gatewayDeviceName, string target, CancellationToken cancellationToken)
        {
            var targetOwnerIds = await _context.Switches
                .Where(s => s.Name == target)
                .SelectMany(s => _context.UserSwitches
                    .Where(us => us.SwitchId == s.Id && us.IsOwner)
                    .Select(us => us.UserId))
                .Distinct()
                .ToListAsync(cancellationToken);

            if (targetOwnerIds.Count == 0)
            {
                return false;
            }

            var gatewayOwnerIds = await _context.Sensors
                .Where(s => s.ParentName == gatewayDeviceName)
                .SelectMany(s => _context.UserSensors
                    .Where(us => us.SensorId == s.Id && us.IsOwner)
                    .Select(us => us.UserId))
                .Distinct()
                .ToListAsync(cancellationToken);

            // A gateway nobody owns yet cannot be shown to share an owner with the target, so an
            // owned target stays off limits until the gateway is claimed.
            return !targetOwnerIds.Any(gatewayOwnerIds.Contains);
        }
    }
}
