using garge_api.Helpers;
using garge_api.Models;
using garge_api.Models.Subscription;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SjolystInnovation.AppKit.Vipps;

namespace garge_api.Services
{
    public class SubscriptionChargeSchedulerService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly VippsSettings _vippsOpts;
        private readonly ILogger<SubscriptionChargeSchedulerService> _logger;
        private static readonly TimeSpan Interval = TimeSpan.FromDays(1);
        private static readonly TimeSpan Lookahead = TimeSpan.FromDays(7);
        // The retryDays the Vipps client sends with a charge when the request leaves it out.
        private const int ChargeRetryDays = 5;
        // The alerts the admins got, by kind and charge key or subscription, so a failure that keeps coming back is
        // reported once. Sweeps run one at a time.
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

                var stops = await SubscriptionAgreementStops.PendingAsync(db, stoppingToken);
                foreach (var sub in stops.Where(s => !_vippsOpts.IsConfigured(s.IsTest)))
                    await AlertOnceAsync(notifier, db, $"stop-unconfigured:{sub.Id}", "A Vipps agreement cannot be stopped",
                        $"Subscription #{sub.Id} is stopped, but its Vipps agreement is in the {(sub.IsTest ? "test" : "production")} environment, which has no credentials here. Stop it in the Vipps portal.",
                        $"garge-stop-unconfigured-{sub.Id}", sub.Id, stoppingToken);
                await SubscriptionAgreementStops.StopAsync(db, vipps, _vippsOpts, stops, _logger, stoppingToken);
                var failingSince = DateTime.UtcNow - SubscriptionAgreementStops.StopFailingAlertAfter;
                foreach (var sub in stops.Where(s => s.AgreementStopRequestedAt < failingSince && _vippsOpts.IsConfigured(s.IsTest)))
                    await AlertOnceAsync(notifier, db, $"stop-failing:{sub.Id}", "A Vipps agreement cannot be stopped",
                        $"Subscription #{sub.Id} is stopped, but stopping its Vipps agreement has failed since {LocalTime.Format(sub.AgreementStopRequestedAt!.Value, "yyyy-MM-dd")}. It is tried again every day. Check the log and the agreement in the Vipps portal.",
                        $"garge-stop-failing-{sub.Id}", sub.Id, stoppingToken);

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
                    if (sub.LastChargeKey == key)
                    {
                        await StopIfPostedChargeWentQuietAsync(vipps, notifier, db, sub, key, dueDate, stoppingToken);
                        continue;
                    }
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
                        // A 400 or 404 can come from the access token or from the request as well as from the agreement,
                        // so the agreement decides. One that has ended can never be charged, and the subscription stops
                        // at once. Otherwise nothing is counted and the key stays, so the period is never charged twice.
                        var ended = await SubscriptionAgreementStops.EndedStatusAsync(vipps, sub, _logger);
                        if (ended != null)
                        {
                            sub.Status = SubscriptionStatus.Stopped;
                            sub.UpdatedAt = DateTime.UtcNow;
                            await db.SaveChangesAsync(stoppingToken);
                            _logger.LogWarning(ex, "ChargeScheduler: stopped subscription {SubId}, its Vipps agreement is {Status}", sub.Id, ended);
                            await AlertAdminsAsync(notifier, db, sub, "agreement-ended", key, amountInOre, dueDate,
                                $"Its agreement is {ended} at Vipps, so the subscription is stopped.", stoppingToken);
                        }
                        else
                        {
                            _logger.LogError(ex, "ChargeScheduler: Vipps refused the charge for subscription {SubId} due {DueDate}, its agreement has not ended",
                                sub.Id, dueDate);
                            await AlertAdminsAsync(notifier, db, sub, "refused", key, amountInOre, dueDate,
                                "Its agreement has not ended at Vipps, so it is not counted against the subscriber and the same charge is tried again with the next daily run. Check the error in the log and the agreement in the Vipps portal.",
                                stoppingToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        // The charge may have reached Vipps, or the fault is ours, such as credentials Vipps does not
                        // accept. It is not counted, and the next run posts it again with the same key.
                        _logger.LogError(ex, "ChargeScheduler: failed to post charge for subscription {SubId} due {DueDate}",
                            sub.Id, dueDate);
                        await AlertAdminsAsync(notifier, db, sub, "failed", key, amountInOre, dueDate,
                            "It is tried again with the next daily run. Check the agreement in the Vipps portal if it keeps failing.",
                            stoppingToken);
                    }
                }

                // Subscriptions that reached the attempt limit through charges that failed at Vipps.
                await StopUnpaidAsync(db, vipps, email, stoppingToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ChargeScheduler: unexpected error during sweep.");
            }
        }

        // Tells the admins, so a charge Vipps does not take is not only in the log. Each kind of failure is reported
        // once for each charge key, so a failure that keeps coming back is not reported again with every run.
        private Task AlertAdminsAsync(ISecurityNotifier notifier, ApplicationDbContext db, Subscription sub,
            string kind, string key, int amountInOre, DateTime dueDate, string next, CancellationToken ct) =>
            AlertOnceAsync(notifier, db, $"{kind}:{key}", "Vipps refused a charge",
                $"Vipps did not take the charge of {MoneyFormat.Nok(amountInOre)} kr due {LocalTime.Format(dueDate, "yyyy-MM-dd")} for subscription #{sub.Id} ({sub.Product?.Name}). {next}",
                $"garge-charge-refused-{sub.Id}", sub.Id, ct);

        // Sends the alert unless one with this key already reached an admin. One that reached nobody is sent again.
        private async Task AlertOnceAsync(ISecurityNotifier notifier, ApplicationDbContext db, string alertKey,
            string title, string message, string tag, int subscriptionId, CancellationToken ct)
        {
            if (_alertedKeys.Contains(alertKey)) return;
            try
            {
                if (await notifier.NotifyAdminsAsync(db, title, message, tag, ct))
                    _alertedKeys.Add(alertKey);
                else
                    _logger.LogError("ChargeScheduler: the alert for subscription {SubId} reached no admin", subscriptionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ChargeScheduler: could not tell the admins about subscription {SubId}", subscriptionId);
            }
        }

        // A 400 or a 404 may mean the agreement has ended, which the agreement itself then has to confirm. A 401 or a
        // 403 means credentials or merchant settings Vipps does not accept, which the subscriber is never stopped for.
        internal static bool IsChargeRefusal(Exception ex) =>
            ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.NotFound }
            && ex is not VippsAccessTokenException;

        // A posted charge is captured or fails by its due date plus its retry days, and a webhook says which. When
        // neither came two days after that, the agreement may have been stopped with its webhook lost. The posted
        // key then holds the subscription forever, so an agreement that has ended stops the subscription here.
        private async Task StopIfPostedChargeWentQuietAsync(IVippsService vipps, ISecurityNotifier notifier, ApplicationDbContext db,
            Subscription sub, string key, DateTime dueDate, CancellationToken ct)
        {
            if (dueDate.AddDays(ChargeRetryDays + 2) > DateTime.UtcNow) return;
            var ended = await SubscriptionAgreementStops.EndedStatusAsync(vipps, sub, _logger);
            if (ended == null) return;
            sub.Status = SubscriptionStatus.Stopped;
            sub.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            _logger.LogWarning("ChargeScheduler: stopped subscription {SubId}, its posted charge got no answer and its Vipps agreement is {Status}", sub.Id, ended);
            await AlertOnceAsync(notifier, db, $"quiet-ended:{key}", "A subscription was stopped",
                $"Subscription #{sub.Id} ({sub.Product?.Name}) had a charge due {LocalTime.Format(dueDate, "yyyy-MM-dd")} that Vipps never reported on, and its agreement is {ended} at Vipps, so the subscription is stopped.",
                $"garge-quiet-ended-{sub.Id}", sub.Id, ct);
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
                if (!await SubscriptionAgreementStops.StopOneAsync(vipps, sub, _logger)) continue;
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
