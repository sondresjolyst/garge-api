namespace garge_api.Services
{
    public interface IInvoiceService
    {
        /// <summary>
        /// Generate the invoice for an order and email the buyer. Idempotent by default: if an
        /// invoice already exists for the order, the call is a no-op and returns the existing id.
        /// Pass <paramref name="force"/> = true to re-render the PDF and re-send the email,
        /// reusing the same invoice row.
        /// </summary>
        Task<int> GenerateAndStoreAsync(int orderId, bool force = false);

        /// <summary>
        /// Generate the invoice for a single recurring charge against a Vipps subscription
        /// agreement and email the buyer. Idempotent on <paramref name="vippsChargeId"/>:
        /// webhook redelivery for the same charge returns the existing invoice id without
        /// inserting a duplicate row or re-emailing.
        /// </summary>
        Task<int> GenerateForSubscriptionChargeAsync(
            int subscriptionId, string vippsChargeId, int amountInOre, DateTime occurredAt);
    }
}
