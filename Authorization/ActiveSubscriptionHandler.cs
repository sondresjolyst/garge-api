using garge_api.Constants;
using garge_api.Models;
using garge_api.Models.Subscription;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Claims;

namespace garge_api.Authorization
{
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
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
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

            var hasActive = await db.Subscriptions.AnyAsync(s =>
                s.UserId == userId &&
                s.Status == SubscriptionStatus.Active &&
                (!s.IsTest || isTestMode));

            if (hasActive)
                context.Succeed(requirement);
        }
    }
}
