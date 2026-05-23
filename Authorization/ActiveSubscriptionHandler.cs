using garge_api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace garge_api.Authorization
{
    /// <summary>
    /// Allows a claim when the user has room for one more owned sensor under their current subscriptions.
    ///
    /// Capacity model (see <see cref="ISubscriptionCapacityService"/> for the single source of truth):
    ///   - An active Primary subscription grants a base allowance of 1 owned sensor.
    ///   - Each active AddOn subscription contributes its Quantity to the allowance.
    ///   - Without an active Primary, capacity is 0 — AddOns alone are not enough.
    ///   - A cancelled subscription still counts during its paid-period grace (future NextChargeDate).
    ///   - Only ACTIVE (non-suspended) owned sensors consume capacity.
    ///
    /// A claim is allowed when active owned sensors &lt; capacity. This handler only runs on the few
    /// endpoints that take new ownership (sensor/switch claim). Reads, unclaim, profile, and account
    /// deletion are gated by [Authorize] alone so a user who drops out of quota can still recover.
    /// </summary>
    public class ActiveSubscriptionHandler : AuthorizationHandler<ActiveSubscriptionRequirement>
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public ActiveSubscriptionHandler(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        protected override async Task HandleRequirementAsync(
            AuthorizationHandlerContext context,
            ActiveSubscriptionRequirement requirement)
        {
            var userId = context.User.UserId();
            if (userId == null) return;

            using var scope = _scopeFactory.CreateScope();
            var capacityService = scope.ServiceProvider.GetRequiredService<ISubscriptionCapacityService>();

            // Subscription-bypass roles (complimentary / service-account / admin) have no capacity
            // limit. Resolve from the DB — not the JWT (context.User.IsInRole) — so this matches
            // GET /sensors/capacity exactly and works even when the role was granted after the
            // user's token was issued (otherwise the meter shows access but the claim 403s).
            if (await capacityService.HasSubscriptionBypassAsync(userId))
            {
                context.Succeed(requirement);
                return;
            }

            var capacity = await capacityService.GetCapacityAsync(userId);
            var activeOwned = await capacityService.GetActiveOwnedSensorCountAsync(userId);

            if (activeOwned < capacity)
                context.Succeed(requirement);
        }
    }
}
