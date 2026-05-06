using garge_api.Constants;
using garge_api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace garge_api.Services
{
    public class VippsWebhookRegistrationService : IHostedService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly AppOptions _appOpts;
        private readonly ILogger<VippsWebhookRegistrationService> _logger;

        public VippsWebhookRegistrationService(
            IServiceScopeFactory scopeFactory,
            IOptions<AppOptions> appOpts,
            ILogger<VippsWebhookRegistrationService> logger)
        {
            _scopeFactory = scopeFactory;
            _appOpts = appOpts.Value;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var vipps = scope.ServiceProvider.GetRequiredService<IVippsService>();
            var protector = scope.ServiceProvider.GetRequiredService<IWebhookSecretProtector>();
            var settingsCache = scope.ServiceProvider.GetRequiredService<IAppSettingsCache>();

            var settings = await db.AppSettings.FindAsync([1], cancellationToken);
            if (settings == null) return;

            var changed = false;

            if (string.IsNullOrEmpty(settings.VippsShopWebhookId))
            {
                if (await TryRegisterAsync(vipps, protector,
                        $"{_appOpts.ApiBaseUrl}/api/shop/webhook",
                        VippsEvents.ShopEvents,
                        (id, secret) =>
                        {
                            settings.VippsShopWebhookId = id;
                            settings.VippsShopWebhookSecret = secret;
                        },
                        "shop"))
                    changed = true;
            }

            if (string.IsNullOrEmpty(settings.VippsSubscriptionWebhookId))
            {
                if (await TryRegisterAsync(vipps, protector,
                        $"{_appOpts.ApiBaseUrl}/api/subscriptions/webhook",
                        VippsEvents.SubscriptionEvents,
                        (id, secret) =>
                        {
                            settings.VippsSubscriptionWebhookId = id;
                            settings.VippsSubscriptionWebhookSecret = secret;
                        },
                        "subscription"))
                    changed = true;
            }

            if (changed)
            {
                await db.SaveChangesAsync(cancellationToken);
                settingsCache.Invalidate();
            }
        }

        private async Task<bool> TryRegisterAsync(
            IVippsService vipps,
            IWebhookSecretProtector protector,
            string url, string[] events,
            Action<string, string> persist, string label)
        {
            try
            {
                var (id, secret) = await vipps.RegisterWebhookAsync(url, events);
                persist(id, protector.Protect(secret));
                _logger.LogInformation("Registered Vipps {Label} webhook {WebhookId}", label, id);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register Vipps {Label} webhook", label);
                return false;
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
