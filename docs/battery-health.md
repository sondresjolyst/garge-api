# Battery health rules

This document lists the rules the battery health analyzer applies to a voltage sensor. Update it whenever a rule or threshold changes.

The rules live in `Helpers/BatteryHealthMath.cs` (thresholds and calculations) and `Services/BatteryHealthAnalyzerService.cs` (windows and orchestration).

## When analysis runs

| Trigger | Rule |
|---|---|
| New reading | Each new voltage reading analyzes the sensor, at most once per 15 minutes per sensor. |
| Fallback sweep | Every hour, sensors whose latest result is over 90 minutes old, or missing, are analyzed. |
| Admin | `POST /api/battery-health/admin/reanalyze` analyzes every voltage sensor. `POST /api/battery-health/admin/reanalyze/{sensorId}` analyzes one. |

Each run reads the sensor's readings from the last 90 days and adds a new `BatteryHealthData` row. A run stops without a row if the sensor has no valid readings in that window.

## Valid readings

Readings below 5 V, above 20 V, or that cannot be parsed are ignored.

## Resting voltage

Resting voltage is the median of the lowest 25% of readings in a window. At least 1 reading is used.

| Value | Window | Used for |
|---|---|---|
| Long resting voltage | 14 days | Charge detection baseline, `PeakResting` checkpoints |
| Current resting voltage | 3 days | Stored as `RestingMedian`, drop from peak |
| `PeakResting` | Highest long resting voltage, checked daily over 90 days | Drop from peak, on-charger check |

## Charge events

All limits are ratios of the long resting voltage at the time of the run.

| Rule | Value | Constant |
|---|---|---|
| A charge starts above | 1.04 × resting | `ChargeEntryRatio` |
| A charge ends below, for the sustain time | 1.03 × resting | `ChargeExitRatio` |
| Sustain time below the end limit | 30 minutes | `ChargeExitSustainMinutes` |
| Minimum length to count | 60 minutes | `ChargeMinMinutes` |
| Minimum peak to count | 1.08 × resting | `ChargePeakMinRatio` |

- The end time is the first reading below the end limit.
- A charge still in progress at the latest reading is not reported as an event.
- A detected event is saved to `BatteryChargeEvents` unless a row with the same sensor and `StartedAt` exists.
- `LastFullChargeAt` and `LastFullChargePeak` come from the latest event detected in this run.
- `FullChargesLast30d` counts events detected in this run that ended in the last 30 days.

## On charger

`OnChargerNow` is true when the last 3 readings are all at least 1.02 × `PeakResting` (`OnChargerRatio`).

## Settled voltage

Settled voltage is the median of readings from 12 to 36 hours after a charge ended. It needs at least 3 readings. It gives a comparable measurement after each charge.

## Status

The status is one of `learning`, `good`, `attention` or `replace`. Rules apply in this order:

1. Under 14 days of data in the window gives `learning` (`LearningMinDays`).
2. No charge event ending in the last 90 days gives `learning`.
3. Otherwise, the worst of the three signals below sets the status.

| Signal | Input | `good` | `attention` | `replace` | Constants |
|---|---|---|---|---|---|
| S1, drop from peak | (`PeakResting` − current resting voltage) ÷ `PeakResting` | Up to 2% | Up to 5% | Over 5% | `S1AttentionDropPct`, `S1ReplaceDropPct` |
| S2, cycle slope | Decline per week across settled voltages, fitted by least squares | Up to 0.1% | Up to 0.3% | Over 0.3% | `S2AttentionSlopePctWeek`, `S2ReplaceSlopePctWeek` |
| S3, charge acceptance | Highest peak ÷ long resting voltage among recent events | At least 1.10 | At least 1.04 | Under 1.04 | `S3GoodAcceptance`, `S3ReplaceAcceptance` |

- S1 never goes below 0%.
- S2 needs at least 3 settled voltages (`S2MinAnchors`). With fewer, it does not affect the status. A rising slope counts as no decline.
- S3 does not affect the status when there is no recent event.

## Stored fields

| `BatteryHealthData` field | Meaning |
|---|---|
| `Status` | Result of the status rules |
| `CurrentVoltage` | Latest valid reading |
| `RestingMedian` | Current resting voltage (3 days) |
| `PeakResting` | Highest long resting voltage over 90 days |
| `OnChargerNow` | Result of the on-charger rule |
| `LastFullChargeAt`, `LastFullChargePeak` | End time and peak of the latest detected event |
| `VoltageMin24h` | Lowest valid reading in the last 24 hours |
| `FullChargesLast30d` | Events detected in this run that ended in the last 30 days |
| `DailyDropPctPerWeek` | S2 cycle slope in percent per week, negative when falling |
| `ChargeAcceptanceRatio` | S3 input |

## In the app

`garge-app` shows "Charging now" for last full charge when `OnChargerNow` is true. Otherwise it shows the time since `LastFullChargeAt`, or "None detected".
