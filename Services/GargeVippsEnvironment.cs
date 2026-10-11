using garge_api.Models;
using Microsoft.Extensions.Caching.Memory;

namespace garge_api.Services
{
    /// <summary>
    /// Sends new payments and agreements to the Vipps test environment while test mode is on in the
    /// admin settings. Read for at most 30 seconds from the cache.
    /// </summary>
    public class GargeVippsEnvironment(IMemoryCache cache, IServiceScopeFactory scopeFactory) : IVippsEnvironment
    {
        internal const string TestModeCacheKey = "vipps_test_mode";

        public async Task<bool> IsTestAsync(CancellationToken cancellationToken = default)
        {
            if (cache.TryGetValue(TestModeCacheKey, out bool cached))
                return cached;

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var settings = await db.AppSettings.FindAsync([1], cancellationToken);
            var isTest = settings?.VippsTestMode ?? false;
            cache.Set(TestModeCacheKey, isTest, TimeSpan.FromSeconds(30));
            return isTest;
        }
    }
}
