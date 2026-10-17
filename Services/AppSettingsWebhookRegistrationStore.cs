using garge_api.Models;
using garge_api.Models.Admin;

namespace garge_api.Services
{
    /// <summary>
    /// Keeps the Vipps webhook registrations in the eight webhook columns of the AppSettings row, where
    /// garge stored them before the registration moved to AppKit.Vipps.
    /// </summary>
    public class AppSettingsWebhookRegistrationStore(ApplicationDbContext db, IAppSettingsCache settingsCache, ILogger<AppSettingsWebhookRegistrationStore> logger)
        : IVippsWebhookRegistrationStore
    {
        public async Task<VippsWebhookRegistration?> GetAsync(string slot, bool isTest, CancellationToken cancellationToken)
        {
            // Find, so a registration set earlier in the same run is seen before it is saved.
            var settings = await db.AppSettings.FindAsync([1], cancellationToken);
            if (settings == null) return null;
            var (id, secret) = Read(settings, slot, isTest);
            return string.IsNullOrEmpty(id) && string.IsNullOrEmpty(secret)
                ? null
                : new VippsWebhookRegistration(id ?? string.Empty, secret ?? string.Empty);
        }

        public async Task SetAsync(string slot, bool isTest, VippsWebhookRegistration? registration, CancellationToken cancellationToken)
        {
            var settings = await db.AppSettings.FindAsync([1], cancellationToken);
            if (settings == null)
            {
                logger.LogInformation("AppSettings row missing, seeding the default row");
                settings = new AppSettings { Id = 1 };
                db.AppSettings.Add(settings);
            }
            Write(settings, slot, isTest, registration?.Id, registration?.ProtectedSecret);
        }

        public Task SavedAsync(CancellationToken cancellationToken)
        {
            settingsCache.Invalidate();
            return Task.CompletedTask;
        }

        private static (string? Id, string? Secret) Read(AppSettings s, string slot, bool isTest) => (slot, isTest) switch
        {
            (GargeVippsWebhooks.ShopSlot, false) => (s.VippsShopWebhookId, s.VippsShopWebhookSecret),
            (GargeVippsWebhooks.ShopSlot, true) => (s.VippsTestShopWebhookId, s.VippsTestShopWebhookSecret),
            (GargeVippsWebhooks.SubscriptionSlot, false) => (s.VippsSubscriptionWebhookId, s.VippsSubscriptionWebhookSecret),
            (GargeVippsWebhooks.SubscriptionSlot, true) => (s.VippsTestSubscriptionWebhookId, s.VippsTestSubscriptionWebhookSecret),
            _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "Unknown Vipps webhook slot."),
        };

        private static void Write(AppSettings s, string slot, bool isTest, string? id, string? secret)
        {
            switch (slot, isTest)
            {
                case (GargeVippsWebhooks.ShopSlot, false):
                    (s.VippsShopWebhookId, s.VippsShopWebhookSecret) = (id, secret);
                    break;
                case (GargeVippsWebhooks.ShopSlot, true):
                    (s.VippsTestShopWebhookId, s.VippsTestShopWebhookSecret) = (id, secret);
                    break;
                case (GargeVippsWebhooks.SubscriptionSlot, false):
                    (s.VippsSubscriptionWebhookId, s.VippsSubscriptionWebhookSecret) = (id, secret);
                    break;
                case (GargeVippsWebhooks.SubscriptionSlot, true):
                    (s.VippsTestSubscriptionWebhookId, s.VippsTestSubscriptionWebhookSecret) = (id, secret);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(slot), slot, "Unknown Vipps webhook slot.");
            }
        }
    }
}
