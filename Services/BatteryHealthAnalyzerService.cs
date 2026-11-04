using garge_api.Constants;
using garge_api.Helpers;
using garge_api.Models;
using garge_api.Models.Sensor;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    /// <summary>
    /// Server-side battery-health analyzer. Replaces the firmware's NVS
    /// state machine. On new <c>SensorData</c> inserts for a voltage
    /// sensor, at most once per <see cref="ReadingAnalysisInterval"/>,
    /// replay charge detection from the latest final charge, across owners, bring
    /// <c>BatteryChargeEvents</c> in line with the result and add a new
    /// <c>BatteryHealth</c> row of reading facts when a reading has arrived since
    /// the latest row. Anything that depends on the
    /// viewer is worked out at read time by <see cref="BatteryHealthReadService"/>.
    /// The rules are described in docs/battery-health.md.
    ///
    /// Trigger: piggybacks on the existing <c>sensordata_change_trigger</c>
    /// Postgres NOTIFY plumbing that <see cref="PostgresNotificationService"/>
    /// already subscribes to for SwitchData. We add a separate listener so
    /// the existing service stays focused.
    ///
    /// Fallback timer: every hour, scan for voltage sensors whose latest
    /// BatteryHealth row is older than 90 min (or missing) and that have a
    /// reading newer than it, and run the analyzer for them. Covers missed
    /// notifications.
    /// </summary>
    public class BatteryHealthAnalyzerService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<BatteryHealthAnalyzerService> _logger;
        private readonly TimeProvider _time;
        private static readonly TimeSpan FallbackInterval = TimeSpan.FromHours(1);
        private static readonly TimeSpan StalenessThreshold = TimeSpan.FromMinutes(90);
        // Readings arrive far more often than the health metrics move, so
        // analyzing every reading only repeats the same query.
        private static readonly TimeSpan ReadingAnalysisInterval = TimeSpan.FromMinutes(15);
        private readonly PerKeyThrottle _readingThrottle = new(ReadingAnalysisInterval);

        // A stored charge that ended this long ago no longer changes, so a run
        // replays from its end instead of from the start of the history. Longer
        // than SlowClimbHours, the furthest a later reading can reach back.
        internal static readonly TimeSpan FinalAfter = TimeSpan.FromDays(5);
        // A sensor this quiet is offline. It gets no new health row.
        internal static readonly TimeSpan OfflineAfter = TimeSpan.FromDays(3);

        // A replay rewrites a sensor's charge rows, so runs from the notification
        // handler, the sweep and the admin endpoints take turns.
        private readonly SemaphoreSlim _runGate = new(1, 1);

        public BatteryHealthAnalyzerService(
            IServiceScopeFactory scopeFactory,
            ILogger<BatteryHealthAnalyzerService> logger,
            TimeProvider time)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _time = time;
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
        /// Entry for the notification handler: analyze a sensor after a new
        /// reading, unless it was analyzed within the last
        /// <see cref="ReadingAnalysisInterval"/>.
        /// </summary>
        public Task AnalyzeSensorForReadingAsync(int sensorId, CancellationToken ct)
        {
            if (!_readingThrottle.TryEnter(sensorId)) return Task.CompletedTask;
            return AnalyzeSensorAsync(sensorId, ct);
        }

        /// <summary>
        /// Analyze a single sensor now, replaying from its latest final charge.
        /// </summary>
        public Task AnalyzeSensorAsync(int sensorId, CancellationToken ct) => RunInScopeAsync(sensorId, rebuild: false, ct);

        /// <summary>
        /// Rebuild a single sensor from its whole reading history, replacing its
        /// charge events. An online sensor with a reading newer than its latest
        /// health row also gets a row. Used by the admin reanalyze endpoints.
        /// </summary>
        public Task ReanalyzeSensorAsync(int sensorId, CancellationToken ct) => RunInScopeAsync(sensorId, rebuild: true, ct);

        private async Task RunInScopeAsync(int sensorId, bool rebuild, CancellationToken ct)
        {
            await _runGate.WaitAsync(ct);
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var sensor = await db.Sensors.FindAsync(new object?[] { sensorId }, ct);
                if (sensor == null || !sensor.Type.Equals(SensorTypes.Voltage, StringComparison.OrdinalIgnoreCase))
                    return;

                await RunForSensorAsync(db, sensor, rebuild, ct);
                await db.SaveChangesAsync(ct);
            }
            finally
            {
                _runGate.Release();
            }
        }

        internal async Task SweepStaleSensorsAsync(CancellationToken ct)
        {
            List<int> staleSensorIds = new();
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var voltageSensorIds = await db.Sensors
                    .Where(s => s.Type == SensorTypes.Voltage)
                    .Select(s => s.Id)
                    .ToListAsync(ct);

                var cutoff = _time.GetUtcNow().UtcDateTime - StalenessThreshold;
                foreach (var sensorId in voltageSensorIds)
                {
                    if (ct.IsCancellationRequested) return;
                    var latest = await db.BatteryHealthData
                        .Where(bh => bh.SensorId == sensorId)
                        .OrderByDescending(bh => bh.Timestamp)
                        .Select(bh => (DateTime?)bh.Timestamp)
                        .FirstOrDefaultAsync(ct);
                    if (latest != null && latest >= cutoff) continue;

                    // A row is timed by its reading, so a quiet sensor's row stays old.
                    // With no reading since that row there is nothing to replay.
                    var latestReading = await db.SensorData
                        .Where(sd => sd.SensorId == sensorId)
                        .OrderByDescending(sd => sd.Timestamp)
                        .Select(sd => (DateTime?)sd.Timestamp)
                        .FirstOrDefaultAsync(ct);
                    if (latest != null && latestReading <= latest) continue;
                    staleSensorIds.Add(sensorId);
                }
            }

            // Each sensor saves on its own, so one failure leaves the others untouched.
            foreach (var sensorId in staleSensorIds)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    await AnalyzeSensorAsync(sensorId, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "BatteryHealthAnalyzer: sweep failed for sensor {SensorId}", sensorId);
                }
            }
        }

        private async Task RunForSensorAsync(ApplicationDbContext db, Sensor sensor, bool rebuild, CancellationToken ct)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var offlineCutoff = now - OfflineAfter;

            // Checked before reading any history, so the hourly sweep stays
            // cheap for a sensor that stopped reporting.
            if (!rebuild)
            {
                var latestReading = await db.SensorData
                    .Where(sd => sd.SensorId == sensor.Id && sd.Timestamp <= now)
                    .OrderByDescending(sd => sd.Timestamp)
                    .Select(sd => (DateTime?)sd.Timestamp)
                    .FirstOrDefaultAsync(ct);
                if (latestReading == null || latestReading < offlineCutoff)
                {
                    _logger.LogDebug("BatteryHealthAnalyzer: sensor {SensorId} has no recent reading", sensor.Id);
                    return;
                }
            }

            var stored = await db.BatteryChargeEvents
                .Where(e => e.SensorId == sensor.Id)
                .OrderBy(e => e.StartedAt)
                .ToListAsync(ct);

            // Only a replay from the start can rebuild a charge the readings no
            // longer continue from. This is also the path with no final charge yet.
            var final = rebuild ? null : stored.LastOrDefault(e => e.EndedAt < now - FinalAfter);
            var samples = await ReadValidSamplesAsync(db, sensor.Id, final?.EndedAt, now, ct);
            var detection = BatteryHealthMath.DetectCharges(samples, final?.EndedAt);
            if (detection.ResumePointChanged)
            {
                _logger.LogInformation("BatteryHealthAnalyzer: sensor {SensorId} readings do not continue from the final charge, replaying the whole history", sensor.Id);
                final = null;
                samples = await ReadValidSamplesAsync(db, sensor.Id, null, now, ct);
                detection = BatteryHealthMath.DetectCharges(samples);
            }

            var latestIsRecent = samples.Count > 0 && samples[^1].Timestamp >= offlineCutoff;
            if (!latestIsRecent && !rebuild)
            {
                _logger.LogDebug("BatteryHealthAnalyzer: sensor {SensorId} has no recent valid reading", sensor.Id);
                return;
            }

            if (final != null)
            {
                final.SettledVoltage = detection.ResumeSettledVoltage;
                final.SelfDischargePctPerWeek = final.IsFull ? detection.ResumeSelfDischarge : null;
            }
            ApplyCharges(db, sensor.Id, stored, final, detection.Charges);
            if (!latestIsRecent)
            {
                _logger.LogInformation("BatteryHealthAnalyzer: sensor {SensorId} rebuilt {Count} charges, offline so no new health row", sensor.Id, detection.Charges.Count);
                return;
            }

            // A row is timed by the reading it holds, so the ownership filter on
            // Timestamp keeps a reading from before a viewer's period away from them.
            // Only a reading newer than the latest row gives a new row, so a rerun adds
            // no duplicate.
            var latestRow = await db.BatteryHealthData
                .Where(bh => bh.SensorId == sensor.Id)
                .OrderByDescending(bh => bh.Timestamp)
                .Select(bh => (DateTime?)bh.Timestamp)
                .FirstOrDefaultAsync(ct);
            if (latestRow != null && samples[^1].Timestamp <= latestRow)
            {
                _logger.LogDebug("BatteryHealthAnalyzer: sensor {SensorId} has no reading since its latest health row", sensor.Id);
                return;
            }

            var openCharge = detection.Charges.FirstOrDefault(c => c.IsOpen);
            var restingVoltage = BatteryHealthMath.ComputeRestingVoltage(samples, detection.Charges, now);
            var voltageMin24h = samples
                .Where(s => s.Timestamp >= now.AddHours(-24))
                .Select(s => (float?)s.Value)
                .DefaultIfEmpty(null)
                .Min();

            db.BatteryHealthData.Add(new BatteryHealth
            {
                SensorId = sensor.Id,
                CurrentVoltage = samples[^1].Value,
                RestingMedian = restingVoltage,
                OnChargerNow = openCharge != null,
                VoltageMin24h = voltageMin24h,
                Timestamp = samples[^1].Timestamp,
            });

            _logger.LogInformation(
                "BatteryHealthAnalyzer: sensor {SensorId} resting={Resting} onCharger={OnCharger} replayedFrom={ReplayedFrom} readings={Readings} charges={Charges}",
                sensor.Id, restingVoltage, openCharge != null, final?.EndedAt, samples.Count, detection.Charges.Count);
        }

        // Readings from a point in time, or all of them, with unphysical ones
        // dropped. 12 V lead-acid systems never produce readings below ~5 V
        // (sensor init artifacts, disconnects) or above ~20 V (charger
        // overvoltage, wiring shorts). The text "NaN" parses as a float, so
        // a reading must also be finite.
        private static async Task<List<BatteryHealthMath.VoltageSample>> ReadValidSamplesAsync(
            ApplicationDbContext db,
            int sensorId,
            DateTime? from,
            DateTime to,
            CancellationToken ct)
        {
            const float MinPlausibleVolt = 5.0f;
            const float MaxPlausibleVolt = 20.0f;
            var query = db.SensorData.Where(sd => sd.SensorId == sensorId && sd.Timestamp <= to);
            if (from != null) query = query.Where(sd => sd.Timestamp >= from.Value);
            var rawSamples = await query
                .OrderBy(sd => sd.Timestamp)
                .ThenBy(sd => sd.Id)
                .Select(sd => new { sd.Timestamp, sd.Value })
                .ToListAsync(ct);

            var samples = new List<BatteryHealthMath.VoltageSample>(rawSamples.Count);
            foreach (var s in rawSamples)
            {
                if (!float.TryParse(s.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) continue;
                if (!float.IsFinite(v) || v < MinPlausibleVolt || v > MaxPlausibleVolt) continue;
                samples.Add(new BatteryHealthMath.VoltageSample(s.Timestamp, v));
            }
            return samples;
        }

        // Makes the rows after the final charge match the replay, matching on
        // start time, so a charge whose start moved leaves no row behind.
        private static void ApplyCharges(
            ApplicationDbContext db,
            int sensorId,
            List<BatteryChargeEvent> stored,
            BatteryChargeEvent? final,
            IReadOnlyList<BatteryHealthMath.ChargeWindow> charges)
        {
            var replaced = stored.Where(e => final == null || e.StartedAt > final.StartedAt).ToDictionary(e => e.StartedAt);

            foreach (var charge in charges.Where(c => !c.IsOpen))
            {
                if (!replaced.Remove(charge.StartedAt, out var row))
                {
                    row = new BatteryChargeEvent { SensorId = sensorId, StartedAt = charge.StartedAt };
                    db.BatteryChargeEvents.Add(row);
                }
                row.EndedAt = charge.EndedAt!.Value;
                row.PeakVoltage = charge.PeakVoltage;
                row.RestingAtTime = charge.RestingAtTime;
                row.DurationMinutes = (int)Math.Round((charge.EndedAt.Value - charge.StartedAt).TotalMinutes);
                row.IsFull = charge.IsFull;
                row.SettledVoltage = charge.SettledVoltage;
                row.SelfDischargePctPerWeek = charge.SelfDischargePctPerWeek;
            }

            db.BatteryChargeEvents.RemoveRange(replaced.Values);
        }
    }
}
