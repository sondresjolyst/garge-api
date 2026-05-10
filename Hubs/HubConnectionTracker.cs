using System.Collections.Concurrent;

namespace garge_api.Hubs
{
    /// <summary>
    /// Tracks active SignalR DeviceHub connection IDs per user so admin
    /// flows (e.g., user soft-delete) can target them. Singleton; mutated
    /// by DeviceHub.OnConnected/OnDisconnected and read by controllers.
    /// </summary>
    public interface IHubConnectionTracker
    {
        void Add(string userId, string connectionId);
        void Remove(string userId, string connectionId);
        IReadOnlyCollection<string> GetConnectionIds(string userId);
    }

    public class HubConnectionTracker : IHubConnectionTracker
    {
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _byUser = new();

        public void Add(string userId, string connectionId)
        {
            var bag = _byUser.GetOrAdd(userId, _ => new ConcurrentDictionary<string, byte>());
            bag[connectionId] = 0;
        }

        public void Remove(string userId, string connectionId)
        {
            if (_byUser.TryGetValue(userId, out var bag))
            {
                bag.TryRemove(connectionId, out _);
                if (bag.IsEmpty)
                {
                    _byUser.TryRemove(userId, out _);
                }
            }
        }

        public IReadOnlyCollection<string> GetConnectionIds(string userId)
        {
            if (_byUser.TryGetValue(userId, out var bag))
            {
                return bag.Keys.ToList();
            }
            return Array.Empty<string>();
        }
    }
}
