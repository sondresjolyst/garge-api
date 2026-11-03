namespace garge_api.Helpers
{
    /// <summary>
    /// Pure-function helpers for battery-health analysis. Charge detection
    /// compares a battery only with itself, using ratios, because sensors
    /// differ in calibration. Unit-tested in isolation, no DB or time deps.
    /// The rules are described in docs/battery-health.md.
    /// </summary>
    public static class BatteryHealthMath
    {
        public record VoltageSample(DateTime Timestamp, float Value);

        /// <summary>
        /// One detected charge. <see cref="EndedAt"/> is null while the charge
        /// is still open at the latest reading.
        /// </summary>
        public record ChargeWindow(
            DateTime StartedAt,
            DateTime? EndedAt,
            float PeakVoltage,
            float RestingAtTime,
            float Plateau,
            double HeldHours,
            bool IsFull,
            float? SettledVoltage)
        {
            public bool IsOpen => EndedAt == null;
        }

        /// <summary>The time span of a charge, used to find a rest between charges.</summary>
        public record ChargeSpan(DateTime StartedAt, DateTime EndedAt, bool IsFull);

        /// <summary>
        /// Result of a replay. <see cref="Charges"/> holds every charge after
        /// the resume point in time order, the open one last.
        /// <see cref="ResumePointChanged"/> means the readings do not continue
        /// from the resume point, so the caller must replay the whole history.
        /// <see cref="ResumeSettledVoltage"/> is the settled voltage of the
        /// charge the replay resumed from.
        /// </summary>
        public record ChargeDetection(
            IReadOnlyList<ChargeWindow> Charges,
            bool ResumePointChanged,
            float? ResumeSettledVoltage);

        // Charge start and end.
        public const float ChargeStartRatio = 1.03f;
        public const float ChargeEndRatio = 0.965f;
        public const int ChargeConfirmReadings = 2;
        public const float ChargeEndClimbVolts = 0.005f;

        // Baseline at rest.
        public const int BaselineWindowDays = 7;
        public const int BaselineMinReadings = 3;
        public const int BaselineCapDays = 7;

        // Plateau during a charge.
        public const int PlateauWindowReadings = 12;
        public const int PlateauMinReadings = 6;

        // Same session and slow climb.
        public const double SessionMergeHours = 24;
        public const double SlowClimbHours = 72;
        public const float SlowClimbMaxRiseVolts = 0.10f;
        public const float SlowClimbDropVolts = 0.10f;
        public const float SlowClimbAboveLowVolts = 0.05f;

        // Full charge.
        public const float FullHoldRatio = 0.98f;
        public const double FullHoldHours = 6;
        public const double FullHoldMaxGapHours = 3;

        // Settled voltage and resting voltage.
        public const double SettleHours = 12;
        public const double SettledEndHours = 36;
        public const int SettledMinReadings = 3;
        public const double RestingWindowDays = 3;

        // Self-discharge, in days after day 0.
        public const double SelfDischargeMinRestDays = 15;
        public const double SelfDischargeStartFromDay = 2;
        public const double SelfDischargeStartToDay = 4;
        public const double SelfDischargeEndFromDay = 13;
        public const double SelfDischargeEndToDay = 15;
        public const float SelfDischargeSpanDays = 11f;
        public const int SelfDischargeMinReadings = 6;

        // Status classification thresholds.
        public const float S1AttentionDropPct = 2f;
        public const float S1ReplaceDropPct = 5f;
        public const float S2AttentionSlopePctWeek = 0.1f;
        public const float S2ReplaceSlopePctWeek = 0.3f;
        public const int S2MinAnchors = 3;

        private sealed class ChargeState
        {
            public DateTime Start;
            public float Baseline, Peak, Plateau;
            public List<VoltageSample> Samples = new();
        }

        private sealed record ClosedCharge(ChargeState State, DateTime End);

        // Rest readings of the last 7 days, kept sorted so a long history
        // stays cheap to replay.
        private sealed class RestWindow
        {
            private readonly Queue<VoltageSample> _byTime = new();
            private readonly List<float> _sorted = new();

            public int Count => _byTime.Count;
            public float? Last { get; private set; }
            public float LowQuartile => _sorted[_sorted.Count / 4];

            public void Add(VoltageSample s)
            {
                _byTime.Enqueue(s);
                var at = _sorted.BinarySearch(s.Value);
                _sorted.Insert(at < 0 ? ~at : at, s.Value);
                Last = s.Value;
            }

            public void DropBefore(DateTime cutoff)
            {
                while (_byTime.Count > 0 && _byTime.Peek().Timestamp < cutoff)
                    _sorted.RemoveAt(_sorted.BinarySearch(_byTime.Dequeue().Value));
            }

            public void Clear()
            {
                _byTime.Clear();
                _sorted.Clear();
                Last = null;
            }
        }

        /// <summary>
        /// Replay charge detection over readings in time order. Every decision
        /// uses only readings up to that point, so a later reading can never
        /// move a charge that closed more than <see cref="SlowClimbHours"/> ago.
        ///
        /// With <paramref name="resumeFromEnd"/> the replay continues from the
        /// end of an earlier charge. The first reading must be the one at that
        /// end, and the state then equals what a full replay holds at that point.
        /// </summary>
        public static ChargeDetection DetectCharges(IReadOnlyList<VoltageSample> samples, DateTime? resumeFromEnd = null)
        {
            var closed = new List<ClosedCharge>();
            ChargeState? cur = null;
            DateTime? restFrom = null;
            var settleLow = float.MaxValue;
            var rest = new RestWindow();
            int above = 0, below = 0;
            float prevBelow = 0, entryRise = 0;
            VoltageSample? firstAbove = null, firstBelow = null;
            VoltageSample? prev = null;
            var from = 0;

            if (resumeFromEnd != null)
            {
                if (samples.Count == 0 || samples[0].Timestamp != resumeFromEnd.Value)
                    return new ChargeDetection(Array.Empty<ChargeWindow>(), true, null);
                closed.Add(new ClosedCharge(new ChargeState(), resumeFromEnd.Value));
                restFrom = resumeFromEnd;
                settleLow = samples[0].Value;
                prev = samples[0];
                from = 1;
            }

            for (var i = from; i < samples.Count; i++)
            {
                var s = samples[i];
                var last = prev;
                prev = s;

                if (cur != null)
                {
                    cur.Samples.Add(s);
                    cur.Peak = Math.Max(cur.Peak, s.Value);
                    var window = cur.Samples
                        .GetRange(Math.Max(0, cur.Samples.Count - PlateauWindowReadings), Math.Min(cur.Samples.Count, PlateauWindowReadings))
                        .Select(x => x.Value)
                        .OrderBy(v => v)
                        .ToList();
                    // Lower median of at least 6 readings, so a spike early in a charge cannot set the plateau.
                    if (window.Count >= PlateauMinReadings) cur.Plateau = Math.Max(cur.Plateau, window[(window.Count - 1) / 2]);

                    if (cur.Plateau <= 0 || s.Value >= cur.Plateau * ChargeEndRatio)
                    {
                        below = 0;
                        continue;
                    }

                    // Voltage still climbing is a slow charge, not a disconnect.
                    if (below > 0 && s.Value > prevBelow + ChargeEndClimbVolts) below = 0;
                    if (below == 0) firstBelow = s;
                    prevBelow = s.Value;
                    if (++below < ChargeConfirmReadings) continue;

                    // Rest starts at the first low reading. This reading then
                    // takes the rest path, the same as when a replay resumes here.
                    closed.Add(new ClosedCharge(cur, firstBelow!.Timestamp));
                    cur = null;
                    restFrom = firstBelow.Timestamp;
                    settleLow = firstBelow.Value;
                    rest.Clear();
                    below = 0;
                }

                // A resting battery does not climb. A slow rise that ends in a sudden
                // drop means a low-current charger stayed connected until that drop.
                if (closed.Count > 0 && last != null && (s.Timestamp - closed[^1].End).TotalHours <= SlowClimbHours)
                {
                    if (last.Value >= settleLow + SlowClimbAboveLowVolts && s.Value <= last.Value - SlowClimbDropVolts)
                    {
                        if (resumeFromEnd != null && closed.Count == 1)
                            return new ChargeDetection(Array.Empty<ChargeWindow>(), true, null);
                        closed[^1] = closed[^1] with { End = s.Timestamp };
                        restFrom = s.Timestamp;
                        settleLow = s.Value;
                        rest.Clear();
                        above = 0;
                        continue;
                    }
                    settleLow = Math.Min(settleLow, s.Value);
                }

                var inSettle = restFrom != null && s.Timestamp < restFrom.Value.AddHours(SettleHours);
                if (inSettle) settleLow = Math.Min(settleLow, s.Value);
                float baseline;
                if (inSettle) baseline = settleLow;
                else
                {
                    // Low quartile over 7 days, so a slow climb cannot drag its own baseline up.
                    rest.DropBefore(s.Timestamp.AddDays(-BaselineWindowDays));
                    if (rest.Count >= BaselineMinReadings)
                    {
                        baseline = rest.LowQuartile;
                        // For a week after a charge, a climb still counts from the settled low.
                        if (restFrom != null && s.Timestamp < restFrom.Value.AddDays(BaselineCapDays)) baseline = Math.Min(baseline, settleLow);
                    }
                    else if (settleLow < float.MaxValue) baseline = settleLow;
                    else if (rest.Last != null) baseline = rest.Last.Value;
                    else
                    {
                        rest.Add(s);
                        continue;
                    }
                }

                if (s.Value < baseline * ChargeStartRatio)
                {
                    above = 0;
                    if (!inSettle) rest.Add(s);
                    continue;
                }

                if (above == 0)
                {
                    firstAbove = s;
                    entryRise = last == null ? 0 : s.Value - last.Value;
                }
                if (++above < ChargeConfirmReadings) continue;

                // A charger that cycles within a day is one session. So is a slow
                // climb after a charge. A sharp rise after 24 hours is a new charge.
                var sinceEnd = closed.Count > 0 ? (firstAbove!.Timestamp - closed[^1].End).TotalHours : double.MaxValue;
                if (sinceEnd <= SessionMergeHours || (sinceEnd <= SlowClimbHours && entryRise < SlowClimbMaxRiseVolts))
                {
                    if (resumeFromEnd != null && closed.Count == 1)
                        return new ChargeDetection(Array.Empty<ChargeWindow>(), true, null);
                    cur = closed[^1].State;
                    closed.RemoveAt(closed.Count - 1);
                }
                else
                {
                    cur = new ChargeState { Start = firstAbove!.Timestamp, Baseline = baseline };
                }
                cur.Samples.Add(firstAbove!);
                cur.Samples.Add(s);
                cur.Peak = Math.Max(cur.Peak, Math.Max(firstAbove!.Value, s.Value));
                above = 0;
                below = 0;
            }

            var charges = new List<ChargeWindow>();
            float? resumeSettled = null;
            for (var i = 0; i < closed.Count; i++)
            {
                var c = closed[i];
                var next = i + 1 < closed.Count ? closed[i + 1].State.Start : cur?.Start ?? DateTime.MaxValue;
                var settled = PostChargeResting(samples, c.End, next);
                if (resumeFromEnd != null && i == 0)
                {
                    resumeSettled = settled;
                    continue;
                }
                var held = HeldHours(c.State.Samples.Where(x => x.Timestamp < c.End).ToList(), c.State.Plateau * FullHoldRatio);
                charges.Add(new ChargeWindow(c.State.Start, c.End, c.State.Peak, c.State.Baseline, c.State.Plateau, held, held >= FullHoldHours, settled));
            }
            if (cur != null)
                charges.Add(new ChargeWindow(cur.Start, null, cur.Peak, cur.Baseline, cur.Plateau, HeldHours(cur.Samples, cur.Plateau * FullHoldRatio), false, null));
            return new ChargeDetection(charges, false, resumeSettled);
        }

        // Hours spent at or above the level, ignoring gaps in the readings.
        private static double HeldHours(List<VoltageSample> charge, float level)
        {
            double hours = 0;
            for (var i = 1; i < charge.Count; i++)
            {
                var gap = (charge[i].Timestamp - charge[i - 1].Timestamp).TotalHours;
                if (charge[i].Value >= level && charge[i - 1].Value >= level && gap <= FullHoldMaxGapHours) hours += gap;
            }
            return hours;
        }

        /// <summary>
        /// Resting voltage now: median of the bottom quartile of the readings in
        /// the last 3 days, leaving out charging and the 12 hours after it.
        /// Null when no such readings exist.
        /// </summary>
        public static float? ComputeRestingVoltage(IReadOnlyList<VoltageSample> samples, IReadOnlyList<ChargeWindow> charges, DateTime asOf)
        {
            var cutoff = asOf.AddDays(-RestingWindowDays);
            var values = samples
                .Where(s => s.Timestamp >= cutoff && s.Timestamp <= asOf)
                .Where(s => !charges.Any(c => s.Timestamp >= c.StartedAt && (c.EndedAt == null || s.Timestamp < c.EndedAt.Value.AddHours(SettleHours))))
                .Select(s => s.Value)
                .OrderBy(v => v)
                .ToList();
            if (values.Count == 0) return null;
            return Median(values.Take(Math.Max(1, values.Count / 4)).ToList());
        }

        private static float Median(List<float> sortedAscending)
        {
            var n = sortedAscending.Count;
            return n % 2 == 1
                ? sortedAscending[n / 2]
                : (sortedAscending[n / 2 - 1] + sortedAscending[n / 2]) / 2f;
        }

        /// <summary>
        /// Settled voltage of a charge: median of the readings 12 to 36 hours
        /// after it ended, stopping at the next charge. This is the comparable
        /// state-of-charge anchor for tracking a battery across charges.
        ///
        /// Returns null with fewer than 3 readings (next charge started before
        /// the battery settled, or the sensor lost connectivity).
        /// </summary>
        public static float? PostChargeResting(IReadOnlyList<VoltageSample> samples, DateTime chargeEndedAt, DateTime nextChargeStart)
        {
            var windowStart = chargeEndedAt.AddHours(SettleHours);
            var windowEnd = chargeEndedAt.AddHours(SettledEndHours);
            var values = samples
                .Where(s => s.Timestamp >= windowStart && s.Timestamp <= windowEnd && s.Timestamp < nextChargeStart)
                .Select(s => s.Value)
                .OrderBy(v => v)
                .ToList();
            if (values.Count < SettledMinReadings) return null;
            return Median(values);
        }

        /// <summary>
        /// The end of the charge whose rest is measured for self-discharge: the
        /// latest full charge followed by at least 15 days of rest, counted from
        /// day 0, 36 hours after it ended. Null when no such rest exists.
        /// </summary>
        public static DateTime? FindSelfDischargeRest(IReadOnlyList<ChargeSpan> charges, DateTime? openChargeStart, DateTime latestReading)
        {
            for (var i = charges.Count - 1; i >= 0; i--)
            {
                var restEnd = i + 1 < charges.Count ? charges[i + 1].StartedAt : openChargeStart ?? latestReading;
                if (!charges[i].IsFull) continue;
                if ((restEnd - charges[i].EndedAt.AddHours(SettledEndHours)).TotalDays >= SelfDischargeMinRestDays)
                    return charges[i].EndedAt;
            }
            return null;
        }

        /// <summary>
        /// Self-discharge in percent per week over day 3 to 14 of a rest. Each
        /// end is the median of a 2-day window, which needs at least 6 readings,
        /// otherwise the result is null. Information only, status never uses it.
        /// </summary>
        public static float? ComputeSelfDischarge(IReadOnlyList<VoltageSample> samples, DateTime chargeEndedAt)
        {
            var day0 = chargeEndedAt.AddHours(SettledEndHours);
            var start = MedianBetween(samples, day0.AddDays(SelfDischargeStartFromDay), day0.AddDays(SelfDischargeStartToDay));
            var end = MedianBetween(samples, day0.AddDays(SelfDischargeEndFromDay), day0.AddDays(SelfDischargeEndToDay));
            if (start == null || end == null) return null;
            return (start.Value - end.Value) / start.Value / SelfDischargeSpanDays * 7f * 100f;
        }

        private static float? MedianBetween(IReadOnlyList<VoltageSample> samples, DateTime from, DateTime to)
        {
            var values = samples
                .Where(s => s.Timestamp >= from && s.Timestamp < to)
                .Select(s => s.Value)
                .OrderBy(v => v)
                .ToList();
            return values.Count < SelfDischargeMinReadings ? null : Median(values);
        }

        /// <summary>
        /// Cycle-anchored slope: linear least-squares fit across the settled
        /// voltages of full charges. Every anchor is taken at a comparable
        /// state of charge, so drift between anchors reflects the battery and
        /// not how long it has rested.
        ///
        /// Returns null if fewer than <paramref name="minAnchors"/> anchors
        /// exist, since a one-off value cannot show a trend.
        /// </summary>
        public static float? ComputeCycleSlopePercentPerWeek(
            IReadOnlyList<VoltageSample> anchors,
            int minAnchors)
        {
            if (anchors.Count < minAnchors) return null;
            return ComputeSlopePercentPerWeek(anchors);
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

        public record HealthClassification(string Status, string Reason, float? DropPct, float? SlopePctPerWeek);

        /// <summary>
        /// Status from the settled voltages of full charges, oldest first. S1 is
        /// the drop of the latest against the median of the earlier ones. S2 is
        /// the slope across all of them. Worst wins. Fewer than 2 is "learning".
        /// </summary>
        public static HealthClassification ClassifyStatus(IReadOnlyList<VoltageSample> anchors)
        {
            var slope = ComputeCycleSlopePercentPerWeek(anchors, S2MinAnchors);
            if (anchors.Count < 2)
                return new HealthClassification("learning", $"{anchors.Count} settled full charge(s), needs 2 to compare", null, slope);

            // Median of earlier charges, so one unusually high anchor cannot set the reference.
            var reference = Median(anchors.Take(anchors.Count - 1).Select(a => a.Value).OrderBy(v => v).ToList());
            var drop = Math.Max(0f, (reference - anchors[^1].Value) / reference * 100f);
            var s1 = drop <= S1AttentionDropPct ? 0 : drop <= S1ReplaceDropPct ? 1 : 2;

            var decline = slope < 0 ? -slope.Value : 0f;
            var s2 = decline <= S2AttentionSlopePctWeek ? 0 : decline <= S2ReplaceSlopePctWeek ? 1 : 2;

            var status = Math.Max(s1, s2) switch
            {
                0 => "good",
                1 => "attention",
                _ => "replace"
            };
            return new HealthClassification(status, $"settled drop {drop:F1}%, decline {decline:F2}%/wk", drop, slope);
        }
    }
}
