using System.Collections.Concurrent;

namespace garge_api.Helpers
{
    /// <summary>
    /// Lets work for a key run at most once per interval. Calls inside the interval are rejected
    /// and do not move the window, so a steady stream of calls still gets through once per interval.
    /// </summary>
    public sealed class PerKeyThrottle
    {
        private readonly TimeSpan _interval;
        private readonly TimeProvider _time;
        private readonly ConcurrentDictionary<int, DateTimeOffset> _lastEntered = new();

        public PerKeyThrottle(TimeSpan interval, TimeProvider? time = null)
        {
            _interval = interval;
            _time = time ?? TimeProvider.System;
        }

        /// <summary>
        /// Returns true and starts a new interval when the key has not entered within the last
        /// interval. Returns false otherwise.
        /// </summary>
        public bool TryEnter(int key)
        {
            var now = _time.GetUtcNow();
            while (true)
            {
                if (!_lastEntered.TryGetValue(key, out var last))
                {
                    if (_lastEntered.TryAdd(key, now)) return true;
                    continue;
                }
                if (now - last < _interval) return false;
                if (_lastEntered.TryUpdate(key, now, last)) return true;
            }
        }
    }
}
