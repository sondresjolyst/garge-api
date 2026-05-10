using garge_api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace garge_api.Services
{
    /// <summary>
    /// Single source of truth for "which users own this device" and "can this
    /// user access this device". Used by both the SignalR dispatch path
    /// (PostgresNotificationService) and the request-time access checks in
    /// SwitchesController/SensorController.
    ///
    /// Caches results for <see cref="OwnershipCacheTtl"/>. Callers that mutate
    /// UserSwitches / UserSensors must invalidate via
    /// <see cref="InvalidateSwitch"/> / <see cref="InvalidateSensor"/>.
    /// </summary>
    public interface IDeviceOwnershipService
    {
        Task<IReadOnlyCollection<string>> ListSwitchOwnersAsync(int switchId, CancellationToken ct = default);
        Task<IReadOnlyCollection<string>> ListSensorOwnersAsync(int sensorId, CancellationToken ct = default);
        Task<bool> CanUserAccessSwitchAsync(string userId, int switchId, CancellationToken ct = default);
        Task<bool> CanUserAccessSensorAsync(string userId, int sensorId, CancellationToken ct = default);
        void InvalidateSwitch(int switchId);
        void InvalidateSensor(int sensorId);
    }

    public class DeviceOwnershipService : IDeviceOwnershipService
    {
        private static readonly TimeSpan OwnershipCacheTtl = TimeSpan.FromSeconds(60);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IMemoryCache _cache;

        public DeviceOwnershipService(IServiceScopeFactory scopeFactory, IMemoryCache cache)
        {
            _scopeFactory = scopeFactory;
            _cache = cache;
        }

        private static string SwitchOwnersKey(int switchId) => $"switch-owners:{switchId}";
        private static string SensorOwnersKey(int sensorId) => $"sensor-owners:{sensorId}";

        public Task<IReadOnlyCollection<string>> ListSwitchOwnersAsync(int switchId, CancellationToken ct = default) =>
            _cache.GetOrCreateAsync(SwitchOwnersKey(switchId), async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = OwnershipCacheTtl;
                return await LoadSwitchOwnersAsync(switchId, ct);
            })!;

        public Task<IReadOnlyCollection<string>> ListSensorOwnersAsync(int sensorId, CancellationToken ct = default) =>
            _cache.GetOrCreateAsync(SensorOwnersKey(sensorId), async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = OwnershipCacheTtl;
                return await LoadSensorOwnersAsync(sensorId, ct);
            })!;

        public async Task<bool> CanUserAccessSwitchAsync(string userId, int switchId, CancellationToken ct = default)
        {
            var owners = await ListSwitchOwnersAsync(switchId, ct);
            return owners.Contains(userId);
        }

        public async Task<bool> CanUserAccessSensorAsync(string userId, int sensorId, CancellationToken ct = default)
        {
            var owners = await ListSensorOwnersAsync(sensorId, ct);
            return owners.Contains(userId);
        }

        public void InvalidateSwitch(int switchId) => _cache.Remove(SwitchOwnersKey(switchId));
        public void InvalidateSensor(int sensorId) => _cache.Remove(SensorOwnersKey(sensorId));

        private async Task<IReadOnlyCollection<string>> LoadSwitchOwnersAsync(int switchId, CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var directOwners = await db.UserSwitches
                .Where(us => us.SwitchId == switchId)
                .Select(us => us.UserId)
                .ToListAsync(ct);

            var switchEntity = await db.Switches.AsNoTracking().FirstOrDefaultAsync(s => s.Id == switchId, ct);

            var indirectOwners = new List<string>();
            if (switchEntity != null)
            {
                indirectOwners = await db.DiscoveredDevices
                    .Where(dd => dd.Target == switchEntity.Name)
                    .Join(db.Sensors, dd => dd.DiscoveredBy, s => s.ParentName, (dd, s) => s.Id)
                    .Join(db.UserSensors, sid => sid, us => us.SensorId, (sid, us) => us.UserId)
                    .Distinct()
                    .ToListAsync(ct);
            }

            // Mirror SwitchesController.UserHasRequiredRoleAsync admin bypass:
            // admin / SwitchAdmin can read every switch via REST, so they should
            // also receive live SignalR events for every switch.
            var admins = await GetUserIdsInRolesAsync(db, new[] { "Admin", "SwitchAdmin" }, ct);

            return directOwners.Concat(indirectOwners).Concat(admins).Distinct().ToList();
        }

        private async Task<IReadOnlyCollection<string>> LoadSensorOwnersAsync(int sensorId, CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var directOwners = await db.UserSensors
                .Where(us => us.SensorId == sensorId)
                .Select(us => us.UserId)
                .Distinct()
                .ToListAsync(ct);

            // Same admin bypass as switches: admin / SensorAdmin see every sensor.
            var admins = await GetUserIdsInRolesAsync(db, new[] { "Admin", "SensorAdmin" }, ct);

            return directOwners.Concat(admins).Distinct().ToList();
        }

        private static async Task<List<string>> GetUserIdsInRolesAsync(ApplicationDbContext db, string[] roleNames, CancellationToken ct)
        {
            return await db.UserRoles
                .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
                .Where(x => x.Name != null && roleNames.Contains(x.Name))
                .Select(x => x.UserId)
                .Distinct()
                .ToListAsync(ct);
        }
    }
}
