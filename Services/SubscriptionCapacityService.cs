using garge_api.Models;
using garge_api.Models.Sensor;
using garge_api.Models.Subscription;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace garge_api.Services
{
    /// <summary>
    /// Computes a user's sensor capacity and how much of it is in use. Single source of truth shared
    /// by the claim authorization handler, the suspend/activate toggle, and the reconciliation job.
    ///
    /// Capacity = 1 (an active Primary) + sum of active AddOn quantities. A subscription counts while
    /// Active, or during the paid-period grace after cancel/lapse (Stopped/Expired with a future
    /// NextChargeDate). Only ACTIVE (non-suspended) owned sensors consume capacity.
    /// </summary>
    public interface ISubscriptionCapacityService
    {
        Task<int> GetCapacityAsync(string userId, CancellationToken ct = default);
        Task<int> GetActiveOwnedSensorCountAsync(string userId, CancellationToken ct = default);
    }

    public class SubscriptionCapacityService : ISubscriptionCapacityService
    {
        private readonly ApplicationDbContext _db;
        private readonly IMemoryCache _cache;
        private const string TestModeCacheKey = "vipps_test_mode";

        public SubscriptionCapacityService(ApplicationDbContext db, IMemoryCache cache)
        {
            _db = db;
            _cache = cache;
        }

        public Task<int> GetActiveOwnedSensorCountAsync(string userId, CancellationToken ct = default)
            => _db.UserSensors.CountAsync(us => us.UserId == userId && us.IsOwner && us.SuspendedAt == null, ct);

        public async Task<int> GetCapacityAsync(string userId, CancellationToken ct = default)
        {
            if (!_cache.TryGetValue(TestModeCacheKey, out bool isTestMode))
            {
                var settings = await _db.AppSettings.FindAsync([1], ct);
                isTestMode = settings?.VippsTestMode ?? false;
                _cache.Set(TestModeCacheKey, isTestMode, TimeSpan.FromSeconds(30));
            }

            var now = DateTime.UtcNow;
            var subs = await _db.Subscriptions
                .Where(s => s.UserId == userId
                         && (!s.IsTest || isTestMode)
                         && (s.Status == SubscriptionStatus.Active
                             || ((s.Status == SubscriptionStatus.Stopped || s.Status == SubscriptionStatus.Expired)
                                 && s.NextChargeDate != null && s.NextChargeDate > now)))
                .Select(s => new { Type = s.Product!.Type, s.Quantity })
                .ToListAsync(ct);

            var primaryActive = subs.Any(s => s.Type == ProductType.Primary);
            var addOnCapacity = subs.Where(s => s.Type == ProductType.AddOn).Sum(s => s.Quantity);
            return primaryActive ? 1 + addOnCapacity : 0;
        }
    }
}
