# Access, quota & device sharing

How device access, subscription capacity, cancellation, and sharing behave. This is the product
decision record referenced by issue #245.

## Subscription capacity

- **Capacity** = 1 (one active **Primary** subscription) + the summed `Quantity` of every active
  **AddOn**. Without an active Primary, capacity is 0 — AddOns alone grant nothing.
- A cancelled subscription (`Stopped`/`Expired`) still counts during its **paid-period grace**: while
  `NextChargeDate` is in the future, the user keeps the capacity they paid for.
- Only **active, owned** sensors consume capacity: `UserSensor.IsOwner = true` and `SuspendedAt = null`.
  Suspended sensors and shared (viewer) sensors do not count.
- The single source of truth is `SubscriptionCapacityService`; clients read it via `GET /sensors/capacity`.

### Subscription-bypass roles
`Admin, SensorAdmin, MqttAdmin, AutomationAdmin, SwitchAdmin, ComplimentaryUser, DeviceBridge` use the
service with **no capacity limit**. They are never auto-suspended and their data is never purged. The
check is DB-backed (`HasSubscriptionBypassAsync`) so a role granted to a logged-in user takes effect
immediately, with no re-login.

## What happens when an owner cancels (issue #245)

The model is **suspension, not deletion** — data is never lost on cancellation.

- **New claims** are gated at claim time (`ActiveSubscriptionHandler`): blocked once active owned sensors
  ≥ capacity (bypass roles exempt). Existing access is never blocked at request time.
- **Owner cancels Primary** → capacity drops to 0. After the paid-period grace lapses, the nightly
  `QuotaReconciliationService` auto-suspends the owner's sensors (keeps the oldest `capacity` by claim
  date, suspends the newest excess). With capacity 0 that is all of them.
- **Owner cancels an AddOn while over capacity** → only the newest excess is suspended; the oldest stay
  active.
- **Suspended sensor**: dashboard/history reads are blocked and the slot is freed, but telemetry keeps
  flowing. The owner re-picks which sensors are active via the suspend/activate toggle.
- **Re-subscribe**: capacity is restored, but suspended sensors stay suspended until the owner
  re-activates them (the user chooses which to bring back).

### Retention of suspended data
Suspended-sensor history is kept under legitimate interest so a returning seasonal user keeps their
year-over-year data. A user may **opt out** (GDPR Art. 21); once opted out and without coverage, their
suspended sensors become eligible for the 180-day purge (`SuspendedSensorPurgeService`), which moves the
telemetry into the anonymized ML store and removes the personal rows.

## Device sharing

An owner can share a device with another Garge user. Two tiers, set per share.

### Mechanism
- Share by the recipient's **email**; they must already have a Garge account.
- A share is a `UserSensor` row with `IsOwner = false` and a `Permission` of `Read` or `Edit`.
- Sharing **does not consume the recipient's capacity** (only `IsOwner = true` rows count), and it does
  not affect the owner's capacity.

### Permission matrix

| Action | Read | Edit | Owner |
|---|:--:|:--:|:--:|
| View data, history, battery health, live updates | ✓ | ✓ | ✓ |
| Set own custom name / activities / photos / alert prefs (per-user, private) | ✓ | ✓ | ✓ |
| Toggle switches | | ✓ | ✓ |
| Create / edit automations | | ✓ | ✓ |
| Battery calibration (global offset) | | ✓ | ✓ |
| Suspend / activate (capacity) | | | ✓ |
| Unclaim / delete | | | ✓ |
| Share / revoke / transfer ownership | | | ✓ |

Per-user data (custom name, activities, photos, offline-alert preferences) is keyed by user, so a viewer
editing it only changes **their own** view — it never touches the owner's. That is why it needs no Edit
tier.

### History window
A share opens an ownership period starting at share time, so the recipient sees data **from when it was
shared onward** — not the owner's earlier private history (same model used for resold sensors).

### Lifecycle
- Viewer access is tied to the **owner's sensor existence**, not the owner's subscription. If the owner
  unclaims the sensor, all shares are removed (cascade). A recipient can also leave a share themselves.
- If the owner lets a sensor get suspended (over quota), reads are paused for everyone, including
  recipients — there is simply no data being shown while it is off.

### Switches and the discovery edge
Switches (shown as "sockets" in the app) share the same model. A switch *owner* is a direct owner (who
claimed it) **or** an indirect owner — a user who owns the parent sensor whose gateway discovered the
switch. Either can share it; Edit gates the only user-reachable switch mutation (deleting telemetry),
since toggling is admin/operator-only or via automations (already gated).

When a sensor discovers a new socket, only the sensor's **owner** gains access to it automatically;
people the sensor is *shared* with do not (a sensor share is not a switch share). Because indirect
owners can share, unclaiming a sensor could leave a discovered socket with no owner — so sensor unclaim
also revokes shares on any socket that is left ownerless, keeping socket-share lifetime tied to
ownership.

## Status / scope

Implemented: capacity model, claim gating, auto-suspension, suspended-data retention + opt-out purge,
the capacity endpoint, bypass-role handling, and **sensor + switch sharing** (Read/Edit tiers,
share/revoke/list, viewer history window, owner-unclaim cascade, orphaned-socket-share cleanup, and
Edit/owner gating of automations, calibration and switch-telemetry deletion).

Remaining: the frontend share UI for switches/sockets (sensor share UI shipped first).
