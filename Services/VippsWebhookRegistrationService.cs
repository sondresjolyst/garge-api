using garge_api.Models;
using garge_api.Models.Admin;
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

            var settings = await db.AppSettings.FindAsync([1], cancellationToken);
            if (settings == null) return;

            var shopTask = string.IsNullOrEmpty(settings.VippsShopWebhookId)
                ? TryRegisterAsync(vipps,
                    $"{_appOpts.ApiBaseUrl}/api/shop/webhook",
                    ["epayments.payment.authorized.v1", "epayments.payment.terminated.v1", "epayments.payment.refunded.v1"],
                    (id, secret) => { settings.VippsShopWebhookId = id; settings.VippsShopWebhookSecret = secret; },
                    "shop")
                : Task.FromResult(false);

            var subTask = string.IsNullOrEmpty(settings.VippsSubscriptionWebhookId)
                ? TryRegisterAsync(vipps,
                    $"{_appOpts.ApiBaseUrl}/api/subscriptions/webhook",
                    ["recurring.agreement-activated.v1", "recurring.agreement-stopped.v1", "recurring.agreement-expired.v1"],
                    (id, secret) => { settings.VippsSubscriptionWebhookId = id; settings.VippsSubscriptionWebhookSecret = secret; },
                    "subscription")
                : Task.FromResult(false);

            var results = await Task.WhenAll(shopTask, subTask);
            if (results.Any(r => r))
                await db.SaveChangesAsync(cancellationToken);
        }

        private async Task<bool> TryRegisterAsync(
            IVippsService vipps, string url, string[] events,
            Action<string, string> persist, string label)
        {
            try
            {
                var (id, secret) = await vipps.RegisterWebhookAsync(url, events);
                persist(id, secret);
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
