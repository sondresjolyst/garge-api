using garge_api.Constants;
using garge_api.Models;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    /// <summary>
    /// Resolves permissions from the user's roles in the database. A role grants a permission through
    /// <see cref="RoleNames.RolePermissions"/> or a <c>RolePermissions</c> row; Admin holds every known permission.
    /// </summary>
    public interface IPermissionService
    {
        Task<bool> HasPermissionAsync(string userId, string permission, CancellationToken ct = default);
        Task<IReadOnlyCollection<string>> GetPermissionsAsync(string userId, CancellationToken ct = default);
        Task<HashSet<string>> GetUsersWithPermissionAsync(IEnumerable<string> userIds, string permission, CancellationToken ct = default);
    }

    public class PermissionService(ApplicationDbContext db) : IPermissionService
    {
        public async Task<bool> HasPermissionAsync(string userId, string permission, CancellationToken ct = default)
            => (await GetUsersWithPermissionAsync([userId], permission, ct)).Count > 0;

        public async Task<IReadOnlyCollection<string>> GetPermissionsAsync(string userId, CancellationToken ct = default)
        {
            var byUser = await LoadPermissionsAsync([userId], ct);
            return byUser.TryGetValue(userId, out var permissions)
                ? permissions.Order(StringComparer.Ordinal).ToList()
                : [];
        }

        public async Task<HashSet<string>> GetUsersWithPermissionAsync(IEnumerable<string> userIds, string permission, CancellationToken ct = default)
        {
            var byUser = await LoadPermissionsAsync(userIds, ct);
            return byUser.Where(kv => kv.Value.Contains(permission)).Select(kv => kv.Key).ToHashSet();
        }

        private async Task<Dictionary<string, HashSet<string>>> LoadPermissionsAsync(IEnumerable<string> userIds, CancellationToken ct)
        {
            var ids = userIds.Distinct().ToList();
            if (ids.Count == 0) return [];

            var userRoles = await (
                from ur in db.UserRoles
                join r in db.Roles on ur.RoleId equals r.Id
                where ids.Contains(ur.UserId) && r.Name != null
                select new { ur.UserId, RoleName = r.Name! })
                .ToListAsync(ct);

            var roleNames = userRoles.Select(x => x.RoleName).Distinct().ToList();
            var upperRoleNames = roleNames.Select(n => n.ToUpperInvariant()).ToList();
            var grantedRows = await db.RolePermissions
                .Where(rp => upperRoleNames.Contains(rp.RoleName.ToUpper()))
                .Select(rp => new { rp.RoleName, rp.Permission })
                .ToListAsync(ct);

            var byRole = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var role in roleNames)
            {
                var permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (RoleNames.RolePermissions.TryGetValue(role, out var mapped))
                    permissions.UnionWith(mapped);
                if (string.Equals(role, RoleNames.Admin, StringComparison.OrdinalIgnoreCase))
                    permissions.UnionWith(RoleNames.KnownPermissions);
                byRole[role] = permissions;
            }
            foreach (var row in grantedRows)
            {
                if (byRole.TryGetValue(row.RoleName, out var permissions))
                    permissions.Add(row.Permission);
            }

            var byUser = new Dictionary<string, HashSet<string>>();
            foreach (var ur in userRoles)
            {
                if (!byUser.TryGetValue(ur.UserId, out var permissions))
                    byUser[ur.UserId] = permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                permissions.UnionWith(byRole[ur.RoleName]);
            }
            return byUser;
        }
    }
}
