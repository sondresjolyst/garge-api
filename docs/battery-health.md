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
  missing, if a reading has arrived since that row. This covers missed notifications, and a quiet sensor costs
  only two timestamp lookups.
- **On an admin request.** `POST /api/battery-health/admin/reanalyze` handles all voltage sensors and
  `POST /api/battery-health/admin/reanalyze/{sensorId}` handles one. Both rebuild from the whole history.

A run replays charges and then adds one row to `BatteryHealthData` when the sensor is online and a valid reading
has arrived since its latest row. A sensor with no row yet gets one if it is online. This holds on every path,
including an admin rebuild. The row's `Timestamp` is the time of the reading it holds, not of the run. `GET /api/battery-health/name/{sensorName}/latest`
reads the latest row inside the caller's periods and works out the rest as described in
[Health across owners](#health-across-owners).

## Valid readings

A reading counts when it parses as a finite number from 5 to 20 V. Anything else comes from sensor start-up,
disconnects or wiring faults, and analysis ignores it. `NaN` and infinite values are not valid.

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
latest rest reading, however old. The baseline freezes when a charge starts and is stored as `RestingAtTime`.

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

A sensor is offline when its latest reading is more than 3 days old (`OfflineAfter`). An automatic run, from
a new reading or the sweep, then adds no health row and leaves the charge rows untouched. The check reads only
the latest timestamp, so the sweep stays cheap for these sensors. A quiet sensor keeps its status, since status
comes from its stored charges.

A sensor whose latest readings are all invalid also counts as offline, after a run has read its readings.

An admin rebuild replaces the charge rows of an offline sensor and adds no health row. Its status at read time
comes from the rebuilt charges.

## Health across owners

Battery health follows each viewer's own ownership periods, exactly like sensor data. A user who owned a sensor
all of 2025, not in 2026, and again all of 2027 sees 2025 and 2027 and nothing from 2026.

Detection runs across the whole sensor history, and nothing that depends on the viewer is stored. A health row
holds only reading facts. `BatteryHealthReadService` works out the rest at read time from the stored charges
inside the caller's periods in `SensorOwnershipPeriods`. Two viewers of one sensor can therefore get different
results from one row. A period holds for a time from its `StartedAt` up to, but not including, its `EndedAt`.

- **A charge** counts only when it starts and ends inside one of the viewer's periods. A charge that crosses a
  period boundary counts for nobody on that side. `GET /api/battery-health/name/{sensorName}/events` applies the
  same rule.
- **A settled voltage** counts only when 36 hours after the charge end is also inside that period. Otherwise the
  charge still counts, but not as an anchor for status.
- **A self-discharge value** counts only when its measured rest, up to day 15, is also inside that period.
- **`RestingMedian`** is null for a viewer when the row's 3-day window reaches before their period start.
- **`VoltageMin24h`** is null for a viewer when the row's 24-hour window reaches before their period start.
- **`CurrentVoltage` and `OnChargerNow`** come from the row itself. A row is timed by its reading, so the ownership
  filter on `Timestamp` leaves out any row whose reading came before the viewer's period. A viewer who claims a
  quiet sensor has no row to read until a new reading arrives, so `GET latest` returns 204 with no body until then.
  `OnChargerNow` reads as false when the row is older than `OfflineAfter`, for every viewer.
- **`RestingAtTime`** in `GET events` is shown to admins only, and the data export never holds it, since a baseline
  can come from readings of any age, also from an earlier owner.

`GET /api/battery-health/name/{sensorName}/events` also leaves out a settled voltage or self-discharge value that
does not count for the caller. An admin sees every value.

`OnChargerNow` states whether a charge is open at the row's reading, also when that charge began before the viewer's
period. It holds no voltage value. Whether a charge inside a period is detected can depend on readings before it, while
no such value is shown.

A share recipient follows their own period the same way, so an owner and a co-owner can see different statuses on
one sensor. A former owner keeps their own period's data only as long as they still have access to the sensor,
which is the same access check as for sensor data. An admin sees the whole history with no limit.

`FullChargesLast30d` is counted at read time, so it stays current for an offline sensor too.

The data export holds the viewer's reading facts and their charges, limited the same way. It holds no status.

## Status

Status uses the full charges with a settled voltage that count for the viewer, oldest first. These are
called anchors below.

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

The analyzer stores a value on each full charge once the rest after it has lasted at least 15 days
(`SelfDischargeMinRestDays`). `GET latest` shows the value of the latest charge that has one and counts for the
viewer. A rest ends
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
| `RestingAtTime` | The baseline frozen at the start. Admins only in responses. |
| `IsFull` | The full charge rule was met. |
| `SettledVoltage` | The settled voltage, or null. |
| `SelfDischargePctPerWeek` | The self-discharge of the rest after this charge, or null. |

`BatteryHealthData` holds reading facts only, at most one row per new reading.

| Field | Meaning |
|---|---|
| `CurrentVoltage` | The latest valid reading. |
| `RestingMedian` | The resting voltage, or null. |
| `OnChargerNow` | A charge is open at the latest reading. |
| `VoltageMin24h` | The lowest valid reading in the last 24 hours. |
| `Timestamp` | The time of the reading the row holds. |

`GET latest` adds these for the caller at read time:

| Field | Meaning |
|---|---|
| `Status` | `learning`, `good`, `attention` or `replace`. |
| `LastFullChargeAt`, `LastFullChargePeak` | The end and peak of the latest full charge. |
| `FullChargesLast30d` | Full charges that ended in the last 30 days (`FullChargeCountDays`). |
| `DailyDropPctPerWeek` | The S2 slope, or null. |
| `SelfDischargePctPerWeek` | The self-discharge, or null. |

## How a run replays

A stored charge is final when it ended more than 5 days ago (`FinalAfter`). This is longer than the 72 hours
a later reading can reach back.

1. A run reads readings from the end of the latest final charge, or the whole history when none exists.
2. It replays detection from there, with the settling state seeded from that end.
3. It makes the rows after the final charge match the result, by updating, inserting and deleting. Rows match
   on `StartedAt`, so a charge whose start moved leaves no duplicate.
4. It also refreshes the settled voltage and self-discharge of the final charge, since its rest continues past
   the point the replay resumes from.

If a replay finds that a later reading changes the final charge, or the reading at its end is gone, the run
replays the whole history instead. This can happen after the service was down past the 5-day limit.

A run that resumes must give the same charges as one full replay. `IncrementalRuns_EqualOneFullReplay` tests
this on synthetic traces under `garge-api.Tests/Fixtures/Battery`. Readings that arrive late, with a time
before the final charge, only count after an admin rebuild.

An admin rebuild replaces the sensor's charge rows with a full replay of its whole history.

After the upgrade that adds this code, no charge rows exist, so status reads `learning` until each sensor's first
analyzer run rebuilds its charges. An online sensor does this on its next reading or sweep. An offline sensor needs
an admin reanalyze. The upgrade moves each health row to the time of its reading, and deletes a row with no valid
reading in the 3 days before its run. Down of `BatteryHealthReadingFacts` cannot restore the run times or the deleted
rows.

## Erasure

When `AnonymizationService` erases an ownership period, it moves the readings only that period covers and removes the
derived data:

- A health row inside the period that no other period covers is deleted.
- A health row in the 3 days after the period that no other period covers keeps its reading facts, but its
  `RestingMedian` and `VoltageMin24h` become null, since they were worked out from the erased readings.
- A charge that overlaps the period is deleted, unless another single period covers the whole charge.
- A kept charge loses the values measured from those readings:
  - `SettledVoltage` becomes null when its window, 12 to 36 hours after the end, overlaps the period, unless another
    single period covers that window.
  - `SelfDischargePctPerWeek` becomes null when its measured rest, day 2 to 15, overlaps the period, unless another
    single period covers that rest.
  - `RestingAtTime` becomes null when the 7 days before the start overlap the period.

The next analyzer run replays from the latest final charge that is left. It, or an admin rebuild, can find a new charge
in the readings that remain, for example the part of a charge after the erased period. That charge then holds only
remaining readings.
