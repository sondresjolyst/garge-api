using garge_api.Models;
using Microsoft.Extensions.Options;

namespace garge_api.Services
{
    /// <summary>The shop and subscription webhooks, received through AppKit.Vipps.</summary>
    public static class GargeVippsWebhooks
    {
        public const string ShopSlot = "shop";
        public const string SubscriptionSlot = "subscription";

        // Vipps has these URLs registered, so they must not change.
        public const string ShopPath = "/api/shop/webhook";
        public const string SubscriptionPath = "/api/subscriptions/webhook";

        // Keeps the advisory lock, the Data Protection purpose and the prefix garge used before, so
        // instances on the old and new code take turns and the stored secrets stay readable.
        public const long AdvisoryLockKey = 7300114;
        public const string SecretPurpose = "garge.vipps.webhook-secret.v1";
        public const string SecretPrefix = "enc:v1:";

        /// <summary>Registers the webhook endpoints' services, the handlers, and the registration store.</summary>
        public static IServiceCollection AddGargeVippsWebhooks(this IServiceCollection services)
        {
            services.AddAppKitVippsWebhooks<ApplicationDbContext>(o =>
            {
                o.AddSlot(ShopSlot, VippsWebhookKind.Payment, ShopPath);
                o.AddSlot(SubscriptionSlot, VippsWebhookKind.Recurring, SubscriptionPath);
                o.AdvisoryLockKey = AdvisoryLockKey;
                o.SecretPurpose = SecretPurpose;
                o.SecretPrefix = SecretPrefix;
            });
            services.AddOptions<VippsWebhookOptions>()
                .Configure<IOptions<AppOptions>>((o, app) => o.PublicBaseUrl = app.Value.ApiBaseUrl);
            services.AddScoped<IVippsWebhookRegistrationStore, AppSettingsWebhookRegistrationStore>();
            services.AddScoped<IVippsPaymentWebhookHandler, ShopWebhookHandler>();
            services.AddScoped<IVippsRecurringWebhookHandler, SubscriptionWebhookHandler>();
            return services;
        }

        /// <summary>Maps the shop and subscription webhook endpoints.</summary>
        public static IEndpointRouteBuilder MapGargeVippsWebhooks(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapAppKitVippsWebhook<ApplicationDbContext>(ShopSlot);
            endpoints.MapAppKitVippsWebhook<ApplicationDbContext>(SubscriptionSlot);
            return endpoints;
        }
    }
}
