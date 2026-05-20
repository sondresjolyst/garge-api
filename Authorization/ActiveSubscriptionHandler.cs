using garge_api.Constants;
using garge_api.Models;
using garge_api.Models.Sensor;
using garge_api.Models.Subscription;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Claims;

namespace garge_api.Authorization
{
    /// <summary>
    /// Allows a claim when the user has room for one more owned sensor under their current subscriptions.
    ///
    /// Capacity model:
    ///   - An active Primary subscription grants a base allowance of 1 owned sensor.
    ///   - Each active AddOn subscription contributes its Quantity to the allowance.
    ///   - Without an active Primary, capacity is 0 — AddOns alone are not enough.
    ///
    /// A claim is allowed when ownedSensorCount &lt; capacity. Shared access (UserSensor.IsOwner = false)
    /// is not counted toward ownedSensorCount, so the owner's subscription covers shared viewers.
    ///
    /// This handler only runs on the few endpoints that take new ownership (sensor/switch claim). Reads,
    /// unclaim, profile, and account deletion are gated by [Authorize] alone so a user who drops out of
    /// quota can still see their account and unclaim sensors to recover.
    /// </summary>
    public class ActiveSubscriptionHandler : AuthorizationHandler<ActiveSubscriptionRequirement>
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IMemoryCache _cache;

        private const string TestModeCacheKey = "vipps_test_mode";

        public ActiveSubscriptionHandler(IServiceScopeFactory scopeFactory, IMemoryCache cache)
        {
            _scopeFactory = scopeFactory;
            _cache = cache;
        }

        protected override async Task HandleRequirementAsync(
            AuthorizationHandlerContext context,
            ActiveSubscriptionRequirement requirement)
        {
            var userId = context.User.UserId();
            if (userId == null) return;

            if (RoleNames.SubscriptionBypassRoles.Any(r => context.User.IsInRole(r)))
            {
                context.Succeed(requirement);
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            if (!_cache.TryGetValue(TestModeCacheKey, out bool isTestMode))
            {
                var settings = await db.AppSettings.FindAsync(1);
                isTestMode = settings?.VippsTestMode ?? false;
                _cache.Set(TestModeCacheKey, isTestMode, TimeSpan.FromSeconds(30));
            }

            // Only ACTIVE (non-suspended) owned sensors consume capacity: turning a sensor off frees
            // a slot, and over-quota sensors are suspended rather than counted.
            var ownedSensorCount = await db.UserSensors.CountAsync(us => us.UserId == userId && us.IsOwner && us.SuspendedAt == null);

            // A subscription contributes to capacity while Active, OR during the paid-period grace
            // after cancel/lapse — i.e. a Stopped/Expired sub whose NextChargeDate is still in the
            // future. The user paid through that date, so access continues until then.
            var now = DateTime.UtcNow;
            var activeSubs = await db.Subscriptions
                .Where(s => s.UserId == userId
                         && (!s.IsTest || isTestMode)
                         && (s.Status == SubscriptionStatus.Active
                             || ((s.Status == SubscriptionStatus.Stopped || s.Status == SubscriptionStatus.Expired)
                                 && s.NextChargeDate != null && s.NextChargeDate > now)))
                .Select(s => new { Type = s.Product!.Type, s.Quantity })
                .ToListAsync();

            var primaryActive = activeSubs.Any(s => s.Type == ProductType.Primary);
            var addOnCapacity = activeSubs
                .Where(s => s.Type == ProductType.AddOn)
                .Sum(s => s.Quantity);

            var capacity = primaryActive ? 1 + addOnCapacity : 0;

            if (ownedSensorCount < capacity)
                context.Succeed(requirement);
        }
    }
}
