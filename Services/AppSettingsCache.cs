using garge_api.Models;
using garge_api.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace garge_api.Services
{
    public class AppSettingsCache : IAppSettingsCache
    {
        private const string CacheKey = "app_settings";
        private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IMemoryCache _cache;

        public AppSettingsCache(IServiceScopeFactory scopeFactory, IMemoryCache cache)
        {
            _scopeFactory = scopeFactory;
            _cache = cache;
        }

        public async Task<AppSettings> GetAsync()
        {
            if (_cache.TryGetValue(CacheKey, out AppSettings? cached) && cached != null)
                return cached;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var settings = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1)
                           ?? new AppSettings { Id = 1 };

            _cache.Set(CacheKey, settings, Ttl);
            return settings;
        }

        public void Invalidate() => _cache.Remove(CacheKey);
    }
}
