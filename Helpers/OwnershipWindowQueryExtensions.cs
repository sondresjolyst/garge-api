using garge_api.Models;
using garge_api.Models.Sensor;
using garge_api.Models.Switch;

namespace garge_api.Helpers
{
    /// <summary>
    /// Single source of truth for the resale-safe "ownership window" read boundary: a caller may only
    /// see telemetry that falls inside one of their own ownership periods, so a new owner of a
    /// re-claimed/resold device never sees the previous owner's history. The boundary rule is
    /// <c>timestamp &gt;= period.StartedAt &amp;&amp; (period.EndedAt == null || timestamp &lt; period.EndedAt)</c>,
    /// defined once here and reused by every read endpoint and the data export. All methods are plain
    /// LINQ over <see cref="IQueryable{T}"/>, so EF Core translates them to SQL like an inline predicate.
    /// Pass <c>isAdmin</c> = true to bypass the window (admins see everything).
    /// </summary>
    public static class OwnershipWindowQueryExtensions
    {
        public static IQueryable<SensorData> WithinSensorOwnership(
            this IQueryable<SensorData> query, ApplicationDbContext db, string? userId, bool isAdmin = false)
            => isAdmin ? query : query.Where(sd => db.SensorOwnershipPeriods.Any(p =>
                p.UserId == userId && p.SensorId == sd.SensorId
                && sd.Timestamp >= p.StartedAt && (p.EndedAt == null || sd.Timestamp < p.EndedAt)));

        public static IQueryable<BatteryHealth> WithinSensorOwnership(
            this IQueryable<BatteryHealth> query, ApplicationDbContext db, string? userId, bool isAdmin = false)
            => isAdmin ? query : query.Where(bh => db.SensorOwnershipPeriods.Any(p =>
                p.UserId == userId && p.SensorId == bh.SensorId
                && bh.Timestamp >= p.StartedAt && (p.EndedAt == null || bh.Timestamp < p.EndedAt)));

        public static IQueryable<BatteryChargeEvent> WithinSensorOwnership(
            this IQueryable<BatteryChargeEvent> query, ApplicationDbContext db, string? userId, bool isAdmin = false)
            => isAdmin ? query : query.Where(e => db.SensorOwnershipPeriods.Any(p =>
                p.UserId == userId && p.SensorId == e.SensorId
                && e.StartedAt >= p.StartedAt && (p.EndedAt == null || e.StartedAt < p.EndedAt)));

        /// <summary>
        /// Switch reads have two access paths: a direct <see cref="SwitchOwnershipPeriod"/>, or indirect
        /// access via the discovered-device chain (switch name → DiscoveredDevice.Target, DiscoveredBy →
        /// Sensor.ParentName → an owned sensor's period). Either path bounds the data to the caller's window.
        /// </summary>
        public static IQueryable<SwitchData> WithinSwitchOwnership(
            this IQueryable<SwitchData> query, ApplicationDbContext db, string? userId, bool isAdmin = false)
            => isAdmin ? query : query.Where(sd =>
                db.SwitchOwnershipPeriods.Any(p => p.UserId == userId && p.SwitchId == sd.SwitchId
                    && sd.Timestamp >= p.StartedAt && (p.EndedAt == null || sd.Timestamp < p.EndedAt))
                || db.Switches.Any(sw => sw.Id == sd.SwitchId
                    && db.DiscoveredDevices.Any(dd => dd.Target == sw.Name
                        && db.Sensors.Any(s => s.ParentName == dd.DiscoveredBy
                            && db.SensorOwnershipPeriods.Any(p => p.UserId == userId && p.SensorId == s.Id
                                && sd.Timestamp >= p.StartedAt && (p.EndedAt == null || sd.Timestamp < p.EndedAt))))));
    }
}
