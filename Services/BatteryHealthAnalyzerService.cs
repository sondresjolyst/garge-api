using garge_api.Constants;
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

        // Long window: smooths historical lows. Used for peakResting daily
        // checkpoints, slope checkpoints, and charge-event detection baseline.
        private static readonly TimeSpan RestingWindow = TimeSpan.FromDays(14);
        // Short window: tracks the battery's *current* rested voltage. The 14d
        // window can include prior deep-discharge dips the battery has since
        // recovered from, so "on charger now" and "drop from peak" need a
        // recent-only stat to avoid false alarms.
        private static readonly TimeSpan CurrentRestingWindow = TimeSpan.FromDays(3);
        private static readonly TimeSpan PeakRestingRollingWindow = TimeSpan.FromDays(90);
        private static readonly TimeSpan ChargeEventLookback = TimeSpan.FromDays(90);
        private static readonly TimeSpan FullChargeCountWindow = TimeSpan.FromDays(30);

        // Post-charge settle window: skip 12h of surface-charge decay, then
        // take the median resting voltage between 12h and 36h after the
        // charge event ended. This is the canonical "same SOC" anchor for
        // cycle-over-cycle capacity tracking.
        private static readonly TimeSpan PostChargeSettleStart = TimeSpan.FromHours(12);
        private static readonly TimeSpan PostChargeSettleEnd = TimeSpan.FromHours(36);
        // Minimum cycle anchors before S2 slope is meaningful — below this,
        // we cannot tell a one-off event from a real declining trend.
        private const int S2MinAnchors = 3;

        // OnChargerNow threshold: voltage above the sensor's 90d best rested
        // state. Per-sensor ratio so calibration drift between sensors is fine.
        private const float OnChargerRatio = 1.02f;

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
            if (sensor == null || !sensor.Type.Equals(SensorTypes.Voltage, StringComparison.OrdinalIgnoreCase))
                return;

            await RunForSensorAsync(db, sensor, ct);
            await db.SaveChangesAsync(ct);
        }

        private async Task SweepStaleSensorsAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var voltageSensors = await db.Sensors
                .Where(s => s.Type == SensorTypes.Voltage)
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

            // 14d bottom-25 median: historical low, used for charge-event
            // baseline and slope checkpoints (smooth long-term trend).
            var restingMedian = BatteryHealthMath.ComputeRestingMedian(samples, now, RestingWindow);
            // 3d bottom-25 median: current rested state, used for OnChargerNow
            // and drop-from-peak (so a recovered battery isn't flagged on the
            // basis of an old dip still inside the 14d window).
            var currentResting = BatteryHealthMath.ComputeRestingMedian(samples, now, CurrentRestingWindow);
            var peakResting = BatteryHealthMath.ComputePeakResting(samples, now, PeakRestingRollingWindow, RestingWindow);

            var currentVoltage = samples[^1].Value;
            // Compare current voltage to the 90d best rested state. A charger
            // pushes voltage measurably above any rested level for that sensor.
            var onChargerNow = peakResting > 0
                && samples.TakeLast(3).All(s => s.Value >= peakResting * OnChargerRatio);

            var voltageMin24h = samples
                .Where(s => s.Timestamp >= now.AddHours(-24))
                .Select(s => (float?)s.Value)
                .DefaultIfEmpty(null)
                .Min();

            var detected = BatteryHealthMath.DetectChargeEvents(samples, restingMedian);
            await UpsertChargeEventsAsync(db, sensor.Id, detected, ct);

            // Cycle-anchored slope: one anchor per detected charge event,
            // taken from the post-charge settle window. Each anchor is a
            // same-SOC measurement, so anchor-to-anchor drift reflects
            // capacity loss rather than state-of-charge variation. A
            // calendar-time slope of raw bottom-25 medians (the prior
            // approach) could not distinguish a transient deep-discharge
            // event from real degradation — both showed as a negative slope.
            var anchors = detected
                .Select(e => new
                {
                    e.EndedAt,
                    Resting = BatteryHealthMath.PostChargeResting(
                        samples,
                        e.EndedAt,
                        PostChargeSettleStart,
                        PostChargeSettleEnd)
                })
                .Where(a => a.Resting.HasValue)
                .OrderBy(a => a.EndedAt)
                .Select(a => new BatteryHealthMath.VoltageSample(a.EndedAt, a.Resting!.Value))
                .ToList();
            var cycleSlopePctWeek = BatteryHealthMath.ComputeCycleSlopePercentPerWeek(anchors, S2MinAnchors);

            var recentEvents = detected.Where(e => e.EndedAt >= now - ChargeEventLookback).ToList();
            var fullChargesLast30d = detected.Count(e => e.EndedAt >= now - FullChargeCountWindow);
            var lastEvent = detected.OrderByDescending(e => e.EndedAt).FirstOrDefault();
            float? acceptance = recentEvents.Count > 0 ? recentEvents.Max(e => e.PeakRatio) : null;

            var daysOfData = (int)Math.Round((now - samples[0].Timestamp).TotalDays);
            // Drop reflects how far the *current* rested state sits below the 90d
            // best rested state. Using currentResting (3d) instead of the 14d
            // restingMedian keeps a recovered-from deep-discharge dip from
            // permanently penalizing the battery once voltage is back up.
            var dropPct = peakResting > 0
                ? Math.Max(0f, (peakResting - currentResting) / peakResting * 100f)
                : 0f;
            var hasRecentCharge = recentEvents.Count > 0;
            var classification = BatteryHealthMath.ClassifyHealth(dropPct, cycleSlopePctWeek, acceptance, daysOfData, hasRecentCharge);

            db.BatteryHealthData.Add(new BatteryHealth
            {
                SensorId = sensor.Id,
                Status = classification.Status,
                CurrentVoltage = currentVoltage,
                // Persist the short-window value: UI labels this "Resting
                // voltage" and users expect it to reflect the battery's
                // present resting state, not its 14d low-water mark.
                RestingMedian = currentResting,
                PeakResting = peakResting,
                OnChargerNow = onChargerNow,
                LastFullChargeAt = lastEvent?.EndedAt,
                LastFullChargePeak = lastEvent?.PeakVoltage,
                VoltageMin24h = voltageMin24h,
                FullChargesLast30d = fullChargesLast30d,
                DailyDropPctPerWeek = cycleSlopePctWeek,
                ChargeAcceptanceRatio = acceptance,
                Timestamp = now,
            });

            _logger.LogInformation(
                "BatteryHealthAnalyzer: sensor {SensorId} status={Status} currentResting={CurrentResting:F3} restingMedian14d={Resting:F3} peakResting={PeakResting:F3} drop={Drop:F2}% cycleSlope={Slope} anchors={Anchors}/{MinAnchors} onCharger={OnCharger} fullCharges30d={Count}",
                sensor.Id, classification.Status, currentResting, restingMedian, peakResting, dropPct,
                cycleSlopePctWeek.HasValue ? $"{cycleSlopePctWeek.Value:F3}%/wk" : "null",
                anchors.Count, S2MinAnchors, onChargerNow, fullChargesLast30d);
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
