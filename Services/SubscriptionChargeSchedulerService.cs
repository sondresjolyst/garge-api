using garge_api.Models;
using garge_api.Models.Subscription;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace garge_api.Services
{
    public class SubscriptionChargeSchedulerService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly VippsOptions _vippsOpts;
        private readonly ILogger<SubscriptionChargeSchedulerService> _logger;
        private static readonly TimeSpan Interval = TimeSpan.FromDays(1);
        private static readonly TimeSpan Lookahead = TimeSpan.FromDays(7);

        public SubscriptionChargeSchedulerService(
            IServiceScopeFactory scopeFactory,
            IOptions<VippsOptions> vippsOpts,
            ILogger<SubscriptionChargeSchedulerService> logger)
        {
            _scopeFactory = scopeFactory;
            _vippsOpts = vippsOpts.Value;
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

                var cutoff = DateTime.UtcNow + Lookahead;

                await StopUnpaidAsync(db, vipps, scope.ServiceProvider.GetRequiredService<ISubscriptionEmailService>(), stoppingToken);

                var due = await db.Subscriptions
                    .Include(s => s.Product)
                    // Every active subscription is charged in the environment it was created in,
                    // whatever the current test mode.
                    .Where(s => s.Status == SubscriptionStatus.Active
                                && s.FailedChargeAttempts < SubscriptionCharges.MaxAttempts
                                && s.NextChargeDate != null
                                && s.NextChargeDate <= cutoff
                                && s.Product != null)
                    .ToListAsync(stoppingToken);

                foreach (var sub in due)
                {
                    if (sub.Product == null || !sub.NextChargeDate.HasValue) continue;
                    if (!_vippsOpts.IsConfigured(sub.IsTest))
                    {
                        _logger.LogWarning("ChargeScheduler: subscription {SubId} is in the {Environment} environment, which has no Vipps credentials",
                            sub.Id, sub.IsTest ? "test" : "production");
                        continue;
                    }

                    var dueDate = sub.NextChargeDate.Value;
                    // A failed attempt needs a new charge with its own key. Reusing a key returns the failed charge.
                    var key = SubscriptionCharges.Key(sub.Id, dueDate, sub.FailedChargeAttempts);
                    if (sub.LastChargeKey == key) continue;
                    var amountInOre = sub.Product.PriceInOre * sub.Quantity;
                    try
                    {
                        await vipps.CreateChargeAsync(
                            sub.VippsAgreementId,
                            amountInOre,
                            dueDate,
                            sub.Product.Name,
                            idempotencyKey: key,
                            isTest: sub.IsTest);
                        sub.LastChargeKey = key;
                        await db.SaveChangesAsync(stoppingToken);

                        _logger.LogInformation("ChargeScheduler: posted charge {ChargeKey} for subscription {SubId} amount {Amount} due {DueDate}",
                            key, sub.Id, amountInOre, dueDate);
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

        // Active subscriptions whose last charge attempt failed, where the webhook could not cancel the agreement.
        private async Task StopUnpaidAsync(ApplicationDbContext db, IVippsService vipps, ISubscriptionEmailService email, CancellationToken stoppingToken)
        {
            var unpaid = await db.Subscriptions
                .Where(s => s.Status == SubscriptionStatus.Active && s.FailedChargeAttempts >= SubscriptionCharges.MaxAttempts)
                .ToListAsync(stoppingToken);

            foreach (var sub in unpaid)
            {
                if (!_vippsOpts.IsConfigured(sub.IsTest)) continue;
                try
                {
                    await vipps.CancelAgreementAsync(sub.VippsAgreementId, $"cancel-{sub.Id}", sub.IsTest);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ChargeScheduler: cancel failed for unpaid subscription {SubId}", sub.Id);
                    continue;
                }
                sub.Status = SubscriptionStatus.Stopped;
                sub.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(stoppingToken);
                _logger.LogInformation("ChargeScheduler: stopped unpaid subscription {SubId}", sub.Id);

                try { await email.SendStoppedForNonPaymentAsync(sub.Id); }
                catch (Exception ex) { _logger.LogError(ex, "Stopped-for-non-payment email failed for subscription {SubscriptionId}", sub.Id); }
            }
        }
    }
}
