using System.Linq.Expressions;
using garge_api.Dtos.Common;
using garge_api.Models;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Helpers
{
    /// <summary>
    /// Outcome of an attempt to upsert a share membership row, so the caller can return the matching
    /// HTTP result while the shared mutation logic lives in one place.
    /// </summary>
    public enum ShareUpsertResult
    {
        /// <summary>A new viewer membership row and ownership period were added.</summary>
        Added,

        /// <summary>An existing viewer row had its permission tier updated; no new period.</summary>
        PermissionUpdated,

        /// <summary>The target already owns the device; nothing changed.</summary>
        AlreadyOwner,
    }

    /// <summary>
    /// Sensor and switch sharing share the same membership/period upsert and the same recipient-listing
    /// projection, differing only in the entity types and FK column. These generic helpers hold that
    /// common logic so the two controllers stay in lockstep without duplicating it. Device-specific
    /// concerns (the owner check — direct for sensors, direct-or-indirect for switches — the cache
    /// invalidation, and the SignalR <c>kind</c> label) remain in each controller.
    /// </summary>
    public static class DeviceShareHelper
    {
        /// <summary>
        /// Upserts a viewer membership for the target user (identified by <paramref name="matchesTarget"/>):
        /// rejects when the target already owns the device; updates the permission tier on an existing viewer
        /// row (no new period); otherwise adds a new viewer row plus an ownership period that starts now (the
        /// recipient sees data from share time, not the owner's earlier private history). Does not save changes.
        /// </summary>
        /// <typeparam name="TMembership">UserSensor or UserSwitch.</typeparam>
        /// <typeparam name="TPeriod">SensorOwnershipPeriod or SwitchOwnershipPeriod.</typeparam>
        public static async Task<ShareUpsertResult> UpsertShareAsync<TMembership, TPeriod>(
            DbSet<TMembership> memberships,
            DbSet<TPeriod> periods,
            SharePermission permission,
            Expression<Func<TMembership, bool>> matchesTarget,
            Func<TMembership, bool> isOwner,
            Action<TMembership, SharePermission> setPermission,
            Func<TMembership> newMembership,
            Func<TPeriod> newPeriod)
            where TMembership : class
            where TPeriod : class
        {
            var existing = await memberships.FirstOrDefaultAsync(matchesTarget);
            if (existing != null)
            {
                if (isOwner(existing))
                    return ShareUpsertResult.AlreadyOwner;

                setPermission(existing, permission); // Re-sharing updates the permission tier.
                return ShareUpsertResult.PermissionUpdated;
            }

            memberships.Add(newMembership());
            periods.Add(newPeriod());
            return ShareUpsertResult.Added;
        }

        /// <summary>
        /// Projects the viewer (non-owner) membership rows for a device, joined to the user record, into
        /// the recipient DTO returned to the owner. Identical for sensors and switches once the membership
        /// rows for the device have been selected.
        /// </summary>
        public static async Task<List<ShareRecipientDto>> ListRecipientsAsync<TMembership>(
            IQueryable<TMembership> viewerRows,
            DbSet<User> users,
            Func<TMembership, string> userIdOf,
            Func<TMembership, SharePermission> permissionOf,
            Func<TMembership, DateTime> sharedAtOf)
            where TMembership : class
        {
            // The membership rows are materialised first so the join and projection run in memory; the
            // membership/user types have no shared navigation to express the join in a single provider query.
            var rows = await viewerRows.ToListAsync();
            var userIds = rows.Select(userIdOf).ToList();
            var userById = await users
                .Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u);

            return rows
                .Where(r => userById.ContainsKey(userIdOf(r)))
                .Select(r =>
                {
                    var u = userById[userIdOf(r)];
                    return new ShareRecipientDto
                    {
                        UserId = u.Id,
                        Email = u.Email!,
                        FirstName = u.FirstName,
                        LastName = u.LastName,
                        Permission = permissionOf(r),
                        SharedAt = sharedAtOf(r),
                    };
                })
                .ToList();
        }
    }
}
