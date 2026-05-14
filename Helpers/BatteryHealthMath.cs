namespace garge_api.Helpers
{
    /// <summary>
    /// Pure-function helpers for battery-health analysis. All thresholds
    /// are per-sensor ratios so calibration drift between sensors doesn't
    /// affect classification. Unit-tested in isolation; no DB / time deps.
    /// </summary>
    public static class BatteryHealthMath
    {
        public record VoltageSample(DateTime Timestamp, float Value);

        public record ChargeWindow(
            DateTime StartedAt,
            DateTime EndedAt,
            float PeakVoltage,
            float RestingAtTime,
            int DurationMinutes)
        {
            public float PeakRatio => RestingAtTime > 0 ? PeakVoltage / RestingAtTime : 0;
        }

        // Charge-event detection ratios (relative to restingMedian).
        public const float ChargeEntryRatio = 1.04f;
        public const float ChargeExitRatio = 1.02f;
        public const float ChargePeakMinRatio = 1.08f;
        public const int ChargeMinMinutes = 60;
        public const int ChargeExitSustainMinutes = 30;

        // Status classification thresholds.
        public const float S1AttentionDropPct = 2f;
        public const float S1ReplaceDropPct = 5f;
        public const float S2AttentionSlopePctWeek = 0.1f;
        public const float S2ReplaceSlopePctWeek = 0.3f;
        public const float S3GoodAcceptance = 1.10f;
        public const float S3ReplaceAcceptance = 1.04f;

        public const int LearningMinDays = 14;

        /// <summary>
        /// Resting voltage estimate: median of the bottom quartile (lowest
        /// 25%) of samples in the window. Filters elevated charging periods.
        /// </summary>
        public static float ComputeRestingMedian(IReadOnlyList<VoltageSample> samples, DateTime asOf, TimeSpan window)
        {
            var cutoff = asOf - window;
            var values = samples
                .Where(s => s.Timestamp >= cutoff && s.Timestamp <= asOf)
                .Select(s => s.Value)
                .OrderBy(v => v)
                .ToList();
            if (values.Count == 0) return 0;
            var quartileCount = Math.Max(1, values.Count / 4);
            var bottom = values.Take(quartileCount).ToList();
            return Median(bottom);
        }

        private static float Median(List<float> sortedAscending)
        {
            if (sortedAscending.Count == 0) return 0;
            var n = sortedAscending.Count;
            return n % 2 == 1
                ? sortedAscending[n / 2]
                : (sortedAscending[n / 2 - 1] + sortedAscending[n / 2]) / 2f;
        }

        /// <summary>
        /// Highest resting median observed over the rolling window. Tracks
        /// slow calibration drift by NOT being a lifetime max.
        /// </summary>
        public static float ComputePeakResting(IReadOnlyList<VoltageSample> samples, DateTime asOf, TimeSpan rollingWindow, TimeSpan restingWindow)
        {
            var start = asOf - rollingWindow;
            if (samples.Count == 0) return 0;
            // Sample the resting median across the rolling window at daily checkpoints.
            float peak = 0;
            for (var t = start; t <= asOf; t = t.AddDays(1))
            {
                var resting = ComputeRestingMedian(samples, t, restingWindow);
                if (resting > peak) peak = resting;
            }
            return peak;
        }

        /// <summary>
        /// Detect "full charge" events: contiguous windows of elevated
        /// voltage (above ChargeEntryRatio × resting) lasting ≥ ChargeMinMinutes
        /// and peaking at ≥ ChargePeakMinRatio × resting. Window ends after
        /// ChargeExitSustainMinutes below ChargeExitRatio × resting.
        /// </summary>
        public static IReadOnlyList<ChargeWindow> DetectChargeEvents(IReadOnlyList<VoltageSample> samples, float restingMedian)
        {
            var events = new List<ChargeWindow>();
            if (restingMedian <= 0 || samples.Count == 0) return events;

            var entry = restingMedian * ChargeEntryRatio;
            var exit = restingMedian * ChargeExitRatio;
            var peakMin = restingMedian * ChargePeakMinRatio;

            DateTime? winStart = null;
            float winPeak = 0;
            DateTime? exitCandidateSince = null;

            foreach (var s in samples.OrderBy(s => s.Timestamp))
            {
                if (winStart == null)
                {
                    if (s.Value > entry)
                    {
                        winStart = s.Timestamp;
                        winPeak = s.Value;
                        exitCandidateSince = null;
                    }
                }
                else
                {
                    if (s.Value > winPeak) winPeak = s.Value;

                    if (s.Value < exit)
                    {
                        exitCandidateSince ??= s.Timestamp;
                        if ((s.Timestamp - exitCandidateSince.Value).TotalMinutes >= ChargeExitSustainMinutes)
                        {
                            var endedAt = exitCandidateSince.Value;
                            var durationMin = (int)Math.Round((endedAt - winStart.Value).TotalMinutes);
                            if (durationMin >= ChargeMinMinutes && winPeak >= peakMin)
                            {
                                events.Add(new ChargeWindow(winStart.Value, endedAt, winPeak, restingMedian, durationMin));
                            }
                            winStart = null;
                            winPeak = 0;
                            exitCandidateSince = null;
                        }
                    }
                    else
                    {
                        exitCandidateSince = null;
                    }
                }
            }
            return events;
        }

        /// <summary>
        /// Linear least-squares slope of values over time, expressed as
        /// percent change per week of the values' mean. Positive = rising.
        /// </summary>
        public static float ComputeSlopePercentPerWeek(IReadOnlyList<VoltageSample> samples)
        {
            if (samples.Count < 2) return 0;
            var t0 = samples[0].Timestamp;
            var xs = samples.Select(s => (s.Timestamp - t0).TotalDays).ToList();
            var ys = samples.Select(s => (double)s.Value).ToList();
            var meanX = xs.Average();
            var meanY = ys.Average();
            double num = 0, den = 0;
            for (var i = 0; i < xs.Count; i++)
            {
                num += (xs[i] - meanX) * (ys[i] - meanY);
                den += (xs[i] - meanX) * (xs[i] - meanX);
            }
            if (den == 0 || meanY == 0) return 0;
            var slopePerDay = num / den;
            var slopePerWeek = slopePerDay * 7;
            return (float)(slopePerWeek / meanY * 100.0);
        }

        public record HealthClassification(string Status, string Reason, float DropPct);

        /// <summary>
        /// Combine the three signals into a final status. Worst wins.
        /// Returns "learning" if fewer than <see cref="LearningMinDays"/>
        /// days of data exist.
        /// </summary>
        public static HealthClassification ClassifyHealth(
            float dropPctFromPeak,
            float slopePctPerWeek,
            float? chargeAcceptanceRatio,
            int daysOfData)
        {
            if (daysOfData < LearningMinDays)
                return new HealthClassification("learning", $"only {daysOfData}d of data (need ≥{LearningMinDays}d)", dropPctFromPeak);

            // S1
            var s1 = dropPctFromPeak switch
            {
                <= S1AttentionDropPct => 0,
                <= S1ReplaceDropPct => 1,
                _ => 2
            };

            // S2 (slope is decline if negative; we compare magnitude of decline)
            var declinePctWeek = slopePctPerWeek < 0 ? -slopePctPerWeek : 0;
            var s2 = declinePctWeek switch
            {
                var d when d <= S2AttentionSlopePctWeek => 0,
                var d when d <= S2ReplaceSlopePctWeek => 1,
                _ => 2
            };

            // S3 (only if we have a charge event)
            var s3 = 0;
            if (chargeAcceptanceRatio.HasValue)
            {
                s3 = chargeAcceptanceRatio.Value switch
                {
                    >= S3GoodAcceptance => 0,
                    >= S3ReplaceAcceptance => 1,
                    _ => 2
                };
            }

            var worst = Math.Max(s1, Math.Max(s2, s3));
            var status = worst switch
            {
                0 => "good",
                1 => "attention",
                _ => "replace"
            };

            var reasons = new List<string>();
            if (s1 > 0) reasons.Add($"drop {dropPctFromPeak:F1}%");
            if (s2 > 0) reasons.Add($"decline {declinePctWeek:F2}%/wk");
            if (s3 > 0 && chargeAcceptanceRatio.HasValue) reasons.Add($"charge acceptance {chargeAcceptanceRatio.Value:F2}×");
            var reason = reasons.Count > 0 ? string.Join(", ", reasons) : "all signals healthy";

            return new HealthClassification(status, reason, dropPctFromPeak);
        }
    }
}
