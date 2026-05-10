using System.Collections.Concurrent;
using garge_api.Hubs;
using garge_api.Models.Sensor;
using garge_api.Models.Switch;
using Microsoft.AspNetCore.SignalR;

namespace garge_api.Services
{
    public class CoalescingDispatcher : BackgroundService
    {
        public const string SwitchEventName = "switch";
        public const string SensorEventName = "sensor";

        private static readonly TimeSpan DrainInterval = TimeSpan.FromMilliseconds(100);

        private readonly IHubContext<DeviceHub> _hub;
        private readonly ILogger<CoalescingDispatcher> _logger;

        // group -> (kind, entityId) -> latest payload
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<EntityKey, EventEnvelope>> _pending = new();

        public CoalescingDispatcher(IHubContext<DeviceHub> hub, ILogger<CoalescingDispatcher> logger)
        {
            _hub = hub;
            _logger = logger;
        }

        public void EnqueueSwitchForUser(string userId, SwitchData data) =>
            Enqueue(DeviceHub.UserGroup(userId), SwitchEventName, data.SwitchId, data);

        public void EnqueueSwitchForBridges(SwitchData data) =>
            Enqueue(DeviceHub.BridgeGroup, SwitchEventName, data.SwitchId, data);

        public void EnqueueSensorForUser(string userId, SensorData data) =>
            Enqueue(DeviceHub.UserGroup(userId), SensorEventName, data.SensorId, data);

        private void Enqueue(string group, string kind, int entityId, object payload)
        {
            var bucket = _pending.GetOrAdd(group, _ => new ConcurrentDictionary<EntityKey, EventEnvelope>());
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

                var snapshot = new Dictionary<EntityKey, EventEnvelope>();
                foreach (var key in bucket.Keys.ToArray())
                {
                    if (bucket.TryRemove(key, out var env))
                        snapshot[key] = env;
                }
                if (snapshot.Count == 0) continue;

                foreach (var env in snapshot.Values)
                {
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
