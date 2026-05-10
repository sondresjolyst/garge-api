using garge_api.Models;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    public interface IDeviceOwnershipService
    {
        Task<IReadOnlyCollection<string>> ListSwitchOwnersAsync(int switchId, CancellationToken ct = default);
        Task<IReadOnlyCollection<string>> ListSensorOwnersAsync(int sensorId, CancellationToken ct = default);
    }

    public class DeviceOwnershipService : IDeviceOwnershipService
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public DeviceOwnershipService(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        public async Task<IReadOnlyCollection<string>> ListSwitchOwnersAsync(int switchId, CancellationToken ct = default)
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

            return directOwners.Concat(indirectOwners).Distinct().ToList();
        }

        public async Task<IReadOnlyCollection<string>> ListSensorOwnersAsync(int sensorId, CancellationToken ct = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            return await db.UserSensors
                .Where(us => us.SensorId == sensorId)
                .Select(us => us.UserId)
                .Distinct()
                .ToListAsync(ct);
        }
    }
}
