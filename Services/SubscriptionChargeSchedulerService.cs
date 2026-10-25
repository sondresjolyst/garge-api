using garge_api.Helpers;
using garge_api.Models;
using garge_api.Models.Subscription;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace garge_api.Services
{
    public class SubscriptionChargeSchedulerService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly VippsSettings _vippsOpts;
        private readonly ILogger<SubscriptionChargeSchedulerService> _logger;
        private static readonly TimeSpan Interval = TimeSpan.FromDays(1);
        private static readonly TimeSpan Lookahead = TimeSpan.FromDays(7);
        // Every start of the service runs a sweep, so a refused charge waits this long before its next attempt.
        // Restarts and replicas then do not use up the attempts within hours.
        internal static readonly TimeSpan RefusalRetryDelay = TimeSpan.FromHours(20);
        // The charge keys the admins were told about, so a charge that keeps failing is reported once. Sweeps run
        // one at a time.
        private readonly HashSet<string> _alertedKeys = [];

        public SubscriptionChargeSchedulerService(
            IServiceScopeFactory scopeFactory,
            IOptions<VippsSettings> vippsOpts,
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
                var notifier = scope.ServiceProvider.GetRequiredService<ISecurityNotifier>();

                var cutoff = DateTime.UtcNow + Lookahead;

                var email = scope.ServiceProvider.GetRequiredService<ISubscriptionEmailService>();

                await SubscriptionAgreementStops.StopAsync(db, vipps, _vippsOpts,
                    await SubscriptionAgreementStops.PendingAsync(db, stoppingToken), _logger, stoppingToken);

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

                var dueIds = due.Select(s => s.Id).ToList();
                var notices = await db.PriceChangeNotices
                    .Include(n => n.PriceChange)
                    .Where(n => dueIds.Contains(n.SubscriptionId) && n.AppliedAt == null && n.EffectiveFrom != null)
                    .ToListAsync(stoppingToken);

                foreach (var sub in due)
                {
                    // The list was read before the Vipps calls of earlier subscriptions. A deletion or a cancel
                    // may have stopped this one since.
                    await db.Entry(sub).ReloadAsync(stoppingToken);
                    if (sub.Status != SubscriptionStatus.Active || sub.FailedChargeAttempts >= SubscriptionCharges.MaxAttempts) continue;
                    if (sub.LastChargeRefusedAt > DateTime.UtcNow - RefusalRetryDelay) continue;
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
                    var amountInOre = SubscriptionPricing.UnitPriceFor(sub, notices, dueDate) * sub.Quantity;
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
                    catch (Exception ex) when (IsChargeRefusal(ex))
                    {
                        // Vipps answers a reused key with the same refusal, so the refusal counts as a failed attempt
                        // and the next attempt gets its own key. The attempt limit then stops the agreement.
                        sub.FailedChargeAttempts++;
                        sub.LastChargeFailureReason = null;
                        sub.LastChargeRefusedAt = DateTime.UtcNow;
                        sub.UpdatedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(stoppingToken);
                        _logger.LogError(ex, "ChargeScheduler: Vipps refused charge attempt {Attempt} of {Max} for subscription {SubId} due {DueDate}",
                            sub.FailedChargeAttempts, SubscriptionCharges.MaxAttempts, sub.Id, dueDate);
                        await AlertAdminsAsync(notifier, db, sub, key, amountInOre, dueDate,
                            sub.FailedChargeAttempts >= SubscriptionCharges.MaxAttempts
                                ? $"That was the last of {SubscriptionCharges.MaxAttempts} attempts, so the subscription is stopped."
                                : $"That was attempt {sub.FailedChargeAttempts} of {SubscriptionCharges.MaxAttempts}. The next daily run posts a new charge, and the agreement is stopped after {SubscriptionCharges.MaxAttempts} failed attempts.",
                            stoppingToken);
                        // The subscriber hears of it too, as with a charge that fails at Vipps.
                        if (sub.FailedChargeAttempts < SubscriptionCharges.MaxAttempts)
                        {
                            try { await email.SendChargeFailedAsync(sub.Id); }
                            catch (Exception mailEx) { _logger.LogError(mailEx, "Charge-failed email failed for subscription {SubscriptionId}", sub.Id); }
                        }
                    }
                    catch (Exception ex)
                    {
                        // The charge may have reached Vipps, or the fault is ours, such as credentials Vipps does not
                        // accept. It is not counted, and the next run posts it again with the same key.
                        _logger.LogError(ex, "ChargeScheduler: failed to post charge for subscription {SubId} due {DueDate}",
                            sub.Id, dueDate);
                        await AlertAdminsAsync(notifier, db, sub, key, amountInOre, dueDate,
                            "It is tried again with the next daily run. Check the agreement in the Vipps portal if it keeps failing.",
                            stoppingToken);
                    }
                }

                // After the charges, so a subscription that just reached the attempt limit is stopped in this run.
                await StopUnpaidAsync(db, vipps, email, stoppingToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ChargeScheduler: unexpected error during sweep.");
            }
        }

        // Tells the admins, so a charge Vipps does not take is not only in the log. Each charge key is reported
        // once: a refusal moves on to a new key, and a charge that keeps failing with its key is not reported
        // again with every run.
        private async Task AlertAdminsAsync(ISecurityNotifier notifier, ApplicationDbContext db, Subscription sub,
            string key, int amountInOre, DateTime dueDate, string next, CancellationToken ct)
        {
            if (_alertedKeys.Contains(key)) return;
            try
            {
                var reached = await notifier.NotifyAdminsAsync(db, "Vipps refused a charge",
                    $"Vipps did not take the charge of {MoneyFormat.Nok(amountInOre)} kr due {LocalTime.Format(dueDate, "yyyy-MM-dd")} for subscription #{sub.Id} ({sub.Product?.Name}). {next}",
                    $"garge-charge-refused-{sub.Id}", ct);
                if (reached)
                    _alertedKeys.Add(key);
                else
                    _logger.LogError("ChargeScheduler: the refused charge for subscription {SubId} reached no admin", sub.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ChargeScheduler: could not tell the admins about the refused charge for subscription {SubId}", sub.Id);
            }
        }

        // Only a 400 or a 404 counts. A 401 or a 403 means credentials or merchant settings Vipps does not accept,
        // which the subscriber cannot fix and must not be stopped for.
        internal static bool IsChargeRefusal(Exception ex) =>
            ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.NotFound };

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
