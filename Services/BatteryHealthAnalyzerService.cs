using garge_api.Helpers;
using garge_api.Models;
using garge_api.Models.Sensor;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    /// <summary>
    /// Server-side battery-health analyzer. Replaces the firmware's NVS
    /// state machine. On every new <c>SensorData</c> insert for a voltage
    /// sensor, recompute health metrics from the voltage stream and
    /// upsert a new <c>BatteryHealth</c> row plus any newly-detected
    /// <c>BatteryChargeEvents</c>.
    ///
    /// Trigger: piggybacks on the existing <c>sensordata_change_trigger</c>
    /// Postgres NOTIFY plumbing that <see cref="PostgresNotificationService"/>
    /// already subscribes to for SwitchData. We add a separate listener so
    /// the existing service stays focused.
    ///
    /// Fallback timer: every hour, scan for voltage sensors whose latest
    /// BatteryHealth row is older than 90 min (or missing) and run the
    /// analyzer for them. Covers missed notifications.
    /// </summary>
    public class BatteryHealthAnalyzerService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<BatteryHealthAnalyzerService> _logger;
        private static readonly TimeSpan FallbackInterval = TimeSpan.FromHours(1);
        private static readonly TimeSpan StalenessThreshold = TimeSpan.FromMinutes(90);

        private static readonly TimeSpan RestingWindow = TimeSpan.FromDays(14);
        private static readonly TimeSpan PeakRestingRollingWindow = TimeSpan.FromDays(90);
        private static readonly TimeSpan SlopeWindow = TimeSpan.FromDays(30);
        private static readonly TimeSpan ChargeEventLookback = TimeSpan.FromDays(90);
        private static readonly TimeSpan FullChargeCountWindow = TimeSpan.FromDays(30);

        public BatteryHealthAnalyzerService(
            IServiceScopeFactory scopeFactory,
            ILogger<BatteryHealthAnalyzerService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SweepStaleSensorsAsync(stoppingToken);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "BatteryHealthAnalyzer: sweep failed");
                }

                try
                {
                    await Task.Delay(FallbackInterval, stoppingToken);
                }
                catch (OperationCanceledException) { }
            }
        }

        /// <summary>
        /// Public entry for the notification handler: analyze a single sensor.
        /// </summary>
        public async Task AnalyzeSensorAsync(int sensorId, CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var sensor = await db.Sensors.FindAsync(new object?[] { sensorId }, ct);
            if (sensor == null || !sensor.Type.Equals("voltage", StringComparison.OrdinalIgnoreCase))
                return;

            await RunForSensorAsync(db, sensor, ct);
            await db.SaveChangesAsync(ct);
        }

        private async Task SweepStaleSensorsAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var voltageSensors = await db.Sensors
                .Where(s => s.Type == "voltage")
                .ToListAsync(ct);

            var cutoff = DateTime.UtcNow - StalenessThreshold;
            foreach (var sensor in voltageSensors)
            {
                if (ct.IsCancellationRequested) return;
                var latest = await db.BatteryHealthData
                    .Where(bh => bh.SensorId == sensor.Id)
                    .OrderByDescending(bh => bh.Timestamp)
                    .Select(bh => (DateTime?)bh.Timestamp)
                    .FirstOrDefaultAsync(ct);
                if (latest != null && latest >= cutoff) continue;
                await RunForSensorAsync(db, sensor, ct);
            }

            await db.SaveChangesAsync(ct);
        }

        private async Task RunForSensorAsync(ApplicationDbContext db, Sensor sensor, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var rangeStart = now - PeakRestingRollingWindow;

            var rawSamples = await db.SensorData
                .Where(sd => sd.SensorId == sensor.Id && sd.Timestamp >= rangeStart)
                .OrderBy(sd => sd.Timestamp)
                .Select(sd => new { sd.Timestamp, sd.Value })
                .ToListAsync(ct);

            if (rawSamples.Count == 0)
            {
                _logger.LogDebug("BatteryHealthAnalyzer: no samples for sensor {SensorId}", sensor.Id);
                return;
            }

            // Drop unphysical readings. 12 V lead-acid systems never produce
            // readings below ~5 V (sensor init artifacts, disconnects) or above
            // ~20 V (charger overvoltage, wiring shorts). Including them poisons
            // bottom-quartile resting-median and the slope built from it.
            const float MinPlausibleVolt = 5.0f;
            const float MaxPlausibleVolt = 20.0f;
            var samples = new List<BatteryHealthMath.VoltageSample>(rawSamples.Count);
            foreach (var s in rawSamples)
            {
                if (!float.TryParse(s.Value, System.Globalization.CultureInfo.InvariantCulture, out var v)) continue;
                if (v < MinPlausibleVolt || v > MaxPlausibleVolt) continue;
                samples.Add(new BatteryHealthMath.VoltageSample(s.Timestamp, v));
            }
            if (samples.Count == 0) return;

            var restingMedian = BatteryHealthMath.ComputeRestingMedian(samples, now, RestingWindow);
            var peakResting = BatteryHealthMath.ComputePeakResting(samples, now, PeakRestingRollingWindow, RestingWindow);

            var currentVoltage = samples[^1].Value;
            var onChargerNow = restingMedian > 0
                && samples.TakeLast(3).All(s => s.Value >= restingMedian * 1.05f);

            var voltageMin24h = samples
                .Where(s => s.Timestamp >= now.AddHours(-24))
                .Select(s => (float?)s.Value)
                .DefaultIfEmpty(null)
                .Min();

            // Fit slope to daily resting-voltage checkpoints (same bottom-25% median
            // stat used for "Drop from peak") so both metrics describe the same
            // underlying signal: trend of resting voltage. Using raw mean here
            // would skew the slope whenever the window contains charging spikes.
            var slopeCheckpoints = new List<BatteryHealthMath.VoltageSample>();
            for (var t = now - SlopeWindow; t <= now; t = t.AddDays(1))
            {
                var r = BatteryHealthMath.ComputeRestingMedian(samples, t, RestingWindow);
                if (r > 0) slopeCheckpoints.Add(new BatteryHealthMath.VoltageSample(t, r));
            }
            var slopePctWeek = BatteryHealthMath.ComputeSlopePercentPerWeek(slopeCheckpoints);

            var detected = BatteryHealthMath.DetectChargeEvents(samples, restingMedian);
            await UpsertChargeEventsAsync(db, sensor.Id, detected, ct);

            var recentEvents = detected.Where(e => e.EndedAt >= now - ChargeEventLookback).ToList();
            var fullChargesLast30d = detected.Count(e => e.EndedAt >= now - FullChargeCountWindow);
            var lastEvent = detected.OrderByDescending(e => e.EndedAt).FirstOrDefault();
            float? acceptance = recentEvents.Count > 0 ? recentEvents.Max(e => e.PeakRatio) : null;

            var daysOfData = (int)Math.Round((now - samples[0].Timestamp).TotalDays);
            var dropPct = peakResting > 0 ? (peakResting - restingMedian) / peakResting * 100f : 0f;
            var hasRecentCharge = recentEvents.Count > 0;
            var classification = BatteryHealthMath.ClassifyHealth(dropPct, slopePctWeek, acceptance, daysOfData, hasRecentCharge);

            db.BatteryHealthData.Add(new BatteryHealth
            {
                SensorId = sensor.Id,
                Status = classification.Status,
                // Legacy field mapping for cutover backwards-compat:
                Baseline = peakResting,
                LastCharge = lastEvent?.PeakVoltage ?? 0f,
                DropPct = dropPct,
                ChargesRecorded = fullChargesLast30d,
                LastChargedAt = lastEvent?.EndedAt,
                // New analyzer-computed fields:
                CurrentVoltage = currentVoltage,
                RestingMedian = restingMedian,
                PeakResting = peakResting,
                OnChargerNow = onChargerNow,
                LastFullChargeAt = lastEvent?.EndedAt,
                LastFullChargePeak = lastEvent?.PeakVoltage,
                VoltageMin24h = voltageMin24h,
                FullChargesLast30d = fullChargesLast30d,
                DailyDropPctPerWeek = slopePctWeek,
                ChargeAcceptanceRatio = acceptance,
                Timestamp = now,
            });

            _logger.LogInformation(
                "BatteryHealthAnalyzer: sensor {SensorId} status={Status} resting={Resting:F3} peakResting={PeakResting:F3} drop={Drop:F2}% slope={Slope:F3}%/wk onCharger={OnCharger} fullCharges30d={Count}",
                sensor.Id, classification.Status, restingMedian, peakResting, dropPct, slopePctWeek, onChargerNow, fullChargesLast30d);
        }

        private static async Task UpsertChargeEventsAsync(
            ApplicationDbContext db,
            int sensorId,
            IReadOnlyList<BatteryHealthMath.ChargeWindow> detected,
            CancellationToken ct)
        {
            if (detected.Count == 0) return;
            var startTimes = detected.Select(d => d.StartedAt).ToList();
            var existing = await db.BatteryChargeEvents
                .Where(e => e.SensorId == sensorId && startTimes.Contains(e.StartedAt))
                .Select(e => e.StartedAt)
                .ToListAsync(ct);
            var existingSet = existing.ToHashSet();
            foreach (var d in detected)
            {
                if (existingSet.Contains(d.StartedAt)) continue;
                db.BatteryChargeEvents.Add(new BatteryChargeEvent
                {
                    SensorId = sensorId,
                    StartedAt = d.StartedAt,
                    EndedAt = d.EndedAt,
                    PeakVoltage = d.PeakVoltage,
                    RestingAtTime = d.RestingAtTime,
                    PeakRatio = d.PeakRatio,
                    DurationMinutes = d.DurationMinutes,
                });
            }
        }
    }
}
