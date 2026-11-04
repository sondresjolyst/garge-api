using garge_api.Dtos.Sensor;
using garge_api.Helpers;
using garge_api.Models;
using garge_api.Models.Sensor;
using MapsterMapper;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    /// <summary>
    /// Builds battery health for one viewer at read time. A stored row holds only reading facts,
    /// and everything derived from charges comes from the stored charges inside the viewer's own
    /// ownership periods, so two viewers of one sensor can get different results. Admins see the
    /// whole history. The rules are described in docs/battery-health.md.
    /// </summary>
    public class BatteryHealthReadService
    {
        private readonly ApplicationDbContext _db;
        private readonly IMapper _mapper;
        private readonly TimeProvider _time;

        public BatteryHealthReadService(ApplicationDbContext db, IMapper mapper, TimeProvider time)
        {
            _db = db;
            _mapper = mapper;
            _time = time;
        }

        /// <summary>
        /// The latest health a viewer may see for a sensor, or null when their periods hold no row.
        /// </summary>
        public async Task<BatteryHealthDto?> GetLatestAsync(int sensorId, string? userId, bool isAdmin, CancellationToken ct)
        {
            var latest = await _db.BatteryHealthData
                .Where(bh => bh.SensorId == sensorId)
                .WithinSensorOwnership(_db, userId, isAdmin)
                .OrderByDescending(bh => bh.Timestamp)
                .ThenByDescending(bh => bh.Id)
                .FirstOrDefaultAsync(ct);
            if (latest == null) return null;

            var periods = await ReadPeriodsAsync(sensorId, userId, isAdmin, ct);
            var charges = await ReadChargesAsync(sensorId, userId, isAdmin, null, ct);
            var limited = charges
                .Select(c => BatteryHealthMath.LimitChargeToPeriods(ToStoredCharge(c), periods))
                .OfType<BatteryHealthMath.StoredCharge>()
                .ToList();
            var summary = BatteryHealthMath.SummarizeCharges(limited, _time.GetUtcNow().UtcDateTime);

            var dto = _mapper.Map<BatteryHealthDto>(latest);
            dto.RestingMedian = BatteryHealthMath.RestingInsidePeriods(latest.RestingMedian, latest.Timestamp, periods);
            dto.VoltageMin24h = BatteryHealthMath.Min24hInsidePeriods(latest.VoltageMin24h, latest.Timestamp, periods);
            dto.Status = summary.Status;
            dto.DailyDropPctPerWeek = summary.SlopePctPerWeek;
            dto.LastFullChargeAt = summary.LastFullChargeAt;
            dto.LastFullChargePeak = summary.LastFullChargePeak;
            dto.FullChargesLast30d = summary.FullChargesLast30d;
            dto.SelfDischargePctPerWeek = summary.SelfDischargePctPerWeek;
            return dto;
        }

        /// <summary>
        /// The stored charges a viewer may see for a sensor, newest first. A settled voltage or
        /// self-discharge measured past the end of the viewer's period is left out, and so is a
        /// baseline that reaches before the start of it.
        /// </summary>
        public async Task<List<BatteryChargeEventDto>> GetEventsAsync(int sensorId, string? userId, bool isAdmin, DateTime? since, CancellationToken ct)
        {
            var periods = await ReadPeriodsAsync(sensorId, userId, isAdmin, ct);
            var charges = await ReadChargesAsync(sensorId, userId, isAdmin, since, ct);
            var result = new List<BatteryChargeEventDto>(charges.Count);
            foreach (var charge in charges.OrderByDescending(c => c.StartedAt))
            {
                var limited = BatteryHealthMath.LimitChargeToPeriods(ToStoredCharge(charge), periods);
                if (limited == null) continue;
                var dto = _mapper.Map<BatteryChargeEventDto>(charge);
                dto.RestingAtTime = BatteryHealthMath.BaselineInsidePeriods(charge.RestingAtTime, charge.StartedAt, periods);
                dto.SettledVoltage = limited.SettledVoltage;
                dto.SelfDischargePctPerWeek = limited.SelfDischargePctPerWeek;
                result.Add(dto);
            }
            return result;
        }

        private async Task<IReadOnlyList<BatteryHealthMath.OwnershipPeriod>?> ReadPeriodsAsync(int sensorId, string? userId, bool isAdmin, CancellationToken ct)
        {
            if (isAdmin) return null;
            return await _db.SensorOwnershipPeriods
                .Where(p => p.SensorId == sensorId && p.UserId == userId)
                .Select(p => new BatteryHealthMath.OwnershipPeriod(p.StartedAt, p.EndedAt))
                .ToListAsync(ct);
        }

        private async Task<List<BatteryChargeEvent>> ReadChargesAsync(int sensorId, string? userId, bool isAdmin, DateTime? since, CancellationToken ct)
        {
            var query = _db.BatteryChargeEvents
                .Where(e => e.SensorId == sensorId)
                .WithinSensorOwnership(_db, userId, isAdmin);
            if (since != null) query = query.Where(e => e.StartedAt >= since.Value);
            return await query.OrderBy(e => e.StartedAt).ToListAsync(ct);
        }

        internal static BatteryHealthMath.StoredCharge ToStoredCharge(BatteryChargeEvent e) =>
            new(e.StartedAt, e.EndedAt, e.PeakVoltage, e.IsFull, e.SettledVoltage, e.SelfDischargePctPerWeek);
    }
}
