using garge_api.Models;
using garge_api.Models.Subscription;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    public class SubscriptionChargeSchedulerService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<SubscriptionChargeSchedulerService> _logger;
        private static readonly TimeSpan Interval = TimeSpan.FromDays(1);
        private static readonly TimeSpan Lookahead = TimeSpan.FromDays(7);

        public SubscriptionChargeSchedulerService(
            IServiceScopeFactory scopeFactory,
            ILogger<SubscriptionChargeSchedulerService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await ScheduleDueChargesAsync(stoppingToken);
                await Task.Delay(Interval, stoppingToken);
            }
        }

        internal async Task ScheduleDueChargesAsync(CancellationToken stoppingToken)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var vipps = scope.ServiceProvider.GetRequiredService<IVippsService>();
                var settingsCache = scope.ServiceProvider.GetRequiredService<IAppSettingsCache>();

                var settings = await settingsCache.GetAsync();
                var testMode = settings.VippsTestMode;
                var cutoff = DateTime.UtcNow + Lookahead;

                var due = await db.Subscriptions
                    .Include(s => s.Product)
                    .Where(s => s.Status == SubscriptionStatus.Active
                                && s.IsTest == testMode
                                && s.NextChargeDate != null
                                && s.NextChargeDate <= cutoff
                                && s.Product != null)
                    .ToListAsync(stoppingToken);

                foreach (var sub in due)
                {
                    if (sub.Product == null || !sub.NextChargeDate.HasValue) continue;

                    var dueDate = sub.NextChargeDate.Value;
                    var unitPriceInOre = Pricing.EffectiveInOre(sub.Product.PriceInOre, settings.VatEnabled);
                    var amountInOre = unitPriceInOre * sub.Quantity;
                    try
                    {
                        await vipps.CreateChargeAsync(
                            sub.VippsAgreementId,
                            amountInOre,
                            dueDate,
                            sub.Product.Name,
                            idempotencyKey: $"charge-{sub.Id}-{dueDate.Ticks}");

                        _logger.LogInformation("ChargeScheduler: posted charge for subscription {SubId} amount {Amount} due {DueDate}",
                            sub.Id, amountInOre, dueDate);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ChargeScheduler: failed to post charge for subscription {SubId} due {DueDate}",
                            sub.Id, dueDate);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ChargeScheduler: unexpected error during sweep.");
            }
        }
    }
}
