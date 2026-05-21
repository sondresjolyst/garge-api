# Legitimate Interest Assessment (LIA) & Re-Identification Assessment

**Subject:** Retention of suspended-sensor telemetry, and anonymization of telemetry for long-term ML use, in the Garge platform.

> **Controller-approved, subject to the pre-launch conditions in §6.** No DPO is designated (not required — see Art. 30 §1); the controller is the sign-off authority. This is an engineering-prepared assessment, not legal advice. Garge is operated for users in Norway / the EEA; the relevant supervisory authority is **Datatilsynet**. Frameworks used: GDPR (Reg. 2016/679) Arts. 5, 6, 13, 14, 17, 20, 21, 25, 30; Recital 26; **EDPB Guidelines 1/2024 on Art. 6(1)(f) legitimate interest** (which update WP29 Opinion 06/2014); WP29 Opinion 05/2014 on Anonymisation Techniques (EDPB-endorsed; WP29 was succeeded by the EDPB on 25 May 2018). **Currency note (2026):** the EDPB is preparing new anonymisation/pseudonymisation guidelines following the CJEU *SRB* judgment (stakeholder event 12 Dec 2025); these may revise the standard relied on in §3 — see §6.

| | |
|---|---|
| **Prepared** | 2026-05-20 |
| **Revised** | 2026-05-21 — (a) Activity A retention changed from a fixed 6-month cap to claim-lifetime retention under legitimate interest with an Art. 21 opt-out; (b) citation currency pass — named EDPB Guidelines 1/2024, flagged the post-*SRB* EDPB anonymisation guidance in progress; (c) finalized with controller decisions (§ Decisions), controller identity, and the updated backup policy; (d) completed the motivated-intruder test (Appendix A) + regulatory conformity check (Appendix B) |
| **Prepared by** | Engineering (garge-api) |
| **Status** | Controller-approved; pre-launch conditions complete except one tracked dependency — EDPB Anonymisation Guidelines (pending, ~summer 2026; §6.3) |
| **Next review** | 2027-05-20 (or on any change to the retention/anonymization design) |
| **Controller** | Sjølyst Innovations (trading as Garge), org. 934 531 035, Mårvegen 21a, 4347 Lye, Norway. Privacy contact: sondresjoelyst@gmail.com. No DPO designated (not required — see Art. 30 §1). Self-hosted; no upstream cloud processor for telemetry. |
| **Related docs** | DPIA `garge-app/docs/dpia-sensor-data.md`; Records of Processing `garge-app/docs/article30.md` |

### Decisions (controller, 2026-05-21)
- **Activity B anonymisation:** keep **per-device series, indefinitely** (max ML utility). This makes the §3.5 motivated-intruder test and the post-*SRB* guidance re-check **required pre-launch conditions**, not optional.
- **Claim-lifetime retention:** **no dormancy ceiling** — data is kept for as long as the user owns the device; the Art. 21 opt-out is the only off-switch.
- **Active-device telemetry:** **no Art. 25 time cap** — retained until unclaim / account deletion (matches the live dashboard/history product expectation).
- **DPIA:** existing DPIA updated to v2 to cover this processing (see Related docs).

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
- **Database backups** taken before anonymization still contain the mapping → backup retention must be bounded. Backup policy is **3 daily / 4 weekly / 6 monthly (no yearly)** → maximum residual age ≈ **6 months**, which sits within the mapping horizon. **Resolved** (the earlier 12-month yearly-snapshot concern no longer applies); a disaster restore re-applies the anonymization sweeps on next run.

### 3.5 Residual risk & required controls
Because **absolute timestamps** and a **per-device** (not aggregated) series are retained for ML utility, the data is best characterised as **strongly pseudonymized trending toward anonymous**, not trivially anonymous. To defend the "anonymous, keep-forever" position, the following must hold and be documented:
- fresh surrogate key, no reverse map (**implemented**);
- independent series, no cross-linking (**implemented**);
- logs (90d) + backups (≤6mo) within the mapping horizon (**both OK**);
- a documented **motivated-intruder test** concluding re-identification is not reasonably likely (**completed — see Appendix A**).

If the motivated-intruder test had failed (or the forthcoming EDPB anonymisation guidance later raises the bar), the documented fallback is **aggregate-at-cap** (cohort statistics, drop per-device rows) — the schema supports this without migration.

**2026 currency:** this assessment applies the WP29 Opinion 05/2014 framework (still EDPB-endorsed). The CJEU *SRB* judgment has shifted the analysis toward a **relative** (controller-specific, means-reasonably-likely) test of identifiability, and the EDPB is drafting updated anonymisation/pseudonymisation guidelines (stakeholder event 12 Dec 2025). The "anonymous, keep-forever" position in this section is supported for now by Appendix A (motivated-intruder test) + Appendix B; it must be **re-validated against the final EDPB anonymisation guidance on publication** (~summer 2026 — tracked in §6.3). If that guidance raises the bar, fall back to aggregate-at-cap.

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
| Database backups | — | ≤ ~6 months (3 daily / 4 weekly / 6 monthly; no yearly) | Legitimate interest |

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

## 6. Status of actions

### Decided / resolved
- ✅ **Legitimate-interest basis (A)** — approved; balancing rests on claim-bounded retention + Art. 21 opt-out + export/delete (§2.3).
- ✅ **Claim-lifetime default** — no dormancy ceiling (controller decision); opt-out is the off-switch.
- ✅ **Active-sensor retention** — no Art. 25 time cap (controller decision); kept until unclaim/deletion.
- ✅ **Opt-out vs consent** — confirmed legitimate interest + Art. 21 objection (default-on, disclosed), not consent.
- ✅ **Backup retention (§3.4)** — resolved: 3 daily / 4 weekly / 6 monthly (no yearly) ≈ ≤6 months, within the mapping horizon.
- ✅ **Art. 30 Records of Processing** — updated to v2 to cover suspended/claim-lifetime retention + the anonymized ML store (`garge-app/docs/article30.md`).
- ✅ **DPIA** — updated to v2 for this processing (`garge-app/docs/dpia-sensor-data.md`).

### Pre-launch conditions — status
1. ✅ **Motivated-intruder test** — completed and documented in **Appendix A**. Conclusion: re-identification not reasonably likely for any external recipient (anonymous on release); for the controller, a time-bounded residual exists only via exact timestamp+value correlation against backups, which expires when backups rotate (≤6 months). Overall residual risk **low**; per-device keep-forever is defensible given backup access controls + the ≤6-month horizon. Aggregate-at-cap remains the documented fallback.
2. ✅ **Regulatory conformity check (available guidance)** — completed in **Appendix B**: legitimate interest mapped to **EDPB Guidelines 1/2024**; identifiability assessed under the CJEU *SRB* relative test; design checked against **EDPB Guidelines 01/2025 on Pseudonymisation**.
3. ⏳ **EDPB Anonymisation Guidelines (tracked — not yet published):** still in progress (EDPB "sprint team", expected ~summer 2026 per the April 2026 plenary). **Re-validate Appendix A/B against the final text when published.** This is the one open dependency; until then the assessment relies on WP29 05/2014 (EDPB-endorsed) + the *SRB* relative test + the Pseudonymisation Guidelines.

**Optional hardening (controller may elect):** coarsen/jitter the retained timestamps in the anonymized store to remove even the time-bounded controller-side correlation vector in Appendix A. Not required for the current conclusion; noted for the DPO.

## 7. Sign-off

| Role | Name | Date |
|---|---|---|
| Controller | Sondre Sjølyst | 2026-05-21 |

Re-review on any change to the retention/anonymization design, when the EDPB anonymisation guidelines are published, or annually (see header).

---

## Appendix A — Motivated-intruder test (anonymized ML store)

**Prepared:** 2026-05-21 (engineering-prepared; controller-accepted — not legal advice). **Scope:** the `AnonymizedSeries` / `AnonymizedReading` store only (Activity B). **Method:** the WP29 "motivated intruder" concept (Opinion 05/2014), applied under the CJEU *SRB* **relative-identifiability** standard — identifiability is judged by the *means reasonably likely to be used* by the party holding the data, not in the absolute.

### A.1 What the store actually contains
- `AnonymizedSeries`: surrogate `Id` (random, **no stored mapping** to `SensorId`/`SwitchId`/`UserId`), `SourceType` (voltage/temperature/humidity/socket), optional `CalibrationOffsetV`, `AnonymizedAt`.
- `AnonymizedReading`: `Value` (double), `Timestamp` (absolute), FK to series.
- **Not present:** any user/device/account id, name, location, billing address, `ParentName`/gateway, registration code. Series are **independent** (never cross-linked).

### A.2 Intruder profiles & means reasonably likely
| Intruder | Auxiliary data available | Can re-identify? |
|---|---|---|
| **External recipient / data leaked** | Only the de-identified series. No key, no device/location, independent series. | **No.** To link a value-time series to a person they would need to *already possess that same person's raw telemetry* (same sensor, same timestamps) — they don't, and there's no quasi-identifier (no location/postcode/DOB) to join on. Singling out a *record* is possible; identifying a *person* is not by means reasonably likely. |
| **Controller insider (live DB)** | Live personal tables exist, but anonymization **deletes the source rows in the same transaction** and stores **no join key**. Post-anonymization there is nothing to join on except value+timestamp. | **Not in the live store.** Residual vector only via backups — see below. |
| **Controller insider (backups)** | Backups taken *before* a given anonymization still contain the original `SensorData` with the same `Value`+`Timestamp`. An exact (value, timestamp) match could re-link a series to a `SensorId` → user. | **Time-bounded only.** Possible *during the backup horizon* (≤6 months: 3 daily/4 weekly/6 monthly, no yearly), then the matching source is gone permanently. Mitigated by backup encryption + RBAC; backups are restore-only, not processed. |
| **New owner of a resold device** | The live ownership-window already hides prior data; the anonymized store has no device id to query. | **No.** |

### A.3 WP29 three-risk check
- **Singling out:** per-device series + absolute timestamps can isolate a record, but with no identifier/quasi-identifier this does not identify a person. **Low.**
- **Linkability:** independent series, no surrogate→source map → two series cannot be tied to the same household. **Low.**
- **Inference:** no individual profiling; aggregate model development only. **Low.**

### A.4 Conclusion
- **Relative to any external party:** the data is **anonymous on release** (no means reasonably likely to re-identify).
- **Relative to the controller:** **strongly pseudonymised for ≤6 months** (the backup timestamp-correlation vector), becoming **anonymous once backups rotate**. The data is not used during that window; the vector is gated by encryption + RBAC.
- **Overall residual risk: LOW.** Per-device, keep-forever retention is **defensible** provided (i) backup access controls hold and (ii) the ≤6-month backup horizon is maintained — both currently true.
- **Optional hardening:** rounding/jittering retained timestamps would remove even the controller-side ≤6-month vector (would make the store anonymous to the controller immediately). Not required for this conclusion.

## Appendix B — Regulatory conformity check (guidance available May 2026)

**Prepared:** 2026-05-21.

### B.1 Legitimate interest — EDPB Guidelines 1/2024 (Art. 6(1)(f))
Mapped to the guideline's three cumulative conditions:
- **Legitimate interest pursued** — preserving the owner's own history (incl. year-over-year across seasonal gaps); real, present, specific (LIA §2.1). ✔
- **Necessity** — restoring/comparing history is impossible without retaining the rows; proportionality preserved by binding to the ownership claim + opt-out, not a blanket cap (§2.2). ✔
- **Balancing** — low-sensitivity data, reasonable expectations (disclosed in privacy policy + just-in-time notice), minimal impact, decisive safeguards: **Art. 21 opt-out**, claim-boundary anonymization, export/delete (§2.3). The guideline's emphasis on data-subject expectations + an effective right to object is satisfied. ✔
- *Status:* Guidelines 1/2024 were in final consultation/adoption as of this writing; the three-part test is stable. Re-confirm wording on formal adoption.

### B.2 Identifiability — CJEU *SRB* relative test
Applied throughout Appendix A: identifiability assessed by means reasonably likely for each holder. Result — anonymous to external parties; controller-side residual is time-bounded to the backup horizon. Consistent with the relative approach.

### B.3 Pseudonymisation — EDPB Guidelines 01/2025
The store's design (fresh surrogate key, **no stored reverse map**, independent series, source + regenerable-derived rows deleted) matches the guidelines' pseudonymisation expectations and goes further (no retained mapping at all). The open question — whether this crosses fully into *anonymisation* — is answered in Appendix A.

### B.4 Anonymisation — EDPB Guidelines (pending)
Not yet published (EDPB "sprint team", expected ~summer 2026). **Tracked dependency:** re-validate Appendices A & B against the final text on publication. Until then this assessment rests on WP29 Opinion 05/2014 (EDPB-endorsed) + *SRB* + the Pseudonymisation Guidelines.
