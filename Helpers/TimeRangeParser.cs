namespace garge_api.Helpers
{
    /// <summary>
    /// Parses compact time-range strings such as "5m", "1h", "2d", "1w", "1y" into a
    /// <see cref="TimeSpan"/>. Shared by the sensor and switch data endpoints so both honour
    /// identical range semantics.
    /// </summary>
    public static class TimeRangeParser
    {
        /// <summary>
        /// Converts a compact time-range string (e.g. "5m", "1h", "2d", "1w", "1y") into a
        /// <see cref="TimeSpan"/>. Returns <c>null</c> when the input is empty, too short, or
        /// uses an unrecognised unit/value.
        /// </summary>
        public static TimeSpan? Parse(string timeRange)
        {
            if (string.IsNullOrEmpty(timeRange) || timeRange.Length < 2)
                return null;

            var value = timeRange.Substring(0, timeRange.Length - 1);
            var unit = timeRange.Substring(timeRange.Length - 1).ToLowerInvariant();

            if (!int.TryParse(value, out var intValue))
                return null;

            return unit switch
            {
                "m" => TimeSpan.FromMinutes(intValue),
                "h" => TimeSpan.FromHours(intValue),
                "d" => TimeSpan.FromDays(intValue),
                "w" => TimeSpan.FromDays(intValue * 7),
                "y" => TimeSpan.FromDays(intValue * 365),
                _ => null,
            };
        }
    }
}
