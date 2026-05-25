using garge_api.Models.Common;

namespace garge_api.Helpers
{
    /// <summary>
    /// Shared time-range filtering for telemetry reads. Both the sensor and switch data endpoints
    /// accept the same trio of parameters — a compact <c>timeRange</c> (e.g. "1h"), or an explicit
    /// <c>startDate</c>/<c>endDate</c> pair — with <c>timeRange</c> taking precedence. The resolution
    /// and the EF <c>Where</c> predicate are defined once here so all four endpoints honour identical
    /// semantics.
    /// </summary>
    public static class TimeRangeQueryExtensions
    {
        /// <summary>
        /// Resolves the effective inclusive bounds for a request. When <paramref name="timeRange"/> is a
        /// recognised compact range it yields <c>(now - span, null)</c>; otherwise it passes through
        /// <paramref name="startDate"/>/<paramref name="endDate"/>. An unparseable <paramref name="timeRange"/>
        /// yields no bounds, exactly as the original inline blocks did (the parsed span being null left the
        /// query unfiltered without falling back to the explicit dates).
        /// </summary>
        public static (DateTime? Start, DateTime? End) ResolveRange(
            string? timeRange, DateTime? startDate, DateTime? endDate)
        {
            if (!string.IsNullOrEmpty(timeRange))
            {
                var timeSpan = TimeRangeParser.Parse(timeRange);
                if (timeSpan.HasValue)
                    return (DateTime.UtcNow.Subtract(timeSpan.Value), null);

                return (null, null);
            }

            return (startDate, endDate);
        }

        /// <summary>
        /// Applies the resolved inclusive bounds to a telemetry query. A null bound is skipped, so the
        /// emitted SQL matches the original per-endpoint blocks: <c>Timestamp &gt;= start</c> and/or
        /// <c>Timestamp &lt;= end</c>.
        /// </summary>
        public static IQueryable<T> ApplyTimeRange<T>(
            this IQueryable<T> query, DateTime? start, DateTime? end) where T : ITimestamped
        {
            if (start.HasValue)
                query = query.Where(x => x.Timestamp >= start.Value);
            if (end.HasValue)
                query = query.Where(x => x.Timestamp <= end.Value);
            return query;
        }

        /// <summary>
        /// Resolves the bounds from the request parameters and applies them in one step. Equivalent to
        /// calling <see cref="ResolveRange"/> followed by the two-bound <c>ApplyTimeRange</c> overload.
        /// </summary>
        public static IQueryable<T> ApplyTimeRange<T>(
            this IQueryable<T> query, string? timeRange, DateTime? startDate, DateTime? endDate)
            where T : ITimestamped
        {
            var (start, end) = ResolveRange(timeRange, startDate, endDate);
            return query.ApplyTimeRange(start, end);
        }
    }
}
