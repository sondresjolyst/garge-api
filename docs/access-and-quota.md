# Access, quota and device sharing

This document describes how device access is governed: subscription capacity, what happens when a
subscription is cancelled, retention of suspended data, and how owners share devices with other users.

Throughout, "switch" and "socket" refer to the same device — the API uses *switch*, the app presents it
to users as *socket*.

## Subscription capacity

Capacity is the number of sensors a user may keep active at once.

- Capacity equals one active **Primary** subscription plus the combined `Quantity` of every active
  **AddOn**. Without an active Primary, capacity is zero; AddOns alone grant nothing.
- A cancelled subscription continues to count for the remainder of the period already paid for: while
  its `NextChargeDate` is in the future, the user retains that capacity.
- Only active, owned sensors count against capacity. Suspended sensors and sensors shared with the user
  do not.

`SubscriptionCapacityService` is the authority for capacity, and clients read it from
`GET /sensors/capacity`.

### Roles that bypass capacity

`Admin`, `SensorAdmin`, `MqttAdmin`, `AutomationAdmin`, `SwitchAdmin`, `ComplimentaryUser` and
`DeviceBridge` use the service without a capacity limit. They are never auto-suspended and their data is
never purged. The role is resolved from the database, so granting it to a signed-in user takes effect
immediately.

## Cancelling a subscription

Cancellation suspends devices; it never deletes data.

- **Adding devices** is blocked once a user's active owned sensors reach their capacity. Access to
  devices they already have is never blocked.
- **Cancelling the Primary** drops capacity to zero. When the paid period ends, the user's sensors are
  automatically suspended.
- **Cancelling an AddOn while over capacity** suspends only the excess, keeping the oldest sensors (by
  claim date) active.
- A **suspended sensor** stops showing data and frees its capacity slot, while telemetry keeps being
  recorded. The owner chooses which sensors are active using the on/off toggle.
- **Resubscribing** restores capacity. Suspended sensors remain off until the owner turns them back on,
  so the owner decides which to resume.

### Retention of suspended data

Data from a suspended sensor is retained so a returning seasonal user keeps their year-over-year
history. A user may object to this retention; once they have opted out and have no remaining coverage,
their suspended sensors become eligible for purge after six months. Purging moves the telemetry into the
anonymised analytics store and removes the personal records.

## Device sharing

An owner can share a device with another Garge user at one of two access levels.

### How a share works

- The owner shares with the recipient's account email; the recipient must already have a Garge account.
- Sharing does not affect either user's capacity. Only owned devices count against capacity, so a shared
  device never consumes the recipient's allowance or the owner's.

### Access levels

| Action | Read | Edit | Owner |
|---|:--:|:--:|:--:|
| View data, history, battery health and live updates | ✓ | ✓ | ✓ |
| Set a personal name, activities, photos and alert preferences | ✓ | ✓ | ✓ |
| Control switches | | ✓ | ✓ |
| Create and edit automations | | ✓ | ✓ |
| Calibrate a battery sensor | | ✓ | ✓ |
| Turn a device on or off (capacity) | | | ✓ |
| Remove a device | | | ✓ |
| Share, revoke a share, or transfer ownership | | | ✓ |

Personal details such as a custom name, activities, photos and alert preferences are stored per user.
A recipient editing them changes only their own view, so these remain available at every access level.

### History visible to a recipient

A recipient sees data from the moment the device was shared with them onward. Earlier history, from
before the share, stays private to the owner.

### Lifecycle

- A recipient's access lasts as long as the owner keeps the device, independent of the owner's
  subscription. When the owner removes the device, every share of it ends. A recipient may also remove
  their own access at any time.
- While a sensor is suspended, its data is hidden for everyone, including recipients, until it is turned
  back on.

### Switches and discovery

A switch may be owned directly, by the user who claimed it, or indirectly, by the owner of the sensor
whose gateway discovered it. Both can share the switch. Switch control is performed through automations
or the operator, so the only switch action gated to Edit is deleting telemetry.

When a sensor's gateway discovers a new socket, only the sensor's owner gains access to it. Sharing the
sensor does not extend to the sockets behind it. Because an indirect owner can share a socket, removing
the sensor that provided that ownership would otherwise leave the socket's shares without an owner;
removing a sensor therefore also ends shares on any socket left without an owner.
