# Legitimate Interest Assessment (LIA) & Re-Identification Assessment

**Subject:** Retention of suspended-sensor telemetry, and anonymization of telemetry for long-term ML use, in the Garge platform.

> **DRAFT — requires DPO / legal counsel sign-off before launch.** This is an engineering-prepared assessment to support that review; it is not legal advice. Garge is operated for users in Norway / the EEA; the relevant supervisory authority is **Datatilsynet**. Frameworks used: GDPR Arts. 5, 6, 13, 14, 17, 25, 30; Recital 26; WP29 Opinion 05/2014 on Anonymisation Techniques; EDPB guidance on legitimate interest.

| | |
|---|---|
| **Prepared** | 2026-05-20 |
| **Revised** | 2026-05-21 — Activity A retention changed from a fixed 6-month cap to claim-lifetime retention under legitimate interest with an Art. 21 opt-out |
| **Prepared by** | Engineering (garge-api) |
| **Status** | Draft — pending DPO sign-off |
| **Next review** | 2027-05-20 (or on any change to the retention/anonymization design) |
| **Controller** | Garge (self-hosted; no upstream cloud processor for telemetry) |

---

## 1. Scope & data

Two processing activities are assessed:

- **A — Suspended-sensor retention.** When a user cancels/downgrades and owns more sensors than their plan covers, access to the excess sensors is suspended, but their telemetry continues to be collected and stored. By **default it is kept for the lifetime of the claim** (for as long as the user still owns the sensor), so a returning subscriber — e.g. a seasonal motorcyclist who cancels each summer — regains their full year-over-year history. The user may **object at any time** via a data-retention opt-out (Art. 21); once a user has opted out *and* no longer has any subscription coverage, the data is purged after a **6-month** grace.
- **B — Anonymization for ML.** On the opt-out purge, on GDPR erasure, and on account deletion, the relevant telemetry is moved into an **anonymized store** with no link back to the user or device, and retained indefinitely for analytics / model development (e.g. battery-health algorithms).

**Data categories:** environmental/device telemetry — temperature, humidity, battery voltage, switch on/off state — with timestamps. Low sensitivity: no special-category data (Art. 9), no location, no direct profiling of individuals. Personal only by virtue of being linked to a user via the `UserSensor` / `SensorOwnershipPeriod` mapping (the raw `SensorData`/`SwitchData` rows contain no user identifier).

---

## 2. Activity A — Legitimate Interest Assessment (suspended-sensor retention)

### 2.1 Purpose test
**Interest:** Retain a suspended sensor's telemetry while the user still owns the sensor, so a returning subscriber regains their full history — including **year-over-year comparison** across seasonal usage gaps — and so no irreversible data loss occurs for users who lapse briefly (failed payment, temporary downgrade) or seasonally (a motorcyclist who cancels each off-season). This is a real, specific, present interest of the controller **and a direct benefit to the user** (it is their own history). **Lawful basis: Art. 6(1)(f) legitimate interest** — *not* contract (6(1)(b)), because during suspension the service for that sensor is withheld, so retention is not "necessary to perform the contract"; and *not* consent, because a default-on retention disclosed in the privacy policy with a withdrawal/opt-out is the legitimate-interest + Art. 21 model, not the opt-in, unbundled, affirmative-act model that valid consent requires.

### 2.2 Necessity test
Restoring a user's history is not possible without retaining the rows; deleting on suspension and re-collecting later cannot reconstruct it, and year-over-year comparison is inherently long-horizon, so a short fixed cap would defeat the purpose for the very users it benefits. Retention is therefore necessary for the stated purpose. Proportionality is preserved not by a blanket time cap but by **binding retention to the ownership claim** (it ends when the user unclaims/sells or deletes their account) and by an **opt-out** for users who do not want it. No less-intrusive alternative delivers the same outcome.

### 2.3 Balancing test
- **Nature of data:** low sensitivity (environmental/device), no special categories, no profiling.
- **Reasonable expectations:** the user is told in the privacy policy, and at the point of cancel/downgrade (just-in-time notice), that their sensor history is kept so they can resume/compare later, and that they can switch this off. Processing is therefore **not a surprise** and matches what an owner of the physical device would expect.
- **Impact on the individual:** minimal — the data is not used against them; it sits dormant pending possible restore, and the data subject controls it.
- **Safeguards (decisive):** retention is **bounded to the lifetime of the claim** (unclaim/sale/account-deletion all end it and trigger anonymization); the user has an **opt-out / right to object** (Art. 21) that, once exercised, makes their suspended data eligible for a **6-month-then-anonymize** purge after coverage lapses; the user can **export or delete** the data at any time. The combination of *claim-bounded retention + opt-out + export/delete* is what makes the balance pass and is **load-bearing** — an unbounded retention with no opt-out and no claim boundary would NOT pass.

**Conclusion (A):** Legitimate interest is an appropriate basis **provided** the opt-out, the claim-boundary anonymization, and the export/delete affordances are genuinely in place (they are — see §5). Because retention now defaults to the lifetime of the claim rather than a short cap, the **balancing rests on the opt-out and the claim boundary**, which the DPO should weigh explicitly (see §6).

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
| Sensor/switch telemetry | Suspended (over-quota), user **not** opted out | Lifetime of the claim (until unclaim/sale/account deletion), then anonymized | Legitimate interest 6(1)(f) |
| Sensor/switch telemetry | Suspended, user **opted out** + no coverage | ≤ 6 months from the later of opt-out / coverage lapse, then anonymized | Legitimate interest ends on objection (Art. 21) |
| Anonymized telemetry (ML store) | Anonymized | Indefinite | Out of scope (anonymous) — *contingent on §3* |
| Derived battery health/charge events | — | Regenerated from raw voltage; not separately retained in ML store | — |
| Application logs | — | 90 days | Legitimate interest |
| Database backups | — | up to 12 months (yearly snapshot) | **flagged §3.4** |

---

## 5. Safeguards & technical measures (implemented)

- **Right to object (opt-out)** — `User.DataRetentionOptOutAt` set/cleared via `GET`/`PUT /api/users/{id}/data-retention`; surfaced as a profile toggle and at cancel/downgrade. This is the Art. 21 control the balancing test relies on.
- **Opt-out cap purge** — `SuspendedSensorPurgeService` (daily) force-unclaims and anonymizes sensors suspended > 180 days **only** for owners who have opted out **and** have no subscription coverage. Default (not opted out) sensors are kept for the lifetime of the claim.
- **Claim-boundary anonymization** — unclaim/sale (`SensorOwnershipPeriod` close) and account deletion move the user's exclusive telemetry into the anonymized store, so personal data does not outlive the claim.
- **Anonymization routine** — `AnonymizationService`: surrogate-keyed series, no reverse map, exclusive-window only (co-owners preserved), regenerable battery data dropped.
- **Resale/ownership window** — `SensorOwnershipPeriod` / `SwitchOwnershipPeriod` bound reads to the caller's ownership window; a new owner cannot see a previous owner's history.
- **Data-subject rights decoupled from suspension** — export (`ExportData`) and unclaim/delete are **never** gated by the suspension 403; suspended data remains exportable and erasable.
- **Erasure** — account deletion anonymizes the user's exclusive telemetry and removes all per-user rows (incl. photos; orphan bug fixed).
- **Transparency** — privacy policy retention clause + legal-basis section updated; just-in-time notice in cancel/downgrade flows.

## 6. Outstanding actions for DPO / counsel
1. **Sign off** the legitimate-interest basis (A) and the anonymization claim (B), or direct the fallback to aggregate-at-cap.
2. **Weigh the claim-lifetime default (§2.3):** confirm that *claim-bounded retention + Art. 21 opt-out + export/delete* is sufficient to keep the balance, now that retention defaults to the lifetime of the claim rather than a fixed 6-month cap. If not, set a maximum dormancy ceiling (e.g. anonymize after N years of no active subscription even without opt-out).
3. **Backup retention (§3.4):** reconcile the 12-month yearly snapshot with the anonymization (mapping) horizon (shorten, or document re-anonymization on restore).
4. **Motivated-intruder test (§3.5):** confirm residual singling-out risk is acceptable given absolute timestamps + per-device series.
5. Confirm whether **active-sensor** default retention ("until unclaim") needs an Art. 25 ceiling.
6. Confirm the **opt-out is the correct mechanism** (right to object under Art. 21) rather than consent, given retention is default-on and disclosed in the privacy policy.
7. Update the **Art. 30 Records of Processing** to include "suspended-telemetry retention", "claim-lifetime retention with opt-out", and "anonymization for ML" as activities.
