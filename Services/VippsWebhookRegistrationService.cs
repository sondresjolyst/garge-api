using garge_api.Constants;
using garge_api.Models;
using garge_api.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace garge_api.Services
{
    /// <summary>
    /// Makes sure the shop and subscription webhooks are registered in each configured Vipps environment.
    /// Test and production are separate at Vipps, so each has its own registration and secret. A stored
    /// registration that Vipps no longer has, for example after two weeks of failed deliveries, is
    /// replaced.
    /// </summary>
    public class VippsWebhookRegistrationService : IHostedService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly AppOptions _appOpts;
        private readonly VippsOptions _vippsOpts;
        private readonly ILogger<VippsWebhookRegistrationService> _logger;

        private sealed record Slot(string Label, string Path, string[] Events, bool IsTest,
            Func<AppSettings, string?> GetId, Action<AppSettings, string?, string?> Set);

        private static readonly Slot[] Slots =
        [
            new("shop", "/api/shop/webhook", VippsEvents.ShopEvents, false,
                s => s.VippsShopWebhookId, (s, id, secret) => { s.VippsShopWebhookId = id; s.VippsShopWebhookSecret = secret; }),
            new("subscription", "/api/subscriptions/webhook", VippsEvents.SubscriptionEvents, false,
                s => s.VippsSubscriptionWebhookId, (s, id, secret) => { s.VippsSubscriptionWebhookId = id; s.VippsSubscriptionWebhookSecret = secret; }),
            new("test shop", "/api/shop/webhook", VippsEvents.ShopEvents, true,
                s => s.VippsTestShopWebhookId, (s, id, secret) => { s.VippsTestShopWebhookId = id; s.VippsTestShopWebhookSecret = secret; }),
            new("test subscription", "/api/subscriptions/webhook", VippsEvents.SubscriptionEvents, true,
                s => s.VippsTestSubscriptionWebhookId, (s, id, secret) => { s.VippsTestSubscriptionWebhookId = id; s.VippsTestSubscriptionWebhookSecret = secret; }),
        ];

        public VippsWebhookRegistrationService(
            IServiceScopeFactory scopeFactory,
            IOptions<AppOptions> appOpts,
            IOptions<VippsOptions> vippsOpts,
            ILogger<VippsWebhookRegistrationService> logger)
        {
            _scopeFactory = scopeFactory;
            _appOpts = appOpts.Value;
            _vippsOpts = vippsOpts.Value;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var vipps = scope.ServiceProvider.GetRequiredService<IVippsService>();
            var protector = scope.ServiceProvider.GetRequiredService<IWebhookSecretProtector>();
            var settingsCache = scope.ServiceProvider.GetRequiredService<IAppSettingsCache>();

            // Instances starting together register one at a time. Otherwise one could delete the
            // registration another just stored. Raw SQL because EF Core has no advisory lock API.
            await using var transaction = db.Database.IsNpgsql()
                ? await db.Database.BeginTransactionAsync(cancellationToken)
                : null;
            if (transaction != null)
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7300114)", cancellationToken);

            var settings = await db.AppSettings.FindAsync([1], cancellationToken);
            if (settings == null)
            {
                _logger.LogInformation("AppSettings row missing, seeding the default row");
                settings = new AppSettings { Id = 1 };
                db.AppSettings.Add(settings);
                await db.SaveChangesAsync(cancellationToken);
            }

            var registered = new Dictionary<bool, IReadOnlyList<VippsWebhookInfo>>();
            foreach (var isTest in new[] { false, true })
            {
                if (!_vippsOpts.IsConfigured(isTest)) continue;
                try
                {
                    registered[isTest] = await vipps.ListWebhooksAsync(isTest);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Could not list Vipps webhooks in the {Environment} environment", isTest ? "test" : "production");
                }
            }

            var changed = MoveToTheirEnvironment(settings, registered);
            var baseUrl = _appOpts.ApiBaseUrl.TrimEnd('/');
            var stale = new List<(string Id, bool IsTest, string Label)>();
            foreach (var slot in Slots)
            {
                if (!registered.TryGetValue(slot.IsTest, out var existing)) continue;
                var storedId = slot.GetId(settings);
                if (!string.IsNullOrEmpty(storedId) && existing.Any(h => h.Id == storedId)) continue;

                // A production slot may hold a test registration. Without the test listing that cannot
                // be ruled out, so the slot is left as it is.
                if (!slot.IsTest && !string.IsNullOrEmpty(storedId) && _vippsOpts.IsConfigured(true) && !registered.ContainsKey(true))
                {
                    _logger.LogWarning("Vipps {Label} webhook {WebhookId} is not listed in production, left in place until the test environment can be listed", slot.Label, storedId);
                    continue;
                }

                var url = $"{baseUrl}{slot.Path}";
                try
                {
                    var (id, secret) = await vipps.RegisterWebhookAsync(url, slot.Events, slot.IsTest);
                    slot.Set(settings, id, protector.Protect(secret));
                    changed = true;
                    _logger.LogInformation("Registered Vipps {Label} webhook {WebhookId}", slot.Label, id);

                    // Vipps hands out a secret only when registering, so other registrations for this
                    // URL cannot be verified. They are deleted once the new registration is stored.
                    stale.AddRange(existing.Where(h => SameUrl(h.Url, url)).Select(h => (h.Id, slot.IsTest, slot.Label)));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to register Vipps {Label} webhook", slot.Label);
                }
            }

            if (changed)
                await db.SaveChangesAsync(cancellationToken);
            if (transaction != null)
                await transaction.CommitAsync(cancellationToken);
            // Cleared after the commit, so a read in between cannot cache the old secrets.
            if (changed)
                settingsCache.Invalidate();

            foreach (var (id, isTest, label) in stale)
            {
                try
                {
                    await vipps.DeleteWebhookAsync(id, isTest);
                    _logger.LogInformation("Deleted unverifiable Vipps {Label} webhook {WebhookId}", label, id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete unverifiable Vipps {Label} webhook {WebhookId}", label, id);
                }
            }
        }

        private static bool SameUrl(string a, string b) =>
            string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

        // A production slot holds a test registration when the test environment lists its id. It moves
        // to the test slot.
        private static bool MoveToTheirEnvironment(AppSettings settings, Dictionary<bool, IReadOnlyList<VippsWebhookInfo>> registered)
        {
            if (!registered.TryGetValue(true, out var test)) return false;
            var moved = false;
            if (!string.IsNullOrEmpty(settings.VippsShopWebhookId) && test.Any(h => h.Id == settings.VippsShopWebhookId)
                && string.IsNullOrEmpty(settings.VippsTestShopWebhookId))
            {
                (settings.VippsTestShopWebhookId, settings.VippsTestShopWebhookSecret) = (settings.VippsShopWebhookId, settings.VippsShopWebhookSecret);
                (settings.VippsShopWebhookId, settings.VippsShopWebhookSecret) = (null, null);
                moved = true;
            }
            if (!string.IsNullOrEmpty(settings.VippsSubscriptionWebhookId) && test.Any(h => h.Id == settings.VippsSubscriptionWebhookId)
                && string.IsNullOrEmpty(settings.VippsTestSubscriptionWebhookId))
            {
                (settings.VippsTestSubscriptionWebhookId, settings.VippsTestSubscriptionWebhookSecret) = (settings.VippsSubscriptionWebhookId, settings.VippsSubscriptionWebhookSecret);
                (settings.VippsSubscriptionWebhookId, settings.VippsSubscriptionWebhookSecret) = (null, null);
                moved = true;
            }
            return moved;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
