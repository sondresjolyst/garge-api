using System.Collections.Concurrent;
using garge_api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace garge_api.Services
{
    /// <summary>
    /// Buffers hub events per (group, kind, entityId) and drains every
    /// <see cref="DrainInterval"/>. Coalesces by (kind, entityId) so a slow
    /// client always sees the latest state and never a stale-then-fresh
    /// sequence: writes to the same entity overwrite the buffered envelope
    /// instead of queueing behind it.
    ///
    /// Edge case: a key added after a drain's snapshot waits one full
    /// DrainInterval before being delivered. Acceptable as added latency,
    /// not data loss — no event is dropped, only collapsed.
    /// </summary>
    public class CoalescingDispatcher : BackgroundService
    {
        public const string SwitchEventName = "switch";
        public const string SensorEventName = "sensor";

        private static readonly TimeSpan DrainInterval = TimeSpan.FromMilliseconds(100);

        private readonly IHubContext<DeviceHub> _hub;
        private readonly ILogger<CoalescingDispatcher> _logger;

        // group -> (kind, entityId) -> latest payload envelope
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<EntityKey, EventEnvelope>> _pending = new();

        public CoalescingDispatcher(IHubContext<DeviceHub> hub, ILogger<CoalescingDispatcher> logger)
        {
            _hub = hub;
            _logger = logger;
        }

        public void EnqueueSwitchForUser(string userId, SwitchEventDto data) =>
            Enqueue(DeviceHub.UserGroup(userId), SwitchEventName, data.SwitchId, data);

        public void EnqueueSwitchForBridges(SwitchEventDto data) =>
            Enqueue(DeviceHub.BridgeGroup, SwitchEventName, data.SwitchId, data);

        public void EnqueueSensorForUser(string userId, SensorEventDto data) =>
            Enqueue(DeviceHub.UserGroup(userId), SensorEventName, data.SensorId, data);

        private void Enqueue(string group, string kind, int entityId, object payload)
        {
            var bucket = _pending.GetOrAdd(group, _ => new ConcurrentDictionary<EntityKey, EventEnvelope>());
            // Indexer-set replaces any existing envelope for this key — that's
            // the coalescing primitive.
            bucket[new EntityKey(kind, entityId)] = new EventEnvelope(kind, payload);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("CoalescingDispatcher started");
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await DrainOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "CoalescingDispatcher drain failed");
                }
                await Task.Delay(DrainInterval, stoppingToken);
            }
        }

        private async Task DrainOnceAsync(CancellationToken ct)
        {
            foreach (var (group, bucket) in _pending.ToArray())
            {
                if (bucket.IsEmpty) continue;

                // ToArray on the bucket returns key/value pairs atomically —
                // any new write between snapshot and TryRemove that targets
                // the same key wins (we'd remove the newer one); that's
                // still latest-wins semantics. New keys added after the
                // snapshot wait one drain tick.
                foreach (var pair in bucket.ToArray())
                {
                    if (!bucket.TryRemove(pair.Key, out var env)) continue;
                    try
                    {
                        await _hub.Clients.Group(group).SendAsync(env.Kind, env.Payload, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to send {Kind} to group {Group}", env.Kind, group);
                    }
                }
            }
        }

        private record EntityKey(string Kind, int EntityId);
        private record EventEnvelope(string Kind, object Payload);
    }
}
