using System.Globalization;
using garge_api.Models;
using garge_api.Models.Anonymized;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    /// <summary>
    /// Moves a device's telemetry for one ownership stint into the anonymized ML store and deletes the
    /// personal originals. Used by the retention cap, GDPR erasure, and account deletion. The new
    /// <see cref="AnonymizedSeries"/> has no stored link back to the device or user, so the data leaves
    /// GDPR scope and a future re-claim of the same physical device cannot rejoin it.
    /// </summary>
    public interface IAnonymizationService
    {
        /// <summary>Anonymize the telemetry exclusive to a sensor ownership period, then delete the period. Returns readings moved.</summary>
        Task<int> AnonymizeSensorPeriodAsync(int periodId, CancellationToken ct = default);

        /// <summary>Anonymize the telemetry exclusive to a switch ownership period, then delete the period. Returns readings moved.</summary>
        Task<int> AnonymizeSwitchPeriodAsync(int periodId, CancellationToken ct = default);
    }

    public class AnonymizationService : IAnonymizationService
    {
        private readonly ApplicationDbContext _db;
        private readonly ILogger<AnonymizationService> _logger;

        public AnonymizationService(ApplicationDbContext db, ILogger<AnonymizationService> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task<int> AnonymizeSensorPeriodAsync(int periodId, CancellationToken ct = default)
        {
            var period = await _db.SensorOwnershipPeriods.FirstOrDefaultAsync(p => p.Id == periodId, ct);
            if (period == null) return 0;

            var sensorId = period.SensorId;
            var start = period.StartedAt;
            var end = period.EndedAt ?? DateTime.UtcNow;

            // Telemetry exclusive to this period: inside [start, end) and not covered by any OTHER
            // ownership period (an open period covers through the present). Co-owned ranges stay put.
            var exclusive = await _db.SensorData
                .Where(sd => sd.SensorId == sensorId && sd.Timestamp >= start && sd.Timestamp < end
                    && !_db.SensorOwnershipPeriods.Any(q => q.Id != period.Id && q.SensorId == sensorId
                        && sd.Timestamp >= q.StartedAt && (q.EndedAt == null || sd.Timestamp < q.EndedAt)))
                .OrderBy(sd => sd.Timestamp)
                .ToListAsync(ct);

            var moved = 0;
            if (exclusive.Count > 0)
            {
                var sensor = await _db.Sensors.FirstOrDefaultAsync(s => s.Id == sensorId, ct);
                var series = new AnonymizedSeries
                {
                    SourceType = sensor?.Type ?? "unknown",
                    AnonymizedAt = DateTime.UtcNow
                };
                _db.AnonymizedSeries.Add(series);
                await _db.SaveChangesAsync(ct); // assigns series.Id

                foreach (var sd in exclusive)
                {
                    if (!TryParseValue(sd.Value, out var value)) continue;
                    _db.AnonymizedReadings.Add(new AnonymizedReading { SeriesId = series.Id, Value = value, Timestamp = sd.Timestamp });
                    moved++;
                }
                _db.SensorData.RemoveRange(exclusive);
            }

            // Derived battery data in the exclusive range is regenerable from raw voltage — drop it.
            var batteryHealth = await _db.BatteryHealthData
                .Where(b => b.SensorId == sensorId && b.Timestamp >= start && b.Timestamp < end
                    && !_db.SensorOwnershipPeriods.Any(q => q.Id != period.Id && q.SensorId == sensorId
                        && b.Timestamp >= q.StartedAt && (q.EndedAt == null || b.Timestamp < q.EndedAt)))
                .ToListAsync(ct);
            _db.BatteryHealthData.RemoveRange(batteryHealth);

            var chargeEvents = await _db.BatteryChargeEvents
                .Where(e => e.SensorId == sensorId && e.StartedAt >= start && e.StartedAt < end
                    && !_db.SensorOwnershipPeriods.Any(q => q.Id != period.Id && q.SensorId == sensorId
                        && e.StartedAt >= q.StartedAt && (q.EndedAt == null || e.StartedAt < q.EndedAt)))
                .ToListAsync(ct);
            _db.BatteryChargeEvents.RemoveRange(chargeEvents);

            _db.SensorOwnershipPeriods.Remove(period);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Anonymized sensor period {PeriodId} (sensor {SensorId}): moved {Moved} readings", periodId, sensorId, moved);
            return moved;
        }

        public async Task<int> AnonymizeSwitchPeriodAsync(int periodId, CancellationToken ct = default)
        {
            var period = await _db.SwitchOwnershipPeriods.FirstOrDefaultAsync(p => p.Id == periodId, ct);
            if (period == null) return 0;

            var switchId = period.SwitchId;
            var start = period.StartedAt;
            var end = period.EndedAt ?? DateTime.UtcNow;

            var exclusive = await _db.SwitchData
                .Where(sd => sd.SwitchId == switchId && sd.Timestamp >= start && sd.Timestamp < end
                    && !_db.SwitchOwnershipPeriods.Any(q => q.Id != period.Id && q.SwitchId == switchId
                        && sd.Timestamp >= q.StartedAt && (q.EndedAt == null || sd.Timestamp < q.EndedAt)))
                .OrderBy(sd => sd.Timestamp)
                .ToListAsync(ct);

            var moved = 0;
            if (exclusive.Count > 0)
            {
                var switchEntity = await _db.Switches.FirstOrDefaultAsync(s => s.Id == switchId, ct);
                var series = new AnonymizedSeries
                {
                    SourceType = switchEntity?.Type ?? "socket",
                    AnonymizedAt = DateTime.UtcNow
                };
                _db.AnonymizedSeries.Add(series);
                await _db.SaveChangesAsync(ct);

                foreach (var sd in exclusive)
                {
                    if (!TryParseValue(sd.Value, out var value)) continue;
                    _db.AnonymizedReadings.Add(new AnonymizedReading { SeriesId = series.Id, Value = value, Timestamp = sd.Timestamp });
                    moved++;
                }
                _db.SwitchData.RemoveRange(exclusive);
            }

            _db.SwitchOwnershipPeriods.Remove(period);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Anonymized switch period {PeriodId} (switch {SwitchId}): moved {Moved} readings", periodId, switchId, moved);
            return moved;
        }

        /// <summary>Numeric for every source: parses sensor numbers; maps switch on/true → 1, off/false → 0.</summary>
        internal static bool TryParseValue(string? raw, out double value)
        {
            if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out value)) return true;
            switch (raw?.Trim().ToLowerInvariant())
            {
                case "on":
                case "true":
                    value = 1; return true;
                case "off":
                case "false":
                    value = 0; return true;
            }
            value = 0;
            return false;
        }
    }
}
