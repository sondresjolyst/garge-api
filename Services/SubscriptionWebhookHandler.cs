using garge_api.Models;
using garge_api.Models.Subscription;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    /// <summary>Applies Vipps Recurring agreement and charge events to subscriptions.</summary>
    public class SubscriptionWebhookHandler(
        ApplicationDbContext db,
        IVippsService vipps,
        IInvoiceService invoice,
        ISubscriptionEmailService subEmail,
        IWebPushService push,
        ISecurityNotifier notifier,
        ILogger<SubscriptionWebhookHandler> logger) : IVippsRecurringWebhookHandler
    {
        public async Task<VippsWebhookOutcome> HandleAsync(VippsWebhookDelivery<VippsRecurringEvent> delivery, CancellationToken cancellationToken)
        {
            var payload = delivery.Event;
            var eventId = delivery.EventKey;
            // A time without an offset counts as UTC. The database stores UTC.
            var occurred = payload.Occurred?.UtcDateTime;

            var subscription = await db.Subscriptions
                .FirstOrDefaultAsync(s => s.VippsAgreementId == payload.AgreementId, cancellationToken);
            if (subscription == null)
            {
                logger.LogWarning("Webhook: unknown agreementId {AgreementId}", payload.AgreementId);
                return VippsWebhookOutcome.Rejected;
            }

            if (!VippsPaymentEventChecks.EnvironmentMatches(delivery, subscription.IsTest))
            {
                logger.LogError("Subscription webhook {EventId} rejected: sent from the {Sent} environment for a {Subscription} subscription {SubscriptionId}",
                    eventId, delivery.IsTest ? "test" : "production", subscription.IsTest ? "test" : "production", subscription.Id);
                return VippsWebhookOutcome.Rejected;
            }

            var wasActive = subscription.Status == SubscriptionStatus.Active;
            var wasStopped = subscription.Status == SubscriptionStatus.Stopped;
            var chargeFailureCounted = false;

            switch (payload.EventType)
            {
                case VippsEvents.AgreementActivated when subscription.Status == SubscriptionStatus.Stopped:
                    // A stopped subscription, for example of a deleted account, is not started again. Its agreement
                    // is stopped in Vipps instead, by the charge scheduler.
                    subscription.AgreementStopRequestedAt = DateTime.UtcNow;
                    logger.LogWarning("Webhook: agreement {AgreementId} activated for stopped subscription {SubscriptionId}, stopping it",
                        payload.AgreementId, subscription.Id);
                    break;
                case VippsEvents.AgreementActivated:
                    subscription.Status = SubscriptionStatus.Active;
                    if (subscription.StartDate == null)
                        subscription.StartDate = occurred ?? DateTime.UtcNow;
                    if (string.IsNullOrEmpty(subscription.BillingAddress))
                        await TryPopulateBillingAddressAsync(subscription, payload.AgreementId);
                    break;
                case VippsEvents.AgreementStopped:
                case VippsEvents.AgreementRejected:
                    subscription.Status = SubscriptionStatus.Stopped;
                    break;
                case VippsEvents.AgreementExpired:
                    subscription.Status = SubscriptionStatus.Expired;
                    break;
                case VippsEvents.ChargeCaptured when occurred.HasValue:
                    var product = await db.Products.FindAsync([subscription.ProductId], cancellationToken);
                    if (product == null) break;
                    // Billing days follow the Norwegian calendar date the subscription started on.
                    var anchor = LocalTime.Date(subscription.StartDate ?? occurred.Value);
                    if (SubscriptionCharges.TryParse(payload.ChargeId, out var capturedSubId, out var capturedDue, out _)
                        && capturedSubId == subscription.Id)
                    {
                        // A scheduled charge moves billing on from the date it was due, never backwards.
                        var next = SubscriptionCharges.NextDue(anchor, capturedDue, product.Interval);
                        if (subscription.NextChargeDate == null || next > subscription.NextChargeDate)
                        {
                            subscription.NextChargeDate = next;
                            subscription.FailedChargeAttempts = 0;
                            subscription.LastChargeFailureReason = null;
                        }
                    }
                    else if (subscription.NextChargeDate == null)
                    {
                        // The initial charge, captured when the agreement is accepted, starts billing.
                        subscription.NextChargeDate = SubscriptionCharges.NextDue(anchor, LocalTime.Date(occurred.Value), product.Interval);
                    }
                    break;
                case VippsEvents.ChargeFailed:
                    if (SubscriptionCharges.TryParse(payload.ChargeId, out var failedSubId, out var failedDue, out var failedAttempt)
                        && failedSubId == subscription.Id
                        && failedDue == subscription.NextChargeDate
                        && failedAttempt == subscription.FailedChargeAttempts)
                    {
                        subscription.FailedChargeAttempts++;
                        subscription.LastChargeFailureReason = payload.FailureReason is { Length: > 0 and <= 50 } reason ? reason : null;
                        chargeFailureCounted = true;
                        logger.LogWarning("Charge attempt {Attempt} of {Max} failed for subscription {SubId} agreement {AgreementId}",
                            subscription.FailedChargeAttempts, SubscriptionCharges.MaxAttempts, subscription.Id, payload.AgreementId);
                        if (subscription.FailedChargeAttempts >= SubscriptionCharges.MaxAttempts)
                        {
                            // The failure is saved either way. A subscription left active here is stopped by
                            // the charge scheduler, which retries the cancel.
                            try
                            {
                                await vipps.CancelAgreementAsync(subscription.VippsAgreementId, $"cancel-{subscription.Id}", subscription.IsTest);
                                subscription.Status = SubscriptionStatus.Stopped;
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, "Vipps agreement cancel failed for subscription {SubscriptionId} after its last failed charge", subscription.Id);
                            }
                        }
                    }
                    else
                    {
                        logger.LogWarning("Charge {ChargeId} failed for subscription {SubId} agreement {AgreementId}, not the current attempt",
                            payload.ChargeId, subscription.Id, payload.AgreementId);
                    }
                    break;
                case VippsEvents.ChargeCreationFailed:
                    logger.LogWarning("Charge creation failed for subscription {SubId} agreement {AgreementId}",
                        subscription.Id, payload.AgreementId);
                    break;
            }

            subscription.UpdatedAt = DateTime.UtcNow;
            return VippsWebhookOutcome.Applied(async _ =>
            {
                await AfterCommitAsync(subscription, payload, occurred, wasActive, chargeFailureCounted);
                if (wasStopped && payload.EventType is VippsEvents.AgreementActivated or VippsEvents.ChargeCaptured)
                    await AlertStoppedAsync(subscription, payload);
            });
        }

        private async Task AfterCommitAsync(Subscription subscription, VippsRecurringEvent payload, DateTime? occurred, bool wasActive, bool chargeFailureCounted)
        {
            logger.LogInformation("Webhook: agreement {AgreementId} -> {EventType}",
                payload.AgreementId, payload.EventType);

            if (!wasActive && subscription.Status == SubscriptionStatus.Active)
            {
                try { await subEmail.SendActivatedAsync(subscription.Id); }
                catch (Exception ex) { logger.LogError(ex, "Activation email failed for subscription {SubscriptionId}", subscription.Id); }

                _ = SafePushAsync(subscription.UserId, "Subscription active",
                    "Your Garge subscription is confirmed.");
            }

            if (payload.EventType == VippsEvents.ChargeCaptured && !string.IsNullOrEmpty(payload.ChargeId))
            {
                var product = await db.Products.FindAsync(subscription.ProductId);
                if (product != null)
                {
                    // The invoice shows what Vipps captured. Without an amount in the event it falls
                    // back to what the scheduler charges for this subscription on the capture date.
                    var amountInOre = payload.AmountCaptured ?? payload.Amount;
                    if (amountInOre == null)
                    {
                        var notices = await db.PriceChangeNotices
                            .Include(n => n.PriceChange)
                            .Where(n => n.SubscriptionId == subscription.Id && n.AppliedAt == null)
                            .ToListAsync();
                        amountInOre = SubscriptionPricing.UnitPriceFor(subscription, notices, occurred ?? DateTime.UtcNow) * subscription.Quantity;
                    }
                    try
                    {
                        await invoice.GenerateForSubscriptionChargeAsync(
                            subscription.Id,
                            payload.ChargeId,
                            amountInOre.Value,
                            occurred ?? DateTime.UtcNow);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Subscription invoice generation failed for subscription {SubscriptionId} charge {ChargeId}",
                            subscription.Id, payload.ChargeId);
                    }
                }
            }

            if (chargeFailureCounted && subscription.Status == SubscriptionStatus.Stopped)
            {
                try { await subEmail.SendStoppedForNonPaymentAsync(subscription.Id); }
                catch (Exception ex) { logger.LogError(ex, "Stopped-for-non-payment email failed for subscription {SubscriptionId}", subscription.Id); }

                _ = SafePushAsync(subscription.UserId, "Subscription stopped",
                    subscription.LastChargeFailureReason == SubscriptionCharges.AmountTooHigh
                        ? "The charge was above the maximum amount approved in Vipps, so your Garge subscription has been stopped. You can subscribe again under Billing."
                        : "We couldn't charge your Garge subscription, so it has been stopped. You can subscribe again under Billing.");
            }
            else if (chargeFailureCounted)
            {
                try { await subEmail.SendChargeFailedAsync(subscription.Id); }
                catch (Exception ex) { logger.LogError(ex, "Charge-failed email failed for subscription {SubscriptionId}", subscription.Id); }

                _ = SafePushAsync(subscription.UserId, "Payment failed",
                    subscription.LastChargeFailureReason == SubscriptionCharges.AmountTooHigh
                        ? "The charge for your Garge subscription is above the maximum amount you approved. Approve the new amount in the Vipps app."
                        : "We couldn't charge your Garge subscription. Please update your payment method in Vipps.");
            }
        }

        // A stopped subscription, for example of a deleted account, should never be activated or charged again. The
        // admins are told, so a captured charge can be refunded by hand. Nothing is refunded here.
        private async Task AlertStoppedAsync(Subscription subscription, VippsRecurringEvent payload)
        {
            // An activation after a stop that went through needs no one. The alert goes out when the agreement is
            // active at Vipps, or cannot be read.
            if (payload.EventType == VippsEvents.AgreementActivated)
            {
                try
                {
                    var status = (await vipps.GetAgreementAsync(subscription.VippsAgreementId, subscription.IsTest))?.Status;
                    if (status is not null and not "ACTIVE") return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Reading the agreement of stopped subscription {SubscriptionId} failed", subscription.Id);
                }
            }
            var amount = payload.AmountCaptured ?? payload.Amount;
            var message = payload.EventType == VippsEvents.ChargeCaptured
                ? $"Vipps captured {(amount is int ore ? $"{MoneyFormat.Nok(ore)} kr" : "a charge")} for subscription #{subscription.Id}, which is stopped. Check whether it has to be refunded."
                : $"Vipps activated the agreement of subscription #{subscription.Id}, which is stopped. The agreement is being stopped. Check whether its initial charge has to be refunded.";
            try
            {
                if (!await notifier.NotifyAdminsAsync(db, "Vipps event for a stopped subscription", message, $"garge-stopped-sub-{subscription.Id}"))
                    logger.LogError("The alert for stopped subscription {SubscriptionId} reached no admin", subscription.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not tell the admins about stopped subscription {SubscriptionId}", subscription.Id);
            }
        }

        private async Task SafePushAsync(string userId, string title, string body)
        {
            try { await push.SendAsync(userId, title, body); }
            catch (Exception ex) { logger.LogWarning(ex, "Push send failed for user {UserId}", userId); }
        }

        private async Task TryPopulateBillingAddressAsync(Subscription subscription, string agreementId)
        {
            try
            {
                var details = await vipps.GetAgreementAsync(agreementId, subscription.IsTest);
                if (string.IsNullOrEmpty(details?.Sub)) return;

                var info = await vipps.GetUserInfoAsync(details.Sub, subscription.IsTest);
                var formatted = VippsAddressFormatter.Format(info?.Address);
                if (!string.IsNullOrEmpty(formatted))
                    subscription.BillingAddress = formatted;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch Vipps user info for subscription {SubscriptionId}", subscription.Id);
            }
        }
    }
}
