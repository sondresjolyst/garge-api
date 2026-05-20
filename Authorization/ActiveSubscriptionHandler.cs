using garge_api.Constants;
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

            if (RoleNames.SubscriptionBypassRoles.Any(r => context.User.IsInRole(r)))
            {
                context.Succeed(requirement);
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var capacityService = scope.ServiceProvider.GetRequiredService<ISubscriptionCapacityService>();

            var capacity = await capacityService.GetCapacityAsync(userId);
            var activeOwned = await capacityService.GetActiveOwnedSensorCountAsync(userId);

            if (activeOwned < capacity)
                context.Succeed(requirement);
        }
    }
}
