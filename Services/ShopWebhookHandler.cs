using garge_api.Models;
using garge_api.Models.Shop;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    /// <summary>Applies Vipps ePayment events to shop orders.</summary>
    public class ShopWebhookHandler(
        ApplicationDbContext db,
        IVippsService vipps,
        IInvoiceService invoice,
        IOrderEmailService orderEmail,
        IWebPushService push,
        ILogger<ShopWebhookHandler> logger) : IVippsPaymentWebhookHandler
    {
        public async Task<VippsWebhookOutcome> HandleAsync(VippsWebhookDelivery<VippsPaymentEvent> delivery, CancellationToken cancellationToken)
        {
            var payload = delivery.Event;
            var eventId = delivery.EventKey;

            var order = await db.Orders
                .Include(o => o.OrderItems)
                .FirstOrDefaultAsync(o => o.VippsOrderId == payload.Reference, cancellationToken);
            if (order == null)
            {
                logger.LogWarning("Shop webhook: unknown reference {Reference}", payload.Reference);
                return VippsWebhookOutcome.Rejected;
            }

            if (!VippsPaymentEventChecks.EnvironmentMatches(delivery, order.IsTest))
            {
                logger.LogError("Shop webhook {EventId} rejected: sent from the {Sent} environment for a {Order} order {OrderId}",
                    eventId, delivery.IsTest ? "test" : "production", order.IsTest ? "test" : "production", order.Id);
                return VippsWebhookOutcome.Rejected;
            }

            if (!VippsPaymentEventChecks.AmountFits(payload, order.TotalInOre))
            {
                logger.LogError("Shop webhook {EventId} rejected: amount mismatch (got {Got}, expected {Expected}) for order {OrderId}",
                    eventId, payload.Amount?.Value, order.TotalInOre, order.Id);
                return VippsWebhookOutcome.Rejected;
            }

            if (!payload.Success)
            {
                logger.LogWarning("Shop webhook {EventId}: {Name} did not succeed for order {OrderId}, order unchanged",
                    eventId, payload.Name, order.Id);
                return VippsWebhookOutcome.Rejected;
            }

            var amount = payload.Amount?.Value;
            var prevStatus = order.Status;

            try
            {
                switch (payload.Name)
                {
                    case "AUTHORIZED":
                        order.Status = OrderStatus.Reserved;
                        await TryPopulateShippingFromVippsAsync(order);
                        break;
                    case "CAPTURED":
                        // A partial capture leaves the order Reserved until the whole amount is captured.
                        var capturedSoFar = amount == order.TotalInOre ? order.TotalInOre : (await PaymentTotalsAsync(order)).CapturedAmountInOre;
                        order.CapturedInOre = Math.Clamp(Math.Max(order.CapturedInOre, capturedSoFar), 0, order.TotalInOre);
                        if (order.CapturedInOre >= order.TotalInOre)
                        {
                            order.Status = OrderStatus.Paid;
                            if (order.ShippedAt == null) order.ShippedAt = DateTime.UtcNow;
                        }
                        else
                        {
                            logger.LogWarning("Shop webhook {EventId}: partial capture of {Amount} on order {OrderId}, order stays {Status}",
                                eventId, amount, order.Id, order.Status);
                        }
                        break;
                    case "REFUNDED":
                        // A partial refund leaves the order Paid. Only a refund of the whole amount refunds it.
                        var refundedSoFar = amount == order.TotalInOre ? order.TotalInOre : (await PaymentTotalsAsync(order)).RefundedAmountInOre;
                        order.RefundedInOre = Math.Clamp(Math.Max(order.RefundedInOre, refundedSoFar), 0, order.TotalInOre);
                        if (order.RefundedInOre >= order.TotalInOre)
                        {
                            order.Status = OrderStatus.Refunded;
                        }
                        else
                        {
                            logger.LogWarning("Shop webhook {EventId}: partial refund of {Amount} on order {OrderId}, order stays {Status}",
                                eventId, amount, order.Id, order.Status);
                        }
                        break;
                    case "ABORTED":
                    case "EXPIRED":
                    case "TERMINATED":
                        order.Status = OrderStatus.Failed;
                        break;
                    case "CANCELLED":
                        // Cancelling after a partial capture only releases the rest, and the customer has paid.
                        if (prevStatus == OrderStatus.Reserved && await PaymentTotalsAsync(order) is { CapturedAmountInOre: > 0 } partly)
                        {
                            order.CapturedInOre = Math.Clamp(Math.Max(order.CapturedInOre, partly.CapturedAmountInOre), 0, order.TotalInOre);
                            // The customer paid part of the order and the rest was released. That needs a person.
                            logger.LogError("Shop webhook {EventId}: order {OrderId} was captured for {Captured} of {Total} øre and the rest cancelled, order stays {Status} and needs manual handling",
                                eventId, order.Id, partly.CapturedAmountInOre, order.TotalInOre, order.Status);
                        }
                        else
                        {
                            order.Status = OrderStatus.Cancelled;
                        }
                        break;
                    case "CREATED":
                        break;
                    default:
                        logger.LogWarning("Shop webhook: unknown event {Name} for order {OrderId}",
                            payload.Name, order.Id);
                        break;
                }
            }
            catch (HttpRequestException ex)
            {
                // The payment totals could not be read. Vipps sends the event again.
                logger.LogWarning(ex, "Shop webhook {EventId}: could not read the payment of order {OrderId} from Vipps, left for a retry", eventId, order.Id);
                return VippsWebhookOutcome.Retry;
            }

            order.UpdatedAt = DateTime.UtcNow;

            // Checkout takes the items off stock, so an order that ends unpaid gives them back.
            if (prevStatus is OrderStatus.Pending or OrderStatus.Reserved &&
                order.Status is OrderStatus.Failed or OrderStatus.Cancelled)
            {
                await OrderStock.RestoreAsync(db, order, cancellationToken);
            }

            return VippsWebhookOutcome.Applied(_ => AfterCommitAsync(order, prevStatus, payload.Name));
        }

        private async Task AfterCommitAsync(Order order, OrderStatus prevStatus, string name)
        {
            if (name == "REFUNDED")
                await TryCreditRefundsAsync(order.Id);

            if (prevStatus != OrderStatus.Paid && order.Status == OrderStatus.Paid)
            {
                await TryGenerateInvoiceAsync(order.Id, "webhook capture");

                _ = SafePushAsync(order.UserId, "Payment captured",
                    $"Order #{order.Id} is paid. Invoice on its way to your email.");
            }
            else if (name == "CAPTURED" && order.Status == OrderStatus.Paid)
            {
                // The order is already Paid, by an admin capture or an earlier capture event. The service is
                // idempotent: it short-circuits when a complete invoice already exists and otherwise
                // renders one. Empty placeholder rows left by a prior failed render are cleaned up
                // inside the service.
                logger.LogInformation("Webhook recovery check for order {OrderId}", order.Id);
                await TryGenerateInvoiceAsync(order.Id, "webhook recovery");
            }
            else if (prevStatus != OrderStatus.Reserved && order.Status == OrderStatus.Reserved)
            {
                try { await orderEmail.SendOrderConfirmedAsync(order.Id); }
                catch (Exception ex) { logger.LogError(ex, "Order confirmation email failed for order {OrderId}", order.Id); }

                _ = SafePushAsync(order.UserId, "Order confirmed",
                    $"Order #{order.Id} confirmed. We'll get it ready to ship.");
            }

            logger.LogInformation("Shop webhook: order {OrderId} -> {Status}", order.Id, name);
        }

        /// <summary>The payment's capture, refund and cancel totals, read in the environment the order was created in.</summary>
        private Task<VippsPayment> PaymentTotalsAsync(Order order)
            => vipps.GetPaymentAsync(order.VippsOrderId ?? string.Empty, order.IsTest);

        // A refund is never undone by a credit note that fails. The retry job makes it later.
        private async Task TryCreditRefundsAsync(int orderId)
        {
            try { await invoice.CreditOrderRefundsAsync(orderId); }
            catch (Exception ex) { logger.LogError(ex, "The credit note for the refund of order {OrderId} is made later", orderId); }
        }

        private async Task SafePushAsync(string userId, string title, string body)
        {
            try { await push.SendAsync(userId, title, body); }
            catch (Exception ex) { logger.LogWarning(ex, "Push send failed for user {UserId}", userId); }
        }

        private async Task TryGenerateInvoiceAsync(int orderId, string context)
        {
            try { await invoice.GenerateAndStoreAsync(orderId); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Invoice generation failed ({Context}) for order {OrderId}", context, orderId);
            }
        }

        private async Task TryPopulateShippingFromVippsAsync(Order order)
        {
            if (string.IsNullOrEmpty(order.VippsOrderId)) return;
            try
            {
                var payment = await vipps.GetPaymentAsync(order.VippsOrderId, order.IsTest);
                if (string.IsNullOrEmpty(payment?.ProfileSub)) return;

                var info = await vipps.GetUserInfoAsync(payment.ProfileSub, order.IsTest);
                var formatted = VippsAddressFormatter.Format(info?.Address);
                if (!string.IsNullOrEmpty(formatted))
                    order.ShippingAddress = formatted;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch Vipps user info for order {OrderId}", order.Id);
            }
        }
    }
}
