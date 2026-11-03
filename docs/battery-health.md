# Battery charge detection and health

This document describes how the API turns voltage readings into charges and a battery status. Read it
before you change `Helpers/BatteryHealthMath.cs` or `Services/BatteryHealthAnalyzerService.cs`.

Update this document in the same change whenever a rule or a value changes.

## Why the rules use ratios

Sensors differ in calibration, so the same battery can read differently on two sensors. A fixed voltage
limit would therefore mean different things on different sensors. Detection compares a battery only with
itself and uses ratios of its own earlier readings. Temperature and humidity play no part.

The few fixed values in millivolts describe the shape of a change between neighbouring readings. They do
not depend on the voltage level, so a calibration offset does not move them.

## When analysis runs

`BatteryHealthAnalyzerService` analyzes a voltage sensor at these times:

- **After a new reading.** A database trigger notifies the API, and a sensor is analyzed at most once every
  15 minutes.
- **In an hourly sweep.** The sweep analyzes a sensor whose latest health row is older than 90 minutes or
  missing. This covers missed notifications.
- **On an admin request.** `POST /api/battery-health/admin/reanalyze` handles all voltage sensors and
  `POST /api/battery-health/admin/reanalyze/{sensorId}` handles one. Both rebuild from the whole history.

Each run adds one row to `BatteryHealthData`, unless the sensor is offline. The latest row is what clients
read.

## Valid readings

A reading counts when it parses as a number from 5 to 20 V. Anything else comes from sensor start-up,
disconnects or wiring faults, and analysis ignores it.

## Detecting a charge

All constants are in `BatteryHealthMath`. Detection handles readings in time order, and every decision uses
only readings up to that point.

### Baseline

The baseline is what the battery reads at rest, and a charge start is measured against it.

| Rule | Value | Constant |
|---|---|---|
| Low quartile (25th percentile) of the rest readings in a window | 7 days | `BaselineWindowDays` |
| Rest readings needed for the low quartile | at least 3 | `BaselineMinReadings` |
| After a charge, the baseline is the lowest settling reading | 12 hours | `SettleHours` |
| After that, the baseline is capped at that settling low | 7 days after the charge | `BaselineCapDays` |

With too few rest readings, the baseline is the settling low if a charge has ended. Otherwise it is the
latest rest reading. The baseline freezes when a charge starts and is stored as `RestingAtTime`.

Rest readings do not carry across a charge. Readings during the settling time do not count as rest readings.

### Charge start

A charge starts at 2 readings in a row (`ChargeConfirmReadings`) at or above 1.03 x baseline
(`ChargeStartRatio`). The start time is the first of them. One high reading alone is ignored.

### Plateau

The plateau is the highest lower median of the last 12 charge readings (`PlateauWindowReadings`), once at
least 6 exist (`PlateauMinReadings`). A spike early in a charge cannot set it.

### Charge end

A charge ends at 2 readings in a row (`ChargeConfirmReadings`) below 0.965 x plateau (`ChargeEndRatio`) with
the voltage not climbing. A rise over 5 mV (`ChargeEndClimbVolts`) between them restarts the count. The end
time is the first of them. A charge has no plateau before it has 6 readings, so it cannot end before that.

### Same session

A new charge continues the last one in either of these cases:

- It starts within 24 hours of the end (`SessionMergeHours`).
- It starts within 72 hours of the end (`SlowClimbHours`), and its first reading rose less than 100 mV from
  the previous reading (`SlowClimbMaxRiseVolts`). This is a slow climb.

The session keeps its start and its frozen baseline. A sharp rise after 24 hours is a new charge.

### Slow climb then drop

A resting battery does not climb. A slow rise that ends in a sudden drop means a low-current charger stayed
connected until the drop. Within 72 hours of a charge ending (`SlowClimbHours`), the end moves to a reading
when both of these hold:

- The reading is at least 100 mV below the previous reading (`SlowClimbDropVolts`).
- The previous reading is at least 50 mV above the settling low (`SlowClimbAboveLowVolts`).

The settling time then starts again from that reading.

### Full charge

A charge is full with at least 6 hours (`FullHoldHours`) at or above 0.98 x plateau (`FullHoldRatio`). Time
between 2 readings more than 3 hours apart (`FullHoldMaxGapHours`) does not count, so a gap in the readings
cannot add hold time. A charge that is still open is never full.

### Settled voltage

The settled voltage is the median of the readings 12 to 36 hours after the end (`SettleHours`,
`SettledEndHours`). The window stops at the next charge start. It needs at least 3 readings
(`SettledMinReadings`), otherwise the value is null. This gives a comparable state of charge across charges.

### On charger now

`OnChargerNow` is true when a charge is open at the latest reading.

## Resting voltage

`RestingMedian` is the median of the bottom quartile of the readings in the last 3 days
(`RestingWindowDays`). It leaves out charging and the 12 hours after it. It is null when no such readings
exist and is never stored as 0.

## Offline sensors

A sensor is offline when its latest reading is more than 3 days old (`OfflineAfter`). A run then adds no
health row and leaves the last one standing, so a quiet sensor keeps its status. Charge rows stay untouched
too. The check reads only the latest timestamp, so the sweep stays cheap for these sensors.

A sensor whose latest readings are all invalid also counts as offline, after a run has read its readings.

An admin rebuild still replaces the charge rows of an offline sensor, but adds no health row.

## Status

Status uses full charges with a settled voltage, oldest first. These are called anchors below.

- **Fewer than 2 anchors:** `learning`.
- **S1, drop:** `max(0, (median of earlier settled voltages - latest settled) / that median x 100)`. Up to 2 %
  is good (`S1AttentionDropPct`), up to 5 % is attention (`S1ReplaceDropPct`), above that is replace.
- **S2, slope:** the least-squares slope of the anchors, in percent of their mean per week, stored as
  `DailyDropPctPerWeek`. It needs at least 3 anchors (`S2MinAnchors`). A decline up to 0.1 % per week is good
  (`S2AttentionSlopePctWeek`), up to 0.3 % is attention (`S2ReplaceSlopePctWeek`), above that is replace.

The worst signal wins.

The status limits are starting values. They have not yet been tested on a failing battery.

## Self-discharge

`SelfDischargePctPerWeek` is for information only and never affects status.

It measures the latest rest of at least 15 days (`SelfDischargeMinRestDays`) after a full charge. A rest ends
at the next charge start or at the latest reading. Day 0 is 36 hours after the charge ended
(`SettledEndHours`). A rest after a partial charge is skipped, since the battery did not start from the same
state of charge.

- `v0` is the median of the readings in days 2 to 4 (`SelfDischargeStartFromDay`, `SelfDischargeStartToDay`).
- `v1` is the median of the readings in days 13 to 15 (`SelfDischargeEndFromDay`, `SelfDischargeEndToDay`).
- Each window needs at least 6 readings (`SelfDischargeMinReadings`), otherwise the value is null.
- The value is `(v0 - v1) / v0 / 11 x 7 x 100` (`SelfDischargeSpanDays`).

## What is stored

`BatteryChargeEvents` is the saved charge history. A charge that is still open is not stored.

| Field | Meaning |
|---|---|
| `StartedAt`, `EndedAt`, `DurationMinutes` | When the charge ran. Unique per sensor by `StartedAt`. |
| `PeakVoltage` | The highest reading in the charge. |
| `RestingAtTime` | The baseline frozen at the start. |
| `IsFull` | The full charge rule was met. |
| `SettledVoltage` | The settled voltage, or null. |

`BatteryHealthData` holds one row per run.

| Field | Meaning |
|---|---|
| `Status` | `learning`, `good`, `attention` or `replace`. |
| `CurrentVoltage` | The latest valid reading. |
| `RestingMedian` | The resting voltage, or null. |
| `OnChargerNow` | A charge is open at the latest reading. |
| `LastFullChargeAt`, `LastFullChargePeak` | The end and peak of the latest full charge. |
| `VoltageMin24h` | The lowest valid reading in the last 24 hours. |
| `FullChargesLast30d` | Full charges that ended in the last 30 days. |
| `DailyDropPctPerWeek` | The S2 slope, or null. |
| `SelfDischargePctPerWeek` | The self-discharge, or null. |

## How a run replays

A stored charge is final when it ended more than 5 days ago (`FinalAfter`). This is longer than the 72 hours
a later reading can reach back.

1. A run reads readings from the end of the latest final charge, or the whole history when none exists.
2. It replays detection from there, with the settling state seeded from that end.
3. It makes the rows after the final charge match the result, by updating, inserting and deleting. Rows match
   on `StartedAt`, so a charge whose start moved leaves no duplicate.
4. It also refreshes the settled voltage of the final charge, which a late run may not have had readings for.

If a replay finds that a later reading changes the final charge, or the reading at its end is gone, the run
replays the whole history instead. This can happen after the service was down past the 5-day limit.

`LastFullChargeAt`, `LastFullChargePeak`, `FullChargesLast30d`, status and slope come from the stored full
charges plus this run, so history older than the readings read is kept.

A run that resumes must give the same charges as one full replay. `IncrementalRuns_EqualOneFullReplay` tests
this on real-shaped traces under `garge-api.Tests/Fixtures/Battery`. Readings that arrive late, with a time
before the final charge, only count after an admin rebuild.

An admin rebuild replaces the sensor's charge rows with a full replay of its whole history.
