# Legitimate Interest Assessment (LIA) & Re-Identification Assessment

**Subject:** Retention of suspended-sensor telemetry, and anonymization of telemetry for long-term ML use, in the Garge platform.

> **DRAFT — requires DPO / legal counsel sign-off before launch.** This is an engineering-prepared assessment to support that review; it is not legal advice. Garge is operated for users in Norway / the EEA; the relevant supervisory authority is **Datatilsynet**. Frameworks used: GDPR Arts. 5, 6, 13, 14, 17, 25, 30; Recital 26; WP29 Opinion 05/2014 on Anonymisation Techniques; EDPB guidance on legitimate interest.

| | |
|---|---|
| **Prepared** | 2026-05-20 |
| **Prepared by** | Engineering (garge-api) |
| **Status** | Draft — pending DPO sign-off |
| **Next review** | 2027-05-20 (or on any change to the retention/anonymization design) |
| **Controller** | Garge (self-hosted; no upstream cloud processor for telemetry) |

---

## 1. Scope & data

Two processing activities are assessed:

- **A — Suspended-sensor retention.** When a user cancels/downgrades and owns more sensors than their plan covers, access to the excess sensors is suspended, but their telemetry continues to be collected and stored, **for up to 6 months**, so the user's full history can be restored instantly if they re-subscribe.
- **B — Anonymization for ML.** At the 6-month cap (and on GDPR erasure / account deletion), the relevant telemetry is moved into an **anonymized store** with no link back to the user or device, and retained indefinitely for analytics / model development (e.g. battery-health algorithms).

**Data categories:** environmental/device telemetry — temperature, humidity, battery voltage, switch on/off state — with timestamps. Low sensitivity: no special-category data (Art. 9), no location, no direct profiling of individuals. Personal only by virtue of being linked to a user via the `UserSensor` / `SensorOwnershipPeriod` mapping (the raw `SensorData`/`SwitchData` rows contain no user identifier).

---

## 2. Activity A — Legitimate Interest Assessment (suspended-sensor retention)

### 2.1 Purpose test
**Interest:** Retain a suspended sensor's recent telemetry for a bounded period so a returning subscriber regains their full history immediately, reducing churn friction and avoiding irreversible data loss for users who lapse briefly (failed payment, temporary downgrade). This is a real, specific, present interest of the controller and a benefit to the user. **Lawful basis: Art. 6(1)(f) legitimate interest** — *not* contract (6(1)(b)), because during suspension the service for that sensor is withheld, so retention is not "necessary to perform the contract".

### 2.2 Necessity test
Instant restore is not possible without retaining the rows; deleting on suspension and re-collecting later cannot reconstruct history. Retention is therefore necessary for the stated purpose — but only for a **bounded** window, which is what keeps it proportionate. No less-intrusive alternative delivers the same outcome.

### 2.3 Balancing test
- **Nature of data:** low sensitivity (environmental/device), no special categories, no profiling.
- **Reasonable expectations:** the user is told at the point of cancel/downgrade (just-in-time notice) and in the privacy policy that suspended-device data is kept for 6 months then deleted/anonymized. Processing is therefore **not a surprise**.
- **Impact on the individual:** minimal — the data is not used against them; it sits dormant pending possible restore.
- **Safeguards (decisive):** a hard **6-month cap** with automated purge; the user can **export or delete** the data at any time during suspension; access controls unchanged; the cap-driven purge is what makes the balance pass. **Indefinite retention of identifiable suspended data would NOT pass** — the cap is load-bearing.

**Conclusion (A):** Legitimate interest is an appropriate basis **provided** the 6-month cap and the export/delete affordances are genuinely in place (they are — see §5).

---

## 3. Activity B — Re-Identification Assessment (anonymized ML store)

Claim: at the cap (or on erasure), telemetry is rendered **anonymous** (Recital 26) and falls outside GDPR, so it may be retained indefinitely for ML. This holds **only if** re-identification is not reasonably likely by any means available to the controller or a third party. Assessed against the three WP29 risks:

### 3.1 Singling out
A per-device time series can in principle fingerprint a household (usage patterns). Mitigations: series are stored under a **fresh surrogate key with no stored mapping** to the original `SensorId`; series are **kept independent** (never cross-linked by site/gateway/`ParentName`), preventing multi-sensor household fingerprints. **Residual risk: low** for this low-sensitivity data, but **non-zero** because absolute timestamps are retained (a deliberate utility choice). → see Residual Risk & required controls.

### 3.2 Linkability
The link to a person is severed by deleting/orphaning every vector that ties `SensorId`/`SwitchId` to a user: the `UserSensor`/`SensorOwnershipPeriod` rows, custom names, sensor activities (notes/odometer), photos, offline-notification rows, and (for re-claim) by assigning a **fresh device identity** so the same physical device cannot rejoin the anonymized data. **No `SeriesId → SensorId` map is stored.**

### 3.3 Inference
No profiling or inference about individuals is performed on the anonymized set; it is used for aggregate model development.

### 3.4 Out-of-database vectors (must be controlled — see §5)
- **Application logs** pair `CallerUserId + SensorId + RegistrationCode` (claim/unclaim). These reconstruct the mapping if retained past the analytics horizon → **log retention must be ≤ 6 months** (current policy: 90 days — compliant).
- **Database backups** taken before anonymization still contain the mapping → backup retention must be bounded and restores must re-run anonymization. (Current backup policy retains a yearly snapshot up to 12 months — **flagged for DPO**: this exceeds the 6-month mapping horizon; either shorten, or document that restored backups are re-anonymized.)

### 3.5 Residual risk & required controls
Because **absolute timestamps** and a **per-device** (not aggregated) series are retained for ML utility, the data is best characterised as **strongly pseudonymized trending toward anonymous**, not trivially anonymous. To defend the "anonymous, keep-forever" position, the following must hold and be documented:
- fresh surrogate key, no reverse map (**implemented**);
- independent series, no cross-linking (**implemented**);
- logs + backups within the mapping horizon (**logs OK; backups flagged**);
- a documented **motivated-intruder test** concluding re-identification is not reasonably likely (**DPO to confirm**).

If the DPO judges residual singling-out risk too high, fall back to **aggregate-at-cap** (cohort statistics, drop per-device rows) — the schema supports this without migration.

---

## 4. Retention schedule

| Data | State | Retention | Basis |
|---|---|---|---|
| Sensor/switch telemetry | Active device | Until unclaim / account deletion | Contract 6(1)(b) |
| Sensor/switch telemetry | Suspended (over-quota) | ≤ 6 months, then deleted or anonymized | Legitimate interest 6(1)(f) |
| Anonymized telemetry (ML store) | Anonymized | Indefinite | Out of scope (anonymous) — *contingent on §3* |
| Derived battery health/charge events | — | Regenerated from raw voltage; not separately retained in ML store | — |
| Application logs | — | 90 days | Legitimate interest |
| Database backups | — | up to 12 months (yearly snapshot) | **flagged §3.4** |

---

## 5. Safeguards & technical measures (implemented)

- **6-month cap purge** — `SuspendedSensorPurgeService` (daily) force-unclaims and anonymizes sensors suspended > 180 days.
- **Anonymization routine** — `AnonymizationService`: surrogate-keyed series, no reverse map, exclusive-window only (co-owners preserved), regenerable battery data dropped.
- **Resale/ownership window** — `SensorOwnershipPeriod` / `SwitchOwnershipPeriod` bound reads to the caller's ownership window; a new owner cannot see a previous owner's history.
- **Data-subject rights decoupled from suspension** — export (`ExportData`) and unclaim/delete are **never** gated by the suspension 403; suspended data remains exportable and erasable.
- **Erasure** — account deletion anonymizes the user's exclusive telemetry and removes all per-user rows (incl. photos; orphan bug fixed).
- **Transparency** — privacy policy retention clause + legal-basis section updated; just-in-time notice in cancel/downgrade flows.

## 6. Outstanding actions for DPO / counsel
1. **Sign off** the legitimate-interest basis (A) and the anonymization claim (B), or direct the fallback to aggregate-at-cap.
2. **Backup retention (§3.4):** reconcile the 12-month yearly snapshot with the 6-month mapping horizon (shorten, or document re-anonymization on restore).
3. **Motivated-intruder test (§3.5):** confirm residual singling-out risk is acceptable given absolute timestamps + per-device series.
4. Confirm whether **active-sensor** default retention ("until unclaim") needs an Art. 25 ceiling.
5. Update the **Art. 30 Records of Processing** to include "suspended-telemetry retention" and "anonymization for ML" as activities.
